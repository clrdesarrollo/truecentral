using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Una celda de la grilla de video: dueña de su propio Player de FlyleafLib.
/// Cada apertura (incluidos los reintentos) pide una concesión NUEVA al
/// servidor: los tokens expiran y nunca se reutiliza una URL antigua. Si el
/// stream se corta (red, equipo reiniciado, expulsión), la celda reintenta
/// sola mientras siga asignada.
/// </summary>
public partial class VideoCellViewModel : ObservableObject, IZoomTarget, IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly ApiClient _api;
    private readonly ClientSettings _settings;
    private ChannelNode? _assigned;
    private int _openSequence;
    private bool _retryPending;

    /// <summary>
    /// Cuadro dueño de cada player. Los manejadores del player NO capturan el
    /// cuadro que lo creó: al arrastrar un cuadro sobre otro el player se muda
    /// con el video andando, y sus avisos (apertura fallida, fin de stream)
    /// tienen que llegar al cuadro donde quedó. Tabla débil: un player
    /// descartado se recolecta sin dejar rastro.
    /// </summary>
    private static readonly ConditionalWeakTable<Player, VideoCellViewModel> PlayerOwners = new();

    private static VideoCellViewModel? OwnerOf(object? sender) =>
        sender is Player player && PlayerOwners.TryGetValue(player, out var owner) ? owner : null;

    /// <summary>Player visible del cuadro. Observable porque el cambio suave de
    /// stream lo REEMPLAZA: el nuevo se abre en un player de reserva mientras
    /// este sigue reproduciendo, y recién con imagen lista se intercambian
    /// (FlyleafHost reengancha su superficie al vuelo).</summary>
    [ObservableProperty] private Player _player = null!;

    /// <summary>Se encendió el audio de este cuadro (el dueño silencia el resto).</summary>
    public event Action<VideoCellViewModel>? AudioActivated;

    /// <summary>Se guardó un archivo local (título, glifo MDL2, ruta completa):
    /// el shell muestra la notificación con el link a la ubicación.</summary>
    public event Action<string, string, string>? MediaSaved;

    /// <summary>Ruta de la cápsula en curso (Flyleaf le agrega la extensión).</summary>
    private string? _recordingFile;
    /// <summary>Inicio de la cápsula: viaja con el cuadro en un intercambio,
    /// así el contador sigue marcando el tiempo real y no vuelve a cero.</summary>
    private DateTime _recordingStarted;
    /// <summary>Ya hay un contador corriendo para esta cápsula (no duplicar).</summary>
    private bool _tickerRunning;

    [ObservableProperty] private string? _title;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isEmpty = true;

    /// <summary>
    /// El cuadro está pidiendo la concesión y abriendo el stream. Sin aviso, el
    /// par de segundos hasta el primer cuadro parece un cuadro colgado.
    /// </summary>
    [ObservableProperty] private bool _isConnecting;
    /// <summary>Número del cuadro en la grilla (1..N), visible en su barra.</summary>
    [ObservableProperty] private int _index;
    /// <summary>Canal asignado al cuadro (null = libre). Lo usa el panel PTZ.</summary>
    [ObservableProperty] private ChannelNode? _assignedChannel;
    /// <summary>Audio del cuadro. Exclusivo: la grilla silencia los demás.</summary>
    [ObservableProperty] private bool _isAudioOn;
    /// <summary>Stream en uso (Main/Sub). El botón P/S de la barra lo alterna.</summary>
    [ObservableProperty] private StreamProfile _profile;
    /// <summary>Grabando una cápsula local de este cuadro (botón ● en rojo).</summary>
    [ObservableProperty] private bool _isRecordingClip;
    /// <summary>Tiempo transcurrido de la cápsula ("00:12"); vacío si no graba.</summary>
    [ObservableProperty] private string _recordingElapsed = "";

    public VideoCellViewModel(ApiClient api, ClientSettings settings)
    {
        _api = api;
        _settings = settings;
        _player = CreatePlayer();
    }

    /// <summary>
    /// Player configurado para vigilancia en vivo, con los manejadores de
    /// reintento enganchados. Los manejadores ignoran a los players retirados
    /// (el cambio suave de stream deja al viejo agonizando un instante).
    /// </summary>
    private Player CreatePlayer()
    {
        var config = new Config();
        config.Player.AutoPlay = true;
        config.Audio.Enabled = false; // se enciende por cuadro con su botón 🔊
        // RTSP por TCP y con la menor latencia posible para vigilancia en vivo.
        config.Demuxer.FormatOpt["rtsp_transport"] = "tcp";
        config.Demuxer.FormatOpt["fflags"] = "nobuffer";
        // Arranque rápido: sin estos límites, FFmpeg puede pasar varios
        // segundos "analizando" el stream antes del primer cuadro. El SDP de
        // MediaMTX ya anuncia los parámetros del códec (sprop), así que un
        // análisis corto basta; la imagen aparece con el primer keyframe.
        config.Demuxer.FormatOpt["analyzeduration"] = "500000"; // 0,5 s (µs)
        config.Demuxer.FormatOpt["probesize"] = "524288";       // 512 KB
        var player = new Player(config);
        PlayerOwners.AddOrUpdate(player, this);
        player.Audio.Volume = Math.Clamp(_settings.DefaultVolume, 0, 100);
        player.Config.Video.AspectRatio = _settings.StretchVideo ? AspectRatio.Fill : AspectRatio.Keep;

        player.OpenCompleted += (sender, e) =>
        {
            if (OwnerOf(sender) is not { } cell) return;
            if (!ReferenceEquals(sender, cell.Player) || cell._assigned is null) return;
            cell.IsConnecting = false;
            if (e.Success) { cell.Status = ""; return; }
            cell.Status = "No se pudo abrir el video: " + (e.Error ?? "error desconocido");
            cell.ScheduleRetry(cell._openSequence);
        };

        // Fin de reproducción no pedido (caída de red, equipo apagado,
        // expulsión desde el panel): reintentar. Clear/Dispose/reasignación
        // invalidan la secuencia ANTES de detener, así que no reintentan.
        player.PlaybackStopped += (sender, e) =>
        {
            if (OwnerOf(sender) is not { } cell || !ReferenceEquals(sender, cell.Player)) return;
            // Flyleaf corta la grabación junto con el stream: reflejar y avisar
            // (la cápsula quedó guardada hasta el momento del corte).
            if (cell.IsRecordingClip && !cell.Player.IsRecording)
            {
                cell.IsRecordingClip = false;
                cell.NotifyClipSaved(notify: true);
            }
            if (cell._assigned is null) return;
            if (!e.Success) cell.Status = "Sin señal — reintentando…";
            cell.ScheduleRetry(cell._openSequence);
        };
        return player;
    }

    /// <summary>Abre un canal en esta celda (perfil main/sub según el tamaño de la grilla).</summary>
    public async Task OpenAsync(ChannelNode node, StreamProfile profile)
    {
        int sequence = ++_openSequence;
        StopClipRecording(notify: true);
        ResetDigitalZoom();
        _assigned = node;
        Profile = profile;
        AssignedChannel = node;
        IsEmpty = false;
        Title = $"{node.Device.Name} · {node.Channel.Name}";
        Status = "Conectando…";
        IsConnecting = true;
        await ConnectAsync(sequence);
    }

    /// <summary>Alterna entre stream principal y secundario del canal en
    /// pantalla. Parte del destino del cambio en vuelo (si lo hay): así un
    /// segundo clic rápido significa "vuelve al que estaba" en lugar de
    /// relanzar el mismo cambio una y otra vez.</summary>
    [RelayCommand]
    private async Task SwitchProfileAsync()
    {
        var current = PendingSwitchTarget ?? Profile;
        await SwitchToProfileAsync(current == StreamProfile.Main ? StreamProfile.Sub : StreamProfile.Main);
    }

    /// <summary>Anula un cambio de stream en vuelo: el player de reserva se
    /// descarta solo al despertar (ve la secuencia vencida).</summary>
    public void CancelPendingSwitch() => _openSequence++;

    /// <summary>Destino del cambio suave en vuelo, atado a la secuencia que lo
    /// lanzó: cualquier bump (reasignación, Clear, intercambio, cancelación)
    /// lo invalida solo, sin limpieza explícita en cada camino.</summary>
    private int _pendingSwitchSequence = -1;
    private StreamProfile _pendingSwitchTarget;
    private StreamProfile? PendingSwitchTarget =>
        _pendingSwitchSequence == _openSequence ? _pendingSwitchTarget : null;

    /// <summary>
    /// Cambio SUAVE de stream: el destino se abre en un player de reserva
    /// mientras el actual sigue reproduciendo, y solo cuando el nuevo ya tiene
    /// imagen (primer keyframe decodificado) se intercambian — el corte pasa de
    /// varios segundos en negro a un parpadeo. Si el nuevo no logra imagen en
    /// 20 s (2 intentos), se descarta y el cuadro sigue con el stream que tenía.
    /// </summary>
    public async Task SwitchToProfileAsync(StreamProfile target, bool hardFallbackOnFailure = false)
    {
        if (_assigned is not { } node) return;
        // Clics rápidos: un cambio ya en camino hacia ese destino se deja
        // terminar (relanzarlo descarta una conexión a medio abrir para
        // volver a empezar de cero: puro costo). Pedir el stream visible con
        // un cambio en vuelo significa "quédate como estás": basta abortarlo.
        if ((PendingSwitchTarget ?? Profile) == target) return;
        if (target == Profile && PendingSwitchTarget is not null)
        {
            CancelPendingSwitch();
            Status = "";
            return;
        }
        int sequence = ++_openSequence; // invalida reintentos del stream visible
        _pendingSwitchSequence = sequence;
        _pendingSwitchTarget = target;
        StopClipRecording(notify: true);
        Status = target == StreamProfile.Main ? "Cambiando a principal…" : "Cambiando a secundario…";

        // Hasta 2 intentos: el primero puede pillar el pull hacia el equipo
        // recién partiendo (sourceOnDemand) o el enlace saturado y quedarse
        // corto; el reintento encuentra ese pull ya tibio en MediaMTX y suele
        // enganchar de inmediato. Todo ocurre detrás del video vigente.
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var fresh = CreatePlayer();
            try
            {
                var grant = await _api.RequestStreamAsync(node.Device.Id, node.Channel.RtspChannel, target);
                if (sequence != _openSequence) { fresh.Dispose(); return; }

                // OpenAsync de Flyleaf despacha a un hilo propio y retorna al
                // tiro: el Status sigue en Stopped hasta que ese hilo parte.
                // Mirar el Status de inmediato daba el cambio por muerto en
                // milisegundos (con el pool ocupado por clics rápidos, casi
                // siempre). La señal fiable de apertura es OpenCompleted.
                var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                fresh.OpenCompleted += (_, e) => opened.TrySetResult(e.Success);
                fresh.OpenAsync(grant.RtspUrl);

                var deadline = DateTime.UtcNow.AddSeconds(20);
                bool openOk = await Task.WhenAny(opened.Task, Task.Delay(TimeSpan.FromSeconds(15))) == opened.Task
                    && opened.Task.Result;

                // Con la apertura confirmada, esperar la primera imagen real:
                // el reloj del player parte a correr con el primer cuadro
                // presentado (recién ahí el intercambio es invisible). Un
                // Status terminal aquí sí significa que el stream murió.
                while (openOk && sequence == _openSequence &&
                       DateTime.UtcNow < deadline && fresh.CurTime == 0 &&
                       fresh.Status is not (FlyleafLib.MediaPlayer.Status.Failed or FlyleafLib.MediaPlayer.Status.Stopped))
                    await Task.Delay(100);

                if (sequence != _openSequence)
                {
                    fresh.Dispose();
                    return;
                }
                if (fresh.CurTime > 0)
                {
                    var retired = Player;
                    Profile = target;
                    _pendingSwitchSequence = -1;
                    fresh.Config.Audio.Enabled = IsAudioOn;
                    Player = fresh; // FlyleafHost reengancha la superficie: corte mínimo
                    Status = "";
                    retired.Dispose();
                    return;
                }
                fresh.Dispose(); // sin imagen: reintentar o rendirse
            }
            catch (ApiException ex)
            {
                fresh.Dispose();
                if (sequence != _openSequence) return;
                _pendingSwitchSequence = -1;
                FlashStatus("No se pudo cambiar el stream: " + ex.Message);
                return;
            }
        }
        if (sequence == _openSequence) _pendingSwitchSequence = -1;
        if (hardFallbackOnFailure)
        {
            // El destino es obligatorio (ej. volver a secundario al restaurar
            // un cuadro: quedarse en principal quema ancho de banda): apertura
            // dura con la lógica normal de reintentos, aunque corte la imagen.
            if (sequence == _openSequence && _assigned == node)
                await OpenAsync(node, target);
            return;
        }
        FlashStatus("El stream destino no entregó imagen (¿perfil no disponible o enlace del equipo saturado?). Se mantiene el actual.");
        // El cambio pudo intentarse sobre un cuadro ya sin señal, y su
        // reintento murió con el bump de secuencia de este cambio: se re-arma
        // (si el stream visible sigue andando, el reintento se descarta solo).
        ScheduleRetry(sequence);
    }

    /// <summary>Pide una concesión nueva y abre la URL RTSP resultante.</summary>
    private async Task ConnectAsync(int sequence)
    {
        if (_assigned is not { } node) return;
        try
        {
            var grant = await _api.RequestStreamAsync(node.Device.Id, node.Channel.RtspChannel, Profile);
            if (sequence != _openSequence) return; // la celda ya se reasignó
            Player.OpenAsync(grant.RtspUrl);
        }
        catch (ApiException ex)
        {
            if (sequence != _openSequence) return;
            Status = ex.Message;
            ScheduleRetry(sequence);
        }
    }

    /// <summary>
    /// Reintento con espera fija. Se descarta si la celda cambió de asignación
    /// o si el player ya volvió a estar activo (una reasignación detiene el
    /// video anterior y ese stop también dispara PlaybackStopped).
    /// </summary>
    private async void ScheduleRetry(int sequence)
    {
        if (_retryPending) return;
        _retryPending = true;
        try { await Task.Delay(RetryDelay); }
        finally { _retryPending = false; }

        if (sequence != _openSequence || _assigned is null) return;
        if (Player.Status is FlyleafLib.MediaPlayer.Status.Playing
            or FlyleafLib.MediaPlayer.Status.Opening
            or FlyleafLib.MediaPlayer.Status.Paused) return;
        Status = "Reconectando…";
        await ConnectAsync(sequence);
    }

    // ------------------------------------------------------------------
    // Captura y grabación local (carpetas de Configuración)
    // ------------------------------------------------------------------

    private string MediaFileBase(string folder, string extension)
    {
        System.IO.Directory.CreateDirectory(folder);
        string name = string.Join("_", (Title ?? "captura").Split(System.IO.Path.GetInvalidFileNameChars()));
        return System.IO.Path.Combine(folder, $"{name} {DateTime.Now:yyyy-MM-dd HH.mm.ss}{extension}");
    }

    /// <summary>Guarda el cuadro actual como imagen en la carpeta de capturas.</summary>
    [RelayCommand]
    private void Snapshot()
    {
        if (_assigned is null) return;
        try
        {
            string extension = _settings.SnapshotFormat.Equals("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
            string file = MediaFileBase(_settings.EffectiveSnapshotFolder, extension);
            Player.TakeSnapshotToFile(file);
            FlashStatus("Captura guardada");
            MediaSaved?.Invoke("Captura guardada", "\uE722", file);
        }
        catch (Exception ex)
        {
            FlashStatus("No se pudo guardar la captura: " + ex.Message);
        }
    }

    /// <summary>
    /// Graba una cápsula local del stream tal como llega (remux sin
    /// recomprimir: no cuesta CPU). Un clic parte, otro detiene.
    /// </summary>
    [RelayCommand]
    private void ToggleRecording()
    {
        if (_assigned is null) return;
        if (IsRecordingClip)
        {
            StopClipRecording(notify: true);
            return;
        }
        try
        {
            // Sin extensión: Flyleaf agrega la recomendada según el contenedor
            // (y actualiza la ruta por el parámetro ref).
            string file = MediaFileBase(_settings.EffectiveRecordingFolder, "");
            Player.StartRecording(ref file, useRecommendedExtension: true);
            _recordingFile = file;
            _recordingStarted = DateTime.UtcNow;
            IsRecordingClip = true;
            RunRecordingTicker();
        }
        catch (Exception ex)
        {
            FlashStatus("No se pudo iniciar la grabación: " + ex.Message);
        }
    }

    private void StopClipRecording(bool notify)
    {
        if (!IsRecordingClip) return;
        try { Player.StopRecording(); } catch { }
        IsRecordingClip = false;
        NotifyClipSaved(notify);
    }

    /// <summary>
    /// Contador de la cápsula en la barra del cuadro (mm:ss, o h:mm:ss al pasar
    /// la hora). Corre en el hilo de UI (lo dispara el comando del botón) y
    /// muere solo cuando la grabación termina por cualquier vía.
    /// </summary>
    private async void RunRecordingTicker()
    {
        if (_tickerRunning) return;
        _tickerRunning = true;
        while (IsRecordingClip)
        {
            var elapsed = DateTime.UtcNow - _recordingStarted;
            RecordingElapsed = elapsed.TotalHours >= 1
                ? elapsed.ToString(@"h\:mm\:ss")
                : elapsed.ToString(@"mm\:ss");
            await Task.Delay(500);
        }
        _tickerRunning = false;
        RecordingElapsed = "";
    }

    /// <summary>Arranca el contador si este cuadro quedó con la cápsula en
    /// curso (tras un intercambio la grabación cambia de cuadro).</summary>
    private void EnsureRecordingTicker()
    {
        if (IsRecordingClip) RunRecordingTicker();
    }

    /// <summary>Cierra el ciclo de una cápsula: avisa con la ruta final (el
    /// toast del shell muestra el link) salvo en cierres silenciosos.</summary>
    private void NotifyClipSaved(bool notify)
    {
        string? file = _recordingFile;
        _recordingFile = null;
        if (!notify || file is null) return;
        FlashStatus("Grabación guardada");
        MediaSaved?.Invoke("Grabación guardada", "\uE714", file);
    }

    /// <summary>Mensaje transitorio en la barra del cuadro (no pisa los estados
    /// persistentes de conexión: se borra solo si nadie lo cambió).</summary>
    private async void FlashStatus(string message)
    {
        Status = message;
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (Status == message) Status = "";
    }

    // ------------------------------------------------------------------
    // Zoom digital (rueda del mouse sobre el video)
    // ------------------------------------------------------------------

    /// <summary>Ajuste de imagen: estirar al cuadro (sin barras negras) o
    /// mantener la proporción original. Aplica en caliente.</summary>
    public void ApplyStretch(bool stretch) =>
        Player.Config.Video.AspectRatio = stretch ? AspectRatio.Fill : AspectRatio.Keep;

    /// <summary>Zoom en % (100 = sin zoom). Flyleaf recorta el viewport en GPU.</summary>
    public double DigitalZoom => Player.Config.Video.Zoom;

    /// <summary>Acerca o aleja centrado en el punto del cursor (normalizado 0..1).</summary>
    public void DigitalZoomStep(bool zoomIn, System.Windows.Point center)
    {
        double target = Math.Clamp(Player.Config.Video.Zoom + (zoomIn ? 20 : -20), 100, 600);
        if (target <= ViewModels.DigitalZoom.NoZoom)
            ResetDigitalZoom();
        else
            Player.Config.Video.SetZoomAndCenter(target, center);
        FlashStatus(ViewModels.DigitalZoom.Describe(target));
    }

    /// <summary>Un cuadro vacío no tiene nada que acercar.</summary>
    public bool CanDigitalZoom => !IsEmpty;

    /// <summary>Acerca el área marcada con el mouse (píxeles físicos de la ventana de video).</summary>
    public void DigitalZoomToArea(System.Windows.Rect areaPx) =>
        FlashStatus(ViewModels.DigitalZoom.Describe(ViewModels.DigitalZoom.ApplyArea(Player, areaPx)));

    /// <summary>Rueda del mouse: un paso de zoom que deja quieto el punto bajo el cursor.</summary>
    public void DigitalZoomStepAt(System.Windows.Point pointPx, bool zoomIn) =>
        FlashStatus(ViewModels.DigitalZoom.Describe(
            ViewModels.DigitalZoom.StepAtPoint(Player, zoomIn, pointPx)));

    /// <summary>Vuelve a 1× (se hace también al abrir otro canal en el cuadro).</summary>
    public void ResetDigitalZoom() => ViewModels.DigitalZoom.Reset(Player);

    // ------------------------------------------------------------------
    // Intercambio de cuadros (arrastrar un cuadro sobre otro en la grilla)
    // ------------------------------------------------------------------

    /// <summary>
    /// Intercambia el CONTENIDO de dos cuadros: las cámaras cambian de
    /// ubicación en la grilla, no los cuadros (el número y la posición son del
    /// cuadro). Viaja el player entero — con su imagen ya andando, su zoom
    /// digital y su cápsula en curso —, así que no se corta el video ni se pide
    /// una concesión nueva al servidor: el mismo stream aparece en el otro
    /// lugar. Si el destino está libre, el intercambio equivale a mudar la
    /// cámara y dejar libre el origen.
    /// </summary>
    public static void SwapContent(VideoCellViewModel a, VideoCellViewModel b)
    {
        if (ReferenceEquals(a, b)) return;

        // Lo que esté en vuelo (apertura o reintento) queda sin efecto: apunta
        // al cuadro que ya no tiene ese canal. Más abajo se relanza si hace falta.
        a._openSequence++;
        b._openSequence++;

        // El audio se apaga en el player que lo tenía y se enciende en el que
        // llega: el flag toca la configuración del player vigente del cuadro.
        (bool audioA, bool audioB) = (a.IsAudioOn, b.IsAudioOn);
        a.IsAudioOn = false;
        b.IsAudioOn = false;

        // Los players se SUELTAN antes de reasignarse. Entregarle a un host un
        // player que todavía pertenece a otro hace que FlyleafHost se lo quite
        // al otro escribiendo directo en su propiedad Player, y esa escritura
        // borra el enlace del host con su cuadro: quedaría negro para siempre.
        (var playerA, var playerB) = (a.Player, b.Player);
        a.Player = null!;
        b.Player = null!;
        a.Player = playerB;
        b.Player = playerA;
        PlayerOwners.AddOrUpdate(playerB, a);
        PlayerOwners.AddOrUpdate(playerA, b);

        (a._assigned, b._assigned) = (b._assigned, a._assigned);
        (a.AssignedChannel, b.AssignedChannel) = (b.AssignedChannel, a.AssignedChannel);
        (a.Title, b.Title) = (b.Title, a.Title);
        (a.Status, b.Status) = (b.Status, a.Status);
        (a.Profile, b.Profile) = (b.Profile, a.Profile);
        (a.IsConnecting, b.IsConnecting) = (b.IsConnecting, a.IsConnecting);
        (a._recordingFile, b._recordingFile) = (b._recordingFile, a._recordingFile);
        (a._recordingStarted, b._recordingStarted) = (b._recordingStarted, a._recordingStarted);
        (a.IsRecordingClip, b.IsRecordingClip) = (b.IsRecordingClip, a.IsRecordingClip);
        (a.RecordingElapsed, b.RecordingElapsed) = (b.RecordingElapsed, a.RecordingElapsed);
        (a.IsEmpty, b.IsEmpty) = (b.IsEmpty, a.IsEmpty);

        a.IsAudioOn = audioB;
        b.IsAudioOn = audioA;

        // El contador de la cápsula corre en el cuadro que quedó grabando.
        a.EnsureRecordingTicker();
        b.EnsureRecordingTicker();

        // Un cuadro que venía conectando (o reintentando) perdió su intento al
        // invalidarse la secuencia: se vuelve a lanzar en su nuevo lugar.
        a.RestartConnect();
        b.RestartConnect();
    }

    /// <summary>Retoma la conexión si el stream no viene en camino (tras un
    /// intercambio puede haber quedado una apertura a medio hacer).</summary>
    private void RestartConnect()
    {
        if (_assigned is null) return;
        if (Player.Status is FlyleafLib.MediaPlayer.Status.Opening
            or FlyleafLib.MediaPlayer.Status.Playing
            or FlyleafLib.MediaPlayer.Status.Paused) return;
        Status = "Reconectando…";
        IsConnecting = true;
        _ = ConnectAsync(_openSequence);
    }

    [RelayCommand]
    private void ToggleAudio() => IsAudioOn = !IsAudioOn;

    partial void OnIsAudioOnChanged(bool value)
    {
        // Flyleaf abre/cierra el stream de audio en caliente con este flag:
        // apagado no se decodifica (ahorra CPU en grillas grandes).
        Player.Config.Audio.Enabled = value;
        if (value) AudioActivated?.Invoke(this);
    }

    [RelayCommand]
    public void Clear()
    {
        _openSequence++;
        StopClipRecording(notify: true);
        ResetDigitalZoom();
        _assigned = null;
        AssignedChannel = null;
        IsAudioOn = false;
        Player.Stop();
        Title = null;
        Status = "";
        IsConnecting = false;
        IsEmpty = true;
    }

    public void Dispose()
    {
        _openSequence++;
        // Silencioso: la celda muere (cambio de división o cierre de la app);
        // la cápsula igual queda guardada, pero sin ventana que lo anuncie.
        StopClipRecording(notify: false);
        _assigned = null;
        Player.Dispose();
    }
}
