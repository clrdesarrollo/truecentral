using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Reports;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Registros de acceso: el buscador del historial y su reportería en Excel y
/// PDF (detalle, asistencia por persona y día, resumen por puerta). Los dos usan el MISMO
/// filtro, así que lo que se exporta es exactamente lo que el operador ve en
/// pantalla.
///
/// Filtros (todos opcionales, por query string):
/// <c>from</c>/<c>to</c> (ISO, UTC), <c>deviceIds</c> y <c>doorIds</c> (listas
/// separadas por coma), <c>kinds</c> (Granted, Denied, DoorOpen, DoorClose,
/// Alarm, Other), <c>credentials</c> (Card, Face, Fingerprint...),
/// <c>personId</c>, <c>department</c>, <c>q</c> con <c>mode</c> (all, person,
/// employee, card) y el orden con <c>sort</c>/<c>dir</c>. Por compatibilidad
/// se aceptan también <c>deviceId</c>, <c>doorId</c> y <c>kind</c> sueltos.
/// </summary>
public static class AccessRecordsApi
{
    /// <summary>Tope de renglones de un reporte de detalle (más que eso no se lee ni se imprime).</summary>
    private const int MaxDetailExcel = 50_000;
    private const int MaxDetailPdf = 5_000;
    /// <summary>Tope de eventos que se agregan en los reportes de resumen.</summary>
    private const int MaxAggregate = 200_000;

    public sealed record Filter(
        DateTime? From, DateTime? To,
        IReadOnlyList<int> DeviceIds, IReadOnlyList<int> DoorIds,
        IReadOnlyList<AccessEventKind> Kinds, IReadOnlyList<AccessCredentialKind> Credentials,
        int? PersonId, string? Department, string? Q, string Mode, string Sort, bool Ascending);

    public static void MapAccessRecordsApi(this WebApplication app)
    {
        app.MapGet("/api/access/events", async (HttpContext ctx, VmsDbContext db, AuditService audit,
            int? page, int? pageSize, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            int pageNumber = Math.Max(page ?? 1, 1);
            int size = Math.Clamp(pageSize is null or 0 ? 50 : pageSize.Value, 1, 500);

            var filter = ReadFilter(ctx.Request.Query);
            var (query, error) = await BuildAsync(db, filter, ct);
            if (query is null) return Error(error!);
            // Alcance por ubicación: solo eventos de sus puertas (en la consulta: paginado y reportes cuentan solo esos).
            query = ScopeQueries.AccessEvents(db, query, await ctx.ScopeAsync(session));

            int total = await query.CountAsync(ct);
            var items = await Sorted(query, filter).Skip((pageNumber - 1) * size).Take(size).ToListAsync(ct);
            var people = await AccessEventService.PersonInfoAsync(db, items.Select(e => e.AccessPersonId), ct);

            // Solo se audita la búsqueda con filtros: el refresco automático de
            // la pantalla llenaría la bitácora de ruido.
            if (pageNumber == 1 && HasUserFilters(filter))
                await audit.LogAsync(ctx, "access", "search",
                    detail: $"Consultó los registros de acceso ({total} resultado(s)): {await DescribeAsync(db, filter, ct)}.");

            return Results.Ok(new AccessEventPageDto(total, pageNumber, size,
                items.Select(e => AccessEventService.ToDto(e,
                    e.AccessPersonId is int id ? people.GetValueOrDefault(id) : null)).ToList()));
        });

        // Reportería: detail = cada registro, attendance = primer y último
        // acceso de cada persona por día, doors = resumen por puerta.
        app.MapGet("/api/access/events/export", async (HttpContext ctx, VmsDbContext db, AuditService audit,
            IWebHostEnvironment env, string? format, string? report, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            bool pdf = string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase);
            string kind = (report ?? "detail").Trim().ToLowerInvariant();
            if (kind is not ("detail" or "attendance" or "doors"))
                return Error("Reporte desconocido: use detail, attendance o doors.");

            var filter = ReadFilter(ctx.Request.Query);
            var (query, error) = await BuildAsync(db, filter, ct);
            if (query is null) return Error(error!);
            // Alcance por ubicación: solo eventos de sus puertas (en la consulta: paginado y reportes cuentan solo esos).
            query = ScopeQueries.AccessEvents(db, query, await ctx.ScopeAsync(session));

            string filtersText = await DescribeAsync(db, filter, ct);
            int total = await query.CountAsync(ct);
            int limit = kind == "detail" ? (pdf ? MaxDetailPdf : MaxDetailExcel) : MaxAggregate;
            var events = await Sorted(query, kind == "detail" ? filter : filter with { Sort = "time", Ascending = true })
                .Take(limit).ToListAsync(ct);
            var people = await PeopleAsync(db, events, ct);

            var meta = new ReportMeta(
                GeneratedBy: session.Username,
                GeneratedAt: DateTime.Now,
                Filters: filtersText,
                Truncated: total > limit ? $"Se incluyen los primeros {limit:N0} de {total:N0} registros: acote los filtros para ver el resto." : null);
            var table = kind switch
            {
                "attendance" => AccessReports.Attendance(events, people, meta),
                "doors" => AccessReports.Doors(events, people, meta),
                _ => AccessReports.Detail(events, people, meta),
            };

            byte[] content;
            try
            {
                content = pdf
                    ? PdfReportWriter.Write(table, Path.Combine(env.WebRootPath, "logo.png"))
                    : XlsxWriter.Write(table);
            }
            catch (Exception ex)
            {
                await audit.LogAsync(ctx, "access", "report-exported",
                    detail: $"No se pudo generar el reporte \"{table.Title}\" en {(pdf ? "PDF" : "Excel")}: {ex.Message}",
                    success: false);
                return Error($"No se pudo generar el reporte: {ex.Message}", StatusCodes.Status500InternalServerError);
            }

            await audit.LogAsync(ctx, "access", "report-exported",
                detail: $"Exportó el reporte \"{table.Title}\" en {(pdf ? "PDF" : "Excel")} " +
                        $"({table.Rows.Count:N0} renglón(es) de {Math.Min(total, limit):N0} registro(s)): {filtersText}.",
                data: new { report = kind, format = pdf ? "pdf" : "xlsx", rows = table.Rows.Count, total });

            string file = $"{table.FileName}_{DateTime.Now:yyyyMMdd_HHmmss}";
            return pdf
                ? Results.File(content, "application/pdf", file + ".pdf")
                : Results.File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file + ".xlsx");
        });
    }

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    // ------------------------------------------------------------------
    // Filtro
    // ------------------------------------------------------------------

    public static Filter ReadFilter(IQueryCollection q)
    {
        static DateTime? Date(string? text) =>
            DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var value) ? value : null;
        static List<string> List(IQueryCollection q, params string[] names) => names
            .SelectMany(n => q[n].ToArray())
            .SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
        static List<int> Ints(IQueryCollection q, params string[] names) =>
            List(q, names).Select(v => int.TryParse(v, out int n) ? n : (int?)null).OfType<int>().Distinct().ToList();
        static List<T> Enums<T>(IQueryCollection q, params string[] names) where T : struct, Enum =>
            List(q, names).Select(v => Enum.TryParse<T>(v, true, out var e) ? e : (T?)null).OfType<T>().Distinct().ToList();

        string mode = (q["mode"].ToString() is { Length: > 0 } m ? m : "all").ToLowerInvariant();
        string sort = (q["sort"].ToString() is { Length: > 0 } s ? s : "time").ToLowerInvariant();
        return new Filter(
            From: Date(q["from"]), To: Date(q["to"]),
            DeviceIds: Ints(q, "deviceIds", "deviceId"),
            DoorIds: Ints(q, "doorIds", "doorId"),
            Kinds: Enums<AccessEventKind>(q, "kinds", "kind"),
            Credentials: Enums<AccessCredentialKind>(q, "credentials", "credential"),
            PersonId: int.TryParse(q["personId"], out int person) ? person : null,
            Department: q["department"].ToString() is { Length: > 0 } dep ? dep.Trim() : null,
            Q: q["q"].ToString() is { Length: > 0 } text ? text.Trim() : null,
            Mode: mode, Sort: sort,
            Ascending: string.Equals(q["dir"], "asc", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasUserFilters(Filter f) =>
        f.From is not null || f.To is not null || f.PersonId is not null || f.Q is not null || f.Department is not null ||
        f.DoorIds.Count > 0 || f.DeviceIds.Count > 0 || f.Kinds.Count > 0 || f.Credentials.Count > 0;

    public static async Task<(IQueryable<AccessEvent>? Query, string? Error)> BuildAsync(VmsDbContext db, Filter f,
        CancellationToken ct)
    {
        // Arreglos locales: EF traduce Contains sobre arreglos a "= ANY(...)".
        int[] deviceIds = [.. f.DeviceIds];
        AccessEventKind[] kinds = [.. f.Kinds];
        AccessCredentialKind[] credentials = [.. f.Credentials];

        var query = db.AccessEvents.AsNoTracking().AsQueryable();
        if (f.From is { } start) query = query.Where(e => e.Timestamp >= start);
        if (f.To is { } end) query = query.Where(e => e.Timestamp <= end);
        if (deviceIds.Length > 0) query = query.Where(e => deviceIds.Contains(e.AccessDeviceId));
        if (f.PersonId is int person) query = query.Where(e => e.AccessPersonId == person);
        if (kinds.Length > 0) query = query.Where(e => kinds.Contains(e.Kind));
        if (credentials.Length > 0) query = query.Where(e => credentials.Contains(e.Credential));

        if (f.DoorIds.Count > 0)
        {
            // La puerta se identifica en el historial por equipo + número, no
            // por su Id: el historial sobrevive a que se borre la puerta.
            int[] doorIds = [.. f.DoorIds];
            var doors = await db.AccessDoors.AsNoTracking().Where(d => doorIds.Contains(d.Id))
                .Select(d => new { d.AccessDeviceId, d.Number }).ToListAsync(ct);
            if (doors.Count == 0) return (null, "Las puertas indicadas ya no existen.");
            int[] keys = doors.Select(d => d.AccessDeviceId * 1000 + d.Number).ToArray();
            query = query.Where(e => e.DoorNumber != null && keys.Contains(e.AccessDeviceId * 1000 + e.DoorNumber.Value));
        }

        if (f.Department is { } department)
            query = query.Where(e => e.AccessPersonId != null &&
                                     db.AccessPersons.Any(p => p.Id == e.AccessPersonId && p.Department == department));

        if (f.Q is { } term)
        {
            string like = $"%{term}%";
            query = f.Mode switch
            {
                "person" => query.Where(e => e.PersonName != null && EF.Functions.ILike(e.PersonName, like)),
                "employee" => query.Where(e => e.EmployeeNo != null && EF.Functions.ILike(e.EmployeeNo, like)),
                "card" => query.Where(e => e.CardNumber != null && EF.Functions.ILike(e.CardNumber, like)),
                _ => query.Where(e => (e.PersonName != null && EF.Functions.ILike(e.PersonName, like)) ||
                                      (e.EmployeeNo != null && EF.Functions.ILike(e.EmployeeNo, like)) ||
                                      (e.CardNumber != null && EF.Functions.ILike(e.CardNumber, like)) ||
                                      EF.Functions.ILike(e.Description, like)),
            };
        }
        return (query, null);
    }

    private static IQueryable<AccessEvent> Sorted(IQueryable<AccessEvent> query, Filter f) => (f.Sort, f.Ascending) switch
    {
        ("person", true) => query.OrderBy(e => e.PersonName).ThenByDescending(e => e.Timestamp),
        ("person", false) => query.OrderByDescending(e => e.PersonName).ThenByDescending(e => e.Timestamp),
        ("employee", true) => query.OrderBy(e => e.EmployeeNo).ThenByDescending(e => e.Timestamp),
        ("employee", false) => query.OrderByDescending(e => e.EmployeeNo).ThenByDescending(e => e.Timestamp),
        ("card", true) => query.OrderBy(e => e.CardNumber).ThenByDescending(e => e.Timestamp),
        ("card", false) => query.OrderByDescending(e => e.CardNumber).ThenByDescending(e => e.Timestamp),
        ("door", true) => query.OrderBy(e => e.DeviceName).ThenBy(e => e.DoorName).ThenByDescending(e => e.Timestamp),
        ("door", false) => query.OrderByDescending(e => e.DeviceName).ThenByDescending(e => e.DoorName).ThenByDescending(e => e.Timestamp),
        ("kind", true) => query.OrderBy(e => e.Kind).ThenByDescending(e => e.Timestamp),
        ("kind", false) => query.OrderByDescending(e => e.Kind).ThenByDescending(e => e.Timestamp),
        (_, true) => query.OrderBy(e => e.Timestamp).ThenBy(e => e.Id),
        _ => query.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id),
    };

    /// <summary>Los filtros en palabras: van a la bitácora y al encabezado de los reportes.</summary>
    public static async Task<string> DescribeAsync(VmsDbContext db, Filter f, CancellationToken ct)
    {
        var parts = new List<string>();
        string Local(DateTime utc) => utc.ToLocalTime().ToString("dd-MM-yyyy HH:mm");
        if (f.From is { } from && f.To is { } to) parts.Add($"del {Local(from)} al {Local(to)}");
        else if (f.From is { } onlyFrom) parts.Add($"desde el {Local(onlyFrom)}");
        else if (f.To is { } onlyTo) parts.Add($"hasta el {Local(onlyTo)}");
        else parts.Add("todo el período");

        if (f.DoorIds.Count > 0)
        {
            int[] doorIds = [.. f.DoorIds];
            var names = await db.AccessDoors.AsNoTracking().Where(d => doorIds.Contains(d.Id))
                .Select(d => d.AccessDevice!.Name + " · " + d.Name).ToListAsync(ct);
            parts.Add(names.Count <= 4 ? $"puertas: {string.Join(", ", names)}" : $"{names.Count} puertas");
        }
        if (f.DeviceIds.Count > 0)
        {
            int[] deviceIds = [.. f.DeviceIds];
            var names = await db.AccessDevices.AsNoTracking().Where(d => deviceIds.Contains(d.Id))
                .Select(d => d.Name).ToListAsync(ct);
            parts.Add($"equipos: {string.Join(", ", names)}");
        }
        if (f.Kinds.Count > 0) parts.Add("resultado: " + string.Join(", ", f.Kinds.Select(AccessReports.KindLabel)));
        if (f.Credentials.Count > 0) parts.Add("credencial: " + string.Join(", ", f.Credentials.Select(AccessReports.CredentialLabel)));
        if (f.Department is { } department) parts.Add($"área: {department}");
        if (f.PersonId is int personId)
        {
            string? name = await db.AccessPersons.AsNoTracking().Where(p => p.Id == personId)
                .Select(p => p.FirstName + " " + p.LastName).FirstOrDefaultAsync(ct);
            parts.Add($"persona: {name ?? personId.ToString()}");
        }
        if (f.Q is { } term)
            parts.Add(f.Mode switch
            {
                "person" => $"nombre contiene \"{term}\"",
                "employee" => $"ID contiene \"{term}\"",
                "card" => $"tarjeta contiene \"{term}\"",
                _ => $"texto \"{term}\"",
            });
        return string.Join("; ", parts);
    }

    private static async Task<Dictionary<int, AccessReports.Person>> PeopleAsync(VmsDbContext db,
        List<AccessEvent> events, CancellationToken ct)
    {
        var ids = events.Select(e => e.AccessPersonId).OfType<int>().Distinct().ToList();
        if (ids.Count == 0) return [];
        return await db.AccessPersons.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Department, p.Position })
            .ToDictionaryAsync(p => p.Id, p => new AccessReports.Person(p.Department, p.Position), ct);
    }
}
