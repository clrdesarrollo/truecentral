using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;

namespace TrueCentralVms.Server.Services.Workflows;

/// <summary>
/// Motor de automatizaciones: recibe lo que pasa en el sistema (por ahora,
/// eventos y conexión de los paneles de alarma), busca qué workflows lo
/// esperaban y ejecuta sus acciones en orden.
///
/// Reglas de diseño:
/// · Quien publica el disparo NUNCA se bloquea: <see cref="Publish"/> solo
///   encola. Una alarma no puede quedar esperando a que responda un servidor
///   FTP lento.
/// · Las automatizaciones habilitadas se mantienen en memoria y se recargan
///   cuando la API avisa que cambiaron (<see cref="Invalidate"/>): en cada
///   evento del panel no se consulta la base.
/// · Cada workflow tiene un tiempo mínimo entre ejecuciones: un detector que
///   rebota veinte veces no manda veinte correos.
/// · Toda ejecución queda en el historial (qué la disparó, qué acción hizo
///   qué y cuánto demoró) y en la bitácora de auditoría.
/// </summary>
public sealed class WorkflowEngine : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceProvider _services;
    private readonly IEnumerable<IWorkflowActionExecutor> _executorList;
    private readonly Dictionary<string, IWorkflowActionExecutor> _executors;
    private readonly CredentialProtector _credentials;
    private readonly WorkflowStore _store;
    private readonly IHubContext<VmsHub> _hub;
    private readonly AuditService _audit;
    private readonly IConfiguration _config;
    private readonly ILogger<WorkflowEngine> _logger;

    public WorkflowEngine(
        IServiceScopeFactory scopeFactory,
        IServiceProvider services,
        IEnumerable<IWorkflowActionExecutor> executors,
        CredentialProtector credentials,
        WorkflowStore store,
        IHubContext<VmsHub> hub,
        AuditService audit,
        IConfiguration config,
        ILogger<WorkflowEngine> logger)
    {
        _scopeFactory = scopeFactory;
        _services = services;
        _executorList = executors;
        _executors = executors.ToDictionary(e => e.Type, StringComparer.OrdinalIgnoreCase);
        _credentials = credentials;
        _store = store;
        _hub = hub;
        _audit = audit;
        _config = config;
        _logger = logger;
    }

    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(6);

    private readonly Channel<WorkflowTrigger> _incoming =
        System.Threading.Channels.Channel.CreateBounded<WorkflowTrigger>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    /// <summary>Automatizaciones habilitadas en memoria; null = hay que recargarlas.</summary>
    private volatile List<Workflow>? _cache;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    /// <summary>Última ejecución REAL por workflow (para el tiempo mínimo entre ejecuciones).</summary>
    private readonly ConcurrentDictionary<int, DateTime> _lastRun = new();

    /// <summary>Automatizaciones con una verificación de condición sostenida en curso.</summary>
    private readonly ConcurrentDictionary<int, byte> _verifying = new();

    /// <summary>true si el workflow todavía está dentro de su tiempo mínimo entre ejecuciones.</summary>
    private bool IsInCooldown(Workflow workflow, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (workflow.CooldownSeconds <= 0) return false;
        if (!_lastRun.TryGetValue(workflow.Id, out var last)) return false;
        var elapsed = DateTime.UtcNow - last;
        var window = TimeSpan.FromSeconds(workflow.CooldownSeconds);
        if (elapsed >= window) return false;
        remaining = window - elapsed;
        return true;
    }

    /// <summary>Ejecuciones simultáneas permitidas.</summary>
    private SemaphoreSlim? _slots;

    /// <summary>Acciones disponibles, para el catálogo que consume el editor del panel.</summary>
    public IReadOnlyList<IWorkflowActionExecutor> Executors => _executorList.ToList();

    public IWorkflowActionExecutor? FindExecutor(string type) =>
        _executors.TryGetValue(type, out var executor) ? executor : null;

    /// <summary>
    /// La configuración de las automatizaciones cambió. Se recarga de
    /// inmediato (en segundo plano) y no en el próximo evento, porque de esa
    /// lista sale qué paneles hay que sondear rápido: esperar al evento sería
    /// esperar justamente lo que el sondeo rápido debe detectar.
    /// </summary>
    public void Invalidate()
    {
        _cache = null;
        _ = Task.Run(async () =>
        {
            try { await GetWorkflowsAsync(CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "No se pudieron recargar las automatizaciones."); }
        });
    }

    /// <summary>
    /// Publica algo que acaba de pasar. No bloquea ni lanza: si la cola está
    /// llena (motor saturado) el disparo se descarta con una advertencia, que
    /// es preferible a frenar el módulo que lo publicó.
    /// </summary>
    public void Publish(WorkflowTrigger trigger)
    {
        if (!_config.GetValue("Workflows:Enabled", true)) return;
        if (!_incoming.Writer.TryWrite(trigger))
            _logger.LogWarning("Cola de automatizaciones llena: se descartó el disparo '{Summary}'.", trigger.Summary);
    }

    // ------------------------------------------------------------------
    // Ciclo de vida
    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _slots = new SemaphoreSlim(Math.Clamp(_config.GetValue("Workflows:MaxConcurrentRuns", 4), 1, 32));
        var purger = Task.Run(() => PurgeLoopAsync(ct), ct);
        try { await GetWorkflowsAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "No se pudieron cargar las automatizaciones al arrancar."); }
        try
        {
            await foreach (var trigger in _incoming.Reader.ReadAllAsync(ct))
            {
                try { await DispatchAsync(trigger, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Fallo al despachar el disparo '{Summary}' de automatizaciones.", trigger.Summary);
                }
            }
        }
        catch (OperationCanceledException) { /* cierre del servidor */ }
        await purger;
    }

    private async Task DispatchAsync(WorkflowTrigger trigger, CancellationToken ct)
    {
        var workflows = await GetWorkflowsAsync(ct);
        foreach (var workflow in workflows)
        {
            if (workflow.TriggerType != trigger.Type) continue;
            if (!Matches(workflow, trigger)) continue;

            // Tiempo mínimo entre ejecuciones (anti-avalancha). Solo se
            // CONSULTA aquí: se consume al ejecutar de verdad, más abajo. Si
            // se marcara ahora, una verificación que después se descarta
            // (sensor que se restableció) dejaría bloqueada la interrupción
            // larga que venga a continuación, que es justo la que importa.
            if (IsInCooldown(workflow, out var remaining))
            {
                _logger.LogInformation(
                    "Automatización '{Name}' omitida: faltan {Seconds} s para el tiempo mínimo entre ejecuciones.",
                    workflow.Name, (int)remaining.TotalSeconds);
                continue;
            }

            var current = workflow;
            int sustained = WorkflowJson.Conditions(current.ConditionsJson).SustainedSeconds ?? 0;

            // Con condición sostenida, una sola verificación en curso por
            // automatización: un detector que pulsa manda varios eventos del
            // MISMO hecho y no tiene sentido esperarlos todos en paralelo
            // (además de que se comerían los cupos de ejecución).
            if (sustained > 0 && !_verifying.TryAdd(current.Id, 0))
            {
                _logger.LogDebug("Automatización '{Name}': ya hay una verificación en curso.", current.Name);
                continue;
            }

            await _slots!.WaitAsync(ct);
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!await IsSustainedAsync(current, trigger, sustained, ct)) return;
                    // El tiempo mínimo se consume recién ahora: cuenta desde
                    // la última ejecución REAL.
                    if (IsInCooldown(current, out var wait))
                    {
                        _logger.LogInformation(
                            "Automatización '{Name}' omitida: faltan {Seconds} s para el tiempo mínimo entre ejecuciones.",
                            current.Name, (int)wait.TotalSeconds);
                        return;
                    }
                    _lastRun[current.Id] = DateTime.UtcNow;
                    await ExecuteWorkflowAsync(current, trigger, "automático", ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* cierre del servidor */ }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "La automatización '{Name}' terminó con un error inesperado.", current.Name);
                }
                finally
                {
                    if (sustained > 0) _verifying.TryRemove(current.Id, out _);
                    _slots!.Release();
                }
            }, ct);
        }
    }

    // ------------------------------------------------------------------
    // Condiciones
    // ------------------------------------------------------------------

    /// <summary>true si el disparo cumple TODAS las condiciones del workflow.</summary>
    public static bool Matches(Workflow workflow, WorkflowTrigger trigger)
    {
        var conditions = WorkflowJson.Conditions(workflow.ConditionsJson);

        if (Has(conditions.PanelIds) && (trigger.PanelId is null || !conditions.PanelIds!.Contains(trigger.PanelId.Value)))
            return false;
        if (Has(conditions.Kinds) && (trigger.Kind is null || !conditions.Kinds!.Contains(trigger.Kind.Value)))
            return false;
        if (Has(conditions.Severities) && (trigger.Severity is null || !conditions.Severities!.Contains(trigger.Severity.Value)))
            return false;
        if (Has(conditions.Statuses) && (trigger.PanelStatus is null || !conditions.Statuses!.Contains(trigger.PanelStatus.Value)))
            return false;
        if (Has(conditions.AreaNumbers) && (trigger.AreaNumber is null || !conditions.AreaNumbers!.Contains(trigger.AreaNumber.Value)))
            return false;
        if (Has(conditions.ZoneNumbers) && (trigger.ZoneNumber is null || !conditions.ZoneNumbers!.Contains(trigger.ZoneNumber.Value)))
            return false;
        if (Has(conditions.Codes) &&
            !conditions.Codes!.Any(c => string.Equals(c?.Trim(), trigger.Code, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (Has(conditions.Sources) &&
            !conditions.Sources!.Any(s => string.Equals(s, trigger.Source, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (conditions.TextContains is { Length: > 0 } text &&
            (trigger.Text is null || !trigger.Text.Contains(text, StringComparison.OrdinalIgnoreCase)))
            return false;

        return InSchedule(conditions, trigger.At);
    }

    private static bool Has<T>(IReadOnlyList<T>? list) => list is { Count: > 0 };

    /// <summary>
    /// Ventana horaria (hora local del servidor). Si la hora de término es
    /// menor que la de inicio, la ventana cruza la medianoche (22:00 → 06:00)
    /// y el DÍA que se compara es el del comienzo de la ventana.
    /// </summary>
    private static bool InSchedule(WorkflowConditionsDto conditions, DateTime localTime)
    {
        bool hasWindow = TryTime(conditions.FromTime, out var from) & TryTime(conditions.ToTime, out var to);
        var time = localTime.TimeOfDay;
        var day = localTime.DayOfWeek;

        if (hasWindow && to <= from)
        {
            // Ventana nocturna: antes de la hora de término, el día que cuenta
            // es el anterior (una alarma a las 02:00 pertenece a la noche del
            // día previo, que es el que el operador marcó en el calendario).
            if (time >= from) { /* mismo día */ }
            else if (time < to) day = (DayOfWeek)(((int)day + 6) % 7);
            else return false;
        }
        else if (hasWindow && (time < from || time >= to))
        {
            return false;
        }

        if (Has(conditions.DaysOfWeek) && !conditions.DaysOfWeek!.Contains((int)day))
            return false;
        return true;
    }

    private static bool TryTime(string? value, out TimeSpan time)
    {
        time = default;
        return !string.IsNullOrWhiteSpace(value) && TimeSpan.TryParse(value, out time);
    }

    /// <summary>
    /// Condición sostenida: la interrupción tiene que durar. Durante la
    /// ventana configurada se mira el estado de la zona cada segundo (lo
    /// mantiene al día el sondeo rápido) y se descarta la ejecución en cuanto
    /// el sensor queda libre.
    ///
    /// Se toleran los PARPADEOS del detector: un PIR no informa "interrumpido"
    /// de corrido, sino pulsos con pausas de un par de segundos entre medio.
    /// Exigir que esté interrumpido en TODAS las lecturas descartaría
    /// justamente el caso que interesa (alguien dando vueltas frente al
    /// sensor). La pausa máxima tolerada es <c>Workflows:SustainedGapSeconds</c>.
    /// </summary>
    private async Task<bool> IsSustainedAsync(Workflow workflow, WorkflowTrigger trigger, int seconds, CancellationToken ct)
    {
        if (seconds <= 0) return true;
        if (trigger.PanelId is not { } panelId || trigger.ZoneNumber is not { } zoneNumber) return true;

        var tolerance = TimeSpan.FromSeconds(Math.Clamp(_config.GetValue("Workflows:SustainedGapSeconds", 3), 0, 60));
        var started = DateTime.UtcNow;
        var deadline = started + TimeSpan.FromSeconds(Math.Min(seconds, 600));
        var lastActive = started;   // el evento que nos trajo aquí ya es actividad
        string zoneName = trigger.Fields.TryGetValue("zona", out var name) && name.Length > 0
            ? name
            : $"zona {zoneNumber}";

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);

            bool? active = await IsZoneActiveAsync(panelId, zoneNumber, ct);
            if (active is null) continue;                                   // no se pudo leer: no castigar
            if (active.Value) { lastActive = DateTime.UtcNow; continue; }
            if (DateTime.UtcNow - lastActive <= tolerance) continue;        // parpadeo del detector

            _logger.LogInformation(
                "Automatización '{Name}' omitida: el sensor '{Zone}' quedó libre a los {Elapsed} s (se pedían {Needed} s).",
                workflow.Name, zoneName, (int)(DateTime.UtcNow - started).TotalSeconds, seconds);
            return false;
        }
        return true;
    }

    /// <summary>
    /// ¿La zona está interrumpida (o en alarma) ahora? Se lee de la base, que
    /// el sondeo rápido mantiene al día: preguntarle al panel una vez por
    /// segundo lo cargaría sin ganar frescura. null = no se pudo leer.
    /// </summary>
    private async Task<bool?> IsZoneActiveAsync(int panelId, int zoneNumber, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var zone = await db.AlarmZones.AsNoTracking()
                .Where(z => z.AlarmPanelId == panelId && z.Number == zoneNumber)
                .Select(z => new { z.Status, z.InAlarm })
                .FirstOrDefaultAsync(ct);
            return zone is null ? null : zone.InAlarm || zone.Status == AlarmZoneStatus.Triggered;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo leer el estado de la zona {Zone} del panel {Panel}.", zoneNumber, panelId);
            return null;
        }
    }

    /// <summary>
    /// Le dice al módulo de paneles qué paneles necesitan sondeo rápido: los
    /// de las automatizaciones con condición sostenida (sin él, una
    /// interrupción de pocos segundos ocurre entera entre dos lecturas).
    /// </summary>
    private void ApplyFastPoll(List<Workflow> workflows)
    {
        bool all = false;
        var panels = new HashSet<int>();
        foreach (var workflow in workflows)
        {
            var conditions = WorkflowJson.Conditions(workflow.ConditionsJson);
            if (conditions.SustainedSeconds is not > 0) continue;
            if (conditions.PanelIds is { Count: > 0 } ids) panels.UnionWith(ids);
            else all = true;
        }
        try { _services.GetRequiredService<AlarmPanelService>().SetFastPoll(all, panels); }
        catch (Exception ex) { _logger.LogWarning(ex, "No se pudo ajustar el sondeo rápido de los paneles."); }
    }

    // ------------------------------------------------------------------
    // Ejecución
    // ------------------------------------------------------------------

    /// <summary>
    /// Ejecuta a mano un workflow con un evento de ejemplo (botón "Probar").
    /// Hace lo mismo que haría en producción: manda el correo, sube el
    /// archivo y suena el parlante de verdad.
    /// </summary>
    public async Task<WorkflowRunDto?> TestAsync(int workflowId, string startedBy, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var workflow = await db.Workflows.AsNoTracking().Include(w => w.Actions)
            .FirstOrDefaultAsync(w => w.Id == workflowId, ct);
        if (workflow is null) return null;

        var conditions = WorkflowJson.Conditions(workflow.ConditionsJson);
        int? panelId = conditions.PanelIds is { Count: > 0 } ? conditions.PanelIds[0] : null;
        var panel = await db.AlarmPanels.AsNoTracking().Include(p => p.Areas).Include(p => p.Zones).AsSplitQuery()
            .Where(p => panelId == null || p.Id == panelId)
            .OrderBy(p => p.Id)
            .FirstOrDefaultAsync(ct);

        return await ExecuteWorkflowAsync(workflow, WorkflowTrigger.Sample(workflow, panel), startedBy, ct);
    }

    private async Task<WorkflowRunDto> ExecuteWorkflowAsync(Workflow workflow, WorkflowTrigger trigger,
        string startedBy, CancellationToken ct)
    {
        var run = new WorkflowRun
        {
            WorkflowId = workflow.Id,
            WorkflowName = Cut(workflow.Name, 128),
            StartedAt = DateTime.UtcNow,
            TriggerSummary = Cut(trigger.Summary, 256),
            TriggerJson = WorkflowJson.Serialize(trigger.Fields),
            StartedBy = Cut(startedBy, 64),
        };
        var steps = new List<WorkflowRunStepDto>();
        var files = new List<WorkflowFile>();
        var alerts = new List<long>();
        var channels = new List<int>();
        bool success = true;
        string? error = null;

        int timeoutSeconds = Math.Clamp(_config.GetValue("Workflows:ActionTimeoutSeconds", 120), 5, 900);

        foreach (var action in workflow.Actions.Where(a => a.Enabled).OrderBy(a => a.Order))
        {
            var executor = FindExecutor(action.Type);
            var watch = Stopwatch.StartNew();
            WorkflowStepResult result;

            if (executor is null)
            {
                result = WorkflowStepResult.Fail($"El servidor no conoce la acción '{action.Type}'.");
            }
            else
            {
                try
                {
                    if (action.DelaySeconds > 0)
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(action.DelaySeconds, 600)), ct);

                    // Cada acción tiene su propio tope de tiempo: un servidor
                    // FTP que no responde no puede dejar colgada la ejecución.
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                    var context = new WorkflowActionContext
                    {
                        Workflow = workflow,
                        Action = action,
                        Trigger = trigger,
                        Config = WorkflowJson.ParseConfig(action.ConfigJson),
                        Secret = Decrypt(action),
                        Files = files,
                        Channels = channels,
                        Alerts = alerts,
                    };
                    result = await executor.ExecuteAsync(context, timeout.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    result = WorkflowStepResult.Fail("El servidor se está deteniendo.");
                }
                catch (OperationCanceledException)
                {
                    result = WorkflowStepResult.Fail($"La acción superó el tiempo máximo ({timeoutSeconds} s).");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "La acción '{Type}' de la automatización '{Name}' falló.", action.Type, workflow.Name);
                    result = WorkflowStepResult.Fail($"Error inesperado: {ex.Message}");
                }
            }
            watch.Stop();

            steps.Add(new WorkflowRunStepDto(action.Order, action.Type,
                executor?.Label ?? action.Type, result.Success, Cut(result.Detail, 512),
                (int)watch.ElapsedMilliseconds, result.Files));

            if (result.Success) continue;
            success = false;
            error ??= $"{executor?.Label ?? action.Type}: {result.Detail}";
            if (!action.ContinueOnError) break;
        }

        run.FinishedAt = DateTime.UtcNow;
        run.Success = success;
        run.Error = Truncate(error, 512);
        run.StepsJson = WorkflowJson.Serialize(steps);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            db.WorkflowRuns.Add(run);
            // El contador y la última ejecución son del workflow, no del historial.
            await db.Workflows.Where(w => w.Id == workflow.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(w => w.LastRunAt, run.StartedAt)
                    .SetProperty(w => w.RunCount, w => w.RunCount + 1), CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);

            // Las alertas nacieron antes de que la ejecución tuviera id: recién
            // ahora se pueden enlazar con ella.
            if (alerts.Count > 0)
                await db.WorkflowAlerts.Where(a => alerts.Contains(a.Id))
                    .ExecuteUpdateAsync(u => u.SetProperty(a => a.RunId, run.Id), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo guardar la ejecución de la automatización '{Name}'.", workflow.Name);
        }

        var dto = WorkflowMapper.ToDto(run, steps);
        try { await _hub.Clients.All.SendAsync(VmsHubContract.WorkflowRunCompleted, dto, CancellationToken.None); }
        catch (Exception ex) { _logger.LogDebug(ex, "No se pudo publicar la ejecución por el hub."); }

        // La prueba manual la audita la API (tiene el usuario y su IP); aquí
        // solo queda constancia de lo que hizo el servidor por su cuenta.
        if (startedBy == "automático")
            await AuditRunAsync(workflow, run, steps);

        _logger.LogInformation("Automatización '{Name}': {Result} ({Steps} acciones) por {Trigger}",
            workflow.Name, success ? "ejecutada" : "con errores", steps.Count, run.TriggerSummary);
        return dto;
    }

    private Task AuditRunAsync(Workflow workflow, WorkflowRun run, List<WorkflowRunStepDto> steps)
    {
        int failed = steps.Count(s => !s.Success);
        string detail = $"La automatización '{workflow.Name}' se ejecutó por: {run.TriggerSummary}. " +
                        $"{steps.Count} acción(es)" + (failed > 0 ? $", {failed} con error." : " sin errores.");
        return _audit.LogSystemAsync("workflows", run.Success ? "workflow-executed" : "workflow-failed",
            targetType: "workflow", targetId: workflow.Id.ToString(), targetName: workflow.Name,
            detail: detail, success: run.Success, origin: "server",
            data: new
            {
                run.TriggerSummary,
                Trigger = workflow.TriggerType,
                Acciones = steps.Select(s => new { s.Order, s.Type, s.Success, s.Detail, s.ElapsedMs }),
            });
    }

    private string? Decrypt(WorkflowAction action)
    {
        if (action.SecretCiphertext is not { Length: > 0 }) return null;
        try { return _credentials.Unprotect(action.SecretCiphertext); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo descifrar la contraseña de una acción de automatización.");
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Caché de automatizaciones habilitadas
    // ------------------------------------------------------------------

    private async Task<List<Workflow>> GetWorkflowsAsync(CancellationToken ct)
    {
        if (_cache is { } cached) return cached;
        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_cache is { } current) return current;
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var list = await db.Workflows.AsNoTracking()
                .Include(w => w.Actions)
                .Where(w => w.Enabled)
                .ToListAsync(ct);
            foreach (var workflow in list)
                _lastRun.TryAdd(workflow.Id, workflow.LastRunAt ?? DateTime.MinValue);
            _cache = list;
            ApplyFastPoll(list);
            return list;
        }
        finally { _cacheLock.Release(); }
    }

    // ------------------------------------------------------------------
    // Purga del historial
    // ------------------------------------------------------------------

    private async Task PurgeLoopAsync(CancellationToken ct)
    {
        // Un respiro al arrancar: el servidor está levantando media y paneles.
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await PurgeAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogWarning(ex, "Fallo en la purga del historial de automatizaciones."); }
            try { await Task.Delay(PurgeInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PurgeAsync(CancellationToken ct)
    {
        int retentionDays = _config.GetValue("Workflows:RetentionDays", 90);
        int maxRuns = _config.GetValue("Workflows:MaxRuns", 50_000);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();

        int removed = 0;
        if (retentionDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            removed += await db.WorkflowRuns.Where(r => r.StartedAt < cutoff).ExecuteDeleteAsync(ct);
            // Las fotos de esas ejecuciones se van con ellas (viven en disco).
            _store.PurgeOlderThan(DateTime.Now.AddDays(-retentionDays));
        }
        if (retentionDays > 0)
        {
            // Las alertas ya confirmadas envejecen con el historial; una alerta
            // PENDIENTE no se borra sola nunca: es justamente la evidencia de
            // que nadie se dio por enterado.
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            removed += await db.WorkflowAlerts
                .Where(a => a.RaisedAt < cutoff && a.AcknowledgedAt != null)
                .ExecuteDeleteAsync(ct);
        }
        if (maxRuns > 0)
        {
            int total = await db.WorkflowRuns.CountAsync(ct);
            if (total > maxRuns)
            {
                var threshold = await db.WorkflowRuns.OrderByDescending(r => r.StartedAt)
                    .Skip(maxRuns).Select(r => r.StartedAt).FirstAsync(ct);
                removed += await db.WorkflowRuns.Where(r => r.StartedAt <= threshold).ExecuteDeleteAsync(ct);
            }
        }
        if (removed > 0)
            _logger.LogInformation("Purga del historial de automatizaciones: {Count} ejecuciones eliminadas.", removed);
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
