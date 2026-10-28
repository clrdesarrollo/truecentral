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
public sealed partial class EquipmentCardPanel(ApiClient api) : PollingCardPanel(TimeSpan.FromSeconds(15))
{
    public override string Glyph => "\uE9D9";
    public override string Kind => CardKinds.Equipment;

    public ObservableCollection<EquipmentLine> Lines { get; } = [];
    public ObservableCollection<OfflineLine> Offline { get; } = [];
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _level = "Unknown";

    protected override async Task RefreshAsync()
    {
        Title = "Estado de los equipos";
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

/// <summary>Salud del servidor: CPU, memoria, disco, servicios y tiempo encendido.</summary>
public sealed partial class ServerCardPanel(ApiClient api) : PollingCardPanel(TimeSpan.FromSeconds(5))
{
    public override string Glyph => "\uE977";
    public override string Kind => CardKinds.Server;

    [ObservableProperty] private double _cpu;
    [ObservableProperty] private double _ram;
    [ObservableProperty] private double _disk;
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _ramText = "—";
    [ObservableProperty] private string _diskText = "—";
    [ObservableProperty] private string _servicesText = "—";
    [ObservableProperty] private string _servicesLevel = "Unknown";
    [ObservableProperty] private string _problems = "";
    [ObservableProperty] private string _uptimeText = "";

    protected override async Task RefreshAsync()
    {
        Title = "Servidor";
        var s = (await api.GetDashboardAsync()).Server;
        if (s is null) return;
        Cpu = s.Cpu ?? 0;
        Ram = s.Ram ?? 0;
        Disk = s.Disk ?? 0;
        CpuText = s.Cpu is { } cpu ? $"{cpu:0} %" : "—";
        RamText = s.Ram is { } ram ? $"{ram:0} %  ({s.RamUsedGb:0.0} de {s.RamTotalGb:0.0} GB)" : "—";
        DiskText = s.Disk is { } disk ? $"{disk:0} %  ({s.DiskUsedGb:0} de {s.DiskTotalGb:0} GB{(string.IsNullOrEmpty(s.DiskName) ? "" : $", {s.DiskName}")})" : "—";
        ServicesText = $"{s.ServicesRunning} de {s.ServicesTotal} servicios funcionando";
        ServicesLevel = s.ServicesFailed.Count > 0 ? "Alarm" : s.ServicesWarning.Count > 0 ? "Warning" : "Ok";
        Problems = string.Join("   ", new[]
        {
            s.ServicesFailed.Count > 0 ? "Detenidos: " + string.Join(", ", s.ServicesFailed) : null,
            s.ServicesWarning.Count > 0 ? "Con problemas: " + string.Join(", ", s.ServicesWarning) : null,
        }.Where(p => p is not null));
        var up = TimeSpan.FromSeconds(s.UptimeSeconds);
        UptimeText = $"Versión {s.Version} · encendido hace " +
                     (up.TotalDays >= 1 ? $"{(int)up.TotalDays} d {up.Hours} h" : $"{up.Hours} h {up.Minutes} min");
    }
}

/// <summary>Una alerta de automatización en la tarjeta.</summary>
public sealed class AlertLine(WorkflowAlertDto alert)
{
    public WorkflowAlertDto Alert { get; } = alert;
    public string Time { get; } = alert.RaisedAt.ToLocalTime().ToString("dd-MM HH:mm");
    public string Title { get; } = alert.Title;
    public string Detail { get; } = alert.LocationPath ?? alert.Message ?? "";
    public string State { get; } = alert.Pending ? "Pendiente" : "Confirmada";
    public string Level { get; } = alert.Pending ? (alert.Severity == AlarmSeverity.Critical ? "Alarm" : "Warning") : "Unknown";
}

/// <summary>Últimas alertas de las automatizaciones (con las pendientes marcadas).</summary>
public sealed partial class AlertsCardPanel : CellPanel
{
    private readonly ApiClient _api;
    private readonly VmsHubClient _hub;
    private readonly Action<WorkflowAlertDto> _open;

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
    [ObservableProperty] private string _summary = "";

    public override Task LoadAsync() => ReloadAsync();

    private async Task ReloadAsync()
    {
        try
        {
            var list = await _api.GetAlertsAsync(pendingOnly: false, take: 12);
            Items.Clear();
            foreach (var alert in list?.Items ?? []) Items.Add(new AlertLine(alert));
            Summary = list is null ? "" : list.Pending == 0 ? "Sin alertas pendientes" : $"{list.Pending} pendiente(s) de confirmar";
            Status = "";
        }
        catch (ApiException ex) { Status = ex.Message; }
    }

    private void OnNotification(WorkflowNotificationDto dto)
    {
        if (dto.AlertId > 0) Ui.Run(() => _ = ReloadAsync());
    }

    private void OnAcknowledged(WorkflowAlertDto dto) => Ui.Run(() => _ = ReloadAsync());

    [RelayCommand] private void Open(AlertLine line) => _open(line.Alert);

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
    public string Time { get; } = e.CapturedAt.ToLocalTime().ToString("HH:mm:ss");
    public string Plate { get; } = e.PlateNumber;
    public string Where { get; } = string.Join(" · ", new[] { e.ChannelName, e.Direction }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>
/// Últimos reconocimientos de patente, en vivo: a un lado la escena y el
/// recorte de la patente de la lectura elegida (la más nueva, salvo que el
/// operador elija otra) y al otro la lista.
/// </summary>
public sealed partial class PlatesCardPanel : CellPanel
{
    private const int Size = 12;
    private readonly ApiClient _api;
    private readonly VmsHubClient _hub;
    private long _imagesFor;

    public PlatesCardPanel(ApiClient api, VmsHubClient hub)
    {
        _api = api;
        _hub = hub;
        Title = "Últimas patentes";
        _hub.PlateRecognized += OnPlate;
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
        var events = await _api.GetPlateEventsAsync(take: Size);
        Items.Clear();
        foreach (var e in events) Items.Add(new PlateLine(e));
        Selected = Items.FirstOrDefault();
    }

    private void OnPlate(PlateEventDto e) => Ui.Run(() =>
    {
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
        SelectedText = $"{e.PlateNumber} · {e.CapturedAt.ToLocalTime():dd-MM-yyyy HH:mm:ss} · {line.Where}";
        var scene = e.HasSceneImage ? await _api.GetPlateImageAsync(e.Id, "scene") : null;
        var plate = e.HasPlateImage ? await _api.GetPlateImageAsync(e.Id, "plate") : null;
        if (_imagesFor != e.Id) return; // ya eligió otra mientras descargaba
        SceneImage = Decode(scene, 1280);
        PlateImage = Decode(plate);
    }

    /// <summary>JPEG → imagen congelada (como en el módulo de patentes).</summary>
    private static System.Windows.Media.ImageSource? Decode(byte[]? bytes, int maxWidth = 0)
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
