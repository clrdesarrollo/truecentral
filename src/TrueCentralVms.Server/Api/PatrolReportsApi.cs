using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Patrol;
using TrueCentralVms.Server.Services.Reports;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Informes del control de rondas en PDF o Excel:
/// <c>GET /api/patrol/reports/{kind}?format=pdf|xlsx&amp;from=&amp;to=&amp;routeId=&amp;guardId=&amp;checkpointId=&amp;locationId=&amp;gapMinutes=</c>
/// con <c>kind</c> = summary, compliance, incidents, guards, guard-log o coverage
/// (ver <see cref="PatrolReports"/>). <c>from</c>/<c>to</c> van en hora local
/// (como el resto del módulo); por defecto, los últimos 7 días. Exige
/// <c>patrol.export</c>, respeta el alcance de ubicación del usuario y cada
/// generación queda en la auditoría (patrol / report-exported).
/// </summary>
public static class PatrolReportsApi
{
    private const int MaxRunsPdf = 3_000;
    private const int MaxRunsExcel = 50_000;
    private const int MaxRunsAggregate = 200_000;
    private const int MaxPasses = 300_000;

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    public static void MapPatrolReportsApi(this WebApplication app)
    {
        app.MapGet("/api/patrol/reports/{kind}", async (HttpContext ctx, string kind, string? format, DateTime? from, DateTime? to,
            int? routeId, int? guardId, int? checkpointId, int? locationId, int? gapMinutes,
            VmsDbContext db, AuditService audit, IWebHostEnvironment env, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolExport, out var session) is { } failure) return failure;
            kind = kind.Trim().ToLowerInvariant();
            var known = PatrolReports.Kinds.FirstOrDefault(k => k.Key == kind);
            if (known.Key is null)
                return Error("Informe desconocido: use summary, compliance, incidents, guards, guard-log o coverage.", StatusCodes.Status404NotFound);
            string fmt = (format ?? "pdf").Trim().ToLowerInvariant();
            if (fmt is not ("pdf" or "xlsx")) return Error("Formato desconocido: use pdf o xlsx.");
            bool pdf = fmt == "pdf";

            var nowLocal = PatrolService.ToLocal(DateTime.UtcNow);
            var fromLocal = from ?? nowLocal.Date.AddDays(-7);
            var toLocal = to ?? nowLocal;
            if (toLocal <= fromLocal) return Error("El fin del periodo debe ser posterior al inicio.");
            if ((toLocal - fromLocal).TotalDays > 367) return Error("El periodo puede abarcar como máximo un año.");
            int gap = gapMinutes ?? 60;
            if (gap is < 1 or > 10_080) return Error("El umbral sin cobertura va de 1 a 10 080 minutos (una semana).");
            if (kind == "guard-log" && guardId is null) return Error("Elija el guardia de la hoja de servicio.");

            var filter = new PatrolReports.Filter(PatrolService.ToUtc(fromLocal), PatrolService.ToUtc(toLocal),
                routeId, guardId, checkpointId, locationId, gap);
            var scope = await ctx.ScopeAsync(session);
            var data = await PatrolReports.LoadAsync(db, scope, filter, ct);

            // Lo pedido debe existir y estar al alcance del usuario.
            if (routeId is not null && data.Routes.Count == 0)
                return Error("La ruta no existe o está fuera de su alcance.", StatusCodes.Status404NotFound);
            if (checkpointId is not null && !data.Checkpoints.ContainsKey(checkpointId.Value))
                return Error("El punto no existe o está fuera de su alcance.", StatusCodes.Status404NotFound);
            if (guardId is not null && !data.Guards.ContainsKey(guardId.Value))
                return Error("El guardia no existe.", StatusCodes.Status404NotFound);
            if (locationId is not null && (!data.LocationPaths.ContainsKey(locationId.Value) || !scope.CanSeeLocation(locationId.Value)))
                return Error("La ubicación no existe o está fuera de su alcance.", StatusCodes.Status404NotFound);

            string filtersText = Describe(data, fromLocal, toLocal, kind == "coverage");
            string subtitle = $"{PatrolReports.Product} · Control de rondas · Periodo: {fromLocal:dd-MM-yyyy HH:mm} a {toLocal:dd-MM-yyyy HH:mm}";
            string? truncated = null;
            ReportMeta Meta() => new(session.Username, DateTime.Now, filtersText, truncated);

            ReportTable table;
            int records;
            switch (kind)
            {
                case "summary" or "guards":
                {
                    await PatrolReports.LoadRunsAsync(db, data, MaxRunsAggregate, ct);
                    truncated = Truncation(data.RunsTotal, MaxRunsAggregate);
                    records = data.Runs.Count;
                    if (kind == "summary") table = PatrolReports.Summary(data, Meta(), subtitle);
                    else
                    {
                        var counts = await PassCountsAsync(db, data, ct);
                        table = PatrolReports.GuardPerformance(data, counts, Meta(), subtitle);
                    }
                    break;
                }
                case "compliance" or "incidents":
                {
                    int limit = pdf ? MaxRunsPdf : MaxRunsExcel;
                    if (kind == "incidents")
                    {
                        // Las incidencias se cuentan sobre todas las rondas, pero solo se listan las fallidas.
                        await PatrolReports.LoadRunsAsync(db, data, MaxRunsAggregate, ct);
                        var failed = data.Runs.Where(r => r.Status is PatrolRunStatus.NotStarted or PatrolRunStatus.Partial
                            or PatrolRunStatus.Unfinished).ToList();
                        if (failed.Count > limit)
                        {
                            truncated = $"Se incluyen las primeras {limit:N0} de {failed.Count:N0} rondas con incidencia: acote el periodo para ver el resto.";
                            var keep = failed.Take(limit).Select(r => r.Id).ToHashSet();
                            data.Runs = data.Runs.Where(r => !failed.Contains(r) || keep.Contains(r.Id)).ToList();
                        }
                        else truncated = Truncation(data.RunsTotal, MaxRunsAggregate);
                    }
                    else
                    {
                        await PatrolReports.LoadRunsAsync(db, data, limit, ct);
                        truncated = Truncation(data.RunsTotal, limit);
                    }
                    records = data.Runs.Count;
                    table = PatrolReports.Compliance(data, Meta(), subtitle, pdf, kind == "incidents");
                    break;
                }
                case "guard-log":
                {
                    var checkpointIds = PatrolReports.CoverageCheckpoints(data).Select(c => c.Id).ToList();
                    var q = db.PatrolPasses.AsNoTracking().Where(p => p.PatrolGuardId == guardId && p.ReadAt >= filter.FromUtc
                        && p.ReadAt < filter.ToUtc && checkpointIds.Contains(p.CheckpointId));
                    int total = await q.CountAsync(ct);
                    var passes = await q.OrderBy(p => p.ReadAt).ThenBy(p => p.Id).Take(MaxPasses).ToListAsync(ct);
                    truncated = total > MaxPasses ? $"Se incluyen los primeros {MaxPasses:N0} de {total:N0} pasos: acote el periodo." : null;
                    var usage = await UsageAsync(db, passes.Select(p => p.Id).ToList(), ct);
                    records = passes.Count;
                    table = PatrolReports.GuardLog(data, guardId!.Value, passes, usage, Meta(), subtitle, pdf);
                    break;
                }
                default: // coverage
                {
                    var checkpoints = PatrolReports.CoverageCheckpoints(data);
                    var ids = checkpoints.Select(c => c.Id).ToList();
                    var q = db.PatrolPasses.AsNoTracking().Where(p => !p.Voided && p.ReadAt >= filter.FromUtc && p.ReadAt < filter.ToUtc
                        && ids.Contains(p.CheckpointId));
                    if (guardId is not null) q = q.Where(p => p.PatrolGuardId == guardId);
                    int total = await q.CountAsync(ct);
                    var passes = await q.OrderBy(p => p.ReadAt).Take(MaxPasses).ToListAsync(ct);
                    truncated = total > MaxPasses ? $"Se incluyen las primeras {MaxPasses:N0} de {total:N0} visitas: acote el periodo." : null;
                    records = passes.Count;
                    table = PatrolReports.Coverage(data, checkpoints, passes, Meta(), subtitle, pdf);
                    break;
                }
            }

            byte[] content;
            try
            {
                content = pdf ? PdfReportWriter.Write(table, Path.Combine(env.WebRootPath, "logo.png")) : XlsxWriter.Write(table);
            }
            catch (Exception ex)
            {
                await audit.LogAsync(ctx, "patrol", "report-exported", "patrol-report", kind, table.Title,
                    $"No se pudo generar el informe \"{table.Title}\" en {(pdf ? "PDF" : "Excel")}: {ex.Message}", success: false);
                return Error($"No se pudo generar el informe: {ex.Message}", StatusCodes.Status500InternalServerError);
            }

            await audit.LogAsync(ctx, "patrol", "report-exported", "patrol-report", kind, table.Title,
                $"Generó el informe \"{table.Title}\" en {(pdf ? "PDF" : "Excel")} ({records:N0} registro(s)): {filtersText}.",
                data: new { report = kind, format = fmt, records, rows = table.Rows.Count, from = fromLocal, to = toLocal,
                    routeId, guardId, checkpointId, locationId, gapMinutes = kind == "coverage" ? gap : (int?)null });

            string file = $"{table.FileName}_{DateTime.Now:yyyyMMdd_HHmmss}";
            return pdf
                ? Results.File(content, "application/pdf", file + ".pdf")
                : Results.File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file + ".xlsx");
        });
    }

    private static string? Truncation(int total, int limit) =>
        total > limit ? $"Se incluyen las primeras {limit:N0} de {total:N0} rondas: acote el periodo para ver el resto." : null;

    private static string Describe(PatrolReports.Data data, DateTime fromLocal, DateTime toLocal, bool coverage)
    {
        var f = data.Filter;
        var parts = new List<string> { $"periodo {fromLocal:dd-MM-yyyy HH:mm} a {toLocal:dd-MM-yyyy HH:mm}" };
        if (f.RouteId is { } r) parts.Add($"ruta {data.RouteName(r)}");
        if (f.LocationId is { } l) parts.Add($"ubicación {data.LocationPaths.GetValueOrDefault(l, l.ToString())}");
        if (f.GuardId is { } g) parts.Add($"guardia {(data.Guards.TryGetValue(g, out var guard) ? guard.Name : g.ToString())}");
        if (f.CheckpointId is { } c) parts.Add($"punto {data.CheckpointName(c)}");
        if (coverage) parts.Add($"umbral sin cobertura {f.GapMinutes} min");
        return string.Join("; ", parts);
    }

    /// <summary>Pasos válidos de cada guardia en el periodo (en los puntos visibles y filtrados).</summary>
    private static async Task<Dictionary<int, int>> PassCountsAsync(VmsDbContext db, PatrolReports.Data data, CancellationToken ct)
    {
        var f = data.Filter;
        var ids = PatrolReports.CoverageCheckpoints(data).Select(c => c.Id).ToList();
        return await db.PatrolPasses.AsNoTracking()
            .Where(p => !p.Voided && p.PatrolGuardId != null && p.ReadAt >= f.FromUtc && p.ReadAt < f.ToUtc && ids.Contains(p.CheckpointId))
            .GroupBy(p => p.PatrolGuardId!.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }

    /// <summary>Para cada paso, la ronda y la parada que cumplió (si alguna).</summary>
    private static async Task<Dictionary<long, (PatrolRun Run, PatrolRunStop Stop)>> UsageAsync(VmsDbContext db, List<long> passIds,
        CancellationToken ct)
    {
        var result = new Dictionary<long, (PatrolRun, PatrolRunStop)>();
        foreach (var chunk in passIds.Chunk(5_000))
        {
            var stops = await db.PatrolRunStops.AsNoTracking().Where(s => s.PassId != null && chunk.Contains(s.PassId.Value)).ToListAsync(ct);
            var runIds = stops.Select(s => s.RunId).Distinct().ToList();
            var runs = await db.PatrolRuns.AsNoTracking().Where(r => runIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
            foreach (var s in stops) result[s.PassId!.Value] = (runs[s.RunId], s);
        }
        return result;
    }
}
