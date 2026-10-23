using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Fleet;
using TrueCentralVms.Server.Services.Licensing;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Flota, fase 4: eventos de los vehículos (lista, pendientes y acuse) y geocercas (las ve quien ve el mapa; las edita quien
/// administra la flota). Los eventos se recortan al alcance: cada uno es de un vehículo y se ve si se ve su grabador.
/// </summary>
public static class FleetEventsApi
{
    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    public static void MapFleetEventsApi(this WebApplication app)
    {
        // Eventos: por vehículo y rango (recorridos) o los últimos de toda la flota (mapa). pending=true: solo los por acusar.
        app.MapGet("/api/fleet/events", async (HttpContext ctx, int? vehicleId, DateTime? from, DateTime? to, bool? pending, int? take,
            VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.FleetView, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var visible = (await db.Vehicles.AsNoTracking().Select(v => new { v.Id, v.DeviceId, v.Code }).ToListAsync(ct))
                .Where(v => scope.CanViewDevice(v.DeviceId)).ToDictionary(v => v.Id);
            if (vehicleId is int vid && !visible.ContainsKey(vid)) return Results.NotFound();
            var ids = vehicleId is int one ? [one] : visible.Keys.ToList();

            var q = db.FleetEvents.AsNoTracking().Where(e => ids.Contains(e.VehicleId));
            if (from is { } f) { var fu = f.ToUniversalTime(); q = q.Where(e => e.At >= fu); }
            if (to is { } t) { var tu = t.ToUniversalTime(); q = q.Where(e => e.At < tu); }
            int pendingCount = await db.FleetEvents.CountAsync(e => ids.Contains(e.VehicleId) && e.AcknowledgedAt == null, ct);
            if (pending == true) q = q.Where(e => e.AcknowledgedAt == null);
            int total = await q.CountAsync(ct);
            var rows = await q.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Take(Math.Clamp(take ?? 200, 1, 2000)).ToListAsync(ct);
            return Results.Ok(new FleetEventPageDto(
                rows.Select(e => FleetEventService.ToDto(e, visible[e.VehicleId].Code, visible[e.VehicleId].DeviceId)).ToList(),
                total, pendingCount));
        });

        // Acusar (atender) un evento: queda quién y cuándo, con una nota opcional.
        app.MapPost("/api/fleet/events/{id:long}/ack", async (HttpContext ctx, long id, FleetEventAckDto? request, VmsDbContext db,
            AuditService audit, ScopedHub hub, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.FleetView, out var session) is { } failure) return failure;
            var e = await db.FleetEvents.Include(x => x.Vehicle).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (e is null) return Results.NotFound();
            if (!(await ctx.ScopeAsync(session)).CanViewDevice(e.Vehicle.DeviceId))
                return await ctx.OutOfScopeAsync(session, "vehicle", e.VehicleId.ToString(), e.Vehicle.Code, "atender sus eventos");
            if (request?.Note is { Length: > 512 }) return Error("La nota admite hasta 512 caracteres.");
            if (e.AcknowledgedAt is null)
            {
                e.AcknowledgedAt = DateTime.UtcNow;
                e.AcknowledgedByUserId = session.UserId;
                e.AcknowledgedBy = session.Username;
                e.AckNote = string.IsNullOrWhiteSpace(request?.Note) ? null : request!.Note!.Trim();
                await db.SaveChangesAsync(ct);
                await audit.LogAsync(ctx, "fleet", "event-acknowledged", "vehicle", e.VehicleId.ToString(), e.Vehicle.Code,
                    $"Atendió «{FleetEventKinds.LabelOf(e.Kind)}» de {e.Vehicle.Code} ({DateTime.SpecifyKind(e.At, DateTimeKind.Utc).ToLocalTime():yyyy-MM-dd HH:mm:ss})" +
                    (e.AckNote is { } note ? $": {note}" : "."));
            }
            var dto = FleetEventService.ToDto(e, e.Vehicle.Code, e.Vehicle.DeviceId);
            await hub.SendAsync(VmsHubContract.FleetEventAcknowledged, dto, s => s.CanViewDevice(dto.DeviceId), ct);
            return Results.Ok(dto);
        });

        // ---------------- geocercas ----------------

        app.MapGet("/api/fleet/geofences", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAny(ctx, out _, Permissions.FleetView, Permissions.FleetManage) is { } failure) return failure;
            var rows = await db.Geofences.AsNoTracking().OrderBy(f => f.Name).ToListAsync(ct);
            return Results.Ok(rows.Select(ToDto));
        });

        app.MapPost("/api/fleet/geofences", async (HttpContext ctx, GeofenceWriteDto request, VmsDbContext db, LicenseService license,
            FleetEventService events, AuditService audit, IHubContext<VmsHub> hub, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.FleetManage, out _) is { } failure) return failure;
            if (license.Deny(LicenseFeatures.ModuleFleet, null, 0, 0) is { } noModule)
                return await license.DenyAsync(ctx, noModule, "geofence", request.Name?.Trim());
            if (await ValidateAsync(request, db, null, ct) is { } error) return Error(error);
            var f = new Geofence();
            Apply(f, request);
            db.Geofences.Add(f);
            await db.SaveChangesAsync(ct);
            events.Invalidate();
            await audit.LogAsync(ctx, "fleet", "geofence-created", "geofence", f.Id.ToString(), f.Name, $"Creó la geocerca «{f.Name}» ({Describe(f)}).");
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "fleet-geofences", cancellationToken: ct);
            return Results.Ok(ToDto(f));
        });

        app.MapPut("/api/fleet/geofences/{id:int}", async (HttpContext ctx, int id, GeofenceWriteDto request, VmsDbContext db,
            FleetEventService events, AuditService audit, IHubContext<VmsHub> hub, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.FleetManage, out _) is { } failure) return failure;
            var f = await db.Geofences.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (f is null) return Results.NotFound();
            if (await ValidateAsync(request, db, id, ct) is { } error) return Error(error);
            string before = Describe(f);
            Apply(f, request);
            f.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            events.Invalidate();
            await audit.LogAsync(ctx, "fleet", "geofence-updated", "geofence", f.Id.ToString(), f.Name,
                $"Modificó la geocerca «{f.Name}»: {Describe(f)} (antes: {before}).");
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "fleet-geofences", cancellationToken: ct);
            return Results.Ok(ToDto(f));
        });

        app.MapDelete("/api/fleet/geofences/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, FleetEventService events,
            AuditService audit, IHubContext<VmsHub> hub, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.FleetManage, out _) is { } failure) return failure;
            var f = await db.Geofences.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (f is null) return Results.NotFound();
            db.Geofences.Remove(f);
            await db.SaveChangesAsync(ct);
            events.Invalidate();
            await audit.LogAsync(ctx, "fleet", "geofence-deleted", "geofence", f.Id.ToString(), f.Name, $"Eliminó la geocerca «{f.Name}».");
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "fleet-geofences", cancellationToken: ct);
            return Results.NoContent();
        });
    }

    private static async Task<string?> ValidateAsync(GeofenceWriteDto r, VmsDbContext db, int? exceptId, CancellationToken ct)
    {
        string name = r.Name?.Trim() ?? "";
        if (name.Length is 0 or > 80) return "El nombre es obligatorio y admite hasta 80 caracteres.";
        if (await db.Geofences.AnyAsync(f => f.Name == name && f.Id != exceptId, ct)) return $"Ya existe una geocerca «{name}».";
        if (r.Kind is not ("polygon" or "circle" or "corridor")) return "Tipo de geocerca desconocido.";
        var pts = (r.Points ?? []).ToArray();
        if (pts.Length > 500) return "La geocerca admite hasta 500 puntos.";
        if (!Geo.IsValid(r.Kind, pts, r.RadiusM))
            return r.Kind switch
            {
                "polygon" => "El polígono necesita al menos 3 vértices válidos.",
                "circle" => "El círculo necesita su centro y un radio entre 1 m y 500 km.",
                _ => "El corredor necesita al menos 2 puntos y un ancho entre 1 m y 10 km.",
            };
        if (r.SpeedLimitKmh is < 1 or > 300) return "La velocidad máxima va de 1 a 300 km/h.";
        if (r.Color is { Length: > 0 } c && !System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9a-fA-F]{6}$")) return "Color inválido.";
        return null;
    }

    private static void Apply(Geofence f, GeofenceWriteDto r)
    {
        f.Name = r.Name.Trim();
        f.Kind = r.Kind;
        f.PointsJson = JsonSerializer.Serialize(r.Points.Select(p => new[] { Math.Round(p[0], 7), Math.Round(p[1], 7) }));
        f.RadiusM = r.Kind == "polygon" ? 0 : Math.Round(r.RadiusM, 1);
        f.Color = r.Color is { Length: > 0 } c ? c : "#3b82f6";
        f.AlertOnEnter = r.AlertOnEnter;
        f.AlertOnExit = r.AlertOnExit;
        f.SpeedLimitKmh = r.SpeedLimitKmh;
        f.VehicleIdsJson = JsonSerializer.Serialize((r.VehicleIds ?? []).Distinct().Order());
        f.Enabled = r.Enabled;
    }

    private static GeofenceDto ToDto(Geofence f) => new(f.Id, f.Name, f.Kind,
        JsonSerializer.Deserialize<double[][]>(f.PointsJson) ?? [], f.RadiusM, f.Color, f.AlertOnEnter, f.AlertOnExit, f.SpeedLimitKmh,
        JsonSerializer.Deserialize<int[]>(f.VehicleIdsJson) ?? [], f.Enabled);

    private static string Describe(Geofence f)
    {
        string shape = f.Kind switch
        {
            "circle" => $"círculo de {f.RadiusM:0} m",
            "corridor" => $"corredor de {f.RadiusM:0} m de ancho",
            _ => $"polígono de {(JsonSerializer.Deserialize<double[][]>(f.PointsJson) ?? []).Length} vértices",
        };
        var alerts = new List<string>();
        if (f.AlertOnEnter) alerts.Add("avisa al entrar");
        if (f.AlertOnExit) alerts.Add("avisa al salir");
        if (f.SpeedLimitKmh is int s) alerts.Add($"máximo {s} km/h");
        return $"{shape}{(alerts.Count > 0 ? ", " + string.Join(", ", alerts) : "")}{(f.Enabled ? "" : ", deshabilitada")}";
    }
}
