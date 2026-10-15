using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TrueCentralVms.AlarmScreen.Models;
using TrueCentralVms.AlarmScreen.Services;
using TrueCentralVms.Core.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TrueCentralVms.AlarmScreen.ViewModels;

/// <summary>
/// La pantalla del guardia. Toda la lógica vive en el servidor de TrueCentral; aquí solo se
/// pinta lo que llega, se marca como visto y se hace ruido.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ClientConfig _config;
    private readonly ServerLink _link;
    private readonly SoundService _sound = new();
    private readonly CancellationTokenSource _cts = new();

    private DispatcherTimer? _clockTimer;
    private DispatcherTimer? _reminderTimer;
    private int _remindersSent;

    /// <summary>Cuántas fotos se guardan ya descargadas. Ver <see cref="CachePhotoBytes"/>.</summary>
    private const int MaxCachedPhotos = 20;

    private readonly Dictionary<long, byte[]> _photoBytes = [];
    private readonly Queue<long> _photoOrder = new();

    public ObservableCollection<AlarmCardViewModel> Alarms { get; } = [];

    [ObservableProperty] private AlarmCardViewModel? _selected;
    [ObservableProperty] private string _statusText = "Iniciando…";
    [ObservableProperty] private string _screenName = "";
    [ObservableProperty] private string _siteName = "";
    [ObservableProperty] private string _pairingCode = "";
    [ObservableProperty] private bool _isPairing;
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private bool _showSetup;
    [ObservableProperty] private string _setupTitle = "Buscando el servidor";
    [ObservableProperty] private string _manualUrl = "";
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private string _clock = "";

    /// <summary>
    /// El detalle queda resaltado mientras la alarma que se está mostrando siga sin marcar. No
    /// es un destello con temporizador: es el estado de la alarma, así que se apaga exactamente
    /// cuando alguien la marca —aquí o en otra caseta— y no antes.
    /// </summary>
    [ObservableProperty] private bool _isHighlighting;

    /// <summary>La foto ocupando toda la pantalla, para mirar una patente o una cara de cerca.</summary>
    [ObservableProperty] private bool _isPhotoFullScreen;

    /// <summary>
    /// Aviso a pantalla completa de la alarma que acaba de entrar. Es <b>uno solo</b>: si llega
    /// otra antes de que el guardia cierre el que hay, se reemplaza el contenido en vez de apilar
    /// ventanas. Con dos alarmas seguidas, una pila obliga a cerrar dos veces y la segunda se
    /// cierra sin haberla leído.
    /// </summary>
    [ObservableProperty] private bool _showAlarmPopup;

    [ObservableProperty] private AlarmCardViewModel? _popupAlarm;

    // --- configuración que manda el servidor ---
    /// <summary>Monitor en pie: lista arriba y detalle abajo, en vez de dos columnas.</summary>
    [ObservableProperty] private bool _isVerticalLayout;
    private AlarmScreenConfigDto _boothConfig = new();

    public MainViewModel()
    {
        _config = ConfigService.Load();
        _link = new ServerLink(_config);

        // se arranca con lo último que dijo el servidor: un corte de red no debe dejar la
        // caseta muda ni con la pantalla al revés
        ApplyConfig(_config.ToScreenConfig(), []);

        ScreenName = string.IsNullOrWhiteSpace(_config.ScreenName) ? "Pantalla sin emparejar" : _config.ScreenName;
        SiteName = _config.SiteName;
        ManualUrl = _config.ServerUrl;

        _link.Changed += () => Dispatcher.UIThread.Post(OnLinkChanged);
        _link.AlarmRaised += alarm => Dispatcher.UIThread.Post(() => OnAlarmRaised(alarm));
        _link.AlarmUpdated += alarm => Dispatcher.UIThread.Post(() => OnAlarmUpdated(alarm));
        _link.Acknowledged += ack => Dispatcher.UIThread.Post(() => OnAcknowledged(ack));
        _link.BulkAcknowledged += ack => Dispatcher.UIThread.Post(() => OnBulkAcknowledged(ack));
        _link.Resynced += () => Dispatcher.UIThread.Post(() => _ = ResyncAsync());
        _link.ConfigChanged += () => Dispatcher.UIThread.Post(() => _ = RefreshConfigAsync());
        _link.RebootRequested += () => Dispatcher.UIThread.Post(OnRebootRequested);
    }

    /// <summary>Arranca el enlace con el servidor y los temporizadores de reloj y recordatorio.</summary>
    public void Start()
    {
        _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => Clock = DateTime.Now.ToString("HH:mm:ss"));
        _clockTimer.Start();

        // cada segundo, para que «repetir cada N segundos» valga tal cual y no redondeado
        _reminderTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => TickReminder());
        _reminderTimer.Start();

        _ = _link.RunAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        await _cts.CancelAsync();
        await _link.DisposeAsync();
    }

    // ---------- eventos del servidor ----------

    private void OnLinkChanged()
    {
        StatusText = _link.StatusText;
        IsOnline = _link.State == LinkState.Online;
        IsPairing = _link.State == LinkState.Pairing && _link.PairingCode.Length > 0;
        PairingCode = _link.PairingCode;
        ShowSetup = _link.State is LinkState.Searching or LinkState.Blocked;
        SetupTitle = _link.State == LinkState.Blocked ? "Pantalla sin acceso" : "Buscando el servidor";

        if (_link.Session is { } session)
        {
            ScreenName = session.ScreenName;
            SiteName = session.SiteName;
        }
        if (IsOnline)
        {
            _ = RefreshConfigAsync();
            _ = ReportAudioOutputsAsync();
        }
    }

    /// <summary>
    /// Trae sonidos, volumen y disposición, y descarga los audios que falten. Se llama al
    /// conectar y cada vez que el servidor avisa que cambió la configuración.
    /// </summary>
    private async Task RefreshConfigAsync()
    {
        var config = await _link.LoadConfigAsync(_cts.Token);
        if (config is null) return;

        var library = await _link.EnsureSoundsAsync(
            config.Sounds, Path.Combine(ConfigService.AppDataDir, "sounds"), _cts.Token);

        ApplyConfig(config, library);
        // el nombre puede cambiarse desde el panel con la pantalla conectada
        if (config.ScreenName.Length > 0) ScreenName = config.ScreenName;
        if (config.SiteName.Length > 0) SiteName = config.SiteName;

        _config.Remember(config);
        ConfigService.Save(_config);
    }

    private void ApplyConfig(AlarmScreenConfigDto config, Dictionary<string, string> library)
    {
        _boothConfig = config;
        _sound.Configure(config, library);
        IsVerticalLayout = AlarmScreenLayouts.IsVertical(config.Layout);

        // Fuera del hilo de UI: wpctl tarda algunas decenas de milisegundos y esto se ejecuta
        // también al recibir ConfigChanged, o sea con el guardia mirando la pantalla.
        if (!string.IsNullOrWhiteSpace(config.AudioOutput))
        {
            var salida = config.AudioOutput;
            _ = Task.Run(() => DeviceControl.UseAudioOutput(salida));
        }
    }

    /// <summary>
    /// Le cuenta al servidor por qué salidas puede sonar este equipo. Se hace al conectar y no
    /// una sola vez al instalar: alguien enchufa un parlante USB o cambia el HDMI y el panel
    /// tiene que ofrecer lo que hay hoy, no lo que había el día de la instalación.
    /// </summary>
    private async Task ReportAudioOutputsAsync()
    {
        var salidas = DeviceControl.AudioOutputs().Select(s => s.Name).ToList();
        if (salidas.Count > 0) await _link.ReportAudioOutputsAsync(salidas, _cts.Token);
    }

    private void OnAlarmRaised(AlarmScreenAlertDto alarm)
    {
        if (Alarms.Any(a => a.Id == alarm.Id)) return;

        var card = new AlarmCardViewModel(alarm);
        Alarms.Insert(0, card);
        // la alarma que acaba de entrar pasa a primer plano: el guardia no tiene que buscarla
        Selected = card;
        Trim();
        RefreshPending();

        if (!alarm.Acknowledged)
        {
            _remindersSent = 0;
            _sound.PlayNew(alarm.Priority, alarm.SoundFile);

            // el aviso es uno solo: si ya había uno abierto se reemplaza su contenido
            PopupAlarm = card;
            ShowAlarmPopup = true;
        }
        // la foto no se pide aquí: al quedar seleccionada, OnSelectedChanged ya la pidió
    }

    /// <summary>Marca la alarma del aviso y lo cierra. Es el único botón que trae.</summary>
    [RelayCommand]
    private async Task AcknowledgePopup()
    {
        var card = PopupAlarm;
        ShowAlarmPopup = false;
        PopupAlarm = null;
        if (card is not null) await Acknowledge(card);
    }

    private void OnAlarmUpdated(AlarmScreenAlertDto alarm)
    {
        var card = Alarms.FirstOrDefault(a => a.Id == alarm.Id);
        if (card is null) { OnAlarmRaised(alarm); return; }

        card.Apply(alarm);
        RefreshPending();
        _ = EnsurePhotoAsync(card);
    }

    private void OnAcknowledged(AlarmScreenAckDto ack)
    {
        Alarms.FirstOrDefault(a => a.Id == ack.AlertId)?.MarkAcknowledged(ack.By, ack.From, ack.At);
        OnAnyAcknowledged();
    }

    private void OnBulkAcknowledged(AlarmScreenBulkAckDto ack)
    {
        foreach (var id in ack.AlertIds)
            Alarms.FirstOrDefault(a => a.Id == id)?.MarkAcknowledged(ack.By, ack.From, ack.At);
        OnAnyAcknowledged();
    }

    /// <summary>
    /// Lo que pasa siempre que se marca algo, sea aquí, en otra pantalla o en TrueCentral: callar lo que suena y
    /// recalcular la pantalla. El recordatorio arranca de cero, así que si quedan pendientes el
    /// siguiente pitido llega tras el intervalo completo y no de inmediato.
    /// </summary>
    private void OnAnyAcknowledged()
    {
        _sound.Stop();
        _reminderCounter = 0;
        _remindersSent = 0;

        // si la del aviso ya la marcó otra pantalla o un operador en TrueCentral, el aviso sobra:
        // dejarlo abierto invita a marcar algo que ya está atendido
        if (PopupAlarm is { Acknowledged: true })
        {
            ShowAlarmPopup = false;
            PopupAlarm = null;
        }

        RefreshPending();
        RefreshHighlight();
        ClearIfNothingPending();
    }

    /// <summary>
    /// Resincronización completa. Se pide al conectar y en cada reconexión: una caseta que
    /// perdió la red no puede quedarse con el estado viejo en pantalla.
    /// </summary>
    private async Task ResyncAsync()
    {
        var recent = await _link.LoadRecentAsync(_cts.Token);
        if (recent.Count == 0 && Alarms.Count > 0)
        {
            // el servidor no contestó: lo que hay en pantalla sigue siendo lo mejor que se sabe
            _link.MarkResynced();
            return;
        }

        var selectedId = Selected?.Id;
        Alarms.Clear();
        foreach (var alarm in recent) Alarms.Add(new AlarmCardViewModel(alarm));

        // tras reconectar se muestra lo pendiente; si no hay nada, la pantalla queda en espera
        Selected = Alarms.FirstOrDefault(a => a.Id == selectedId && !a.Acknowledged)
                ?? Alarms.FirstOrDefault(a => !a.Acknowledged);
        RefreshPending();

        // deja constancia de que esta caseta las recibió, aunque haya sido tarde
        await _link.ConfirmDeliveriesAsync(recent.Select(a => a.Id).ToArray());

        if (Selected is not null) await EnsurePhotoAsync(Selected);
        _link.MarkResynced();
    }

    // ---------- acciones del guardia ----------

    [RelayCommand]
    private async Task Acknowledge(AlarmCardViewModel? card)
    {
        if (card is null || card.Acknowledged) return;
        await _link.AcknowledgeAsync(card.Id);
    }

    [RelayCommand]
    private async Task AcknowledgeAll() => await _link.AcknowledgeAllAsync();

    /// <summary>Configuración manual: el broadcast no cruza VLANs y en sitios reales eso pasa.</summary>
    [RelayCommand]
    private void UseManualUrl()
    {
        var url = ManualUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "http://" + url;

        _link.UseServer(url);
        StatusText = "Servidor fijado a mano: " + url;
    }

    partial void OnSelectedChanging(AlarmCardViewModel? oldValue, AlarmCardViewModel? newValue)
    {
        // la foto de la que se deja de mostrar se libera: es la única forma de que la memoria no
        // crezca con cada alarma. Salvo la del aviso abierto: un resync puede mover la selección
        // mientras está en pantalla, y liberarla dejaría el aviso diciendo que no trae imagen.
        if (ReferenceEquals(oldValue, newValue)) return;
        if (ShowAlarmPopup && ReferenceEquals(oldValue, PopupAlarm)) return;

        ReleasePhoto(oldValue);
    }

    partial void OnSelectedChanged(AlarmCardViewModel? value)
    {
        // al cambiar de alarma se vuelve a la pantalla completa del detalle: la foto ampliada de
        // la anterior no tiene nada que decir sobre la nueva
        IsPhotoFullScreen = false;
        RefreshHighlight();
        if (value is not null) _ = EnsurePhotoAsync(value);
    }

    // ---------- apoyo ----------

    /// <summary>
    /// Baja la foto y, si esa alarma es la que se está mostrando, la decodifica.
    ///
    /// La distinción importa: en pantalla hay <b>una</b> foto a la vez —la lista no lleva
    /// imágenes— y un JPEG de cámara descomprimido son 8 MB a 1080p, 15 MB a 1440p. Decodificar
    /// las 200 alarmas que retiene <see cref="Trim"/> son 1,6–2,9 GB, más de lo que tiene la Pi.
    /// Los bytes comprimidos sí se guardan, que son 350–750 KB y evitan volver a pedirla por red
    /// al cambiar de alarma.
    /// </summary>
    private async Task EnsurePhotoAsync(AlarmCardViewModel card)
    {
        if (card.PhotoUrl is null) return;

        if (!_photoBytes.TryGetValue(card.Id, out var bytes))
        {
            if (card.PhotoRequested) return;
            card.PhotoRequested = true;

            bytes = await _link.DownloadAsync(card.PhotoUrl, _cts.Token);
            if (bytes is null || bytes.Length == 0) return;
            CachePhotoBytes(card.Id, bytes);
        }

        // entre la descarga y aquí el guardia pudo cambiar de alarma
        if (!ReferenceEquals(card, Selected)) return;

        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);
            Dispatcher.UIThread.Post(() =>
            {
                // se comprueba otra vez ya en el hilo de UI: la selección pudo cambiar mientras tanto
                if (!ReferenceEquals(card, Selected)) { bitmap.Dispose(); return; }

                // una foto diferida reemplaza a la que hubiera: sin esto la anterior queda colgada
                var previous = card.Photo;
                card.Photo = bitmap;
                if (previous is not null)
                    Dispatcher.UIThread.Post(previous.Dispose, DispatcherPriority.Background);
            });
        }
        catch
        {
            // imagen corrupta: la alarma vale igual sin foto
        }
    }

    /// <summary>
    /// Caché de bytes comprimidos, en orden de llegada y con tope. 20 fotos son ~15 MB en el peor
    /// caso; sin tope, un turno movido las acumula todas. No se escribe a disco a propósito: la
    /// caseta no guarda datos, y la SD se corrompe con los cortes de luz.
    /// </summary>
    private void CachePhotoBytes(long alarmId, byte[] bytes)
    {
        if (!_photoBytes.TryAdd(alarmId, bytes)) return;
        _photoOrder.Enqueue(alarmId);

        while (_photoOrder.Count > MaxCachedPhotos && _photoOrder.TryDequeue(out var oldest))
            _photoBytes.Remove(oldest);
    }

    /// <summary>
    /// Suelta el bitmap de una tarjeta. Primero se quita la referencia —así el control deja de
    /// pintarlo en este mismo ciclo— y recién después se libera, en una pasada posterior: liberar
    /// un bitmap que el <c>Image</c> todavía tiene en la mano revienta al renderizar.
    /// </summary>
    private static void ReleasePhoto(AlarmCardViewModel? card)
    {
        if (card?.Photo is not { } bitmap) return;
        card.Photo = null;
        Dispatcher.UIThread.Post(bitmap.Dispose, DispatcherPriority.Background);
    }

    /// <summary>
    /// Reinicio pedido desde el panel. Se avisa en pantalla antes de ejecutarlo: si un guardia
    /// está delante, tiene que entender por qué se le apagó la pantalla en vez de creer que se
    /// colgó el equipo. Los dos segundos alcanzan para que se lea y para que el mensaje salga.
    /// </summary>
    private void OnRebootRequested()
    {
        StatusText = "Reiniciando por orden del administrador…";
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            DeviceControl.Reboot();
        });
    }

    private void RefreshHighlight() => IsHighlighting = Selected is { Acknowledged: false };

    /// <summary>Doble clic sobre la foto: la agranda, y el siguiente doble clic la cierra.</summary>
    [RelayCommand]
    private void TogglePhotoFullScreen()
    {
        // sin imagen no hay nada que agrandar; abrir un rectángulo negro solo confunde
        IsPhotoFullScreen = Selected?.Photo is not null && !IsPhotoFullScreen;
    }

    /// <summary>La ✕ de la foto ampliada. El mismo cierre que hace Escape, pero con el dedo.</summary>
    [RelayCommand]
    private void ClosePhoto() => ClosePhotoFullScreen();

    public void ClosePhotoFullScreen() => IsPhotoFullScreen = false;

    /// <summary>
    /// Botón para ampliar, superpuesto a la foto. Existe porque el doble toque no es fiable en la
    /// pantalla táctil de la ubicación: se pierde o se interpreta como dos toques sueltos. El
    /// doble clic se conserva para quien atienda el equipo con ratón.
    /// </summary>
    [RelayCommand]
    private void OpenPhoto()
    {
        if (Selected?.Photo is not null) IsPhotoFullScreen = true;
    }

    /// <summary>Cierra el detalle y deja la pantalla en espera. Las alarmas siguen en la lista.</summary>
    [RelayCommand]
    private void ClearSelection() => Selected = null;

    /// <summary>
    /// Con todo marcado, el detalle se vacía: una alarma ya atendida ocupando la pantalla es
    /// ruido, y una pantalla en espera se lee de un vistazo desde el otro lado de la caseta.
    /// </summary>
    private void ClearIfNothingPending()
    {
        if (PendingCount == 0) Selected = null;
    }

    private void TickReminder()
    {
        if (PendingCount == 0 || _boothConfig.RepeatBeepSeconds <= 0) return;
        if (_boothConfig.MaxReminders > 0 && _remindersSent >= _boothConfig.MaxReminders) return;

        _reminderCounter++;
        if (_reminderCounter < _boothConfig.RepeatBeepSeconds) return;

        _reminderCounter = 0;
        _remindersSent++;
        _sound.PlayReminder();
    }

    private int _reminderCounter;

    private void RefreshPending()
    {
        PendingCount = Alarms.Count(a => !a.Acknowledged);
        if (PendingCount == 0) _remindersSent = 0;
    }

    /// <summary>La caseta muestra lo reciente; el histórico completo vive en el servidor.</summary>
    private void Trim()
    {
        while (Alarms.Count > 200) Alarms.RemoveAt(Alarms.Count - 1);
    }
}
