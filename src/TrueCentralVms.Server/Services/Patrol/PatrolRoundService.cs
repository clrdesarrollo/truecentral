using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Services.Licensing;
using TrueCentralVms.Server.Services.Supervisor;
using TrueCentralVms.Server.Services.Workflows;

namespace TrueCentralVms.Server.Services.Patrol;

/// <summary>
/// Verificación de rondas (fase 2). Cada pocos segundos:
/// <list type="number">
/// <item>prepara las rondas esperadas de las próximas 24 h según la programación (salta feriados si así se pide);</item>
/// <item>inicia la ronda cuando un bastón sale de una de las bases de la ruta (decisión del usuario: el inicio real es la salida);</item>
/// <item>la evalúa cuando ese bastón vuelve y se descarga (estricto: orden y hora de cada punto; libre: todos los requeridos);</item>
/// <item>marca «no iniciada» si nadie salió dentro del margen y «sin terminar» si el bastón no volvió a tiempo.</item>
/// </list>
/// Cada resultado dispara el evento "patrol-event" del motor de automatizaciones (Centro de eventos, correo, parlantes...).
/// Trabaja por sondeo de la base de datos: no depende de que el latido o la descarga lo llamen.
/// </summary>
public sealed class PatrolRoundService(
    IServiceScopeFactory scopeFactory,
    PatrolService patrol,
    AuditService audit,
    LicenseService license,
    ILogger<PatrolRoundService> logger) : SupervisedBackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Horizon = TimeSpan.FromHours(24);
    private DateTime _lastMaterialize = DateTime.MinValue;
    private readonly HashSet<int> _offlineAgents = [];

    protected override async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (license.IsModuleEnabled(LicenseFeatures.ModulePatrol))
                {
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
                    var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();
                    var now = DateTime.UtcNow;
                    if (now - _lastMaterialize > TimeSpan.FromMinutes(1))
                    {
                        await MaterializeAsync(db, now, ct);
                        _lastMaterialize = now;
                    }
                    await ClaimStartsAsync(db, engine, now, ct);
                    await EvaluateReturnsAsync(db, engine, now, ct);
                    await ExpireAsync(db, engine, now, ct);
                    await WatchAgentsAsync(db, engine, now, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Verificación de rondas");
            }
            await Task.Delay(Tick, ct);
        }
    }

    // ------------------------------------------------------------------
    // Programación → rondas esperadas

    public async Task MaterializeAsync(VmsDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var routes = await db.PatrolRoutes.AsNoTracking().Include(r => r.Schedules).Include(r => r.Stops)
            .Where(r => r.Enabled).ToListAsync(ct);
        if (routes.Count == 0) return;
        var holidays = await db.AccessHolidays.AsNoTracking().ToListAsync(ct);
        var todayLocal = DateOnly.FromDateTime(PatrolService.ToLocal(nowUtc));
        int created = 0;
        foreach (var route in routes)
        {
            foreach (var schedule in route.Schedules.Where(s => s.Enabled))
            {
                for (int d = -1; d <= 1; d++)
                {
                    var day = todayLocal.AddDays(d);
                    if ((schedule.DaysMask & (1 << (int)day.DayOfWeek)) == 0) continue;
                    if (schedule.SkipHolidays && IsHoliday(holidays, day)) continue;
                    var startLocal = day.ToDateTime(TimeOnly.MinValue).AddMinutes(schedule.StartMinutes);
                    var start = PatrolService.ToUtc(startLocal);
                    // Solo lo que viene (y lo de hace poco: el margen todavía puede estar corriendo), nunca antes de crear la ruta.
                    if (start > nowUtc + Horizon || start < route.CreatedAt) continue;
                    var deadline = start.AddMinutes(route.StartWindowMinutes);
                    if (deadline < nowUtc.AddHours(-1)) continue;
                    if (await db.PatrolRuns.AnyAsync(r => r.ScheduleId == schedule.Id && r.ExpectedStart == start, ct)) continue;
                    var run = new PatrolRun
                    {
                        RouteId = route.Id, ScheduleId = schedule.Id, ExpectedStart = start, StartDeadline = deadline,
                        DueEnd = deadline.AddMinutes(route.MaxDurationMinutes),
                        Stops = route.Stops.OrderBy(s => s.Sequence).Select(s => new PatrolRunStop
                        {
                            Sequence = s.Sequence, CheckpointId = s.CheckpointId, Required = s.Required,
                            ExpectedAt = route.OrderMode == PatrolOrderMode.Strict && s.OffsetMinutes is { } off ? start.AddMinutes(off) : null,
                            ToleranceMinutes = route.OrderMode == PatrolOrderMode.Strict ? s.ToleranceMinutes : null,
                        }).ToList(),
                    };
                    db.PatrolRuns.Add(run);
                    created++;
                }
            }
        }
        if (created > 0)
        {
            await db.SaveChangesAsync(ct);
            await patrol.NotifyAsync("runs", ct);
        }
    }

    private static bool IsHoliday(List<AccessHoliday> holidays, DateOnly day) => holidays.Any(h =>
        h.Yearly
            ? Between(new DateOnly(day.Year, h.StartDate.Month, h.StartDate.Day), day, h)
            : day >= h.StartDate && day <= h.EndDate);

    private static bool Between(DateOnly start, DateOnly day, AccessHoliday h)
    {
        var end = start.AddDays(h.EndDate.DayNumber - h.StartDate.DayNumber);
        return day >= start && day <= end;
    }

    // ------------------------------------------------------------------
    // Salida del bastón = inicio de la ronda

    private async Task ClaimStartsAsync(VmsDbContext db, WorkflowEngine engine, DateTime now, CancellationToken ct)
    {
        // Bastones que salieron de su base hace poco y todavía no se usaron para ninguna ronda.
        var undocked = await db.PatrolReaders.Where(r => !r.Docked && r.DockedChangedAt != null && r.AgentId != null
                && r.DockedChangedAt > now.AddHours(-2))
            .Select(r => new { r.Id, r.Name, AgentId = r.AgentId!.Value, At = r.DockedChangedAt!.Value }).ToListAsync(ct);
        foreach (var reader in undocked)
        {
            if (await db.PatrolRuns.AnyAsync(r => r.ReaderId == reader.Id && r.ActualStart == reader.At, ct)) continue;
            var routeIds = await db.PatrolRouteAgents.Where(a => a.AgentId == reader.AgentId).Select(a => a.RouteId).ToListAsync(ct);
            if (routeIds.Count == 0) continue;
            // Se acepta salir desde que empieza el aviso previo hasta el fin del margen.
            var candidates = await db.PatrolRuns.Where(r => routeIds.Contains(r.RouteId) && r.Status == PatrolRunStatus.Scheduled
                    && r.StartDeadline >= reader.At)
                .Join(db.PatrolRoutes, r => r.RouteId, rt => rt.Id, (r, rt) => new { Run = r, Route = rt })
                .Where(x => x.Run.ExpectedStart.AddMinutes(-x.Route.ReminderMinutes) <= reader.At)
                .OrderBy(x => x.Run.ExpectedStart).FirstOrDefaultAsync(ct);
            if (candidates is null) continue;
            var run = candidates.Run;
            run.Status = PatrolRunStatus.InProgress;
            run.ActualStart = reader.At;
            run.ReaderId = reader.Id;
            run.DueEnd = reader.At.AddMinutes(candidates.Route.MaxDurationMinutes);
            await db.SaveChangesAsync(ct);
            engine.Publish(WorkflowTrigger.FromPatrolEvent(PatrolEventKinds.RoundStarted, run.RouteId, candidates.Route.Name, reader.At,
                $"{reader.Name} salió de la base a las {PatrolService.ToLocal(reader.At):HH:mm}.", expectedStartUtc: run.ExpectedStart));
            await patrol.NotifyAsync("runs", ct);
        }
    }

    // ------------------------------------------------------------------
    // Vuelta del bastón (descargado) = evaluar

    private async Task EvaluateReturnsAsync(VmsDbContext db, WorkflowEngine engine, DateTime now, CancellationToken ct)
    {
        var runs = await db.PatrolRuns.Where(r => (r.Status == PatrolRunStatus.InProgress || r.Status == PatrolRunStatus.Unfinished)
                && r.ReaderId != null)
            .Join(db.PatrolReaders, r => r.ReaderId, rd => rd.Id, (r, rd) => new { Run = r, Reader = rd })
            .Where(x => x.Reader.Docked && x.Reader.LastDownloadAt != null && x.Reader.LastDownloadAt > x.Run.ActualStart)
            .Select(x => x.Run.Id).ToListAsync(ct);
        foreach (var id in runs)
        {
            var reader = await db.PatrolRuns.Where(r => r.Id == id)
                .Join(db.PatrolReaders, r => r.ReaderId, rd => rd.Id, (r, rd) => rd).FirstAsync(ct);
            await EvaluateAsync(db, engine, id, reader.DockedChangedAt ?? now, ct);
        }
    }

    /// <summary>Evalúa una ronda contra los pasos de la bitácora (de cualquier bastón: cualquiera puede hacerla).</summary>
    public async Task EvaluateAsync(VmsDbContext db, WorkflowEngine? engine, long runId, DateTime endUtc, CancellationToken ct)
    {
        var run = await db.PatrolRuns.Include(r => r.Stops).FirstAsync(r => r.Id == runId, ct);
        var route = await db.PatrolRoutes.AsNoTracking().FirstAsync(r => r.Id == run.RouteId, ct);
        var from = run.ActualStart ?? run.ExpectedStart.AddMinutes(-route.ReminderMinutes);
        var to = run.ActualEnd ?? endUtc;
        var checkpointIds = run.Stops.Select(s => s.CheckpointId).Distinct().ToList();
        // Un paso cumple una sola ronda: se excluyen los que ya usó otra.
        var used = await db.PatrolRunStops.Where(s => s.RunId != run.Id && s.PassId != null).Select(s => s.PassId!.Value).ToListAsync(ct);
        var passes = await db.PatrolPasses.AsNoTracking()
            .Where(p => !p.Voided && checkpointIds.Contains(p.CheckpointId) && p.ReadAt >= from && p.ReadAt <= to && !used.Contains(p.Id))
            .OrderBy(p => p.ReadAt).ToListAsync(ct);

        foreach (var s in run.Stops) { s.PassId = null; s.ActualAt = null; s.Status = PatrolStopStatus.Pending; }
        var taken = new HashSet<long>();
        var stops = run.Stops.OrderBy(s => s.Sequence).ToList();
        if (route.OrderMode == PatrolOrderMode.Strict)
        {
            // Como en Ronda: el tiempo de cada punto se cuenta desde el primer punto de la ruta.
            var anchor = passes.FirstOrDefault(p => p.CheckpointId == stops.FirstOrDefault()?.CheckpointId)?.ReadAt ?? from;
            var last = DateTime.MinValue;
            foreach (var stop in stops)
            {
                var pass = passes.FirstOrDefault(p => p.CheckpointId == stop.CheckpointId && !taken.Contains(p.Id) && p.ReadAt >= last)
                           ?? passes.FirstOrDefault(p => p.CheckpointId == stop.CheckpointId && !taken.Contains(p.Id));
                if (pass is null) { stop.Status = stop.Required ? PatrolStopStatus.Missed : PatrolStopStatus.SkippedOptional; continue; }
                taken.Add(pass.Id);
                stop.PassId = pass.Id;
                stop.ActualAt = pass.ReadAt;
                var expectedOffset = route.OrderMode == PatrolOrderMode.Strict
                    ? (await db.PatrolRouteStops.AsNoTracking().Where(r => r.RouteId == route.Id && r.Sequence == stop.Sequence)
                        .Select(r => r.OffsetMinutes).FirstOrDefaultAsync(ct)) : null;
                stop.ExpectedAt = expectedOffset is { } off ? anchor.AddMinutes(off) : stop.ExpectedAt;
                if (pass.ReadAt < last) stop.Status = PatrolStopStatus.OutOfOrder;
                else if (stop.ExpectedAt is { } exp && stop.ToleranceMinutes is { } tol)
                {
                    var diff = (pass.ReadAt - exp).TotalMinutes;
                    stop.Status = diff < -tol ? PatrolStopStatus.Early : diff > tol ? PatrolStopStatus.Late : PatrolStopStatus.OnTime;
                }
                else stop.Status = PatrolStopStatus.OnTime;
                if (pass.ReadAt > last) last = pass.ReadAt;
            }
        }
        else
        {
            foreach (var stop in stops)
            {
                var pass = passes.FirstOrDefault(p => p.CheckpointId == stop.CheckpointId && !taken.Contains(p.Id));
                if (pass is null) { stop.Status = stop.Required ? PatrolStopStatus.Missed : PatrolStopStatus.SkippedOptional; continue; }
                taken.Add(pass.Id);
                stop.PassId = pass.Id;
                stop.ActualAt = pass.ReadAt;
                stop.Status = PatrolStopStatus.Done;
            }
        }

        bool ok(PatrolRunStop s) => s.Status is PatrolStopStatus.OnTime or PatrolStopStatus.Done or PatrolStopStatus.SkippedOptional
                                    || (!s.Required && s.Status != PatrolStopStatus.Pending);
        var failing = stops.Where(s => s.Required && !ok(s)).ToList();
        var previous = run.Status;
        run.ActualEnd ??= endUtc;
        run.EvaluatedAt = DateTime.UtcNow;
        run.Status = failing.Count == 0 ? PatrolRunStatus.Completed : PatrolRunStatus.Partial;
        if (previous == PatrolRunStatus.Unfinished && run.Status == PatrolRunStatus.Completed)
            run.Cause = "Volvió tarde a la base, pero con todos los puntos.";
        else
            run.Cause = failing.Count == 0 ? null : await DescribeAsync(db, failing, ct);
        await db.SaveChangesAsync(ct);

        var guards = string.Join(", ", passes.Where(p => taken.Contains(p.Id) && p.GuardName != null).Select(p => p.GuardName).Distinct());
        string detail = run.Status == PatrolRunStatus.Completed
            ? $"Todos los puntos requeridos ({stops.Count(s => s.Required)})."
            : run.Cause!;
        engine?.Publish(WorkflowTrigger.FromPatrolEvent(
            run.Status == PatrolRunStatus.Completed ? PatrolEventKinds.RoundCompleted : PatrolEventKinds.RoundPartial,
            route.Id, route.Name, run.ActualEnd.Value, detail, expectedStartUtc: run.ExpectedStart, guards: guards));
        if (run.Status == PatrolRunStatus.Partial)
            await audit.LogSystemAsync("patrol", "run-partial", "patrol-run", run.Id.ToString(), route.Name,
                $"Ronda '{route.Name}' de las {PatrolService.ToLocal(run.ExpectedStart):dd-MM-yyyy HH:mm} incompleta: {run.Cause}", success: false);
        await patrol.NotifyAsync("runs", ct);
    }

    private static async Task<string> DescribeAsync(VmsDbContext db, List<PatrolRunStop> failing, CancellationToken ct)
    {
        var ids = failing.Select(s => s.CheckpointId).ToList();
        var names = await db.PatrolCheckpoints.Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        string Label(PatrolStopStatus s) => s switch
        {
            PatrolStopStatus.Missed => "no marcado",
            PatrolStopStatus.Late => "tarde",
            PatrolStopStatus.Early => "antes de tiempo",
            PatrolStopStatus.OutOfOrder => "fuera de orden",
            _ => s.ToString(),
        };
        return string.Join("; ", failing.GroupBy(s => s.Status)
            .Select(g => $"{Label(g.Key)}: {string.Join(", ", g.Select(s => names.GetValueOrDefault(s.CheckpointId, "?")))}"));
    }

    // ------------------------------------------------------------------
    // Plazos vencidos

    private async Task ExpireAsync(VmsDbContext db, WorkflowEngine engine, DateTime now, CancellationToken ct)
    {
        var notStarted = await db.PatrolRuns.Where(r => r.Status == PatrolRunStatus.Scheduled && r.StartDeadline < now).ToListAsync(ct);
        foreach (var run in notStarted)
        {
            var route = await db.PatrolRoutes.AsNoTracking().FirstAsync(r => r.Id == run.RouteId, ct);
            var agentIds = await db.PatrolRouteAgents.Where(a => a.RouteId == run.RouteId).Select(a => a.AgentId).ToListAsync(ct);
            var agents = await db.PatrolAgents.AsNoTracking().Where(a => agentIds.Contains(a.Id)).ToListAsync(ct);
            bool allOffline = agents.Count > 0 && agents.All(a => a.LastSeenAt is null
                || now - a.LastSeenAt > TimeSpan.FromSeconds(PatrolAgentProtocol.OfflineAfterSeconds));
            run.Status = PatrolRunStatus.NotStarted;
            run.Cause = agents.Count == 0 ? "La ruta no tiene una base asignada: no hay forma de saber si salió."
                : allOffline ? "La base de la guardia estaba sin conexión: no se pudo verificar la salida."
                : "Ningún bastón salió de la base dentro del margen.";
            run.EvaluatedAt = now;
            await db.SaveChangesAsync(ct);
            engine.Publish(WorkflowTrigger.FromPatrolEvent(PatrolEventKinds.RoundNotStarted, route.Id, route.Name, now, run.Cause,
                expectedStartUtc: run.ExpectedStart));
            await audit.LogSystemAsync("patrol", "run-not-started", "patrol-run", run.Id.ToString(), route.Name,
                $"Ronda '{route.Name}' de las {PatrolService.ToLocal(run.ExpectedStart):dd-MM-yyyy HH:mm} no iniciada: {run.Cause}", success: false);
            await patrol.NotifyAsync("runs", ct);
        }

        var unfinished = await db.PatrolRuns.Where(r => r.Status == PatrolRunStatus.InProgress && r.DueEnd < now).ToListAsync(ct);
        foreach (var run in unfinished)
        {
            var route = await db.PatrolRoutes.AsNoTracking().FirstAsync(r => r.Id == run.RouteId, ct);
            run.Status = PatrolRunStatus.Unfinished;
            run.Cause = $"El bastón no volvió a la base dentro de {route.MaxDurationMinutes} min.";
            await db.SaveChangesAsync(ct);
            engine.Publish(WorkflowTrigger.FromPatrolEvent(PatrolEventKinds.RoundUnfinished, route.Id, route.Name, now, run.Cause,
                expectedStartUtc: run.ExpectedStart));
            await audit.LogSystemAsync("patrol", "run-unfinished", "patrol-run", run.Id.ToString(), route.Name,
                $"Ronda '{route.Name}' de las {PatrolService.ToLocal(run.ExpectedStart):dd-MM-yyyy HH:mm} sin terminar: {run.Cause}", success: false);
            await patrol.NotifyAsync("runs", ct);
        }
    }

    private async Task WatchAgentsAsync(VmsDbContext db, WorkflowEngine engine, DateTime now, CancellationToken ct)
    {
        var agents = await db.PatrolAgents.AsNoTracking().Where(a => a.TokenHash != null).ToListAsync(ct);
        foreach (var a in agents)
        {
            bool offline = a.LastSeenAt is null || now - a.LastSeenAt > TimeSpan.FromSeconds(PatrolAgentProtocol.OfflineAfterSeconds);
            if (offline && _offlineAgents.Add(a.Id) && a.LastSeenAt is not null)
            {
                engine.Publish(WorkflowTrigger.FromPatrolEvent(PatrolEventKinds.AgentOffline, null, null, now,
                    $"La base '{a.Name}' ({a.MachineName}) dejó de responder."));
                await audit.LogSystemAsync("patrol", "agent-offline", "patrol-agent", a.Id.ToString(), a.Name,
                    $"La base '{a.Name}' dejó de responder.", success: false);
                await patrol.NotifyAsync("agents", ct);
            }
            else if (!offline) _offlineAgents.Remove(a.Id);
        }
    }

    // ------------------------------------------------------------------
    // Recordatorios para el PC de cada base (van en la respuesta del latido)

    public async Task<IReadOnlyList<PatrolAgentReminder>> RemindersAsync(VmsDbContext db, int agentId, DateTime now, CancellationToken ct)
    {
        var routeIds = await db.PatrolRouteAgents.Where(a => a.AgentId == agentId).Select(a => a.RouteId).ToListAsync(ct);
        if (routeIds.Count == 0) return [];
        var rows = await db.PatrolRuns.AsNoTracking()
            .Where(r => routeIds.Contains(r.RouteId)
                && (r.Status == PatrolRunStatus.Scheduled || (r.Status == PatrolRunStatus.NotStarted && r.StartDeadline > now.AddMinutes(-30))))
            .Join(db.PatrolRoutes.AsNoTracking(), r => r.RouteId, rt => rt.Id, (r, rt) => new { Run = r, Route = rt })
            .Where(x => x.Run.ExpectedStart.AddMinutes(-x.Route.ReminderMinutes) <= now)
            .OrderBy(x => x.Run.ExpectedStart).Take(5).ToListAsync(ct);
        return rows.Select(x => new PatrolAgentReminder(x.Run.Id, x.Route.Name,
            PatrolService.ToLocal(x.Run.ExpectedStart), PatrolService.ToLocal(x.Run.StartDeadline),
            x.Run.Status == PatrolRunStatus.NotStarted ? "late" : now < x.Run.ExpectedStart ? "upcoming" : "due",
            Math.Max(1, x.Route.RepeatMinutes))).ToList();
    }
}
