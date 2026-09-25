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
    /// <summary>Alarma sonora de las automatizaciones en este equipo.</summary>
    private readonly AlertSoundPlayer _alertSound;
    /// <summary>Sirena de alarma de cerco (una por puesto) y los paneles que la mantienen sonando.</summary>
    private readonly CercoSiren _cercoSiren = new();
    private readonly HashSet<int> _cercoSounding = [];
    /// <summary>Aviso en pantalla de cada panel (id negativo: no choca con las alertas de automatizaciones).</summary>
    private readonly Dictionary<int, long> _cercoToast = [];
    private long _cercoToastSeq;
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

    /// <summary>Ajustes locales compartidos con las pantallas auxiliares (sus
    /// cuadros usan las mismas carpetas, formato y stream por defecto).</summary>
    internal ClientSettings Settings => _settings;

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

    /// <summary>La viñeta "Paneles de alarma" existe en el navbar.</summary>
    [ObservableProperty] private bool _isAlarmsOpen;

    /// <summary>
    /// Módulo Paneles de alarma. Se carga al arrancar (no al abrir la viñeta)
    /// para que el icono del riel avise de una alarma aunque el operador
    /// nunca haya entrado al módulo.
    /// </summary>
    public AlarmsViewModel Alarms { get; }

    /// <summary>Parlantes IP (panel de la Vista en Vivo: hablar, sonidos, biblioteca, texto a voz).</summary>
    public SpeakersViewModel Speakers { get; }

    /// <summary>
    /// Citofonía. Vive desde el arranque: la llamada de un frente tiene que
    /// sonar y abrir su ventana aunque la viñeta del módulo esté cerrada.
    /// </summary>
    public IntercomsViewModel Intercom { get; }

    [ObservableProperty] private bool _isIntercomOpen;

    [RelayCommand]
    private void OpenIntercom()
    {
        IsIntercomOpen = true;
        ActiveSection = "Intercom";
        StatusMessage = "Citofonía: las llamadas suenan y se abren solas; desde aquí puede ver y hablarle a un frente o abrir su puerta.";
        _ = Intercom.LoadHistoryCommand.ExecuteAsync(null);
    }

    /// <summary>Cerrar la viñeta no apaga nada: las llamadas siguen sonando en este puesto.</summary>
    [RelayCommand]
    private void CloseIntercom()
    {
        IsIntercomOpen = false;
        ActiveSection = "Home";
        StatusMessage = ReadyMessage;
    }

    /// <summary>Abre la ventana de un frente (con la llamada que suena, si la hay).</summary>
    private void ShowIntercomWindow(Core.Contracts.IntercomDto intercom, Core.Contracts.IntercomCallDto? call) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var owner = Application.Current.MainWindow is { IsLoaded: true } main ? main : null;
            Views.IntercomCallWindow.Show(owner, _api, _settings, Intercom,
                id => Devices.SelectMany(d => d.Channels).FirstOrDefault(c => c.Channel.Id == id),
                intercom, call);
        });

    [RelayCommand]
    private void OpenAlarms()
    {
        IsAlarmsOpen = true;
        ActiveSection = "Alarms";
        StatusMessage = AlarmsHintMessage;
        _ = Alarms.InitializeAsync();
    }

    /// <summary>Cerrar la viñeta solo saca el módulo de la vista: el servidor
    /// sigue conectado a los paneles y las alarmas siguen avisando.</summary>
    [RelayCommand]
    private void CloseAlarms()
    {
        IsAlarmsOpen = false;
        ActiveSection = "Home";
        StatusMessage = ReadyMessage;
    }

    /// <summary>La viñeta "Cerco eléctrico" existe en el navbar.</summary>
    [ObservableProperty] private bool _isCercoOpen;

    /// <summary>
    /// Módulo Cerco eléctrico. Vive desde el arranque: una alarma de cerco tiene
    /// que sonar y avisar aunque la viñeta nunca se haya abierto.
    /// </summary>
    public CercoViewModel Cerco { get; }

    [RelayCommand]
    private void OpenCerco()
    {
        IsCercoOpen = true;
        ActiveSection = "Cerco";
        StatusMessage = CercoHintMessage;
        _ = Cerco.InitializeAsync();
    }

    /// <summary>Cerrar la viñeta no apaga nada: las alarmas de cerco siguen sonando en este puesto.</summary>
    [RelayCommand]
    private void CloseCerco()
    {
        IsCercoOpen = false;
        ActiveSection = "Home";
        StatusMessage = ReadyMessage;
    }

    /// <summary>
    /// Alarma de un panel de cerco: sirena en este puesto + aviso flotante que se
    /// queda hasta que el operador pulse "Enterado", se esté donde se esté.
    /// </summary>
    private void OnCercoAlarm(Core.Contracts.CercoEventDto dto)
    {
        StatusMessage = $"ALARMA DE CERCO: {dto.PanelName} — {dto.Description}";
        _cercoSounding.Add(dto.PanelId);
        _cercoSiren.Start();
        long toastId = -(++_cercoToastSeq);
        _cercoToast[dto.PanelId] = toastId;
        if (Application.Current.MainWindow is { IsLoaded: true } owner)
            Views.ToastWindow.ShowAlertWithAck(owner, toastId, $"Alarma de cerco · {dto.PanelName}",
                $"{dto.Description}\n{dto.ReceivedAt.ToLocalTime():HH:mm:ss}", "\uE945", null,
                () =>
                {
                    // "Enterado" apaga el sonido en este puesto (la sirena del panel se
                    // silencia con el botón Silenciar del módulo).
                    _cercoSounding.Clear();
                    _cercoSiren.Stop();
                    return Task.CompletedTask;
                });
    }

    /// <summary>Se silenció o desarmó el panel (desde aquí, otro puesto o el control): calla la sirena local.</summary>
    private void OnCercoAlarmHandled(int panelId, bool disarmed)
    {
        _cercoSounding.Remove(panelId);
        if (_cercoSounding.Count == 0) _cercoSiren.Stop();
        if (disarmed && _cercoToast.Remove(panelId, out long toastId)) Views.ToastWindow.DismissAlert(toastId);
    }

    /// <summary>Alarma crítica de un panel: aviso flotante + barra de estado,
    /// se esté donde se esté (la viñeta puede estar cerrada).</summary>
    private void OnAlarmRaised(Core.Contracts.AlarmEventDto dto)
    {
        string where = string.Join(" · ", new[] { dto.PanelName, dto.AreaName, dto.ZoneName }
            .Where(s => !string.IsNullOrWhiteSpace(s))!);
        StatusMessage = $"ALARMA: {dto.Description} ({where})";
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (Application.Current.MainWindow is { IsLoaded: true } owner)
                Views.ToastWindow.ShowAlert(owner, $"Alarma · {dto.PanelName}",
                    dto.Description + (where.Length > 0 ? $"\n{where}" : "") +
                    $"\n{dto.Timestamp.ToLocalTime():HH:mm:ss}");
        });
    }

    /// <summary>
    /// Alertas de automatizaciones pendientes de confirmar en este puesto. Se
    /// muestra la más reciente; al confirmarla aparece la siguiente, así una
    /// tanda de alertas no deja ninguna sin acuse de recibo.
    /// </summary>
    private readonly List<Core.Contracts.WorkflowAlertDto> _pendingAlerts = [];

    /// <summary>
    /// Aviso pedido por una automatización del servidor: sonido, barra de
    /// estado y notificación flotante con la foto que capturó (la imagen se
    /// descarga aparte porque por el hub solo viaja su ruta). Si el aviso
    /// exige acuse de recibo, se encola y NO se cierra solo.
    /// </summary>
    private void OnWorkflowNotification(Core.Contracts.WorkflowNotificationDto dto)
    {
        StatusMessage = $"{dto.Title}: {dto.Message}";
        // El sonido arranca de inmediato, sin esperar a que baje la foto: es
        // lo que hace que el operador levante la vista.
        _ = _alertSound.PlayAsync(dto.Sound, dto.SoundRepeat);

        if (dto.RequiresAck && dto.AlertId > 0)
        {
            // El aviso del hub es liviano: el detalle completo (todas las fotos
            // y la ejecución que la generó) se lee de la API para la ventana.
            _ = Task.Run(async () =>
            {
                var alert = await _api.GetAlertAsync(dto.AlertId);
                // Respaldo si la API no contesta: con lo que trae el aviso del
                // hub alcanza para mostrar la alerta y confirmarla.
                QueueAlert(alert ?? new Core.Contracts.WorkflowAlertDto(dto.AlertId, null, dto.WorkflowId,
                    dto.WorkflowName, dto.At, dto.Title, dto.Message, dto.Severity, dto.ImagePath,
                    dto.ImagePath is { Length: > 0 } one ? [one] : [], [], dto.Sound, dto.SoundRepeat,
                    "", true, null, null, null, null));
            });
            return;
        }

        _ = Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            byte[]? image = dto.ImagePath is { Length: > 0 }
                ? await _api.GetWorkflowImageAsync(dto.ImagePath)
                : null;
            if (Application.Current.MainWindow is { IsLoaded: true } owner)
                Views.ToastWindow.ShowAlert(owner, dto.Title, dto.Message, GlyphOf(dto.Severity), image);
        });
    }

    private static string GlyphOf(Core.Contracts.AlarmSeverity severity) =>
        severity == Core.Contracts.AlarmSeverity.Info ? "\uE783" : "\uE814";

    /// <summary>Suma una alerta a la cola (sin repetir) y muestra la más reciente.</summary>
    private void QueueAlert(Core.Contracts.WorkflowAlertDto alert) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_pendingAlerts.Any(a => a.Id == alert.Id)) return;
            _pendingAlerts.Add(alert);
            ShowPendingCount();
            _ = ShowTopAlertAsync();
        });

    /// <summary>
    /// Abre (o actualiza) la ventana de alarma con todas las pendientes,
    /// dejando arriba la más reciente.
    /// </summary>
    private Task ShowTopAlertAsync()
    {
        var pending = _pendingAlerts.Where(a => a.Pending).ToList();
        if (pending.Count == 0)
        {
            Views.AlertWindow.CloseIfDone();
            return Task.CompletedTask;
        }
        if (Application.Current.MainWindow is not { IsLoaded: true } owner)
        {
            // Arranque del cliente: la ventana principal todavía no existe. Las
            // alertas pendientes no se pueden perder por eso: se reintenta.
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ =>
                Application.Current?.Dispatcher.InvokeAsync(() => _ = ShowTopAlertAsync()));
            return Task.CompletedTask;
        }
        // La ventana necesita resolver la cámara vinculada contra el árbol de
        // dispositivos que este cliente ya tiene cargado.
        Views.AlertWindow.Show(owner, _api, _settings, _alertSound,
            id => Devices.SelectMany(d => d.Channels).FirstOrDefault(c => c.Channel.Id == id),
            pending, pending[^1].Id);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Alguien confirmó la alerta (este puesto u otro): se refleja en la
    /// ventana de alarma y se saca de las pendientes.
    /// </summary>
    private void OnAlertAcknowledged(Core.Contracts.WorkflowAlertDto alert) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            int position = _pendingAlerts.FindIndex(a => a.Id == alert.Id);
            if (position >= 0) _pendingAlerts[position] = alert;
            // Confirmada (aquí o en otro puesto): la alarma sonora se calla.
            _alertSound.Stop();
            Views.AlertWindow.Update(alert);
            _pendingAlerts.RemoveAll(a => a.Id == alert.Id);
            ShowPendingCount();
            StatusMessage = $"Alerta «{alert.Title}» confirmada por {alert.AcknowledgedBy}.";
        });

    private void ShowPendingCount()
    {
        PendingAlertCount = _pendingAlerts.Count(a => a.Pending);
        if (PendingAlertCount > 0)
            StatusMessage = $"{PendingAlertCount} alerta(s) sin confirmar.";
    }

    /// <summary>
    /// Alertas que siguen sin acuse de recibo en este puesto. El riel lo usa
    /// para encender el botón del Centro de eventos y mostrar la insignia.
    /// </summary>
    [ObservableProperty] private int _pendingAlertCount;

    /// <summary>Hay al menos una alerta sin confirmar.</summary>
    public bool HasPendingAlerts => PendingAlertCount > 0;

    partial void OnPendingAlertCountChanged(int value) => OnPropertyChanged(nameof(HasPendingAlerts));

    /// <summary>
    /// Trae las alertas que quedaron sin confirmar (al abrir el cliente o al
    /// recuperar la conexión): si el puesto estaba cerrado cuando se emitió la
    /// alerta, igual hay que darse por enterado.
    /// </summary>
    public async Task LoadPendingAlertsAsync()
    {
        try
        {
            if (await _api.GetAlertsAsync(pendingOnly: true, take: 20) is not { } list) return;
            foreach (var alert in list.Items.OrderBy(a => a.RaisedAt))
                QueueAlert(alert);
        }
        catch (Exception)
        {
            // Sin conexión ahora; al reconectar se vuelve a intentar.
        }
    }

    /// <summary>La viñeta "Reconocimiento de patentes" existe en el navbar.</summary>
    [ObservableProperty] private bool _isLprOpen;

    /// <summary>
    /// Aplicaciones → Reconocimiento de patentes. El historial se carga una
    /// sola vez: después el módulo se mantiene solo con lo que empuja el hub,
    /// así que volver a abrir la viñeta no vuelve a golpear la API.
    /// </summary>
    public LprViewModel Lpr { get; }

    /// <summary>Centro de eventos (historial de alertas de automatizaciones).</summary>
    public EventCenterViewModel Events { get; }

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

    // ---------- Centro de descargas ----------

    /// <summary>Cola de exportaciones de grabaciones. Vive en el shell (no en
    /// la ventana): las descargas siguen aunque el centro esté cerrado.</summary>
    public DownloadCenterViewModel Downloads { get; }

    private Views.DownloadCenterWindow? _downloadWindow;
    private Views.EventCenterWindow? _eventWindow;

    /// <summary>La viñeta "Centro de eventos" existe en el navbar.</summary>
    [ObservableProperty] private bool _isEventsOpen;

    /// <summary>
    /// Centro de eventos: historial de las alertas con su acuse de recibo.
    /// Vive DENTRO del sistema como una sección más; el botón "Ventana aparte"
    /// lo saca a su propia ventana (misma vista y mismo ViewModel).
    /// </summary>
    [RelayCommand]
    private void OpenEventCenter()
    {
        if (_eventWindow is not null)
        {
            // Ya está afuera: se trae al frente en vez de duplicarlo.
            if (_eventWindow.WindowState == WindowState.Minimized)
                _eventWindow.WindowState = WindowState.Normal;
            _eventWindow.Activate();
            return;
        }
        IsEventsOpen = true;
        ActiveSection = "Events";
        StatusMessage = "Centro de eventos: historial de alertas y su acuse de recibo.";
        _ = Events.LoadAsync();
    }

    /// <summary>Cerrar la viñeta solo saca el módulo de la vista; el historial sigue en el servidor.</summary>
    [RelayCommand]
    private void CloseEvents()
    {
        IsEventsOpen = false;
        ActiveSection = "Home";
        StatusMessage = ReadyMessage;
    }

    /// <summary>
    /// Saca el Centro de eventos a una ventana independiente (para dejarlo en
    /// otro monitor). Al cerrarla, el módulo vuelve al panel principal.
    /// </summary>
    private void PopOutEventCenter()
    {
        if (_eventWindow is not null) { _eventWindow.Activate(); return; }

        Events.IsPoppedOut = true;
        IsEventsOpen = false;
        if (ActiveSection == "Events") ActiveSection = "Home";

        _eventWindow = new Views.EventCenterWindow(Events) { Owner = Application.Current.MainWindow };
        _eventWindow.Closed += (_, _) =>
        {
            _eventWindow = null;
            Events.IsPoppedOut = false;
            // Al cerrar la ventana, el módulo vuelve a su viñeta.
            IsEventsOpen = true;
            ActiveSection = "Events";
        };
        _eventWindow.Show();
    }

    /// <summary>Abre (o trae al frente) la ventana NO modal del Centro de
    /// descargas. Instancia única: la lista es la misma se abra cuando se abra.</summary>
    [RelayCommand]
    private void OpenDownloadCenter()
    {
        if (_downloadWindow is null)
        {
            _downloadWindow = new Views.DownloadCenterWindow(Downloads) { Owner = Application.Current.MainWindow };
            _downloadWindow.Closed += (_, _) => _downloadWindow = null;
            _downloadWindow.Show();
            return;
        }
        if (_downloadWindow.WindowState == WindowState.Minimized)
            _downloadWindow.WindowState = WindowState.Normal;
        _downloadWindow.Activate();
    }

    /// <summary>Ventana de Configuración. Edita la MISMA instancia de ajustes
    /// que usan las celdas (carpetas, formato, stream por defecto): al guardar,
    /// los cambios rigen de inmediato sin reiniciar.</summary>
    [RelayCommand]
    private void OpenSettings()
    {
        var window = new Views.SettingsWindow(_settings, _api) { Owner = Application.Current.MainWindow };
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

    // ---------- Aviso de licencia (prueba por vencer, gracia, restricción) ----------

    [ObservableProperty] private string _licenseWarning = "";
    [ObservableProperty] private bool _hasLicenseWarning;
    [ObservableProperty] private bool _licenseRestricted;
    private readonly DispatcherTimer _licenseTimer;

    /// <summary>Consulta el estado de licencia del servidor (cada 5 min y al
    /// arrancar). Un servidor sin el endpoint (versión vieja) no muestra nada.</summary>
    private async Task PollLicenseAsync()
    {
        try
        {
            var s = await _api.GetLicenseAsync();
            LicenseWarning = s.Warning ?? "";
            HasLicenseWarning = !string.IsNullOrEmpty(s.Warning);
            LicenseRestricted = !s.Operational;
        }
        catch (ApiException)
        {
            // servidor caído o sin licenciamiento: no se inventa un aviso
        }
    }

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

    private const string AlarmsHintMessage =
        "Paneles de alarma: seleccione un panel para ver sus áreas y zonas; armar, desarmar y anular zonas queda auditado.";

    private const string CercoHintMessage =
        "Cerco eléctrico: armar, desarmar y silenciar quedan auditados; las alarmas suenan aunque cierre esta viñeta.";

    private const string WallHintMessage =
        "Muro de video: arrastre un canal a una ventana; doble clic para pantalla completa.";

    /// <summary>Nombre del usuario en el navbar: es el botón que despliega el
    /// menú de la sesión (el rol y el servidor viven dentro de ese menú).</summary>
    public string UserLabel => _api.Username ?? "";

    /// <summary>Rol de la sesión, para el menú del usuario.</summary>
    public string RoleLabel => _api.Role == "Admin" ? "Administrador" : "Operador";

    /// <summary>Servidor al que está conectada esta sesión (menú del usuario).</summary>
    public string ServerLabel => _api.BaseUrl ?? "";

    public string WelcomeTitle => $"Bienvenido, {_api.Username}";

    public MainViewModel(ApiClient api, VmsHubClient hub)
    {
        _api = api;
        _hub = hub;
        _alertSound = new AlertSoundPlayer(api);

        _hub.ConfigChanged += entity =>
        {
            if (entity is "devices" or "channels")
                Application.Current.Dispatcher.InvokeAsync(() => _ = LoadTreeAsync());
            if (entity is "license")
                Application.Current.Dispatcher.InvokeAsync(() => _ = PollLicenseAsync());
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
            // Al recuperar la conexión pueden haber quedado alertas sin confirmar.
            if (ok) _ = LoadPendingAlertsAsync();
        });
        // Automatizaciones del servidor: el aviso llega ya resuelto (título,
        // mensaje y, si la automatización capturó una, la foto del hecho).
        _hub.WorkflowNotification += OnWorkflowNotification;
        _hub.WorkflowAlertAcknowledged += OnAlertAcknowledged;

        // El Centro de descargas atiende la cola aunque su ventana esté cerrada;
        // al encolar desde Reproducción, la ventana se muestra sola (no modal).
        Downloads = new DownloadCenterViewModel(api);
        Downloads.MediaSaved += OnCellMediaSaved;
        Downloads.JobEnqueued += () => Application.Current.Dispatcher.InvokeAsync(OpenDownloadCenter);

        Playback = new PlaybackViewModel(api, _settings, Downloads,
            () => Devices.SelectMany(d => d.Channels));
        // Los tramos exportados avisan igual que las capturas y cápsulas del vivo.
        Playback.MediaSaved += OnCellMediaSaved;

        Lpr = new LprViewModel(api, hub, _settings);
        Lpr.MediaSaved += OnCellMediaSaved;

        Events = new EventCenterViewModel(api, hub);
        Events.PopOutRequested += () => Application.Current.Dispatcher.InvokeAsync(PopOutEventCenter);
        // "Ver" en el centro de eventos abre la misma ventana de alarma.
        Events.OpenRequested += alert => Application.Current.Dispatcher.InvokeAsync(() =>
            Views.AlertWindow.Show(Application.Current.MainWindow, _api, _settings, _alertSound,
                id => Devices.SelectMany(d => d.Channels).FirstOrDefault(c => c.Channel.Id == id),
                [alert], alert.Id));

        Alarms = new AlarmsViewModel(api, hub);
        Alarms.AlarmRaised += OnAlarmRaised;

        Cerco = new CercoViewModel(api, hub);
        Cerco.AlarmRaised += OnCercoAlarm;
        Cerco.AlarmHandled += OnCercoAlarmHandled;
        _ = Cerco.InitializeAsync();

        Speakers = new SpeakersViewModel(api, hub, _settings);
        _ = Speakers.InitializeAsync();

        Intercom = new IntercomsViewModel(api, hub);
        Intercom.CallRinging += call =>
        {
            StatusMessage = $"Llamada de citofonía: {call.IntercomName}";
            if (Intercom.Find(call.IntercomId) is { } intercom) ShowIntercomWindow(intercom, call);
        };
        Intercom.WindowRequested += intercom => ShowIntercomWindow(intercom, intercom.ActiveCall);
        _ = Intercom.InitializeAsync();
        _ = Alarms.InitializeAsync();

        // Alertas que quedaron pendientes mientras este puesto estaba cerrado.
        _ = LoadPendingAlertsAsync();

        // Preferencia local: se abre con la última división que usó el usuario
        // (asíncrono: las celdas se crean por tandas sin congelar el arranque).
        _ = ApplyLayoutAsync(Layouts.FirstOrDefault(l => l.Name == _settings.LastLayout) ?? VideoLayout.Default);

        // Indicadores CPU/RAM/disco del servidor: sondeo liviano cada 5 s.
        _metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _metricsTimer.Tick += async (_, _) => await PollMetricsAsync();
        _metricsTimer.Start();
        _ = PollMetricsAsync();

        // Estado de licencia: aviso en la barra de estado (cada 5 min y cuando
        // el servidor anuncia un cambio por el hub).
        _licenseTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _licenseTimer.Tick += async (_, _) => await PollLicenseAsync();
        _licenseTimer.Start();
        _ = PollLicenseAsync();
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
            RefreshLiveChannels(); // y marcar lo que ya está en pantalla
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
    private void EnterGridFullscreen()
    {
        // La barra de herramientas se oculta: su desplegable de vistas no
        // puede quedar flotando sobre el video.
        IsViewsPopupOpen = false;
        IsGridFullscreen = true;
    }

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
            // El secundario queda estacionado: restaurar es instantáneo.
            _ = cell.SwitchToProfileAsync(StreamProfile.Main, keepCurrentForRestore: true);
        }
    }

    /// <summary>Cambiar de división deshace el "maximizado" sin restaurar el
    /// stream (el cuadro sigue en principal): el secundario estacionado ya no
    /// tiene a qué volver y se libera para no gastar una sesión de más.</summary>
    private void ForgetPromotion()
    {
        _autoPromotedCell?.ReleaseParked();
        _autoPromotedCell = null;
        _autoPromotedChannel = null;
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
        ForgetPromotion();
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
            cell.AssignedChannelChanged -= RefreshLiveChannels;
            cell.Dispose();
            await BreatheAsync();
        }
        while (Cells.Count < count)
        {
            var cell = new VideoCellViewModel(_api, _settings);
            cell.AudioActivated += OnCellAudioActivated;
            cell.MediaSaved += OnCellMediaSaved;
            cell.AssignedChannelChanged += RefreshLiveChannels;
            Cells.Add(cell);
            await BreatheAsync();
        }
        for (int i = 0; i < Cells.Count; i++)
            Cells[i].Index = i + 1;
        if (SelectedCell is not null && !Cells.Contains(SelectedCell))
            SelectedCell = null;
        RefreshLiveChannels(); // cuadros liberados y números de cuadro nuevos
    }

    /// <summary>Tope de la pausa entre celda y celda (ver BreatheAsync).</summary>
    private static readonly TimeSpan BreathTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Cede el turno al dispatcher para que la interfaz alcance a pintar entre
    /// celda y celda, pero CON TOPE. Background es de las prioridades más bajas
    /// de la cola: cualquier trabajo continuo por encima (el render de una
    /// animación, por ejemplo la barra indeterminada del aviso de apertura
    /// masiva) puede dejarla sin turno indefinidamente. Una espera Background
    /// pura se quedaba entonces sin despertar, el bucle de celdas no avanzaba
    /// más y la aplicación quedaba congelada con el aviso puesto — que además
    /// bloquea la entrada. El respaldo por tiempo despierta en prioridad Normal
    /// (por encima del render), así que el bucle SIEMPRE avanza.
    /// </summary>
    internal static async Task BreatheAsync()
    {
        var pumped = Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task;
        await Task.WhenAny(pumped, Task.Delay(BreathTimeout));
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
        // El destino es de esta grilla (aquí se soltó); el origen puede venir
        // de una pantalla auxiliar: el player viaja entre ventanas con el
        // video andando, igual que dentro de la misma grilla.
        if (!Cells.Contains(target)) return;
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
    /// preparar los cuadros y enganchar los streams toma unos segundos y sin
    /// aviso parece que la aplicación se colgó).</summary>
    [ObservableProperty] private bool _isBulkOpening;
    [ObservableProperty] private string _bulkOpeningText = "";

    /// <summary>Canales que se abren juntos en la apertura masiva.</summary>
    private const int OpenBatchSize = 4;

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
            // Si ninguna división estándar da abasto (más canales que cuadros),
            // entran los que quepan: el resto de la grilla queda libre.
            int openCount = Math.Min(channels.Count, Cells.Count);
            for (int i = openCount; i < Cells.Count; i++)
                Cells[i].Clear();

            // Por tandas y no los N de golpe: cada apertura crea el player del
            // cuadro (un dispositivo Direct3D) y lanza un pull RTSP hacia el
            // equipo. Repartido, la interfaz respira entre tanda y tanda, el
            // aviso muestra el avance y el grabador no recibe quince conexiones
            // en el mismo instante (los equipos limitan sesiones simultáneas).
            for (int i = 0; i < openCount; i += OpenBatchSize)
            {
                int upTo = Math.Min(i + OpenBatchSize, openCount);
                var wave = new List<Task>();
                for (int j = i; j < upTo; j++)
                    wave.Add(Cells[j].OpenAsync(channels[j], profile));
                await Task.WhenAll(wave);
                BulkOpeningText = $"Abriendo canales de \"{device.Device.Name}\"… {upTo}/{openCount}";
                StatusMessage = BulkOpeningText;
                loading?.Update(BulkOpeningText);
                await BreatheAsync();
            }
            StatusMessage = $"{openCount} canal(es) de \"{device.Device.Name}\" en pantalla.";
        }
        catch (Exception ex)
        {
            // Abrir video toca driver, GPU y red: una falla ahí NO puede
            // llevarse la aplicación. El doble clic del árbol es async void y
            // la excepción no tendría dónde caer — se cierra el proceso.
            StatusMessage = $"No se pudieron abrir todos los canales de \"{device.Device.Name}\": {ex.Message}";
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

    /// <summary>Audio exclusivo en TODA la aplicación: encender el audio de un
    /// cuadro apaga el resto, incluidos los de las pantallas auxiliares (por
    /// eso lo comparten sus grillas: una sola cámara suena a la vez).</summary>
    internal void OnCellAudioActivated(VideoCellViewModel active)
    {
        foreach (var cell in Cells.Concat(_auxWindows.SelectMany(w => w.Vm.Cells)))
            if (cell != active && cell.IsAudioOn)
                cell.IsAudioOn = false;
    }

    /// <summary>
    /// Marca en el árbol los canales que están en algún cuadro de la vista en
    /// vivo — grilla principal y pantallas auxiliares, que comparten el árbol —
    /// y deja en el tooltip en qué cuadro(s). Se compara por Id de canal y no
    /// por nodo: recargar el árbol crea nodos nuevos mientras los cuadros
    /// siguen mostrando los anteriores.
    /// </summary>
    internal void RefreshLiveChannels()
    {
        var locations = new Dictionary<int, List<string>>();
        void Collect(IEnumerable<VideoCellViewModel> cells, string screen)
        {
            foreach (var cell in cells)
            {
                if (cell.AssignedChannel is not { } node) continue;
                if (!locations.TryGetValue(node.Channel.Id, out var list))
                    locations[node.Channel.Id] = list = [];
                list.Add($"cuadro {cell.Index}{screen}");
            }
        }
        Collect(Cells, "");
        foreach (var window in _auxWindows)
            Collect(window.Vm.Cells, $" (pantalla auxiliar {window.Vm.SlotNumber})");

        foreach (var channel in Devices.SelectMany(d => d.Channels))
        {
            bool live = locations.TryGetValue(channel.Channel.Id, out var where);
            channel.IsLive = live;
            channel.LiveLocation = live ? "En vivo en " + string.Join(", ", where!) : "";
        }
    }

    // ---------- Pantallas auxiliares (estilo iVMS-4200, máximo 3) ----------

    /// <summary>Pantallas auxiliares abiertas: ventanas independientes con su
    /// propia grilla, para ver cámaras en más de un monitor.</summary>
    private readonly List<Views.AuxLiveWindow> _auxWindows = [];

    private const int MaxAuxScreens = 3;

    [RelayCommand]
    private void OpenAuxScreen()
    {
        if (_auxWindows.Count >= MaxAuxScreens)
        {
            StatusMessage = $"Ya hay {MaxAuxScreens} pantallas auxiliares abiertas (el máximo).";
            return;
        }
        // El número más bajo libre: cerrar la 2 y abrir otra vuelve a dar la 2.
        int slot = Enumerable.Range(1, MaxAuxScreens).First(n => _auxWindows.All(w => w.Vm.SlotNumber != n));
        var window = new Views.AuxLiveWindow(new AuxScreenViewModel(this, slot));
        window.Closed += (_, _) =>
        {
            _auxWindows.Remove(window);
            RefreshLiveChannels(); // lo que mostraba deja de estar en vivo
        };
        _auxWindows.Add(window);

        // Parte en el primer monitor sin ventanas de la aplicación (si lo hay).
        var occupied = new List<Window>();
        if (Application.Current.MainWindow is { } main) occupied.Add(main);
        occupied.AddRange(_auxWindows.Where(w => w != window && w.IsLoaded));
        window.ShowOnFreeMonitor(occupied);
        StatusMessage = $"Pantalla auxiliar {slot} abierta: arrástrela al monitor que quiera si no partió ahí.";
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
        _licenseTimer.Stop();
        _cercoSiren.Stop();
        // Las pantallas auxiliares mueren con la principal (cada una libera
        // sus players en su propio Closed).
        foreach (var window in _auxWindows.ToList())
            window.Close();
        foreach (var cell in Cells)
            cell.Dispose();
        Playback.Dispose();
        _ = _hub.DisposeAsync();
    }
}
