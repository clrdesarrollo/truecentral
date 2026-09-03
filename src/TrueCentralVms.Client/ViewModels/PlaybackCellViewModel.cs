using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Un canal dentro del módulo Reproducción: dueño de su player, de sus
/// segmentos del día y de su concesión. Los cuadros comparten la línea de
/// tiempo y la aguja: al posicionarse, TODOS abren el mismo instante (la
/// reproducción multicanal va sincronizada por hora del equipo, no por
/// relojes de video independientes).
/// </summary>
public partial class PlaybackCellViewModel : ObservableObject, IZoomTarget, IDisposable
{
    private readonly ApiClient _api;
    private readonly ClientSettings _settings;
    private string? _recordingFile;
    private int _openSequence;
    private double _speed = 1;

    /// <summary>
    /// Aperturas en vuelo. <c>Player.OpenAsync</c> cierra primero el tramo
    /// anterior, y ese cierre también dispara <c>PlaybackStopped</c>: sin este
    /// contador, saltar a otra hora mientras se reproduce se interpretaba como
    /// "terminó el tramo" y la reproducción quedaba detenida en el acto.
    /// </summary>
    private int _pendingOpens;

    public ChannelNode Channel { get; }
    public Player Player { get; }

    public string Title => $"{Channel.Device.Name} · {Channel.Channel.Name}";

    /// <summary>Tramos grabados del día visible (pinta su pista de la línea de tiempo).</summary>
    [ObservableProperty] private List<RecordingSegmentDto> _segments = [];
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isAudioOn;

    /// <summary>
    /// El tramo se está abriendo: pedir la concesión y que el equipo entregue
    /// el primer cuadro toma un momento, y sin aviso el cuadro parece colgado.
    /// </summary>
    [ObservableProperty] private bool _isOpening;

    /// <summary>
    /// false cuando el equipo no arranca exactamente en el instante pedido
    /// (ONVIF Perfil G reproduce desde el comienzo de la grabación).
    /// </summary>
    [ObservableProperty] private bool _isSeekExact = true;

    /// <summary>Hora local del equipo en la que arranca el tramo abierto: la
    /// aguja es esta hora más el reloj del player.</summary>
    public DateTime RangeStart { get; private set; }

    /// <summary>Hora que este cuadro está mostrando, o null si no reproduce.</summary>
    public DateTime? Playhead => IsPlaying && Player.CurTime > 0 ? RangeStart.AddTicks(Player.CurTime) : null;

    /// <summary>Se encendió el audio de este cuadro (el dueño silencia el resto).</summary>
    public event Action<PlaybackCellViewModel>? AudioActivated;

    /// <summary>El usuario cerró el cuadro con su ✕.</summary>
    public event Action<PlaybackCellViewModel>? CloseRequested;

    /// <summary>Se guardó un archivo local (título, glifo MDL2, ruta).</summary>
    public event Action<string, string, string>? MediaSaved;

    /// <summary>Cápsula en curso en este cuadro.</summary>
    [ObservableProperty] private bool _isRecordingClip;

    /// <summary>Tiempo transcurrido de la cápsula, para la barra del cuadro.</summary>
    [ObservableProperty] private string _recordingElapsed = "";

    public PlaybackCellViewModel(ApiClient api, ClientSettings settings, ChannelNode channel)
    {
        _api = api;
        _settings = settings;
        Channel = channel;

        var config = new Config();
        config.Player.AutoPlay = true;
        config.Audio.Enabled = false; // se enciende por cuadro con su botón
        config.Demuxer.FormatOpt["rtsp_transport"] = "tcp";
        // Arranque rápido, igual que en vivo: el análisis largo de FFmpeg
        // agregaría segundos antes del primer cuadro.
        config.Demuxer.FormatOpt["analyzeduration"] = "500000";
        config.Demuxer.FormatOpt["probesize"] = "524288";
        Player = new Player(config);
        Player.Audio.Volume = Math.Clamp(settings.DefaultVolume, 0, 100);
        Player.Config.Video.AspectRatio = settings.StretchVideo ? AspectRatio.Fill : AspectRatio.Keep;

        Player.OpenCompleted += (_, e) =>
        {
            // Si al descontar esta apertura todavía quedan otras en vuelo, la
            // que terminó es una que el operador dejó atrás (saltó a otra hora
            // o cambió la velocidad): su resultado ya no dice nada del cuadro.
            bool superseded = Interlocked.Decrement(ref _pendingOpens) > 0;
            if (!superseded) IsOpening = false;

            if (e.Success)
            {
                // La velocidad se reafirma con cada apertura: el player arranca
                // siempre en 1× y el usuario espera seguir en la que eligió.
                if (_speed != 1) ApplySpeed(_speed);
                if (!superseded) Status = "";
                return;
            }

            // Abrir el tramo nuevo CANCELA el anterior, y esa cancelación llega
            // como si fuera un fallo. Mostrarla ponía "No se pudo abrir la
            // grabación: Cancelled" sobre un video que estaba abriendo bien.
            if (superseded || IsCancellation(e.Error)) return;

            if (IsPlaying) Status = "No se pudo abrir la grabación: " + (e.Error ?? "error desconocido");
            IsPlaying = false;
            // El media server solo informa un error genérico al lector; el
            // motivo del EQUIPO (p. ej. 453: sin ancho de banda para otra
            // reproducción) lo tiene el servidor y es lo accionable.
            ExplainFailureAsync();
        };
        Player.PlaybackStopped += (_, _) =>
        {
            // El cierre del tramo anterior que hace la apertura nueva NO es el
            // fin de la reproducción: es el salto a otra hora.
            if (Volatile.Read(ref _pendingOpens) > 0) return;
            if (!IsPlaying) return;
            IsPlaying = false;
            Status = "Fin del tramo.";
        };
    }

    /// <summary>Ruta del media server de la última concesión: con ella se le
    /// pregunta al servidor por qué el equipo rechazó la reproducción.</summary>
    private string? _lastPath;

    /// <summary>
    /// Reemplaza el error técnico por el motivo real del equipo, si el
    /// servidor lo registró. Sin esperar a nadie: es un detalle del mensaje.
    /// </summary>
    private async void ExplainFailureAsync()
    {
        if (_lastPath is not { Length: > 0 } path) return;
        int sequence = _openSequence;
        if (await _api.GetPlaybackFailureAsync(path) is not { Length: > 0 } message) return;
        // El operador ya se movió a otra hora: su mensaje manda.
        if (sequence != _openSequence || IsPlaying) return;
        Status = message;
    }

    /// <summary>
    /// ¿El error de apertura es una cancelación? Flyleaf informa así la
    /// apertura que quedó atrás cuando se abre otra encima, y eso no es un
    /// fallo que mostrarle al operador.
    /// </summary>
    private static bool IsCancellation(string? error) =>
        error is { Length: > 0 } && error.Contains("cancel", StringComparison.OrdinalIgnoreCase);

    /// <summary>Tramos grabados del canal para un día (hora local del equipo).</summary>
    public async Task LoadSegmentsAsync(DateTime date)
    {
        try
        {
            Segments = await _api.GetRecordingSegmentsAsync(Channel.Device.Id, Channel.Channel.ChannelNumber, date);
            Status = Segments.Count == 0 ? "Sin grabaciones este día." : "";
        }
        catch (ApiException ex)
        {
            Segments = [];
            Status = ex.Message;
        }
    }

    /// <summary>
    /// Abre la grabación desde una hora local del equipo. Cada apertura pide
    /// una concesión NUEVA: el servidor monta la ruta de reproducción en el
    /// media server y entrega un token corto.
    /// </summary>
    public async Task<bool> OpenAtAsync(DateTime localTime, DateTime localEnd, double speed = 1)
    {
        int sequence = ++_openSequence;
        IsOpening = true;
        // La ventana protegida cubre TAMBIÉN la concesión: al pedirla, el
        // servidor cierra la ruta anterior de este canal (así el grabador
        // libera la sesión en el acto) y ese corte llegaría como "fin del
        // tramo" sobre un cuadro que en realidad está saltando de hora.
        Interlocked.Increment(ref _pendingOpens);
        try
        {
            // La velocidad viaja en la concesión: a más de 1× el servidor toma
            // la sesión RTSP del equipo y le pide que entregue más rápido. El
            // player acompaña con la misma velocidad (ApplySpeed más abajo):
            // llegan más cuadros por segundo y se presentan más rápido.
            _speed = speed;
            var grant = await _api.RequestPlaybackAsync(Channel.Device.Id, Channel.Channel.RtspChannel,
                localTime, localEnd, speed);
            if (sequence != _openSequence)
            {
                // El usuario ya se movió a otra hora: como no se llega a abrir,
                // nadie va a descontar esta apertura al completarse.
                Interlocked.Decrement(ref _pendingOpens);
                return false;
            }
            RangeStart = localTime;
            IsSeekExact = grant.ExactSeek;
            IsPlaying = true;
            Status = grant.ExactSeek
                ? ""
                : "El equipo reproduce desde el inicio de su grabación (ONVIF no permite posicionar).";
            Player.Config.Audio.Enabled = IsAudioOn;
            // La ruta identifica esta reproducción en el servidor: si el equipo
            // la rechaza, con ella se recupera el motivo.
            _lastPath = new Uri(grant.RtspUrl).AbsolutePath.Trim('/');
            Player.OpenAsync(grant.RtspUrl); // el descuento lo hace OpenCompleted
            return true;
        }
        catch (ApiException ex)
        {
            Interlocked.Decrement(ref _pendingOpens);
            if (sequence != _openSequence) return false;
            IsOpening = false;
            IsPlaying = false;
            Status = ex.Message;
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Captura y cápsula local (mismas carpetas de Configuración que el vivo)
    // ------------------------------------------------------------------

    /// <summary>
    /// Nombre de archivo con la hora de la GRABACIÓN, no la del reloj del
    /// puesto: en reproducción lo que identifica a la evidencia es el momento
    /// que se está viendo.
    /// </summary>
    private string MediaFileBase(string folder, string extension)
    {
        System.IO.Directory.CreateDirectory(folder);
        string name = string.Join("_", Title.Split(System.IO.Path.GetInvalidFileNameChars()));
        var moment = Playhead ?? RangeStart;
        return System.IO.Path.Combine(folder, $"{name} {moment:yyyy-MM-dd HH.mm.ss}{extension}");
    }

    /// <summary>Guarda el cuadro actual como imagen en la carpeta de capturas.</summary>
    [RelayCommand]
    private void Snapshot()
    {
        if (!IsPlaying) return;
        try
        {
            string extension = _settings.SnapshotFormat.Equals("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
            string file = MediaFileBase(_settings.EffectiveSnapshotFolder, extension);
            Player.TakeSnapshotToFile(file);
            ReportAudit("playback-snapshot", file);
            Status = "Captura guardada";
            MediaSaved?.Invoke("Captura guardada", "\uE722", file);
        }
        catch (Exception ex)
        {
            Status = "No se pudo guardar la captura: " + ex.Message;
        }
    }

    /// <summary>
    /// Graba una cápsula de lo que se está reproduciendo, tal como llega
    /// (remux sin recomprimir). Un clic parte, otro detiene. Es distinto de
    /// "Exportar MP4": eso pide al equipo un tramo por hora exacta; esto
    /// guarda lo que el operador está mirando ahora.
    /// </summary>
    [RelayCommand]
    private void ToggleRecording()
    {
        if (IsRecordingClip)
        {
            StopClipRecording(notify: true);
            return;
        }
        if (!IsPlaying) return;
        try
        {
            // Sin extensión: Flyleaf agrega la recomendada según el contenedor
            // (y actualiza la ruta por el parámetro ref).
            string file = MediaFileBase(_settings.EffectiveRecordingFolder, "");
            Player.StartRecording(ref file, useRecommendedExtension: true);
            _recordingFile = file;
            IsRecordingClip = true;
            RunRecordingTicker();
        }
        catch (Exception ex)
        {
            Status = "No se pudo iniciar la grabación: " + ex.Message;
        }
    }

    private void StopClipRecording(bool notify)
    {
        if (!IsRecordingClip) return;
        try { Player.StopRecording(); }
        catch (Exception) { /* ya estaba cerrada: igual se avisa el archivo */ }
        IsRecordingClip = false;

        string? file = _recordingFile;
        _recordingFile = null;
        if (file is null) return;
        ReportAudit("playback-clip-saved", file);
        if (!notify) return;
        Status = "Grabación guardada";
        MediaSaved?.Invoke("Grabación guardada", "\uE714", file);
    }

    /// <summary>Deja el evento en la bitácora de auditoría del servidor, con la
    /// hora de la grabación que se estaba viendo; nunca molesta si falla.</summary>
    private void ReportAudit(string action, string file)
    {
        var moment = Playhead ?? RangeStart;
        _ = _api.ReportAuditEventAsync(new ClientAuditEventDto(
            action,
            Channel.Device.Id, Channel.Device.Name,
            Channel.Channel.ChannelNumber, Channel.Channel.Name,
            file, $"instante grabado: {moment:dd-MM-yyyy HH:mm:ss}"));
    }

    /// <summary>Contador de la cápsula en la barra del cuadro (mm:ss).</summary>
    private async void RunRecordingTicker()
    {
        var started = DateTime.UtcNow;
        while (IsRecordingClip)
        {
            var elapsed = DateTime.UtcNow - started;
            RecordingElapsed = elapsed.TotalHours >= 1
                ? elapsed.ToString(@"h\:mm\:ss")
                : elapsed.ToString(@"mm\:ss");
            await Task.Delay(500);
        }
        RecordingElapsed = "";
    }

    // ------------------------------------------------------------------
    // Zoom digital (mismo comportamiento que la Vista en Vivo)
    // ------------------------------------------------------------------

    /// <summary>El zoom vale también con la reproducción pausada (revisar un detalle).</summary>
    public bool CanDigitalZoom => true;

    /// <summary>Acerca el área marcada con el mouse (píxeles físicos de la ventana de video).</summary>
    public void DigitalZoomToArea(System.Windows.Rect areaPx)
    {
        double zoom = DigitalZoom.ApplyArea(Player, areaPx);
        Status = zoom <= DigitalZoom.NoZoom ? "" : DigitalZoom.Describe(zoom);
    }

    /// <summary>Rueda del mouse: un paso de zoom que deja quieto el punto bajo el cursor.</summary>
    public void DigitalZoomStepAt(System.Windows.Point pointPx, bool zoomIn)
    {
        double zoom = DigitalZoom.StepAtPoint(Player, zoomIn, pointPx);
        Status = zoom <= DigitalZoom.NoZoom ? "" : DigitalZoom.Describe(zoom);
    }

    public void ResetDigitalZoom()
    {
        DigitalZoom.Reset(Player);
        Status = "";
    }

    public void Pause() => Player.Pause();

    public void Resume() => Player.Play();

    /// <summary>Velocidad de reproducción (1 = normal).</summary>
    public void ApplySpeed(double speed)
    {
        _speed = speed;
        try
        {
            Player.Speed = speed;
        }
        catch
        {
            // Velocidad fuera de rango del decodificador: se ignora y sigue en la actual.
        }
    }

    public void Stop()
    {
        _openSequence++;
        StopClipRecording(notify: true);
        IsOpening = false;
        IsPlaying = false;
        Player.Stop();
        Status = "";
    }

    [RelayCommand]
    private void ToggleAudio()
    {
        IsAudioOn = !IsAudioOn;
        Player.Config.Audio.Enabled = IsAudioOn;
        if (IsAudioOn) AudioActivated?.Invoke(this);
    }

    /// <summary>Silencia el cuadro cuando otro toma el audio (exclusivo).</summary>
    public void MuteQuietly()
    {
        IsAudioOn = false;
        Player.Config.Audio.Enabled = false;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this);

    public void Dispose()
    {
        _openSequence++;
        StopClipRecording(notify: false);
        Player.Dispose();
    }
}
