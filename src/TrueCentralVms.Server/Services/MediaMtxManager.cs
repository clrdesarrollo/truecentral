using System.Diagnostics;
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
public sealed class MediaMtxManager(
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
                  sourceOnDemandCloseAfter: 10s
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
                            yml.AppendLine($"    source: {source}");
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
        process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) logger.LogInformation("[mediamtx] {Line}", e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) logger.LogWarning("[mediamtx] {Line}", e.Data); };
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
