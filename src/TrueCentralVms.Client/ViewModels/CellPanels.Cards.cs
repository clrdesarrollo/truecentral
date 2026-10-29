using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

// ---------------------------------------------------------------------------
// Tarjetas de reporte del Centro de mando: el estado de los equipos y del
// servidor (los mismos números del panel de inicio del web) y las últimas
// alertas, patentes y pasos por puertas. Las de lista se llenan al abrirse y
// se mantienen al día por el hub; las de estado se releen cada pocos segundos.
// ---------------------------------------------------------------------------

/// <summary>Claves de las tarjetas (las guardan las vistas guardadas, no se cambian).</summary>
public static class CardKinds
{
    public const string Equipment = "card-equipment";
    public const string Server = "card-server";
    public const string Alerts = "card-alerts";
    public const string Plates = "card-plates";
    public const string Access = "card-access";
    /// <summary>Página web de la lista del servidor (no es tarjeta: es lo que pide la lista).</summary>
    public const string Web = "web";
    /// <summary>«Otra dirección…»: se escribe al abrirla.</summary>
    public const string WebCustom = "web-custom";
    /// <summary>Grupo de parlantes (Id = el del grupo).</summary>
    public const string SpeakerGroup = "speaker-group";

    /// <summary>«Últimas patentes» con las cámaras ANPR elegidas en su engranaje: "card-plates?devices=3,7"
    /// (sin cámaras = todas, la clave sola). Así lo guardan los tableros y la última sesión.</summary>
    public static string PlatesRef(IEnumerable<int> devices)
    {
        var ids = devices.Distinct().Order().ToList();
        return ids.Count == 0 ? Plates : $"{Plates}?devices={string.Join(',', ids)}";
    }

    /// <summary>Las cámaras ANPR de una clave de «Últimas patentes» (vacío = todas).</summary>
    public static IReadOnlyCollection<int> PlatesDevices(string? reference)
    {
        int q = reference?.IndexOf("?devices=", StringComparison.Ordinal) ?? -1;
        if (q < 0) return [];
        return reference![(q + "?devices=".Length)..].Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out int id) ? id : 0).Where(id => id > 0).ToHashSet();
    }
}

/// <summary>Base de las tarjetas que se releen solas cada cierto tiempo.</summary>
public abstract partial class PollingCardPanel : CellPanel
{
    private readonly DispatcherTimer _timer;

    protected PollingCardPanel(TimeSpan every)
    {
        _timer = new DispatcherTimer { Interval = every };
        _timer.Tick += async (_, _) => await RefreshSafeAsync();
    }

    [ObservableProperty] private string _updatedText = "";

    public override async Task LoadAsync()
    {
        await RefreshSafeAsync();
        _timer.Start();
    }

    private async Task RefreshSafeAsync()
    {
        try
        {
            await RefreshAsync();
            UpdatedText = $"Actualizado {DateTime.Now:HH:mm:ss}";
            Status = "";
        }
        catch (ApiException ex) { Status = ex.Message; }
    }

    protected abstract Task RefreshAsync();

    public override void Dispose() => _timer.Stop();
}

/// <summary>Una fila «tipo de equipo · en línea / total».</summary>
public sealed class EquipmentLine(string name, string glyph, int online, int total)
{
    public string Name { get; } = name;
    public string Glyph { get; } = glyph;
    public string Count { get; } = $"{online} / {total}";
    public string Level { get; } = online == total ? "Ok" : online == 0 ? "Alarm" : "Warning";
}

/// <summary>Un equipo caído en la tarjeta de estado.</summary>
public sealed class OfflineLine(DashboardOfflineItem item)
{
    public string Name { get; } = item.Name;
    public string Detail { get; } = string.Join(" · ", new[] { item.Kind, item.Detail }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string Since { get; } = item.LastSeenAt is { } at ? $"desde {at.ToLocalTime():dd-MM HH:mm}" : "";
}

/// <summary>Estado de los equipos: en línea por tipo y los que están caídos.</summary>
public sealed partial class EquipmentCardPanel : PollingCardPanel
{
    private readonly ApiClient api;

    public EquipmentCardPanel(ApiClient api) : base(TimeSpan.FromSeconds(15))
    {
        this.api = api;
        // Desde el principio: la barra del cuadro lo toma al recibir la tarjeta.
        Title = "Estado de los equipos";
    }

    public override string Glyph => "\uE9D9";
    public override string Kind => CardKinds.Equipment;

    public ObservableCollection<EquipmentLine> Lines { get; } = [];
    public ObservableCollection<OfflineLine> Offline { get; } = [];
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _level = "Unknown";

    protected override async Task RefreshAsync()
    {
        var snapshot = await api.GetDashboardAsync();
        var e = snapshot.Equipment;
        var lines = new List<EquipmentLine>();
        void Add(string name, string glyph, DashboardCount? count)
        {
            if (count is { Total: > 0 }) lines.Add(new EquipmentLine(name, glyph, count.Online, count.Total));
        }
        Add("Cámaras", "\uE714", e?.Cameras);
        Add("Grabadores y cámaras IP", "\uE7F4", e?.Video);
        Add("Control de acceso", "\uE8D7", e?.Access);
        Add("Paneles de alarma", "\uE730", e?.AlarmPanels);
        Add("Cercos eléctricos", "\uE945", e?.Fence);
        Add("Citófonos", "\uE717", e?.Intercoms);
        Add("Parlantes", "\uE767", e?.Speakers);
        Add("Módulos de E/S", "\uE950", e?.IoModules);
        Lines.Clear();
        foreach (var line in lines) Lines.Add(line);
        Offline.Clear();
        foreach (var item in snapshot.Offline?.Items ?? []) Offline.Add(new OfflineLine(item));
        int down = snapshot.Offline?.Total ?? 0;
        Summary = down == 0 ? "Todos los equipos en línea" : $"{down} equipo(s) sin conexión";
        Level = down == 0 ? "Ok" : "Alarm";
    }
}

/// <summary>Tasas de red en texto y escala del medidor, como en el panel de inicio del web.</summary>
public static class NetRate
{
    /// <summary>Número y unidad: hasta 2 decimales bajo 10 Mbps, 1 bajo 100, ninguno más arriba y en Gbps desde 1000.</summary>
    public static (string Number, string Unit) Split(double mbps) =>
        mbps >= 1000 ? ((mbps / 1000).ToString("0.##"), "Gbps")
                     : (mbps.ToString(mbps < 10 ? "0.##" : mbps < 100 ? "0.#" : "0"), "Mbps");

    public static string Format(double mbps)
    {
        var (number, unit) = Split(mbps);
        return $"{number} {unit}";
    }

    /// <summary>Escala del medidor: redonda y con aire sobre el pico reciente, sin pasar la capacidad del enlace.</summary>
    public static double Scale(NetworkThroughputDto net)
    {
        double want = NiceMax(Math.Max(10, Math.Max(net.PeakMbps, net.RxMbps + net.TxMbps) * 1.25));
        return net.LinkMbps > 0 ? Math.Min(want, NiceMax(net.LinkMbps)) : want;
    }

    // 1, 2, 5 o 10 por la potencia de diez que corresponda.
    private static double NiceMax(double v)
    {
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double n = v / p;
        return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * p;
    }
}

/// <summary>Salud del servidor: throughput de red, CPU, memoria, disco, servicios y tiempo encendido.</summary>
public sealed partial class ServerCardPanel : PollingCardPanel
{
    private readonly ApiClient api;

    public ServerCardPanel(ApiClient api) : base(TimeSpan.FromSeconds(5))
    {
        this.api = api;
        Title = "Estado del servidor";
    }

    public override string Glyph => "\uE977";
    public override string Kind => CardKinds.Server;

    // Porcentajes de los medidores (NaN = el servidor no lo informó).
    [ObservableProperty] private double _cpu = double.NaN;
    [ObservableProperty] private double _ram = double.NaN;
    [ObservableProperty] private double _disk = double.NaN;
    [ObservableProperty] private string _ramText = "";
    [ObservableProperty] private string _diskText = "";
    // Throughput (Mbit/s): el medidor de aguja con su escala, entrada / salida y la curva de los últimos minutos.
    [ObservableProperty] private bool _hasNetwork;
    [ObservableProperty] private double _netMbps;
    [ObservableProperty] private double _netScale = 10;
    [ObservableProperty] private string _netLevel = "Ok";
    [ObservableProperty] private string _rxText = "—";
    [ObservableProperty] private string _txText = "—";
    [ObservableProperty] private IReadOnlyList<double> _netHistory = [];
    [ObservableProperty] private string _peakText = "";
    [ObservableProperty] private string _linkText = "";
    [ObservableProperty] private string _servicesText = "—";
    [ObservableProperty] private string _servicesLevel = "Unknown";
    [ObservableProperty] private string _problems = "";
    [ObservableProperty] private string _uptimeText = "";
    // Clientes conectados: puestos de escritorio y sesiones del panel web con el canal en vivo abierto.
    [ObservableProperty] private bool _hasClients;
    [ObservableProperty] private string _clientsText = "";
    [ObservableProperty] private string _clientsDetail = "";

    protected override async Task RefreshAsync()
    {
        var s = (await api.GetDashboardAsync()).Server;
        if (s is null) return;
        Cpu = s.Cpu ?? double.NaN;
        Ram = s.Ram ?? double.NaN;
        Disk = s.Disk ?? double.NaN;
        RamText = s.Ram is null ? "" : $"{s.RamUsedGb:0.0} de {s.RamTotalGb:0} GB";
        DiskText = s.Disk is null ? "" : $"{s.DiskName} {s.DiskUsedGb:0} de {s.DiskTotalGb:0} GB".Trim();
        if (s.Network is { } net)
        {
            double total = net.RxMbps + net.TxMbps;
            NetScale = NetRate.Scale(net);
            NetMbps = total;
            RxText = NetRate.Format(net.RxMbps);
            TxText = NetRate.Format(net.TxMbps);
            NetHistory = net.History.Count > 1 ? net.History : [0, 0];
            PeakText = $"Pico 5 min  {NetRate.Format(net.PeakMbps)}";
            double? use = net.LinkMbps > 0 ? total / net.LinkMbps * 100 : null;
            LinkText = use is { } u
                ? $"Enlace {NetRate.Format(net.LinkMbps)} · {(u < 0.1 && total > 0 ? $"<{0.1:0.0}" : u.ToString("0.#"))} % en uso"
                : "";
            NetLevel = use >= 85 ? "Alarm" : use >= 60 ? "Warning" : "Ok";
        }
        HasNetwork = s.Network is not null;
        ServicesText = $"{s.ServicesRunning} de {s.ServicesTotal} servicios funcionando";
        ServicesLevel = s.ServicesFailed.Count > 0 ? "Alarm" : s.ServicesWarning.Count > 0 ? "Warning" : "Ok";
        Problems = string.Join("   ", new[]
        {
            s.ServicesFailed.Count > 0 ? "Detenidos: " + string.Join(", ", s.ServicesFailed) : null,
            s.ServicesWarning.Count > 0 ? "Con problemas: " + string.Join(", ", s.ServicesWarning) : null,
        }.Where(p => p is not null));
        // Un servidor anterior no los informa: la fila no se muestra.
        HasClients = s.ClientsDesktop is not null;
        if (s.ClientsDesktop is { } desktop)
        {
            int web = s.ClientsWeb ?? 0;
            ClientsText = (desktop + web).ToString();
            ClientsDetail = $"{desktop} de escritorio" +
                            (s.ClientsLimit is > 0 and { } limit ? $" (de {limit} puesto{(limit == 1 ? "" : "s")})" : "") +
                            $" · {web} en el panel web";
        }
        var up = TimeSpan.FromSeconds(s.UptimeSeconds);
        UptimeText = $"Versión {s.Version} · encendido hace " +
                     (up.TotalDays >= 1 ? $"{(int)up.TotalDays} d {up.Hours} h" : $"{up.Hours} h {up.Minutes} min");
    }
}

/// <summary>
/// Una alerta de automatización en la tarjeta, como una tarjeta de la lista de
/// la pantalla de alarma de las casetas: la barra de color de la prioridad, el
/// título, de dónde viene y si sigue sin confirmar.
/// </summary>
public sealed class AlertLine(WorkflowAlertDto alert)
{
    private static readonly System.Windows.Media.SolidColorBrush CriticalBrush = Frozen(0xD3, 0x2F, 0x2F),
        WarningBrush = Frozen(0xF5, 0x7C, 0x00), InfoBrush = Frozen(0xFB, 0xC0, 0x2D);

    private static System.Windows.Media.SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public WorkflowAlertDto Alert { get; } = alert;
    public string Time { get; } = alert.RaisedAt.ToLocalTime().ToString("HH:mm:ss");
    public string DateText { get; } = alert.RaisedAt.ToLocalTime().ToString("dd-MM-yyyy HH:mm:ss");
    public string Title { get; } = alert.Title;
    /// <summary>De dónde viene: la ubicación del recurso o, si no tiene, la automatización.</summary>
    public string Source { get; } = !string.IsNullOrWhiteSpace(alert.LocationPath) ? alert.LocationPath! : alert.WorkflowName;
    public string Message { get; } = alert.Message ?? "";
    public bool HasMessage => Message.Length > 0;
    public string Origin { get; } = string.IsNullOrWhiteSpace(alert.WorkflowName) ? "" : $"Automatización: {alert.WorkflowName}";
    /// <summary>Consignas de la ficha del recurso: qué tiene que hacer el operador.</summary>
    public string Instructions { get; } = alert.Instructions ?? "";
    public bool HasInstructions => !string.IsNullOrWhiteSpace(Instructions);
    public bool IsPending { get; } = alert.Pending;
    public bool Acknowledged { get; } = alert.AcknowledgedAt is not null;
    public string AckText { get; } = alert.AcknowledgedAt is { } at
        ? alert.AcknowledgedFrom == "screen"
            ? $"Vista en la pantalla «{alert.AcknowledgedBy}» a las {at.ToLocalTime():HH:mm:ss}"
            : $"Confirmada por {alert.AcknowledgedBy} a las {at.ToLocalTime():HH:mm:ss}"
        : alert.RequiresAck ? "Sin confirmar" : "Aviso informativo: no pide confirmación";
    public string PriorityText { get; } = alert.Severity switch
    {
        AlarmSeverity.Critical => "CRÍTICA",
        AlarmSeverity.Warning => "ADVERTENCIA",
        _ => "INFORMATIVA",
    };
    public System.Windows.Media.Brush PriorityBrush { get; } = alert.Severity switch
    {
        AlarmSeverity.Critical => CriticalBrush,
        AlarmSeverity.Warning => WarningBrush,
        _ => InfoBrush,
    };
    /// <summary>Lo atendido se apaga: de un vistazo se ve qué falta.</summary>
    public double CardOpacity => IsPending ? 1 : 0.6;
}

/// <summary>
/// Últimas alertas de las automatizaciones con el diseño de la pantalla de
/// alarma de las casetas (Raspberry): arriba el detalle de la elegida con su
/// imagen y el botón para confirmarla; abajo la lista. Elige sola la pendiente
/// más nueva (o la más nueva) y sigue a la que llega, salvo que el operador
/// esté mirando otra más vieja.
/// </summary>
public sealed partial class AlertsCardPanel : CellPanel
{
    private const int Size = 12;
    private readonly ApiClient _api;
    private readonly VmsHubClient _hub;
    private readonly Action<WorkflowAlertDto> _open;
    private long _imageFor;

    public AlertsCardPanel(ApiClient api, VmsHubClient hub, Action<WorkflowAlertDto> open)
    {
        _api = api;
        _hub = hub;
        _open = open;
        Title = "Últimas alertas";
        _hub.WorkflowNotification += OnNotification;
        _hub.WorkflowAlertAcknowledged += OnAcknowledged;
    }

    public override string Glyph => "\uE7BA";
    public override string Kind => CardKinds.Alerts;

    public ObservableCollection<AlertLine> Items { get; } = [];
    [ObservableProperty] private AlertLine? _selected;
    [ObservableProperty] private System.Windows.Media.ImageSource? _selectedImage;
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private string _pendingText = "";
    [ObservableProperty] private bool _isEmpty;

    public override Task LoadAsync() => ReloadAsync(null);

    /// <param name="arrived">Alerta recién llegada: queda elegida si el operador miraba la más nueva.</param>
    private async Task ReloadAsync(long? arrived)
    {
        try
        {
            var list = await _api.GetAlertsAsync(pendingOnly: false, take: Size);
            long? keep = Selected?.Alert.Id;
            bool followNewest = Selected is null || Selected == Items.FirstOrDefault();
            Items.Clear();
            foreach (var alert in list?.Items ?? []) Items.Add(new AlertLine(alert));
            PendingCount = list?.Pending ?? 0;
            PendingText = PendingCount == 1 ? "1 SIN CONFIRMAR" : $"{PendingCount} SIN CONFIRMAR";
            IsEmpty = Items.Count == 0;
            Selected = (arrived is { } id && followNewest ? Items.FirstOrDefault(l => l.Alert.Id == id) : null)
                       ?? Items.FirstOrDefault(l => l.Alert.Id == keep)
                       ?? Items.FirstOrDefault(l => l.IsPending)
                       ?? Items.FirstOrDefault();
            Status = "";
        }
        catch (ApiException ex) { Status = ex.Message; }
    }

    partial void OnSelectedChanged(AlertLine? value) => _ = LoadImageAsync(value);

    /// <summary>La PRIMERA foto que capturó la automatización (la 1 de la ventana de alarma;
    /// ImagePath es la última que se tomó).</summary>
    private async Task LoadImageAsync(AlertLine? line)
    {
        if (line?.Alert is not { } alert || (alert.ImagePaths.FirstOrDefault() ?? alert.ImagePath) is not { Length: > 0 } path)
        {
            _imageFor = 0;
            SelectedImage = null;
            return;
        }
        // La misma alerta tras recargar la lista: la foto no cambia.
        if (_imageFor == alert.Id && SelectedImage is not null) return;
        _imageFor = alert.Id;
        SelectedImage = null;
        byte[]? bytes;
        try { bytes = await _api.GetWorkflowImageAsync(path); }
        catch (ApiException) { bytes = null; }
        if (_imageFor == alert.Id) SelectedImage = PlatesCardPanel.Decode(bytes, 1280);
    }

    private void OnNotification(WorkflowNotificationDto dto)
    {
        if (dto.AlertId > 0) Ui.Run(() => _ = ReloadAsync(dto.AlertId));
    }

    private void OnAcknowledged(WorkflowAlertDto dto) => Ui.Run(() => _ = ReloadAsync(null));

    /// <summary>«Ver la alarma»: la ventana de alarma de siempre, con sus cámaras en vivo.</summary>
    [RelayCommand]
    private void Open(AlertLine? line)
    {
        if ((line ?? Selected) is { } target) _open(target.Alert);
    }

    /// <summary>Se da por enterado desde la tarjeta (como «Marcar como vista» en la caseta).</summary>
    [RelayCommand]
    private async Task AcknowledgeAsync(AlertLine? line)
    {
        if ((line ?? Selected) is not { IsPending: true } target) return;
        try
        {
            await _api.AcknowledgeAlertAsync(target.Alert.Id);
            await ReloadAsync(null);
        }
        catch (ApiException ex) { Status = ex.Message; }
    }

    public override void Dispose()
    {
        _hub.WorkflowNotification -= OnNotification;
        _hub.WorkflowAlertAcknowledged -= OnAcknowledged;
    }
}

/// <summary>Una lectura de patente en la tarjeta.</summary>
public sealed class PlateLine(PlateEventDto e)
{
    public PlateEventDto Event { get; } = e;
    // CapturedAt ya es la hora local del equipo (como en la página ANPR): convertirla la corría el desfase de UTC.
    public string Time { get; } = e.CapturedAt.ToString("HH:mm:ss");
    public string Plate { get; } = e.PlateNumber;
    public string Where { get; } = string.Join(" · ", new[] { e.ChannelName, e.Direction }.Where(s => !string.IsNullOrWhiteSpace(s)));
    /// <summary>No pasa como PPU chilena: la patente grande va en rojo, como en la página ANPR.</summary>
    public bool InvalidChile { get; } = e.Chile is { Valid: false };
    public string PlateHint => InvalidChile
        ? "Patente leída por el equipo (no es una patente chilena válida)"
        : "Patente leída por el equipo";
}

/// <summary>
/// Últimos reconocimientos de patente, en vivo: a un lado la escena y el
/// recorte de la patente de la lectura elegida (la más nueva, salvo que el
/// operador elija otra) y al otro la lista. El engranaje de la barra elige de
/// qué cámaras ANPR se muestran las lecturas (por omisión, de todas).
/// </summary>
public sealed partial class PlatesCardPanel : CellPanel
{
    private const int Size = 12;
    /// <summary>Lecturas que se piden al abrir con cámaras elegidas: las de esas cámaras
    /// salen de las últimas de todas (pedirlas por equipo dejaría una búsqueda en la
    /// bitácora por cada uno, cada vez que se abre la tarjeta).</summary>
    private const int FilteredTake = 200;
    private readonly ApiClient _api;
    private readonly VmsHubClient _hub;
    private long _imagesFor;
    /// <summary>Cámaras ANPR (equipos) elegidas; vacío = todas.</summary>
    private IReadOnlyCollection<int> _devices;

    public PlatesCardPanel(ApiClient api, VmsHubClient hub, IReadOnlyCollection<int>? devices = null)
    {
        _api = api;
        _hub = hub;
        _devices = devices ?? [];
        Title = TitleFor(_devices);
        _hub.PlateRecognized += OnPlate;
    }

    private static string TitleFor(IReadOnlyCollection<int> devices) =>
        devices.Count == 0 ? "Últimas patentes"
        : devices.Count == 1 ? "Últimas patentes · 1 cámara" : $"Últimas patentes · {devices.Count} cámaras";

    private bool Accepts(PlateEventDto e) => _devices.Count == 0 || _devices.Contains(e.DeviceId);

    public override bool CanConfigure => true;
    public override string ConfigureHint => "Elegir de qué cámaras ANPR se reciben los reconocimientos";

    /// <summary>
    /// El engranaje: la lista de cámaras ANPR que ve esta sesión (su alcance),
    /// para marcar de cuáles mostrar las lecturas. Lo elegido viaja en la
    /// clave de la tarjeta (tableros y última sesión).
    /// </summary>
    protected override async Task ConfigureAsync()
    {
        List<AnprSourceDto> sources;
        try { sources = await _api.GetAnprSourcesAsync(); }
        catch (ApiException ex)
        {
            Status = ex.Message;
            return;
        }
        var owner = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive)
                    ?? System.Windows.Application.Current.MainWindow;
        if (Views.PlateSourcesWindow.Ask(owner, sources, _devices) is not { } chosen) return;
        _devices = chosen;
        Title = TitleFor(_devices);
        if (Content is { } content)
        {
            // El cuadro avisa y se guarda la sesión con las cámaras elegidas.
            Content = content with { Ref = CardKinds.PlatesRef(_devices) };
            OnPropertyChanged(nameof(Content));
        }
        try { await LoadAsync(); }
        catch (ApiException ex) { Status = ex.Message; }
    }

    public override string Glyph => "\uE804";
    public override string Kind => CardKinds.Plates;

    public ObservableCollection<PlateLine> Items { get; } = [];

    [ObservableProperty] private PlateLine? _selected;
    [ObservableProperty] private System.Windows.Media.ImageSource? _sceneImage;
    [ObservableProperty] private System.Windows.Media.ImageSource? _plateImage;
    [ObservableProperty] private string _selectedText = "";

    public override async Task LoadAsync()
    {
        var events = await _api.GetPlateEventsAsync(take: _devices.Count == 0 ? Size : FilteredTake);
        Items.Clear();
        foreach (var e in events.Where(Accepts).Take(Size)) Items.Add(new PlateLine(e));
        Selected = Items.FirstOrDefault();
        Status = Items.Count == 0 && _devices.Count > 0 ? "Sin lecturas recientes de las cámaras elegidas." : "";
    }

    private void OnPlate(PlateEventDto e) => Ui.Run(() =>
    {
        if (!Accepts(e)) return;
        Status = "";
        // Si el operador estaba mirando la más nueva, sigue a la que llega.
        bool followNewest = Selected is null || Selected == Items.FirstOrDefault();
        var line = new PlateLine(e);
        Items.Insert(0, line);
        while (Items.Count > Size) Items.RemoveAt(Items.Count - 1);
        if (followNewest) Selected = line;
    });

    partial void OnSelectedChanged(PlateLine? value) => _ = LoadImagesAsync(value);

    private async Task LoadImagesAsync(PlateLine? line)
    {
        if (line is null)
        {
            SceneImage = PlateImage = null;
            SelectedText = "";
            return;
        }
        var e = line.Event;
        _imagesFor = e.Id;
        SelectedText = $"{e.CapturedAt:dd-MM-yyyy HH:mm:ss} · {line.Where}";
        var scene = e.HasSceneImage ? await _api.GetPlateImageAsync(e.Id, "scene") : null;
        var plate = e.HasPlateImage ? await _api.GetPlateImageAsync(e.Id, "plate") : null;
        if (_imagesFor != e.Id) return; // ya eligió otra mientras descargaba
        SceneImage = Decode(scene, 1280);
        PlateImage = Decode(plate);
    }

    /// <summary>JPEG → imagen congelada (como en el módulo de patentes; también la foto de las alertas).</summary>
    internal static System.Windows.Media.ImageSource? Decode(byte[]? bytes, int maxWidth = 0)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            if (maxWidth > 0) image.DecodePixelWidth = maxWidth;
            image.StreamSource = new System.IO.MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    public override void Dispose() => _hub.PlateRecognized -= OnPlate;
}

/// <summary>Un paso por cualquier puerta, con la puerta.</summary>
public sealed class AccessLine(AccessEventDto e)
{
    private readonly AccessEventLine _line = new(e);
    public string Time => _line.Time;
    public string What => _line.What;
    public string Who => _line.Who;
    public string Level => _line.Level;
    public string Door { get; } = string.Join(" · ", new[] { e.DoorName, e.DeviceName }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>Últimos pasos por todas las puertas, en vivo.</summary>
public sealed partial class AccessCardPanel : CellPanel
{
    private const int Size = 12;
    private readonly ApiClient _api;
    private readonly VmsHubClient _hub;

    public AccessCardPanel(ApiClient api, VmsHubClient hub)
    {
        _api = api;
        _hub = hub;
        Title = "Últimos accesos";
        _hub.AccessEventReceived += OnEvent;
    }

    public override string Glyph => "\uE8D7";
    public override string Kind => CardKinds.Access;

    public ObservableCollection<AccessLine> Items { get; } = [];

    public override async Task LoadAsync()
    {
        var page = await _api.GetAccessEventsAsync(Size);
        Items.Clear();
        foreach (var e in page.Items) Items.Add(new AccessLine(e));
    }

    private void OnEvent(AccessEventDto e) => Ui.Run(() =>
    {
        Items.Insert(0, new AccessLine(e));
        while (Items.Count > Size) Items.RemoveAt(Items.Count - 1);
    });

    public override void Dispose() => _hub.AccessEventReceived -= OnEvent;
}
