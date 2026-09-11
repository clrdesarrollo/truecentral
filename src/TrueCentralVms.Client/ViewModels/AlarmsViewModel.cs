using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Módulo Paneles de alarma: lista de centrales de intrusión con el estado de
/// sus áreas y zonas, órdenes de armado/desarmado/anulación y el flujo de
/// eventos en vivo. El estado no se sondea desde el cliente: el servidor lo
/// mantiene (sondeo + canal de eventos del panel) y lo empuja por el hub; las
/// órdenes van por la API y la respuesta ya trae el estado resultante.
/// </summary>
public sealed partial class AlarmsViewModel : ObservableObject
{
    private const int MaxEvents = 500;

    private readonly ApiClient _api;

    /// <summary>Alarma crítica recibida (para avisar aunque el módulo esté cerrado).</summary>
    public event Action<AlarmEventDto>? AlarmRaised;

    public AlarmsViewModel(ApiClient api, VmsHubClient hub)
    {
        _api = api;
        hub.AlarmPanelStateChanged += dto => Application.Current.Dispatcher.InvokeAsync(() => OnPanelState(dto));
        hub.AlarmEventReceived += dto => Application.Current.Dispatcher.InvokeAsync(() => OnEvent(dto));
        hub.ConfigChanged += entity =>
        {
            if (entity == "alarm-panels")
                Application.Current.Dispatcher.InvokeAsync(() => _ = LoadPanelsAsync());
        };

        // Cuenta regresiva del retardo de salida ("Armando…"): actualiza cada
        // segundo el texto de las áreas que están armando para que no parezca
        // que la interfaz no hace nada.
        _armTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _armTimer.Tick += (_, _) => TickArmCountdown();
        _armTimer.Start();
    }

    private readonly System.Windows.Threading.DispatcherTimer _armTimer;

    /// <summary>Refresca el conteo de salida de las áreas del panel seleccionado.</summary>
    private void TickArmCountdown()
    {
        var panel = SelectedPanel;
        if (panel is null) return;
        foreach (var area in panel.Areas)
        {
            if (area.IsArming)
            {
                int total = area.Dto.ExitDelaySeconds > 0 ? area.Dto.ExitDelaySeconds : 30;
                var since = panel.ArmingSince.TryGetValue(area.Number, out var t) ? t : DateTime.UtcNow;
                int remaining = total - (int)(DateTime.UtcNow - since).TotalSeconds;
                area.RemainingSeconds = remaining > 0 ? remaining : null;
            }
            else if (area.RemainingSeconds is not null)
            {
                area.RemainingSeconds = null;
            }
        }
    }

    // ------------------------------------------------------------------
    // Estado
    // ------------------------------------------------------------------

    public ObservableCollection<AlarmPanelItem> Panels { get; } = [];
    public ObservableCollection<AlarmEventItem> Events { get; } = [];

    [ObservableProperty] private AlarmPanelItem? _selectedPanel;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";
    /// <summary>Mostrar solo los eventos del panel seleccionado.</summary>
    [ObservableProperty] private bool _onlySelectedPanel;
    /// <summary>Filtro por naturaleza del evento ("" = todos).</summary>
    [ObservableProperty] private string _kindFilter = "";
    /// <summary>Paneles con alguna alarma activa ahora (enciende el icono del riel).</summary>
    [ObservableProperty] private int _activeAlarmCount;
    private bool _loaded;

    public bool HasPanels => Panels.Count > 0;
    public bool HasActiveAlarms => ActiveAlarmCount > 0;
    public bool IsAdmin => _api.Role == "Admin";

    /// <summary>Opciones del filtro de eventos (etiqueta → clave).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> KindOptions { get; } =
    [
        new("Todos los eventos", ""),
        new("Alarmas", nameof(AlarmEventKind.Alarm)),
        new("Armados", nameof(AlarmEventKind.Arm)),
        new("Desarmados", nameof(AlarmEventKind.Disarm)),
        new("Anulaciones", nameof(AlarmEventKind.Bypass)),
        new("Sensores", nameof(AlarmEventKind.ZoneTriggered)),
        new("Fallas", nameof(AlarmEventKind.Trouble)),
        new("Restauraciones", nameof(AlarmEventKind.Restore)),
        new("Sistema", nameof(AlarmEventKind.System)),
    ];

    partial void OnActiveAlarmCountChanged(int value) => OnPropertyChanged(nameof(HasActiveAlarms));
    partial void OnOnlySelectedPanelChanged(bool value) => _ = LoadEventsAsync();
    partial void OnKindFilterChanged(string value) => _ = LoadEventsAsync();

    partial void OnSelectedPanelChanged(AlarmPanelItem? value)
    {
        if (OnlySelectedPanel) _ = LoadEventsAsync();
    }

    // ------------------------------------------------------------------
    // Carga
    // ------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await LoadPanelsAsync();
        await LoadEventsAsync();
    }

    public async Task LoadPanelsAsync()
    {
        try
        {
            var panels = await _api.GetAlarmPanelsAsync();
            int? keep = SelectedPanel?.Id;
            // Actualizar en el lugar para no perder la selección ni parpadear.
            foreach (var dto in panels)
            {
                var item = Panels.FirstOrDefault(p => p.Id == dto.Id);
                if (item is null) Panels.Add(new AlarmPanelItem(dto));
                else item.Apply(dto);
            }
            foreach (var stale in Panels.Where(p => panels.All(d => d.Id != p.Id)).ToList())
                Panels.Remove(stale);
            SelectedPanel = Panels.FirstOrDefault(p => p.Id == keep) ?? Panels.FirstOrDefault();
            RecountAlarms();
            OnPropertyChanged(nameof(HasPanels));
            StatusMessage = Panels.Count == 0
                ? (IsAdmin
                    ? "No hay paneles de alarma registrados: agréguelos desde el panel web (Dispositivos → Paneles de alarma)."
                    : "No hay paneles de alarma registrados. Pida a un administrador que los agregue desde el panel web.")
                : "";
        }
        catch (ApiException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task LoadEventsAsync()
    {
        try
        {
            var events = await _api.GetAlarmEventsAsync(
                OnlySelectedPanel ? SelectedPanel?.Id : null,
                string.IsNullOrEmpty(KindFilter) ? null : KindFilter,
                take: MaxEvents);
            Events.Clear();
            foreach (var dto in events)
                Events.Add(new AlarmEventItem(dto));
            OnPropertyChanged(nameof(HasEvents));
        }
        catch (ApiException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    public bool HasEvents => Events.Count > 0;

    // ------------------------------------------------------------------
    // Llegada en vivo
    // ------------------------------------------------------------------

    private void OnPanelState(AlarmPanelDto dto)
    {
        var item = Panels.FirstOrDefault(p => p.Id == dto.Id);
        if (item is null)
        {
            Panels.Add(new AlarmPanelItem(dto));
            OnPropertyChanged(nameof(HasPanels));
            SelectedPanel ??= Panels[0];
        }
        else
        {
            item.Apply(dto);
        }
        RecountAlarms();
    }

    private void OnEvent(AlarmEventDto dto)
    {
        bool matches = (!OnlySelectedPanel || SelectedPanel?.Id == dto.PanelId)
                       && (string.IsNullOrEmpty(KindFilter) || dto.Kind.ToString() == KindFilter);
        if (matches)
        {
            Events.Insert(0, new AlarmEventItem(dto));
            while (Events.Count > MaxEvents)
                Events.RemoveAt(Events.Count - 1);
            OnPropertyChanged(nameof(HasEvents));
        }
        if (dto.Severity == AlarmSeverity.Critical)
            AlarmRaised?.Invoke(dto);
    }

    private void RecountAlarms() => ActiveAlarmCount = Panels.Count(p => p.InAlarm);

    // ------------------------------------------------------------------
    // Órdenes
    // ------------------------------------------------------------------

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(SelectedPanel, p => _api.RefreshAlarmPanelAsync(p.Id), "Estado actualizado.");

    /// <summary>Arma un área (número 0 = todas) en el modo indicado.</summary>
    public Task ArmAsync(AlarmAreaItem? area, AlarmArmMode mode) =>
        RunAsync(SelectedPanel, p => _api.ArmAlarmAreaAsync(p.Id, area?.Number ?? 0, mode),
            mode == AlarmArmMode.Stay ? "Armado parcial enviado." : "Armado enviado.");

    public Task DisarmAsync(AlarmAreaItem? area) =>
        RunAsync(SelectedPanel, p => _api.DisarmAlarmAreaAsync(p.Id, area?.Number ?? 0), "Desarmado enviado.");

    public Task ClearAlarmAsync(AlarmAreaItem? area) =>
        RunAsync(SelectedPanel, p => _api.ClearAlarmAsync(p.Id, area?.Number ?? 0), "Alarma silenciada.");

    public Task ToggleBypassAsync(AlarmZoneItem zone) =>
        RunAsync(SelectedPanel, p => _api.SetAlarmZoneBypassAsync(p.Id, zone.Number, !zone.Bypassed),
            zone.Bypassed ? "Zona restituida." : "Zona anulada.");

    [RelayCommand] private Task ArmAreaAway(AlarmAreaItem area) => ArmAsync(area, AlarmArmMode.Away);
    [RelayCommand] private Task ArmAreaStay(AlarmAreaItem area) => ArmAsync(area, AlarmArmMode.Stay);
    [RelayCommand] private Task DisarmArea(AlarmAreaItem area) => DisarmAsync(area);
    [RelayCommand] private Task ClearAreaAlarm(AlarmAreaItem area) => ClearAlarmAsync(area);
    [RelayCommand] private Task ToggleZoneBypass(AlarmZoneItem zone) => ToggleBypassAsync(zone);
    [RelayCommand] private Task ArmAllAway() => ArmAsync(null, AlarmArmMode.Away);
    [RelayCommand] private Task ArmAllStay() => ArmAsync(null, AlarmArmMode.Stay);
    [RelayCommand] private Task DisarmAll() => DisarmAsync(null);
    [RelayCommand] private Task ClearAllAlarms() => ClearAlarmAsync(null);

    private async Task RunAsync(AlarmPanelItem? panel, Func<AlarmPanelItem, Task<AlarmPanelDto>> command, string okMessage)
    {
        if (panel is null || IsBusy) return;
        IsBusy = true;
        panel.IsBusy = true;
        try
        {
            var dto = await command(panel);
            panel.Apply(dto);
            RecountAlarms();
            StatusMessage = okMessage;
        }
        catch (ApiException ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            panel.IsBusy = false;
            IsBusy = false;
        }
    }
}

/// <summary>Panel en la lista, con sus áreas y zonas actualizables en el lugar.</summary>
public sealed partial class AlarmPanelItem : ObservableObject
{
    public AlarmPanelItem(AlarmPanelDto dto)
    {
        Dto = dto;
        Rebuild();
    }

    public AlarmPanelDto Dto { get; private set; }
    public int Id => Dto.Id;

    public ObservableCollection<AlarmAreaItem> Areas { get; } = [];
    public ObservableCollection<AlarmZoneItem> Zones { get; } = [];

    [ObservableProperty] private bool _isBusy;

    public string Name => Dto.Name;
    /// <summary>Dirección de red tal cual, sin decir de quién es.</summary>
    private string Address => Dto.UseHttps ? $"https://{Dto.Host}:{Dto.Port}" : $"{Dto.Host}:{Dto.Port}";

    /// <summary>El panel llega a través de una receptora (pasarela) y no directo.</summary>
    public bool ViaReceiver => !string.IsNullOrEmpty(Dto.DeviceId);

    /// <summary>
    /// Dirección que se muestra. Con una receptora de por medio, ESA dirección
    /// es la de la receptora, no la del panel: el panel reporta hacia ella y
    /// el sistema nunca ve su IP. Se dice con todas las letras para que nadie
    /// la confunda con la del equipo.
    /// </summary>
    public string Host => ViaReceiver ? $"Receptora {Address}  ·  panel {Dto.DeviceId}" : Address;

    public string HostTooltip => ViaReceiver
        ? $"El panel «{Dto.DeviceId}» reporta a la receptora {Address}. Esa es la dirección de la receptora: " +
          "la del panel no la conoce el sistema, porque es el panel el que llama."
        : $"Dirección del panel: {Address}";
    public string Model => Dto.Model ?? "—";
    public string Detail => string.Join("  ·  ", new[] { Dto.Model, Dto.SerialNumber, Dto.FirmwareVersion }.Where(s => !string.IsNullOrWhiteSpace(s))!);
    public bool IsOnline => Dto.Status == AlarmPanelStatus.Online;
    public bool IsLive => Dto.Live;
    public bool InAlarm => Dto.InAlarm;
    public bool IsEnabled => Dto.Enabled;
    public string? LastError => Dto.LastError;
    public bool HasMultipleAreas => Dto.Areas.Count > 1;

    public string StatusText => Dto.Status switch
    {
        AlarmPanelStatus.Online => Dto.Live ? "En línea · recibiendo eventos" : "En línea",
        AlarmPanelStatus.Offline => "Sin conexión",
        AlarmPanelStatus.AuthFailed => "Credenciales rechazadas",
        _ => Dto.Enabled ? "Conectando…" : "Monitoreo desactivado",
    };

    public string LastStateText => Dto.LastStateAt is { } at
        ? $"Estado leído a las {at.ToLocalTime():HH:mm:ss}"
        : "Estado aún no leído";

    /// <summary>Resumen del armado para la lista: "Armado", "Desarmado", "Parcial 1/3".</summary>
    public string ArmSummary
    {
        get
        {
            if (Dto.Areas.Count == 0) return "Sin áreas";
            int armed = Dto.Areas.Count(a => a.ArmState is AlarmArmState.Away or AlarmArmState.Stay or AlarmArmState.Vacation);
            if (armed == Dto.Areas.Count) return "Armado";
            if (Dto.Areas.Any(a => a.ArmState == AlarmArmState.Arming)) return armed == 0 ? "Armando…" : $"Armando… ({armed}/{Dto.Areas.Count})";
            if (armed == 0) return "Desarmado";
            return $"Parcial ({armed}/{Dto.Areas.Count})";
        }
    }

    /// <summary>"Armed" | "Disarmed" | "Partial" | "Alarm": colorea el distintivo de la lista.</summary>
    public string ArmLevel
    {
        get
        {
            if (InAlarm) return "Alarm";
            if (Dto.Areas.Count == 0) return "Disarmed";
            int armed = Dto.Areas.Count(a => a.ArmState is AlarmArmState.Away or AlarmArmState.Stay or AlarmArmState.Vacation);
            if (armed == Dto.Areas.Count) return "Armed";
            if (armed > 0 || Dto.Areas.Any(a => a.ArmState == AlarmArmState.Arming)) return "Partial";
            return "Disarmed";
        }
    }

    public string Counts => $"{Dto.Areas.Count} área(s) · {Dto.Zones.Count} zona(s)";

    public bool PanelTamper => Dto.PanelTamper;
    public bool AcLoss => Dto.AcLoss;
    /// <summary>Avisos de chasis (tapa/corriente) para el detalle del panel; vacío si no hay.</summary>
    public string WarningText
    {
        get
        {
            var w = new List<string>();
            if (Dto.PanelTamper) w.Add("Tapa del panel abierta (sabotaje): el equipo no permitirá armar hasta cerrarla.");
            if (Dto.AcLoss) w.Add("Falla de corriente de red: el panel está funcionando en batería.");
            return string.Join("\n", w);
        }
    }
    public bool HasWarning => Dto.PanelTamper || Dto.AcLoss;

    public void Apply(AlarmPanelDto dto)
    {
        Dto = dto;
        Rebuild();
        OnPropertyChanged(string.Empty); // todas las derivadas
    }

    /// <summary>Momento en que cada área entró en "Armando…" (para la cuenta regresiva).</summary>
    public Dictionary<int, DateTime> ArmingSince { get; } = new();

    /// <summary>Alguna área está en conteo de salida ("Armando…").</summary>
    public bool HasArming => Dto.Areas.Any(a => a.ArmState == AlarmArmState.Arming);

    /// <summary>Base para armar: en línea, sin tapa abierta y sin un armado en curso.</summary>
    private bool CanArmBase => IsOnline && !Dto.PanelTamper && !HasArming;
    /// <summary>Armar todo (total): solo si alguna área habilitada no está ya en total.</summary>
    public bool CanArmAllAway => CanArmBase && Dto.Areas.Any(a => a.Enabled && a.ArmState != AlarmArmState.Away);
    /// <summary>Armar todo (parcial): solo si alguna área habilitada no está ya en parcial.</summary>
    public bool CanArmAllStay => CanArmBase && Dto.Areas.Any(a => a.Enabled && a.ArmState != AlarmArmState.Stay);
    /// <summary>Desarmar todo: solo si alguna área está armada o en conteo.</summary>
    public bool CanDisarmAll => IsOnline && Dto.Areas.Any(a =>
        a.ArmState is AlarmArmState.Away or AlarmArmState.Stay or AlarmArmState.Vacation or AlarmArmState.Arming);

    private void Rebuild()
    {
        // Mantener las marcas de inicio de armado a través de las recreaciones.
        foreach (var a in Dto.Areas)
        {
            if (a.ArmState == AlarmArmState.Arming)
                ArmingSince.TryAdd(a.Number, DateTime.UtcNow);
            else
                ArmingSince.Remove(a.Number);
        }

        Areas.Clear();
        foreach (var a in Dto.Areas)
            Areas.Add(new AlarmAreaItem(a, Dto.PanelTamper));
        Zones.Clear();
        foreach (var z in Dto.Zones)
            Zones.Add(new AlarmZoneItem(z, Dto.Areas.FirstOrDefault(a => a.Number == z.AreaNumber)?.Name));
    }
}

public sealed partial class AlarmAreaItem : ObservableObject
{
    private readonly bool _panelTamper;
    public AlarmAreaItem(AlarmAreaDto dto, bool panelTamper = false)
    {
        Dto = dto;
        _panelTamper = panelTamper;
    }

    public AlarmAreaDto Dto { get; }
    public int Number => Dto.Number;
    public string Name => Dto.Name;
    public bool InAlarm => Dto.InAlarm;
    public bool IsArmed => Dto.ArmState is AlarmArmState.Away or AlarmArmState.Stay or AlarmArmState.Vacation;
    public bool IsArming => Dto.ArmState == AlarmArmState.Arming;
    public bool IsEnabled => Dto.Enabled;
    /// <summary>Base para armar el área: habilitada, sin conteo en curso y sin tapa abierta.</summary>
    private bool CanArmBase => Dto.Enabled && !IsArming && !_panelTamper;
    /// <summary>Armar total: solo si no está ya en total.</summary>
    public bool CanArmAway => CanArmBase && Dto.ArmState != AlarmArmState.Away;
    /// <summary>Armar parcial: solo si no está ya en parcial.</summary>
    public bool CanArmStay => CanArmBase && Dto.ArmState != AlarmArmState.Stay;
    /// <summary>Desarmar / cancelar: solo si el área está armada o en conteo.</summary>
    public bool CanDisarm => IsArmed || IsArming;
    public string ZoneCountText => $"{Dto.ZoneCount} zona(s)";

    /// <summary>Segundos restantes del retardo de salida (null = sin conteo o ya venció).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArmLabel))]
    private int? _remainingSeconds;

    public string ArmLabel => Dto.ArmState switch
    {
        AlarmArmState.Disarmed => "Desarmada",
        AlarmArmState.Away => "Armada · total",
        AlarmArmState.Stay => "Armada · parcial",
        AlarmArmState.Vacation => "Armada · vacaciones",
        AlarmArmState.Arming => RemainingSeconds is int r and > 0 ? $"Armando… ({r} s)" : "Armando… (conteo de salida)",
        _ => "Estado desconocido",
    };

    /// <summary>"Armed" | "Disarmed" | "Alarm" | "Unknown" para los estilos.</summary>
    public string Level => InAlarm ? "Alarm" : IsArmed ? "Armed"
        : Dto.ArmState == AlarmArmState.Arming ? "Partial"
        : Dto.ArmState == AlarmArmState.Disarmed ? "Disarmed" : "Unknown";

    /// <summary>Glifo Segoe MDL2: candado cerrado/abierto o alerta.</summary>
    public string Glyph => InAlarm ? "\uE814" : (IsArmed || Dto.ArmState == AlarmArmState.Arming) ? "\uE72E" : "\uE785";
}

public sealed class AlarmZoneItem(AlarmZoneDto dto, string? areaName)
{
    public AlarmZoneDto Dto { get; } = dto;
    public int Number => Dto.Number;
    public string Name => Dto.Name;
    public bool Bypassed => Dto.Bypassed;
    public bool InAlarm => Dto.InAlarm;
    public bool IsArmed => Dto.Armed;
    public string AreaText => areaName ?? (Dto.AreaNumber is { } n ? $"Área {n}" : "Sin área");

    public string StatusLabel => InAlarm ? "¡ALARMA!" : Dto.Status switch
    {
        AlarmZoneStatus.Normal => "Normal",
        AlarmZoneStatus.Triggered => "Activada",
        AlarmZoneStatus.Fault => "Falla",
        AlarmZoneStatus.Offline => "Sin comunicación",
        AlarmZoneStatus.NotConfigured => "Sin área",
        _ => "—",
    };

    /// <summary>"Alarm" | "Triggered" | "Fault" | "Bypassed" | "Normal" | "Unknown".</summary>
    public string Level => InAlarm ? "Alarm"
        : Bypassed ? "Bypassed"
        : Dto.Status switch
        {
            AlarmZoneStatus.Triggered => "Triggered",
            AlarmZoneStatus.Fault or AlarmZoneStatus.Offline => "Fault",
            AlarmZoneStatus.Normal => "Normal",
            _ => "Unknown",
        };

    public string Flags => string.Join(" · ", new[]
    {
        Bypassed ? "anulada" : null,
        Dto.Tamper ? "tamper" : null,
        Dto.LowBattery ? "batería baja" : null,
        IsArmed ? "armada" : null,
    }.Where(s => s is not null)!);

    public string Detail => string.Join(" · ", new[]
    {
        Dto.DetectorType, Dto.ZoneType, Dto.Model,
        Dto.Signal is { } s ? $"señal {s}" : null,
    }.Where(s => !string.IsNullOrWhiteSpace(s))!);

    public string BypassLabel => Bypassed ? "Restituir" : "Anular";
    public string Tooltip => $"Zona {Number} · {AreaText}" + (Detail.Length > 0 ? $"\n{Detail}" : "");
}

/// <summary>Fila del flujo de eventos.</summary>
public sealed class AlarmEventItem(AlarmEventDto dto)
{
    public AlarmEventDto Dto { get; } = dto;
    public string TimeText => Dto.Timestamp.ToLocalTime().ToString("dd-MM-yyyy HH:mm:ss");
    public string Description => Dto.Description;
    public AlarmSeverity Severity => Dto.Severity;
    public AlarmEventKind Kind => Dto.Kind;

    public string Where => string.Join("  ·  ", new[]
    {
        Dto.PanelName,
        Dto.AreaName,
        Dto.ZoneName,
    }.Where(s => !string.IsNullOrWhiteSpace(s))!);

    public string Origin => Dto.Source switch
    {
        "vms" => $"Operador: {Dto.Operator}",
        "panel" => Dto.Operator is { Length: > 0 } ? $"Usuario del panel: {Dto.Operator}" : "Informado por el panel",
        _ => "Detectado por sondeo",
    } + (Dto.Code is null ? "" : $"  ·  código {Dto.Code}");

    public string Glyph => Dto.Kind switch
    {
        AlarmEventKind.Alarm => "\uE814",
        AlarmEventKind.Arm => "\uE72E",
        AlarmEventKind.Disarm => "\uE785",
        AlarmEventKind.Bypass => "\uE894",
        AlarmEventKind.Trouble => "\uE7BA",
        AlarmEventKind.ZoneTriggered => "\uE7B3",
        AlarmEventKind.Restore => "\uE8FB",
        _ => "\uE946",
    };
}
