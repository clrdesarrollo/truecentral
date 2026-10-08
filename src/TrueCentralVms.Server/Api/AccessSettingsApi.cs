using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Configuración PROPIA de un equipo de control de acceso (parámetros de sus
/// puertas y de sus lectores), para la página del equipo. El VMS no guarda
/// nada de esto: cada lectura va al equipo y cada cambio se le escribe y se
/// relee. Solo administradores, igual que el resto del mantenedor.
/// </summary>
public static class AccessSettingsApi
{
    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    public static void MapAccessSettingsApi(this WebApplication app)
    {
        app.MapGet("/api/access/devices/{id:int}/settings", async (HttpContext ctx, int id, VmsDbContext db,
            IEnumerable<IAccessDeviceSettingsProvider> providers, AccessControlService service, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var device = await db.AccessDevices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
            if (device is null) return Results.NotFound();
            var provider = providers.FirstOrDefault(p => p.DriverKey.Equals(device.DriverKey, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
                return Error("Los equipos de esta marca todavía no se configuran desde el VMS: use la página web del equipo.",
                    StatusCodes.Status501NotImplemented);
            try
            {
                return Results.Ok(await provider.ReadAsync(service.ConnectionOf(device), ct));
            }
            catch (DriverException ex)
            {
                return Error(ex.Message, StatusCodes.Status502BadGateway);
            }
        });

        app.MapPut("/api/access/devices/{id:int}/settings/{section}", async (HttpContext ctx, int id, string section,
            AccessSettingsWriteDto request, VmsDbContext db, IEnumerable<IAccessDeviceSettingsProvider> providers,
            AccessControlService service, AuditService audit, IHubContext<VmsHub> hub, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (request?.Values is null || request.Values.Count == 0) return Error("No hay cambios que guardar.");
            var device = await db.AccessDevices.Include(d => d.Doors).FirstOrDefaultAsync(d => d.Id == id, ct);
            if (device is null) return Results.NotFound();
            var provider = providers.FirstOrDefault(p => p.DriverKey.Equals(device.DriverKey, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
                return Error("Los equipos de esta marca todavía no se configuran desde el VMS.", StatusCodes.Status501NotImplemented);

            // En la bitácora van las claves tocadas y sus valores, menos las
            // contraseñas del equipo (coacción, maestra), que no se anotan.
            string Described(KeyValuePair<string, string?> pair) =>
                pair.Key.Contains("password", StringComparison.OrdinalIgnoreCase)
                    ? $"{pair.Key} (cambiada)"
                    : $"{pair.Key} = {pair.Value ?? "—"}";
            string changes = string.Join(", ", request.Values.Select(Described));

            AccessSettingsSectionDto result;
            try
            {
                result = await provider.ApplyAsync(service.ConnectionOf(device), section, request.Values, ct);
            }
            catch (DriverException ex)
            {
                await audit.LogAsync(ctx, "access", "device-settings-updated",
                    targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                    detail: $"El equipo '{device.Name}' no aceptó cambios en {section}: {ex.Message} ({changes})",
                    success: false);
                return Error(ex.Message, StatusCodes.Status502BadGateway);
            }

            // Si cambió el nombre de una puerta en el equipo, el VMS lo sigue
            // mientras nadie la haya renombrado en Recursos (mismo criterio que
            // la revalidación).
            bool renamed = false;
            if (result.Kind == "door" && result.Subtitle is { Length: > 0 } doorName &&
                device.Doors.FirstOrDefault(d => d.Number == result.Number) is { NameFromDevice: true } door && door.Name != doorName)
            {
                door.Name = doorName;
                door.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                renamed = true;
            }

            await audit.LogAsync(ctx, "access", "device-settings-updated",
                targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                detail: $"Cambió la configuración del equipo '{device.Name}' ({device.Host}), {result.Title.ToLowerInvariant()}: {changes}.",
                data: new { section, values = request.Values.Where(v => !v.Key.Contains("password", StringComparison.OrdinalIgnoreCase)) });
            if (renamed)
            {
                EquipmentLocation.Changed(ctx);
                await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "access-devices", cancellationToken: ct);
            }
            return Results.Ok(result);
        });
    }
}
