using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Ventana principal: árbol de dispositivos/canales y grilla de video en vivo
/// (1/4/9/16). El hub SignalR refresca el árbol ante cambios de configuración
/// y los estados en línea de los equipos.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly VmsHubClient _hub;
    /// <summary>Preferencias locales (última división, etc.). Copia propia:
    /// el login ya terminó de escribir las suyas cuando esta ventana nace.</summary>
    private readonly ClientSettings _settings = ClientSettings.Load();

    /// <summary>
    /// Cliente de API y hub del shell. Los usa el módulo Muro de video, cuya
    /// interfaz se construye en código (la grilla del muro es dinámica) y por
    /// eso no consume el ViewModel como los demás módulos.
    /// </summary>
    public ApiClient Api => _api;
    public VmsHubClient Hub => _hub;

    public ObservableCollection<DeviceNode> Devices { get; } = [];
    public ObservableCollection<VideoCellViewModel> Cells { get; } = [];

    [ObservableProperty] private string _connectionStatus = "Conectado";
    [ObservableProperty] private bool _isConnected = true;
    [ObservableProperty] private VideoCellViewModel? _selectedCell;
    /// <summary>Cuadro bajo el puntero durante un arrastre (se ilumina para
    /// mostrar dónde va a caer la cámara). null = no hay arrastre encima.</summary>
    [ObservableProperty] private VideoCellViewModel? _dropTargetCell;
    [ObservableProperty] private string _statusMessage = ReadyMessage;

    // ---------- Navegación (navbar superior + menú lateral) ----------

    /// <summary>Página visible: "Home" o "Live". Las pestañas del navbar y el
    /// menú lateral la cambian; el contenido se conmuta por Visibility para no
    /// destruir la Vista en Vivo (sus streams siguen corriendo en segundo plano).</summary>
    [ObservableProperty] private string _activeSection = "Home";

    /// <summary>La viñeta "Vista en Vivo" existe en el navbar (se abre desde el
    /// inicio o el menú lateral y se cierra con su ✕).</summary>
    [ObservableProperty] private bool _isLiveViewOpen;

    /// <summary>La viñeta "Reproducción" existe en el navbar.</summary>
    [ObservableProperty] private bool _isPlaybackOpen;

    /// <summary>La viñeta "Muro de video" existe en el navbar.</summary>
    [ObservableProperty] private bool _isWallOpen;

    [RelayCommand]
    private void OpenWall()
    {
        IsWallOpen = true;
        ActiveSection = "Wall";
        StatusMessage = WallHintMessage;
    }

    /// <summary>
    /// Cerrar la viñeta solo saca el módulo de la vista: el muro sigue
    /// mostrando lo que tenga (lo decodifica el equipo, no este cliente).
    /// </summary>
    [RelayCommand]
    private void CloseWall()
    {
        IsWallOpen = false;
        ActiveSection = "Home";
        StatusMessage = ReadyMessage;
    }

    /// <summary>La viñeta "Reconocimiento de patentes" existe en el navbar.</summary>
    [ObservableProperty] private bool _isLprOpen;

    /// <summary>
    /// Aplicaciones → Reconocimiento de patentes. El historial se carga una
    /// sola vez: después el módulo se mantiene solo con lo que empuja el hub,
    /// así que volver a abrir la viñeta no vuelve a golpear la API.
    /// </summary>
    public LprViewModel Lpr { get; }

    private bool _lprLoaded;

    [RelayCommand]
    private void OpenLpr()
    {
        IsLprOpen = true;
        ActiveSection = "Lpr";
        StatusMessage = LprHintMessage;
        if (_lprLoaded) return;
        _lprLoaded = true;
        _ = Lpr.InitializeAsync();
    }

    /// <summary>
    /// Cerrar la viñeta solo saca el módulo de la vista: el servidor sigue
    /// recibiendo y guardando los reconocimientos (las cámaras no dejan de
    /// leer porque el operador cambie de pantalla).
    /// </summary>
    [RelayCommand]
    private void CloseLpr()
    {
        IsLprOpen = false;
        ActiveSection = "Home";
        StatusMessage = ReadyMessage;
    }

    /// <summary>Módulo Reproducción (grabaciones remotas del DVR/NVR).</summary>
    public PlaybackViewModel Playback { get; }

    [RelayCommand]
    private void OpenPlayback()
    {
        IsPlaybackOpen = true;
        ActiveSection = "Playback";
        StatusMessage = "Reproducción: doble clic en un canal del árbol y luego clic en la línea de tiempo.";
    }

    /// <summary>Cerrar la viñeta detiene la reproducción en curso.</summary>
    [RelayCommand]
    private void ClosePlayback()
    {
        Playback.StopCommand.Execute(null);
        IsGridFullscreen = false; // la pantalla completa es del módulo, no del shell
        IsPlaybackOpen = false;
        ActiveSection = "Home";
        StatusMessage = ReadyMessage;
    }

    [RelayCommand]
    private void GoHome() => ActiveSection = "Home";

    [RelayCommand]
    private void OpenLiveView()
    {
        IsLiveViewOpen = true;
        ActiveSection = "Live";
        StatusMessage = HintMessage;
    }

    /// <summary>Cerrar la viñeta detiene todos los streams (libera ancho de banda).</summary>
    [RelayCommand]
    private void CloseLiveView()
    {
        ClearAll();
        SelectedCell = null;
        IsLiveViewOpen = false;
        ActiveSection = "Home";
        StatusMessage = ReadyMessage;
    }

    [RelayCommand]
    private void ShowComingSoon(string module) =>
        StatusMessage = $"El módulo \"{module}\" estará disponible próximamente.";

    /// <summary>Ventana de Configuración. Edita la MISMA instancia de ajustes
    /// que usan las celdas (carpetas, formato, stream por defecto): al guardar,
    /// los cambios rigen de inmediato sin reiniciar.</summary>
    [RelayCommand]
    private void OpenSettings()
    {
        var window = new Views.SettingsWindow(_settings) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() == true)
        {
            // El ajuste de imagen rige de inmediato en los cuadros existentes.
            foreach (var cell in Cells)
                cell.ApplyStretch(_settings.StretchVideo);
            StatusMessage = "Configuración guardada.";
        }
    }

    /// <summary>
    /// Modo zoom digital de la Vista en Vivo: el puntero pasa a lupa y
    /// arrastrar sobre un cuadro marca el área a acercar. Es zoom sobre la
    /// imagen ya recibida (no mueve la cámara ni pide otro stream).
    /// </summary>
    [ObservableProperty] private bool _isDigitalZoomMode;

    partial void OnIsDigitalZoomModeChanged(bool value) =>
        StatusMessage = value
            ? "Zoom digital: arrastre sobre el video para marcar el área; clic derecho vuelve a 1×."
            : HintMessage;

    // ---------- PTZ ----------
    // El panel está siempre presente (minimizado por defecto); sus controles
    // se habilitan solo cuando el cuadro seleccionado tiene una cámara PTZ.
    [ObservableProperty] private bool _isPtzAvailable;
    /// <summary>El cuerpo del panel PTZ parte minimizado; el usuario lo abre cuando quiera.</summary>
    [ObservableProperty] private bool _isPtzExpanded;
    /// <summary>Modo precisión del teclado (Shift sostenido): PTZ a velocidad mínima.</summary>
    [ObservableProperty] private bool _isPrecisionMode;
    [ObservableProperty] private string _ptzTargetName = "";
    [ObservableProperty] private int _ptzSpeed = 4;
    private ChannelNode? _ptzChannel;

    partial void OnSelectedCellChanged(VideoCellViewModel? oldValue, VideoCellViewModel? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnSelectedCellPropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnSelectedCellPropertyChanged;
        UpdatePtzPanel();
    }

    private void OnSelectedCellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoCellViewModel.AssignedChannel))
            UpdatePtzPanel();
    }

    private void UpdatePtzPanel()
    {
        _ptzChannel = SelectedCell?.AssignedChannel is { Channel.SupportsPtz: true } node ? node : null;
        IsPtzAvailable = _ptzChannel is not null;
        PtzTargetName = _ptzChannel?.Channel.Name ?? "sin cámara PTZ";
        SyncTreeSelection();
    }

    /// <summary>Seleccionar un cuadro de la grilla selecciona su canal en el árbol.</summary>
    private ChannelNode? _treeSelected;

    private void SyncTreeSelection()
    {
        var node = SelectedCell?.AssignedChannel;
        if (_treeSelected == node) return;
        if (_treeSelected is not null) _treeSelected.IsSelected = false;
        _treeSelected = node;
        if (node is not null) node.IsSelected = true;
    }

    // ---------- Buscador del árbol de dispositivos ----------

    [ObservableProperty] private string _searchText = "";

    partial void OnSearchTextChanged(string value) => ApplySearchFilter();

    /// <summary>Filtra el árbol: un canal se muestra si su nombre (o el de su
    /// equipo) contiene el texto; un equipo, si él o alguno de sus canales
    /// calza. Ignora mayúsculas y tildes.</summary>
    private void ApplySearchFilter()
    {
        string text = SearchText.Trim();
        foreach (var device in Devices)
        {
            bool deviceMatch = Matches(device.Header, text);
            bool anyChannelVisible = false;
            foreach (var channel in device.Channels)
            {
                channel.IsVisible = text.Length == 0 || deviceMatch || Matches(channel.Header, text);
                anyChannelVisible |= channel.IsVisible;
            }
            device.IsVisible = text.Length == 0 || deviceMatch || anyChannelVisible;
        }
    }

    private static bool Matches(string haystack, string needle) =>
        needle.Length == 0 ||
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            haystack, needle, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    // ---------- Indicadores de recursos del servidor (CPU / RAM / disco) ----------

    [ObservableProperty] private bool _hasMetrics;
    [ObservableProperty] private string _cpuLevel = "Ok";
    [ObservableProperty] private string _ramLevel = "Ok";
    [ObservableProperty] private string _diskLevel = "Ok";
    [ObservableProperty] private string _cpuTooltip = "";
    [ObservableProperty] private string _ramTooltip = "";
    [ObservableProperty] private string _diskTooltip = "";

    private readonly DispatcherTimer _metricsTimer;

    /// <summary>Umbrales de color: gris &lt; 70 %, amarillo 70–89 %, rojo ≥ 90 %.</summary>
    private static string LevelFor(double percent) =>
        percent >= 90 ? "Crit" : percent >= 70 ? "Warn" : "Ok";

    private async Task PollMetricsAsync()
    {
        try
        {
            var m = await _api.GetSystemMetricsAsync();
            CpuLevel = LevelFor(m.CpuPercent);
            RamLevel = LevelFor(m.RamPercent);
            DiskLevel = LevelFor(m.DiskPercent);
            CpuTooltip = $"CPU del servidor: {m.CpuPercent:0}%";
            RamTooltip = $"RAM del servidor: {m.RamUsedGb:0.#} de {m.RamTotalGb:0.#} GB ({m.RamPercent:0}%)";
            DiskTooltip = $"Disco {m.DiskName} del servidor: {m.DiskUsedGb:0} de {m.DiskTotalGb:0} GB ({m.DiskPercent:0}%)";
            HasMetrics = true;
        }
        catch (ApiException)
        {
            HasMetrics = false; // servidor caído o sin el endpoint: se ocultan
        }
    }

    /// <summary>Envía la orden PTZ al canal del cuadro seleccionado (fuego y
    /// olvido; errores a la barra de estado). <paramref name="speed"/>
    /// sobreescribe la velocidad del panel (modo precisión del teclado).</summary>
    public async Task PtzAsync(PtzCommand command, bool stop, int? speed = null)
    {
        if (_ptzChannel is not { } node) return;
        try
        {
            await _api.PtzAsync(node.Device.Id, node.Channel.ChannelNumber, command, speed ?? PtzSpeed, stop);
        }
        catch (ApiException ex)
        {
            StatusMessage = $"PTZ: {ex.Message}";
        }
    }

    /// <summary>Número de preset activo en el panel PTZ (1..300).</summary>
    [ObservableProperty] private int _ptzPresetIndex = 1;

    /// <summary>Ir / guardar / borrar el preset del panel, con confirmación en la barra de estado.</summary>
    public async Task PtzPresetAsync(PtzPresetAction action)
    {
        if (_ptzChannel is not { } node) return;
        int index = Math.Clamp(PtzPresetIndex, 1, 300);
        PtzPresetIndex = index;
        try
        {
            await _api.PtzPresetAsync(node.Device.Id, node.Channel.ChannelNumber, action, index);
            StatusMessage = action switch
            {
                PtzPresetAction.Goto => $"PTZ: moviéndose al preset {index}.",
                PtzPresetAction.Set => $"PTZ: posición actual guardada como preset {index}.",
                PtzPresetAction.Clear => $"PTZ: preset {index} eliminado.",
                _ => StatusMessage,
            };
        }
        catch (ApiException ex)
        {
            StatusMessage = $"PTZ: {ex.Message}";
        }
    }

    private const string HintMessage =
        "Clic en la barra de un cuadro para seleccionarlo (borde azul); doble clic en un canal del árbol lo abre ahí.";

    private const string ReadyMessage = "Listo.";

    private const string LprHintMessage =
        "Reconocimiento de patentes: las lecturas llegan solas; elija una de la lista para ver su ficha completa.";

    private const string WallHintMessage =
        "Muro de video: arrastre un canal a una ventana; doble clic para pantalla completa.";

    public string UserLabel => $"{_api.Username} ({(_api.Role == "Admin" ? "Administrador" : "Operador")}) — {_api.BaseUrl}";

    public string WelcomeTitle => $"Bienvenido, {_api.Username}";

    public MainViewModel(ApiClient api, VmsHubClient hub)
    {
        _api = api;
        _hub = hub;

        _hub.ConfigChanged += entity =>
        {
            if (entity is "devices" or "channels")
                Application.Current.Dispatcher.InvokeAsync(() => _ = LoadTreeAsync());
        };
        _hub.DeviceStatusChanged += dto => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var node = Devices.FirstOrDefault(d => d.Device.Id == dto.Id);
            if (node is not null) node.Device = dto;
        });
        _hub.ConnectionStateChanged += ok => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            IsConnected = ok;
            ConnectionStatus = ok ? "Conectado" : "Reconectando…";
        });

        Playback = new PlaybackViewModel(api, _settings);
        // Los tramos exportados avisan igual que las capturas y cápsulas del vivo.
        Playback.MediaSaved += OnCellMediaSaved;

        Lpr = new LprViewModel(api, hub, _settings);
        Lpr.MediaSaved += OnCellMediaSaved;

        // Preferencia local: se abre con la última división que usó el usuario
        // (asíncrono: las celdas se crean por tandas sin congelar el arranque).
        _ = ApplyLayoutAsync(Layouts.FirstOrDefault(l => l.Name == _settings.LastLayout) ?? VideoLayout.Default);

        // Indicadores CPU/RAM/disco del servidor: sondeo liviano cada 5 s.
        _metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _metricsTimer.Tick += async (_, _) => await PollMetricsAsync();
        _metricsTimer.Start();
        _ = PollMetricsAsync();
    }

    public async Task LoadTreeAsync()
    {
        try
        {
            var devices = await _api.GetDevicesAsync();
            Devices.Clear();
            foreach (var device in devices)
            {
                var node = new DeviceNode(device);
                var channels = await _api.GetChannelsAsync(device.Id);
                foreach (var channel in channels.Where(c => c.Enabled))
                    node.Channels.Add(new ChannelNode(node, channel));
                Devices.Add(node);
            }
            ApplySearchFilter(); // el árbol nuevo debe respetar el filtro vigente
            StatusMessage = IsLiveViewOpen ? HintMessage : ReadyMessage;
        }
        catch (ApiException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>Divisiones disponibles en el selector (estilo iVMS-4200).</summary>
    public IReadOnlyList<VideoLayout> Layouts => VideoLayout.Standard;

    /// <summary>Cuadro maximizado con doble clic (−1 = grilla normal). Los
    /// demás cuadros quedan ocultos pero vivos: sus streams no se cortan.</summary>
    [ObservableProperty] private int _maximizedIndex = -1;

    /// <summary>Pantalla completa del área de la grilla: se ocultan barra de
    /// título, menú lateral, árbol, barra de herramientas y de estado.
    /// Se sale con Esc.</summary>
    [ObservableProperty] private bool _isGridFullscreen;

    /// <summary>Lista de dispositivos colapsada con el asa lateral (más
    /// espacio para la grilla sin salir de la vista normal).</summary>
    [ObservableProperty] private bool _isTreeCollapsed;

    [RelayCommand]
    private void EnterGridFullscreen() => IsGridFullscreen = true;

    /// <summary>Cuadro promovido a stream principal por estar maximizado (y el
    /// canal que tenía): al restaurar vuelve al secundario, pero SOLO si nadie
    /// lo cambió a mano ni le asignó otro canal en el intertanto.</summary>
    private VideoCellViewModel? _autoPromotedCell;
    private ChannelNode? _autoPromotedChannel;

    /// <summary>Doble clic en un cuadro: alterna entre verlo solo (división de
    /// 1 temporal) y la grilla anterior. Maximizar exige un cuadro con video;
    /// restaurar funciona siempre. Un cuadro en secundario se promueve al
    /// stream principal mientras está maximizado (a pantalla grande se nota la
    /// calidad) y regresa al secundario al restaurar.</summary>
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
                    // La vuelta al secundario es obligatoria (quedar en principal
                    // en un cuadro chico quema ancho de banda): si el cambio
                    // suave no lo logra, reapertura dura.
                    _ = cell.SwitchToProfileAsync(StreamProfile.Sub, hardFallbackOnFailure: true);
                else
                    // La promoción seguía en vuelo: se descarta antes de aterrizar.
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
            _ = cell.SwitchToProfileAsync(StreamProfile.Main);
        }
    }

    /// <summary>División activa de la grilla (el panel de video la dibuja).</summary>
    [ObservableProperty] private VideoLayout _currentLayout = VideoLayout.Default;

    /// <summary>Cambio de división en curso (los clics rápidos del selector se
    /// ignoran mientras se aplica el anterior).</summary>
    private bool _layoutBusy;

    [RelayCommand]
    private async Task SelectLayoutAsync(VideoLayout layout)
    {
        if (_layoutBusy) return;
        _layoutBusy = true;
        try { await ApplyLayoutAsync(layout); }
        finally { _layoutBusy = false; }
        // La elección del usuario se recuerda para la próxima sesión.
        if (_settings.LastLayout != layout.Name)
        {
            _settings.LastLayout = layout.Name;
            _settings.Save();
        }
    }

    /// <summary>
    /// Cambia la división conservando lo que quepa (las celdas sobrantes se
    /// liberan). Crear o destruir players es CARO (superficies de GPU): se
    /// hace por tandas cediendo ciclos al dispatcher, así la UI sigue viva
    /// (sin el "no responde" de Windows) y los cuadros aparecen progresivos.
    /// </summary>
    public async Task ApplyLayoutAsync(VideoLayout layout)
    {
        MaximizedIndex = -1; // los índices cambian con la división
        CurrentLayout = layout;
        int count = layout.CellCount;
        // Cediendo un ciclo por CADA celda: liberar un player con video andando
        // puede tomar cientos de milisegundos y el bombeo de mensajes debe
        // seguir corriendo entre uno y otro.
        while (Cells.Count > count)
        {
            var cell = Cells[^1];
            Cells.RemoveAt(Cells.Count - 1);
            cell.AudioActivated -= OnCellAudioActivated;
            cell.MediaSaved -= OnCellMediaSaved;
            cell.Dispose();
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
        while (Cells.Count < count)
        {
            var cell = new VideoCellViewModel(_api, _settings);
            cell.AudioActivated += OnCellAudioActivated;
            cell.MediaSaved += OnCellMediaSaved;
            Cells.Add(cell);
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
        for (int i = 0; i < Cells.Count; i++)
            Cells[i].Index = i + 1;
        if (SelectedCell is not null && !Cells.Contains(SelectedCell))
            SelectedCell = null;
    }

    /// <summary>
    /// Abre un canal: en el cuadro seleccionado, o en el primero libre, o en
    /// el primero de la grilla si está todo ocupado. Después la selección
    /// avanza al cuadro siguiente (como SmartPSS: doble clics consecutivos van
    /// llenando la grilla). Grillas grandes usan el perfil sub (estándar VMS
    /// para cuidar el ancho de banda del equipo).
    /// </summary>
    public async Task OpenChannelAsync(ChannelNode node)
    {
        var cell = SelectedCell
            ?? Cells.FirstOrDefault(c => c.IsEmpty)
            ?? Cells.FirstOrDefault();
        if (cell is null) return;

        // La selección avanza ANTES del await: el usuario puede seguir
        // abriendo canales mientras este cuadro conecta.
        int next = Cells.IndexOf(cell) + 1;
        SelectedCell = next < Cells.Count ? Cells[next] : null;

        await cell.OpenAsync(node, DefaultProfileForOpen());
    }

    /// <summary>Abre un canal en un cuadro específico (drag & drop del árbol a
    /// la grilla): sin avanzar la selección.</summary>
    public async Task OpenChannelInCellAsync(ChannelNode node, VideoCellViewModel cell) =>
        await cell.OpenAsync(node, DefaultProfileForOpen());

    /// <summary>
    /// Arrastrar un cuadro sobre otro (estilo iVMS-4200): las cámaras cambian
    /// de ubicación en la grilla. Si el destino tiene video se intercambian; si
    /// está libre, la cámara se muda y el origen queda libre. Los números de
    /// cuadro no se mueven — son la posición — y el video NO se corta: viaja el
    /// player, no el canal, así que también funciona entre cuadros de distinto
    /// tamaño (por ejemplo el cuadro grande de las divisiones asimétricas).
    /// </summary>
    public void SwapCells(VideoCellViewModel source, VideoCellViewModel target)
    {
        if (ReferenceEquals(source, target)) return;
        if (!Cells.Contains(source) || !Cells.Contains(target)) return;
        if (source.IsEmpty && target.IsEmpty) return;

        bool exchange = !target.IsEmpty;
        string moved = source.Title ?? "";
        string displaced = target.Title ?? "";
        int from = source.Index, to = target.Index;

        VideoCellViewModel.SwapContent(source, target);

        // La selección (y con ella el panel PTZ) sigue a la cámara movida.
        SelectedCell = target;
        StatusMessage = exchange
            ? $"Cuadros {from} y {to} intercambiados: \"{moved}\" ↔ \"{displaced}\"."
            : $"\"{moved}\" movida del cuadro {from} al {to}.";
    }

    /// <summary>
    /// Doble clic en un equipo del árbol: abre TODOS sus canales habilitados y
    /// adapta la división a la más chica donde quepan (estilo iVMS-4200). Los
    /// cuadros sobrantes de la división elegida quedan libres.
    /// </summary>
    /// <summary>Apertura masiva en curso (indicador en la barra de herramientas:
    /// crear decenas de players congela la UI unos segundos y sin aviso parece
    /// que la aplicación se colgó).</summary>
    [ObservableProperty] private bool _isBulkOpening;
    [ObservableProperty] private string _bulkOpeningText = "";

    public async Task OpenDeviceAsync(DeviceNode device)
    {
        var channels = device.Channels.ToList();
        if (channels.Count == 0)
        {
            StatusMessage = $"\"{device.Device.Name}\" no tiene canales habilitados.";
            return;
        }

        if (channels.Count > 64)
        {
            StatusMessage = $"\"{device.Device.Name}\" tiene {channels.Count} canales: se abren los primeros 64.";
            channels = channels.Take(64).ToList();
        }

        IsBulkOpening = true;
        BulkOpeningText = $"Abriendo {channels.Count} canal(es) de \"{device.Device.Name}\"…";
        StatusMessage = BulkOpeningText;
        // Bloquea la interfaz mientras dura: aviso centrado (ventana propia y
        // Topmost, sobre el video) y la ventana principal deshabilitada.
        var loading = Application.Current.MainWindow is { IsLoaded: true } owner
            ? Views.LoadingWindow.Open(owner, BulkOpeningText)
            : null;
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            // Que el aviso alcance a pintarse antes del trabajo pesado.
            await Dispatcher.Yield(DispatcherPriority.Render);

            if (_settings.FitGridToDevice)
            {
                // Grilla a medida (sin cuadros de sobra). No queda como división
                // recordada: no es una de las estándar del selector.
                await ApplyLayoutAsync(VideoLayout.FitFor(channels.Count));
            }
            else
            {
                var layout = Layouts.FirstOrDefault(l => l.CellCount >= channels.Count) ?? Layouts[^1];
                if (CurrentLayout != layout)
                {
                    await ApplyLayoutAsync(layout);
                    _settings.LastLayout = layout.Name; // división estándar: sí se recuerda
                    _settings.Save();
                }
            }

            SelectedCell = null;
            var profile = DefaultProfileForOpen();
            var openings = new List<Task>();
            for (int i = 0; i < Cells.Count; i++)
            {
                if (i < channels.Count)
                    openings.Add(Cells[i].OpenAsync(channels[i], profile));
                else
                    Cells[i].Clear();
            }
            await Task.WhenAll(openings);
            StatusMessage = $"{channels.Count} canal(es) de \"{device.Device.Name}\" en pantalla.";
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
            loading?.Finish();
            IsBulkOpening = false;
        }
    }

    /// <summary>Stream al abrir un canal según Configuración → Video: fijo, o
    /// automático (principal en grillas chicas, secundario en grandes).</summary>
    private StreamProfile DefaultProfileForOpen() => _settings.DefaultProfile switch
    {
        "main" => StreamProfile.Main,
        "sub" => StreamProfile.Sub,
        _ => Cells.Count <= 4 ? StreamProfile.Main : StreamProfile.Sub,
    };

    /// <summary>Captura o cápsula guardada: notificación flotante con el link
    /// a la ubicación del archivo.</summary>
    private void OnCellMediaSaved(string title, string glyph, string path) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (Application.Current.MainWindow is { IsLoaded: true } owner)
                Views.ToastWindow.ShowSaved(owner, title, glyph, path);
        });

    /// <summary>Audio exclusivo: encender el audio de un cuadro apaga el resto.</summary>
    private void OnCellAudioActivated(VideoCellViewModel active)
    {
        foreach (var cell in Cells)
            if (cell != active && cell.IsAudioOn)
                cell.IsAudioOn = false;
    }

    [RelayCommand]
    public void ClearAll()
    {
        foreach (var cell in Cells)
            cell.Clear();
    }

    public void Shutdown()
    {
        _metricsTimer.Stop();
        foreach (var cell in Cells)
            cell.Dispose();
        Playback.Dispose();
        _ = _hub.DisposeAsync();
    }
}
