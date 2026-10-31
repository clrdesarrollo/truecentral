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
    /// <summary>Cuadro vigía: cambia solo a la cámara del último evento (opciones en la clave, ver <see cref="WatchOptions"/>).</summary>
    public const string Watch = "card-watch";
    /// <summary>Página web de la lista del servidor (no es tarjeta: es lo que pide la lista).</summary>
    public const string Web = "web";
    /// <summary>«Otra dirección…»: se escribe al abrirla.</summary>
    public const string WebCustom = "web-custom";
    /// <summary>Grupo de parlantes (Id = el del grupo).</summary>
    public const string SpeakerGroup = "speaker-group";

    /// <summary>«Últimas patentes» con las cámaras ANPR elegidas en su engranaje: "card-plates?devices=3,7"
    /// (sin cámaras = todas, la clave sola). Así lo guardan los tableros y la última sesión.</summary>
    public static string PlatesRef(IEnumerable<int> devices) => WithOptions(Plates, DevicesOption(devices));

    /// <summary>Las cámaras ANPR de una clave de «Últimas patentes» (vacío = todas).</summary>
    public static IReadOnlyCollection<int> PlatesDevices(string? reference) => Ids(Option(reference, "devices"));

    /// <summary>«Últimas alertas» con el interruptor «Solo sin confirmar» encendido: "card-alerts?pending=1"
    /// (apagado = la clave sola).</summary>
    public static string AlertsRef(bool pendingOnly) => pendingOnly ? $"{Alerts}?pending=1" : Alerts;

    /// <summary>La clave de «Últimas alertas» trae el interruptor «Solo sin confirmar» encendido.</summary>
    public static bool AlertsPendingOnly(string? reference) => Option(reference, "pending") == "1";

    /// <summary>«Últimos accesos» con los equipos de acceso elegidos en su engranaje y el interruptor
    /// «Eventos del sistema»: "card-access?devices=3,7&amp;system=1" (todos los equipos y sin eventos
    /// del sistema = la clave sola).</summary>
    public static string AccessRef(IEnumerable<int> devices, bool includeSystem) =>
        WithOptions(Access, DevicesOption(devices), includeSystem ? "system=1" : null);

    /// <summary>Los equipos de acceso de una clave de «Últimos accesos» (vacío = todos).</summary>
    public static IReadOnlyCollection<int> AccessDevices(string? reference) => Ids(Option(reference, "devices"));

    /// <summary>La clave de «Últimos accesos» trae encendido «Eventos del sistema».</summary>
    public static bool AccessIncludesSystem(string? reference) => Option(reference, "system") == "1";

    private static string? DevicesOption(IEnumerable<int> devices)
    {
        var ids = devices.Distinct().Order().ToList();
        return ids.Count == 0 ? null : $"devices={string.Join(',', ids)}";
    }

    private static string WithOptions(string key, params string?[] options)
    {
        var present = options.OfType<string>().ToList();
        return present.Count == 0 ? key : $"{key}?{string.Join('&', present)}";
    }

    /// <summary>El valor de una opción de la clave ("devices" de "card-access?devices=3,7&amp;system=1"
    /// es "3,7"); null si no la trae.</summary>
    internal static string? Option(string? reference, string name)
    {
        int q = reference?.IndexOf('?') ?? -1;
        if (q < 0) return null;
        return reference![(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(p => p.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
    }

    internal static IReadOnlyCollection<int> Ids(string? list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out int id) ? id : 0).Where(id => id > 0).ToHashSet();
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
    [ObservableProperty] private string _versionText = "";
    // Tiempo encendido: desde el último reinicio del servicio (y del equipo, en el detalle).
    [ObservableProperty] private string _uptimeText = "";
    [ObservableProperty] private string _uptimeDetail = "";
    // Clientes conectados: puestos de escritorio y sesiones del panel web con el canal en vivo abierto.
    [ObservableProperty] private bool _hasClients;
    [ObservableProperty] private string _clientsText = "";
    [ObservableProperty] private string _clientsDetail = "";
    // Ingresos rechazados en 24 h: fallos sueltos y ataques (solo con permiso de bitácora).
    [ObservableProperty] private bool _hasLogins;
    [ObservableProperty] private string _loginsText = "";
    [ObservableProperty] private string _loginsDetail = "";
    [ObservableProperty] private string _attacksDetail = "";
    [ObservableProperty] private string _loginsLevel = "Ok";

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
        VersionText = $"Versión {s.Version}";
        UptimeText = Duration(TimeSpan.FromSeconds(s.UptimeSeconds));
        UptimeDetail = string.Join(" · ", new[]
        {
            s.StartedAt is { } started ? $"desde el {started.ToLocalTime():dd-MM-yyyy HH:mm}" : null,
            s.OsUptimeSeconds is { } os ? $"Windows encendido hace {Duration(TimeSpan.FromSeconds(os))}" : null,
        }.Where(p => p is not null));

        HasLogins = s.Logins is not null;
        if (s.Logins is { } l)
        {
            LoginsText = l.Failed.ToString();
            LoginsDetail = l.Failed == 0 ? "ninguno en 24 h"
                : $"en las últimas 24 h · {l.FailedLastHour} en la última hora";
            int lockedNow = l.LockedAccountsNow + l.LockedIpsNow;
            AttacksDetail = l.Locks == 0 && l.RejectedWhileLocked == 0 ? "Sin ataques (intentos repetidos)"
                : $"Ataques: {l.Locks} bloqueo{(l.Locks == 1 ? "" : "s")} desde {l.AttackIps} IP" +
                  (l.LastAttackAt is { } at ? $" · último {at.ToLocalTime():dd-MM HH:mm}" : "") +
                  (lockedNow > 0 ? $" · {lockedNow} bloqueado{(lockedNow == 1 ? "" : "s")} ahora" : "");
            // Rojo: un ataque en curso (algo bloqueado ahora o en la última hora); ámbar: hubo
            // un ataque en el día o muchos fallos sueltos.
            bool recentAttack = l.LastAttackAt is { } last && DateTime.UtcNow - last.ToUniversalTime() < TimeSpan.FromHours(1);
            LoginsLevel = lockedNow > 0 || recentAttack ? "Alarm"
                : l.Locks > 0 || l.RejectedWhileLocked > 0 || l.FailedLastHour >= 5 ? "Warning" : "Ok";
        }
    }

    /// <summary>"3 d 4 h", "5 h 12 min" o "8 min".</summary>
    private static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h"
        : t.TotalHours >= 1 ? $"{t.Hours} h {t.Minutes} min"
        : $"{Math.Max(0, t.Minutes)} min";
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
/// esté mirando otra más vieja. Con «Solo sin confirmar» la lista deja solo las
/// pendientes: la que se confirma sale y queda elegida la siguiente.
/// </summary>
public sealed partial class AlertsCardPanel : CellPanel
{
    private const int Size = 12;
    private readonly ApiClient _api;
    private readonly VmsHubClient _hub;
    private readonly Action<WorkflowAlertDto> _open;
    private long _imageFor;
    private bool _pendingOnly;
    /// <summary>Lecturas en curso: solo la última llena la lista (el interruptor puede ir y volver antes de que responda la primera).</summary>
    private int _reloads;

    public AlertsCardPanel(ApiClient api, VmsHubClient hub, Action<WorkflowAlertDto> open, bool pendingOnly = false)
    {
        _api = api;
        _hub = hub;
        _open = open;
        _pendingOnly = pendingOnly;
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

    /// <summary>
    /// El interruptor «Solo sin confirmar» sobre la lista. Viaja en la clave de
    /// la tarjeta, así que el tablero y la última sesión lo recuerdan.
    /// </summary>
    public bool PendingOnly
    {
        get => _pendingOnly;
        set
        {
            if (!SetProperty(ref _pendingOnly, value)) return;
            if (Content is { } content)
            {
                // El cuadro avisa y se guarda la sesión con el interruptor como quedó.
                Content = content with { Ref = CardKinds.AlertsRef(value) };
                OnPropertyChanged(nameof(Content));
            }
            _ = ReloadAsync(null);
        }
    }

    public override Task LoadAsync() => ReloadAsync(null);

    /// <param name="arrived">Alerta recién llegada: queda elegida si el operador miraba la más nueva.</param>
    private async Task ReloadAsync(long? arrived)
    {
        int reload = ++_reloads;
        try
        {
            var list = await _api.GetAlertsAsync(pendingOnly: PendingOnly, take: Size);
            if (reload != _reloads) return;
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

    private static readonly Views.CardSourcesWindow.Texts SourcesTexts = new(
        Glyph: "\uE804",
        Header: "Cámaras de «Últimas patentes»",
        Intro: "Reconocimientos que muestra esta tarjeta",
        All: "De todas las cámaras ANPR",
        Some: "Solo de las cámaras marcadas",
        Empty: "No hay cámaras ANPR a su alcance: se configuran en el panel web (Fuentes de video → ANPR).",
        NoneChecked: "Marque al menos una cámara, o elija «De todas las cámaras ANPR».");

    /// <summary>Dueña de los diálogos de los engranajes: la ventana activa (la grilla puede estar en otra pantalla).</summary>
    internal static System.Windows.Window? ActiveWindow() =>
        System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive)
        ?? System.Windows.Application.Current.MainWindow;

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
        var items = sources.Select(s => new Views.CardSourcesWindow.Source(s.DeviceId, s.DeviceName, s.Live,
            !s.Enabled ? "Apagada como fuente de patentes"
            : s.LastEventAt is { } at ? $"Última lectura {at.ToLocalTime():dd-MM HH:mm}" : "Sin lecturas todavía")).ToList();
        if (Views.CardSourcesWindow.Ask(ActiveWindow(), SourcesTexts, items, _devices) is not { } chosen) return;
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

/// <summary>
/// Un paso por cualquier puerta, con la puerta, como una tarjeta de la lista de
/// alertas: la barra de color del resultado, quién, dónde y cuándo. La foto del
/// momento puede llegar después del evento (<see cref="SetPhoto"/>).
/// </summary>
public sealed partial class AccessLine(AccessEventDto e) : ObservableObject
{
    private static readonly System.Windows.Media.SolidColorBrush GrantedBrush = Frozen(0x2E, 0x7D, 0x32),
        DeniedBrush = Frozen(0xD3, 0x2F, 0x2F), OtherBrush = Frozen(0x60, 0x7D, 0x8B);

    private static System.Windows.Media.SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private readonly AccessEventLine _line = new(e);
    public AccessEventDto Event { get; } = e;
    public string Time => _line.Time;
    public string DateText { get; } = e.Timestamp.ToLocalTime().ToString("dd-MM-yyyy HH:mm:ss");
    public string What => _line.What;
    public string Who => _line.Who;
    public string Level => _line.Level;
    public string Door { get; } = string.Join(" · ", new[] { e.DoorName, e.DeviceName }.Where(s => !string.IsNullOrWhiteSpace(s)));
    /// <summary>Lo pasó una persona del padrón: es la que se muestra arriba, con su foto.</summary>
    public bool IsPerson { get; } = e.PersonId is not null;
    /// <summary>La persona tiene foto enrolada (se pide aparte al elegirla).</summary>
    public bool HasFace { get; } = e is { PersonId: not null, HasFace: true };
    public string NoFaceText => IsPerson ? "Sin foto en el padrón" : "Evento sin persona del padrón";
    /// <summary>Hay foto del momento del paso (se pide aparte al elegirlo).</summary>
    [ObservableProperty] private bool _hasPhoto = e.HasPhoto;
    /// <summary>De dónde salió la foto del momento y cuándo, o por qué no hay.</summary>
    [ObservableProperty] private string _photoText = PhotoTextOf(e, e.HasPhoto, e.PhotoCamera, e.PhotoAt);

    /// <summary>Quedó la foto del momento (aviso del hub).</summary>
    public void SetPhoto(string? camera, DateTime at)
    {
        HasPhoto = true;
        PhotoText = PhotoTextOf(Event, true, camera, at);
    }

    private static string PhotoTextOf(AccessEventDto e, bool hasPhoto, string? camera, DateTime? at)
    {
        if (!hasPhoto) return "Sin foto del momento";
        if (camera is null) return "Captura del terminal";
        double after = at is { } taken ? (taken.ToUniversalTime() - e.Timestamp.ToUniversalTime()).TotalSeconds : 0;
        return after < 1.5 ? $"Cámara {camera}" : $"Cámara {camera} · {after:0} s después del paso";
    }
    /// <summary>Área, cargo y con qué se identificó.</summary>
    public string Detail { get; } = string.Join(" · ", new[] { e.Department, e.Position, CredentialOf(e) }
        .Where(s => !string.IsNullOrWhiteSpace(s)));
    public bool HasDetail => Detail.Length > 0;
    public string ResultText => What.ToUpperInvariant();
    public System.Windows.Media.Brush ResultBrush { get; } = e.Kind switch
    {
        AccessEventKind.Granted => GrantedBrush,
        AccessEventKind.Denied or AccessEventKind.Alarm => DeniedBrush,
        _ => OtherBrush,
    };
    /// <summary>Lo que no es un paso de alguien (puesta en hora, armado, puerta abierta o cerrada) se apaga:
    /// de un vistazo se ve quién pasó. Un rechazo o una alarma, aunque no sean de nadie del padrón, no.</summary>
    public double CardOpacity { get; } = e.PersonId is null && e.Kind is not (AccessEventKind.Denied or AccessEventKind.Alarm) ? 0.6 : 1;

    private static string? CredentialOf(AccessEventDto e) => e.Credential switch
    {
        AccessCredentialKind.Card => string.IsNullOrWhiteSpace(e.CardNumber) ? "Tarjeta" : $"Tarjeta {e.CardNumber}",
        AccessCredentialKind.Fingerprint => "Huella",
        AccessCredentialKind.Face => "Rostro",
        AccessCredentialKind.Pin => "PIN",
        AccessCredentialKind.Remote => "Apertura remota",
        AccessCredentialKind.ExitButton => "Botón de salida",
        AccessCredentialKind.Qr => "Código QR",
        AccessCredentialKind.Plate => "Patente",
        _ => null,
    };
}

/// <summary>
/// Últimos pasos por todas las puertas, en vivo, con el diseño de la tarjeta de
/// alertas: arriba quién pasó —su foto del padrón, la puerta, la hora y con qué
/// se identificó—; abajo la lista. Elige solo el último paso de una persona (los
/// eventos del equipo, como la puesta en hora, solo entran a la lista) y sigue
/// al que llega, salvo que el operador esté mirando otro. Si los más nuevos son
/// todos del equipo, el último que ingresó (pedido aparte) queda al pie de la lista.
///
/// Por omisión no muestra los eventos del sistema (<see cref="AccessEventRelevance"/>):
/// con HikCentral conectado, cada terminal se pone en hora cada minuto y la lista
/// quedaba llena de eso. El interruptor «Eventos del sistema» los suma y el
/// engranaje elige de qué equipos de acceso se muestran los pasos (por omisión,
/// de todos). Las dos cosas viajan en la clave de la tarjeta.
///
/// Arriba se cruzan la foto del momento del paso —la captura del terminal o la
/// de la cámara de la puerta, que llega un poco después del evento— y la foto
/// del padrón. «Ver en vivo» abre las cámaras de la puerta en la ventana de
/// verificación (la grilla del operador no se toca).
/// </summary>
public sealed partial class AccessCardPanel : CellPanel
{
    private const int Size = 12;
    private readonly ApiClient _api;
    private readonly VmsHubClient _hub;
    /// <summary>Fotos ya bajadas por persona: la misma persona entra varias veces al día.</summary>
    private readonly Dictionary<int, System.Windows.Media.ImageSource> _faces = [];
    private int _faceFor;
    /// <summary>El último paso de una persona: no sale de la lista aunque lleguen muchos eventos del equipo.</summary>
    private AccessLine? _lastPerson;
    /// <summary>Equipos de acceso elegidos en el engranaje; vacío = todos.</summary>
    private IReadOnlyCollection<int> _devices;
    private bool _includeSystem;
    /// <summary>Lecturas en curso: solo la última llena la lista (el interruptor puede ir y volver antes de que responda la primera).</summary>
    private int _reloads;
    /// <summary>Fotos del momento ya bajadas, por evento (las de la lista).</summary>
    private readonly Dictionary<long, System.Windows.Media.ImageSource> _photos = [];
    private long _photoFor;
    /// <summary>Abre las cámaras de la puerta del paso; devuelve el motivo si no se pudo.</summary>
    private readonly Func<AccessEventDto, Task<string?>>? _openLive;

    public AccessCardPanel(ApiClient api, VmsHubClient hub, IReadOnlyCollection<int>? devices = null, bool includeSystem = false,
        Func<AccessEventDto, Task<string?>>? openLive = null)
    {
        _api = api;
        _hub = hub;
        _devices = devices ?? [];
        _includeSystem = includeSystem;
        _openLive = openLive;
        Title = TitleFor(_devices);
        _hub.AccessEventReceived += OnEvent;
        _hub.AccessEventPhotoReady += OnPhoto;
    }

    private static string TitleFor(IReadOnlyCollection<int> devices) =>
        devices.Count == 0 ? "Últimos accesos"
        : devices.Count == 1 ? "Últimos accesos · 1 equipo" : $"Últimos accesos · {devices.Count} equipos";

    private bool Accepts(AccessEventDto e) =>
        (_devices.Count == 0 || _devices.Contains(e.DeviceId)) &&
        (_includeSystem || !AccessEventRelevance.IsSystem(e.Kind, e.Credential));

    public override string Glyph => "\uE8D7";
    public override string Kind => CardKinds.Access;

    public ObservableCollection<AccessLine> Items { get; } = [];
    [ObservableProperty] private AccessLine? _selected;
    [ObservableProperty] private System.Windows.Media.ImageSource? _selectedFace;
    [ObservableProperty] private System.Windows.Media.ImageSource? _selectedPhoto;
    [ObservableProperty] private bool _isEmpty;
    public bool CanOpenLive => _openLive is not null;

    /// <summary>
    /// El interruptor «Eventos del sistema» sobre la lista: la puesta en hora, los
    /// avisos del equipo y la cerradura y el sensor de la puerta. Viaja en la clave
    /// de la tarjeta, así que el tablero y la última sesión lo recuerdan.
    /// </summary>
    public bool IncludeSystem
    {
        get => _includeSystem;
        set
        {
            if (!SetProperty(ref _includeSystem, value)) return;
            SaveOptions();
            _ = ReloadSafeAsync();
        }
    }

    public override bool CanConfigure => true;
    public override string ConfigureHint => "Elegir de qué equipos de acceso se muestran los pasos";

    private static readonly Views.CardSourcesWindow.Texts SourcesTexts = new(
        Glyph: "\uE8D7",
        Header: "Equipos de «Últimos accesos»",
        Intro: "Pasos que muestra esta tarjeta",
        All: "De todos los equipos de acceso",
        Some: "Solo de los equipos marcados",
        Empty: "No hay puertas a su alcance: los equipos se agregan en el panel web (Dispositivos → Control de acceso).",
        NoneChecked: "Marque al menos un equipo, o elija «De todos los equipos de acceso».");

    /// <summary>
    /// El engranaje: los equipos de acceso de las puertas que ve esta sesión (su
    /// alcance), para marcar de cuáles mostrar los pasos.
    /// </summary>
    protected override async Task ConfigureAsync()
    {
        List<AccessDoorStateDto> doors;
        try { doors = await _api.GetAccessDoorsAsync(); }
        catch (ApiException ex)
        {
            Status = ex.Message;
            return;
        }
        var items = doors.GroupBy(d => d.DeviceId).Select(g =>
        {
            var first = g.First();
            string doorText = g.Count() == 1 ? first.Name : $"{g.Count()} puertas";
            return new Views.CardSourcesWindow.Source(g.Key, first.DeviceName,
                first.DeviceStatus == AccessDeviceStatus.Online,
                string.IsNullOrWhiteSpace(first.Location) ? doorText : $"{doorText} · {first.Location}");
        }).ToList();
        if (Views.CardSourcesWindow.Ask(PlatesCardPanel.ActiveWindow(), SourcesTexts, items, _devices) is not { } chosen) return;
        _devices = chosen;
        Title = TitleFor(_devices);
        SaveOptions();
        await ReloadSafeAsync();
    }

    /// <summary>El cuadro avisa y se guarda la sesión con las opciones como quedaron.</summary>
    private void SaveOptions()
    {
        if (Content is not { } content) return;
        Content = content with { Ref = CardKinds.AccessRef(_devices, _includeSystem) };
        OnPropertyChanged(nameof(Content));
    }

    private async Task ReloadSafeAsync()
    {
        try { await LoadAsync(); }
        catch (ApiException ex) { Status = ex.Message; }
    }

    public override async Task LoadAsync()
    {
        int reload = ++_reloads;
        // El último que ingresó se pide aparte: entre los más nuevos puede no haber ninguno.
        var latest = _api.GetAccessEventsAsync(Size, devices: _devices, noSystem: !_includeSystem);
        var person = _api.GetAccessEventsAsync(1, withPerson: true, devices: _devices, noSystem: !_includeSystem);
        // Un servidor sin los filtros noSystem o withPerson devuelve de todo: lo que no va se descarta.
        var lines = (await latest).Items.Where(Accepts).Select(e => new AccessLine(e)).ToList();
        var lastPerson = (await person).Items.FirstOrDefault(e => e.PersonId is not null && Accepts(e));
        if (reload != _reloads) return;
        Status = "";
        _lastPerson = lastPerson is null ? null
            : lines.FirstOrDefault(l => l.Event.Id == lastPerson.Id) ?? new AccessLine(lastPerson);
        Items.Clear();
        foreach (var line in lines) Items.Add(line);
        if (_lastPerson is not null && !Items.Contains(_lastPerson)) Items.Add(_lastPerson);
        Trim();
        IsEmpty = Items.Count == 0;
        Selected = Newest();
    }

    /// <summary>El que se muestra solo: el último paso de una persona o, si no hay, el último evento.</summary>
    private AccessLine? Newest() => _lastPerson ?? Items.FirstOrDefault();

    /// <summary>Deja la lista en su tamaño quitando los más viejos, menos el último que ingresó.</summary>
    private void Trim()
    {
        while (Items.Count > Size) Items.Remove(Items.Last(l => l != _lastPerson));
    }

    private void OnEvent(AccessEventDto e) => Ui.Run(() =>
    {
        if (!Accepts(e)) return;
        // Si el operador miraba el último, pasa a mostrarse quien acaba de pasar.
        bool followNewest = Selected is null || Selected == Newest();
        var line = new AccessLine(e);
        Items.Insert(0, line);
        if (line.IsPerson) _lastPerson = line;
        Trim();
        IsEmpty = false;
        if (followNewest || Selected is null || !Items.Contains(Selected)) Selected = Newest();
    });

    partial void OnSelectedChanged(AccessLine? value)
    {
        _ = LoadFaceAsync(value);
        _ = LoadPhotoAsync(value);
    }

    /// <summary>Llegó la foto del momento de un paso de la lista.</summary>
    private void OnPhoto(AccessEventPhotoDto dto) => Ui.Run(() =>
    {
        if (Items.FirstOrDefault(l => l.Event.Id == dto.EventId) is not { } line) return;
        line.SetPhoto(dto.Camera, dto.TakenAt);
        if (line == Selected) _ = LoadPhotoAsync(line);
    });

    private async Task LoadPhotoAsync(AccessLine? line)
    {
        if (line is not { HasPhoto: true })
        {
            _photoFor = 0;
            SelectedPhoto = null;
            return;
        }
        long id = line.Event.Id;
        _photoFor = id;
        if (_photos.TryGetValue(id, out var known))
        {
            SelectedPhoto = known;
            return;
        }
        SelectedPhoto = null;
        var photo = PlatesCardPanel.Decode(await _api.GetAccessEventPhotoAsync(id), 1280);
        if (photo is not null)
        {
            if (_photos.Count >= 32) _photos.Clear();
            _photos[id] = photo;
        }
        if (_photoFor == id) SelectedPhoto = photo;
    }

    /// <summary>«Ver en vivo»: las cámaras de la puerta del paso elegido.</summary>
    [RelayCommand]
    private async Task OpenLiveAsync()
    {
        if (_openLive is null || Selected?.Event is not { } e) return;
        Status = await _openLive(e) ?? "";
    }

    private async Task LoadFaceAsync(AccessLine? line)
    {
        if (line is not { HasFace: true, Event.PersonId: int person })
        {
            _faceFor = 0;
            SelectedFace = null;
            return;
        }
        _faceFor = person;
        if (_faces.TryGetValue(person, out var known))
        {
            SelectedFace = known;
            return;
        }
        SelectedFace = null;
        var face = PlatesCardPanel.Decode(await _api.GetAccessPersonFaceAsync(person), 640);
        if (face is not null)
        {
            if (_faces.Count >= 64) _faces.Clear();
            _faces[person] = face;
        }
        if (_faceFor == person) SelectedFace = face;
    }

    public override void Dispose()
    {
        _hub.AccessEventReceived -= OnEvent;
        _hub.AccessEventPhotoReady -= OnPhoto;
    }
}
