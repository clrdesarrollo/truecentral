using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;

namespace TrueCentralVms.Server.Services.Workflows;

/// <summary>
/// Motor de automatizaciones: recibe lo que pasa en el sistema (paneles de
/// alarma, conexión de equipos, analíticas de cámara, patentes, control de
/// acceso, el reloj y llamadas externas), busca qué workflows lo esperaban y
/// recorre su DIAGRAMA DE FLUJO: desde el disparador, por condiciones
/// (sí/no), esperas y acciones, siguiendo las conexiones que dibujó el
/// operador.
///
/// Reglas de diseño:
/// · Quien publica el disparo NUNCA se bloquea: <see cref="Publish"/> solo
///   encola. Una alarma no puede quedar esperando a que responda un servidor
///   FTP lento.
/// · Las automatizaciones habilitadas se mantienen en memoria y se recargan
///   cuando la API avisa que cambiaron (<see cref="Invalidate"/>): en cada
///   evento no se consulta la base.
/// · Cada workflow tiene un tiempo mínimo entre ejecuciones: un detector que
///   rebota veinte veces no manda veinte correos.
/// · Toda ejecución queda en el historial (qué la disparó, qué paso hizo qué
///   y cuánto demoró) y en la bitácora de auditoría.
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

    /// <summary>Tope de pasos recorridos por ejecución: un ciclo dibujado por error no puede correr para siempre.</summary>
    private const int MaxStepsPerRun = 200;

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
    /// lista sale qué paneles hay que sondear rápido y a qué cámaras hay que
    /// suscribirse: esperar al evento sería esperar justamente lo que la
    /// suscripción debe recibir.
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

    /// <summary>La automatización de llamada externa que tiene esta clave, o null.</summary>
    public async Task<Workflow?> FindByHookKeyAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var workflows = await GetWorkflowsAsync(ct);
        return workflows.FirstOrDefault(w => w.TriggerType == WorkflowTriggerTypes.Webhook &&
                                             string.Equals(WorkflowJson.Conditions(w.ConditionsJson).HookKey, key, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // Ciclo de vida
    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _slots = new SemaphoreSlim(Math.Clamp(_config.GetValue("Workflows:MaxConcurrentRuns", 4), 1, 32));
        var purger = Task.Run(() => PurgeLoopAsync(ct), ct);
        var clock = Task.Run(() => ScheduleLoopAsync(ct), ct);
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
        await Task.WhenAll(purger, clock);
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
    // Reloj: disparador "horario"
    // ------------------------------------------------------------------

    /// <summary>
    /// Una vez por minuto (en el cambio de minuto) publica un tic si existe
    /// alguna automatización de horario; cada una decide si es su hora con
    /// sus condiciones (horas + días de la semana).
    /// </summary>
    private async Task ScheduleLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.Now;
            var nextMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Local).AddMinutes(1);
            try { await Task.Delay(nextMinute - now + TimeSpan.FromMilliseconds(200), ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                var workflows = await GetWorkflowsAsync(ct);
                if (workflows.Any(w => w.TriggerType == WorkflowTriggerTypes.Schedule))
                    Publish(WorkflowTrigger.FromSchedule(DateTime.Now));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogWarning(ex, "Fallo en el reloj de automatizaciones."); }
        }
    }

    // ------------------------------------------------------------------
    // Condiciones
    // ------------------------------------------------------------------

    /// <summary>true si el disparo cumple TODAS las condiciones del disparador del workflow.</summary>
    public static bool Matches(Workflow workflow, WorkflowTrigger trigger) =>
        Matches(WorkflowJson.Conditions(workflow.ConditionsJson), trigger);

    /// <summary>
    /// true si el disparo cumple TODAS las condiciones dadas. Cada lista vacía
    /// significa "sin filtrar"; los campos que el disparo no trae (p. ej.
    /// patente en un evento de panel) hacen fallar la condición solo si se
    /// pidió filtrar por ellos.
    /// </summary>
    public static bool Matches(WorkflowConditionsDto conditions, WorkflowTrigger trigger)
    {
        // --- Paneles ---
        if (Has(conditions.PanelIds) && (trigger.PanelId is null || !conditions.PanelIds!.Contains(trigger.PanelId.Value)))
            return false;
        if (Has(conditions.Kinds) && (trigger.Kind is null || !conditions.Kinds!.Contains(trigger.Kind.Value)))
            return false;
        // «Sensor interrumpido» (y su restauración) nacen por diferencia de
        // estado con el área desarmada y no tienen severidad propia: el
        // filtro de severidad no los afecta, si no una automatización con
        // "Advertencia" marcada nunca dispararía con el sensor.
        bool stateDiff = trigger.Kind is AlarmEventKind.ZoneTriggered or AlarmEventKind.ZoneRestored;
        if (!stateDiff && Has(conditions.Severities) &&
            (trigger.Severity is null || !conditions.Severities!.Contains(trigger.Severity.Value)))
            return false;
        if (Has(conditions.Statuses) && (trigger.PanelStatus is null || !conditions.Statuses!.Contains(trigger.PanelStatus.Value)))
            return false;
        if (Has(conditions.AreaNumbers) && (trigger.AreaNumber is null || !conditions.AreaNumbers!.Contains(trigger.AreaNumber.Value)))
            return false;
        if (Has(conditions.ZoneNumbers) && (trigger.ZoneNumber is null || !conditions.ZoneNumbers!.Contains(trigger.ZoneNumber.Value)))
            return false;
        // Zonas y áreas por panel: el número 2 de un panel no es el 2 de otro.
        if (Has(conditions.ZoneKeys) &&
            (trigger.PanelId is null || trigger.ZoneNumber is null || !conditions.ZoneKeys!.Contains($"{trigger.PanelId}:{trigger.ZoneNumber}")))
            return false;
        if (Has(conditions.AreaKeys) &&
            (trigger.PanelId is null || trigger.AreaNumber is null || !conditions.AreaKeys!.Contains($"{trigger.PanelId}:{trigger.AreaNumber}")))
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

        // --- Equipos ---
        if (Has(conditions.DeviceKinds) &&
            !conditions.DeviceKinds!.Any(k => string.Equals(k, trigger.DeviceKind, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (Has(conditions.DeviceStatuses) &&
            !conditions.DeviceStatuses!.Any(s => string.Equals(s, trigger.DeviceStatus, StringComparison.OrdinalIgnoreCase)))
            return false;
        // Los equipos marcados por tipo se evalúan según la clase del disparo:
        // un filtro de cámaras no bloquea un evento de terminal de acceso si
        // además se marcaron terminales (una automatización "cualquier equipo
        // de estos" con equipos de varias clases).
        bool anyDeviceFilter = Has(conditions.DeviceIds) || Has(conditions.AccessDeviceIds) || Has(conditions.SpeakerIds);
        if (anyDeviceFilter)
        {
            bool listed =
                (trigger.DeviceId is { } d && Has(conditions.DeviceIds) && conditions.DeviceIds!.Contains(d)) ||
                (trigger.AccessDeviceId is { } a && Has(conditions.AccessDeviceIds) && conditions.AccessDeviceIds!.Contains(a)) ||
                (trigger.SpeakerId is { } s && Has(conditions.SpeakerIds) && conditions.SpeakerIds!.Contains(s));
            if (!listed) return false;
        }

        // --- Video ---
        if (Has(conditions.VideoEventKinds) && (trigger.VideoKind is null || !conditions.VideoEventKinds!.Contains(trigger.VideoKind.Value)))
            return false;
        if (Has(conditions.ChannelIds) && (trigger.ChannelId is null || !conditions.ChannelIds!.Contains(trigger.ChannelId.Value)))
            return false;

        // --- Patentes ---
        if (conditions.MinConfidence is { } minimum && minimum > 0 && (trigger.Confidence is null || trigger.Confidence < minimum))
            return false;
        string mode = conditions.PlateMatch ?? (Has(conditions.Plates) ? "listed" : "any");
        if (mode != "any" && Has(conditions.Plates))
        {
            bool inList = trigger.Plate is { Length: > 0 } plate && conditions.Plates!.Any(p => PlateMatches(p, plate));
            if (mode == "listed" && !inList) return false;
            if (mode == "unlisted" && inList) return false;
        }

        // --- Control de acceso ---
        if (Has(conditions.AccessKinds) && (trigger.AccessKind is null || !conditions.AccessKinds!.Contains(trigger.AccessKind.Value)))
            return false;
        if (Has(conditions.Credentials) && (trigger.Credential is null || !conditions.Credentials!.Contains(trigger.Credential.Value)))
            return false;
        if (Has(conditions.DoorNumbers) && (trigger.DoorNumber is null || !conditions.DoorNumbers!.Contains(trigger.DoorNumber.Value)))
            return false;
        if (Has(conditions.EmployeeNos) &&
            !conditions.EmployeeNos!.Any(e => string.Equals(e?.Trim(), trigger.EmployeeNo, StringComparison.OrdinalIgnoreCase)))
            return false;

        // --- Horario ---
        if (Has(conditions.ScheduleTimes) && trigger.Type == WorkflowTriggerTypes.Schedule)
        {
            string hhmm = trigger.At.ToString("HH:mm");
            if (!conditions.ScheduleTimes!.Any(t => TimeSpan.TryParse(t, out var ts) && ts.ToString(@"hh\:mm") == hhmm))
                return false;
        }

        return InSchedule(conditions, trigger.At);
    }

    private static bool Has<T>(IReadOnlyList<T>? list) => list is { Count: > 0 };

    /// <summary>Patente contra un patrón con comodines (* = cualquier cosa, ? = un carácter); sin distinguir mayúsculas ni separadores.</summary>
    public static bool PlateMatches(string? pattern, string plate)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        string clean = new(pattern.Where(c => char.IsLetterOrDigit(c) || c is '*' or '?').ToArray());
        if (clean.Length == 0) return false;
        if (!clean.Contains('*') && !clean.Contains('?'))
            return string.Equals(clean, plate, StringComparison.OrdinalIgnoreCase);
        string regex = "^" + Regex.Escape(clean).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(plate, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    /// <summary>
    /// Calendario de la automatización: la franja principal (DaysOfWeek,
    /// FromTime, ToTime) más las franjas adicionales de TimeBands. Sin ninguna
    /// franja definida está siempre activa; con franjas, basta que UNA se
    /// cumpla en la hora local del servidor.
    /// </summary>
    private static bool InSchedule(WorkflowConditionsDto conditions, DateTime localTime)
    {
        bool anyBand = false;
        if (Has(conditions.DaysOfWeek) || !string.IsNullOrWhiteSpace(conditions.FromTime) || !string.IsNullOrWhiteSpace(conditions.ToTime))
        {
            anyBand = true;
            if (InBand(conditions.DaysOfWeek, conditions.FromTime, conditions.ToTime, localTime)) return true;
        }
        foreach (var band in conditions.TimeBands ?? [])
        {
            if (band is null) continue;
            anyBand = true;
            if (InBand(band.DaysOfWeek, band.FromTime, band.ToTime, localTime)) return true;
        }
        return !anyBand;
    }

    /// <summary>
    /// Una franja: días de la semana y ventana horaria. Si la hora de término
    /// es menor que la de inicio, la ventana cruza la medianoche (22:00 → 06:00)
    /// y el DÍA que se compara es el del comienzo de la ventana.
    /// </summary>
    private static bool InBand(IReadOnlyList<int>? daysOfWeek, string? fromTime, string? toTime, DateTime localTime)
    {
        bool hasWindow = TryTime(fromTime, out var from) & TryTime(toTime, out var to);
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
        else if (!hasWindow && TryTime(fromTime, out var onlyFrom) && time < onlyFrom)
        {
            return false; // solo "desde": rige hasta el fin del día
        }
        else if (!hasWindow && TryTime(toTime, out var onlyTo) && time >= onlyTo)
        {
            return false; // solo "hasta": rige desde el comienzo del día
        }

        if (Has(daysOfWeek) && !daysOfWeek!.Contains((int)day))
            return false;
        return true;
    }

    private static bool TryTime(string? value, out TimeSpan time) => WorkflowGraph.TryParseWindowTime(value, out time);

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
            else if (conditions.ZoneKeys is { Count: > 0 } keys)
                panels.UnionWith(keys.Select(k => int.TryParse(k.Split(':')[0], out int p) ? p : -1).Where(p => p > 0));
            else all = true;
        }
        try { _services.GetRequiredService<AlarmPanelService>().SetFastPoll(all, panels); }
        catch (Exception ex) { _logger.LogWarning(ex, "No se pudo ajustar el sondeo rápido de los paneles."); }
    }

    /// <summary>
    /// Le dice al escucha de eventos de cámara a qué equipos suscribirse: los
    /// de las automatizaciones con disparador de evento de video (todos, si
    /// alguna no filtra por equipo). Sin automatizaciones que los pidan no se
    /// abre ningún canal: el operador no configura nada aparte.
    /// </summary>
    private void ApplyVideoSubscriptions(List<Workflow> workflows)
    {
        bool all = false;
        var devices = new HashSet<int>();
        foreach (var workflow in workflows.Where(w => w.TriggerType == WorkflowTriggerTypes.VideoEvent))
        {
            var conditions = WorkflowJson.Conditions(workflow.ConditionsJson);
            if (conditions.DeviceIds is { Count: > 0 } ids) devices.UnionWith(ids);
            else if (conditions.ChannelIds is { Count: > 0 }) devices.Add(-1); // se resuelve a equipos en el servicio
            else all = true;
        }
        var channels = workflows.Where(w => w.TriggerType == WorkflowTriggerTypes.VideoEvent)
            .SelectMany(w => WorkflowJson.Conditions(w.ConditionsJson).ChannelIds ?? [])
            .ToHashSet();
        devices.Remove(-1);
        try { _services.GetRequiredService<VideoEventService>().SetWanted(all, devices, channels); }
        catch (Exception ex) { _logger.LogWarning(ex, "No se pudo ajustar la escucha de eventos de cámara."); }
    }

    // ------------------------------------------------------------------
    // Ejecución
    // ------------------------------------------------------------------

    /// <summary>
    /// Ejecuta a mano un workflow con un evento de ejemplo (botón "Probar").
    /// Hace lo mismo que haría en producción: manda el correo, sube el
    /// archivo, abre la puerta y suena el parlante de verdad.
    /// </summary>
    public async Task<WorkflowRunDto?> TestAsync(int workflowId, string startedBy, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var workflow = await db.Workflows.AsNoTracking().Include(w => w.Actions)
            .FirstOrDefaultAsync(w => w.Id == workflowId, ct);
        if (workflow is null) return null;

        var conditions = WorkflowJson.Conditions(workflow.ConditionsJson);

        // El ejemplo usa, si se puede, un equipo real de los que filtra la
        // automatización: así los nombres del correo de prueba son los de verdad.
        int? panelId = conditions.PanelIds is { Count: > 0 } ? conditions.PanelIds[0] : null;
        var panel = await db.AlarmPanels.AsNoTracking().Include(p => p.Areas).Include(p => p.Zones).AsSplitQuery()
            .Where(p => panelId == null || p.Id == panelId)
            .OrderBy(p => p.Id)
            .FirstOrDefaultAsync(ct);

        Data.Entities.Channel? channel = null;
        if (conditions.ChannelIds is { Count: > 0 } channelIds)
            channel = await db.Channels.AsNoTracking().Include(c => c.Device).FirstOrDefaultAsync(c => c.Id == channelIds[0], ct);
        int? deviceId = channel?.DeviceId ?? (conditions.DeviceIds is { Count: > 0 } ? conditions.DeviceIds[0] : null);
        var device = channel?.Device ?? await db.Devices.AsNoTracking()
            .Where(d => deviceId == null || d.Id == deviceId).OrderBy(d => d.Id).FirstOrDefaultAsync(ct);
        channel ??= device is null ? null
            : await db.Channels.AsNoTracking().Where(c => c.DeviceId == device.Id).OrderBy(c => c.ChannelNumber).FirstOrDefaultAsync(ct);

        int? accessId = conditions.AccessDeviceIds is { Count: > 0 } ? conditions.AccessDeviceIds[0] : null;
        var accessDevice = await db.AccessDevices.AsNoTracking().Include(d => d.Doors)
            .Where(d => accessId == null || d.Id == accessId).OrderBy(d => d.Id).FirstOrDefaultAsync(ct);
        var door = accessDevice?.Doors.OrderBy(d => d.Number)
            .FirstOrDefault(d => conditions.DoorNumbers is not { Count: > 0 } || conditions.DoorNumbers.Contains(d.Number))
            ?? accessDevice?.Doors.OrderBy(d => d.Number).FirstOrDefault();

        return await ExecuteWorkflowAsync(workflow,
            WorkflowTrigger.Sample(workflow, panel, device, channel, accessDevice, door), startedBy, ct);
    }

    /// <summary>Lo que se acumula mientras se recorre el diagrama de una ejecución.</summary>
    private sealed class RunState
    {
        public required Workflow Workflow;
        public required WorkflowTrigger Trigger;
        public required WorkflowGraph Graph;
        public required Dictionary<string, WorkflowAction> ActionsByNode;
        public required CancellationToken Ct;
        public readonly List<WorkflowRunStepDto> Steps = [];
        public readonly List<WorkflowFile> Files = [];
        public readonly List<long> Alerts = [];
        public readonly List<int> Channels = [];
        public bool Success = true;
        public string? Error;
        public int Visited;
        public int TimeoutSeconds;
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

        var graph = WorkflowGraph.Of(workflow);
        var state = new RunState
        {
            Workflow = workflow,
            Trigger = trigger,
            Graph = graph,
            ActionsByNode = workflow.Actions
                .Select(a => (Key: a.NodeId ?? $"a{a.Id}", Action: a))
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Action, StringComparer.Ordinal),
            Ct = ct,
            TimeoutSeconds = Math.Clamp(_config.GetValue("Workflows:ActionTimeoutSeconds", 120), 5, 900),
        };

        // La foto que trajo el propio evento (analíticas con captura) queda
        // disponible como si la hubiera tomado la acción "capturar foto".
        if (trigger.Image is { Length: > 0 } image)
        {
            string suffix = trigger.Fields.TryGetValue("camara", out var cam) && cam.Length > 0 ? cam : "evento";
            if (_store.Save(image, trigger.At, suffix) is { } relative)
            {
                state.Files.Add(new WorkflowFile(relative, _store.FullPath(relative)!,
                    $"{WorkflowStore.Sanitize(suffix)}-{trigger.At:yyyyMMdd-HHmmss}.jpg"));
                if (trigger.ChannelId is { } channelId) state.Channels.Add(channelId);
            }
        }

        try
        {
            foreach (var next in graph.Next(graph.Trigger.Id, WorkflowPorts.Next))
                await WalkAsync(state, next);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            state.Success = false;
            state.Error ??= "El servidor se está deteniendo.";
        }

        if (state.Steps.Count == 0)
        {
            // Todas las ramas terminaron en condiciones que no se cumplieron:
            // no es un error, pero conviene que el historial lo diga.
            state.Steps.Add(new WorkflowRunStepDto(1, "condition", "Condiciones", true,
                "Ninguna acción correspondía ejecutar con este evento.", 0, null));
        }

        run.FinishedAt = DateTime.UtcNow;
        run.Success = state.Success;
        run.Error = Truncate(state.Error, 512);
        run.StepsJson = WorkflowJson.Serialize(state.Steps);

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
            if (state.Alerts.Count > 0)
                await db.WorkflowAlerts.Where(a => state.Alerts.Contains(a.Id))
                    .ExecuteUpdateAsync(u => u.SetProperty(a => a.RunId, run.Id), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo guardar la ejecución de la automatización '{Name}'.", workflow.Name);
        }

        var dto = WorkflowMapper.ToDto(run, state.Steps);
        try { await _hub.Clients.All.SendAsync(VmsHubContract.WorkflowRunCompleted, dto, CancellationToken.None); }
        catch (Exception ex) { _logger.LogDebug(ex, "No se pudo publicar la ejecución por el hub."); }

        // La prueba manual la audita la API (tiene el usuario y su IP); aquí
        // solo queda constancia de lo que hizo el servidor por su cuenta.
        if (startedBy == "automático")
            await AuditRunAsync(workflow, run, state.Steps);

        _logger.LogInformation("Automatización '{Name}': {Result} ({Steps} pasos) por {Trigger}",
            workflow.Name, state.Success ? "ejecutada" : "con errores", state.Steps.Count, run.TriggerSummary);
        return dto;
    }

    /// <summary>
    /// Recorre el diagrama desde un nodo. Las ramas que salen de un mismo
    /// puerto se ejecutan una tras otra, en el orden en que se dibujaron (así
    /// el correo de la segunda rama ya tiene la foto que tomó la primera).
    /// </summary>
    private async Task WalkAsync(RunState state, WorkflowGraph.Node node)
    {
        if (++state.Visited > MaxStepsPerRun)
        {
            state.Success = false;
            state.Error ??= $"La ejecución superó los {MaxStepsPerRun} pasos: revise que el diagrama no tenga un ciclo.";
            return;
        }
        state.Ct.ThrowIfCancellationRequested();

        switch (node.Kind)
        {
            case WorkflowNodeKinds.End:
                return;

            case WorkflowNodeKinds.Condition:
            {
                bool yes = node.Conditions is null || Matches(node.Conditions, state.Trigger);
                state.Steps.Add(new WorkflowRunStepDto(state.Steps.Count + 1, "condition",
                    node.Label ?? "Condición", true, yes ? "Se cumple: sigue por «Sí»." : "No se cumple: sigue por «No».",
                    0, null, node.Id));
                foreach (var next in state.Graph.Next(node.Id, yes ? WorkflowPorts.Yes : WorkflowPorts.No))
                    await WalkAsync(state, next);
                return;
            }

            case WorkflowNodeKinds.Delay:
            {
                int seconds = Math.Clamp(node.DelaySeconds, 0, WorkflowGraph.MaxDelaySeconds);
                var watch = Stopwatch.StartNew();
                if (seconds > 0) await Task.Delay(TimeSpan.FromSeconds(seconds), state.Ct);
                state.Steps.Add(new WorkflowRunStepDto(state.Steps.Count + 1, "delay",
                    node.Label ?? "Espera", true, $"Esperó {seconds} s.", (int)watch.ElapsedMilliseconds, null, node.Id));
                foreach (var next in state.Graph.Next(node.Id, WorkflowPorts.Next))
                    await WalkAsync(state, next);
                return;
            }

            case WorkflowNodeKinds.Action:
            {
                bool ok = await RunActionAsync(state, node);
                foreach (var next in state.Graph.Next(node.Id, ok ? WorkflowPorts.Next : WorkflowPorts.Error))
                    await WalkAsync(state, next);
                return;
            }

            default:
                // El disparador no se vuelve a recorrer: una conexión hacia él no debería existir.
                return;
        }
    }

    /// <summary>Ejecuta la acción de un nodo y deja su paso en el historial. Devuelve true si terminó bien.</summary>
    private async Task<bool> RunActionAsync(RunState state, WorkflowGraph.Node node)
    {
        if (!state.ActionsByNode.TryGetValue(node.Id, out var action))
        {
            state.Steps.Add(new WorkflowRunStepDto(state.Steps.Count + 1, node.Type ?? "action",
                node.Label ?? node.Type ?? "Acción", false, "La acción no tiene configuración guardada.", 0, null, node.Id));
            state.Success = false;
            state.Error ??= $"{node.Label ?? node.Type}: sin configuración.";
            return false;
        }
        var executor = FindExecutor(action.Type);
        string label = node.Label ?? executor?.Label ?? action.Type;

        if (!action.Enabled)
        {
            state.Steps.Add(new WorkflowRunStepDto(state.Steps.Count + 1, action.Type, label, true,
                "Acción desactivada: se saltó.", 0, null, node.Id));
            return true;
        }

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
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(action.DelaySeconds, 600)), state.Ct);

                // Cada acción tiene su propio tope de tiempo: un servidor
                // FTP que no responde no puede dejar colgada la ejecución.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(state.Ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(state.TimeoutSeconds));

                var context = new WorkflowActionContext
                {
                    Workflow = state.Workflow,
                    Action = action,
                    Trigger = state.Trigger,
                    Config = WorkflowJson.ParseConfig(action.ConfigJson),
                    Secret = Decrypt(action),
                    Files = state.Files,
                    Channels = state.Channels,
                    Alerts = state.Alerts,
                };
                result = await executor.ExecuteAsync(context, timeout.Token);
            }
            catch (OperationCanceledException) when (state.Ct.IsCancellationRequested)
            {
                result = WorkflowStepResult.Fail("El servidor se está deteniendo.");
            }
            catch (OperationCanceledException)
            {
                result = WorkflowStepResult.Fail($"La acción superó el tiempo máximo ({state.TimeoutSeconds} s).");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "La acción '{Type}' de la automatización '{Name}' falló.", action.Type, state.Workflow.Name);
                result = WorkflowStepResult.Fail($"Error inesperado: {ex.Message}");
            }
        }
        watch.Stop();

        state.Steps.Add(new WorkflowRunStepDto(state.Steps.Count + 1, action.Type, label, result.Success,
            Cut(result.Detail, 512), (int)watch.ElapsedMilliseconds, result.Files, node.Id));

        if (result.Success) return true;
        state.Success = false;
        state.Error ??= $"{label}: {result.Detail}";
        return false;
    }

    private Task AuditRunAsync(Workflow workflow, WorkflowRun run, List<WorkflowRunStepDto> steps)
    {
        int failed = steps.Count(s => !s.Success);
        string detail = $"La automatización '{workflow.Name}' se ejecutó por: {run.TriggerSummary}. " +
                        $"{steps.Count} paso(s)" + (failed > 0 ? $", {failed} con error." : " sin errores.");
        return _audit.LogSystemAsync("workflows", run.Success ? "workflow-executed" : "workflow-failed",
            targetType: "workflow", targetId: workflow.Id.ToString(), targetName: workflow.Name,
            detail: detail, success: run.Success, origin: "server",
            data: new
            {
                run.TriggerSummary,
                Trigger = workflow.TriggerType,
                Pasos = steps.Select(s => new { s.Order, s.Type, s.Label, s.Success, s.Detail, s.ElapsedMs }),
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
            ApplyVideoSubscriptions(list);
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
