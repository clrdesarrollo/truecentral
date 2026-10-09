using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Módulo Cerco eléctrico: tarjeta por panel con su estado (armado, sirena, pulso,
/// cerco, zonas), órdenes de armar/desarmar/silenciar y los últimos eventos. Igual
/// que Paneles de alarma, el cliente no sondea: el servidor empuja estado y eventos
/// por el hub y las órdenes van por la API. Vive desde el arranque para avisar de una
/// alarma aunque la viñeta nunca se haya abierto.
/// </summary>
public sealed partial class CercoViewModel : ObservableObject
{
    private const int MaxEvents = 50;

    /// <summary>Eventos que disparan la alarma sonora en este puesto.</summary>
    private static readonly HashSet<CercoEventKind> AlarmKinds =
        [CercoEventKind.FenceCut, CercoEventKind.Alarm, CercoEventKind.Panic, CercoEventKind.Tamper, CercoEventKind.HvFault];

    private readonly ApiClient _api;
    private bool _loaded;

    /// <summary>Alarma de un panel (caída del cerco, zona, pánico, sabotaje).</summary>
    public event Action<CercoEventDto>? AlarmRaised;

    /// <summary>
    /// La alarma de un panel quedó atendida en el equipo: se silenció la sirena
    /// (<c>disarmed</c> = false) o se desarmó (<c>disarmed</c> = true).
    /// </summary>
    public event Action<int, bool>? AlarmHandled;

    public CercoViewModel(ApiClient api, VmsHubClient hub)
    {
        _api = api;
        hub.CercoPanelStateChanged += dto => Application.Current.Dispatcher.InvokeAsync(() => OnPanelState(dto));
        hub.CercoEventReceived += dto => Application.Current.Dispatcher.InvokeAsync(() => OnEvent(dto));
        // Al volver la conexión pudo haber cambiado todo: se recarga una vez.
        hub.ConnectionStateChanged += ok =>
        {
            if (ok && _loaded) Application.Current.Dispatcher.InvokeAsync(() => _ = ReloadAsync());
        };
        // Cambió el alcance por ubicación de este usuario, o un panel cambió de
        // ubicación: otros paneles a la vista y otra ruta en cada tarjeta.
        hub.ConfigChanged += entity =>
        {
            if (entity is "scope" or "locations" && _loaded) Application.Current.Dispatcher.InvokeAsync(() => _ = ReloadAsync());
        };
        // Cambió lo que esta sesión puede operar (o sus permisos): se re-evalúan los botones.
        OperableScope.Current.Changed += () =>
        {
            foreach (var panel in Panels) panel.RefreshOperable();
        };
        PermissionScope.Current.Changed += () => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var panel in Panels) panel.RefreshOperable();
        });
    }

    public ObservableCollection<CercoPanelItem> Panels { get; } = [];
    public ObservableCollection<CercoEventItem> Events { get; } = [];

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private int _activeAlarmCount;

    public bool HasPanels => Panels.Count > 0;
    public bool HasActiveAlarms => ActiveAlarmCount > 0;
    partial void OnActiveAlarmCountChanged(int value) => OnPropertyChanged(nameof(HasActiveAlarms));

    public async Task InitializeAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            var panels = await _api.GetCercoPanelsAsync();
            foreach (var dto in panels) Upsert(dto);
            foreach (var gone in Panels.Where(p => panels.All(d => d.Id != p.Id)).ToList()) Panels.Remove(gone);
            var events = await _api.GetCercoEventsAsync(MaxEvents);
            Events.Clear();
            foreach (var e in events) Events.Add(new CercoEventItem(e));
            StatusMessage = "";
        }
        catch (ApiException ex)
        {
            StatusMessage = ex.Message;
        }
        Recount();
        OnPropertyChanged(nameof(HasPanels));
    }

    private void Upsert(CercoPanelDto dto)
    {
        var item = Panels.FirstOrDefault(p => p.Id == dto.Id);
        if (item is null) Panels.Add(new CercoPanelItem(dto));
        else item.Apply(dto);
    }

    private void OnPanelState(CercoPanelDto dto)
    {
        Upsert(dto);
        Recount();
        OnPropertyChanged(nameof(HasPanels));
    }

    private void OnEvent(CercoEventDto dto)
    {
        Events.Insert(0, new CercoEventItem(dto));
        while (Events.Count > MaxEvents) Events.RemoveAt(Events.Count - 1);
        if (AlarmKinds.Contains(dto.Kind)) AlarmRaised?.Invoke(dto);
        else if (dto.Kind == CercoEventKind.Disarmed) AlarmHandled?.Invoke(dto.PanelId, true);
        else if (dto.Kind == CercoEventKind.SirenOff) AlarmHandled?.Invoke(dto.PanelId, false);
    }

    private void Recount() => ActiveAlarmCount = Panels.Count(p => p.InAlarm);

    [RelayCommand] private Task Arm(CercoPanelItem? panel) => RunAsync(panel, "arm");
    [RelayCommand] private Task Disarm(CercoPanelItem? panel) => RunAsync(panel, "disarm");
    [RelayCommand] private Task Silence(CercoPanelItem? panel) => RunAsync(panel, "silence");

    /// <summary>
    /// Envía la orden; el estado real llega por el hub (el panel la confirma y se ve en la
    /// tarjeta y en los eventos). Solo se informan los errores: un "orden enviada" fijo
    /// daría a entender que todavía se espera la respuesta del panel.
    /// </summary>
    private async Task RunAsync(CercoPanelItem? panel, string command)
    {
        if (panel is null || panel.IsBusy) return;
        panel.IsBusy = true;
        StatusMessage = "";
        try
        {
            await _api.SendCercoCommandAsync(panel.Id, command);
        }
        catch (ApiException ex)
        {
            StatusMessage = $"{panel.Name}: {ex.Message}";
        }
        finally
        {
            panel.IsBusy = false;
        }
    }
}

/// <summary>Tarjeta de un panel de cerco: textos y habilitaciones derivados del DTO.</summary>
public sealed partial class CercoPanelItem : ObservableObject
{
    public CercoPanelItem(CercoPanelDto dto) => _dto = dto;

    private CercoPanelDto _dto;
    [ObservableProperty] private bool _isBusy;

    public int Id => _dto.Id;
    public string Name => _dto.Name;
    public string Subtitle => string.IsNullOrWhiteSpace(_dto.Location) ? _dto.DeviceId : _dto.Location!;
    public bool Connected => _dto.Connected;
    public bool Armed => _dto.Armed;
    public bool Siren => _dto.Siren;
    public bool FenceDown => !_dto.FenceOk;
    public bool ZoneAlarm => _dto.Zones.Any(z => z.InAlarm);
    public bool PulseMissing => _dto.Armed && !_dto.HvOk;

    /// <summary>Hay algo que atender: sirena sonando, cerco caído o zona en alarma.</summary>
    public bool InAlarm => _dto.Siren || FenceDown || ZoneAlarm;

    /// <summary>Nivel para el color de la píldora: Alarm / Armed / Partial (armando) / Disarmed.</summary>
    public string Level => InAlarm ? "Alarm" : _dto.Arming ? "Partial" : _dto.Armed ? "Armed" : "Disarmed";

    public string StateText => _dto.Arming ? "Armando…" : _dto.Armed ? "Armado" : "Desarmado";
    public string ConnectionText => !_dto.Enabled ? "Pausado" : _dto.Connected ? "En línea" : "Sin conexión";
    public string LevelText => _dto.Voltage is int v ? $"Nivel {v}/21" : "";

    // Señal WiFi del panel. Umbrales para el ESP8266: >= -67 dBm buena, -68..-75
    // regular, < -75 débil (pierde la asociación de vez en cuando).
    public string SignalText => _dto.Connected && _dto.Rssi is int r
        ? $"{SignalBars(r)} Señal {(r >= -67 ? "buena" : r >= -75 ? "regular" : "débil")} · {r} dBm" : "";
    /// <summary>Color de la píldora (reusa los estilos de LevelBadge): Armed verde, Triggered ámbar, Alarm rojo.</summary>
    public string SignalLevel => _dto.Rssi is int s ? (s >= -67 ? "Armed" : s >= -75 ? "Triggered" : "Alarm") : "Normal";
    private static string SignalBars(int r) => r >= -67 ? "▂▄▆█" : r >= -75 ? "▂▄▆" : r >= -82 ? "▂▄" : "▂";
    public string ZoneText => string.Join(" · ", _dto.Zones.Where(z => z.InAlarm).Select(z => $"Zona {z.Number} en alarma"));

    /// <summary>Sus roles incluyen "Operar cercos eléctricos" (armar, desarmar y silenciar).</summary>
    private static bool MayOperate => PermissionScope.Current.Has(Core.Domain.Permissions.CercoOperate);

    /// <summary>Sus roles lo permiten y el panel está en su alcance (si no, se ve pero no se opera).</summary>
    public bool CanOperate => MayOperate && OperableScope.Current.CanOperateFence(_dto.Id);
    public bool IsReadOnly => !CanOperate;

    /// <summary>Por qué no se puede operar el panel.</summary>
    public string ReadOnlyHint => MayOperate ? OperableScope.DeniedHint
        : "Sus roles no incluyen \"Operar cercos eléctricos\": puede ver el estado, pero no armar, desarmar ni silenciar.";

    public bool CanArm => CanOperate && _dto.Connected && !_dto.Armed && !_dto.Arming && !IsBusy;
    public bool CanDisarm => CanOperate && _dto.Connected && (_dto.Armed || _dto.Arming) && !IsBusy;
    public bool CanSilence => CanOperate && _dto.Connected && _dto.Siren && !IsBusy;

    /// <summary>Cambió lo que esta sesión puede operar.</summary>
    public void RefreshOperable() => OnPropertyChanged(string.Empty);

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanArm));
        OnPropertyChanged(nameof(CanDisarm));
        OnPropertyChanged(nameof(CanSilence));
    }

    public void Apply(CercoPanelDto dto)
    {
        _dto = dto;
        OnPropertyChanged(string.Empty);   // todo lo derivado cambia a la vez
    }
}

/// <summary>Fila de la lista de eventos recientes.</summary>
public sealed class CercoEventItem(CercoEventDto dto)
{
    public string Time => dto.ReceivedAt.ToLocalTime().ToString("dd-MM HH:mm:ss");
    public string PanelName => dto.PanelName;
    public string Description => dto.Description;
    public string ZoneText => dto.ZoneNumber is int z && dto.Kind is CercoEventKind.Alarm or CercoEventKind.ZoneRestore ? $"Zona {z}" : "";
    /// <summary>Color: Alarm (crítico), Triggered (advertencia) o Normal.</summary>
    public string Level => dto.Severity switch
    {
        CercoSeverity.Critical => "Alarm",
        CercoSeverity.Warning => "Triggered",
        _ => "Normal",
    };
}
