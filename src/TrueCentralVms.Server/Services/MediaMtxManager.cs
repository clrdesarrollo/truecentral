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
/// queda en 127.0.0.1; RTMP/HLS/SRT/MoQ deshabilitados y WebRTC (vista en
/// vivo del panel web) con su señalización también en 127.0.0.1. Las
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
    private volatile bool _restartPending;
    private int? _lastExitCode;
    private DateTime? _startedAtUtc;

    public int RtspPort => config.GetValue("Streaming:RtspPort", 8654);
    public int ApiPort => config.GetValue("Streaming:ApiPort", 9911);
    public string ApiBaseUrl => $"http://127.0.0.1:{ApiPort}";

    /// <summary>Señalización WebRTC (WHEP) de MediaMTX: SOLO en loopback. El
    /// navegador nunca le habla directo; el servidor la intermedia en
    /// /api/streams/webrtc con la misma concesión de token que el RTSP.</summary>
    public int WebRtcPort => config.GetValue("Streaming:WebRtcPort", 9914);
    public string WebRtcBaseUrl => $"http://127.0.0.1:{WebRtcPort}";

    /// <summary>Puerto del medio WebRTC (ICE), UDP y TCP: es el único que el
    /// navegador abre directo hacia este equipo (el instalador lo habilita
    /// en el firewall). 0 = vista en vivo web deshabilitada.</summary>
    public int WebRtcIcePort => config.GetValue("Streaming:WebRtcIcePort", 8660);
    public bool WebRtcEnabled => WebRtcIcePort > 0;

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

    /// <summary>true entre una caída del proceso y su relanzamiento automático (lo consulta el supervisor).</summary>
    public bool RestartPending => _restartPending;
    public int? ProcessId => _process is { HasExited: false } p ? p.Id : null;
    public DateTime? StartedAtUtc => _startedAtUtc;
    public int? LastExitCode => _lastExitCode;
    public bool ExecutableFound => FindExecutable() is not null;

    private string ConfigPath => Path.Combine(env.ContentRootPath, "mediamtx.runtime.yml");

    public async Task StartAsync(CancellationToken ct)
    {
        // Reutilizable tras un StopAsync (el supervisor detiene e inicia a pedido).
        if (IsRunning) return;
        _stopping = false;
        _restartPending = false;
        _consecutiveFailures = 0;

        string? exe = FindExecutable();
        if (exe is null)
        {
            logger.LogError(
                "No se encontró tools\\mediamtx\\mediamtx.exe: el streaming en vivo queda deshabilitado. " +
                "Copie MediaMTX v1.20 a tools\\mediamtx (junto al ejecutable o en la raíz del repositorio).");
            return;
        }

        KillOrphans(exe);
        await WriteConfigAsync(ct, startup: true);
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
                // La espera sin tope tras la acotada vacía los eventos pendientes (Exited).
                if (p.WaitForExit(5000)) p.WaitForExit();
                logger.LogInformation("MediaMTX detenido.");
            }
            _process = null;
            _restartPending = false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo detener MediaMTX limpiamente.");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Regenera las rutas tras un cambio de dispositivos/canales y espera a que
    /// MediaMTX las tenga (con tope: si tarda, el pedido sigue en curso). Los
    /// pedidos se encolan y los que llegan mientras se aplica otro se agrupan
    /// en una sola pasada siguiente, que relee la base: la última escritura
    /// siempre refleja el último cambio. Ver <see cref="CommitConfigAsync"/>.
    /// </summary>
    public async Task RefreshPathsAsync(CancellationToken ct = default)
    {
        try
        {
            await RequestRefresh().WaitAsync(RefreshWaitLimit, ct);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("MediaMTX: las rutas aún no quedan aplicadas tras {Seconds} s; se siguen aplicando en segundo plano.",
                RefreshWaitLimit.TotalSeconds);
        }
    }

    // ------------------------------------------------------------------
    // Aplicación de las rutas en caliente. MediaMTX vigila su archivo, pero
    // su vigilante IGNORA todo cambio dentro de 1 s desde su última recarga
    // (minInterval de internal/confwatcher, v1.20): con altas seguidas
    // —importar o migrar equipos, la siembra de la demo— solo la primera
    // escritura se aplicaba y el resto de las rutas quedaba "not configured".
    // Además, si leyera el archivo a medio escribir, MediaMTX se detiene
    // entero. Por eso: escritura atómica, una pasada a la vez, y después de
    // cada escritura se compara lo que MediaMTX tiene (API de control) con lo
    // escrito; lo que no tomó se aplica ruta por ruta por la API, que es
    // inmediata. Solo se tocan las rutas que difieren, así que las sesiones
    // de las demás no se cortan (una recarga posterior del mismo archivo
    // tampoco: la ruta queda idéntica a la cargada desde el archivo).
    // ------------------------------------------------------------------
    private static readonly TimeSpan RefreshWaitLimit = TimeSpan.FromSeconds(15);

    /// <summary>Ventana en que el vigilante de MediaMTX ignora otra escritura (1 s + margen).</summary>
    private const int WatcherBlindMs = 1200;

    /// <summary>Cuánto se le da al vigilante para recargar antes de aplicar por la API.</summary>
    private static readonly TimeSpan WatcherGrace = TimeSpan.FromMilliseconds(800);

    private const int MaxRefreshRetries = 6;

    private readonly Lock _refreshGate = new();
    private TaskCompletionSource? _refreshPending;
    private bool _refreshRunning;
    private int _refreshRetries;

    // Protegidos por _configLock.
    private string? _writtenConfig;
    private Dictionary<string, Dictionary<string, string>> _appliedPaths = new(StringComparer.Ordinal);
    private long _lastWatchedWriteTicks = -WatcherBlindMs;

    /// <summary>true = no se sabe si MediaMTX tiene lo último escrito (recién
    /// lanzado, o falló la pasada anterior): la próxima pasada lo comprueba
    /// aunque ninguna ruta haya cambiado.</summary>
    private volatile bool _resyncPaths;

    /// <summary>Encola una pasada; la tarea termina cuando una pasada que
    /// empezó DESPUÉS de este pedido terminó (con éxito o no).</summary>
    private Task RequestRefresh()
    {
        lock (_refreshGate)
        {
            _refreshPending ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_refreshRunning)
            {
                _refreshRunning = true;
                _ = Task.Run(RefreshLoopAsync);
            }
            return _refreshPending.Task;
        }
    }

    private async Task RefreshLoopAsync()
    {
        while (true)
        {
            TaskCompletionSource batch;
            lock (_refreshGate)
            {
                if (_refreshPending is null)
                {
                    _refreshRunning = false;
                    return;
                }
                batch = _refreshPending;
                _refreshPending = null;
            }
            try
            {
                await WriteConfigAsync(CancellationToken.None, startup: false);
                _refreshRetries = 0;
            }
            catch (Exception ex)
            {
                _resyncPaths = true;
                bool retry = ++_refreshRetries <= MaxRefreshRetries;
                logger.LogError(ex, "MediaMTX: no se pudieron aplicar las rutas{Retry}.",
                    retry ? "; se reintenta en 5 s" : "; quedan pendientes hasta el próximo cambio");
                if (retry)
                    _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => RequestRefresh(), TaskScheduler.Default);
            }
            batch.TrySetResult();
        }
    }

    /// <summary>
    /// Escribe la configuración generada y, si MediaMTX está corriendo, se
    /// asegura de que la tomó. Al arrancar solo escribe: el proceso la lee al
    /// iniciar. Sin cambios respecto de lo último escrito no reescribe (así
    /// tampoco se descartan las rutas de reproducción pb-…, que viven solo en
    /// la API y una recarga del archivo borra).
    /// </summary>
    private async Task CommitConfigAsync(string content, bool startup)
    {
        var expected = ParseConfigPaths(content);
        bool written = false;
        if (startup || content != _writtenConfig)
        {
            await WriteConfigFileAsync(content);
            _writtenConfig = content;
            written = true;
        }
        if (startup || !IsRunning)
        {
            // Arranque o relanzamiento pendiente: el proceso lee el archivo.
            _appliedPaths = expected;
            _resyncPaths = false;
            return;
        }

        // Cuántas rutas cambiaron respecto de lo que MediaMTX ya tiene; null =
        // no se sabe (arranque o error anterior) y se compara igual.
        int? changed = _resyncPaths ? null : ChangedPaths(_appliedPaths, expected);
        if (changed == 0)
            return; // el cambio no tocó ninguna ruta

        // El vigilante solo recarga si su última recarga fue hace más de 1 s.
        bool watcherReloads = written && Environment.TickCount64 - _lastWatchedWriteTicks >= WatcherBlindMs;
        if (watcherReloads) _lastWatchedWriteTicks = Environment.TickCount64;

        var waited = Stopwatch.StartNew();
        List<PathDifference> pending;
        for (int poll = 1; ; poll++)
        {
            if (watcherReloads) await Task.Delay(poll * 100); // 100, 200, 300… ms
            pending = await FindPathDifferencesAsync(expected);
            if (pending.Count == 0 || !watcherReloads || waited.Elapsed >= WatcherGrace) break;
        }

        if (pending.Count > 0)
        {
            logger.LogDebug(
                "MediaMTX no tomó el archivo de configuración ({Reason}): se aplican {Count} rutas por su API de control.",
                watcherReloads ? "no recargó a tiempo"
                : written ? "su última recarga fue hace menos de 1 s" : "difería de lo escrito",
                pending.Count);
            await ApplyPathDifferencesAsync(pending);
            var still = await FindPathDifferencesAsync(expected);
            if (still.Count > 0)
                throw new InvalidOperationException(
                    $"MediaMTX sigue sin {still.Count} rutas tras aplicarlas por la API (p. ej. '{still[0].Name}').");
        }

        _appliedPaths = expected;
        _resyncPaths = false;
        if (changed is not null || pending.Count > 0)
            logger.LogInformation("Rutas de MediaMTX aplicadas: {Count} con cambios{How}.",
                changed ?? pending.Count, pending.Count > 0 ? " (por la API de control)" : "");
    }

    /// <summary>
    /// Escritura atómica: archivo temporal y reemplazo. Un MediaMTX que leyera
    /// el yml a medio escribir lo rechazaría y se detendría. Si justo lo está
    /// leyendo (lo abre sin compartir el borrado), el reemplazo se reintenta.
    /// </summary>
    private async Task WriteConfigFileAsync(string content)
    {
        string temp = ConfigPath + ".tmp";
        await File.WriteAllTextAsync(temp, content);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, ConfigPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                await Task.Delay(50);
            }
        }
    }

    /// <summary>
    /// Rutas del yml generado: nombre → campos tal como se escribieron. Lee el
    /// formato fijo que produce <see cref="WriteConfigAsync"/> ("  nombre:" y
    /// "    clave: valor" bajo "paths:"), así lo que se verifica es justo lo
    /// que se escribió, venga del bloque que venga.
    /// </summary>
    internal static Dictionary<string, Dictionary<string, string>> ParseConfigPaths(string yml)
    {
        var paths = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string>? current = null;
        bool inPaths = false;
        foreach (string raw in yml.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (!inPaths)
            {
                inPaths = line == "paths:";
                continue;
            }
            if (line.StartsWith("    ", StringComparison.Ordinal))
            {
                int colon = line.IndexOf(": ", StringComparison.Ordinal);
                if (current is not null && colon > 0)
                    current[line[..colon].Trim()] = UnquoteYaml(line[(colon + 2)..].Trim());
            }
            else if (line.StartsWith("  ", StringComparison.Ordinal) && line.EndsWith(':'))
            {
                current = new Dictionary<string, string>(StringComparer.Ordinal);
                paths[line.Trim().TrimEnd(':')] = current;
            }
            else if (line.Length > 0 && line[0] != ' ' && line[0] != '#')
            {
                break; // otra clave de primer nivel: se acabaron las rutas
            }
        }
        return paths;
    }

    private static string UnquoteYaml(string value) =>
        value.Length >= 2 && value[0] == '\'' && value[^1] == '\''
            ? value[1..^1].Replace("''", "'")
            : value;

    /// <summary>Rutas agregadas, cambiadas o quitadas entre dos configuraciones.</summary>
    private static int ChangedPaths(
        Dictionary<string, Dictionary<string, string>> before, Dictionary<string, Dictionary<string, string>> after) =>
        after.Count(pair => !before.TryGetValue(pair.Key, out var old) || !SameFields(old, pair.Value))
        + before.Keys.Count(name => !after.ContainsKey(name));

    private static bool SameFields(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);

    /// <summary>Las rutas de reproducción (pb-…) las crea el servidor por la
    /// API y no están en el archivo: la comparación no las toca.</summary>
    private static bool IsConfigPath(string name) => !name.StartsWith("pb-", StringComparison.Ordinal);

    /// <summary>Ruta a corregir: Fields null = sobra y se quita.</summary>
    private sealed record PathDifference(string Name, Dictionary<string, string>? Fields);

    /// <summary>Lo que distingue a una ruta: su fuente (o "publisher" si la
    /// alimenta un relé) y el comando del relé.</summary>
    private static (string Source, string RunOnDemand) Identity(Dictionary<string, string> fields) =>
        (fields.GetValueOrDefault("source") ?? "publisher", fields.GetValueOrDefault("runOnDemand") ?? "");

    /// <summary>
    /// Compara las rutas esperadas con las que MediaMTX tiene configuradas
    /// (todas menos las de reproducción). Va por el listado y no ruta por
    /// ruta: cada consulta de una ruta inexistente MediaMTX la anota como
    /// error en su salida, que termina en el registro del servidor.
    /// </summary>
    private async Task<List<PathDifference>> FindPathDifferencesAsync(
        Dictionary<string, Dictionary<string, string>> expected)
    {
        var actual = await ListConfiguredPathsAsync();
        var differences = new List<PathDifference>();
        foreach (string name in expected.Keys.Union(actual.Keys.Where(IsConfigPath)))
        {
            bool have = actual.TryGetValue(name, out var current);
            if (expected.TryGetValue(name, out var fields))
            {
                if (!have || current != Identity(fields)) differences.Add(new PathDifference(name, fields));
            }
            else if (have && IsConfigPath(name))
            {
                differences.Add(new PathDifference(name, null));
            }
        }
        return differences;
    }

    private async Task<Dictionary<string, (string Source, string RunOnDemand)>> ListConfiguredPathsAsync()
    {
        var paths = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        for (int page = 0, pageCount = 1; page < pageCount; page++)
        {
            string body = await ApiHttp.GetStringAsync($"{ApiBaseUrl}/v3/config/paths/list?itemsPerPage=500&page={page}");
            using var json = System.Text.Json.JsonDocument.Parse(body);
            pageCount = json.RootElement.GetProperty("pageCount").GetInt32();
            foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
                paths[item.GetProperty("name").GetString()!] = ReadIdentity(item);
        }
        return paths;
    }

    private static (string Source, string RunOnDemand) ReadIdentity(System.Text.Json.JsonElement path) =>
        (path.TryGetProperty("source", out var source) ? source.GetString() ?? "" : "",
         path.TryGetProperty("runOnDemand", out var run) ? run.GetString() ?? "" : "");

    /// <summary>Aplica las rutas por la API: replace crea o reemplaza (con los
    /// pathDefaults del archivo para lo que no se indica), delete quita.</summary>
    private async Task ApplyPathDifferencesAsync(List<PathDifference> differences)
    {
        foreach (var difference in differences)
        {
            using var response = difference.Fields is null
                ? await ApiHttp.DeleteAsync($"{ApiBaseUrl}/v3/config/paths/delete/{difference.Name}")
                : await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(ApiHttp,
                    $"{ApiBaseUrl}/v3/config/paths/replace/{difference.Name}",
                    difference.Fields.ToDictionary(f => f.Key, f => f.Value switch
                    {
                        "yes" => (object)true,
                        "no" => false,
                        _ => f.Value,
                    }));
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                throw new InvalidOperationException(
                    $"MediaMTX rechazó la ruta '{difference.Name}' ({(int)response.StatusCode}): " +
                    await response.Content.ReadAsStringAsync());
        }
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

    /// <summary>Genera mediamtx.runtime.yml desde la base y lo aplica (ver
    /// <see cref="CommitConfigAsync"/>). Una pasada a la vez.</summary>
    private async Task WriteConfigAsync(CancellationToken ct, bool startup)
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
                srt: no
                moq: no

                {WebRtcSection()}
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

            await CommitConfigAsync(yml.ToString(), startup);
        }
        finally
        {
            _configLock.Release();
        }
    }

    /// <summary>
    /// Bloque WebRTC del yml (vista en vivo del panel web). La señalización
    /// WHEP escucha solo en loopback y la intermedia el servidor, que agrega
    /// X-Forwarded-For: por eso 127.0.0.1 es proxy de confianza y MediaMTX
    /// informa al callback de autorización la IP real del navegador. El medio
    /// va directo navegador ↔ MediaMTX por el puerto ICE (UDP, y TCP para
    /// redes que bloquean UDP). Con NAT, PublicHost y
    /// Streaming:WebRtcAdditionalHosts se anuncian como candidatos extra.
    /// </summary>
    private string WebRtcSection()
    {
        if (!WebRtcEnabled) return "webrtc: no\n";
        var hosts = new List<string>();
        if (config["Streaming:PublicHost"] is { Length: > 0 } publicHost) hosts.Add(publicHost.Trim());
        // Lista JSON (["a","b"]) o texto separado por comas.
        foreach (var child in config.GetSection("Streaming:WebRtcAdditionalHosts").GetChildren())
            if (child.Value is { Length: > 0 } value) hosts.Add(value.Trim());
        if (config["Streaming:WebRtcAdditionalHosts"] is { Length: > 0 } csv)
            hosts.AddRange(csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        string additional = string.Join(", ", hosts.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(h => $"'{h.Replace("'", "''")}'"));
        return $"""
            # Vista en vivo del panel web (WebRTC). Señalización solo en
            # loopback (la intermedia el servidor); el medio va por el puerto ICE.
            webrtc: yes
            webrtcAddress: 127.0.0.1:{WebRtcPort}
            webrtcEncryption: no
            webrtcTrustedProxies: ['127.0.0.1', '::1']
            webrtcLocalUDPAddress: :{WebRtcIcePort}
            webrtcLocalTCPAddress: :{WebRtcIcePort}
            webrtcIPsFromInterfaces: yes
            webrtcAdditionalHosts: [{additional}]
            webrtcICEServers2: []

            """;
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
    /// Expulsa a un lector cortando su sesión en MediaMTX (RTSP del cliente de
    /// escritorio o WebRTC del panel web: se prueba en ese orden). El
    /// espectador puede volver a pedir una concesión (sigue autenticado):
    /// expulsar corta la sesión en curso, no bloquea la cuenta.
    /// </summary>
    public async Task<bool> KickSessionAsync(string mtxSessionId, CancellationToken ct = default)
    {
        if (!IsRunning || string.IsNullOrEmpty(mtxSessionId)) return false;
        foreach (string kind in new[] { "rtspsessions", "webrtcsessions" })
        {
            try
            {
                using var response = await ApiHttp.PostAsync(
                    $"{ApiBaseUrl}/v3/{kind}/kick/{Uri.EscapeDataString(mtxSessionId)}", null, ct);
                if (response.IsSuccessStatusCode) return true;
            }
            catch
            {
                // MediaMTX ocupado o reiniciando: se prueba el otro tipo igual.
            }
        }
        return false;
    }

    // ------------------------------------------------------------------
    // WebRTC (WHEP) del panel web, intermediado por el servidor. La oferta
    // SDP del navegador se reenvía a la señalización local de MediaMTX con el
    // token de la concesión; la respuesta trae la URL de la sesión WHEP
    // (Location), que es lo que permite cerrarla al instante.
    // ------------------------------------------------------------------

    /// <summary>El arranque bajo demanda de la fuente puede tardar hasta
    /// sourceOnDemandStartTimeout (15 s) antes de que MediaMTX conteste.</summary>
    private static readonly HttpClient WhepHttp = new() { Timeout = TimeSpan.FromSeconds(25) };

    public sealed record WhepAnswer(int Status, string Body, string? SessionUrl);

    /// <summary>Envía la oferta SDP a la ruta; devuelve el estado HTTP de
    /// MediaMTX, el cuerpo (SDP de respuesta o JSON de error) y la URL
    /// absoluta (loopback) de la sesión creada.</summary>
    public async Task<WhepAnswer> WhepOfferAsync(string path, string token, string offerSdp, string? clientIp,
        CancellationToken ct)
    {
        var baseUri = new Uri(WebRtcBaseUrl + "/");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(baseUri, $"{path}/whep?token={Uri.EscapeDataString(token)}"))
        {
            Content = new StringContent(offerSdp, Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/sdp");
        if (clientIp is { Length: > 0 }) request.Headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);
        using var response = await WhepHttp.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        string? session = response.Headers.Location is { } location
            ? new Uri(baseUri, location).ToString()
            : null;
        return new WhepAnswer((int)response.StatusCode, body, session);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int UserId, string Url, DateTime Created)> _whepSessions = new();

    /// <summary>Anota la sesión WHEP creada para el usuario y devuelve el
    /// identificador opaco que recibe el navegador (la URL interna de MediaMTX
    /// no sale del servidor).</summary>
    public string RegisterWhepSession(int userId, string sessionUrl)
    {
        // Las que nadie cerró (pestaña cerrada de golpe) se olvidan al día.
        var stale = DateTime.UtcNow.AddHours(-24);
        foreach (var pair in _whepSessions)
            if (pair.Value.Created < stale) _whepSessions.TryRemove(pair.Key, out _);
        string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _whepSessions[id] = (userId, sessionUrl, DateTime.UtcNow);
        return id;
    }

    /// <summary>URL interna de la sesión, solo para su dueño (y la olvida).</summary>
    public string? TakeWhepSession(string id, int userId)
    {
        if (!_whepSessions.TryGetValue(id, out var entry) || entry.UserId != userId) return null;
        _whepSessions.TryRemove(id, out _);
        return entry.Url;
    }

    /// <summary>Cierra una sesión WHEP (el navegador cerró el cuadro).</summary>
    public async Task<bool> WhepDeleteAsync(string sessionUrl, CancellationToken ct)
    {
        // Solo URLs de la señalización local: nunca un destino arbitrario.
        if (!sessionUrl.StartsWith(WebRtcBaseUrl + "/", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var response = await ApiHttp.DeleteAsync(sessionUrl, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false; // MediaMTX ya la había cerrado o se reinició
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
        _startedAtUtc = DateTime.UtcNow;
        _restartPending = false;
        // Lee el archivo al iniciar, pero hasta que su API conteste no se puede
        // comprobar: la próxima pasada compara todas las rutas.
        _resyncPaths = true;
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
            try { _lastExitCode = process.ExitCode; } catch { /* handle cerrado */ }
            // Un proceso reemplazado (detención + inicio a pedido) no relanza nada.
            if (_stopping || !ReferenceEquals(process, _process)) return;
            _restartPending = true;
            if (Environment.TickCount64 - startedAtTicks > 30_000)
                _consecutiveFailures = 0;
            int delay = Math.Min(3 << Math.Min(_consecutiveFailures, 4), 30); // 3,6,12,24,30...
            _consecutiveFailures++;
            logger.LogWarning("MediaMTX terminó inesperadamente (código {Code}); se reinicia en {Delay} s.",
                process.ExitCode, delay);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(delay));
                if (!_stopping && ReferenceEquals(process, _process))
                    StartProcess(exe);
                else
                    _restartPending = false;
            });
        };

        logger.LogInformation("MediaMTX iniciado (PID {Pid}): RTSP en :{RtspPort} (TCP), API en 127.0.0.1:{ApiPort}.",
            process.Id, RtspPort, ApiPort);
    }
}
