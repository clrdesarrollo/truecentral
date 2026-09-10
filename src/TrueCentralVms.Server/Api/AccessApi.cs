using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Licensing;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// API del módulo Control de acceso. Primera etapa: el ADMINISTRADOR DE
/// DISPOSITIVOS — alta, edición, prueba de conexión, revalidación y baja de
/// terminales y controladoras, con las puertas que declara cada equipo (que
/// son las que cuenta la licencia).
///
/// Al dar de alta se valida SIEMPRE contra el equipo: si no contesta, si las
/// credenciales no sirven o si no es un equipo de control de acceso, no se
/// guarda nada (mismo criterio que fuentes de video, paneles y parlantes).
/// </summary>
public static class AccessApi
{
    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    private static string? ValidateWrite(AccessDeviceWriteDto request, AccessDriverRegistry drivers)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
            return "El nombre es obligatorio (máximo 128 caracteres).";
        if (string.IsNullOrWhiteSpace(request.Host))
            return "La dirección (IP o hostname) es obligatoria.";
        if (request.Port is < 1 or > 65535)
            return "El puerto debe estar entre 1 y 65535.";
        if (request.Location is { Length: > 128 })
            return "La ubicación no puede superar los 128 caracteres.";
        if (drivers.Find(request.DriverKey) is not { } factory)
            return $"Driver desconocido: '{request.DriverKey}'.";
        // ZKTeco no tiene usuario: su credencial es la clave de comunicación,
        // que es un número (0 = el equipo no tiene ninguna).
        if (factory.AuthMode == AccessAuthMode.UserPassword && string.IsNullOrWhiteSpace(request.Username))
            return "El usuario del equipo es obligatorio.";
        if (factory.AuthMode == AccessAuthMode.CommKey && !string.IsNullOrWhiteSpace(request.Password) &&
            (!int.TryParse(request.Password.Trim(), out int commKey) || commKey < 0))
            return "La clave de comunicación es un número (0 si el equipo no tiene).";
        return null;
    }

    private static async Task<(AccessDeviceInfo? Info, string? Error)> ProbeAsync(AccessDriverRegistry drivers,
        string driverKey, AccessConnectionInfo conn, CancellationToken ct)
    {
        try { return (await drivers.Find(driverKey)!.Create().ProbeAsync(conn, ct), null); }
        catch (DriverException ex) { return (null, ex.Message); }
        catch (Exception ex) { return (null, $"Error inesperado al comunicarse con el equipo: {ex.Message}"); }
    }

    private static string? LocationOf(AccessDeviceWriteDto request) =>
        string.IsNullOrWhiteSpace(request.Location) ? null : request.Location.Trim();

    private static void ApplyProbe(AccessDevice device, AccessDeviceInfo info)
    {
        device.Kind = info.Kind;
        device.Model = info.Model;
        device.SerialNumber = info.SerialNumber;
        device.FirmwareVersion = info.FirmwareVersion;
        device.MacAddress = info.MacAddress;
        device.SupportsRemoteControl = info.Capabilities.SupportsRemoteControl;
        device.SupportsEvents = info.Capabilities.SupportsEvents;
        device.SupportsCards = info.Capabilities.SupportsCards;
        device.SupportsFingerprint = info.Capabilities.SupportsFingerprint;
        device.SupportsFace = info.Capabilities.SupportsFace;
        device.UserCapacity = info.Capabilities.UserCapacity;
        device.CardCapacity = info.Capabilities.CardCapacity;
        device.Status = AccessDeviceStatus.Online;
        device.LastError = null;
        device.LastSeenAt = DateTime.UtcNow;
        device.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Deja las puertas guardadas iguales a las que declara el equipo: agrega
    /// las nuevas, actualiza el nombre de las conocidas y borra las que el
    /// equipo dejó de administrar (cambio de firmware o de modelo en la misma
    /// dirección). Devuelve un resumen para la bitácora.
    /// </summary>
    private static string SyncDoors(AccessDevice device, IReadOnlyList<AccessDoorInfo> doors)
    {
        int added = 0, renamed = 0;
        foreach (var door in doors)
        {
            var existing = device.Doors.FirstOrDefault(d => d.Number == door.Number);
            if (existing is null)
            {
                device.Doors.Add(new AccessDoor { Number = door.Number, Name = door.Name });
                added++;
            }
            else if (existing.NameFromDevice && existing.Name != door.Name)
            {
                // Sólo se acepta el nombre del equipo mientras nadie lo haya
                // cambiado en el VMS: el del operador es el que se ve en la
                // planilla del turno y no debe volver a "Door1" solo.
                existing.Name = door.Name;
                existing.UpdatedAt = DateTime.UtcNow;
                renamed++;
            }
        }
        var gone = device.Doors.Where(d => doors.All(x => x.Number != d.Number)).ToList();
        foreach (var door in gone) device.Doors.Remove(door);

        var parts = new List<string>();
        if (added > 0) parts.Add($"{added} puerta(s) agregada(s)");
        if (renamed > 0) parts.Add($"{renamed} renombrada(s)");
        if (gone.Count > 0) parts.Add($"{gone.Count} eliminada(s)");
        return parts.Count > 0 ? string.Join(", ", parts) : "sin cambios en las puertas";
    }

    /// <summary>Puertas habilitadas de los demás equipos: lo que ya consume el cupo de la licencia.</summary>
    private static Task<int> DoorsInUseAsync(VmsDbContext db, int? exceptDeviceId, CancellationToken ct) =>
        db.AccessDoors.CountAsync(d => d.Enabled && d.AccessDevice!.Enabled &&
                                       (exceptDeviceId == null || d.AccessDeviceId != exceptDeviceId), ct);

    public static void MapAccessApi(this WebApplication app)
    {
        // ------------------------------------------------------------------
        // Catálogo de drivers
        // ------------------------------------------------------------------
        app.MapGet("/api/access/drivers", (HttpContext ctx, AccessDriverRegistry drivers) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.Ok(drivers.All.Select(f =>
                new AccessDriverDto(f.DriverKey, f.DisplayName, f.DefaultPort, f.DefaultHttps, f.AuthMode, f.Hint,
                    f.SupportsPersonSync)));
        });

        // ------------------------------------------------------------------
        // Mantenedor
        // ------------------------------------------------------------------
        app.MapGet("/api/access/devices", async (HttpContext ctx, VmsDbContext db, AccessControlService service, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var devices = await db.AccessDevices.AsNoTracking().Include(d => d.Doors)
                .OrderBy(d => d.Location).ThenBy(d => d.Name).ToListAsync(ct);
            return Results.Ok(devices.Select(service.ToDto));
        });

        app.MapGet("/api/access/devices/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            AccessControlService service, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var device = await db.AccessDevices.AsNoTracking().Include(d => d.Doors).FirstOrDefaultAsync(d => d.Id == id, ct);
            return device is null ? Results.NotFound() : Results.Ok(service.ToDto(device));
        });

        app.MapPost("/api/access/devices", async (HttpContext ctx, AccessDeviceWriteDto request, VmsDbContext db,
            AccessDriverRegistry drivers, LicenseService license, CredentialProtector protector, IHubContext<VmsHub> hub,
            AccessControlService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            // Con clave de comunicación, vacío es un valor válido: significa 0.
            if (drivers.Find(request.DriverKey)!.AuthMode == AccessAuthMode.UserPassword &&
                string.IsNullOrEmpty(request.Password))
                return Error("La contraseña del equipo es obligatoria.");
            // El módulo se verifica antes de tocar el equipo; el cupo de puertas,
            // después del sondeo (recién ahí se sabe cuántas administra).
            if (license.Deny(LicenseFeatures.ModuleAccess, null, 0) is { } moduleDenied)
                return await license.DenyAsync(ctx, moduleDenied, "access-device", request.Name?.Trim());

            string host = request.Host.Trim();
            if (await db.AccessDevices.AnyAsync(d => d.Host == host && d.Port == request.Port, ct))
                return Error("Ya existe un equipo de control de acceso con esa dirección y puerto.", StatusCodes.Status409Conflict);

            var conn = new AccessConnectionInfo(host, request.Port, request.UseHttps, request.Username, request.Password);
            var (info, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
            if (info is null)
            {
                await audit.LogAsync(ctx, "access", "device-created", targetType: "access-device", targetName: request.Name.Trim(),
                    detail: $"Alta de equipo de control de acceso rechazada por el equipo {host}:{request.Port}: {probeError}",
                    success: false);
                return Error(probeError!);
            }
            if (request.Enabled &&
                license.Deny(LicenseFeatures.ModuleAccess, LicenseFeatures.AccessDoors,
                    await DoorsInUseAsync(db, null, ct), info.Doors.Count) is { } denied)
                return await license.DenyAsync(ctx, denied, "access-device", request.Name?.Trim());

            var device = new AccessDevice
            {
                Name = request.Name.Trim(),
                DriverKey = request.DriverKey,
                Host = host,
                Port = request.Port,
                UseHttps = request.UseHttps,
                Username = request.Username,
                PasswordCiphertext = protector.Protect(request.Password),
                Location = LocationOf(request),
                Enabled = request.Enabled,
            };
            ApplyProbe(device, info);
            SyncDoors(device, info.Doors);
            db.AccessDevices.Add(device);
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "access", "device-created",
                targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                detail: $"Agregó el equipo de control de acceso '{device.Name}' ({device.DriverKey}, {device.Host}:{device.Port}, " +
                        $"{device.Model ?? "modelo desconocido"}" +
                        (device.Location is null ? "" : $", ubicación '{device.Location}'") +
                        $", {device.Doors.Count} puerta(s)).",
                data: new { device.Host, device.Port, device.UseHttps, device.DriverKey, device.Model, device.SerialNumber, device.Location });
            service.RequestReconcile();
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "access-devices", cancellationToken: ct);
            return Results.Ok(service.ToDto(device));
        });

        app.MapPut("/api/access/devices/{id:int}", async (HttpContext ctx, int id, AccessDeviceWriteDto request, VmsDbContext db,
            AccessDriverRegistry drivers, LicenseService license, CredentialProtector protector, IHubContext<VmsHub> hub,
            AccessControlService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            var device = await db.AccessDevices.Include(d => d.Doors).FirstOrDefaultAsync(d => d.Id == id, ct);
            if (device is null) return Results.NotFound();

            string host = request.Host.Trim();
            if (await db.AccessDevices.AnyAsync(d => d.Host == host && d.Port == request.Port && d.Id != id, ct))
                return Error("Ya existe otro equipo de control de acceso con esa dirección y puerto.", StatusCodes.Status409Conflict);

            string password = string.IsNullOrEmpty(request.Password)
                ? protector.Unprotect(device.PasswordCiphertext)
                : request.Password;

            // Si cambió cómo se llega al equipo, hay que revalidar contra él.
            bool reachChanged = device.Host != host || device.Port != request.Port || device.UseHttps != request.UseHttps ||
                                device.Username != request.Username || !string.IsNullOrEmpty(request.Password) ||
                                device.DriverKey != request.DriverKey;
            AccessDeviceInfo? info = null;
            if (reachChanged)
            {
                var conn = new AccessConnectionInfo(host, request.Port, request.UseHttps, request.Username, password);
                string? probeError;
                (info, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
                if (info is null) return Error(probeError!);
            }
            if (!device.Enabled && request.Enabled &&
                license.Deny(LicenseFeatures.ModuleAccess, LicenseFeatures.AccessDoors,
                    await DoorsInUseAsync(db, id, ct), info?.Doors.Count ?? device.Doors.Count) is { } denied)
                return await license.DenyAsync(ctx, denied, "access-device", device.Name);

            var changes = new List<string>();
            if (device.Name != request.Name.Trim()) changes.Add($"nombre '{device.Name}' → '{request.Name.Trim()}'");
            if (device.Host != host || device.Port != request.Port) changes.Add($"dirección {device.Host}:{device.Port} → {host}:{request.Port}");
            if (device.UseHttps != request.UseHttps) changes.Add(request.UseHttps ? "HTTPS activado" : "HTTPS desactivado");
            if (device.Username != request.Username) changes.Add($"usuario '{device.Username}' → '{request.Username}'");
            if (!string.IsNullOrEmpty(request.Password)) changes.Add("contraseña cambiada");
            if (device.Location != LocationOf(request)) changes.Add($"ubicación '{device.Location}' → '{LocationOf(request)}'");
            if (device.Enabled != request.Enabled) changes.Add(request.Enabled ? "activado" : "desactivado");

            // La sesión cacheada del driver queda con las credenciales viejas.
            if (reachChanged) service.ForgetSessionOf(device);

            device.Name = request.Name.Trim();
            device.DriverKey = request.DriverKey;
            device.Host = host;
            device.Port = request.Port;
            device.UseHttps = request.UseHttps;
            device.Username = request.Username;
            device.PasswordCiphertext = protector.Protect(password);
            device.Location = LocationOf(request);
            device.Enabled = request.Enabled;
            device.UpdatedAt = DateTime.UtcNow;
            if (info is not null)
            {
                ApplyProbe(device, info);
                string doorChanges = SyncDoors(device, info.Doors);
                if (doorChanges != "sin cambios en las puertas") changes.Add(doorChanges);
            }
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "access", "device-updated",
                targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                detail: $"Modificó el equipo de control de acceso '{device.Name}': " +
                        (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios."));
            service.RequestReconcile();
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "access-devices", cancellationToken: ct);
            return Results.Ok(service.ToDto(device));
        });

        app.MapDelete("/api/access/devices/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, IHubContext<VmsHub> hub,
            AccessControlService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var device = await db.AccessDevices.Include(d => d.Doors).FirstOrDefaultAsync(d => d.Id == id, ct);
            if (device is null) return Results.NotFound();
            service.ForgetSessionOf(device);
            int doors = device.Doors.Count;
            db.AccessDevices.Remove(device);   // las puertas se van en cascada
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "access", "device-deleted",
                targetType: "access-device", targetId: id.ToString(), targetName: device.Name,
                detail: $"Eliminó el equipo de control de acceso '{device.Name}' ({device.Host}:{device.Port}) y sus {doors} puerta(s).");
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "access-devices", cancellationToken: ct);
            return Results.Ok();
        });

        // Volver a sondear el equipo: identificación, capacidades y puertas.
        app.MapPost("/api/access/devices/{id:int}/revalidate", async (HttpContext ctx, int id, VmsDbContext db,
            AccessDriverRegistry drivers, IHubContext<VmsHub> hub, AccessControlService service, AuditService audit,
            CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var device = await db.AccessDevices.Include(d => d.Doors).FirstOrDefaultAsync(d => d.Id == id, ct);
            if (device is null) return Results.NotFound();

            var (info, probeError) = await ProbeAsync(drivers, device.DriverKey, service.ConnectionOf(device), ct);
            if (info is null)
            {
                device.Status = AccessDeviceStatus.Offline;
                device.LastError = probeError;
                await db.SaveChangesAsync(ct);
                await audit.LogAsync(ctx, "access", "device-revalidated",
                    targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                    detail: $"Revalidación del equipo '{device.Name}' ({device.Host}) fallida: {probeError}", success: false);
                return Error(probeError!, StatusCodes.Status502BadGateway);
            }

            ApplyProbe(device, info);
            string doorChanges = SyncDoors(device, info.Doors);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "access", "device-revalidated",
                targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                detail: $"Revalidó el equipo de control de acceso '{device.Name}' ({device.Host}): " +
                        $"{device.Model ?? "modelo desconocido"}, firmware {device.FirmwareVersion ?? "desconocido"}, {doorChanges}.");
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "access-devices", cancellationToken: ct);
            return Results.Ok(service.ToDto(device));
        });

        // Probar conexión (sin persistir). Con ?deviceId= reutiliza la clave guardada.
        app.MapPost("/api/access/probe", async (HttpContext ctx, AccessDeviceWriteDto request, VmsDbContext db,
            AccessDriverRegistry drivers, CredentialProtector protector, AuditService audit, int? deviceId, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            string? password = request.Password;
            if (string.IsNullOrEmpty(password) && deviceId is int existingId &&
                await db.AccessDevices.FindAsync([existingId], ct) is { } existing)
                password = protector.Unprotect(existing.PasswordCiphertext);
            if (string.IsNullOrEmpty(password) && drivers.Find(request.DriverKey)!.AuthMode == AccessAuthMode.UserPassword)
                return Error("La contraseña del equipo es obligatoria.");
            password ??= "";

            string host = request.Host.Trim();
            var conn = new AccessConnectionInfo(host, request.Port, request.UseHttps, request.Username, password);
            var (info, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
            await audit.LogAsync(ctx, "access", "device-probed", targetType: "access-device", targetName: request.Name.Trim(),
                detail: info is null
                    ? $"Probó la conexión con el equipo de control de acceso {host}:{request.Port}: {probeError}"
                    : $"Probó la conexión con el equipo de control de acceso {host}:{request.Port}: correcta " +
                      $"({info.Model}, {info.Doors.Count} puerta(s)).",
                success: info is not null);
            if (info is null)
                return Results.Ok(new AccessProbeResultDto(false, probeError, null, null, null, null,
                    AccessDeviceKind.Unknown, 0, false, false, false, false, false, null, null, []));
            return Results.Ok(new AccessProbeResultDto(true, null, info.Model, info.SerialNumber, info.FirmwareVersion,
                info.MacAddress, info.Kind, info.Doors.Count,
                info.Capabilities.SupportsRemoteControl, info.Capabilities.SupportsEvents, info.Capabilities.SupportsCards,
                info.Capabilities.SupportsFingerprint, info.Capabilities.SupportsFace,
                info.Capabilities.UserCapacity, info.Capabilities.CardCapacity,
                info.Doors.Select(d => d.Name).ToList()));
        });
    }
}
