using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Services.Reports;

namespace TrueCentralVms.Server.Services.Patrol;

/// <summary>
/// Informes del control de rondas (plan §6, «Informes»), armados con los
/// generadores comunes (<see cref="PdfReportWriter"/>, <see cref="XlsxWriter"/>):
/// <list type="bullet">
/// <item><b>summary</b>: resumen ejecutivo del periodo (una página, para el cliente).</item>
/// <item><b>compliance</b>: cada ronda con sus puntos (hora esperada, margen, hora real y estado).</item>
/// <item><b>incidents</b>: solo las rondas no iniciadas, incompletas o sin terminar, con su causa y los puntos fallidos.</item>
/// <item><b>guards</b>: desempeño por guardia (ranking del periodo).</item>
/// <item><b>guard-log</b>: hoja de servicio de un guardia (sus pasos, en orden, agrupados por turno).</item>
/// <item><b>coverage</b>: visitas por punto, intervalos, ventanas sin cobertura y mapa de calor por hora.</item>
/// </list>
/// Las rondas se toman por su hora programada dentro del periodo; los pasos, por su hora de lectura.
/// Todas las horas del informe van en la hora local del servidor (la del sitio).
/// </summary>
public static class PatrolReports
{
    public const string Product = "CLR TrueCentral VMS";

    public static readonly (string Key, string Title)[] Kinds =
    [
        ("summary", "Resumen ejecutivo de rondas"),
        ("compliance", "Cumplimiento de rutas"),
        ("incidents", "Incidencias de rondas"),
        ("guards", "Desempeño por guardia"),
        ("guard-log", "Hoja de servicio del guardia"),
        ("coverage", "Cobertura por punto"),
    ];

    /// <summary>Filtro del informe (periodo en UTC; el resto opcional).</summary>
    public sealed record Filter(DateTime FromUtc, DateTime ToUtc, int? RouteId, int? GuardId, int? CheckpointId, int? LocationId,
        int GapMinutes);

    /// <summary>Datos cargados para armar un informe, ya recortados al alcance del usuario y a los filtros.</summary>
    public sealed class Data
    {
        public required Filter Filter { get; init; }
        /// <summary>Rutas visibles que pasan los filtros de ruta y ubicación.</summary>
        public required Dictionary<int, PatrolRoute> Routes { get; init; }
        /// <summary>Puntos visibles (todos; los filtros se aplican en cada informe).</summary>
        public required Dictionary<int, PatrolCheckpoint> Checkpoints { get; init; }
        public required Dictionary<int, string> LocationPaths { get; init; }
        public required HashSet<int>? LocationSubtree { get; init; }
        public required Dictionary<int, string> Readers { get; init; }
        public required Dictionary<int, (string Name, string? EmployeeNo)> Guards { get; init; }
        public List<PatrolRun> Runs { get; set; } = [];
        public int RunsTotal { get; set; }
        /// <summary>Pasos que usan las paradas de <see cref="Runs"/>.</summary>
        public Dictionary<long, PatrolPass> RunPasses { get; set; } = [];

        public string CheckpointName(int id) => Checkpoints.TryGetValue(id, out var c) ? c.Name : $"Punto {id}";
        public string? CheckpointLocation(int id) =>
            Checkpoints.TryGetValue(id, out var c) && c.LocationId is { } l ? LocationPaths.GetValueOrDefault(l) : null;
        public string RouteName(int id) => Routes.TryGetValue(id, out var r) ? r.Name : $"Ruta {id}";
        public string? RouteLocation(int id) =>
            Routes.TryGetValue(id, out var r) && r.LocationId is { } l ? LocationPaths.GetValueOrDefault(l) : null;
        public bool InLocation(int? locationId) => LocationSubtree is null || (locationId is { } l && LocationSubtree.Contains(l));
    }

    // ------------------------------------------------------------------
    // Etiquetas

    public static string RunLabel(PatrolRunStatus s) => s switch
    {
        PatrolRunStatus.Scheduled => "Programada",
        PatrolRunStatus.InProgress => "En curso",
        PatrolRunStatus.AwaitingData => "Esperando descarga",
        PatrolRunStatus.Completed => "Cumplida",
        PatrolRunStatus.Partial => "Incompleta",
        PatrolRunStatus.NotStarted => "No iniciada",
        PatrolRunStatus.Unfinished => "Sin terminar",
        PatrolRunStatus.Cancelled => "Anulada",
        _ => s.ToString(),
    };

    public static string StopLabel(PatrolStopStatus s, PatrolRunStatus run) => s switch
    {
        PatrolStopStatus.OnTime => "A tiempo",
        PatrolStopStatus.Early => "Temprano",
        PatrolStopStatus.Late => "Tarde",
        PatrolStopStatus.OutOfOrder => "Fuera de orden",
        PatrolStopStatus.Done => "Marcado",
        PatrolStopStatus.Missed => "No marcado",
        PatrolStopStatus.SkippedOptional => "Opcional omitido",
        // Sin evaluar: si la ronda no empezó, nadie pasó; si no terminó, faltan los datos del bastón.
        _ => run switch
        {
            PatrolRunStatus.NotStarted => "No marcado",
            PatrolRunStatus.Unfinished => "Sin datos",
            PatrolRunStatus.Cancelled => "Anulada",
            _ => "Pendiente",
        },
    };

    /// <summary>Rondas con resultado definitivo: las que cuentan para el % de cumplimiento.</summary>
    public static bool IsFinal(PatrolRunStatus s) =>
        s is PatrolRunStatus.Completed or PatrolRunStatus.Partial or PatrolRunStatus.NotStarted or PatrolRunStatus.Unfinished;

    /// <summary>Mismo criterio que la evaluación (<see cref="PatrolRoundService.EvaluateAsync"/>).</summary>
    public static bool StopOk(PatrolRunStop s) =>
        s.Status is PatrolStopStatus.OnTime or PatrolStopStatus.Done or PatrolStopStatus.SkippedOptional
        || (!s.Required && s.Status != PatrolStopStatus.Pending);

    public static bool StopFailed(PatrolRunStop s) => s.Required && !StopOk(s);

    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-CL");
    private static string N(int n) => n.ToString("N0", Es);
    private static string P(double? p) => p is { } v ? (v * 100).ToString("0.0", Es) + " %" : "—";
    private static double? Ratio(int n, int d) => d == 0 ? null : n / (double)d;
    private static DateTime L(DateTime utc) => PatrolService.ToLocal(utc);
    private static TimeSpan? LT(DateTime? utc) => utc is { } u ? L(u).TimeOfDay : null;

    public static string DurationText(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} d {t.Hours} h {t.Minutes} min";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
        return $"{(int)t.TotalMinutes} min";
    }

    /// <summary>Hora en que empezó la ronda: la salida de la base o, si no se registró, su primer paso.</summary>
    public static DateTime? StartOf(PatrolRun r) => r.ActualStart ?? r.Stops.Where(s => s.ActualAt != null).Min(s => s.ActualAt);

    // ------------------------------------------------------------------
    // Carga

    public static async Task<Data> LoadAsync(VmsDbContext db, UserScope scope, Filter filter, CancellationToken ct)
    {
        var locations = await db.Locations.AsNoTracking().Select(l => new { l.Id, l.Name, l.ParentId }).ToListAsync(ct);
        var byId = locations.ToDictionary(l => l.Id);
        var paths = new Dictionary<int, string>();
        foreach (var l in locations)
        {
            var parts = new List<string>();
            for (var cur = l; cur is not null && parts.Count < 32; cur = cur.ParentId is { } p ? byId.GetValueOrDefault(p) : null)
                parts.Insert(0, cur.Name);
            paths[l.Id] = string.Join(" / ", parts);
        }
        HashSet<int>? subtree = null;
        if (filter.LocationId is { } root)
        {
            subtree = [root];
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var l in locations)
                    if (l.ParentId is { } p && subtree.Contains(p) && subtree.Add(l.Id)) grew = true;
            }
        }

        var routes = await db.PatrolRoutes.AsNoTracking().Include(r => r.Stops).ToListAsync(ct);
        var checkpoints = await db.PatrolCheckpoints.AsNoTracking().ToListAsync(ct);
        var guards = await db.PatrolGuards.AsNoTracking().Include(g => g.AccessPerson).ToListAsync(ct);
        return new Data
        {
            Filter = filter,
            Routes = routes.Where(r => scope.CanView(r.LocationId))
                .Where(r => filter.RouteId is null || r.Id == filter.RouteId)
                .Where(r => subtree is null || (r.LocationId is { } l && subtree.Contains(l)))
                .ToDictionary(r => r.Id),
            Checkpoints = checkpoints.Where(c => scope.CanView(c.LocationId)).ToDictionary(c => c.Id),
            LocationPaths = paths,
            LocationSubtree = subtree,
            Readers = await db.PatrolReaders.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name, ct),
            Guards = guards.ToDictionary(g => g.Id, g => (g.AccessPerson?.FullName ?? $"Guardia {g.Id}", g.AccessPerson?.EmployeeNo)),
        };
    }

    /// <summary>Rondas del periodo (por hora programada) de las rutas visibles, con sus paradas y los pasos que las cumplen.</summary>
    public static async Task LoadRunsAsync(VmsDbContext db, Data data, int limit, CancellationToken ct)
    {
        var f = data.Filter;
        var routeIds = data.Routes.Keys.ToList();
        var q = db.PatrolRuns.AsNoTracking().Include(r => r.Stops)
            .Where(r => routeIds.Contains(r.RouteId) && r.ExpectedStart >= f.FromUtc && r.ExpectedStart < f.ToUtc);
        if (f.CheckpointId is { } cp) q = q.Where(r => r.Stops.Any(s => s.CheckpointId == cp));
        if (f.GuardId is { } g)
            q = q.Where(r => r.Stops.Any(s => s.PassId != null && db.PatrolPasses.Any(p => p.Id == s.PassId && p.PatrolGuardId == g)));
        data.RunsTotal = await q.CountAsync(ct);
        data.Runs = await q.OrderBy(r => r.ExpectedStart).ThenBy(r => r.RouteId).Take(limit).AsSplitQuery().ToListAsync(ct);
        var passIds = data.Runs.SelectMany(r => r.Stops).Where(s => s.PassId != null).Select(s => s.PassId!.Value).Distinct().ToList();
        data.RunPasses = passIds.Count == 0 ? [] : await db.PatrolPasses.AsNoTracking().Where(p => passIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
    }

    /// <summary>Puntos que entran en el informe de cobertura según los filtros (ruta, ubicación, punto).</summary>
    public static List<PatrolCheckpoint> CoverageCheckpoints(Data data)
    {
        var f = data.Filter;
        HashSet<int>? ofRoute = f.RouteId is null ? null : data.Routes.Values.SelectMany(r => r.Stops).Select(s => s.CheckpointId).ToHashSet();
        return data.Checkpoints.Values
            .Where(c => f.CheckpointId is null || c.Id == f.CheckpointId)
            .Where(c => ofRoute is null || ofRoute.Contains(c.Id))
            .Where(c => data.InLocation(c.LocationId))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ------------------------------------------------------------------
    // 1. Resumen ejecutivo

    public static ReportTable Summary(Data d, ReportMeta meta, string subtitle)
    {
        var runs = d.Runs;
        var final = runs.Where(r => IsFinal(r.Status)).ToList();
        int completed = final.Count(r => r.Status == PatrolRunStatus.Completed);
        int partial = final.Count(r => r.Status == PatrolRunStatus.Partial);
        int notStarted = final.Count(r => r.Status == PatrolRunStatus.NotStarted);
        int unfinished = final.Count(r => r.Status == PatrolRunStatus.Unfinished);
        int cancelled = runs.Count(r => r.Status == PatrolRunStatus.Cancelled);
        int open = runs.Count - final.Count - cancelled;

        var rows = new List<object?[]>
        {
            new object?[] { "Cumplida", completed, Ratio(completed, final.Count) },
            new object?[] { "Incompleta", partial, Ratio(partial, final.Count) },
            new object?[] { "No iniciada", notStarted, Ratio(notStarted, final.Count) },
            new object?[] { "Sin terminar", unfinished, Ratio(unfinished, final.Count) },
        };
        if (cancelled > 0) rows.Add(["Anulada (no cuenta)", cancelled, null]);
        if (open > 0) rows.Add(["En curso o pendiente (no cuenta)", open, null]);

        // Puntos de las rondas evaluadas (cumplidas o incompletas).
        var evaluatedStops = final.Where(r => r.Status is PatrolRunStatus.Completed or PatrolRunStatus.Partial)
            .SelectMany(r => r.Stops).Where(s => s.Required).ToList();
        var delays = final.Select(r => (r, start: StartOf(r))).Where(x => x.start != null)
            .Select(x => (x.start!.Value - x.r.ExpectedStart).TotalMinutes).ToList();

        var totals = new List<(string, string)>
        {
            ("Rondas programadas", N(runs.Count)),
            ("Con resultado", N(final.Count)),
            ("Cumplimiento", P(Ratio(completed, final.Count))),
            ("Puntos bien marcados", P(Ratio(evaluatedStops.Count(StopOk), evaluatedStops.Count))),
            ("Demora promedio de salida", delays.Count == 0 ? "—" : delays.Average().ToString("0.0", Es) + " min"),
        };

        var columns = new List<ReportColumn>
        {
            new("Resultado", ReportCellKind.Result, 7.0, 34),
            new("Rondas", ReportCellKind.Int, 3.0, 12),
            new("% de las rondas con resultado", ReportCellKind.Percent, 4.0, 16),
        };

        // Tendencia: por día hasta un mes; por semana hasta medio año; si no, por mes.
        double days = (d.Filter.ToUtc - d.Filter.FromUtc).TotalDays;
        string bucketName = days <= 31.5 ? "Día" : days <= 190 ? "Semana (desde)" : "Mes (desde)";
        DateTime Bucket(DateTime local) => days <= 31.5 ? local.Date
            : days <= 190 ? local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7))
            : new DateTime(local.Year, local.Month, 1);
        var trend = final.GroupBy(r => Bucket(L(r.ExpectedStart))).OrderBy(g => g.Key).Select(g => new object?[]
        {
            g.Key, g.Count(),
            g.Count(r => r.Status == PatrolRunStatus.Completed), g.Count(r => r.Status == PatrolRunStatus.Partial),
            g.Count(r => r.Status == PatrolRunStatus.NotStarted), g.Count(r => r.Status == PatrolRunStatus.Unfinished),
            Ratio(g.Count(r => r.Status == PatrolRunStatus.Completed), g.Count()),
        }).ToList();
        var trendColumns = new List<ReportColumn>
        {
            new(bucketName, ReportCellKind.Date, 2.6, 14),
            new("Rondas", ReportCellKind.Int, 1.9, 10),
            new("Cumplidas", ReportCellKind.Int, 2.1, 11),
            new("Incompletas", ReportCellKind.Int, 2.3, 12),
            new("No iniciadas", ReportCellKind.Int, 2.4, 12),
            new("Sin terminar", ReportCellKind.Int, 2.4, 12),
            new("% cumplimiento", ReportCellKind.Percent, 2.7, 14),
        };

        var worstRoutes = final.GroupBy(r => r.RouteId).Select(g => new
        {
            Name = d.RouteName(g.Key), Total = g.Count(),
            Completed = g.Count(r => r.Status == PatrolRunStatus.Completed),
            Partial = g.Count(r => r.Status == PatrolRunStatus.Partial),
            NotStarted = g.Count(r => r.Status == PatrolRunStatus.NotStarted),
            Unfinished = g.Count(r => r.Status == PatrolRunStatus.Unfinished),
        }).Where(x => x.Completed < x.Total)
            .OrderBy(x => x.Completed / (double)x.Total).ThenByDescending(x => x.Total - x.Completed).Take(5)
            .Select(x => new object?[] { x.Name, x.Total, x.Total - x.Completed, x.Partial, x.NotStarted, x.Unfinished, Ratio(x.Completed, x.Total) })
            .ToList();
        var routeColumns = new List<ReportColumn>
        {
            new("Ruta", ReportCellKind.Text, 5.4, 30),
            new("Rondas", ReportCellKind.Int, 1.8, 10),
            new("Con falla", ReportCellKind.Int, 2.0, 10),
            new("Incompletas", ReportCellKind.Int, 2.2, 12),
            new("No iniciadas", ReportCellKind.Int, 2.3, 12),
            new("Sin terminar", ReportCellKind.Int, 2.3, 12),
            new("% cumplimiento", ReportCellKind.Percent, 2.6, 14),
        };

        var worstPoints = WorstPoints(d, final, 5);

        return new ReportTable("Resumen ejecutivo de rondas", "rondas_resumen_ejecutivo", meta, columns, rows, totals, Landscape: false)
        {
            Subtitle = subtitle,
            MainHeading = "Resultado de las rondas",
            SheetName = "Resumen",
            EmptyText = "No hay rondas en el periodo.",
            Sections =
            [
                new ReportSection(days <= 31.5 ? "Tendencia diaria" : days <= 190 ? "Tendencia semanal" : "Tendencia mensual",
                    trendColumns, trend)
                {
                    SheetName = "Tendencia", EmptyText = "No hay rondas con resultado en el periodo.",
                    // En el PDF, un gráfico de columnas apiladas (cabe en la página); en Excel, la tabla.
                    Chart = new ReportChart(
                        trend.Select(t => ((DateTime)t[0]!).ToString(days <= 190 ? "d/M" : "MM-yy", Es)).ToList(),
                        [
                            new("Cumplidas", trend.Select(t => (double)(int)t[2]!).ToList(), "2E9E4F"),
                            new("Incompletas", trend.Select(t => (double)(int)t[3]!).ToList(), "E3A21A"),
                            new("No iniciadas", trend.Select(t => (double)(int)t[4]!).ToList(), "C0392B"),
                            new("Sin terminar", trend.Select(t => (double)(int)t[5]!).ToList(), "7E57C2"),
                        ]),
                },
                new ReportSection("Rutas con más fallas", routeColumns, worstRoutes)
                    { SheetName = "Peores rutas", EmptyText = "Ninguna ruta tuvo fallas en el periodo." },
                new ReportSection("Puntos más fallados", PointColumns, worstPoints)
                {
                    SheetName = "Peores puntos",
                    Note = "Sobre las rondas evaluadas (cumplidas o incompletas): las no iniciadas no se cargan a ningún punto.",
                    EmptyText = "Ningún punto falló en el periodo.",
                },
            ],
        };
    }

    private static readonly List<ReportColumn> PointColumns =
    [
        new("Punto", ReportCellKind.Text, 4.4, 26),
        new("Esperado", ReportCellKind.Int, 1.9, 10),
        new("Bien", ReportCellKind.Int, 1.6, 10),
        new("No marcado", ReportCellKind.Int, 2.1, 12),
        new("Tarde", ReportCellKind.Int, 1.6, 10),
        new("Temprano", ReportCellKind.Int, 1.9, 10),
        new("Fuera de orden", ReportCellKind.Int, 2.4, 12),
        new("% bien", ReportCellKind.Percent, 2.0, 10),
    ];

    private static List<object?[]> WorstPoints(Data d, List<PatrolRun> final, int take) =>
        final.Where(r => r.Status is PatrolRunStatus.Completed or PatrolRunStatus.Partial)
            .SelectMany(r => r.Stops).Where(s => s.Required)
            .Where(s => d.Filter.CheckpointId is null || s.CheckpointId == d.Filter.CheckpointId)
            .GroupBy(s => s.CheckpointId).Select(g => new
            {
                Name = d.CheckpointName(g.Key), Total = g.Count(), Ok = g.Count(StopOk),
                Missed = g.Count(s => s.Status is PatrolStopStatus.Missed or PatrolStopStatus.Pending),
                Late = g.Count(s => s.Status == PatrolStopStatus.Late),
                Early = g.Count(s => s.Status == PatrolStopStatus.Early),
                OutOfOrder = g.Count(s => s.Status == PatrolStopStatus.OutOfOrder),
            }).Where(x => x.Ok < x.Total)
            .OrderBy(x => x.Ok / (double)x.Total).ThenByDescending(x => x.Total - x.Ok).Take(take)
            .Select(x => new object?[] { x.Name, x.Total, x.Ok, x.Missed, x.Late, x.Early, x.OutOfOrder, Ratio(x.Ok, x.Total) })
            .ToList();

    // ------------------------------------------------------------------
    // 2 y 3. Cumplimiento de rutas (detalle) e incidencias

    private static string GuardsOf(Data d, PatrolRun r, Func<PatrolRunStop, bool>? only = null) => string.Join(", ",
        r.Stops.Where(s => only is null || only(s)).Where(s => s.PassId is { } p && d.RunPasses.ContainsKey(p))
            .Select(s => d.RunPasses[s.PassId!.Value].GuardName ?? "Sin guardia").Distinct());

    private static string? StopGuard(Data d, PatrolRunStop s) =>
        s.PassId is { } p && d.RunPasses.TryGetValue(p, out var pass) ? pass.GuardName ?? "Sin guardia" : null;

    public static ReportTable Compliance(Data d, ReportMeta meta, string subtitle, bool pdf, bool incidentsOnly)
    {
        var runs = incidentsOnly
            ? d.Runs.Where(r => r.Status is PatrolRunStatus.NotStarted or PatrolRunStatus.Partial or PatrolRunStatus.Unfinished).ToList()
            : d.Runs;
        // En incidencias, de cada ronda solo los puntos que fallaron (de una no iniciada, ninguno: la causa lo dice todo).
        IEnumerable<PatrolRunStop> StopsOf(PatrolRun r) => r.Stops.OrderBy(s => s.Sequence)
            .Where(s => !incidentsOnly || (r.Status != PatrolRunStatus.NotStarted && StopFailed(s)));

        var final = runs.Where(r => IsFinal(r.Status)).ToList();
        var totals = incidentsOnly
            ? new List<(string, string)>
            {
                ("Rondas con incidencia", N(runs.Count)),
                ("No iniciadas", N(runs.Count(r => r.Status == PatrolRunStatus.NotStarted))),
                ("Incompletas", N(runs.Count(r => r.Status == PatrolRunStatus.Partial))),
                ("Sin terminar", N(runs.Count(r => r.Status == PatrolRunStatus.Unfinished))),
                ("Puntos fallidos", N(runs.Where(r => r.Status != PatrolRunStatus.NotStarted).Sum(r => r.Stops.Count(StopFailed)))),
                ("De un total de", N(d.Runs.Count) + " rondas"),
            }
            : new List<(string, string)>
            {
                ("Rondas", N(runs.Count)),
                ("Cumplidas", N(final.Count(r => r.Status == PatrolRunStatus.Completed))),
                ("Incompletas", N(final.Count(r => r.Status == PatrolRunStatus.Partial))),
                ("No iniciadas", N(final.Count(r => r.Status == PatrolRunStatus.NotStarted))),
                ("Sin terminar", N(final.Count(r => r.Status == PatrolRunStatus.Unfinished))),
                ("Cumplimiento", P(Ratio(final.Count(r => r.Status == PatrolRunStatus.Completed), final.Count))),
            };
        string title = incidentsOnly ? "Incidencias de rondas" : "Cumplimiento de rutas";
        string file = incidentsOnly ? "rondas_incidencias" : "rondas_cumplimiento";

        if (pdf)
        {
            var rows = new List<object?[]>();
            var styles = new List<ReportRowStyle>();
            foreach (var r in runs)
            {
                var start = r.ActualStart;
                var parts = new List<string>();
                if (start is { } s) parts.Add($"Salida {L(s):HH:mm:ss}");
                else if (r.Status != PatrolRunStatus.NotStarted) parts.Add("Sin salida registrada");
                if (r.ActualEnd is { } e) parts.Add($"Vuelta {L(e):HH:mm:ss}");
                else if (start is not null) parts.Add("Sin vuelta");
                string guards = GuardsOf(d, r);
                if (guards.Length > 0) parts.Add($"Guardias: {guards}");
                if (r.ReaderId is { } rd && d.Readers.TryGetValue(rd, out var reader)) parts.Add($"Bastón: {reader}");
                int required = r.Stops.Count(x => x.Required);
                if (IsFinal(r.Status) && r.Status != PatrolRunStatus.NotStarted)
                    parts.Add($"Puntos bien: {r.Stops.Count(x => x.Required && StopOk(x))} de {required}");
                if (!string.IsNullOrWhiteSpace(r.Cause)) parts.Add($"Causa: {r.Cause}");
                string location = d.RouteLocation(r.RouteId) is { } loc ? $" ({loc})" : "";
                rows.Add([$"{L(r.ExpectedStart).ToString("ddd dd-MM-yyyy HH:mm", Es)} · {d.RouteName(r.RouteId)}{location} · {RunLabel(r.Status).ToUpperInvariant()}\n" +
                          string.Join(" · ", parts)]);
                styles.Add(ReportRowStyle.Group);
                foreach (var stop in StopsOf(r))
                {
                    double? diff = stop.ExpectedAt is { } exp && stop.ActualAt is { } act ? Math.Round((act - exp).TotalMinutes, 1) : null;
                    rows.Add([
                        stop.Sequence + 1,
                        d.CheckpointName(stop.CheckpointId) + (stop.Required ? "" : " (opcional)"),
                        LT(stop.ExpectedAt),
                        stop.ToleranceMinutes is { } tol ? $"± {tol} min" : "",
                        LT(stop.ActualAt),
                        diff,
                        StopLabel(stop.Status, r.Status),
                        StopGuard(d, stop) ?? "",
                    ]);
                    styles.Add(ReportRowStyle.Normal);
                }
            }
            var columns = new List<ReportColumn>
            {
                new("N°", ReportCellKind.Int, 1.0, 0),
                new("Punto", ReportCellKind.Text, 6.0, 0),
                new("Hora esperada", ReportCellKind.Time, 2.8, 0),
                new("Margen", ReportCellKind.Text, 2.0, 0),
                new("Hora real", ReportCellKind.Time, 2.8, 0),
                new("Diferencia (min)", ReportCellKind.Decimal, 2.6, 0),
                new("Estado", ReportCellKind.Result, 3.0, 0),
                new("Guardia", ReportCellKind.Text, 6.5, 0),
            };
            return new ReportTable(title, file, meta, columns, rows, totals)
            {
                Subtitle = subtitle, RowStyles = styles,
                EmptyText = incidentsOnly ? "No hubo incidencias en el periodo." : "No hay rondas en el periodo.",
            };
        }

        // Excel: una hoja con una fila por ronda y otra con una fila por punto, para filtrar y contar.
        var runColumns = new List<ReportColumn>
        {
            new("Programada", ReportCellKind.DateTime, 0, 20),
            new("Ruta", ReportCellKind.Text, 0, 26),
            new("Ubicación", ReportCellKind.Text, 0, 24),
            new("Salida", ReportCellKind.DateTime, 0, 20),
            new("Vuelta", ReportCellKind.DateTime, 0, 20),
            new("Duración", ReportCellKind.Duration, 0, 10),
            new("Demora de salida (min)", ReportCellKind.Decimal, 0, 12),
            new("Guardias", ReportCellKind.Text, 0, 30),
            new("Bastón", ReportCellKind.Text, 0, 16),
            new("Estado", ReportCellKind.Result, 0, 14),
            new("Causa", ReportCellKind.Text, 0, 50),
            new("Puntos requeridos", ReportCellKind.Int, 0, 11),
            new("Puntos bien", ReportCellKind.Int, 0, 11),
            new("Puntos fallidos", ReportCellKind.Int, 0, 11),
        };
        var runRows = runs.Select(r =>
        {
            var start = StartOf(r);
            bool counted = IsFinal(r.Status) && r.Status != PatrolRunStatus.NotStarted;
            return new object?[]
            {
                L(r.ExpectedStart), d.RouteName(r.RouteId), d.RouteLocation(r.RouteId) ?? "",
                r.ActualStart is { } s ? L(s) : null, r.ActualEnd is { } e ? L(e) : null,
                r.ActualStart is { } s2 && r.ActualEnd is { } e2 && e2 >= s2 ? e2 - s2 : null,
                start is { } st ? Math.Round((st - r.ExpectedStart).TotalMinutes, 1) : null,
                GuardsOf(d, r),
                r.ReaderId is { } rd ? d.Readers.GetValueOrDefault(rd) ?? "" : "",
                RunLabel(r.Status), r.Cause ?? "",
                r.Stops.Count(x => x.Required),
                counted ? r.Stops.Count(x => x.Required && StopOk(x)) : null,
                counted || r.Status == PatrolRunStatus.NotStarted ? r.Stops.Count(StopFailed) : null,
            };
        }).ToList();
        var stopColumns = new List<ReportColumn>
        {
            new("Ronda programada", ReportCellKind.DateTime, 0, 20),
            new("Ruta", ReportCellKind.Text, 0, 24),
            new("Estado de la ronda", ReportCellKind.Result, 0, 14),
            new("N°", ReportCellKind.Int, 0, 6),
            new("Punto", ReportCellKind.Text, 0, 22),
            new("Ubicación del punto", ReportCellKind.Text, 0, 22),
            new("Requerido", ReportCellKind.Text, 0, 10),
            new("Hora esperada", ReportCellKind.DateTime, 0, 20),
            new("Margen (min)", ReportCellKind.Int, 0, 10),
            new("Hora real", ReportCellKind.DateTime, 0, 20),
            new("Diferencia (min)", ReportCellKind.Decimal, 0, 11),
            new("Estado del punto", ReportCellKind.Result, 0, 15),
            new("Guardia", ReportCellKind.Text, 0, 26),
        };
        var stopRows = runs.SelectMany(r => StopsOf(r).Select(stop => new object?[]
        {
            L(r.ExpectedStart), d.RouteName(r.RouteId), RunLabel(r.Status), stop.Sequence + 1,
            d.CheckpointName(stop.CheckpointId), d.CheckpointLocation(stop.CheckpointId) ?? "", stop.Required ? "Sí" : "No",
            stop.ExpectedAt is { } exp ? L(exp) : null, stop.ToleranceMinutes,
            stop.ActualAt is { } act ? L(act) : null,
            stop.ExpectedAt is { } e1 && stop.ActualAt is { } a1 ? Math.Round((a1 - e1).TotalMinutes, 1) : null,
            StopLabel(stop.Status, r.Status), StopGuard(d, stop) ?? "",
        })).ToList();
        return new ReportTable(title, file, meta, runColumns, runRows, totals)
        {
            Subtitle = subtitle, SheetName = "Rondas",
            Sections =
            [
                new ReportSection(incidentsOnly ? "Puntos fallidos" : "Puntos de cada ronda", stopColumns, stopRows)
                    { SheetName = incidentsOnly ? "Puntos fallidos" : "Puntos", ExcelOnly = true },
            ],
        };
    }

    // ------------------------------------------------------------------
    // 4. Desempeño por guardia y hoja de servicio

    public static ReportTable GuardPerformance(Data d, Dictionary<int, int> passCounts, ReportMeta meta, string subtitle)
    {
        var stats = new Dictionary<int, GuardStats>();
        GuardStats Of(int id) => stats.TryGetValue(id, out var s) ? s : stats[id] = new GuardStats();
        foreach (var r in d.Runs.Where(r => r.Status is PatrolRunStatus.Completed or PatrolRunStatus.Partial or PatrolRunStatus.Unfinished))
        {
            var mine = r.Stops.Where(s => s.PassId is { } p && d.RunPasses.TryGetValue(p, out var pass) && pass.PatrolGuardId != null)
                .GroupBy(s => d.RunPasses[s.PassId!.Value].PatrolGuardId!.Value).ToList();
            var start = StartOf(r);
            foreach (var g in mine)
            {
                if (d.Filter.GuardId is { } only && g.Key != only) continue;
                var s = Of(g.Key);
                s.Runs++;
                if (r.Status == PatrolRunStatus.Completed) s.Completed++;
                if (start is { } st) s.Delays.Add((st - r.ExpectedStart).TotalMinutes);
                foreach (var stop in g)
                {
                    s.Marked++;
                    switch (stop.Status)
                    {
                        case PatrolStopStatus.OnTime or PatrolStopStatus.Done: s.OnTime++; break;
                        case PatrolStopStatus.Late: s.Late++; break;
                        case PatrolStopStatus.Early: s.Early++; break;
                        case PatrolStopStatus.OutOfOrder: s.OutOfOrder++; break;
                    }
                }
                // Los puntos que nadie marcó en una ronda que hizo se le cargan (si hubo relevo, a ambos).
                s.Missed += r.Stops.Count(x => x.Required && x.PassId == null);
            }
        }
        foreach (var (id, _) in passCounts)
            if (d.Filter.GuardId is null || d.Filter.GuardId == id) Of(id);

        var rows = stats.Select(kv =>
        {
            var (name, employeeNo) = d.Guards.TryGetValue(kv.Key, out var g) ? g : ($"Guardia {kv.Key}", null);
            var s = kv.Value;
            return new
            {
                Name = name, Ratio = Ratio(s.Completed, s.Runs), s.Runs,
                Row = new object?[]
                {
                    name, employeeNo ?? "", s.Runs, s.Completed, Ratio(s.Completed, s.Runs),
                    s.Delays.Count == 0 ? null : Math.Round(s.Delays.Average(), 1),
                    s.Marked, s.OnTime, s.Late, s.Early, s.OutOfOrder, s.Missed, passCounts.GetValueOrDefault(kv.Key),
                },
            };
        }).OrderByDescending(x => x.Ratio ?? -1).ThenByDescending(x => x.Runs).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select((x, i) => new object?[] { i + 1 }.Concat(x.Row).ToArray()).ToList();

        var columns = new List<ReportColumn>
        {
            new("Lugar", ReportCellKind.Int, 1.2, 7),
            new("Guardia", ReportCellKind.Text, 4.6, 28),
            new("N° / RUT", ReportCellKind.Text, 2.2, 12),
            new("Rondas hechas", ReportCellKind.Int, 1.9, 10),
            new("Cumplidas", ReportCellKind.Int, 1.9, 10),
            new("% cumplimiento", ReportCellKind.Percent, 2.1, 12),
            new("Demora de salida prom. (min)", ReportCellKind.Decimal, 2.4, 14),
            new("Puntos marcados", ReportCellKind.Int, 1.9, 10),
            new("A tiempo", ReportCellKind.Int, 1.6, 9),
            new("Tarde", ReportCellKind.Int, 1.4, 9),
            new("Temprano", ReportCellKind.Int, 1.7, 9),
            new("Fuera de orden", ReportCellKind.Int, 1.8, 9),
            new("Puntos omitidos", ReportCellKind.Int, 1.9, 10),
            new("Pasos en bitácora", ReportCellKind.Int, 2.0, 11),
        };
        var all = stats.Values.ToList();
        var totals = new List<(string, string)>
        {
            ("Guardias", N(all.Count)),
            ("Rondas con resultado en el periodo", N(d.Runs.Count(r => IsFinal(r.Status)))),
            ("Pasos en bitácora", N(passCounts.Where(kv => d.Filter.GuardId is null || kv.Key == d.Filter.GuardId).Sum(kv => kv.Value))),
        };
        return new ReportTable("Desempeño por guardia", "rondas_desempeno_guardias", meta, columns, rows, totals)
        {
            Subtitle = subtitle, SheetName = "Desempeño",
            EmptyText = "Ningún guardia registró pasos en el periodo.",
            Sections =
            [
                new ReportSection("Cómo se calcula", [new("Concepto", ReportCellKind.Text, 5.5, 30), new("Detalle", ReportCellKind.Text, 21.0, 110)],
                [
                    ["Rondas hechas", "Rondas con resultado (cumplidas, incompletas o sin terminar) en que el guardia marcó al menos un punto."],
                    ["% cumplimiento", "Cumplidas sobre rondas hechas."],
                    ["Demora de salida", "Promedio de (salida de la base, o primer punto si no hay base) menos la hora programada. Negativo = salió antes."],
                    ["Puntos omitidos", "Puntos requeridos que nadie marcó en las rondas que hizo (si hubo relevo, se cargan a ambos)."],
                    ["Pasos en bitácora", "Todos sus pasos válidos del periodo, sean o no de una ronda programada."],
                ]),
            ],
        };
    }

    private sealed class GuardStats
    {
        public int Runs, Completed, Marked, OnTime, Late, Early, OutOfOrder, Missed;
        public List<double> Delays { get; } = [];
    }

    /// <summary>Pasos de un guardia en orden, agrupados por turno (pasos separados por más de 4 h abren otro turno).</summary>
    public static ReportTable GuardLog(Data d, int guardId, List<PatrolPass> passes,
        Dictionary<long, (PatrolRun Run, PatrolRunStop Stop)> usage, ReportMeta meta, string subtitle, bool pdf)
    {
        var (name, employeeNo) = d.Guards.TryGetValue(guardId, out var g) ? g : (passes.FirstOrDefault()?.GuardName ?? $"Guardia {guardId}", null);
        var rows = new List<object?[]>();
        var styles = new List<ReportRowStyle>();
        var shifts = new List<List<PatrolPass>>();
        foreach (var p in passes)
        {
            if (shifts.Count == 0 || p.ReadAt - shifts[^1][^1].ReadAt > TimeSpan.FromHours(4)) shifts.Add([]);
            shifts[^1].Add(p);
        }
        foreach (var shift in shifts)
        {
            if (pdf)
            {
                var first = L(shift[0].ReadAt);
                var last = L(shift[^1].ReadAt);
                int runs = shift.Where(p => usage.ContainsKey(p.Id)).Select(p => usage[p.Id].Run.Id).Distinct().Count();
                rows.Add([$"Turno del {first.ToString("ddd dd-MM-yyyy", Es)}: {first:HH:mm} a {last:HH:mm}" +
                          (last.Date != first.Date ? $" del {last:dd-MM}" : "") +
                          $" · {N(shift.Count)} pasos · {N(shift.Select(p => p.CheckpointId).Distinct().Count())} puntos · {N(runs)} rondas"]);
                styles.Add(ReportRowStyle.Group);
            }
            foreach (var p in shift)
            {
                usage.TryGetValue(p.Id, out var use);
                string state = p.Voided ? $"Anulado: {p.VoidReason}" : p.EditedBy is not null ? $"Corregido por {p.EditedBy}: {p.EditReason}" : "";
                rows.Add([
                    L(p.ReadAt), d.CheckpointName(p.CheckpointId), d.CheckpointLocation(p.CheckpointId) ?? "",
                    p.ReaderId is { } rd ? d.Readers.GetValueOrDefault(rd) ?? "" : "",
                    use.Run is null ? "" : d.RouteName(use.Run.RouteId),
                    use.Run is null ? null : L(use.Run.ExpectedStart),
                    use.Stop is null ? (p.Voided ? "" : "Fuera de ronda") : StopLabel(use.Stop.Status, use.Run!.Status),
                    state,
                ]);
                styles.Add(ReportRowStyle.Normal);
            }
        }
        var columns = new List<ReportColumn>
        {
            new("Fecha y hora", ReportCellKind.DateTime, 3.3, 20),
            new("Punto", ReportCellKind.Text, 4.0, 22),
            new("Ubicación", ReportCellKind.Text, 3.6, 22),
            new("Bastón", ReportCellKind.Text, 2.6, 16),
            new("Ruta", ReportCellKind.Text, 3.8, 24),
            new("Ronda programada", ReportCellKind.DateTime, 3.3, 20),
            new("Estado del punto", ReportCellKind.Result, 2.6, 15),
            new("Observación", ReportCellKind.Text, 3.5, 40),
        };
        var valid = passes.Where(p => !p.Voided).ToList();
        var totals = new List<(string, string)>
        {
            ("Guardia", employeeNo is { Length: > 0 } ? $"{name} ({employeeNo})" : name),
            ("Pasos", N(valid.Count)),
            ("Anulados", N(passes.Count - valid.Count)),
            ("Turnos", N(shifts.Count)),
            ("Puntos distintos", N(valid.Select(p => p.CheckpointId).Distinct().Count())),
            ("Rondas en que participó", N(usage.Values.Select(u => u.Run.Id).Distinct().Count())),
        };
        return new ReportTable($"Hoja de servicio: {name}", "rondas_hoja_servicio", meta, columns, rows, totals)
        {
            Subtitle = subtitle, RowStyles = pdf ? styles : null, SheetName = "Hoja de servicio",
            EmptyText = "El guardia no tiene pasos en el periodo.",
        };
    }

    // ------------------------------------------------------------------
    // 5. Cobertura por punto

    public static ReportTable Coverage(Data d, List<PatrolCheckpoint> checkpoints, List<PatrolPass> passes, ReportMeta meta,
        string subtitle, bool pdf)
    {
        var f = d.Filter;
        var gap = TimeSpan.FromMinutes(f.GapMinutes);
        var end = f.ToUtc < DateTime.UtcNow ? f.ToUtc : DateTime.UtcNow;
        var byPoint = passes.GroupBy(p => p.CheckpointId).ToDictionary(g => g.Key, g => g.Select(p => p.ReadAt).OrderBy(t => t).ToList());

        var rows = new List<object?[]>();
        var gapRows = new List<object?[]>();
        var heatRows = new List<object?[]>();
        int totalGaps = 0;
        foreach (var c in checkpoints)
        {
            var times = byPoint.GetValueOrDefault(c.Id) ?? [];
            var intervals = new List<TimeSpan>();
            for (int i = 1; i < times.Count; i++) intervals.Add(times[i] - times[i - 1]);

            // Ventanas sin cobertura: entre visitas, y desde el inicio del periodo / hasta su fin.
            var windows = new List<(DateTime From, DateTime To, string Note)>();
            var marks = new List<DateTime> { f.FromUtc };
            marks.AddRange(times);
            marks.Add(end);
            for (int i = 1; i < marks.Count; i++)
            {
                if (marks[i] - marks[i - 1] <= gap) continue;
                string note = times.Count == 0 ? "Sin visitas en todo el periodo"
                    : i == 1 ? "Desde el inicio del periodo"
                    : i == marks.Count - 1 ? "Hasta el fin del periodo" : "";
                windows.Add((marks[i - 1], marks[i], note));
            }
            totalGaps += windows.Count;
            var uncovered = windows.Aggregate(TimeSpan.Zero, (a, w) => a + (w.To - w.From));
            rows.Add([
                c.Name, d.CheckpointLocation(c.Id) ?? "", times.Count,
                times.Count > 0 ? L(times[0]) : null, times.Count > 0 ? L(times[^1]) : null,
                intervals.Count > 0 ? intervals.Min() : null,
                intervals.Count > 0 ? intervals.Max() : null,
                intervals.Count > 0 ? TimeSpan.FromTicks((long)intervals.Average(t => t.Ticks)) : null,
                windows.Count, uncovered,
            ]);
            foreach (var w in windows)
                gapRows.Add([c.Name, d.CheckpointLocation(c.Id) ?? "", L(w.From), L(w.To), w.To - w.From, w.Note]);

            var hours = new object?[26];
            hours[0] = c.Name;
            for (int h = 0; h < 24; h++) hours[h + 1] = 0;
            foreach (var t in times) hours[L(t).Hour + 1] = (int)hours[L(t).Hour + 1]! + 1;
            hours[25] = times.Count;
            heatRows.Add(hours);
        }

        var columns = new List<ReportColumn>
        {
            new("Punto", ReportCellKind.Text, 4.0, 22),
            new("Ubicación", ReportCellKind.Text, 4.0, 22),
            new("Visitas", ReportCellKind.Int, 1.6, 9),
            new("Primera", ReportCellKind.DateTime, 3.0, 20),
            new("Última", ReportCellKind.DateTime, 3.0, 20),
            new("Intervalo mínimo", ReportCellKind.Duration, 2.0, 11),
            new("Intervalo máximo", ReportCellKind.Duration, 2.0, 11),
            new("Intervalo promedio", ReportCellKind.Duration, 2.0, 11),
            new($"Ventanas > {f.GapMinutes} min", ReportCellKind.Int, 2.0, 11),
            new("Tiempo en esas ventanas", ReportCellKind.Duration, 2.6, 12),
        };
        var gapColumns = new List<ReportColumn>
        {
            new("Punto", ReportCellKind.Text, 4.2, 22),
            new("Ubicación", ReportCellKind.Text, 4.5, 22),
            new("Desde", ReportCellKind.DateTime, 3.6, 20),
            new("Hasta", ReportCellKind.DateTime, 3.6, 20),
            new("Duración", ReportCellKind.Duration, 2.4, 11),
            new("Nota", ReportCellKind.Text, 5.0, 30),
        };
        var heatColumns = new List<ReportColumn> { new("Punto", ReportCellKind.Text, 3.4, 22) };
        for (int h = 0; h < 24; h++) heatColumns.Add(new($"{h:00}", ReportCellKind.Heat, 0.93, 5.5));
        heatColumns.Add(new("Total", ReportCellKind.Int, 1.3, 9));

        const int MaxPdfGaps = 1500;
        string? gapNote = pdf && gapRows.Count > MaxPdfGaps
            ? $"Se muestran las primeras {MaxPdfGaps:N0} de {gapRows.Count:N0} ventanas: el Excel las trae todas."
            : null;
        var totals = new List<(string, string)>
        {
            ("Puntos", N(checkpoints.Count)),
            ("Visitas", N(passes.Count)),
            ("Puntos sin visitas", N(checkpoints.Count(c => !byPoint.ContainsKey(c.Id)))),
            ($"Ventanas sin cobertura (> {f.GapMinutes} min)", N(totalGaps)),
        };
        return new ReportTable("Cobertura por punto", "rondas_cobertura_puntos", meta, columns, rows, totals)
        {
            Subtitle = subtitle, SheetName = "Cobertura",
            EmptyText = "No hay puntos que coincidan con los filtros.",
            Sections =
            [
                new ReportSection("Mapa de calor: visitas por hora del día", heatColumns, heatRows)
                {
                    SheetName = "Mapa de calor",
                    Note = "Cantidad de visitas a cada punto según la hora local en que ocurrieron (00 = de 00:00 a 00:59).",
                },
                new ReportSection($"Ventanas sin cobertura de más de {f.GapMinutes} min",
                    gapColumns, pdf ? gapRows.Take(MaxPdfGaps).ToList() : gapRows)
                {
                    SheetName = "Ventanas sin cobertura",
                    Note = (gapNote is null ? "" : gapNote + " ") +
                           "Tiempo entre dos visitas seguidas a un punto (y desde el inicio o hasta el fin del periodo) mayor que el umbral.",
                    EmptyText = "Ningún punto estuvo sin visita más que el umbral.",
                },
            ],
        };
    }
}
