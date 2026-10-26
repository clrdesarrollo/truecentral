using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Licensing;
using TrueCentralVms.Server.Services.Patrol;
using TrueCentralVms.Server.Services.Workflows;

namespace TrueCentralVms.Server.Api;

/// <summary>Rutas, programación y rondas (fase 2 del control de rondas).</summary>
public static class PatrolRoutesApi
{
    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    public static void MapPatrolRoutesApi(this WebApplication app)
    {
        app.MapGet("/api/patrol/routes", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolView, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var routes = await LoadRoutesAsync(db, null, ct);
            return Results.Ok(routes.Where(r => scope.CanView(r.LocationId)).Select(ToDto));
        });

        app.MapPost("/api/patrol/routes", async (HttpContext ctx, PatrolRouteWriteDto request, VmsDbContext db,
            AuditService audit, PatrolService patrol, PatrolRoundService rounds, LicenseService license,
            WorkflowEngine engine, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out var session) is { } failure) return failure;
            var route = new PatrolRoute();
            if (await ApplyAsync(db, route, request, ct) is { } invalid) return invalid;
            db.PatrolRoutes.Add(route);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "patrol", "route-created", "patrol-route", route.Id.ToString(), route.Name,
                $"Agregó la ruta '{route.Name}' ({Describe(route)}).");
            await EnsureDefaultWorkflowAsync(ctx, db, license, audit, engine, session.Username, ct);
            await rounds.MaterializeAsync(db, DateTime.UtcNow, ct);
            await patrol.NotifyAsync("routes", ct);
            return Results.Ok(ToDto((await LoadRoutesAsync(db, route.Id, ct))[0]));
        });

        app.MapPut("/api/patrol/routes/{id:int}", async (HttpContext ctx, int id, PatrolRouteWriteDto request, VmsDbContext db,
            AuditService audit, PatrolService patrol, PatrolRoundService rounds, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out _) is { } failure) return failure;
            var route = (await LoadRoutesAsync(db, id, ct, tracking: true)).FirstOrDefault();
            if (route is null) return Results.NotFound();
            string before = Describe(route);
            if (await ApplyAsync(db, route, request, ct) is { } invalid) return invalid;
            route.UpdatedAt = DateTime.UtcNow;
            // Las rondas que aún no empiezan se rehacen con la ruta nueva; las pasadas quedan como fueron.
            var future = await db.PatrolRuns.Where(r => r.RouteId == id && r.Status == PatrolRunStatus.Scheduled).ToListAsync(ct);
            db.PatrolRuns.RemoveRange(future);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "patrol", "route-updated", "patrol-route", route.Id.ToString(), route.Name,
                $"Modificó la ruta '{route.Name}': antes {before}; ahora {Describe(route)}.");
            await rounds.MaterializeAsync(db, DateTime.UtcNow, ct);
            await patrol.NotifyAsync("routes", ct);
            return Results.Ok(ToDto((await LoadRoutesAsync(db, id, ct))[0]));
        });

        app.MapDelete("/api/patrol/routes/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out _) is { } failure) return failure;
            var route = await db.PatrolRoutes.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (route is null) return Results.NotFound();
            if (await db.PatrolRuns.AnyAsync(r => r.RouteId == id && r.Status != PatrolRunStatus.Scheduled, ct))
                return Error("La ruta tiene rondas registradas: deshabilítela para conservar su historial.", StatusCodes.Status409Conflict);
            db.PatrolRoutes.Remove(route);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "patrol", "route-deleted", "patrol-route", id.ToString(), route.Name, $"Eliminó la ruta '{route.Name}'.");
            await patrol.NotifyAsync("routes", ct);
            return Results.Ok();
        });

        // Rondas del periodo (tablero de cumplimiento).
        app.MapGet("/api/patrol/runs", async (HttpContext ctx, DateTime? from, DateTime? to, int? routeId, string? status,
            int? page, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolView, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var start = from is null ? DateTime.UtcNow.AddHours(-24) : PatrolService.ToUtc(from.Value);
            var end = to is null ? DateTime.UtcNow.AddHours(24) : PatrolService.ToUtc(to.Value);
            var (rows, total) = await QueryRunsAsync(db, scope, start, end, routeId, status, Math.Max(page ?? 1, 1), 100, false, ct);
            return Results.Ok(new PatrolRunPageDto(rows, total));
        });

        app.MapGet("/api/patrol/runs/{id:long}", async (HttpContext ctx, long id, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolView, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var run = await db.PatrolRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            if (run is null) return Results.NotFound();
            var (rows, _) = await QueryRunsAsync(db, scope, run.ExpectedStart, run.ExpectedStart.AddSeconds(1), run.RouteId, null, 1, 10, true, ct,
                onlyId: id);
            return rows.Count == 0 ? Results.NotFound() : Results.Ok(rows[0]);
        });

        app.MapPost("/api/patrol/runs/{id:long}/cancel", async (HttpContext ctx, long id, PatrolRunCancelDto request, VmsDbContext db,
            AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolEdit, out var session) is { } failure) return failure;
            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 400)
                return Error("Indique el motivo de la anulación (máximo 400 caracteres).");
            var run = await db.PatrolRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (run is null) return Results.NotFound();
            var route = await db.PatrolRoutes.AsNoTracking().FirstAsync(r => r.Id == run.RouteId, ct);
            var previous = run.Status;
            run.Status = PatrolRunStatus.Cancelled;
            run.Cause = $"Anulada: {request.Reason.Trim()}";
            run.CancelledBy = session.Username;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "patrol", "run-cancelled", "patrol-run", id.ToString(), route.Name,
                $"Anuló la ronda '{route.Name}' de las {PatrolService.ToLocal(run.ExpectedStart):dd-MM-yyyy HH:mm} (estaba {previous}). Motivo: {request.Reason.Trim()}");
            await patrol.NotifyAsync("runs", ct);
            return Results.Ok();
        });

        // Volver a evaluar (p. ej. tras corregir pasos de la bitácora).
        app.MapPost("/api/patrol/runs/{id:long}/reevaluate", async (HttpContext ctx, long id, VmsDbContext db,
            PatrolRoundService rounds, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolEdit, out _) is { } failure) return failure;
            var run = await db.PatrolRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            if (run is null) return Results.NotFound();
            if (run.Status is PatrolRunStatus.Scheduled or PatrolRunStatus.Cancelled)
                return Error("Solo se evalúan rondas que ya pasaron.");
            await rounds.EvaluateAsync(db, null, id, run.ActualEnd ?? run.DueEnd, ct);
            await audit.LogAsync(ctx, "patrol", "run-reevaluated", "patrol-run", id.ToString(), null, $"Volvió a evaluar la ronda {id}.");
            return Results.Ok();
        });
    }

    // ----------------------------------------------------------------------

    internal static async Task<(List<PatrolRunDto> Rows, int Total)> QueryRunsAsync(VmsDbContext db, UserScope scope,
        DateTime startUtc, DateTime endUtc, int? routeId, string? status, int page, int pageSize, bool withStops,
        CancellationToken ct, long? onlyId = null)
    {
        var routes = await db.PatrolRoutes.AsNoTracking().ToListAsync(ct);
        var visible = routes.Where(r => scope.CanView(r.LocationId)).ToDictionary(r => r.Id);
        var ids = visible.Keys.ToList();
        var q = db.PatrolRuns.AsNoTracking().Include(r => r.Stops).Where(r => ids.Contains(r.RouteId));
        q = onlyId is { } one ? q.Where(r => r.Id == one) : q.Where(r => r.ExpectedStart >= startUtc && r.ExpectedStart < endUtc);
        if (routeId is not null) q = q.Where(r => r.RouteId == routeId);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<PatrolRunStatus>(status, true, out var st)) q = q.Where(r => r.Status == st);
        int total = await q.CountAsync(ct);
        var runs = await q.OrderByDescending(r => r.ExpectedStart).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var passIds = runs.SelectMany(r => r.Stops).Where(s => s.PassId != null).Select(s => s.PassId!.Value).ToList();
        var passes = await db.PatrolPasses.AsNoTracking().Where(p => passIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var checkpoints = await db.PatrolCheckpoints.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var readers = await db.PatrolReaders.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var locations = await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);

        return (runs.Select(r =>
        {
            var route = visible[r.RouteId];
            var guards = r.Stops.Where(s => s.PassId is { } p && passes.ContainsKey(p) && passes[p].GuardName != null)
                .Select(s => passes[s.PassId!.Value].GuardName!).Distinct().ToList();
            var stops = withStops
                ? r.Stops.OrderBy(s => s.Sequence).Select(s => new PatrolRunStopDto(s.Sequence, s.CheckpointId,
                    checkpoints.GetValueOrDefault(s.CheckpointId, "?"), s.Required, s.ExpectedAt, s.ToleranceMinutes, s.ActualAt,
                    s.PassId is { } p && passes.TryGetValue(p, out var pass) ? pass.GuardName : null, s.Status)).ToList()
                : null;
            return new PatrolRunDto(r.Id, r.RouteId, route.Name, route.LocationId is { } l ? locations.GetValueOrDefault(l) : null,
                r.ExpectedStart, r.StartDeadline, r.DueEnd, r.ActualStart, r.ActualEnd, r.Status, r.Cause,
                r.Stops.Count(s => s.Required),
                r.Stops.Count(s => s.Required && s.Status is PatrolStopStatus.OnTime or PatrolStopStatus.Done),
                guards, r.ReaderId is { } rd ? readers.GetValueOrDefault(rd) : null, stops);
        }).ToList(), total);
    }

    private static async Task<List<PatrolRoute>> LoadRoutesAsync(VmsDbContext db, int? id, CancellationToken ct, bool tracking = false)
    {
        var q = db.PatrolRoutes.Include(r => r.Stops).ThenInclude(s => s.Checkpoint).Include(r => r.Agents).Include(r => r.Schedules)
            .AsQueryable();
        if (!tracking) q = q.AsNoTracking();
        if (id is not null) q = q.Where(r => r.Id == id);
        return await q.OrderBy(r => r.Name).ToListAsync(ct);
    }

    private static async Task<IResult?> ApplyAsync(VmsDbContext db, PatrolRoute route, PatrolRouteWriteDto request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
            return Error("El nombre es obligatorio (máximo 128 caracteres).");
        if (request.ReminderMinutes is < 0 or > 240 || request.StartWindowMinutes is < 1 or > 240
            || request.RepeatMinutes is < 1 or > 60 || request.MaxDurationMinutes is < 1 or > 1440)
            return Error("Revise los tiempos: aviso previo 0-240 min, margen para salir 1-240, repetición 1-60 y duración 1-1440.");
        if (request.Stops is not { Count: > 0 }) return Error("La ruta necesita al menos un punto.");
        if (request.Stops.Count > 200) return Error("Máximo 200 puntos por ruta.");
        var checkpointIds = request.Stops.Select(s => s.CheckpointId).Distinct().ToList();
        if (await db.PatrolCheckpoints.CountAsync(c => checkpointIds.Contains(c.Id), ct) != checkpointIds.Count)
            return Error("Algún punto de la ruta no existe.", StatusCodes.Status404NotFound);
        if (request.OrderMode == PatrolOrderMode.Strict
            && request.Stops.Any(s => s.OffsetMinutes is < 0 or > 1440 || s.ToleranceMinutes is < 0 or > 240))
            return Error("En modo estricto, el tiempo de cada punto va de 0 a 1440 min y su margen de 0 a 240.");
        var agentIds = (request.AgentIds ?? []).Distinct().ToList();
        if (await db.PatrolAgents.CountAsync(a => agentIds.Contains(a.Id), ct) != agentIds.Count)
            return Error("Alguna base no existe.", StatusCodes.Status404NotFound);
        var schedules = new List<PatrolSchedule>();
        foreach (var s in request.Schedules ?? [])
        {
            if (!TimeOnly.TryParseExact(s.StartTime, "HH:mm", out var t)) return Error($"Hora inválida: '{s.StartTime}' (use HH:mm).");
            if ((s.DaysMask & 0x7F) == 0) return Error("Cada horario necesita al menos un día.");
            schedules.Add(new PatrolSchedule { DaysMask = s.DaysMask & 0x7F, StartMinutes = t.Hour * 60 + t.Minute, SkipHolidays = s.SkipHolidays, Enabled = s.Enabled });
        }
        var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, route.LocationId, ct);
        if (locationError is not null) return Error(locationError, StatusCodes.Status404NotFound);

        route.Name = request.Name.Trim();
        route.LocationId = locationId;
        route.Enabled = request.Enabled;
        route.OrderMode = request.OrderMode;
        route.ReminderMinutes = request.ReminderMinutes;
        route.StartWindowMinutes = request.StartWindowMinutes;
        route.RepeatMinutes = request.RepeatMinutes;
        route.MaxDurationMinutes = request.MaxDurationMinutes;
        route.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim()[..Math.Min(request.Notes.Trim().Length, 1024)];
        route.Stops.Clear();
        int seq = 0;
        foreach (var s in request.Stops)
            route.Stops.Add(new PatrolRouteStop
            {
                Sequence = seq++, CheckpointId = s.CheckpointId, Required = s.Required,
                OffsetMinutes = request.OrderMode == PatrolOrderMode.Strict ? s.OffsetMinutes ?? 0 : null,
                ToleranceMinutes = request.OrderMode == PatrolOrderMode.Strict ? s.ToleranceMinutes ?? 5 : null,
            });
        route.Agents.Clear();
        foreach (var a in agentIds) route.Agents.Add(new PatrolRouteAgent { AgentId = a });
        // Los horarios que no cambian conservan su Id (las rondas ya creadas apuntan a él).
        var keep = new List<PatrolSchedule>();
        foreach (var s in schedules)
        {
            var same = route.Schedules.FirstOrDefault(x => x.DaysMask == s.DaysMask && x.StartMinutes == s.StartMinutes && !keep.Contains(x));
            if (same is not null) { same.SkipHolidays = s.SkipHolidays; same.Enabled = s.Enabled; keep.Add(same); }
            else keep.Add(s);
        }
        foreach (var gone in route.Schedules.Where(x => !keep.Contains(x)).ToList()) route.Schedules.Remove(gone);
        foreach (var s in keep.Where(x => !route.Schedules.Contains(x))) route.Schedules.Add(s);
        return null;
    }

    /// <summary>
    /// Automatización por defecto «Rondas → Centro de eventos» (decisión del usuario: si la ronda no se inicia, alerta
    /// en el Centro de eventos). Se crea una vez, editable como cualquier otra; solo si la licencia lo permite.
    /// </summary>
    private static async Task EnsureDefaultWorkflowAsync(HttpContext ctx, VmsDbContext db, LicenseService license,
        AuditService audit, WorkflowEngine engine, string username, CancellationToken ct)
    {
        if (await db.Workflows.AnyAsync(w => w.TriggerType == WorkflowTriggerTypes.PatrolEvent, ct)) return;
        if (license.Deny(LicenseFeatures.ModuleAutomation, LicenseFeatures.AutomationRules,
                await db.Workflows.CountAsync(w => w.Enabled, ct)) is not null) return;
        var workflow = new Workflow
        {
            Name = "Rondas: alertas al Centro de eventos",
            Description = "Creada sola con la primera ruta. Avisa en el Centro de eventos cuando una ronda no se inicia, queda sin terminar o incompleta.",
            GroupName = "Control de rondas",
            TriggerType = WorkflowTriggerTypes.PatrolEvent,
            ConditionsJson = WorkflowJson.Serialize(new WorkflowConditionsDto(PatrolEventKinds:
                [PatrolEventKinds.RoundNotStarted, PatrolEventKinds.RoundUnfinished, PatrolEventKinds.RoundPartial])),
            CooldownSeconds = 0,
            CreatedBy = username,
            Actions =
            [
                new WorkflowAction
                {
                    Order = 1, Type = WorkflowActionTypes.Notify,
                    ConfigJson = WorkflowJson.Serialize(new Dictionary<string, object>
                    {
                        ["title"] = "{tipo}: {ruta}",
                        ["message"] = "Programada a las {horaprogramada} · {detalle}",
                        ["severity"] = "Critical",
                    }),
                },
            ],
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(ct);
        engine.Invalidate();
        await audit.LogAsync(ctx, "patrol", "default-workflow-created", "workflow", workflow.Id.ToString(), workflow.Name,
            "Creó la automatización «Rondas: alertas al Centro de eventos» (ronda no iniciada, sin terminar o incompleta).");
    }

    private static string Describe(PatrolRoute r) =>
        $"{r.Stops.Count} puntos, {(r.OrderMode == PatrolOrderMode.Strict ? "orden estricto" : "orden libre")}, " +
        $"{r.Schedules.Count} horario(s), {r.Agents.Count} base(s), aviso {r.ReminderMinutes} min, margen {r.StartWindowMinutes} min, " +
        $"duración {r.MaxDurationMinutes} min{(r.Enabled ? "" : ", deshabilitada")}";

    private static PatrolRouteDto ToDto(PatrolRoute r) => new(
        r.Id, r.Name, r.LocationId, null, r.Enabled, r.OrderMode, r.ReminderMinutes, r.StartWindowMinutes, r.RepeatMinutes,
        r.MaxDurationMinutes, r.Agents.Select(a => a.AgentId).ToList(),
        r.Stops.OrderBy(s => s.Sequence).Select(s => new PatrolRouteStopDto(s.Sequence, s.CheckpointId, s.Checkpoint?.Name, s.Required,
            s.OffsetMinutes, s.ToleranceMinutes)).ToList(),
        r.Schedules.OrderBy(s => s.StartMinutes).Select(s => new PatrolScheduleDto(s.Id, s.DaysMask,
            $"{s.StartMinutes / 60:D2}:{s.StartMinutes % 60:D2}", s.SkipHolidays, s.Enabled)).ToList(), r.Notes);
}
