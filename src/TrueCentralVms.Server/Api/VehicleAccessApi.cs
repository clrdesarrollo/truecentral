using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Reports;
using TrueCentralVms.Server.Services.Workflows.Actions;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Acceso vehicular por patente:
/// <list type="bullet">
/// <item><b>Padrón de vehículos</b> (<c>/api/access/vehicles</c>): la patente es
/// una credencial de la persona, así que se administra con los permisos de
/// personas.</item>
/// <item><b>Registro de ingresos y salidas</b> (<c>/api/vehicle-access/records</c>):
/// lo que anotan las automatizaciones; se consulta con «Ver patentes» y se
/// exporta además con «Exportar registros de acceso». Alcance: el de la cámara
/// que leyó la patente.</item>
/// </list>
/// Va dentro del módulo de patentes de la licencia (decisión del usuario).
/// </summary>
public static class VehicleAccessApi
{
    private const int MaxDetailExcel = 50_000;
    private const int MaxDetailPdf = 5_000;

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    public sealed record Filter(
        DateTime? From, DateTime? To,
        IReadOnlyList<VehicleAccessDirection> Directions, IReadOnlyList<VehicleAccessResult> Results,
        string? Plate, int? PersonId, string? Q, string? AccessPoint);

    public static void MapVehicleAccessApi(this WebApplication app)
    {
        MapVehicles(app);
        MapRecords(app);
    }

    // ==================================================================
    // Padrón de vehículos
    // ==================================================================

    private static AccessVehicleDto ToDto(AccessVehicle v, AccessPerson p)
    {
        var (status, reason) = VehicleRegistry.StatusOf(v, p, DateTime.UtcNow);
        return new AccessVehicleDto(v.Id, p.Id, $"{p.FirstName} {p.LastName}".Trim(), p.EmployeeNo, p.Department, p.Enabled,
            v.Plate, v.Label, v.Brand, v.Model, v.Color, v.Notes, v.Enabled, v.ValidFrom, v.ValidTo,
            status, reason, v.CreatedAt, v.UpdatedAt);
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<string?> ValidateAsync(VmsDbContext db, AccessVehicleWriteDto request, string plate,
        int? exceptId, CancellationToken ct)
    {
        if (plate.Length is < 4 or > 10) return "La patente debe tener entre 4 y 10 letras o números.";
        if (!await db.AccessPersons.AnyAsync(p => p.Id == request.AccessPersonId, ct)) return "La persona no existe.";
        if (request.ValidFrom is { } from && request.ValidTo is { } to && to <= from)
            return "La vigencia debe terminar después de empezar.";
        if (Trimmed(request.Label) is { Length: > 64 }) return "El nombre del vehículo no puede pasar de 64 caracteres.";
        if (Trimmed(request.Notes) is { Length: > 512 }) return "Las notas no pueden pasar de 512 caracteres.";
        if ((Trimmed(request.Brand)?.Length ?? 0) > 64 || (Trimmed(request.Model)?.Length ?? 0) > 64 || (Trimmed(request.Color)?.Length ?? 0) > 32)
            return "Marca y modelo admiten hasta 64 caracteres; el color, 32.";
        var owner = await db.AccessVehicles.AsNoTracking().Include(v => v.AccessPerson)
            .Where(v => v.Plate == plate && v.Id != exceptId).FirstOrDefaultAsync(ct);
        if (owner is not null)
            return $"La patente {plate} ya está registrada a nombre de {owner.AccessPerson?.FirstName} {owner.AccessPerson?.LastName}.";
        return null;
    }

    private static string Describe(AccessVehicle v, AccessPerson p) =>
        $"{v.Plate}" + (v.Label is { } label ? $" ({label})" : "") + $" de '{p.FirstName} {p.LastName}' ({p.EmployeeNo})";

    private static void MapVehicles(WebApplication app)
    {
        app.MapGet("/api/access/vehicles", async (HttpContext ctx, VmsDbContext db, string? q, int? personId, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PersonsView, out _) is { } failure) return failure;
            var query = db.AccessVehicles.AsNoTracking().Include(v => v.AccessPerson).AsQueryable();
            if (personId is int person) query = query.Where(v => v.AccessPersonId == person);
            if (!string.IsNullOrWhiteSpace(q))
            {
                string term = q.Trim();
                string plate = VehicleRegistry.Normalize(term);
                query = query.Where(v => (plate.Length > 0 && v.Plate.Contains(plate)) ||
                                         (v.Label != null && EF.Functions.ILike(v.Label, $"%{term}%")) ||
                                         EF.Functions.ILike(v.AccessPerson!.FirstName, $"%{term}%") ||
                                         EF.Functions.ILike(v.AccessPerson!.LastName, $"%{term}%") ||
                                         EF.Functions.ILike(v.AccessPerson!.EmployeeNo, $"%{term}%"));
            }
            var vehicles = await query.OrderBy(v => v.AccessPerson!.LastName).ThenBy(v => v.AccessPerson!.FirstName)
                .ThenBy(v => v.Plate).Take(2000).ToListAsync(ct);
            return Results.Ok(vehicles.Select(v => ToDto(v, v.AccessPerson!)).ToList());
        });

        app.MapPost("/api/access/vehicles", async (HttpContext ctx, AccessVehicleWriteDto request, VmsDbContext db,
            VehicleRegistry registry, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PersonsManage, out _) is { } failure) return failure;
            string plate = VehicleRegistry.Normalize(request.Plate);
            if (await ValidateAsync(db, request, plate, null, ct) is { } invalid)
            {
                await audit.LogAsync(ctx, "vehicles", "vehicle-created", targetType: "access-vehicle", targetName: plate,
                    detail: $"No se agregó el vehículo {plate}: {invalid}", success: false);
                return Error(invalid);
            }

            var vehicle = new AccessVehicle
            {
                AccessPersonId = request.AccessPersonId,
                Plate = plate,
                Label = Trimmed(request.Label),
                Brand = Trimmed(request.Brand),
                Model = Trimmed(request.Model),
                Color = Trimmed(request.Color),
                Notes = Trimmed(request.Notes),
                Enabled = request.Enabled,
                ValidFrom = request.ValidFrom,
                ValidTo = request.ValidTo,
            };
            db.AccessVehicles.Add(vehicle);
            await db.SaveChangesAsync(ct);
            registry.Invalidate();

            var person = await db.AccessPersons.AsNoTracking().FirstAsync(p => p.Id == vehicle.AccessPersonId, ct);
            await audit.LogAsync(ctx, "vehicles", "vehicle-created",
                targetType: "access-vehicle", targetId: vehicle.Id.ToString(), targetName: vehicle.Plate,
                detail: $"Agregó al padrón el vehículo {Describe(vehicle, person)}" +
                        (vehicle.Enabled ? "." : ", deshabilitado."),
                data: new { vehicle.Plate, vehicle.AccessPersonId, vehicle.Label, vehicle.Enabled, vehicle.ValidFrom, vehicle.ValidTo });
            return Results.Ok(ToDto(vehicle, person));
        });

        app.MapPut("/api/access/vehicles/{id:int}", async (HttpContext ctx, int id, AccessVehicleWriteDto request,
            VmsDbContext db, VehicleRegistry registry, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PersonsManage, out _) is { } failure) return failure;
            var vehicle = await db.AccessVehicles.Include(v => v.AccessPerson).FirstOrDefaultAsync(v => v.Id == id, ct);
            if (vehicle is null) return Results.NotFound();
            string plate = VehicleRegistry.Normalize(request.Plate);
            if (await ValidateAsync(db, request, plate, id, ct) is { } invalid)
            {
                await audit.LogAsync(ctx, "vehicles", "vehicle-updated", targetType: "access-vehicle", targetId: id.ToString(),
                    targetName: vehicle.Plate, detail: $"No se modificó el vehículo {vehicle.Plate}: {invalid}", success: false);
                return Error(invalid);
            }

            var changes = new List<string>();
            if (vehicle.Plate != plate) changes.Add($"patente {vehicle.Plate} → {plate}");
            if (vehicle.AccessPersonId != request.AccessPersonId)
            {
                var newOwner = await db.AccessPersons.AsNoTracking().FirstAsync(p => p.Id == request.AccessPersonId, ct);
                changes.Add($"dueño '{vehicle.AccessPerson?.FirstName} {vehicle.AccessPerson?.LastName}' → '{newOwner.FirstName} {newOwner.LastName}'");
            }
            if (vehicle.Label != Trimmed(request.Label)) changes.Add($"nombre '{vehicle.Label ?? "—"}' → '{Trimmed(request.Label) ?? "—"}'");
            if (vehicle.Enabled != request.Enabled) changes.Add(request.Enabled ? "habilitado" : "deshabilitado");
            if (vehicle.ValidFrom != request.ValidFrom || vehicle.ValidTo != request.ValidTo)
                changes.Add($"vigencia {Range(vehicle.ValidFrom, vehicle.ValidTo)} → {Range(request.ValidFrom, request.ValidTo)}");
            if (vehicle.Brand != Trimmed(request.Brand) || vehicle.Model != Trimmed(request.Model) ||
                vehicle.Color != Trimmed(request.Color) || vehicle.Notes != Trimmed(request.Notes))
                changes.Add("datos del vehículo");

            vehicle.AccessPersonId = request.AccessPersonId;
            vehicle.Plate = plate;
            vehicle.Label = Trimmed(request.Label);
            vehicle.Brand = Trimmed(request.Brand);
            vehicle.Model = Trimmed(request.Model);
            vehicle.Color = Trimmed(request.Color);
            vehicle.Notes = Trimmed(request.Notes);
            vehicle.Enabled = request.Enabled;
            vehicle.ValidFrom = request.ValidFrom;
            vehicle.ValidTo = request.ValidTo;
            vehicle.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            registry.Invalidate();

            var person = await db.AccessPersons.AsNoTracking().FirstAsync(p => p.Id == vehicle.AccessPersonId, ct);
            await audit.LogAsync(ctx, "vehicles", "vehicle-updated",
                targetType: "access-vehicle", targetId: id.ToString(), targetName: vehicle.Plate,
                detail: $"Modificó el vehículo {Describe(vehicle, person)}: " +
                        (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios."));
            return Results.Ok(ToDto(vehicle, person));
        });

        app.MapDelete("/api/access/vehicles/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            VehicleRegistry registry, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PersonsManage, out _) is { } failure) return failure;
            var vehicle = await db.AccessVehicles.Include(v => v.AccessPerson).FirstOrDefaultAsync(v => v.Id == id, ct);
            if (vehicle is null) return Results.NotFound();
            string description = Describe(vehicle, vehicle.AccessPerson!);
            db.AccessVehicles.Remove(vehicle);
            await db.SaveChangesAsync(ct);
            registry.Invalidate();
            await audit.LogAsync(ctx, "vehicles", "vehicle-deleted",
                targetType: "access-vehicle", targetId: id.ToString(), targetName: vehicle.Plate,
                detail: $"Eliminó del padrón el vehículo {description}. Sus pasos ya registrados se conservan.");
            return Results.Ok();
        });
    }

    private static string Range(DateTime? from, DateTime? to) =>
        from is null && to is null ? "sin límite"
            : $"{(from is { } f ? f.ToLocalTime().ToString("dd-MM-yyyy") : "…")}–{(to is { } t ? t.ToLocalTime().ToString("dd-MM-yyyy") : "…")}";

    // ==================================================================
    // Registro de ingresos y salidas
    // ==================================================================

    private static VehicleAccessRecordDto ToDto(VehicleAccessRecord r) => new(
        r.Id, r.At, r.AccessPoint, r.Direction, r.Result, r.Plate, r.Confidence, r.PlateStatus,
        r.DeviceId, r.ChannelId, r.CameraName, r.AccessVehicleId, r.VehicleLabel, r.AccessPersonId, r.PersonName,
        r.EmployeeNo, r.Department, r.WorkflowName, r.Detail, r.PlateImagePath is not null);

    private static void MapRecords(WebApplication app)
    {
        app.MapGet("/api/vehicle-access/records", async (HttpContext ctx, VmsDbContext db, AuditService audit,
            int? page, int? pageSize, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.AnprView, out var session) is { } failure) return failure;
            int pageNumber = Math.Max(page ?? 1, 1);
            int size = Math.Clamp(pageSize is null or 0 ? 50 : pageSize.Value, 1, 500);
            var filter = ReadFilter(ctx.Request.Query);
            var query = Scoped(db, Build(db, filter), await ctx.ScopeAsync(session));

            int total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(r => r.At).ThenByDescending(r => r.Id)
                .Skip((pageNumber - 1) * size).Take(size).ToListAsync(ct);

            // Patentes y nombres son datos personales: la búsqueda con filtros se
            // audita; el refresco automático de la pantalla, no.
            if (pageNumber == 1 && HasUserFilters(filter))
                await audit.LogAsync(ctx, "vehicles", "records-search",
                    detail: $"Consultó el registro de accesos vehiculares ({total} resultado(s)): {await DescribeAsync(db, filter, ct)}.");

            return Results.Ok(new VehicleAccessRecordPageDto(total, pageNumber, size, items.Select(ToDto).ToList()));
        });

        app.MapGet("/api/vehicle-access/access-points", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.AnprView, out _) is { } failure) return failure;
            return Results.Ok(await db.VehicleAccessRecords.AsNoTracking().Select(r => r.AccessPoint).Distinct()
                .OrderBy(a => a).Take(200).ToListAsync(ct));
        });

        app.MapGet("/api/vehicle-access/records/{id:long}/plate", async (HttpContext ctx, long id, VmsDbContext db,
            AnprStore store, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.AnprView, out var session) is { } failure) return failure;
            var record = await Scoped(db, db.VehicleAccessRecords.AsNoTracking().Where(r => r.Id == id),
                    await ctx.ScopeAsync(session))
                .Select(r => new { r.PlateImagePath }).FirstOrDefaultAsync(ct);
            if (record is null) return Error("El registro no existe.", StatusCodes.Status404NotFound);
            if (store.FullPath(record.PlateImagePath) is not { } path || !File.Exists(path))
                return Error("El registro no tiene foto de la patente.", StatusCodes.Status404NotFound);
            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(path, "image/jpeg");
        });

        app.MapGet("/api/vehicle-access/records/export", async (HttpContext ctx, VmsDbContext db, AuditService audit,
            IWebHostEnvironment env, string? format, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.AnprView, out var session) is { } failure) return failure;
            if (ApiSecurity.Require(ctx, Permissions.AccessRecordsExport, out _) is { } denied) return denied;
            bool pdf = string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase);
            var filter = ReadFilter(ctx.Request.Query);
            var query = Scoped(db, Build(db, filter), await ctx.ScopeAsync(session));
            string filtersText = await DescribeAsync(db, filter, ct);
            int total = await query.CountAsync(ct);
            int limit = pdf ? MaxDetailPdf : MaxDetailExcel;
            var records = await query.OrderBy(r => r.At).ThenBy(r => r.Id).Take(limit).ToListAsync(ct);

            var meta = new ReportMeta(session.Username, DateTime.Now, filtersText,
                total > limit ? $"Se incluyen los primeros {limit:N0} de {total:N0} registros: acote los filtros para ver el resto." : null);
            var table = Detail(records, meta);
            byte[] content;
            try
            {
                content = pdf ? PdfReportWriter.Write(table, Path.Combine(env.WebRootPath, "logo.png")) : XlsxWriter.Write(table);
            }
            catch (Exception ex)
            {
                await audit.LogAsync(ctx, "vehicles", "report-exported",
                    detail: $"No se pudo generar el registro de accesos vehiculares en {(pdf ? "PDF" : "Excel")}: {ex.Message}",
                    success: false);
                return Error($"No se pudo generar el reporte: {ex.Message}", StatusCodes.Status500InternalServerError);
            }
            await audit.LogAsync(ctx, "vehicles", "report-exported",
                detail: $"Exportó el registro de accesos vehiculares en {(pdf ? "PDF" : "Excel")} " +
                        $"({table.Rows.Count:N0} de {total:N0} registro(s)): {filtersText}.",
                data: new { format = pdf ? "pdf" : "xlsx", rows = table.Rows.Count, total });
            string file = $"{table.FileName}_{DateTime.Now:yyyyMMdd_HHmmss}";
            return pdf
                ? Results.File(content, "application/pdf", file + ".pdf")
                : Results.File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file + ".xlsx");
        });
    }

    public static Filter ReadFilter(IQueryCollection q)
    {
        static DateTime? Date(string? text) =>
            DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var value) ? value : null;
        static List<T> Enums<T>(IQueryCollection q, string name) where T : struct, Enum =>
            q[name].ToArray().SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(v => Enum.TryParse<T>(v, true, out var e) ? e : (T?)null).OfType<T>().Distinct().ToList();
        static string? Text(IQueryCollection q, string name) => q[name].ToString() is { Length: > 0 } t ? t.Trim() : null;
        return new Filter(Date(q["from"]), Date(q["to"]),
            Enums<VehicleAccessDirection>(q, "directions"), Enums<VehicleAccessResult>(q, "results"),
            Text(q, "plate"), int.TryParse(q["personId"], out int person) ? person : null,
            Text(q, "q"), Text(q, "accessPoint"));
    }

    private static bool HasUserFilters(Filter f) =>
        f.From is not null || f.To is not null || f.Directions.Count > 0 || f.Results.Count > 0 ||
        f.Plate is not null || f.PersonId is not null || f.Q is not null || f.AccessPoint is not null;

    private static IQueryable<VehicleAccessRecord> Build(VmsDbContext db, Filter f)
    {
        VehicleAccessDirection[] directions = [.. f.Directions];
        VehicleAccessResult[] results = [.. f.Results];
        var query = db.VehicleAccessRecords.AsNoTracking().AsQueryable();
        if (f.From is { } from) query = query.Where(r => r.At >= from);
        if (f.To is { } to) query = query.Where(r => r.At <= to);
        if (directions.Length > 0) query = query.Where(r => directions.Contains(r.Direction));
        if (results.Length > 0) query = query.Where(r => results.Contains(r.Result));
        if (f.PersonId is int person) query = query.Where(r => r.AccessPersonId == person);
        if (f.AccessPoint is { } point) query = query.Where(r => r.AccessPoint == point);
        if (f.Plate is { } plateText && VehicleRegistry.Normalize(plateText) is { Length: > 0 } plate)
            query = query.Where(r => r.Plate.Contains(plate));
        if (f.Q is { } term)
        {
            string like = $"%{term}%";
            query = query.Where(r => (r.PersonName != null && EF.Functions.ILike(r.PersonName, like)) ||
                                     (r.EmployeeNo != null && EF.Functions.ILike(r.EmployeeNo, like)) ||
                                     (r.Department != null && EF.Functions.ILike(r.Department, like)) ||
                                     (r.VehicleLabel != null && EF.Functions.ILike(r.VehicleLabel, like)) ||
                                     EF.Functions.ILike(r.Plate, like));
        }
        return query;
    }

    /// <summary>Alcance por ubicación: solo los pasos leídos por cámaras que el usuario puede ver.</summary>
    private static IQueryable<VehicleAccessRecord> Scoped(VmsDbContext db, IQueryable<VehicleAccessRecord> query, UserScope scope)
    {
        if (!scope.FiltersView) return query;
        var allowed = scope.AllowedIds(ResourceKind.Camera).ToList();
        return query.Where(r => r.ChannelId != null && allowed.Contains(r.ChannelId.Value));
    }

    private static async Task<string> DescribeAsync(VmsDbContext db, Filter f, CancellationToken ct)
    {
        var parts = new List<string>();
        string Local(DateTime utc) => utc.ToLocalTime().ToString("dd-MM-yyyy HH:mm");
        if (f.From is { } from && f.To is { } to) parts.Add($"del {Local(from)} al {Local(to)}");
        else if (f.From is { } onlyFrom) parts.Add($"desde el {Local(onlyFrom)}");
        else if (f.To is { } onlyTo) parts.Add($"hasta el {Local(onlyTo)}");
        else parts.Add("todo el período");
        if (f.AccessPoint is { } point) parts.Add($"acceso: {point}");
        if (f.Directions.Count > 0) parts.Add("sentido: " + string.Join(", ", f.Directions.Select(VehicleAccessAction.DirectionLabel)));
        if (f.Results.Count > 0) parts.Add("resultado: " + string.Join(", ", f.Results.Select(VehicleAccessAction.ResultLabel)));
        if (f.Plate is { } plate) parts.Add($"patente contiene \"{plate}\"");
        if (f.PersonId is int personId)
        {
            string? name = await db.AccessPersons.AsNoTracking().Where(p => p.Id == personId)
                .Select(p => p.FirstName + " " + p.LastName).FirstOrDefaultAsync(ct);
            parts.Add($"persona: {name ?? personId.ToString()}");
        }
        if (f.Q is { } term) parts.Add($"texto \"{term}\"");
        return string.Join("; ", parts);
    }

    private static ReportTable Detail(List<VehicleAccessRecord> records, ReportMeta meta)
    {
        var columns = new List<ReportColumn>
        {
            new("Fecha y hora", ReportCellKind.DateTime, 2.9, 20),
            new("Acceso", ReportCellKind.Text, 2.8, 20),
            new("Sentido", ReportCellKind.Text, 1.6, 10),
            new("Patente", ReportCellKind.Text, 1.8, 12),
            new("Vehículo", ReportCellKind.Text, 2.4, 18),
            new("Persona", ReportCellKind.Text, 3.6, 28),
            new("ID", ReportCellKind.Text, 1.6, 12),
            new("Área", ReportCellKind.Text, 2.4, 18),
            new("Padrón", ReportCellKind.Text, 2.0, 14),
            new("Resultado", ReportCellKind.Result, 3.2, 26),
            new("Cámara", ReportCellKind.Text, 3.4, 28),
        };
        var rows = records.Select(r => new object?[]
        {
            r.At.ToLocalTime(),
            r.AccessPoint,
            VehicleAccessAction.DirectionLabel(r.Direction),
            r.Plate,
            r.VehicleLabel ?? "",
            r.PersonName ?? "",
            r.EmployeeNo ?? "",
            r.Department ?? "",
            PlateRegistryStatuses.Label(r.PlateStatus),
            VehicleAccessAction.ResultLabel(r.Result),
            r.CameraName,
        }).ToList();
        var totals = new List<(string, string)>
        {
            ("Registros", records.Count.ToString("N0")),
            ("Ingresos", records.Count(r => r.Direction == VehicleAccessDirection.Entry).ToString("N0")),
            ("Salidas", records.Count(r => r.Direction == VehicleAccessDirection.Exit).ToString("N0")),
            ("Aperturas automáticas", records.Count(r => r.Result == VehicleAccessResult.AutoOpened).ToString("N0")),
            ("Rechazados", records.Count(r => r.Result == VehicleAccessResult.Rejected).ToString("N0")),
            ("Vehículos distintos", records.Select(r => r.Plate).Distinct().Count().ToString("N0")),
        };
        return new ReportTable("Registro de accesos vehiculares", "accesos_vehiculares", meta, columns, rows, totals);
    }
}
