using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace TrueCentralVms.Client.Services;

/// <summary>Monitor físico del PC, en coordenadas físicas del escritorio virtual (independiente del escalado DPI).</summary>
public sealed record MonitorInfo(int Index, string Device, int X, int Y, int Width, int Height, bool Primary)
{
    public override string ToString() =>
        $"Pantalla {Index + 1} — {Width}×{Height}{(Primary ? " (principal)" : "")}";
}

/// <summary>
/// Proyección de pantalla al video wall: captura un monitor con FFmpeg
/// (gdigrab → NVENC, con reserva a x264), lo publica como RTSP en un MediaMTX
/// local y el decoder lo consume como cualquier fuente RTSP del sistema.
///
/// Requisito de red: el DECODER inicia la conexión hacia este PC, así que el
/// PC debe ser alcanzable desde la red del decoder (misma red, o ruta/
/// port-forward del router). El firewall de Windows debe permitir la entrada
/// a mediamtx.exe (puerto 8554 TCP).
///
/// Las herramientas se buscan en la carpeta "tools" junto al ejecutable, en la
/// carpeta "tools" del repositorio (desarrollo) o en el PATH:
///   tools\mediamtx\mediamtx.exe   ·   tools\ffmpeg\bin\ffmpeg.exe
/// </summary>
public sealed class ScreenProjectionService : IDisposable
{
    public const int RtspPort = 8554;

    private Process? _ffmpeg;
    private Process? _mediamtx;
    private bool _mediamtxSpawned;
    private bool _intentionalKill;
    private int _fps = 25;

    public bool IsProjecting => _ffmpeg is { HasExited: false };
    public MonitorInfo? ActiveMonitor { get; private set; }

    /// <summary>Descripción de lo que se está transmitiendo ("Pantalla 1 — …" o el nombre del archivo).</summary>
    public string? ActiveDescription { get; private set; }

    /// <summary>Se dispara cuando ffmpeg muere sin que lo hayamos detenido nosotros
    /// (p. ej. un formato cuyo bucle interno falla al llegar al final).</summary>
    public event Action? ProjectionUnexpectedlyEnded;

    /// <summary>Se dispara cuando un video SIN bucle llega al final (ffmpeg
    /// termina normalmente).</summary>
    public event Action? FilePlaybackFinished;

    /// <summary>Reproducir los archivos en bucle (-stream_loop -1). Se aplica al
    /// próximo inicio/salto.</summary>
    public bool LoopFile { get; set; } = true;

    // --- Detección de lectores (el decoder) a partir del log de MediaMTX ---
    // MediaMTX escribe "[session X] is reading from path 'ruta'" cuando un
    // cliente RTSP empieza a leer. Con eso el cliente sabe cuándo el muro
    // realmente empezó a mostrar la transmisión (y no solo que ffmpeg arrancó).
    // Se cuentan los lectores desde el último inicio (una sesión por ventana).
    private readonly object _readerLock = new();
    private TaskCompletionSource<bool>? _readerTcs;
    private int _readersSinceArm;
    private int _readersExpected = 1;

    /// <summary>Lectores (sesiones RTSP del decoder) detectados desde el último inicio.</summary>
    public int ReadersSinceStart { get { lock (_readerLock) return _readersSinceArm; } }

    /// <summary>
    /// True si el MediaMTX en uso lo lanzó este proceso y por tanto podemos
    /// observar su log (si ya había uno escuchando en 8554 no hay visibilidad).
    /// </summary>
    public bool CanObserveReaders => _mediamtxSpawned && _mediamtx is { HasExited: false };

    // --- Estado de reproducción de archivo (pausa/posición para los controles) ---
    private string? _activeFilePath;
    private double _fileOffset;         // posición al iniciar/reanudar (s)
    private DateTime _fileStartedUtc;   // cuándo se (re)inició la reproducción

    /// <summary>Duración del video en segundos (0 si no se pudo determinar).</summary>
    public double FileDuration { get; private set; }

    public bool IsFileProjection => _activeFilePath is not null;
    public bool IsFilePaused { get; private set; }

    /// <summary>Posición actual de reproducción en segundos (envuelve en el bucle).</summary>
    public double FilePosition
    {
        get
        {
            if (_activeFilePath is null) return 0;
            double position = IsFilePaused
                ? _fileOffset
                : _fileOffset + (DateTime.UtcNow - _fileStartedUtc).TotalSeconds;
            if (FileDuration <= 0) return position;
            return LoopFile ? position % FileDuration : Math.Min(position, FileDuration);
        }
    }

    /// <summary>Ruta RTSP publicada (p. ej. "pantalla-jdlf-laptop").</summary>
    public string PublishPath { get; } = "pantalla-" + Sanitize(Environment.MachineName);

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        return sb.ToString().Trim('-');
    }

    private static string LogDir
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CLRTrueCentral", "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // -----------------------------------------------------------------------
    // Descubrimiento de herramientas
    // -----------------------------------------------------------------------

    /// <summary>Busca una herramienta en tools\ junto al exe, subiendo por el árbol (repo) y en el PATH.</summary>
    public static string? FindTool(string relativePath)
    {
        var candidates = new List<string>();
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            candidates.Add(Path.Combine(dir, "tools", relativePath));
            dir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir));
        }
        foreach (string candidate in candidates)
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);

        // PATH del sistema (solo el nombre del archivo).
        string fileName = Path.GetFileName(relativePath);
        foreach (string pathDir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string full = Path.Combine(pathDir.Trim(), fileName);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    // -----------------------------------------------------------------------
    // Monitores (EnumDisplaySettings entrega píxeles físicos, sin líos de DPI)
    // -----------------------------------------------------------------------

    public static List<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            if ((device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
            {
                var mode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
                if (EnumDisplaySettingsEx(device.DeviceName, ENUM_CURRENT_SETTINGS, ref mode, 0) &&
                    mode.dmPelsWidth > 0 && mode.dmPelsHeight > 0)
                {
                    monitors.Add(new MonitorInfo(
                        monitors.Count, device.DeviceName,
                        mode.dmPositionX, mode.dmPositionY,
                        (int)mode.dmPelsWidth, (int)mode.dmPelsHeight,
                        (device.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0));
                }
            }
            device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        }
        return monitors;
    }

    // -----------------------------------------------------------------------
    // IPs locales candidatas (la primera es la de la ruta por defecto)
    // -----------------------------------------------------------------------

    public static List<string> GetLocalIPv4Candidates()
    {
        var list = new List<string>();
        try
        {
            // Truco estándar: "conectar" un socket UDP no envía nada pero
            // resuelve la IP local de la interfaz con ruta por defecto.
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 53);
            if (socket.LocalEndPoint is IPEndPoint ep) list.Add(ep.Address.ToString());
        }
        catch { }

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                string ip = addr.Address.ToString();
                if (ip.StartsWith("169.254.") || list.Contains(ip)) continue;
                list.Add(ip);
            }
        }
        return list;
    }

    // -----------------------------------------------------------------------
    // Ciclo de vida
    // -----------------------------------------------------------------------

    /// <summary>Inicia MediaMTX (si no hay uno escuchando) y la transmisión de un monitor.</summary>
    public Task StartMonitorAsync(MonitorInfo monitor, int fps = 25)
    {
        _activeFilePath = null;
        IsFilePaused = false;
        FileDuration = 0;
        return StartCoreAsync(BuildMonitorInput(monitor, fps), MonitorFilters(), fps, monitor.ToString(), monitor);
    }

    /// <summary>Inicia MediaMTX (si hace falta) y la transmisión de un archivo de video en bucle.</summary>
    public async Task StartFileAsync(string filePath, int fps = 25)
    {
        if (!File.Exists(filePath))
            throw new InvalidOperationException($"No existe el archivo:\n{filePath}");

        FileDuration = await ProbeDurationAsync(filePath);
        await StartCoreAsync(BuildFileInput(filePath, 0), FileFilters(fps), fps,
            Path.GetFileName(filePath), monitor: null);

        _activeFilePath = filePath;
        _fileOffset = 0;
        _fileStartedUtc = DateTime.UtcNow;
        IsFilePaused = false;
    }

    /// <summary>
    /// Pausa el video. El stream RTSP NO se corta: si se cortara, MediaMTX
    /// cerraría la sesión del decoder, éste reintentaría, recibiría 404 y
    /// mostraría "RTSP interaction failed" en el muro. En su lugar se extrae el
    /// fotograma actual y se publica en bucle como stream estático (imagen
    /// congelada real). La posición se conserva para reanudar.
    /// El decoder debe re-engancharse después (re-asignar la ventana), igual
    /// que tras un salto, porque el publicador cambia de proceso.
    /// </summary>
    public async Task PauseFileAsync()
    {
        if (!IsFileProjection || IsFilePaused) return;
        string filePath = _activeFilePath!;
        double position = FilePosition;

        // El fotograma se extrae ANTES de cortar la reproducción para que el
        // hueco sin publicador sea lo más corto posible.
        string? frame = await ExtractFrameAsync(filePath, position);

        _fileOffset = position;
        IsFilePaused = true;
        KillFfmpeg();

        await StartStillFrameAsync(frame);
    }

    /// <summary>Extrae a PNG el fotograma en <paramref name="seconds"/> (null si falla).</summary>
    private static async Task<string?> ExtractFrameAsync(string filePath, double seconds)
    {
        string? ffmpeg = FindTool(Path.Combine("ffmpeg", "bin", "ffmpeg.exe"));
        if (ffmpeg is null) return null;

        string framePath = Path.Combine(Path.GetDirectoryName(LogDir)!, "pause-frame.png");
        string seek = seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-hide_banner -loglevel error -ss {seek} -i \"{filePath}\" -frames:v 1 " +
                            $"-vf \"{MonitorFilters()}\" -y \"{framePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null) return null;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(8)).Token);
            return process.ExitCode == 0 && File.Exists(framePath) ? framePath : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Publica en bucle un fotograma fijo (o negro si no hay fotograma) para
    /// mantener vivo el path RTSP mientras el video está en pausa.
    /// </summary>
    private async Task StartStillFrameAsync(string? framePath)
    {
        string? ffmpeg = FindTool(Path.Combine("ffmpeg", "bin", "ffmpeg.exe"))
            ?? throw new InvalidOperationException("No se encontró ffmpeg.exe.");

        // -re + -loop 1 + -framerate: emite la imagen a ritmo real como si fuera video.
        string input = framePath is not null
            ? $"-re -loop 1 -framerate {_fps} -i \"{framePath}\""
            : $"-re -f lavfi -i color=c=black:s=1280x720:r={_fps}";

        string? nvencError = await TryStartFfmpegAsync(ffmpeg, input, FileFilters(_fps), _fps, useNvenc: true);
        if (nvencError is not null)
        {
            string? x264Error = await TryStartFfmpegAsync(ffmpeg, input, FileFilters(_fps), _fps, useNvenc: false);
            if (x264Error is not null)
                throw new InvalidOperationException(
                    $"No se pudo publicar la imagen congelada.\nNVENC: {nvencError}\nx264: {x264Error}");
        }
    }

    /// <summary>
    /// Salta a una posición (segundos) y reproduce desde ahí. También sirve
    /// para reanudar tras una pausa (seconds = FilePosition). El decoder debe
    /// re-engancharse después (re-asignar la ventana).
    /// </summary>
    public async Task SeekFileAsync(double seconds)
    {
        string filePath = _activeFilePath
            ?? throw new InvalidOperationException("No hay un video en proyección.");
        if (FileDuration > 0)
            seconds = Math.Clamp(seconds, 0, Math.Max(0, FileDuration - 0.5));

        KillFfmpeg();
        await StartCoreAsync(BuildFileInput(filePath, seconds), FileFilters(_fps), _fps,
            Path.GetFileName(filePath), monitor: null);

        _fileOffset = seconds;
        _fileStartedUtc = DateTime.UtcNow;
        IsFilePaused = false;
    }

    private static string BuildMonitorInput(MonitorInfo monitor, int fps)
    {
        // Dimensiones pares (requisito de yuv420p).
        int width = monitor.Width & ~1;
        int height = monitor.Height & ~1;
        return $"-f gdigrab -framerate {fps} -offset_x {monitor.X} -offset_y {monitor.Y} " +
               $"-video_size {width}x{height} -i desktop";
    }

    private string BuildFileInput(string filePath, double startAt)
    {
        // -re reproduce a velocidad real; -stream_loop -1 repite en bucle (si
        // no hay bucle, ffmpeg termina al llegar al final y se avisa con
        // FilePlaybackFinished).
        string seek = startAt > 0.05
            ? $"-ss {startAt.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} "
            : "";
        string loop = LoopFile ? "-stream_loop -1 " : "";
        return $"{seek}-re {loop}-i \"{filePath}\" -an";
    }

    private static string MonitorFilters() => "scale='min(1920,iw)':-2";

    /// <summary>
    /// Para archivos, además de escalar se normaliza el ritmo (fps) y se
    /// REGENERAN los timestamps (setpts): al reiniciar el bucle los PTS del
    /// archivo vuelven a cero y ese salto congela a los decoders; con PTS
    /// continuos el bucle es imperceptible.
    /// </summary>
    private static string FileFilters(int fps) =>
        $"scale='min(1920,iw)':-2,fps={fps},setpts=N/({fps}*TB)";

    /// <summary>
    /// Espera a que un lector (el decoder) empiece a leer la ruta publicada,
    /// según el log de MediaMTX. Se arma en cada inicio de transmisión, así que
    /// solo cuenta lectores conectados DESPUÉS del último Start.
    /// Devuelve true si se detectó, false si venció el tiempo y null si no hay
    /// forma de observarlo (MediaMTX externo).
    /// </summary>
    public async Task<bool?> WaitForReadersAsync(int count, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!CanObserveReaders) return null;

        TaskCompletionSource<bool>? tcs;
        lock (_readerLock)
        {
            _readersExpected = Math.Max(1, count);
            tcs = _readerTcs;
            if (tcs is not null && _readersSinceArm >= _readersExpected) tcs.TrySetResult(true);
        }
        if (tcs is null) return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    private void ArmReaderWatch()
    {
        lock (_readerLock)
        {
            _readersSinceArm = 0;
            _readerTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void OnMediaMtxLine(string line)
    {
        // Ej.: "... INF [RTSP] [session b517eebe] is reading from path 'pantalla-x', with UDP, 1 track (H264)"
        if (!line.Contains("is reading from path '" + PublishPath + "'", StringComparison.Ordinal)) return;
        lock (_readerLock)
        {
            _readersSinceArm++;
            if (_readersSinceArm >= _readersExpected) _readerTcs?.TrySetResult(true);
        }
    }

    private async Task StartCoreAsync(string inputArgs, string filters, int fps, string description, MonitorInfo? monitor)
    {
        if (IsProjecting) KillFfmpeg();
        _fps = fps;
        ArmReaderWatch();

        string? ffmpeg = FindTool(Path.Combine("ffmpeg", "bin", "ffmpeg.exe"))
            ?? throw new InvalidOperationException(
                "No se encontró ffmpeg.exe. Copie FFmpeg en la carpeta tools\\ffmpeg\\bin del proyecto o del cliente.");

        await EnsureMediaMtxAsync();

        // NVENC primero (GPU NVIDIA, casi sin CPU); si no está disponible se
        // reintenta con x264 por software.
        string? nvencError = await TryStartFfmpegAsync(ffmpeg, inputArgs, filters, fps, useNvenc: true);
        if (nvencError is not null)
        {
            string? x264Error = await TryStartFfmpegAsync(ffmpeg, inputArgs, filters, fps, useNvenc: false);
            if (x264Error is not null)
                throw new InvalidOperationException(
                    $"FFmpeg no pudo iniciar la transmisión.\nNVENC: {nvencError}\nx264: {x264Error}");
        }

        ActiveMonitor = monitor;
        ActiveDescription = description;
    }

    /// <summary>Detiene la transmisión (la fuente queda registrada; el decoder verá el stream caído).</summary>
    public void Stop()
    {
        KillFfmpeg();
        ActiveMonitor = null;
        ActiveDescription = null;
        _activeFilePath = null;
        IsFilePaused = false;
        FileDuration = 0;
    }

    /// <summary>Duración de un archivo de video en segundos vía ffprobe (0 si falla).</summary>
    private static async Task<double> ProbeDurationAsync(string filePath)
    {
        string? ffprobe = FindTool(Path.Combine("ffmpeg", "bin", "ffprobe.exe"));
        if (ffprobe is null) return 0;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -show_entries format=duration -of csv=p=0 \"{filePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null) return 0;
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
            return double.TryParse(output.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double duration) && duration > 0
                ? duration : 0;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        Stop();
        // El MediaMTX solo se cierra si lo lanzamos nosotros (podría estar
        // sirviendo otras cosas si el usuario lo tenía corriendo).
        if (_mediamtxSpawned) KillProcess(ref _mediamtx);
    }

    private static void KillProcess(ref Process? process)
    {
        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch { }
        finally
        {
            process?.Dispose();
            process = null;
        }
    }

    private static bool IsPortListening(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(IPAddress.Loopback, port).Wait(400);
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureMediaMtxAsync()
    {
        if (IsPortListening(RtspPort)) return; // ya hay un servidor RTSP local

        string mediamtx = FindTool(Path.Combine("mediamtx", "mediamtx.exe"))
            ?? throw new InvalidOperationException(
                "No se encontró mediamtx.exe. Copie MediaMTX en la carpeta tools\\mediamtx del proyecto o del cliente.");

        _mediamtx = Process.Start(new ProcessStartInfo
        {
            FileName = mediamtx,
            WorkingDirectory = Path.GetDirectoryName(mediamtx)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("No se pudo iniciar MediaMTX.");
        _mediamtxSpawned = true;
        string logPath = Path.Combine(LogDir, "mediamtx.log");
        DrainToLog(_mediamtx, logPath, OnMediaMtxLine);

        for (int i = 0; i < 25; i++)
        {
            if (IsPortListening(RtspPort)) return;
            if (_mediamtx.HasExited)
            {
                // Pequeña espera para que el drenado de stdout/stderr alcance a
                // escribir las últimas líneas (la causa suele estar ahí).
                await Task.Delay(300);
                string tail = ReadLogTail(logPath, onlyErrors: true);
                throw new InvalidOperationException(
                    $"MediaMTX terminó inesperadamente (código {_mediamtx.ExitCode})." +
                    (string.IsNullOrWhiteSpace(tail) ? "" : $"\n\n{tail}") +
                    $"\n\nLog completo: {logPath}");
            }
            await Task.Delay(200);
        }
        throw new InvalidOperationException("MediaMTX no abrió el puerto RTSP 8554.");
    }

    /// <summary>Lanza ffmpeg y verifica que sobreviva el arranque. Devuelve null si quedó corriendo, o el error.</summary>
    private async Task<string?> TryStartFfmpegAsync(string ffmpeg, string inputArgs, string filters, int fps, bool useNvenc)
    {
        // Prioridad: latencia. Sin B-frames, IDR cada 1 s, CBR con buffer chico
        // (tune ull/zerolatency). El ancho se limita a 1920 para no exceder el
        // presupuesto de decodificación del muro.
        string encoder = useNvenc
            ? "-c:v h264_nvenc -preset p2 -tune ull -rc cbr -b:v 6M -maxrate 6M -bufsize 2M -profile:v main"
            : "-c:v libx264 -preset veryfast -tune zerolatency -b:v 6M -maxrate 6M -bufsize 2M -profile:v main";

        string args =
            $"-hide_banner -loglevel warning " +
            $"{inputArgs} " +
            $"-vf \"{filters}\" " +
            $"{encoder} -g {fps} -bf 0 -pix_fmt yuv420p " +
            $"-f rtsp -rtsp_transport tcp rtsp://127.0.0.1:{RtspPort}/{PublishPath}";

        string logPath = Path.Combine(LogDir, $"proyeccion-{(useNvenc ? "nvenc" : "x264")}.log");
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        if (process is null) return "no se pudo crear el proceso";

        DrainToLog(process, logPath);

        // Si el encoder no existe o gdigrab falla, ffmpeg muere en el arranque.
        await Task.Delay(2500);
        if (process.HasExited)
        {
            int exitCode = SafeExitCode(process);
            // Video sin bucle iniciado a menos de 2,5 s del final: terminó
            // normalmente antes de la comprobación. No es un fallo de arranque.
            if (exitCode == 0 && _activeFilePath is not null && !LoopFile)
            {
                process.Dispose();
                _ffmpeg = null;
                _intentionalKill = false;
                _ = Task.Run(() => FilePlaybackFinished?.Invoke());
                return null;
            }
            string tail = ReadLogTail(logPath);
            process.Dispose();
            return string.IsNullOrWhiteSpace(tail) ? $"terminó con código {exitCode}" : tail;
        }

        // Aviso de muerte inesperada (formatos cuyo bucle interno falla, errores
        // de captura, etc.) para que la UI pueda relanzar la reproducción.
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            // En pausa el proceso activo es el de la imagen congelada; su caída
            // no es un "fin de reproducción" (no hay que reiniciar desde 0).
            if (_intentionalKill || IsFilePaused || !ReferenceEquals(process, _ffmpeg)) return;

            // Video sin bucle que llegó al final: fin normal (código 0).
            bool finished = IsFileProjection && !LoopFile && SafeExitCode(process) == 0;
            if (finished) FilePlaybackFinished?.Invoke();
            else ProjectionUnexpectedlyEnded?.Invoke();
        };

        _ffmpeg = process;
        _intentionalKill = false;
        return null;
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    /// <summary>Mata el ffmpeg activo marcándolo como detención intencional.</summary>
    private void KillFfmpeg()
    {
        _intentionalKill = true;
        KillProcess(ref _ffmpeg);
    }

    /// <summary>Vuelca stdout/stderr del proceso a un archivo de log (sobrescribe por sesión).</summary>
    private static void DrainToLog(Process process, string logPath, Action<string>? onLine = null)
    {
        var writer = new StreamWriter(logPath, append: false) { AutoFlush = true };
        int open = 2;
        void HandleLine(string? line)
        {
            if (line is not null)
            {
                lock (writer) writer.WriteLine(line);
                try { onLine?.Invoke(line); } catch { /* el observador no debe tumbar el drenado */ }
            }
            else if (Interlocked.Decrement(ref open) == 0)
            {
                lock (writer) writer.Dispose();
            }
        }
        process.OutputDataReceived += (_, e) => HandleLine(e.Data);
        process.ErrorDataReceived += (_, e) => HandleLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    /// <summary>
    /// Últimas líneas del log. Con <paramref name="onlyErrors"/> prioriza las
    /// líneas WAR/ERR de MediaMTX (si las hay) sobre las informativas.
    /// </summary>
    private static string ReadLogTail(string logPath, bool onlyErrors = false)
    {
        try
        {
            var lines = File.ReadAllLines(logPath);
            if (onlyErrors)
            {
                var problems = lines.Where(l => l.Contains(" WAR ") || l.Contains(" ERR ")).ToArray();
                if (problems.Length > 0)
                    return string.Join("\n", problems.TakeLast(4));
                return string.Join("\n", lines.TakeLast(3));
            }
            return string.Join(" · ", lines.TakeLast(3));
        }
        catch
        {
            return "";
        }
    }

    // -----------------------------------------------------------------------
    // Interop de monitores
    // -----------------------------------------------------------------------

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType;
        public uint dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsEx(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, uint dwFlags);
}
