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
public partial class VideoCellViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly ApiClient _api;
    private readonly ClientSettings _settings;
    private ChannelNode? _assigned;
    private int _openSequence;
    private bool _retryPending;

    public Player Player { get; }

    /// <summary>Se encendió el audio de este cuadro (el dueño silencia el resto).</summary>
    public event Action<VideoCellViewModel>? AudioActivated;

    /// <summary>Se guardó un archivo local (título, glifo MDL2, ruta completa):
    /// el shell muestra la notificación con el link a la ubicación.</summary>
    public event Action<string, string, string>? MediaSaved;

    /// <summary>Ruta de la cápsula en curso (Flyleaf le agrega la extensión).</summary>
    private string? _recordingFile;

    [ObservableProperty] private string? _title;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isEmpty = true;
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
        Player = new Player(config);
        Player.Audio.Volume = Math.Clamp(settings.DefaultVolume, 0, 100);

        Player.OpenCompleted += (_, e) =>
        {
            if (_assigned is null) return;
            if (e.Success) { Status = ""; return; }
            Status = "No se pudo abrir el video: " + (e.Error ?? "error desconocido");
            ScheduleRetry(_openSequence);
        };

        // Fin de reproducción no pedido (caída de red, equipo apagado,
        // expulsión desde el panel): reintentar. Clear/Dispose/reasignación
        // invalidan la secuencia ANTES de detener, así que no reintentan.
        Player.PlaybackStopped += (_, e) =>
        {
            // Flyleaf corta la grabación junto con el stream: reflejar y avisar
            // (la cápsula quedó guardada hasta el momento del corte).
            if (IsRecordingClip && !Player.IsRecording)
            {
                IsRecordingClip = false;
                NotifyClipSaved(notify: true);
            }
            if (_assigned is null) return;
            if (!e.Success) Status = "Sin señal — reintentando…";
            ScheduleRetry(_openSequence);
        };
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
        await ConnectAsync(sequence);
    }

    /// <summary>
    /// Alterna entre stream principal y secundario del canal en pantalla.
    /// Reabre por el flujo normal: concesión nueva y misma lógica de
    /// reintentos; el fan-out de MediaMTX hace el resto.
    /// </summary>
    [RelayCommand]
    private async Task SwitchProfileAsync()
    {
        if (_assigned is not { } node) return;
        await OpenAsync(node, Profile == StreamProfile.Main ? StreamProfile.Sub : StreamProfile.Main);
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

    /// <summary>Zoom en % (100 = sin zoom). Flyleaf recorta el viewport en GPU.</summary>
    public double DigitalZoom => Player.Config.Video.Zoom;

    /// <summary>Acerca o aleja centrado en el punto del cursor (normalizado 0..1).</summary>
    public void DigitalZoomStep(bool zoomIn, System.Windows.Point center)
    {
        double target = Math.Clamp(Player.Config.Video.Zoom + (zoomIn ? 20 : -20), 100, 600);
        if (target <= 100)
            ResetDigitalZoom();
        else
            Player.Config.Video.SetZoomAndCenter(target, center);
        FlashStatus(target <= 100 ? "Zoom 1x" : $"Zoom digital {target / 100.0:0.#}x");
    }

    private void ResetDigitalZoom()
    {
        Player.Config.Video.Zoom = 100;
        Player.Config.Video.ZoomCenter = new System.Windows.Point(0.5, 0.5);
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
