using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TrueCentralVms.Core.Isup;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services.Isup;

/// <summary>
/// Orquestador del servicio ISUP interno (TrueCentralVms.IsupHost.exe, proceso hijo): lo lanza con su configuración y sus tokens, lo
/// relanza si se cae y lo cierra con el servidor. Va aparte porque el SDK ISUP abre puertos a internet y sus estructuras no tienen
/// headers oficiales para todo: si el SDK se cae, el VMS sigue en pie y esto lo levanta de nuevo.
///
/// Seguridad: la API del host escucha solo en 127.0.0.1 y exige <see cref="Token"/>. Las URL de video que lee ffmpeg (y que quedan
/// en la configuración de MediaMTX) llevan otro token, <see cref="MediaToken"/>, que solo abre video. Ambos cambian en cada arranque.
/// </summary>
public sealed class IsupHostManager(EmbeddedPostgres postgres, IConfiguration config, IServiceScopeFactory scopes,
    Network.WanAccessService wan, ILogger<IsupHostManager> logger) : IHostedService
{
    /// <summary>Nombre del ajuste en SystemBlobs: "true" = se aceptan equipos ISUP 2.0/4.0 (lo enciende un usuario desde el panel).</summary>
    public const string AllowLegacySetting = "isup.allow-legacy";

    private bool? _allowLegacy;
    private Process? _process;
    private volatile bool _stopping;
    private volatile bool _restartPending;
    private int _consecutiveFailures;
    private int? _lastExitCode;
    private DateTime? _startedAtUtc;
    private string? _lastError;

    public bool Enabled => config.GetValue("Isup:Enabled", true);
    public int ApiPort => config.GetValue("Isup:ApiPort", 7699);
    public string ApiBaseUrl => $"http://127.0.0.1:{ApiPort}";
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public string MediaToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public bool IsRunning => _process is { HasExited: false };
    public bool RestartPending => _restartPending;
    public int? ProcessId => _process is { HasExited: false } p ? p.Id : null;
    public DateTime? StartedAtUtc => _startedAtUtc;
    public int? LastExitCode => _lastExitCode;
    /// <summary>Último error de arranque que informó el host (puerto ocupado, SDK que no carga…).</summary>
    public string? LastError => _lastError;
    public bool ExecutableFound => FindExecutable() is not null;

    /// <summary>Se dispara cada vez que arranca un proceso nuevo: hay que volver a entregarle el directorio de equipos.</summary>
    public event Action? ProcessStarted;

    /// <summary>Carpeta de datos del host (fotos de eventos, registros del SDK): junto a los datos de PostgreSQL.</summary>
    public string DataDirectory =>
        config["Isup:DataDirectory"] is { Length: > 0 } dir ? dir
        : Path.Combine(Path.GetDirectoryName(postgres.DataDirectory) ?? postgres.DataDirectory, "isup");

    /// <summary>Configuración que se le pasa al host (sección Isup de appsettings).</summary>
    public IsupHostOptions Options
    {
        get
        {
            var o = new IsupHostOptions();
            var s = config.GetSection("Isup");
            o.PublicAddress = s["PublicAddress"] ?? "";
            // Equipos ISUP desde Internet: se anuncia la dirección pública (los de la LAN también la usan: el router
            // tiene que permitir que se llegue a ella desde adentro, «NAT loopback»).
            if (wan.Has(Network.WanAccessService.ScenarioIsup)) o.PublicAddress = wan.PublicHost;
            // Se lee a mano: el binder de configuración AGREGA a un arreglo con valores iniciales en vez de reemplazarlo.
            var ports = s.GetSection("RegistrationPorts").GetChildren().Select(c => int.TryParse(c.Value, out var p) ? p : 0).Where(p => p > 0).ToArray();
            if (ports.Length > 0) o.RegistrationPorts = ports;
            o.AlarmPort = s.GetValue("AlarmPort", o.AlarmPort);
            o.AllowLegacy = AllowLegacy;
            o.LegacyAlarmPort = s.GetValue("LegacyAlarmPort", o.LegacyAlarmPort);
            o.StreamPort = s.GetValue("StreamPort", o.StreamPort);
            o.PlaybackPort = s.GetValue("PlaybackPort", o.PlaybackPort);
            o.VoicePort = s.GetValue("VoicePort", o.VoicePort);
            o.PictureServerPort = s.GetValue("PictureServerPort", o.PictureServerPort);
            o.CommandTimeoutMs = s.GetValue("CommandTimeoutMs", o.CommandTimeoutMs);
            return o;
        }
    }

    /// <summary>
    /// ¿Se aceptan equipos ISUP 2.0/4.0? Esas versiones no prueban la clave al registrarse, así que viene apagado: lo
    /// enciende un usuario desde Sistema → Conexión ISUP, confirmando el riesgo. Sin ajuste guardado manda Isup:AllowLegacy.
    /// </summary>
    public bool AllowLegacy
    {
        get
        {
            if (_allowLegacy is { } known) return known;
            try { return (_allowLegacy = LoadAllowLegacySetting() ?? config.GetValue("Isup:AllowLegacy", false)).Value; }
            catch (Exception ex)
            {
                // Sin base todavía: lo seguro es no aceptar 2.0/4.0, sin recordarlo (se relee en el próximo arranque del host).
                logger.LogWarning(ex, "No se pudo leer el ajuste ISUP 2.0/4.0: se rechazan esos equipos hasta poder leerlo.");
                return false;
            }
        }
    }

    private bool? LoadAllowLegacySetting()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        string? stored = db.SystemBlobs.Where(b => b.Name == AllowLegacySetting).Select(b => b.Data).FirstOrDefault();
        return stored is null ? null : stored == "true";
    }

    /// <summary>Relanza el servicio ISUP para que tome la configuración nueva (los equipos se vuelven a registrar solos).</summary>
    public async Task RestartAsync(CancellationToken ct)
    {
        if (!Enabled) return;
        await StopAsync(ct);
        await StartAsync(ct);
    }

    /// <summary>Guarda el ajuste y relanza el servicio ISUP para que lo aplique (los equipos se vuelven a registrar solos).</summary>
    public async Task SetAllowLegacyAsync(bool allow, CancellationToken ct)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var row = await db.SystemBlobs.FirstOrDefaultAsync(b => b.Name == AllowLegacySetting, ct);
            if (row is null) db.SystemBlobs.Add(row = new SystemBlob { Name = AllowLegacySetting });
            row.Data = allow ? "true" : "false";
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        _allowLegacy = allow;
        if (!Enabled) return;
        await StopAsync(ct);
        await StartAsync(ct);
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (IsRunning) return Task.CompletedTask;
        _stopping = false;
        _restartPending = false;
        _consecutiveFailures = 0;
        if (!Enabled)
        {
            logger.LogInformation("Servicio ISUP deshabilitado (Isup:Enabled = false): los equipos no podrán conectarse por ISUP.");
            return Task.CompletedTask;
        }
        var exe = FindExecutable();
        if (exe is null)
        {
            logger.LogError("No se encontró TrueCentralVms.IsupHost.exe junto al servidor: los equipos ISUP (paneles AX PRO, equipos detrás de NAT) no podrán conectarse.");
            return Task.CompletedTask;
        }
        KillOrphans(exe);
        StartProcess(exe);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _stopping = true;
        _restartPending = false;
        if (_process is not { HasExited: false } p) { _process = null; return; }
        try
        {
            // Cierre ordenado (el SDK suelta sus puertos); si no termina a tiempo, se lo mata.
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/shutdown");
            req.Headers.Add("X-Isup-Token", Token);
            try { await http.SendAsync(req, ct); } catch (Exception) { /* ya no contesta: se mata abajo */ }
            if (!p.WaitForExit(8000)) p.Kill(entireProcessTree: true);
            p.WaitForExit();
            logger.LogInformation("Servicio ISUP detenido.");
        }
        catch (Exception ex) { logger.LogWarning(ex, "No se pudo detener el servicio ISUP limpiamente."); }
        _process = null;
    }

    private static string? FindExecutable()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "TrueCentralVms.IsupHost.exe");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>Instancias de NUESTRO host que hayan quedado de un cierre abrupto: ocuparían los puertos ISUP.</summary>
    private void KillOrphans(string exe)
    {
        foreach (var orphan in Process.GetProcessesByName("TrueCentralVms.IsupHost"))
        {
            try
            {
                if (string.Equals(orphan.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Servicio ISUP huérfano de una ejecución anterior (PID {Pid}): se termina.", orphan.Id);
                    orphan.Kill(entireProcessTree: true);
                    orphan.WaitForExit(5000);
                }
            }
            catch { /* proceso ajeno o sin permisos */ }
            finally { orphan.Dispose(); }
        }
    }

    private void StartProcess(string exe)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var optionsPath = Path.Combine(DataDirectory, "isup-options.json");
            File.WriteAllText(optionsPath, JsonSerializer.Serialize(Options, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            foreach (var a in new[] { "--api-port", ApiPort.ToString(), "--options", optionsPath, "--data-dir", DataDirectory, "--parent-pid", Environment.ProcessId.ToString() })
                psi.ArgumentList.Add(a);
            psi.Environment["TCVMS_ISUP_TOKEN"] = Token;
            psi.Environment["TCVMS_ISUP_MEDIA_TOKEN"] = MediaToken;

            var process = Process.Start(psi);
            if (process is null) { logger.LogError("No se pudo iniciar el servicio ISUP ({Exe}).", exe); return; }
            _process = process;
            _startedAtUtc = DateTime.UtcNow;
            _restartPending = false;
            _lastError = null;
            process.EnableRaisingEvents = true;
            ChildProcessJob.Attach(process);   // muere con el servidor aunque el apagado sea abrupto

            // Las líneas del host llevan fecha; lo demás es eco interno del SDK (solo para depurar).
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not { Length: > 0 } line) return;
                if (line.Length > 20 && char.IsDigit(line[0]) && line[4] == '-') logger.LogInformation("[isup] {Line}", line[20..]);
                else logger.LogDebug("[isup-sdk] {Line}", line);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not { Length: > 0 } line) return;
                _lastError = line;
                logger.LogError("[isup] {Line}", line);
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            long startedAt = Environment.TickCount64;
            process.Exited += (_, _) =>
            {
                try { _lastExitCode = process.ExitCode; } catch { /* handle cerrado */ }
                if (_stopping || !ReferenceEquals(process, _process)) return;
                _restartPending = true;
                if (Environment.TickCount64 - startedAt > 30_000) _consecutiveFailures = 0;
                int delay = Math.Min(3 << Math.Min(_consecutiveFailures, 4), 60);   // 3, 6, 12, 24, 48, 60…
                _consecutiveFailures++;
                logger.LogWarning("El servicio ISUP terminó (código {Code}); se reinicia en {Delay} s.", _lastExitCode, delay);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay));
                    if (!_stopping && ReferenceEquals(process, _process)) StartProcess(exe);
                    else _restartPending = false;
                });
            };
            logger.LogInformation("Servicio ISUP iniciado (PID {Pid}), API interna en 127.0.0.1:{Port}.", process.Id, ApiPort);
            ProcessStarted?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo iniciar el servicio ISUP.");
        }
    }
}
