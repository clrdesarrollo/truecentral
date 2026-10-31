using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Pantalla auxiliar de Vista en Vivo (estilo iVMS-4200, hasta 3): una ventana
/// independiente con su propia grilla de video, pensada para llevarse a otro
/// monitor. Comparte el árbol de dispositivos del shell (mismo inventario y
/// estados en línea) pero es dueña de sus cuadros — players propios, streams
/// propios —, así que cerrarla solo detiene lo que ella mostraba. El audio
/// sigue siendo exclusivo en TODA la aplicación: lo coordina el shell.
/// </summary>
public partial class AuxScreenViewModel : ObservableObject
{
    private readonly MainViewModel _shell;

    /// <summary>Número de la pantalla (1..3), fijo mientras esté abierta; al
    /// cerrarla su número queda libre para la próxima. 0 en Flota en vivo.</summary>
    public int SlotNumber { get; }

    /// <summary>
    /// Flota en vivo (módulo Flota): la misma ventana con el árbol de VEHÍCULOS en vez del inventario del shell, el stream
    /// secundario por omisión (el video viaja por el 4G del vehículo) y corte por inactividad. No entra en la última sesión.
    /// </summary>
    public bool IsFleet { get; }

    /// <summary>La principal es el Centro de mando 1: los que se abren con «+» son el 2, el 3 y el 4.</summary>
    public string WindowTitle => IsFleet ? "Flota en vivo" : $"Centro de mando {SlotNumber + 1}";
    public string TreeCaption => IsFleet ? "VEHÍCULOS" : "RECURSOS";
    public string SearchHint => IsFleet ? "Buscar vehículo o cámara…" : "Buscar…";

    private readonly ObservableCollection<DeviceNode> _fleetDevices = [];
    private string _fleetSearch = "";

    /// <summary>Árbol del shell, compartido: el mismo inventario y los mismos
    /// estados en línea (y el filtro del buscador de la ventana principal). En Flota, sus vehículos.</summary>
    public ObservableCollection<DeviceNode> Devices => IsFleet ? _fleetDevices : _shell.Devices;

    /// <summary>Árbol de cámaras según el modo del shell (por equipo o por
    /// ubicación): cambia junto con el de la ventana principal.</summary>
    public System.Collections.IEnumerable TreeItems => IsFleet ? _fleetDevices : _shell.TreeItems;

    /// <summary>Texto del buscador: es el MISMO del shell, así que escribir en
    /// cualquier ventana filtra el árbol de todas y se conserva al cambiar de modo. Flota tiene el suyo.</summary>
    public string SearchText
    {
        get => IsFleet ? _fleetSearch : _shell.SearchText;
        set
        {
            if (!IsFleet) { _shell.SearchText = value ?? ""; return; }
            _fleetSearch = value ?? "";
            OnPropertyChanged();
            ApplyFleetFilter();
        }
    }

    public ObservableCollection<VideoCellViewModel> Cells { get; } = [];

    /// <summary>Divisiones para monitor horizontal o vertical (cada ventana la suya: puede estar en un monitor parado).</summary>
    public IReadOnlyList<VideoLayoutGroup> LayoutGroups => VideoLayout.GroupsFor(IsVerticalLayouts);

    [ObservableProperty] private VideoLayout _currentLayout = VideoLayout.Default;
    [ObservableProperty] private int _maximizedIndex = -1;
    [ObservableProperty] private VideoCellViewModel? _selectedCell;
    /// <summary>Cuadro bajo el puntero durante un arrastre (se ilumina).</summary>
    [ObservableProperty] private VideoCellViewModel? _dropTargetCell;
    /// <summary>Pantalla completa de la grilla en ESTA ventana (cubre su
    /// monitor; Esc sale). Independiente de la de la ventana principal.</summary>
    [ObservableProperty] private bool _isGridFullscreen;
    [ObservableProperty] private bool _isTreeCollapsed;
    /// <summary>Modo zoom digital (lupa + recuadro) de esta ventana.</summary>
    [ObservableProperty] private bool _isDigitalZoomMode;
    [ObservableProperty] private bool _isBulkOpening;
    [ObservableProperty] private string _bulkOpeningText = "";
    [ObservableProperty] private string _statusMessage = HintMessage;

    private const string HintMessage =
        "Doble clic en un canal del árbol lo abre aquí; arrastre esta ventana al monitor que quiera.";

    /// <summary>Ventana que hospeda esta pantalla (el aviso modal de la
    /// apertura masiva y las notificaciones salen sobre ella, en su monitor).</summary>
    public Window? OwnerWindow { get; set; }

    /// <summary>Se guardó un archivo local desde esta pantalla: la ventana
    /// muestra la notificación flotante en su propio monitor.</summary>
    public event Action<string, string, string>? MediaSaved;

    public AuxScreenViewModel(MainViewModel shell, int slotNumber, bool fleet = false)
    {
        _shell = shell;
        SlotNumber = slotNumber;
        IsFleet = fleet;
        if (fleet) _statusMessage = FleetHint;
        _shell.PropertyChanged += OnShellPropertyChanged;
        InitialLayout = ApplyLayoutAsync(VideoLayout.Default);
        if (fleet)
        {
            _ = LoadFleetAsync();
            _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _idleTimer.Tick += (_, _) => IdleTick();
            _idleTimer.Start();
        }
    }

    /// <summary>La primera división: quien quiera cambiarla enseguida (restaurar la última sesión) la espera.</summary>
    public Task InitialLayout { get; }

    private const string FleetHint =
        "Doble clic en un vehículo abre todas sus cámaras. El video de los vehículos usa sus datos móviles.";

    // ---------- Flota: árbol de vehículos ----------

    /// <summary>Carga (o recarga) los vehículos y sus cámaras habilitadas; las celdas abiertas siguen con su video.</summary>
    public async Task LoadFleetAsync()
    {
        if (!IsFleet) return;
        try
        {
            var vehicles = await _shell.Api.GetFleetVehiclesAsync();
            _fleetDevices.Clear();
            foreach (var v in vehicles.Where(v => v.Enabled))
            {
                // El vehículo hace de "equipo" (su grabador): las concesiones de vivo van por equipo + canal RTSP.
                string name = v.Plate is { Length: > 0 } plate ? $"{v.Code} · {plate}" : v.Code;
                var device = new DeviceDto(v.DeviceId, name, DeviceType.Dvr, "hikvision-isup", "", 0, 0, "",
                    v.Model, null, v.FirmwareVersion, v.Channels.Count, v.Status, v.LastSeenAt, v.CreatedAt,
                    EnabledChannelCount: v.Channels.Count(c => c.Enabled), LocationId: v.LocationId, Location: v.Location);
                var node = new DeviceNode(device);
                foreach (var c in v.Channels.Where(c => c.Enabled))
                    node.Channels.Add(new ChannelNode(node, new ChannelDto(c.Id, v.DeviceId, c.ChannelNumber, c.RtspChannel, c.Name,
                        c.Enabled, c.IsOnline, SupportsPtz: false, LocationId: v.LocationId)));
                _fleetDevices.Add(node);
            }
            ApplyFleetFilter();
            RefreshFleetLive();
            if (_fleetDevices.Count == 0)
                StatusMessage = "Todavía no hay vehículos habilitados (o su alcance no incluye ninguno).";
        }
        catch (ApiException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>Abre las cámaras de un vehículo por el id de su grabador (lo pide el mapa de flota con "Abrir en grilla").</summary>
    public async Task OpenFleetDeviceAsync(int deviceId)
    {
        if (!IsFleet) return;
        var node = _fleetDevices.FirstOrDefault(d => d.Device.Id == deviceId);
        if (node is null)
        {
            await LoadFleetAsync();
            node = _fleetDevices.FirstOrDefault(d => d.Device.Id == deviceId);
        }
        if (node is null) { StatusMessage = "Ese vehículo no está disponible (deshabilitado o fuera de su alcance)."; return; }
        await OpenDeviceAsync(node);
    }

    /// <summary>Estado de un grabador que cambió (en línea / fuera de línea): llega por el hub del shell.</summary>
    public void UpdateDeviceStatus(DeviceDto dto)
    {
        var node = _fleetDevices.FirstOrDefault(d => d.Device.Id == dto.Id);
        if (node is not null) node.Device = node.Device with { Status = dto.Status, LastSeenAt = dto.LastSeenAt };
    }

    private void ApplyFleetFilter()
    {
        string q = Normalize(_fleetSearch.Trim());
        foreach (var d in _fleetDevices)
        {
            bool deviceMatch = q.Length == 0 || Normalize(d.Device.Name).Contains(q);
            foreach (var c in d.Channels) c.IsVisible = deviceMatch || Normalize(c.Header).Contains(q);
            d.IsVisible = deviceMatch || d.Channels.Any(c => c.IsVisible);
        }
    }

    private static string Normalize(string s) =>
        new string(s.Normalize(System.Text.NormalizationForm.FormD)
            .Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray()).ToLowerInvariant();

    /// <summary>Marca ▶ en los vehículos que están en pantalla (el árbol de Flota es propio de esta ventana).</summary>
    private void RefreshFleetLive()
    {
        var live = Cells.Where(c => c.AssignedChannel is not null)
            .GroupBy(c => c.AssignedChannel!.Channel.Id)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(c => $"cuadro {c.Index}")));
        foreach (var channel in _fleetDevices.SelectMany(d => d.Channels))
        {
            bool on = live.TryGetValue(channel.Channel.Id, out var where);
            channel.IsLive = on;
            channel.LiveLocation = on ? "En vivo en " + where : "";
        }
    }

    private void OnCellChannelChanged()
    {
        // Abrir o cambiar una cámara cuenta como actividad (también si la abrió el mapa, sin teclado ni mouse).
        _lastOpenUtc = DateTime.UtcNow;
        if (IsFleet) RefreshFleetLive();
        else _shell.RefreshLiveChannels();
    }

    // ---------- Flota: corte por inactividad ----------
    // El video de un vehículo viaja por su 4G: sin teclado ni mouse en el PC durante Settings.FleetIdleMinutes, se avisa y,
    // si nadie responde en IdleWarnSeconds, se cierran los cuadros (queda en la bitácora).

    private readonly DispatcherTimer? _idleTimer;
    private DateTime? _idleWarnSince;
    private DateTime _lastOpenUtc = DateTime.UtcNow;
    private const int IdleWarnSeconds = 30;

    private void IdleTick()
    {
        int minutes = _shell.Settings.FleetIdleMinutes;
        var open = Cells.Where(c => !c.IsEmpty).ToList();
        if (minutes <= 0 || open.Count == 0) { EndIdleWarning(); return; }
        var sinceOpen = DateTime.UtcNow - _lastOpenUtc;
        var idle = UserInput.IdleTime() is var input && input < sinceOpen ? input : sinceOpen;
        if (idle < TimeSpan.FromMinutes(minutes)) { EndIdleWarning(); return; }
        _idleWarnSince ??= DateTime.UtcNow;
        int left = IdleWarnSeconds - (int)(DateTime.UtcNow - _idleWarnSince.Value).TotalSeconds;
        if (left > 0)
        {
            StatusMessage = $"Sin actividad hace {minutes} min: el video de los vehículos se corta en {left} s " +
                            "para no gastar sus datos móviles (mueva el mouse para seguir viendo).";
            return;
        }
        _idleWarnSince = null;
        var names = open.Select(c => c.AssignedChannel?.Device.Name).Where(n => n is not null).Distinct().ToList();
        ClearAll();
        StatusMessage = $"Se cerró el video de {open.Count} cámara(s) por inactividad (Configuración → Video).";
        _ = _shell.Api.ReportAuditEventAsync(new ClientAuditEventDto("fleet-live-idle-stopped", null, null, null, null, null,
            $"Se cerraron {open.Count} cámara(s) de {string.Join(", ", names)} tras {minutes} min sin actividad en el cliente de escritorio."));
    }

    private void EndIdleWarning()
    {
        if (_idleWarnSince is null) return;
        _idleWarnSince = null;
        StatusMessage = FleetHint;
    }

    private void OnShellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (IsFleet) return;   // Flota tiene su propio árbol y su propio buscador
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SearchText): OnPropertyChanged(nameof(SearchText)); break;
            // Cambiaron los permisos de la sesión: los botones de esta ventana también.
            case nameof(MainViewModel.CanPtz):
                OnPropertyChanged(nameof(CanPtz));
                UpdatePtzPanel();
                break;
            case nameof(MainViewModel.CanSaveViews): OnPropertyChanged(nameof(CanSaveViews)); break;
            case nameof(MainViewModel.CanShareViews): OnPropertyChanged(nameof(CanShareViews)); break;
        }
    }

    [RelayCommand]
    private void EnterGridFullscreen() => IsGridFullscreen = true;

    partial void OnIsDigitalZoomModeChanged(bool value) =>
        StatusMessage = value
            ? "Zoom digital: arrastre sobre el video para marcar el área; clic derecho vuelve a 1×."
            : HintMessage;

    /// <summary>Cambio de división en curso (clics rápidos ignorados).</summary>
    private bool _layoutBusy;

    [RelayCommand]
    private async Task SelectLayoutAsync(VideoLayout layout)
    {
        if (_layoutBusy) return;
        _layoutBusy = true;
        try { await ApplyLayoutAsync(layout); }
        finally { _layoutBusy = false; }
    }

    /// <summary>Igual que en la grilla principal: celdas por tandas cediendo
    /// ciclos, porque crear/liberar players es caro (superficies de GPU).</summary>
    public async Task ApplyLayoutAsync(VideoLayout layout, IReadOnlyList<int>? placement = null)
    {
        MaximizedIndex = -1;
        // Igual que en la grilla principal: el secundario estacionado de un
        // cuadro promovido ya no tiene a qué volver.
        _autoPromotedCell?.ReleaseParked();
        _autoPromotedCell = null;
        _autoPromotedChannel = null;
        if (placement is null) CurrentLayout = layout;
        int count = layout.CellCount;
        // Al unir cuadros la grilla achica: las cámaras se reacomodan ANTES de
        // quitar los cuadros sobrantes (que quedan libres al final).
        if (placement is not null && placement.Count >= count)
        {
            VideoCellViewModel.Rearrange(Cells, placement);
            CurrentLayout = layout;
            placement = null;
        }
        while (Cells.Count > count)
        {
            var cell = Cells[^1];
            Cells.RemoveAt(Cells.Count - 1);
            cell.AudioActivated -= _shell.OnCellAudioActivated;
            cell.MediaSaved -= OnCellMediaSaved;
            cell.AssignedChannelChanged -= OnCellChannelChanged;
            cell.Dispose();
            await MainViewModel.BreatheAsync();
        }
        while (Cells.Count < count)
        {
            var cell = new VideoCellViewModel(_shell.Api, _shell.Settings);
            cell.AudioActivated += _shell.OnCellAudioActivated;
            cell.MediaSaved += OnCellMediaSaved;
            // El árbol es del shell: la marca de "en vivo" cuenta todas las pantallas.
            cell.AssignedChannelChanged += OnCellChannelChanged;
            Cells.Add(cell);
            await MainViewModel.BreatheAsync();
        }
        if (placement is not null)
        {
            VideoCellViewModel.Rearrange(Cells, placement);
            CurrentLayout = layout;
        }
        for (int i = 0; i < Cells.Count; i++)
            Cells[i].Index = i + 1;
        if (SelectedCell is not null && !Cells.Contains(SelectedCell))
            SelectedCell = null;
        OnCellChannelChanged();
    }

    /// <summary>
    /// Modo «Unir cuadros»: arrastrar de un cuadro a otro une el rectángulo en
    /// un solo cuadro; un clic en un cuadro unido lo separa.
    /// </summary>
    [ObservableProperty] private bool _isMergeMode;

    partial void OnIsMergeModeChanged(bool value)
    {
        if (value) IsDigitalZoomMode = false;
        StatusMessage = value
            ? "Unir cuadros: arrastre de un cuadro a otro para unirlos en uno; clic en un cuadro unido lo separa. Esc termina."
            : HintMessage;
    }

    /// <summary>Marca en los bordes lo que uniría la selección de <paramref name="from"/> a <paramref name="to"/>.</summary>
    public void PreviewMerge(VideoCellViewModel from, VideoCellViewModel? to)
    {
        int a = Cells.IndexOf(from), b = to is null ? -1 : Cells.IndexOf(to);
        foreach (var cell in Cells) cell.MergeMark = 0;
        if (a < 0 || b < 0 || a >= CurrentLayout.CellCount || b >= CurrentLayout.CellCount) return;
        var (_, inside, cut) = CurrentLayout.MergeSelection(a, b);
        foreach (int i in inside) Cells[i].MergeMark = cut.Count > 0 ? 2 : 1;
        foreach (int i in cut) Cells[i].MergeMark = 2;
    }

    /// <summary>
    /// Une los cuadros de <paramref name="from"/> a <paramref name="to"/>. Queda
    /// la cámara del cuadro donde empezó la selección (o la primera que haya);
    /// las demás de la selección se cierran.
    /// </summary>
    public async Task MergeCellsAsync(VideoCellViewModel from, VideoCellViewModel to)
    {
        foreach (var c in Cells) c.MergeMark = 0;
        if (_layoutBusy || MaximizedIndex >= 0) return;
        int a = Cells.IndexOf(from), b = Cells.IndexOf(to);
        if (a < 0 || b < 0) return;
        if (CurrentLayout.Merge(a, b, out var reason) is not { } merge)
        {
            if (a != b) StatusMessage = reason ?? "";
            return;
        }
        var inside = Enumerable.Range(0, Cells.Count).Where(i => merge.Placement[i] == merge.StripCells[0]).ToList();
        var keep = !from.IsEmpty ? from : inside.Select(i => Cells[i]).FirstOrDefault(c => !c.IsEmpty);
        var closed = inside.Select(i => Cells[i]).Where(c => c != keep && !c.IsEmpty).Select(c => c.Title).ToList();
        foreach (int i in inside)
            if (Cells[i] != keep) Cells[i].Clear();
        // Permutación completa: lo que se une va al cuadro unido (la cámara que
        // queda) o, vacío, a los lugares sobrantes del final.
        var target = new int[Cells.Count];
        int spare = merge.Layout.CellCount;
        for (int i = 0; i < Cells.Count; i++)
            target[i] = !inside.Contains(i) || Cells[i] == keep || (keep is null && i == inside[0])
                ? merge.Placement[i] : spare++;
        _layoutBusy = true;
        try { await ApplyLayoutAsync(merge.Layout, target); }
        finally { _layoutBusy = false; }
        SelectedCell = Cells[merge.StripCells[0]];
        // La herramienta es de un uso: unido, se apaga (antes del aviso, que apagarla reemplaza).
        IsMergeMode = false;
        StatusMessage = $"{inside.Count} cuadros unidos en uno." +
            (closed.Count > 0 ? $" Se cerró: {string.Join(", ", closed.Select(t => $"\"{t}\""))}." : "");
    }

    /// <summary>Separa un cuadro unido en cuadros simples (la cámara queda arriba a la izquierda).</summary>
    public async Task SplitCellAsync(VideoCellViewModel cell)
    {
        if (_layoutBusy || MaximizedIndex >= 0) return;
        int index = Cells.IndexOf(cell);
        if (index < 0 || index >= CurrentLayout.CellCount || CurrentLayout.Split(index) is not { } split)
        {
            StatusMessage = "Ese cuadro no está unido: arrastre hasta otro cuadro para unirlos.";
            return;
        }
        _layoutBusy = true;
        try { await ApplyLayoutAsync(split.Layout, split.Placement); }
        finally { _layoutBusy = false; }
        SelectedCell = Cells[split.StripCells[0]];
        IsMergeMode = false; // separar también termina la herramienta
        StatusMessage = $"Cuadro separado: la grilla queda en {split.Layout.CellCount} cuadros.";
    }

    /// <summary>Centro de mando: un recurso (puerta, panel, cerco…) en un cuadro de esta pantalla.</summary>
    public async Task OpenResourceInCellAsync(ResourceNode node, VideoCellViewModel cell)
    {
        if (_shell.CreatePanel(node) is not { } panel) return;
        await cell.ShowPanelAsync(panel);
        SelectedCell = cell;
    }

    /// <summary>«Vigilar esta ubicación» en un cuadro de esta pantalla.</summary>
    public async Task WatchLocationAsync(LocationNode location)
    {
        if (location.Location is not { } place) return;
        var panel = _shell.CreateWatchPanel(WatchOptions.Default with { LocationIds = [place.Id] });
        var cell = SelectedCell ?? Cells.FirstOrDefault(c => c.IsEmpty) ?? Cells.FirstOrDefault();
        if (cell is null) return;
        await cell.ShowPanelAsync(panel);
        SelectedCell = cell;
        StatusMessage = $"El cuadro {cell.Index} vigila «{place.Name}».";
    }

    /// <summary>Doble clic en un recurso: en el cuadro seleccionado, o en el primero libre.</summary>
    public async Task OpenResourceAsync(ResourceNode node)
    {
        var cell = SelectedCell ?? Cells.FirstOrDefault(c => c.IsEmpty) ?? Cells.FirstOrDefault();
        if (cell is not null) await OpenResourceInCellAsync(node, cell);
    }

    /// <summary>Igual que en la grilla principal: no más cuadros que cámaras a la vista.</summary>
    private int GrowCellLimit => Math.Max(Cells.Count, Devices.Sum(d => d.Channels.Count));

    public string? GrowBlockedReason(GridEdge edge) =>
        MaximizedIndex >= 0 ? "Restaure el cuadro maximizado para ampliar la grilla."
        : CurrentLayout.GrowBlockedReason(edge, GrowCellLimit);

    /// <summary>Ampliar la grilla de esta pantalla (ver MainViewModel.GrowLayoutAsync).</summary>
    public async Task GrowLayoutAsync(GridEdge edge, int slot, ChannelNode node)
    {
        if (_layoutBusy || CurrentLayout.Grow(edge, GrowCellLimit) is not { } growth) return;
        _layoutBusy = true;
        try { await ApplyLayoutAsync(growth.Layout, growth.Placement); }
        finally { _layoutBusy = false; }
        var cell = Cells[growth.StripCells[Math.Clamp(slot, 0, growth.StripCells.Count - 1)]];
        SelectedCell = cell;
        StatusMessage = $"{MainViewModel.GrowthText(edge)}: la grilla pasa a {growth.Layout.Columns}×{growth.Layout.Rows} " +
                        $"({growth.Layout.CellCount} cuadros).";
        await OpenChannelInCellAsync(node, cell);
    }

    private void OnCellMediaSaved(string title, string glyph, string path) =>
        Application.Current.Dispatcher.InvokeAsync(() => MediaSaved?.Invoke(title, glyph, path));

    /// <summary>Cuadro promovido a stream principal por estar maximizado
    /// (misma regla que la grilla principal: al restaurar vuelve a secundario
    /// si nadie lo tocó en el intertanto).</summary>
    private VideoCellViewModel? _autoPromotedCell;
    private ChannelNode? _autoPromotedChannel;

    public void ToggleMaximize(VideoCellViewModel cell)
    {
        int index = Cells.IndexOf(cell);
        if (index < 0) return;
        if (MaximizedIndex == index)
        {
            MaximizedIndex = -1;
            if (_autoPromotedCell == cell && cell.AssignedChannel == _autoPromotedChannel &&
                _autoPromotedChannel is not null)
            {
                if (cell.Profile == StreamProfile.Main)
                    _ = cell.SwitchToProfileAsync(StreamProfile.Sub, hardFallbackOnFailure: true);
                else
                    cell.CancelPendingSwitch();
            }
            _autoPromotedCell = null;
            _autoPromotedChannel = null;
            return;
        }
        if (cell.IsEmpty) return;
        MaximizedIndex = index;
        SelectedCell = cell;
        if (cell.Profile == StreamProfile.Sub && cell.AssignedChannel is { } node)
        {
            _autoPromotedCell = cell;
            _autoPromotedChannel = node;
            // El secundario queda estacionado: restaurar es instantáneo.
            _ = cell.SwitchToProfileAsync(StreamProfile.Main, keepCurrentForRestore: true);
        }
    }

    /// <summary>Abre un canal en el cuadro seleccionado / primer libre /
    /// primero, y avanza la selección (doble clics consecutivos van llenando).</summary>
    public async Task OpenChannelAsync(ChannelNode node)
    {
        var cell = SelectedCell
            ?? Cells.FirstOrDefault(c => c.IsEmpty)
            ?? Cells.FirstOrDefault();
        if (cell is null) return;

        int next = Cells.IndexOf(cell) + 1;
        SelectedCell = next < Cells.Count ? Cells[next] : null;

        await cell.OpenAsync(node, DefaultProfileForOpen());
    }

    /// <summary>Drag & drop del árbol a un cuadro específico.</summary>
    public async Task OpenChannelInCellAsync(ChannelNode node, VideoCellViewModel cell) =>
        await cell.OpenAsync(node, DefaultProfileForOpen());

    /// <summary>
    /// Soltar un cuadro sobre otro de ESTA grilla. El origen puede venir de
    /// cualquier ventana (principal u otra auxiliar): viaja el player con el
    /// video andando, así que mover una cámara entre monitores no corta el
    /// stream ni pide una concesión nueva.
    /// </summary>
    public void SwapCells(VideoCellViewModel source, VideoCellViewModel target)
    {
        if (ReferenceEquals(source, target)) return;
        if (!Cells.Contains(target)) return;
        if (source.IsEmpty && target.IsEmpty) return;

        bool exchange = !target.IsEmpty;
        string moved = source.Title ?? "";
        string displaced = target.Title ?? "";

        VideoCellViewModel.SwapContent(source, target);

        SelectedCell = target;
        StatusMessage = exchange
            ? $"Cuadros intercambiados: \"{moved}\" ↔ \"{displaced}\"."
            : $"\"{moved}\" movida al cuadro {target.Index}.";
    }

    /// <summary>Canales que se abren juntos en la apertura masiva.</summary>
    private const int OpenBatchSize = 4;

    /// <summary>Doble clic en un equipo: abre todos sus canales habilitados en
    /// esta pantalla, con grilla a medida (misma preferencia que la principal).</summary>
    public Task OpenDeviceAsync(DeviceNode device) => OpenChannelsAsync(device.Device.Name, device.Channels.ToList());

    /// <summary>Doble clic en una ubicación (árbol por ubicación): abre en esta
    /// pantalla sus cámaras y las de sus sububicaciones.</summary>
    public Task OpenLocationAsync(LocationNode location) =>
        OpenChannelsAsync(location.Header, location.AllChannels().ToList());

    private async Task OpenChannelsAsync(string label, List<ChannelNode> channels)
    {
        if (channels.Count == 0)
        {
            StatusMessage = $"\"{label}\" no tiene canales habilitados.";
            return;
        }

        if (channels.Count > 64)
        {
            StatusMessage = $"\"{label}\" tiene {channels.Count} canales: se abren los primeros 64.";
            channels = channels.Take(64).ToList();
        }

        IsBulkOpening = true;
        BulkOpeningText = $"Abriendo {channels.Count} canal(es) de \"{label}\"…";
        StatusMessage = BulkOpeningText;
        var loading = OwnerWindow is { IsLoaded: true } owner
            ? Views.LoadingWindow.Open(owner, BulkOpeningText)
            : null;
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Render);

            if (_shell.Settings.FitGridToDevice)
                await ApplyLayoutAsync(VideoLayout.FitFor(channels.Count));
            else
                await ApplyLayoutAsync(VideoLayout.SmallestFor(channels.Count));

            SelectedCell = null;
            var profile = DefaultProfileForOpen();
            int openCount = Math.Min(channels.Count, Cells.Count);
            for (int i = openCount; i < Cells.Count; i++)
                Cells[i].Clear();

            for (int i = 0; i < openCount; i += OpenBatchSize)
            {
                int upTo = Math.Min(i + OpenBatchSize, openCount);
                var wave = new List<Task>();
                for (int j = i; j < upTo; j++)
                    wave.Add(Cells[j].OpenAsync(channels[j], profile));
                await Task.WhenAll(wave);
                BulkOpeningText = $"Abriendo canales de \"{label}\"… {upTo}/{openCount}";
                StatusMessage = BulkOpeningText;
                loading?.Update(BulkOpeningText);
                await MainViewModel.BreatheAsync();
            }
            StatusMessage = $"{openCount} canal(es) de \"{label}\" en pantalla.";
        }
        catch (Exception ex)
        {
            // Igual que en la grilla principal: una falla de video no puede
            // llevarse la aplicación (el doble clic del árbol es async void).
            StatusMessage = $"No se pudieron abrir todos los canales de \"{label}\": {ex.Message}";
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
            loading?.Finish();
            IsBulkOpening = false;
        }
    }

    /// <summary>Misma regla que la grilla principal (Configuración → Video).</summary>
    private StreamProfile DefaultProfileForOpen() => _shell.Settings.DefaultProfile switch
    {
        // Flota: secundario salvo que el puesto pida siempre el principal (el video viaja por el 4G del vehículo).
        _ when IsFleet => _shell.Settings.DefaultProfile == "main" ? StreamProfile.Main : StreamProfile.Sub,
        "main" => StreamProfile.Main,
        "sub" => StreamProfile.Sub,
        _ => Cells.Count <= 4 ? StreamProfile.Main : StreamProfile.Sub,
    };

    [RelayCommand]
    public void ClearAll()
    {
        foreach (var cell in Cells)
            cell.Clear();
    }

    /// <summary>La ventana se cerró: se liberan sus players.</summary>
    public void Dispose()
    {
        _idleTimer?.Stop();
        _shell.PropertyChanged -= OnShellPropertyChanged;
        foreach (var cell in Cells)
            cell.Dispose();
        Cells.Clear();
    }
}
