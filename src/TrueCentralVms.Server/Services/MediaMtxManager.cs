using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Orquestador del media server embebido (MediaMTX v1.20, proceso hijo).
///
/// El servidor genera mediamtx.runtime.yml con UNA ruta por canal habilitado y
/// perfil (ch/{deviceId}/{rtspChannel}/{main|sub}) cuyo source es la URL RTSP
/// del fabricante. Con sourceOnDemand, MediaMTX abre UN solo pull hacia el
/// equipo cuando llega el primer lector y lo comparte con N lectores: ese es
/// el fan-out "streaming media server" del producto.
///
/// Seguridad: toda lectura pasa por la autorización HTTP delegada a
/// /api/streaming/auth (tokens de StreamTokenService); la API de control
/// queda en 127.0.0.1; RTMP/HLS/WebRTC/SRT/MoQ deshabilitados. Las
/// credenciales de los equipos SOLO existen dentro del yml generado.
/// </summary>
public sealed partial class MediaMtxManager(
    IServiceScopeFactory scopeFactory,
    DriverRegistry drivers,
    CredentialProtector protector,
    IConfiguration config,
    IHostEnvironment env,
    ILogger<MediaMtxManager> logger) : IHostedService
{
    private readonly SemaphoreSlim _configLock = new(1, 1);
    private Process? _process;
    private volatile bool _stopping;
    private int _consecutiveFailures;

    public int RtspPort => config.GetValue("Streaming:RtspPort", 8654);
    public int ApiPort => config.GetValue("Streaming:ApiPort", 9911);
    public string ApiBaseUrl => $"http://127.0.0.1:{ApiPort}";

    /// <summary>
    /// Secreto por arranque que autoriza a los relés FFmpeg locales (rutas con
    /// runOnDemand) a PUBLICAR en MediaMTX. Solo lo conocen este proceso y las
    /// líneas de comando que él mismo genera; el callback de autorización lo
    /// exige junto con conexión desde loopback.
    /// </summary>
    public string PublishSecret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private string? _ffmpegPath;
    private bool _ffmpegWarned;

    public bool IsRunning => _process is { HasExited: false };

    private string ConfigPath => Path.Combine(env.ContentRootPath, "mediamtx.runtime.yml");

    public async Task StartAsync(CancellationToken ct)
    {
        string? exe = FindExecutable();
        if (exe is null)
        {
            logger.LogError(
                "No se encontró tools\\mediamtx\\mediamtx.exe: el streaming en vivo queda deshabilitado. " +
                "Copie MediaMTX v1.20 a tools\\mediamtx (junto al ejecutable o en la raíz del repositorio).");
            return;
        }

        KillOrphans(exe);
        await WriteConfigAsync(ct);
        StartProcess(exe);
    }

    /// <summary>
    /// Mata instancias huérfanas de NUESTRO MediaMTX (mismo ejecutable) que
    /// hayan quedado de un cierre abrupto anterior: seguirían ocupando los
    /// puertos y el nuevo hijo no podría enlazarlos.
    /// </summary>
    private void KillOrphans(string exe)
    {
        foreach (var orphan in Process.GetProcessesByName("mediamtx"))
        {
            try
            {
                if (string.Equals(orphan.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("MediaMTX huérfano de una ejecución anterior (PID {Pid}): se termina.", orphan.Id);
                    orphan.Kill(entireProcessTree: true);
                    orphan.WaitForExit(5000);
                }
            }
            catch { /* proceso ajeno o sin permisos: se ignora */ }
            finally { orphan.Dispose(); }
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        _stopping = true;
        try
        {
            if (_process is { HasExited: false } p)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
                logger.LogInformation("MediaMTX detenido.");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo detener MediaMTX limpiamente.");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Regenera las rutas tras un cambio de dispositivos/canales. MediaMTX
    /// vigila su archivo de configuración y aplica los cambios en caliente,
    /// sin cortar las sesiones de rutas que no cambiaron.
    /// </summary>
    public async Task RefreshPathsAsync(CancellationToken ct = default)
    {
        await WriteConfigAsync(ct);
        logger.LogInformation("Rutas de MediaMTX regeneradas.");
    }

    /// <summary>tools\mediamtx\mediamtx.exe: junto al ejecutable, subiendo por el árbol (repo).</summary>
    private static string? FindExecutable()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "tools", "mediamtx", "mediamtx.exe");
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>tools\ffmpeg\bin\ffmpeg.exe (relé para cámaras con SDP inválido).</summary>
    /// <summary>ffmpeg.exe distribuido con el sistema, o null si falta (lo
    /// usan los relés de canales con proxy y la descarga de clips).</summary>
    public static string? LocateFfmpeg() => FindFfmpeg();

    private static string? FindFfmpeg()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "tools", "ffmpeg", "bin", "ffmpeg.exe");
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private async Task WriteConfigAsync(CancellationToken ct)
    {
        await _configLock.WaitAsync(ct);
        try
        {
            var yml = new StringBuilder();
            yml.AppendLine($"""
                # Generado por CLR TrueCentral VMS: NO EDITAR A MANO (se sobreescribe).
                # Contiene credenciales de dispositivos: no compartir ni versionar.
                logLevel: info
                logDestinations: [stdout]

                api: yes
                apiAddress: 127.0.0.1:{ApiPort}
                metrics: no
                pprof: no
                playback: no

                # Toda lectura se autoriza contra el servidor TrueCentral (tokens
                # de corta vida). La API queda excluida: solo escucha en loopback.
                authMethod: http
                authHTTPAddress: http://127.0.0.1:{config.GetValue("Streaming:ServerPort", 5090)}/api/streaming/auth
                authHTTPExclude:
                - action: api
                - action: metrics
                - action: pprof

                rtsp: yes
                rtspAddress: :{RtspPort}
                rtspTransports: [tcp]
                rtspEncryption: "no"

                rtmp: no
                hls: no
                webrtc: no
                srt: no
                moq: no

                pathDefaults:
                  sourceOnDemand: yes
                  sourceOnDemandStartTimeout: 15s
                  # 30 s tibio tras perder al último lector: los cambios de
                  # stream main↔sub y los ciclos maximizar/restaurar del
                  # cliente re-enganchan sin pagar el arranque en frío.
                  sourceOnDemandCloseAfter: 30s
                  rtspTransport: tcp

                paths:
                """);

            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
                var devices = await db.Devices.Include(d => d.Channels).AsNoTracking().ToListAsync(ct);

                int pathCount = 0;
                foreach (var device in devices)
                {
                    var factory = drivers.Find(device.DriverKey);
                    if (factory is null) continue;
                    var driver = factory.Create();
                    var conn = new DeviceConnectionInfo(device.Host, device.SdkPort, device.Username,
                        protector.Unprotect(device.PasswordCiphertext));

                    foreach (var channel in device.Channels.Where(c => c.Enabled))
                    {
                        foreach (var profile in new[] { StreamProfile.Main, StreamProfile.Sub })
                        {
                            string name = PathName(device.Id, channel.RtspChannel, profile);
                            // ONVIF guarda la URL real resuelta por GetStreamUri
                            // (sin credenciales: se inyectan aquí); las marcas
                            // con plantilla la construyen desde el driver.
                            string? stored = profile == StreamProfile.Main ? channel.RtspMainUrl : channel.RtspSubUrl;
                            string source = stored is { Length: > 0 }
                                ? InjectCredentials(stored, conn)
                                : driver.BuildRtspUrl(conn, device.RtspPort, channel.RtspChannel, profile);
                            yml.AppendLine($"  {name}:");
                            if (channel.UseFfmpegProxy && ResolveFfmpeg() is { } ffmpeg)
                            {
                                // Cámaras con SDP inválido que gortsplib rechaza
                                // ("media N config is missing"): FFmpeg sí tolera
                                // ese SDP, así que MediaMTX lo lanza bajo demanda
                                // para que pull-ee la cámara y publique de vuelta
                                // SOLO el video (-an: el audio mal anunciado se
                                // descarta). La publicación se autoriza con el
                                // secreto por arranque + loopback.
                                string publish = $"rtsp://127.0.0.1:{RtspPort}/{name}?token={PublishSecret}";
                                string command =
                                    $"\"{ffmpeg}\" -hide_banner -loglevel error -rtsp_transport tcp " +
                                    $"-i \"{source}\" -c copy -an -f rtsp \"{publish}\"";
                                // Estas rutas no tienen "source" (publica el relé):
                                // anular el sourceOnDemand heredado de pathDefaults,
                                // o MediaMTX rechaza la configuración completa.
                                yml.AppendLine("    sourceOnDemand: no");
                                yml.AppendLine($"    runOnDemand: '{command.Replace("'", "''")}'");
                                yml.AppendLine("    runOnDemandRestart: yes");
                                yml.AppendLine("    runOnDemandStartTimeout: 15s");
                                yml.AppendLine("    runOnDemandCloseAfter: 30s");
                            }
                            else
                            {
                                yml.AppendLine($"    source: {source}");
                            }
                            pathCount++;
                        }
                    }
                }
                if (pathCount == 0)
                    yml.AppendLine("  {}");
                logger.LogInformation("Configuración de MediaMTX generada con {Count} rutas.", pathCount);
            }

            await File.WriteAllTextAsync(ConfigPath, yml.ToString(), ct);
        }
        finally
        {
            _configLock.Release();
        }
    }

    public static string PathName(int deviceId, int rtspChannel, StreamProfile profile) =>
        $"ch/{deviceId}/{rtspChannel}/{(profile == StreamProfile.Main ? "main" : "sub")}";

    /// <summary>Ubica ffmpeg.exe una sola vez; sin él, los canales marcados
    /// con proxy caen al pull directo (y se avisa una vez en el log).</summary>
    private string? ResolveFfmpeg()
    {
        _ffmpegPath ??= FindFfmpeg();
        if (_ffmpegPath is null && !_ffmpegWarned)
        {
            _ffmpegWarned = true;
            logger.LogWarning(
                "Hay canales marcados con proxy FFmpeg pero no se encontró tools\\ffmpeg\\bin\\ffmpeg.exe: " +
                "esos canales usarán el pull directo (probablemente fallarán por su SDP inválido).");
        }
        return _ffmpegPath;
    }

    private static readonly HttpClient ApiHttp = new() { Timeout = TimeSpan.FromSeconds(3) };

    // ------------------------------------------------------------------
    // Rutas dinámicas de reproducción (pb-...): cada concesión de playback
    // monta por la API de control una ruta cuyo source es la URL RTSP de
    // reproducción del equipo (con el rango horario pedido). Las rutas
    // viejas se barren al crear nuevas; una regeneración de la
    // configuración (RefreshPaths) también las descarta.
    // ------------------------------------------------------------------
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _playbackPaths = new();

    /// <summary>
    /// Crea una ruta de reproducción bajo demanda; false si MediaMTX no está o
    /// la rechazó. Antes cierra las rutas HERMANAS (mismo espectador, equipo y
    /// canal): cada salto en la línea de tiempo pide una ruta nueva, y si la
    /// anterior sigue viva el grabador queda con DOS sesiones de reproducción
    /// abiertas del mismo canal. Los equipos admiten unas pocas, así que la
    /// nueva se quedaba esperando hasta agotar el tiempo de arranque —medido
    /// contra un DVR real: 15 s con la anterior viva, 1 s sin ella—.
    /// </summary>
    public async Task<bool> AddPlaybackPathAsync(string name, string sourceUrl, CancellationToken ct = default)
    {
        if (!IsRunning) return false;
        await SweepPlaybackPathsAsync(TimeSpan.FromHours(2), ct);
        await ReleaseSiblingPathsAsync(name, ct);
        var payload = new
        {
            source = sourceUrl,
            sourceOnDemand = true,
            sourceOnDemandStartTimeout = "15s",
            // De un solo uso: cada salto crea otra ruta y nadie vuelve a esta,
            // así que en cuanto el espectador se va sobra la sesión contra el
            // equipo (en el vivo, en cambio, conviene dejarla tibia 30 s).
            sourceOnDemandCloseAfter = "1s",
        };
        try
        {
            using var response = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(
                ApiHttp, $"{ApiBaseUrl}/v3/config/paths/add/{name}", payload, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("MediaMTX: no aceptó la ruta de reproducción '{Name}' ({Status}).",
                    name, (int)response.StatusCode);
                return false;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("MediaMTX: error creando la ruta de reproducción '{Name}': {Error}", name, ex.Message);
            return false;
        }
        _playbackPaths[name] = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Crea una ruta SIN fuente: la alimenta un publicador nuestro (el relé de
    /// velocidad). Se usa solo cuando la reproducción va a velocidad distinta
    /// de 1×, porque ahí el equipo lo pulsa el servidor y no MediaMTX.
    /// </summary>
    public async Task<bool> AddPublishPathAsync(string name, CancellationToken ct = default)
    {
        if (!IsRunning) return false;
        await SweepPlaybackPathsAsync(TimeSpan.FromHours(2), ct);
        // Igual que en las rutas con fuente: la anterior del mismo espectador y
        // canal sobra (su relé se cierra aparte, en PlaybackRelayManager).
        await ReleaseSiblingPathsAsync(name, ct);
        try
        {
            using var response = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(
                ApiHttp, $"{ApiBaseUrl}/v3/config/paths/add/{name}",
                // Ambos explícitos: la configuración generada trae
                // sourceOnDemand por omisión y MediaMTX rechaza la ruta si
                // queda puesto sobre una fuente de tipo "publisher".
                new { source = "publisher", sourceOnDemand = false }, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("MediaMTX: no aceptó la ruta de publicación '{Name}' ({Status}).",
                    name, (int)response.StatusCode);
                return false;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("MediaMTX: error creando la ruta de publicación '{Name}': {Error}", name, ex.Message);
            return false;
        }
        _playbackPaths[name] = DateTime.UtcNow;
        return true;
    }

    /// <summary>URL local con la que un relé nuestro publica en una ruta.</summary>
    public string PublishUrlFor(string name) => $"rtsp://127.0.0.1:{RtspPort}/{name}?token={PublishSecret}";

    /// <summary>Elimina las rutas de reproducción más viejas que maxAge (mejor esfuerzo).</summary>
    private async Task SweepPlaybackPathsAsync(TimeSpan maxAge, CancellationToken ct)
    {
        foreach (var (name, created) in _playbackPaths)
            if (DateTime.UtcNow - created >= maxAge)
                await DeletePlaybackPathAsync(name, ct);
    }

    /// <summary>
    /// Cierra las rutas de reproducción hermanas de <paramref name="name"/>:
    /// las que comparten todo el nombre menos el sufijo aleatorio, es decir el
    /// mismo espectador sobre el mismo equipo y canal. Borrar la ruta corta su
    /// pull, y con eso el grabador libera la sesión en el acto.
    /// </summary>
    private async Task ReleaseSiblingPathsAsync(string name, CancellationToken ct)
    {
        int lastDash = name.LastIndexOf('-');
        if (lastDash <= 0) return;
        string prefix = name[..(lastDash + 1)];
        foreach (string other in _playbackPaths.Keys)
            if (other != name && other.StartsWith(prefix, StringComparison.Ordinal))
                await DeletePlaybackPathAsync(other, ct);
    }

    // ------------------------------------------------------------------
    // Por qué falló el pull de una ruta. MediaMTX lo dice en su salida y el
    // lector solo recibe un error genérico ("400 Bad Request"), así que el
    // motivo del EQUIPO —típicamente 453, sin ancho de banda para otra
    // reproducción— se guarda aquí para que el cliente pueda explicarlo.
    // ------------------------------------------------------------------
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, int Code, string Reason)>
        _sourceErrors = new();

    /// <summary>Último rechazo del equipo para una ruta (dentro de los últimos 2 minutos).</summary>
    public (int Code, string Reason)? LastSourceError(string path) =>
        _sourceErrors.TryGetValue(path, out var error) && DateTime.UtcNow - error.At < TimeSpan.FromMinutes(2)
            ? (error.Code, error.Reason)
            : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"\[path ([^\]]+)\].*bad status code: (\d+) \(([^)]*)\)")]
    private static partial System.Text.RegularExpressions.Regex SourceErrorPattern();

    private void RecordSourceError(string line)
    {
        if (SourceErrorPattern().Match(line) is not { Success: true } match) return;
        _sourceErrors[match.Groups[1].Value] =
            (DateTime.UtcNow, int.Parse(match.Groups[2].Value), match.Groups[3].Value);

        // La tabla no crece sin límite: cada ruta es de un solo uso.
        if (_sourceErrors.Count > 200)
            foreach (var (path, error) in _sourceErrors)
                if (DateTime.UtcNow - error.At > TimeSpan.FromMinutes(5))
                    _sourceErrors.TryRemove(path, out _);
    }

    private async Task DeletePlaybackPathAsync(string name, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{ApiBaseUrl}/v3/config/paths/delete/{name}");
            using var _ = await ApiHttp.SendAsync(request, ct);
        }
        catch { /* si MediaMTX se reinició, la ruta ya no existe */ }
        _playbackPaths.TryRemove(name, out _);
    }

    /// <summary>
    /// Expulsa a un lector cortando su sesión RTSP en MediaMTX. El espectador
    /// puede volver a pedir una concesión (sigue autenticado): expulsar corta
    /// la sesión en curso, no bloquea la cuenta.
    /// </summary>
    public async Task<bool> KickSessionAsync(string mtxSessionId, CancellationToken ct = default)
    {
        if (!IsRunning || string.IsNullOrEmpty(mtxSessionId)) return false;
        try
        {
            using var response = await ApiHttp.PostAsync(
                $"{ApiBaseUrl}/v3/rtspsessions/kick/{Uri.EscapeDataString(mtxSessionId)}", null, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Inserta usuario:contraseña (URL-encoded) en una URL RTSP guardada sin credenciales.</summary>
    private static string InjectCredentials(string rtspUrl, DeviceConnectionInfo conn)
    {
        if (!Uri.TryCreate(rtspUrl, UriKind.Absolute, out var uri))
            return rtspUrl;
        string credentials = $"{Uri.EscapeDataString(conn.Username)}:{Uri.EscapeDataString(conn.Password)}";
        return $"{uri.Scheme}://{credentials}@{uri.Host}:{(uri.IsDefaultPort ? 554 : uri.Port)}{uri.PathAndQuery}";
    }

    private void StartProcess(string exe)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            ArgumentList = { ConfigPath },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };

        var process = Process.Start(psi);
        if (process is null)
        {
            logger.LogError("No se pudo iniciar MediaMTX ({Exe}).", exe);
            return;
        }
        _process = process;
        process.EnableRaisingEvents = true;
        // El hijo muere con el servidor aunque el apagado sea abrupto.
        ChildProcessJob.Attach(process);

        // El log de MediaMTX pasa al log del servidor (prefijado) y sirve para
        // ver la apertura de listeners y los pulls on-demand.
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 }) return;
            logger.LogInformation("[mediamtx] {Line}", e.Data);
            RecordSourceError(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 }) return;
            logger.LogWarning("[mediamtx] {Line}", e.Data);
            RecordSourceError(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Si sobrevive 30 s se considera sano y el backoff se reinicia.
        long startedAtTicks = Environment.TickCount64;
        process.Exited += (_, _) =>
        {
            if (_stopping) return;
            if (Environment.TickCount64 - startedAtTicks > 30_000)
                _consecutiveFailures = 0;
            int delay = Math.Min(3 << Math.Min(_consecutiveFailures, 4), 30); // 3,6,12,24,30...
            _consecutiveFailures++;
            logger.LogWarning("MediaMTX terminó inesperadamente (código {Code}); se reinicia en {Delay} s.",
                process.ExitCode, delay);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(delay));
                if (!_stopping)
                    StartProcess(exe);
            });
        };

        logger.LogInformation("MediaMTX iniciado (PID {Pid}): RTSP en :{RtspPort} (TCP), API en 127.0.0.1:{ApiPort}.",
            process.Id, RtspPort, ApiPort);
    }
}
