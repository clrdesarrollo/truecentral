using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Lo que vigila un Cuadro vigía. Viaja en la clave de la tarjeta
/// («card-watch?cat=alarm,access-denied&amp;loc=12&amp;sev=1&amp;hold=20»), así lo
/// guardan los tableros y la última sesión; lo que vale lo de omisión no se escribe.
/// </summary>
/// <param name="Categories">Tipos de evento (<see cref="LiveEventCategories"/>).</param>
/// <param name="LocationIds">Ubicaciones vigiladas (vacío = todo lo que ve el usuario).</param>
/// <param name="MinSeverity">Gravedad mínima.</param>
/// <param name="HoldSeconds">Tiempo mínimo que un evento queda en pantalla antes de pasar al siguiente.</param>
/// <param name="IdleChannelId">Cámara que muestra en espera (null = ninguna).</param>
/// <param name="MainStream">Stream principal (por omisión el secundario: cambia más rápido).</param>
/// <param name="PinMinutes">«Fijar» se suelta solo a los N minutos (0 = nunca).</param>
public sealed record WatchOptions(
    IReadOnlyCollection<string> Categories,
    IReadOnlyCollection<int> LocationIds,
    AlarmSeverity MinSeverity = AlarmSeverity.Warning,
    int HoldSeconds = 20,
    int? IdleChannelId = null,
    bool MainStream = false,
    int PinMinutes = 5)
{
    public static WatchOptions Default => new(LiveEventCategories.Default, []);

    /// <summary>Las opciones de una clave guardada (sin opciones = las de omisión).</summary>
    public static WatchOptions FromRef(string? reference)
    {
        var d = Default;
        string? cats = CardKinds.Option(reference, "cat");
        return new WatchOptions(
            cats is null ? d.Categories : cats.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(),
            CardKinds.Ids(CardKinds.Option(reference, "loc")),
            int.TryParse(CardKinds.Option(reference, "sev"), out int sev) && Enum.IsDefined((AlarmSeverity)sev)
                ? (AlarmSeverity)sev : d.MinSeverity,
            int.TryParse(CardKinds.Option(reference, "hold"), out int hold) ? Math.Clamp(hold, 5, 120) : d.HoldSeconds,
            int.TryParse(CardKinds.Option(reference, "idle"), out int idle) && idle > 0 ? idle : null,
            CardKinds.Option(reference, "main") == "1",
            int.TryParse(CardKinds.Option(reference, "pin"), out int pin) ? Math.Clamp(pin, 0, 30) : d.PinMinutes);
    }

    /// <summary>La clave para guardarlo (solo lo que difiere de lo de omisión).</summary>
    public string ToRef()
    {
        var d = Default;
        var parts = new List<string>();
        if (!Categories.ToHashSet().SetEquals(d.Categories))
            parts.Add("cat=" + string.Join(',', LiveEventCategories.All.Where(Categories.Contains)));
        if (LocationIds.Count > 0) parts.Add("loc=" + string.Join(',', LocationIds.Order()));
        if (MinSeverity != d.MinSeverity) parts.Add($"sev={(int)MinSeverity}");
        if (HoldSeconds != d.HoldSeconds) parts.Add($"hold={HoldSeconds}");
        if (IdleChannelId is { } idle) parts.Add($"idle={idle}");
        if (MainStream) parts.Add("main=1");
        if (PinMinutes != d.PinMinutes) parts.Add($"pin={PinMinutes}");
        return parts.Count == 0 ? CardKinds.Watch : $"{CardKinds.Watch}?{string.Join('&', parts)}";
    }

    /// <summary>¿Le interesa este evento?</summary>
    public bool Accepts(LiveEventDto e) =>
        Categories.Contains(e.Category) && e.Severity >= MinSeverity &&
        (LocationIds.Count == 0 || e.LocationIds.Any(LocationIds.Contains));
}

/// <summary>
/// Lo que el Cuadro vigía necesita del Centro de mando: resolver cámaras, abrir
/// un aviso, la lista de ubicaciones y cámaras para su engranaje.
/// </summary>
public sealed record WatchContext(
    Func<int, ChannelNode?> FindChannel,
    Func<long, Task> OpenAlert,
    Func<Task<IReadOnlyList<(int Id, string Path)>>> Locations,
    Func<IReadOnlyList<ChannelNode>> Channels,
    Action<string, string, string> Audit);

/// <summary>
/// Cuadro vigía: un cuadro que cambia solo a la cámara del último evento que le
/// interesa, con una franja arriba que dice qué pasó, dónde y hace cuánto.
///
/// No es una alarma: muestra, no exige acuse. Reglas para que no estorbe:
/// - Un evento queda al menos <see cref="WatchOptions.HoldSeconds"/>; los que
///   llegan entretanto esperan en cola («+3»), salvo que sean más graves.
/// - No cambia mientras el operador lo está usando (zoom digital o recién tocó
///   sus botones), ni mientras está fijado.
/// - Varios vigías con la misma configuración se reparten los eventos
///   (<see cref="WatchCoordinator"/>): cuatro vigías muestran los cuatro últimos.
/// </summary>
public sealed partial class WatchCellPanel : CellPanel
{
    private readonly WatchContext _context;
    private readonly DispatcherTimer _timer;
    private VideoCellViewModel? _host;
    private DateTime _shownAt;
    private DateTime _lastInteraction;
    private DateTime? _pinnedUntil;
    private List<ChannelNode> _cameras = [];
    private int _cameraIndex;

    public WatchCellPanel(WatchOptions options, WatchContext context)
    {
        _context = context;
        Options = options;
        Title = TitleFor(options, null);
        Content = new CellContentRef(DashboardItemKinds.Card, null, options.ToRef());
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        WatchCoordinator.Register(this);
        _ = FillTitleAsync();
    }

    public WatchOptions Options { get; private set; }

    public override string Glyph => "\uE890";
    public override string Kind => CardKinds.Watch;
    public override bool KeepsVideo => true;
    public override bool CanConfigure => true;
    public override string ConfigureHint => "Qué vigila este cuadro";

    /// <summary>Número del cuadro (ordena el reparto entre vigías).</summary>
    public int CellIndex => _host?.Index ?? int.MaxValue;

    /// <summary>Está en un cuadro de la grilla (recibe eventos).</summary>
    public bool IsLive => _host is not null;

    public override void AttachTo(VideoCellViewModel cell) => _host = cell;

    public override void DetachFrom(VideoCellViewModel cell)
    {
        if (ReferenceEquals(_host, cell)) _host = null;
    }

    // ------------------------------------------------------------------
    // Estado que muestra la franja
    // ------------------------------------------------------------------

    /// <summary>Evento en pantalla; null = en espera.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEvent), nameof(IsIdle), nameof(Level), nameof(CanOpenAlert),
        nameof(RepeatsText), nameof(WhereText), nameof(BodyText))]
    private LiveEventDto? _current;

    public bool HasEvent => Current is not null;
    public bool IsIdle => Current is null;

    /// <summary>Gravedad para los colores de la franja (los mismos niveles que los demás paneles).</summary>
    public string Level => Current?.Severity switch
    {
        AlarmSeverity.Critical => "Alarm",
        AlarmSeverity.Warning => "Warning",
        AlarmSeverity.Info => "Ok",
        _ => "Unknown",
    };

    public bool CanOpenAlert => Current?.AlertId is not null;
    public string RepeatsText => Current is { Repeats: > 0 } e ? $"×{e.Repeats + 1}" : "";

    public string WhereText => Current is not { } e ? "" : string.Join(" · ",
        new[] { e.ResourceName, e.LocationPath, e.Detail }.Where(x => !string.IsNullOrWhiteSpace(x)));

    /// <summary>«hace 4 s», «hace 2 min».</summary>
    [ObservableProperty] private string _agoText = "";

    /// <summary>En espera: qué vigila y que está esperando.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BodyText))]
    private string _idleText = "Esperando eventos";

    /// <summary>Aviso cuando el evento no trae cámaras que este usuario pueda ver.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BodyText))]
    private string _noCameraText = "";

    /// <summary>El cuerpo del panel a la vista: no hay video (en espera o un evento sin cámaras).</summary>
    [ObservableProperty] private bool _showsBody = true;

    /// <summary>Lo que dice el cuerpo cuando no hay video.</summary>
    public string BodyText => Current is not null && NoCameraText.Length > 0 ? NoCameraText
        : $"{IdleText}. Cuando pase algo, este cuadro cambia solo a su cámara.";

    public ObservableCollection<LiveEventDto> Queue { get; } = [];
    public string QueueText => Queue.Count == 0 ? "" : $"+{Queue.Count} en cola";
    public bool HasQueue => Queue.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinGlyph), nameof(PinHint))]
    private bool _isPinned;

    public string PinGlyph => IsPinned ? "\uE77A" : "\uE718";
    public string PinHint => IsPinned
        ? "Fijado: no cambia con los eventos nuevos (quedan en cola). Clic para soltarlo"
        : "Fijar: deja de cambiar con los eventos nuevos (quedan en cola)";

    public string CameraText => _cameras.Count <= 1 ? "" : $"Cámara {_cameraIndex + 1}/{_cameras.Count}";
    public bool HasManyCameras => _cameras.Count > 1;

    // ------------------------------------------------------------------
    // Llegada de eventos (los entrega WatchCoordinator)
    // ------------------------------------------------------------------

    /// <summary>Libre para mostrar un evento sin interrumpir a nadie.</summary>
    public bool IsFree => IsLive && Current is null && !IsPinned && !IsBusy;

    /// <summary>Puede reemplazar lo que muestra por este evento (más grave, o ya cumplió su tiempo).</summary>
    public bool CanTake(LiveEventDto e) => IsLive && !IsPinned && !IsBusy && Current is { } shown &&
        (e.Severity > shown.Severity || DateTime.Now - _shownAt >= TimeSpan.FromSeconds(Options.HoldSeconds));

    /// <summary>El operador lo está usando: zoom digital o tocó sus botones hace poco.</summary>
    private bool IsBusy =>
        DateTime.Now - _lastInteraction < TimeSpan.FromSeconds(5) ||
        (_host is { } host && host.DigitalZoom != ViewModels.DigitalZoom.NoZoom);

    public AlarmSeverity CurrentSeverity => Current?.Severity ?? AlarmSeverity.Info;
    public DateTime ShownAt => _shownAt;

    /// <summary>Muestra el evento ya.</summary>
    public void Show(LiveEventDto e)
    {
        Current = e;
        _shownAt = DateTime.Now;
        RemoveQueued(e.Id);
        _cameras = e.ChannelIds.Select(_context.FindChannel).OfType<ChannelNode>().ToList();
        _cameraIndex = 0;
        NoCameraText = _cameras.Count > 0 ? "" : "Este evento no tiene cámaras que usted pueda ver.";
        OnPropertyChanged(nameof(CameraText));
        OnPropertyChanged(nameof(HasManyCameras));
        UpdateAgo();
        _ = OpenCameraAsync();
    }

    /// <summary>Lo deja esperando su turno (el más grave primero y, a igual gravedad, el más antiguo).</summary>
    public void Enqueue(LiveEventDto e)
    {
        RemoveQueued(e.Id);
        int at = 0;
        while (at < Queue.Count && Queue[at].Severity >= e.Severity) at++;
        Queue.Insert(at, e);
        OnQueueChanged();
    }

    /// <summary>Un repetido del evento que muestra o tiene en cola: sube el contador.</summary>
    public bool Refresh(LiveEventDto e)
    {
        if (Current?.Id == e.Id)
        {
            Current = e;
            _shownAt = DateTime.Now; // un evento que se repite sigue siendo actual
            return true;
        }
        int index = Queue.ToList().FindIndex(q => q.Id == e.Id);
        if (index < 0) return false;
        Queue[index] = e;
        return true;
    }

    /// <summary>Saca de la cola el siguiente (lo pide el coordinador para otro vigía libre del grupo).</summary>
    public LiveEventDto? TakeQueued()
    {
        if (Queue.Count == 0) return null;
        var next = Queue[0];
        Queue.RemoveAt(0);
        OnQueueChanged();
        return next;
    }

    private void RemoveQueued(string id)
    {
        for (int i = Queue.Count - 1; i >= 0; i--)
            if (Queue[i].Id == id) Queue.RemoveAt(i);
        OnQueueChanged();
    }

    private void OnQueueChanged()
    {
        OnPropertyChanged(nameof(QueueText));
        OnPropertyChanged(nameof(HasQueue));
    }

    private async Task OpenCameraAsync()
    {
        if (_host is not { } host) return;
        var profile = Options.MainStream ? StreamProfile.Main : StreamProfile.Sub;
        if (_cameras.Count == 0)
        {
            host.StopVideo();
            ShowsBody = true;
            return;
        }
        ShowsBody = false;
        var camera = _cameras[Math.Clamp(_cameraIndex, 0, _cameras.Count - 1)];
        if (host.AssignedChannel?.Channel.Id == camera.Channel.Id) return; // ya está en esa cámara
        try { await host.OpenAsync(camera, profile, keepPanel: true); }
        catch (Exception ex) { Status = ex.Message; }
    }

    /// <summary>En espera: la cámara de reposo, o nada.</summary>
    private void GoIdle()
    {
        Current = null;
        _cameras = [];
        NoCameraText = "";
        AgoText = "";
        OnPropertyChanged(nameof(CameraText));
        OnPropertyChanged(nameof(HasManyCameras));
        if (_host is not { } host) return;
        if (Options.IdleChannelId is { } idle && _context.FindChannel(idle) is { } camera)
        {
            ShowsBody = false;
            if (host.AssignedChannel?.Channel.Id != camera.Channel.Id)
                _ = host.OpenAsync(camera, Options.MainStream ? StreamProfile.Main : StreamProfile.Sub, keepPanel: true);
        }
        else
        {
            host.StopVideo();
            ShowsBody = true;
        }
    }

    // ------------------------------------------------------------------
    // Botones de la franja
    // ------------------------------------------------------------------

    /// <summary>Listo: da por visto el evento; pasa al siguiente de la cola o queda en espera.</summary>
    [RelayCommand]
    private void Done()
    {
        // Dar por visto un evento crítico queda en la bitácora («¿alguien vio esto?»).
        if (Current is { Severity: AlarmSeverity.Critical } seen)
            _context.Audit("live-watch-done", Title, $"«{seen.Title}»" + (WhereText.Length > 0 ? $" ({WhereText})" : ""));
        _lastInteraction = DateTime.MinValue; // «Listo» es justamente para que siga
        if (TakeQueued() is { } next) Show(next);
        else if (WatchCoordinator.PullFor(this) is { } fromGroup) Show(fromGroup);
        else GoIdle();
    }

    /// <summary>Siguiente: el próximo de la cola ya, sin esperar su tiempo.</summary>
    [RelayCommand]
    private void Next()
    {
        _lastInteraction = DateTime.MinValue;
        if (TakeQueued() is { } next) Show(next);
    }

    [RelayCommand]
    private void TogglePin()
    {
        Touch();
        IsPinned = !IsPinned;
        _pinnedUntil = IsPinned && Options.PinMinutes > 0 ? DateTime.Now.AddMinutes(Options.PinMinutes) : null;
        ReportPin(automatic: false);
        if (!IsPinned) Status = "";
    }

    [RelayCommand]
    private void NextCamera() => StepCamera(1);

    [RelayCommand]
    private void PreviousCamera() => StepCamera(-1);

    private void StepCamera(int step)
    {
        if (_cameras.Count < 2) return;
        Touch();
        _cameraIndex = (_cameraIndex + step + _cameras.Count) % _cameras.Count;
        OnPropertyChanged(nameof(CameraText));
        _ = OpenCameraAsync();
    }

    /// <summary>Ver aviso: la ventana de alarma de siempre, con ese aviso.</summary>
    [RelayCommand]
    private async Task OpenAlertAsync()
    {
        Touch();
        if (Current?.AlertId is not { } id) return;
        try { await _context.OpenAlert(id); }
        catch (ApiException ex) { Status = ex.Message; }
    }

    private DateTime _pinnedAt;

    /// <summary>Fijar y soltar quedan en la bitácora, con cuánto estuvo fijado.</summary>
    private void ReportPin(bool automatic)
    {
        if (IsPinned)
        {
            _pinnedAt = DateTime.Now;
            _context.Audit("live-watch-pinned", Title, "fijado" + (Current is { } e ? $" mostrando «{e.Title}»" : " en espera") +
                (Options.PinMinutes > 0 ? $"; se suelta solo a los {Options.PinMinutes} min" : "; sin soltarse solo"));
        }
        else
        {
            var held = DateTime.Now - _pinnedAt;
            _context.Audit("live-watch-pinned", Title,
                $"soltado {(automatic ? "solo" : "a mano")} tras {(int)held.TotalMinutes} min {held.Seconds} s; " +
                $"{Queue.Count} evento(s) en cola");
        }
    }

    /// <summary>El operador tocó el vigía: no se le cambia la cámara por unos segundos.</summary>
    public void Touch() => _lastInteraction = DateTime.Now;

    // ------------------------------------------------------------------
    // Reloj
    // ------------------------------------------------------------------

    private void Tick()
    {
        UpdateAgo();
        if (IsPinned)
        {
            if (_pinnedUntil is { } until)
            {
                var left = until - DateTime.Now;
                if (left <= TimeSpan.Zero)
                {
                    IsPinned = false;
                    _pinnedUntil = null;
                    ReportPin(automatic: true);
                    Status = "";
                }
                else Status = $"Fijado · se suelta en {(int)left.TotalMinutes}:{left.Seconds:00}";
            }
            else Status = "Fijado";
            return;
        }
        // Cumplido su tiempo, pasa al siguiente de la cola (si nadie lo está usando).
        if (Queue.Count > 0 && !IsBusy && IsLive &&
            (Current is null || DateTime.Now - _shownAt >= TimeSpan.FromSeconds(Options.HoldSeconds)))
            Show(TakeQueued()!);
    }

    private void UpdateAgo()
    {
        if (Current is not { } e)
        {
            AgoText = "";
            return;
        }
        var ago = DateTime.UtcNow - e.At;
        AgoText = ago.TotalSeconds < 5 ? "ahora"
            : ago.TotalSeconds < 60 ? $"hace {(int)ago.TotalSeconds} s"
            : ago.TotalMinutes < 60 ? $"hace {(int)ago.TotalMinutes} min"
            : $"a las {e.At.ToLocalTime():HH:mm}";
    }

    // ------------------------------------------------------------------
    // Engranaje
    // ------------------------------------------------------------------

    protected override async Task ConfigureAsync()
    {
        Touch();
        IReadOnlyList<(int Id, string Path)> locations;
        try { locations = await _context.Locations(); }
        catch (ApiException ex)
        {
            Status = ex.Message;
            return;
        }
        if (Views.WatchSettingsWindow.Ask(PlatesCardPanel.ActiveWindow(), Options, locations, _context.Channels())
            is not { } chosen) return;
        WatchCoordinator.Unregister(this);
        Options = chosen;
        WatchCoordinator.Register(this);
        Title = TitleFor(chosen, locations);
        Content = (Content ?? new CellContentRef(DashboardItemKinds.Card)) with { Ref = chosen.ToRef() };
        OnPropertyChanged(nameof(Content));
        if (Current is null) GoIdle();
        else if (!chosen.Accepts(Current)) Done();
    }

    private async Task FillTitleAsync()
    {
        if (Options.LocationIds.Count == 0) return;
        try { Title = TitleFor(Options, await _context.Locations()); }
        catch (ApiException) { /* queda «Vigía» */ }
    }

    private static string TitleFor(WatchOptions options, IReadOnlyList<(int Id, string Path)>? locations)
    {
        if (options.LocationIds.Count == 0) return "Vigía · todo lo que ve";
        var names = (locations ?? []).Where(l => options.LocationIds.Contains(l.Id))
            .Select(l => l.Path.Split(" › ").Last()).ToList();
        return names.Count == 0 ? "Vigía" : "Vigía · " + string.Join(", ", names);
    }

    public override Task LoadAsync()
    {
        IdleText = Options.LocationIds.Count == 0 ? "Esperando eventos" : "Esperando eventos de estas ubicaciones";
        if (Current is null) GoIdle();
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _timer.Stop();
        WatchCoordinator.Unregister(this);
    }
}

/// <summary>
/// Reparte los eventos en vivo entre los Cuadros vigía abiertos. Los vigías con
/// la misma configuración forman un grupo: el evento va al que está libre (el
/// de número más bajo); si todos están ocupados, al que muestra algo menos grave
/// o que ya cumplió su tiempo; si ninguno, a la cola del que tenga menos. Así
/// varios vigías muestran los últimos eventos sin pisarse.
/// </summary>
public static class WatchCoordinator
{
    private static readonly List<WatchCellPanel> Panels = [];
    private static LiveEventHub? _hub;

    /// <summary>Se engancha a los eventos del puesto (una vez, al armar el shell).</summary>
    public static void Initialize(LiveEventHub hub)
    {
        if (_hub is not null) return;
        _hub = hub;
        hub.Arrived += OnArrived;
    }

    public static void Register(WatchCellPanel panel)
    {
        if (!Panels.Contains(panel)) Panels.Add(panel);
    }

    public static void Unregister(WatchCellPanel panel) => Panels.Remove(panel);

    private static void OnArrived(LiveEventDto e, bool repeat)
    {
        var interested = Panels.Where(p => p.IsLive && p.Options.Accepts(e)).ToList();
        if (repeat)
        {
            // Un repetido actualiza donde ya esté; si no estaba en ninguno, ya pasó.
            foreach (var p in interested) p.Refresh(e);
            return;
        }
        foreach (var group in interested.GroupBy(p => p.Options.ToRef()))
        {
            var target = group.Where(p => p.IsFree).OrderBy(p => p.CellIndex).FirstOrDefault()
                         ?? group.Where(p => p.CanTake(e)).OrderBy(p => p.CurrentSeverity).ThenBy(p => p.ShownAt).FirstOrDefault();
            if (target is not null) target.Show(e);
            else group.OrderBy(p => p.Queue.Count).ThenBy(p => p.CellIndex).First().Enqueue(e);
        }
    }

    /// <summary>Un vigía quedó libre: le pasa el siguiente evento esperando en otro del mismo grupo.</summary>
    public static LiveEventDto? PullFor(WatchCellPanel panel)
    {
        string key = panel.Options.ToRef();
        var donor = Panels.Where(p => !ReferenceEquals(p, panel) && p.IsLive && p.Options.ToRef() == key && p.Queue.Count > 0)
            .OrderByDescending(p => p.Queue[0].Severity).ThenBy(p => p.Queue[0].At).FirstOrDefault();
        return donor?.TakeQueued();
    }
}
