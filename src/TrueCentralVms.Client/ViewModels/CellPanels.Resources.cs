using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;

namespace TrueCentralVms.Client.ViewModels;

// ---------------------------------------------------------------------------
// Cuadros de recursos que no son cámara. Reutilizan lo que el cliente ya tiene
// vivo (los paneles de Alarmas, Cerco, Citofonía y Parlantes se actualizan
// solos por el hub) o, para puertas y módulos de E/S —que el cliente no tenía—,
// leen la API y escuchan sus eventos del hub. Los permisos y el alcance son los
// mismos de cada módulo: lo que no se puede operar se ve deshabilitado.
// ---------------------------------------------------------------------------

/// <summary>Los eventos del hub llegan en otro hilo: los cambios van a la interfaz.</summary>
internal static class Ui
{
    public static void Run(Action action) => Application.Current?.Dispatcher.InvokeAsync(action);
}

/// <summary>Puerta de control de acceso: estado, órdenes y últimos pasos.</summary>
public sealed partial class DoorCellPanel : CellPanel
{
    private readonly ApiClient _api;
    private readonly VmsHubClient _hub;
    private readonly int _doorId;
    private AccessDoorStateDto? _door;

    public DoorCellPanel(ApiClient api, VmsHubClient hub, ResourceNode node)
    {
        _api = api;
        _hub = hub;
        _doorId = node.Id;
        Title = node.Name;
        _hub.AccessDoorStateChanged += OnDoorChanged;
        _hub.AccessEventReceived += OnAccessEvent;
    }

    public override string Glyph => ResourceNode.GlyphOf(ResourceKind.Door);
    public override string Kind => "door";
    public override int ResourceId => _doorId;

    /// <summary>Últimos pasos por esta puerta (el más nuevo arriba).</summary>
    public ObservableCollection<AccessEventLine> Recent { get; } = [];

    [ObservableProperty] private string _stateText = "—";
    [ObservableProperty] private string _modeText = "";
    /// <summary>Nivel para el color: Ok, Warning, Alarm, Offline.</summary>
    [ObservableProperty] private string _level = "Unknown";
    [ObservableProperty] private string _deviceText = "";
    [ObservableProperty] private bool _isBusy;

    public bool MayOperate => PermissionScope.Current.Has(Permissions.AccessDoors) && OperableScope.Current.CanOperateDoor(_doorId);
    public bool CanCommand => MayOperate && _door is { SupportsRemoteControl: true, Enabled: true } && !IsBusy &&
                              _door.DeviceStatus == AccessDeviceStatus.Online;
    public string? ReadOnlyHint => MayOperate ? null
        : !PermissionScope.Current.Has(Permissions.AccessDoors) ? "Sus roles no incluyen operar puertas." : OperableScope.DeniedHint;
    public bool ShowsRecent => PermissionScope.Current.Has(Permissions.AccessMonitor);

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanCommand));

    public override async Task LoadAsync()
    {
        var doors = await _api.GetAccessDoorsAsync();
        if (doors.FirstOrDefault(d => d.Id == _doorId) is not { } door)
        {
            Status = "La puerta ya no existe o está fuera de su alcance.";
            return;
        }
        Apply(door);
        if (!ShowsRecent) return;
        try
        {
            var page = await _api.GetAccessEventsAsync(200);
            foreach (var e in page.Items.Where(Mine).Take(6)) Recent.Add(new AccessEventLine(e));
        }
        catch (ApiException) { /* sin historial: la puerta igual se opera */ }
    }

    private bool Mine(AccessEventDto e) => _door is { } d && e.DeviceId == d.DeviceId && e.DoorNumber == d.Number;

    private void Apply(AccessDoorStateDto door)
    {
        _door = door;
        Title = door.Name;
        DeviceText = string.IsNullOrWhiteSpace(door.Location) ? door.DeviceName : $"{door.DeviceName} · {door.Location}";
        bool offline = door.DeviceStatus is AccessDeviceStatus.Offline or AccessDeviceStatus.AuthFailed;
        (StateText, Level) =
            !door.Enabled ? ("Deshabilitada", "Offline")
            : offline ? ("Equipo sin conexión", "Offline")
            : door.Open == true ? ("Abierta", "Warning")
            : door.Open == false ? ("Cerrada", "Ok")
            : ("Estado desconocido", "Unknown");
        ModeText = door.Mode switch
        {
            "RemainOpen" => "Siempre abierta",
            "RemainLocked" => "Siempre cerrada",
            "Normal" => "Modo normal",
            _ => "",
        };
        if (door.Mode is "RemainOpen" or "RemainLocked" && Level == "Ok") Level = "Warning";
        Status = door.SupportsRemoteControl ? "" : "Sin control remoto";
        OnPropertyChanged(nameof(CanCommand));
    }

    private void OnDoorChanged(AccessDoorStateDto dto)
    {
        if (dto.Id == _doorId) Ui.Run(() => Apply(dto));
    }

    private void OnAccessEvent(AccessEventDto e)
    {
        Ui.Run(() =>
        {
            if (!ShowsRecent || !Mine(e)) return;
            Recent.Insert(0, new AccessEventLine(e));
            while (Recent.Count > 6) Recent.RemoveAt(Recent.Count - 1);
        });
    }

    [RelayCommand] private Task Open() => SendAsync("Open", "Puerta abierta.");
    [RelayCommand] private Task Close() => SendAsync("Close", "Puerta cerrada.");
    [RelayCommand] private Task RemainOpen() => SendAsync("RemainOpen", "Queda siempre abierta.");
    [RelayCommand] private Task RemainLocked() => SendAsync("RemainLocked", "Queda siempre cerrada.");

    private async Task SendAsync(string command, string ok)
    {
        if (!CanCommand) return;
        IsBusy = true;
        try
        {
            Apply(await _api.SendAccessDoorCommandAsync(_doorId, command));
            Status = ok;
        }
        catch (ApiException ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    public override void Dispose()
    {
        _hub.AccessDoorStateChanged -= OnDoorChanged;
        _hub.AccessEventReceived -= OnAccessEvent;
    }
}

/// <summary>Un paso por una puerta, para la lista del cuadro.</summary>
public sealed class AccessEventLine(AccessEventDto e)
{
    public string Time => e.Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string Who => !string.IsNullOrWhiteSpace(e.PersonName) ? e.PersonName
        : !string.IsNullOrWhiteSpace(e.CardNumber) ? $"Tarjeta {e.CardNumber}" : e.Description ?? "";
    public string What => e.Kind switch
    {
        AccessEventKind.Granted => "Autorizado",
        AccessEventKind.Denied => "Rechazado",
        AccessEventKind.DoorOpen => "Puerta abierta",
        AccessEventKind.DoorClose => "Puerta cerrada",
        AccessEventKind.Alarm => "Alarma",
        _ => "Evento",
    };
    public string Level => e.Kind switch
    {
        AccessEventKind.Granted => "Ok",
        AccessEventKind.Denied or AccessEventKind.Alarm => "Alarm",
        _ => "Unknown",
    };
}

/// <summary>Panel de alarma: sus áreas con armar/desarmar y las zonas con novedad.</summary>
public sealed partial class AlarmCellPanel : CellPanel
{
    private readonly ApiClient _api;
    private readonly AlarmsViewModel _alarms;
    private readonly int _panelId;

    public AlarmCellPanel(ApiClient api, AlarmsViewModel alarms, int panelId, string name)
    {
        _api = api;
        _alarms = alarms;
        _panelId = panelId;
        Title = name;
        _alarms.Panels.CollectionChanged += OnPanelsChanged;
        Resolve();
    }

    public override string Glyph => ResourceNode.GlyphOf(ResourceKind.Partition);
    public override string Kind => "alarm-panel";
    public override int ResourceId => _panelId;

    /// <summary>El panel vivo del módulo Alarmas (se actualiza solo por el hub).</summary>
    [ObservableProperty] private AlarmPanelItem? _item;
    [ObservableProperty] private bool _isBusy;

    private void OnPanelsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Resolve();

    private void Resolve()
    {
        Item = _alarms.Panels.FirstOrDefault(p => p.Id == _panelId);
        if (Item is not null) Title = Item.Name;
        Status = Item is null ? "Cargando el panel…" : "";
    }

    [RelayCommand] private Task ArmAway(AlarmAreaItem area) => RunAsync(() => _api.ArmAlarmAreaAsync(_panelId, area.Number, AlarmArmMode.Away), "Armado enviado.");
    [RelayCommand] private Task ArmStay(AlarmAreaItem area) => RunAsync(() => _api.ArmAlarmAreaAsync(_panelId, area.Number, AlarmArmMode.Stay), "Armado parcial enviado.");
    [RelayCommand] private Task Disarm(AlarmAreaItem area) => RunAsync(() => _api.DisarmAlarmAreaAsync(_panelId, area.Number), "Desarmado enviado.");
    [RelayCommand] private Task ClearAlarm(AlarmAreaItem area) => RunAsync(() => _api.ClearAlarmAsync(_panelId, area.Number), "Alarma silenciada.");

    private async Task RunAsync(Func<Task<AlarmPanelDto>> command, string ok)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var dto = await command();
            Item?.Apply(dto);
            Status = ok;
        }
        catch (ApiException ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    public override void Dispose() => _alarms.Panels.CollectionChanged -= OnPanelsChanged;
}

/// <summary>Panel de cerco eléctrico: estado y armar, desarmar y silenciar (los del módulo Cerco).</summary>
public sealed partial class CercoCellPanel : CellPanel
{
    private readonly int _panelId;

    public CercoCellPanel(CercoViewModel cerco, int panelId, string name)
    {
        Cerco = cerco;
        _panelId = panelId;
        Title = name;
        cerco.Panels.CollectionChanged += OnPanelsChanged;
        Resolve();
    }

    /// <summary>El módulo Cerco: sus comandos (Arm, Disarm, Silence) reciben el panel.</summary>
    public CercoViewModel Cerco { get; }

    public override string Glyph => ResourceNode.GlyphOf(ResourceKind.Fence);
    public override string Kind => "fence";
    public override int ResourceId => _panelId;

    [ObservableProperty] private CercoPanelItem? _item;

    private void OnPanelsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Resolve();

    private void Resolve()
    {
        Item = Cerco.Panels.FirstOrDefault(p => p.Id == _panelId);
        if (Item is not null) Title = Item.Name;
        Status = Item is null ? "Cargando el cerco…" : "";
    }

    public override void Dispose() => Cerco.Panels.CollectionChanged -= OnPanelsChanged;
}

/// <summary>Citófono: estado, llamada en curso, contestar (abre su ventana) y abrir la puerta.</summary>
public sealed partial class IntercomCellPanel : CellPanel
{
    private readonly ApiClient _api;
    private readonly IntercomsViewModel _intercoms;
    private readonly Action<int> _openWindow;
    private readonly int _id;

    public IntercomCellPanel(ApiClient api, IntercomsViewModel intercoms, Action<int> openWindow, int id, string name)
    {
        _api = api;
        _intercoms = intercoms;
        _openWindow = openWindow;
        _id = id;
        Title = name;
        intercoms.Intercoms.CollectionChanged += OnListChanged;
        Resolve();
    }

    public override string Glyph => ResourceNode.GlyphOf(ResourceKind.Intercom);
    public override string Kind => "intercom";
    public override int ResourceId => _id;

    [ObservableProperty] private IntercomItemViewModel? _item;
    [ObservableProperty] private bool _isBusy;

    private void OnListChanged(object? sender, NotifyCollectionChangedEventArgs e) => Resolve();

    private void Resolve()
    {
        Item = _intercoms.Intercoms.FirstOrDefault(i => i.Id == _id);
        if (Item is not null) Title = Item.Name;
        Status = Item is null ? "Cargando el citófono…" : "";
    }

    [RelayCommand] private void Answer() => _openWindow(_id);

    [RelayCommand]
    private async Task OpenDoor()
    {
        if (Item is not { CanOperate: true } || IsBusy) return;
        IsBusy = true;
        try { Status = (await _api.OpenIntercomDoorAsync(_id, 1)).Message; }
        catch (ApiException ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    public override void Dispose() => _intercoms.Intercoms.CollectionChanged -= OnListChanged;
}

/// <summary>Parlante IP: estado y hablar por él (con el micrófono del panel de parlantes).</summary>
public sealed partial class SpeakerCellPanel : CellPanel
{
    private readonly int _id;

    public SpeakerCellPanel(SpeakersViewModel speakers, int id, string name)
    {
        Speakers = speakers;
        _id = id;
        Title = name;
        speakers.Speakers.CollectionChanged += OnListChanged;
        speakers.PropertyChanged += OnSpeakersChanged;
        Resolve();
    }

    public SpeakersViewModel Speakers { get; }

    public override string Glyph => ResourceNode.GlyphOf(ResourceKind.Speaker);
    public override string Kind => "speaker";
    public override int ResourceId => _id;

    [ObservableProperty] private SpeakerItem? _item;

    /// <summary>Este cuadro es el que está hablando (el panel de parlantes habla por uno a la vez).</summary>
    [ObservableProperty] private bool _isTalking;

    public string TalkLabel => IsTalking ? "Dejar de hablar" : "Hablar";
    partial void OnIsTalkingChanged(bool value) => OnPropertyChanged(nameof(TalkLabel));

    private void OnListChanged(object? sender, NotifyCollectionChangedEventArgs e) => Resolve();

    private void OnSpeakersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SpeakersViewModel.IsTalking) && !Speakers.IsTalking) IsTalking = false;
    }

    private void Resolve()
    {
        Item = Speakers.Speakers.FirstOrDefault(s => s.Dto.Id == _id);
        if (Item is not null) Title = Item.Dto.Name;
        Status = Item is null ? "Cargando el parlante…" : "";
    }

    /// <summary>Hablar deja seleccionado solo este parlante y abre el micrófono; otro clic lo corta.</summary>
    [RelayCommand]
    private async Task ToggleTalk()
    {
        if (IsTalking)
        {
            await Speakers.StopTalkAsync();
            IsTalking = false;
            return;
        }
        if (Item is not { CanOperate: true }) return;
        foreach (var speaker in Speakers.Speakers) speaker.IsSelected = speaker == Item;
        await Speakers.StartTalkAsync();
        IsTalking = Speakers.IsTalking;
    }

    public override void Dispose()
    {
        if (IsTalking) _ = Speakers.StopTalkAsync();
        Speakers.Speakers.CollectionChanged -= OnListChanged;
        Speakers.PropertyChanged -= OnSpeakersChanged;
    }
}

/// <summary>
/// Grupo de parlantes: hablarle a todos de una vez (micrófono del panel de
/// parlantes), hacerlos sonar con un sonido del servidor o detener lo que
/// suena. Solo cuentan los parlantes que esta sesión puede operar; el resto
/// se ve con su aviso. Los integrantes se releen cuando el administrador
/// cambia el grupo.
/// </summary>
public sealed partial class SpeakerGroupCellPanel : CellPanel
{
    private readonly ApiClient _api;
    private readonly int _groupId;

    public SpeakerGroupCellPanel(ApiClient api, SpeakersViewModel speakers, int groupId, string name)
    {
        _api = api;
        Speakers = speakers;
        _groupId = groupId;
        Title = name;
        speakers.Speakers.CollectionChanged += OnListChanged;
        speakers.Groups.CollectionChanged += OnListChanged;
        speakers.PropertyChanged += OnSpeakersChanged;
        // Lo que puede operar llega después de entrar (y cambia con su alcance).
        OperableScope.Current.Changed += OnOperableChanged;
        Resolve();
    }

    private void OnOperableChanged() => Application.Current?.Dispatcher.InvokeAsync(Refresh);

    /// <summary>Un integrante cambió (en línea, ocupado, operable): se rehace el resumen.</summary>
    private void OnMemberChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();

    public SpeakersViewModel Speakers { get; }

    public override string Glyph => ResourceNode.GlyphOf(ResourceKind.Speaker);
    public override string Kind => "speaker-group";
    public override int ResourceId => _groupId;

    /// <summary>Integrantes del grupo que este puesto conoce (habilitados).</summary>
    public ObservableCollection<SpeakerItem> Members { get; } = [];

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isTalking;
    [ObservableProperty] private bool _canOperate;
    [ObservableProperty] private WorkflowAudioDto? _selectedSound;

    public string TalkLabel => IsTalking ? "Dejar de hablar" : "Hablar al grupo";
    partial void OnIsTalkingChanged(bool value) => OnPropertyChanged(nameof(TalkLabel));

    private void OnListChanged(object? sender, NotifyCollectionChangedEventArgs e) => Resolve();

    private void OnSpeakersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SpeakersViewModel.IsTalking) && !Speakers.IsTalking) IsTalking = false;
    }

    private void Resolve()
    {
        var group = Speakers.Groups.FirstOrDefault(g => g.Id == _groupId);
        Members.Clear();
        if (group is null)
        {
            Summary = "";
            CanOperate = false;
            Status = Speakers.Groups.Count == 0 ? "Cargando el grupo…" : "El grupo ya no existe.";
            return;
        }
        Title = group.Name;
        foreach (var old in Members) old.PropertyChanged -= OnMemberChanged;
        Members.Clear();
        foreach (var item in Speakers.Speakers.Where(s => group.SpeakerIds.Contains(s.Dto.Id)))
        {
            item.PropertyChanged += OnMemberChanged;
            Members.Add(item);
        }
        SelectedSound ??= Speakers.SelectedSound ?? Speakers.ServerSounds.FirstOrDefault();
        Status = Members.Count == 0 ? "El grupo no tiene parlantes activos." : "";
        Refresh();
    }

    private void Refresh()
    {
        int online = Members.Count(m => m.IsOnline), operable = Members.Count(m => m.CanOperate);
        Summary = $"{Members.Count} parlante(s) · {online} en línea" +
                  (operable < Members.Count ? $" · {Members.Count - operable} fuera de su alcance" : "");
        CanOperate = operable > 0;
    }

    private List<int> OperableIds() => Members.Where(m => m.CanOperate).Select(m => m.Dto.Id).ToList();

    /// <summary>Hablar marca solo los parlantes del grupo (los que puede operar) y abre el micrófono; otro clic lo corta.</summary>
    [RelayCommand]
    private async Task ToggleTalk()
    {
        if (IsTalking)
        {
            await Speakers.StopTalkAsync();
            IsTalking = false;
            return;
        }
        // Solo los que están en línea: uno caído no tiene que frenar al resto.
        var reachable = Members.Where(m => m.CanOperate && m.IsOnline).ToList();
        var skipped = Members.Where(m => m.CanOperate && !m.IsOnline).Select(m => m.Name).ToList();
        if (reachable.Count == 0)
        {
            Status = OperableIds().Count == 0 ? "Ningún parlante del grupo está a su alcance."
                : "Ningún parlante del grupo está en línea.";
            return;
        }
        var ids = reachable.Select(m => m.Dto.Id).ToList();
        foreach (var speaker in Speakers.Speakers) speaker.IsSelected = ids.Contains(speaker.Dto.Id);
        Status = "Abriendo el canal de voz…";
        await Speakers.StartTalkAsync(Title);
        IsTalking = Speakers.IsTalking;
        Status = !IsTalking ? Speakers.StatusMessage
            : skipped.Count > 0 ? $"Hablando. Sin conexión, se omitió: {string.Join(", ", skipped)}." : "";
    }

    [RelayCommand]
    private async Task PlaySound()
    {
        var ids = OperableIds();
        if (ids.Count == 0) { Status = "Ningún parlante del grupo está a su alcance."; return; }
        if (SelectedSound is null) { Status = "Elija un sonido."; return; }
        Status = "Reproduciendo…";
        try
        {
            var result = await _api.PlaySpeakersAsync(new SpeakerPlayRequestDto(ids, SpeakerPlaySources.Server, Sound: SelectedSound.DisplayName));
            Status = result.Message;
        }
        catch (ApiException ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private async Task Stop()
    {
        var ids = OperableIds();
        if (ids.Count == 0) return;
        if (IsTalking) await ToggleTalk();
        try { Status = (await _api.StopSpeakersAsync(ids)).Message; }
        catch (ApiException ex) { Status = ex.Message; }
    }

    public override void Dispose()
    {
        if (IsTalking) _ = Speakers.StopTalkAsync();
        Speakers.Speakers.CollectionChanged -= OnListChanged;
        Speakers.Groups.CollectionChanged -= OnListChanged;
        Speakers.PropertyChanged -= OnSpeakersChanged;
        OperableScope.Current.Changed -= OnOperableChanged;
        foreach (var member in Members) member.PropertyChanged -= OnMemberChanged;
    }
}

/// <summary>Módulo de E/S: entradas y salidas, con encender/apagar/pulsar cada relé.</summary>
public sealed partial class IoCellPanel : CellPanel
{
    private readonly ApiClient _api;
    private readonly VmsHubClient _hub;
    private readonly int _moduleId;

    public IoCellPanel(ApiClient api, VmsHubClient hub, int moduleId, string name)
    {
        _api = api;
        _hub = hub;
        _moduleId = moduleId;
        Title = name;
        _hub.IoModuleChanged += OnModuleChanged;
    }

    public override string Glyph => ResourceNode.GlyphOf(ResourceKind.IoModule);
    public override string Kind => "io-module";
    public override int ResourceId => _moduleId;

    public ObservableCollection<IoPointLine> Outputs { get; } = [];
    public ObservableCollection<IoPointLine> Inputs { get; } = [];
    [ObservableProperty] private string _connectionText = "";
    [ObservableProperty] private bool _isOnline;

    public bool MayOperate => PermissionScope.Current.Has(Permissions.IoOperate) && OperableScope.Current.CanOperateIoModule(_moduleId);

    public override async Task LoadAsync()
    {
        var modules = await _api.GetIoModulesAsync();
        if (modules.FirstOrDefault(m => m.Id == _moduleId) is { } module) Apply(module);
        else Status = "El módulo ya no existe o está fuera de su alcance.";
    }

    private void Apply(IoModuleDto dto)
    {
        Title = dto.Name;
        IsOnline = dto.Status == IoModuleStatus.Online;
        ConnectionText = !dto.Enabled ? "Pausado" : IsOnline ? "En línea" : "Sin conexión";
        Sync(Outputs, dto.Outputs, output: true);
        Sync(Inputs, dto.Inputs, output: false);
    }

    private void Sync(ObservableCollection<IoPointLine> target, IReadOnlyList<IoPointDto> points, bool output)
    {
        if (target.Count != points.Count)
        {
            target.Clear();
            foreach (var p in points) target.Add(new IoPointLine(this, p, output));
            return;
        }
        for (int i = 0; i < points.Count; i++) target[i].Apply(points[i]);
    }

    private void OnModuleChanged(IoModuleDto dto)
    {
        if (dto.Id == _moduleId) Ui.Run(() => Apply(dto));
    }

    internal async Task CommandAsync(IoPointLine point, IoOutputCommand command)
    {
        if (!MayOperate || !IsOnline) return;
        try
        {
            var result = await _api.SendIoOutputAsync(_moduleId, point.Number, new IoOutputCommandDto(command,
                command == IoOutputCommand.Pulse ? 1 : null));
            Status = result.Message;
        }
        catch (ApiException ex) { Status = ex.Message; }
    }

    public override void Dispose() => _hub.IoModuleChanged -= OnModuleChanged;
}

/// <summary>Entrada o salida de un módulo de E/S.</summary>
public sealed partial class IoPointLine : ObservableObject
{
    private readonly IoCellPanel _owner;

    public IoPointLine(IoCellPanel owner, IoPointDto dto, bool output)
    {
        _owner = owner;
        IsOutput = output;
        Apply(dto);
    }

    public bool IsOutput { get; }
    public int Number { get; private set; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private bool? _active;

    public string StateText => Active switch { true => "Activa", false => "Inactiva", _ => "—" };
    public string Level => Active == true ? "Warning" : Active == false ? "Ok" : "Unknown";
    partial void OnActiveChanged(bool? value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Level));
    }

    public void Apply(IoPointDto dto)
    {
        Number = dto.Number;
        Code = dto.Code;
        Name = string.IsNullOrWhiteSpace(dto.Name) ? dto.Code : dto.Name;
        Active = dto.Active;
    }

    [RelayCommand] private Task On() => _owner.CommandAsync(this, IoOutputCommand.On);
    [RelayCommand] private Task Off() => _owner.CommandAsync(this, IoOutputCommand.Off);
    [RelayCommand] private Task Pulse() => _owner.CommandAsync(this, IoOutputCommand.Pulse);
}
