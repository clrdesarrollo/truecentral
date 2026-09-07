using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting.WindowsServices;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services.Workflows;

namespace TrueCentralVms.Server.Services.Supervisor;

/// <summary>
/// Watchdog del servidor: vigila la salud de cada servicio (procesos hijos
/// como PostgreSQL y MediaMTX, y los subsistemas internos) y permite a un
/// administrador iniciarlos, detenerlos y reiniciarlos desde el panel.
///
/// Reglas:
/// - Un servicio que cae se reinicia solo (si su auto-reinicio está activo)
///   con esperas crecientes (5, 10, 20, 40, 60 s) hasta agotar
///   Supervisor:MaxRestartAttempts dentro de Supervisor:RestartWindowMinutes;
///   tras ese tiempo estable el contador vuelve a cero.
/// - Un servicio detenido A MANO queda detenido: el watchdog no lo toca hasta
///   que un administrador lo inicie.
/// - Los esenciales (la base de datos) no se pueden dejar detenidos, solo
///   reiniciar.
/// - Todo cambio de estado se empuja por el hub y las caídas y reinicios
///   automáticos quedan en la bitácora (los manuales los audita la API con
///   el actor real).
///
/// El propio proceso del servidor no puede vigilarse a sí mismo: para eso
/// están las acciones de recuperación del SCM de Windows (ver README). Sí
/// puede pedir su reinicio completo cuando corre como servicio de Windows.
/// </summary>
public sealed class ServiceSupervisor : BackgroundService
{
    /// <summary>Nombre del servicio de Windows con el que se instala el servidor.</summary>
    public const string WindowsServiceName = "CLRTrueCentralVMS";

    public sealed record CommandResult(bool Ok, string Message, ManagedServiceDto? Service = null, int StatusCode = 200);

    private sealed class Entry(IManagedService service)
    {
        public IManagedService Service { get; } = service;
        /// <summary>Serializa las transiciones: una orden a la vez por servicio (y la pasada del watchdog no se mete en medio).</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ManagedServiceState State = ManagedServiceState.Running;
        public DateTime SinceUtc = DateTime.UtcNow;
        public string? Detail;
        public string? LastError;
        public DateTime? LastErrorAtUtc;
        public int RestartCount;
        public int FailedAttempts;
        public DateTime? NextRetryAtUtc;
        public bool AutoRestart = true;
        public DateTime? HealthySinceUtc;
    }

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);
    // PostgreSQL puede tardar (pg_ctl -w -t 60) y MediaMTX regenera su configuración.
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(120);

    private readonly IReadOnlyList<Entry> _entries;
    private readonly IConfiguration _config;
    private readonly IHubContext<VmsHub> _hub;
    private readonly AuditService _audit;
    private readonly ILogger<ServiceSupervisor> _logger;

    public ServiceSupervisor(
        IServiceProvider services,
        IConfiguration config,
        IHubContext<VmsHub> hub,
        AuditService audit,
        ILoggerFactory loggers,
        ILogger<ServiceSupervisor> logger)
    {
        _config = config;
        _hub = hub;
        _audit = audit;
        _logger = logger;
        _entries = BuildCatalog(services, config, loggers).Select(s => new Entry(s)).ToArray();
    }

    /// <summary>
    /// Catálogo de servicios supervisados, en el orden en que se muestran.
    /// Para vigilar uno nuevo basta con agregarlo aquí (los BackgroundService
    /// deben registrarse como singleton + AddHostedService(sp => ...) para
    /// que esta sea la MISMA instancia que arranca el host).
    /// </summary>
    private static IEnumerable<IManagedService> BuildCatalog(IServiceProvider sp, IConfiguration config, ILoggerFactory loggers)
    {
        yield return new PostgresManagedService(sp.GetRequiredService<EmbeddedPostgres>(), loggers.CreateLogger<PostgresManagedService>());
        yield return new MediaMtxManagedService(sp.GetRequiredService<MediaMtxManager>());
        yield return new HostedServiceAdapter("device-monitor", "Monitor de dispositivos",
            "Sondea cada equipo de video y publica su estado (en línea / sin conexión).",
            sp.GetRequiredService<DeviceStatusMonitor>());
        yield return new HostedServiceAdapter("session-accounting", "Contabilidad de sesiones",
            "Concilia las sesiones de video activas contra MediaMTX y cierra en la bitácora las que terminaron.",
            sp.GetRequiredService<SessionAccounting>());
        yield return new HostedServiceAdapter("alarm-panels", "Paneles de alarma",
            "Sondeo de estado y canal de eventos de cada panel de intrusión habilitado.",
            sp.GetRequiredService<AlarmPanelService>());
        yield return new HostedServiceAdapter("alarm-receiver", "Receptor de alarmas (SIA DC-09)",
            $"Centro receptor al que los paneles reportan por TCP (puerto {config.GetValue("Alarms:Receiver:Port", 5091)}).",
            sp.GetRequiredService<AlarmReceiverService>(),
            () => config.GetValue("Alarms:Receiver:Enabled", true)
                ? null
                : "Deshabilitado en la configuración (Alarms:Receiver:Enabled = false).");
        yield return new HostedServiceAdapter("speakers", "Parlantes IP",
            "Sondeo de estado, reproducción sincronizada y voz en vivo hacia los altavoces de red.",
            sp.GetRequiredService<SpeakerService>());
        yield return new HostedServiceAdapter("anpr", "Reconocimiento de patentes",
            "Mantiene abierto el canal de eventos ANPR de cada equipo marcado como fuente.",
            sp.GetRequiredService<AnprService>());
        yield return new HostedServiceAdapter("workflows", "Automatizaciones",
            "Motor que ejecuta las acciones configuradas cuando ocurre un evento.",
            sp.GetRequiredService<WorkflowEngine>());
        yield return new HostedServiceAdapter("audit-retention", "Retención de la bitácora",
            "Purga periódica de la bitácora de auditoría según Audit:RetentionDays.",
            sp.GetRequiredService<AuditRetentionService>());
    }

    private TimeSpan CheckInterval =>
        TimeSpan.FromSeconds(Math.Clamp(_config.GetValue("Supervisor:CheckSeconds", 10), 3, 300));
    private int MaxAttempts => Math.Clamp(_config.GetValue("Supervisor:MaxRestartAttempts", 5), 1, 100);
    private TimeSpan RestartWindow =>
        TimeSpan.FromMinutes(Math.Clamp(_config.GetValue("Supervisor:RestartWindowMinutes", 10), 1, 1440));

    /// <summary>Espera antes del reintento n (0-based): 5, 10, 20, 40, 60, 60...</summary>
    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(5 << Math.Min(attempt, 4), 60));

    // ------------------------------------------------------------------
    // Watchdog
    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Los servicios tardan unos segundos en su primer ciclo; no juzgarlos antes.
        int initialDelay = Math.Clamp(_config.GetValue("Supervisor:InitialDelaySeconds", 20), 0, 600);
        try { await Task.Delay(TimeSpan.FromSeconds(initialDelay), ct); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation("Supervisor de servicios activo: {Count} servicios, comprobación cada {Seconds} s.",
            _entries.Count, CheckInterval.TotalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.WhenAll(_entries.Select(e => CheckOneAsync(e, ct)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fallo en la pasada del supervisor de servicios.");
            }
            try { await Task.Delay(CheckInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task CheckOneAsync(Entry e, CancellationToken ct)
    {
        // Si hay una orden manual en curso, esta pasada lo deja en paz.
        if (!await e.Gate.WaitAsync(0, ct)) return;
        try
        {
            if (e.Service.DisabledReason is { } why)
            {
                if (e.State != ManagedServiceState.Disabled)
                    Transition(e, ManagedServiceState.Disabled, why);
                return;
            }

            switch (e.State)
            {
                case ManagedServiceState.Stopped:
                case ManagedServiceState.Stopping:
                    return; // detenido a mano: no se toca

                case ManagedServiceState.Disabled:
                    // Se habilitó en caliente: se arranca como un reintento inmediato.
                    e.NextRetryAtUtc = DateTime.UtcNow;
                    Transition(e, ManagedServiceState.Failed, "Habilitado en la configuración: se inicia.");
                    return;

                case ManagedServiceState.Failed:
                    if (!e.AutoRestart || e.NextRetryAtUtc is null || DateTime.UtcNow < e.NextRetryAtUtc)
                        return;
                    await RestartCoreAsync(e, automatic: true, ct);
                    return;

                default: // Running / Starting
                    await ProbeAsync(e, ct);
                    return;
            }
        }
        finally
        {
            e.Gate.Release();
        }
    }

    private async Task ProbeAsync(Entry e, CancellationToken ct)
    {
        var health = await CheckWithTimeoutAsync(e.Service, ct);
        var now = DateTime.UtcNow;
        if (health.Healthy)
        {
            if (health.Transitioning)
            {
                if (e.State != ManagedServiceState.Starting)
                    Transition(e, ManagedServiceState.Starting, health.Detail);
                else
                    e.Detail = health.Detail;
                return;
            }
            e.HealthySinceUtc ??= now;
            // Estable durante toda la ventana: el contador de reintentos parte de cero.
            if (e.FailedAttempts > 0 && now - e.HealthySinceUtc.Value > RestartWindow)
                e.FailedAttempts = 0;
            if (e.State != ManagedServiceState.Running)
                Transition(e, ManagedServiceState.Running, health.Detail);
            else if (e.Detail != health.Detail)
                e.Detail = health.Detail;
            return;
        }
        e.HealthySinceUtc = null;
        await FailAsync(e, health.Detail ?? "El servicio no responde.");
    }

    /// <summary>Marca el servicio caído, programa el reintento (si corresponde) y lo audita.</summary>
    private async Task FailAsync(Entry e, string reason)
    {
        var now = DateTime.UtcNow;
        e.LastError = reason;
        e.LastErrorAtUtc = now;
        bool willRetry = e.AutoRestart && e.FailedAttempts < MaxAttempts;
        var wait = Backoff(e.FailedAttempts);
        e.NextRetryAtUtc = willRetry ? now + wait : null;
        string detail = willRetry
            ? $"Reintento automático {e.FailedAttempts + 1} de {MaxAttempts} en {wait.TotalSeconds:0} s."
            : e.AutoRestart
                ? $"Se agotaron los {MaxAttempts} reintentos automáticos: revise el registro del servidor y reinícielo a mano."
                : "Auto-reinicio desactivado: reinícielo a mano.";
        Transition(e, ManagedServiceState.Failed, detail);
        _logger.LogError("Servicio '{Name}' caído: {Reason} {Detail}", e.Service.Name, reason, detail);
        try
        {
            await _audit.LogSystemAsync("system", "service-failed",
                targetType: "service", targetId: e.Service.Id, targetName: e.Service.Name,
                detail: $"El servicio «{e.Service.Name}» cayó: {reason} {detail}", success: false,
                data: new { reason, failedAttempts = e.FailedAttempts, autoRestart = e.AutoRestart });
        }
        catch (Exception ex)
        {
            // Si la que cayó es la base de datos, la bitácora tampoco está: no
            // se pierde el reintento por eso.
            _logger.LogDebug(ex, "No se pudo auditar la caída del servicio '{Name}'.", e.Service.Name);
        }
    }

    /// <summary>Detiene e inicia un servicio (con el gate ya tomado). false si quedó caído.</summary>
    private async Task<bool> RestartCoreAsync(Entry e, bool automatic, CancellationToken ct)
    {
        if (automatic) e.FailedAttempts++;
        e.NextRetryAtUtc = null;
        _logger.LogInformation("Reinicio {Mode} del servicio '{Name}'.",
            automatic ? $"automático ({e.FailedAttempts} de {MaxAttempts})" : "a pedido", e.Service.Name);

        Transition(e, ManagedServiceState.Stopping, "Deteniendo...");
        try { await StopWithTimeoutAsync(e.Service, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "El servicio '{Name}' no se detuvo limpiamente; se intenta iniciar igual.", e.Service.Name);
        }

        Transition(e, ManagedServiceState.Starting, "Iniciando...");
        try { await StartWithTimeoutAsync(e.Service, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await FailAsync(e, $"No se pudo iniciar: {ex.Message}");
            return false;
        }

        var health = await CheckWithTimeoutAsync(e.Service, ct);
        if (!health.Healthy)
        {
            await FailAsync(e, health.Detail ?? "No respondió tras el arranque.");
            return false;
        }

        e.RestartCount++;
        e.HealthySinceUtc = DateTime.UtcNow;
        Transition(e, health.Transitioning ? ManagedServiceState.Starting : ManagedServiceState.Running, health.Detail);
        if (automatic)
        {
            try
            {
                await _audit.LogSystemAsync("system", "service-restarted",
                    targetType: "service", targetId: e.Service.Id, targetName: e.Service.Name,
                    detail: $"El supervisor reinició el servicio «{e.Service.Name}» (reintento {e.FailedAttempts} de {MaxAttempts}).",
                    data: new { automatic = true, attempt = e.FailedAttempts, restartCount = e.RestartCount });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No se pudo auditar el reinicio del servicio '{Name}'.", e.Service.Name);
            }
        }
        return true;
    }

    // ------------------------------------------------------------------
    // Órdenes de un administrador (la API audita con el actor real)
    // ------------------------------------------------------------------

    public async Task<CommandResult> StartAsync(string id, CancellationToken ct)
    {
        if (Find(id) is not { } e) return NotFound(id);
        await e.Gate.WaitAsync(ct);
        try
        {
            if (e.Service.DisabledReason is { } why)
                return Conflict(e, $"El servicio está deshabilitado: {why}");
            if (e.State is ManagedServiceState.Running or ManagedServiceState.Starting)
                return Conflict(e, "El servicio ya está en ejecución.");
            if (e.State is ManagedServiceState.Stopping)
                return Conflict(e, "El servicio se está deteniendo; espere unos segundos.");

            Transition(e, ManagedServiceState.Starting, "Iniciando...");
            try { await StartWithTimeoutAsync(e.Service, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await FailAsync(e, $"No se pudo iniciar: {ex.Message}");
                return new(false, $"No se pudo iniciar «{e.Service.Name}»: {ex.Message}", Dto(e), StatusCodes.Status500InternalServerError);
            }
            var health = await CheckWithTimeoutAsync(e.Service, ct);
            if (!health.Healthy)
            {
                await FailAsync(e, health.Detail ?? "No respondió tras el arranque.");
                return new(false, $"«{e.Service.Name}» no respondió tras el arranque: {health.Detail}", Dto(e), StatusCodes.Status500InternalServerError);
            }
            e.FailedAttempts = 0;
            e.NextRetryAtUtc = null;
            e.HealthySinceUtc = DateTime.UtcNow;
            Transition(e, health.Transitioning ? ManagedServiceState.Starting : ManagedServiceState.Running, health.Detail);
            return new(true, $"Servicio «{e.Service.Name}» iniciado.", Dto(e));
        }
        finally
        {
            e.Gate.Release();
        }
    }

    public async Task<CommandResult> StopAsync(string id, CancellationToken ct)
    {
        if (Find(id) is not { } e) return NotFound(id);
        await e.Gate.WaitAsync(ct);
        try
        {
            if (!e.Service.CanStop)
                return Conflict(e, "Este servicio es esencial: no se puede dejar detenido, solo reiniciar.");
            if (e.State is ManagedServiceState.Disabled)
                return Conflict(e, "El servicio está deshabilitado en la configuración.");
            if (e.State is ManagedServiceState.Stopped)
                return Conflict(e, "El servicio ya está detenido.");
            if (e.State is ManagedServiceState.Starting or ManagedServiceState.Stopping)
                return Conflict(e, "El servicio está en transición; espere unos segundos.");

            Transition(e, ManagedServiceState.Stopping, "Deteniendo...");
            try { await StopWithTimeoutAsync(e.Service, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                e.LastError = $"No se detuvo limpiamente: {ex.Message}";
                e.LastErrorAtUtc = DateTime.UtcNow;
                e.NextRetryAtUtc = null;
                Transition(e, ManagedServiceState.Failed, "La detención no terminó a tiempo; reinícielo a mano.");
                return new(false, $"«{e.Service.Name}» no se detuvo limpiamente: {ex.Message}", Dto(e), StatusCodes.Status500InternalServerError);
            }
            e.NextRetryAtUtc = null;
            e.HealthySinceUtc = null;
            Transition(e, ManagedServiceState.Stopped, "Detenido por un administrador; el supervisor no lo reiniciará.");
            return new(true, $"Servicio «{e.Service.Name}» detenido. No se reiniciará hasta que lo inicie a mano.", Dto(e));
        }
        finally
        {
            e.Gate.Release();
        }
    }

    public async Task<CommandResult> RestartAsync(string id, CancellationToken ct)
    {
        if (Find(id) is not { } e) return NotFound(id);
        await e.Gate.WaitAsync(ct);
        try
        {
            if (e.Service.DisabledReason is { } why)
                return Conflict(e, $"El servicio está deshabilitado: {why}");
            if (e.State is ManagedServiceState.Starting or ManagedServiceState.Stopping)
                return Conflict(e, "El servicio está en transición; espere unos segundos.");

            e.FailedAttempts = 0; // un reinicio a mano parte de cero
            bool ok = await RestartCoreAsync(e, automatic: false, ct);
            return ok
                ? new(true, $"Servicio «{e.Service.Name}» reiniciado.", Dto(e))
                : new(false, $"No se pudo reiniciar «{e.Service.Name}»: {e.LastError}", Dto(e), StatusCodes.Status500InternalServerError);
        }
        finally
        {
            e.Gate.Release();
        }
    }

    public CommandResult SetAutoRestart(string id, bool enabled)
    {
        if (Find(id) is not { } e) return NotFound(id);
        e.AutoRestart = enabled;
        if (enabled)
        {
            // Si está caído y le quedan reintentos, se reintenta de inmediato.
            if (e.State == ManagedServiceState.Failed && e.NextRetryAtUtc is null && e.FailedAttempts < MaxAttempts)
                e.NextRetryAtUtc = DateTime.UtcNow;
        }
        else
        {
            e.NextRetryAtUtc = null;
        }
        Push(e);
        return new(true, enabled
            ? $"«{e.Service.Name}»: el supervisor lo reiniciará solo si cae."
            : $"«{e.Service.Name}»: el supervisor NO lo reiniciará si cae.", Dto(e));
    }

    /// <summary>
    /// Reinicio completo del servidor. Solo tiene sentido instalado como
    /// servicio de Windows: un proceso auxiliar (fuera del job de los hijos)
    /// le pide al SCM detener y volver a iniciar el servicio.
    /// </summary>
    public CommandResult RestartServer()
    {
        if (!WindowsServiceHelpers.IsWindowsService())
            return new(false,
                "El reinicio completo solo está disponible con el servidor instalado como servicio de Windows. " +
                "En modo consola, ciérrelo (Ctrl+C) y vuelva a ejecutarlo.",
                null, StatusCodes.Status409Conflict);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                ArgumentList =
                {
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                    $"Start-Sleep -Seconds 2; Restart-Service -Name '{WindowsServiceName}' -Force",
                },
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var helper = Process.Start(psi)
                ?? throw new InvalidOperationException("No se pudo lanzar PowerShell.");
            _logger.LogWarning("Reinicio completo del servidor solicitado por un administrador (auxiliar PID {Pid}).", helper.Id);
            return new(true, "Reinicio solicitado: el servicio de Windows se detiene y vuelve a arrancar en unos segundos.");
        }
        catch (Exception ex)
        {
            return new(false, $"No se pudo solicitar el reinicio: {ex.Message}", null, StatusCodes.Status500InternalServerError);
        }
    }

    // ------------------------------------------------------------------
    // Consulta
    // ------------------------------------------------------------------

    public ServicesOverviewDto Snapshot()
    {
        using var me = Process.GetCurrentProcess();
        bool isService = WindowsServiceHelpers.IsWindowsService();
        var startedAt = me.StartTime.ToUniversalTime();
        var server = new ServerProcessDto(
            Version: Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0",
            ProcessId: me.Id,
            StartedAtUtc: startedAt,
            UptimeSeconds: Math.Max(0, (DateTime.UtcNow - startedAt).TotalSeconds),
            IsWindowsService: isService,
            ServiceName: WindowsServiceName,
            WorkingSetMb: Math.Round(me.WorkingSet64 / 1024.0 / 1024.0, 1),
            CheckIntervalSeconds: (int)CheckInterval.TotalSeconds,
            CanRestart: isService,
            RestartHint: isService ? null : "Corriendo como consola: el reinicio completo requiere el servicio de Windows.");
        return new(server, _entries.Select(Dto).ToList());
    }

    private Entry? Find(string id) =>
        _entries.FirstOrDefault(e => string.Equals(e.Service.Id, id, StringComparison.OrdinalIgnoreCase));

    private static CommandResult NotFound(string id) =>
        new(false, $"No existe el servicio '{id}'.", null, StatusCodes.Status404NotFound);

    private CommandResult Conflict(Entry e, string message) =>
        new(false, message, Dto(e), StatusCodes.Status409Conflict);

    private ManagedServiceDto Dto(Entry e) => new(
        e.Service.Id, e.Service.Name, e.Service.Description, e.Service.Kind,
        e.State, e.SinceUtc, e.Detail, e.LastError, e.LastErrorAtUtc,
        e.RestartCount, e.FailedAttempts, e.NextRetryAtUtc,
        e.AutoRestart, e.Service.CanStop,
        CanControl: e.State is not (ManagedServiceState.Disabled or ManagedServiceState.Starting or ManagedServiceState.Stopping));

    private void Transition(Entry e, ManagedServiceState state, string? detail)
    {
        if (e.State != state)
        {
            _logger.LogInformation("Servicio '{Name}': {From} → {To}{Detail}", e.Service.Name, e.State, state,
                detail is null ? "" : $" ({detail})");
            e.State = state;
            e.SinceUtc = DateTime.UtcNow;
        }
        e.Detail = detail;
        Push(e);
    }

    private void Push(Entry e)
    {
        var dto = Dto(e);
        _ = _hub.Clients.All.SendAsync(VmsHubContract.ServiceStateChanged, dto)
            .ContinueWith(t => _logger.LogDebug(t.Exception, "No se pudo publicar el estado del servicio '{Id}'.", dto.Id),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    // ------------------------------------------------------------------
    // Envolturas con tope de tiempo
    // ------------------------------------------------------------------

    private static async Task<ServiceHealth> CheckWithTimeoutAsync(IManagedService s, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CheckTimeout);
        try
        {
            return await s.CheckAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ServiceHealth.Down($"La comprobación de salud no respondió en {CheckTimeout.TotalSeconds:0} s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ServiceHealth.Down($"La comprobación de salud falló: {ex.Message}");
        }
    }

    private static async Task StopWithTimeoutAsync(IManagedService s, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(StopTimeout);
        try
        {
            // Task.Run: algunas detenciones son síncronas (pg_ctl stop) y no
            // deben bloquear el hilo que espera el tope.
            await Task.Run(() => s.StopAsync(cts.Token), CancellationToken.None)
                .WaitAsync(StopTimeout + TimeSpan.FromSeconds(5), ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No respondió a la detención en {StopTimeout.TotalSeconds:0} s.");
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"No respondió a la detención en {StopTimeout.TotalSeconds:0} s.");
        }
    }

    private static async Task StartWithTimeoutAsync(IManagedService s, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(StartTimeout);
        try
        {
            await Task.Run(() => s.StartAsync(cts.Token), CancellationToken.None)
                .WaitAsync(StartTimeout + TimeSpan.FromSeconds(5), ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"El arranque no terminó en {StartTimeout.TotalSeconds:0} s.");
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"El arranque no terminó en {StartTimeout.TotalSeconds:0} s.");
        }
    }
}
