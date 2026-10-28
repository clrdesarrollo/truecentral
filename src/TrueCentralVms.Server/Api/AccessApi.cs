using System.Text.Json;
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
using TrueCentralVms.Server.Services.Isup;
using TrueCentralVms.Core.Domain;

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
        if (drivers.Find(request.DriverKey) is not { } factory)
            return $"Driver desconocido: '{request.DriverKey}'.";
        // Por ISUP el equipo no tiene dirección ni credenciales: lo identifican su ID y su clave (se validan aparte).
        if (factory.UsesIsup) return null;
        if (string.IsNullOrWhiteSpace(request.Host))
            return "La dirección (IP o hostname) es obligatoria.";
        if (request.Port is < 1 or > 65535)
            return "El puerto debe estar entre 1 y 65535.";
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

    /// <summary>Cómo se nombra dónde está el equipo en la bitácora: su dirección, o su ID ISUP.</summary>
    internal static string WhereOf(AccessDevice device) => device.IsupDeviceId is { } isupId ? $"ISUP {isupId}" : $"{device.Host}:{device.Port}";

    internal static void ApplyProbe(AccessDevice device, AccessDeviceInfo info)
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
        // Una lectura fallida de la ficha no borra la anterior: es informativa.
        if (info.Profile is not null)
            device.CapabilityProfileJson = JsonSerializer.Serialize(info.Profile);
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
    internal static string SyncDoors(AccessDevice device, IReadOnlyList<AccessDoorInfo> doors)
    {
        int added = 0, renamed = 0;
        foreach (var door in doors)
        {
            var existing = device.Doors.FirstOrDefault(d => d.Number == door.Number);
            if (existing is null)
            {
                // La puerta nueva queda donde está su equipo.
                device.Doors.Add(new AccessDoor { Number = door.Number, Name = door.Name, LocationId = device.LocationId });
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
    internal static Task<int> DoorsInUseAsync(VmsDbContext db, int? exceptDeviceId, CancellationToken ct) =>
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
                    f.SupportsPersonSync, f.UsesIsup)));
        });

        // ------------------------------------------------------------------
        // Mantenedor
        // ------------------------------------------------------------------
        app.MapGet("/api/access/devices", async (HttpContext ctx, VmsDbContext db, AccessControlService service, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            // Ordenados por la ruta de su ubicación (los por ubicar al final) y por nombre.
            var devices = (await db.AccessDevices.AsNoTracking().Include(d => d.Doors).ToListAsync(ct))
                .OrderBy(d => LocationPaths.Of(d.LocationId) ?? "\uffff", StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            // Alcance por ubicación: cada equipo con solo sus puertas; sin ninguna, no aparece.
            var scope = await ctx.ScopeAsync(session);
            return Results.Ok(devices.Select(service.ToDto).Select(scope.Visible).Where(d => d is not null));
        });

        app.MapGet("/api/access/devices/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            AccessControlService service, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var device = await db.AccessDevices.AsNoTracking().Include(d => d.Doors).FirstOrDefaultAsync(d => d.Id == id, ct);
            return device is not null && (await ctx.ScopeAsync(session)).Visible(service.ToDto(device)) is { } dto
                ? Results.Ok(dto)
                : Results.NotFound();
        });

        // Ficha de capacidades que declaró el equipo al validarlo (pestaña
        // Capacidades). Se lee de la base, no del equipo: para releerla está
        // "Revalidar". null = todavía no se leyó (equipo validado antes).
        app.MapGet("/api/access/devices/{id:int}/capabilities", async (HttpContext ctx, int id, VmsDbContext db,
            AccessControlService service, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var device = await db.AccessDevices.AsNoTracking().Include(d => d.Doors).FirstOrDefaultAsync(d => d.Id == id, ct);
            if (device is null || (await ctx.ScopeAsync(session)).Visible(service.ToDto(device)) is null)
                return Results.NotFound();
            AccessCapabilityProfile? profile = null;
            if (device.CapabilityProfileJson is { Length: > 0 } json)
            {
                try { profile = JsonSerializer.Deserialize<AccessCapabilityProfile>(json); }
                catch (JsonException) { /* ficha de una versión que ya no se entiende: se relee al revalidar */ }
            }
            return Results.Ok(new { profile });
        });

        app.MapPost("/api/access/devices", async (HttpContext ctx, AccessDeviceWriteDto request, VmsDbContext db,
            AccessDriverRegistry drivers, LicenseService license, CredentialProtector protector, IHubContext<VmsHub> hub,
            AccessControlService service, AuditService audit, IsupService isup, IsupHostManager isupHost, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.AccessConfigure, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            var factory = drivers.Find(request.DriverKey)!;
            bool viaIsup = factory.UsesIsup;
            string? isupId = viaIsup ? request.IsupDeviceId?.Trim() : null;
            string? isupKey = viaIsup ? request.IsupKey : null;
            if (viaIsup)
            {
                if (IsupEquipmentRules.Validate(isupId, isupKey, keyStored: false) is { } isupError) return Error(isupError);
                if (await db.AccessDevices.AnyAsync(d => d.IsupDeviceId == isupId, ct))
                    return Error($"Ya existe un equipo de control de acceso con el ID ISUP {isupId}.", StatusCodes.Status409Conflict);
                if (await IsupEquipmentRules.KeyConflictAsync(db, protector, isupId!, isupKey, IsupEquipmentRules.Access, null, ct) is { } conflict)
                    return Error(conflict, StatusCodes.Status409Conflict);
            }
            // Con clave de comunicación, vacío es un valor válido: significa 0.
            else if (factory.AuthMode == AccessAuthMode.UserPassword && string.IsNullOrEmpty(request.Password))
                return Error("La contraseña del equipo es obligatoria.");
            // El módulo se verifica antes de tocar el equipo; el cupo de puertas,
            // después del sondeo (recién ahí se sabe cuántas administra).
            if (license.Deny(LicenseFeatures.ModuleAccess, null, 0) is { } moduleDenied)
                return await license.DenyAsync(ctx, moduleDenied, "access-device", request.Name?.Trim());

            string host = viaIsup ? "" : request.Host.Trim();
            int port = viaIsup ? 0 : request.Port;
            if (!viaIsup && await db.AccessDevices.AnyAsync(d => d.IsupDeviceId == null && d.Host == host && d.Port == port, ct))
                return Error("Ya existe un equipo de control de acceso con esa dirección y puerto.", StatusCodes.Status409Conflict);
            var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, null, ct);
            if (locationError is not null) return Error(locationError, StatusCodes.Status404NotFound);

            // Sin clave = cadena vacía, no null: los equipos con clave de
            // comunicación (ZKTeco) se dan de alta sin contraseña y "vacío"
            // significa 0 para ellos.
            var conn = viaIsup
                ? new AccessConnectionInfo("", 0, false, "", "", isupId)
                : new AccessConnectionInfo(host, port, request.UseHttps, request.Username, request.Password ?? "");
            // Por IP el equipo tiene que contestar para darlo de alta. Por ISUP se conecta recién cuando el VMS lo conoce: se guarda
            // igual y, en su primer contacto, el servicio lee sus puertas y capacidades.
            AccessDeviceInfo? info = null;
            string? waiting = null;
            if (!viaIsup || isup.IsOnline(isupId!))
            {
                (info, var probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
                if (info is null)
                {
                    await audit.LogAsync(ctx, "access", "device-created", targetType: "access-device", targetName: request.Name.Trim(),
                        detail: $"Alta de equipo de control de acceso rechazada por el equipo {(viaIsup ? $"ISUP {isupId}" : $"{host}:{port}")}: {probeError}",
                        success: false);
                    return Error(probeError!);
                }
                if (request.Enabled &&
                    license.Deny(LicenseFeatures.ModuleAccess, LicenseFeatures.AccessDoors,
                        await DoorsInUseAsync(db, null, ct), info.Doors.Count) is { } denied)
                    return await license.DenyAsync(ctx, denied, "access-device", request.Name?.Trim());
            }
            else waiting = await IsupEquipmentRules.NotConnectedMessageAsync(isup, isupHost, isupId!, "equipo", ct);

            var device = new AccessDevice
            {
                Name = request.Name.Trim(),
                DriverKey = request.DriverKey,
                Host = host,
                Port = port,
                UseHttps = !viaIsup && request.UseHttps,
                Username = viaIsup ? "" : request.Username,
                PasswordCiphertext = protector.Protect(viaIsup ? "" : request.Password ?? ""),
                IsupDeviceId = isupId,
                IsupKeyCiphertext = isupKey is null ? null : protector.Protect(isupKey),
                LocationId = locationId,
                Enabled = request.Enabled,
                LiveEvents = request.LiveEvents ?? true,
            };
            if (info is not null)
            {
                ApplyProbe(device, info);
                SyncDoors(device, info.Doors);
            }
            else
            {
                device.Status = AccessDeviceStatus.Offline;
                device.LastError = waiting;
            }
            db.AccessDevices.Add(device);
            await db.SaveChangesAsync(ct);
            if (viaIsup) isup.InvalidateDirectory();

            await audit.LogAsync(ctx, "access", "device-created",
                targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                detail: $"Agregó el equipo de control de acceso '{device.Name}' ({device.DriverKey}, {WhereOf(device)}, " +
                        (info is null ? "a la espera de que se conecte por ISUP" : $"{device.Model ?? "modelo desconocido"}") +
                        (device.LocationId is null ? "" : $", en {EquipmentLocation.Describe(device.LocationId)}") +
                        $", {device.Doors.Count} puerta(s)" + (device.LiveEvents ? "" : ", solo lectura del historial (sin escucha en vivo)") + ").",
                data: new { device.Host, device.Port, device.UseHttps, device.DriverKey, device.IsupDeviceId, device.Model, device.SerialNumber, device.LocationId, device.LiveEvents });
            EquipmentLocation.Changed(ctx);
            service.RequestReconcile();
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "access-devices", cancellationToken: ct);
            return Results.Ok(service.ToDto(device));
        });

        app.MapPut("/api/access/devices/{id:int}", async (HttpContext ctx, int id, AccessDeviceWriteDto request, VmsDbContext db,
            AccessDriverRegistry drivers, LicenseService license, CredentialProtector protector, IHubContext<VmsHub> hub,
            AccessControlService service, AuditService audit, IsupService isup, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.AccessConfigure, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            var device = await db.AccessDevices.Include(d => d.Doors).FirstOrDefaultAsync(d => d.Id == id, ct);
            if (device is null) return Results.NotFound();
            bool viaIsup = drivers.Find(request.DriverKey)!.UsesIsup;
            string? isupId = viaIsup ? request.IsupDeviceId?.Trim() : null;
            string? isupKey = viaIsup && !string.IsNullOrEmpty(request.IsupKey) ? request.IsupKey : null;
            string host = viaIsup ? "" : request.Host.Trim();
            int port = viaIsup ? 0 : request.Port;
            if (viaIsup)
            {
                bool keyStored = device.IsupKeyCiphertext is { Length: > 0 } && device.IsupDeviceId == isupId;
                if (IsupEquipmentRules.Validate(isupId, isupKey, keyStored) is { } isupError) return Error(isupError);
                if (await db.AccessDevices.AnyAsync(d => d.Id != id && d.IsupDeviceId == isupId, ct))
                    return Error($"Ya existe otro equipo de control de acceso con el ID ISUP {isupId}.", StatusCodes.Status409Conflict);
                string effectiveKey = IsupEquipmentRules.EffectiveKey(isupKey, device.IsupKeyCiphertext, protector);
                if (await IsupEquipmentRules.KeyConflictAsync(db, protector, isupId!, effectiveKey, IsupEquipmentRules.Access, id, ct) is { } conflict)
                    return Error(conflict, StatusCodes.Status409Conflict);
            }
            else if (await db.AccessDevices.AnyAsync(d => d.IsupDeviceId == null && d.Host == host && d.Port == port && d.Id != id, ct))
                return Error("Ya existe otro equipo de control de acceso con esa dirección y puerto.", StatusCodes.Status409Conflict);
            var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, device.LocationId, ct);
            if (locationError is not null) return Error(locationError, StatusCodes.Status404NotFound);
            int? previousLocation = device.LocationId;

            if (!viaIsup && device.PasswordCiphertext.Length > 0 && string.IsNullOrEmpty(request.Password) && !StoredPassword.CanReuse(device.Host, request.Host))
                return Error(StoredPassword.RetypeMessage);
            string password = viaIsup ? ""
                : string.IsNullOrEmpty(request.Password) ? protector.Unprotect(device.PasswordCiphertext) : request.Password;
            string username = viaIsup ? "" : request.Username;
            bool isupChanged = device.IsupDeviceId != isupId || isupKey is not null;

            // Si cambió cómo se llega al equipo, hay que revalidar contra él. Con ID o clave ISUP nuevos el equipo tiene que
            // volver a registrarse con ellos: se guarda y se valida en su próximo contacto.
            bool reachChanged = device.Host != host || device.Port != port || device.UseHttps != (!viaIsup && request.UseHttps) ||
                                device.Username != username || (!viaIsup && !string.IsNullOrEmpty(request.Password)) ||
                                device.DriverKey != request.DriverKey || isupChanged;
            AccessDeviceInfo? info = null;
            if (reachChanged && (!viaIsup || (!isupChanged && isup.IsOnline(isupId!))))
            {
                var conn = new AccessConnectionInfo(host, port, !viaIsup && request.UseHttps, username, password, isupId);
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
            if (device.DriverKey != request.DriverKey) changes.Add($"conexión {device.DriverKey} → {request.DriverKey}");
            if (!viaIsup && (device.Host != host || device.Port != port)) changes.Add($"dirección {WhereOf(device)} → {host}:{port}");
            if (!viaIsup && device.UseHttps != request.UseHttps) changes.Add(request.UseHttps ? "HTTPS activado" : "HTTPS desactivado");
            if (!viaIsup && device.Username != username) changes.Add($"usuario '{device.Username}' → '{username}'");
            if (!viaIsup && !string.IsNullOrEmpty(request.Password)) changes.Add("contraseña cambiada");
            if (device.IsupDeviceId != isupId) changes.Add($"ID ISUP '{device.IsupDeviceId}' → '{isupId}'");
            if (isupKey is not null) changes.Add("clave ISUP cambiada");
            if (locationId != previousLocation)
                changes.Add(EquipmentLocation.Change(previousLocation, locationId));
            if (device.Enabled != request.Enabled) changes.Add(request.Enabled ? "activado" : "desactivado");
            bool liveEvents = request.LiveEvents ?? device.LiveEvents;
            if (device.LiveEvents != liveEvents)
                changes.Add(liveEvents ? "escucha de eventos en vivo activada" : "escucha de eventos en vivo apagada (solo lectura del historial)");

            // La sesión cacheada del driver queda con las credenciales viejas.
            if (reachChanged) service.ForgetSessionOf(device);

            device.Name = request.Name.Trim();
            device.DriverKey = request.DriverKey;
            device.Host = host;
            device.Port = port;
            device.UseHttps = !viaIsup && request.UseHttps;
            device.Username = username;
            device.PasswordCiphertext = protector.Protect(password);
            device.IsupDeviceId = isupId;
            if (!viaIsup) device.IsupKeyCiphertext = null;
            else if (isupKey is not null) device.IsupKeyCiphertext = protector.Protect(isupKey);
            device.Enabled = request.Enabled;
            device.LiveEvents = liveEvents;
            device.UpdatedAt = DateTime.UtcNow;
            // Las puertas que estaban con el equipo lo siguen; las ubicadas aparte se quedan.
            var followers = EquipmentLocation.Follow(device.Doors, previousLocation, locationId);
            device.LocationId = locationId;
            if (info is not null)
            {
                ApplyProbe(device, info);
                string doorChanges = SyncDoors(device, info.Doors);
                if (doorChanges != "sin cambios en las puertas") changes.Add(doorChanges);
            }
            await db.SaveChangesAsync(ct);
            if (isupChanged) isup.InvalidateDirectory();

            await audit.LogAsync(ctx, "access", "device-updated",
                targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                detail: $"Modificó el equipo de control de acceso '{device.Name}': " +
                        (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios."));
            // Solo las que siguen existiendo (una puerta que el equipo dejó de declarar se borró).
            await EquipmentLocation.LogFollowersAsync(ctx, audit, "access-device", device.Id, device.Name, locationId,
                followers.Where(d => device.Doors.Contains(d)).Select(d => (ResourceKind.Door, d.Id, d.Name)).ToList());
            if (locationId != previousLocation) await EquipmentLocation.MovedAsync(ctx, ct);
            else if (info is not null) EquipmentLocation.Changed(ctx);
            service.RequestReconcile();
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "access-devices", cancellationToken: ct);
            return Results.Ok(service.ToDto(device));
        });

        app.MapDelete("/api/access/devices/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, IHubContext<VmsHub> hub,
            AccessControlService service, AuditService audit, IsupService isup, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.AccessConfigure, out _) is { } failure) return failure;
            var device = await db.AccessDevices.Include(d => d.Doors).FirstOrDefaultAsync(d => d.Id == id, ct);
            if (device is null) return Results.NotFound();
            service.ForgetSessionOf(device);
            int doors = device.Doors.Count;
            db.AccessDevices.Remove(device);   // las puertas se van en cascada
            await db.SaveChangesAsync(ct);
            if (device.IsupDeviceId is not null) isup.InvalidateDirectory();
            await audit.LogAsync(ctx, "access", "device-deleted",
                targetType: "access-device", targetId: id.ToString(), targetName: device.Name,
                detail: $"Eliminó el equipo de control de acceso '{device.Name}' ({WhereOf(device)}) y sus {doors} puerta(s).");
            EquipmentLocation.Changed(ctx);
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "access-devices", cancellationToken: ct);
            return Results.Ok();
        });

        // Volver a sondear el equipo: identificación, capacidades y puertas.
        app.MapPost("/api/access/devices/{id:int}/revalidate", async (HttpContext ctx, int id, VmsDbContext db,
            AccessDriverRegistry drivers, IHubContext<VmsHub> hub, AccessControlService service, AuditService audit,
            IsupService isup, IsupHostManager isupHost, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.AccessConfigure, out _) is { } failure) return failure;
            var device = await db.AccessDevices.Include(d => d.Doors).FirstOrDefaultAsync(d => d.Id == id, ct);
            if (device is null) return Results.NotFound();

            AccessDeviceInfo? info = null;
            string? probeError;
            if (device.IsupDeviceId is { } isupId && !isup.IsOnline(isupId))
                probeError = await IsupEquipmentRules.NotConnectedMessageAsync(isup, isupHost, isupId, "equipo", ct);
            else
                (info, probeError) = await ProbeAsync(drivers, device.DriverKey, service.ConnectionOf(device), ct);
            if (info is null)
            {
                device.Status = AccessDeviceStatus.Offline;
                device.LastError = probeError;
                await db.SaveChangesAsync(ct);
                await audit.LogAsync(ctx, "access", "device-revalidated",
                    targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                    detail: $"Revalidación del equipo '{device.Name}' ({WhereOf(device)}) fallida: {probeError}", success: false);
                return Error(probeError!, StatusCodes.Status502BadGateway);
            }

            ApplyProbe(device, info);
            string doorChanges = SyncDoors(device, info.Doors);
            await db.SaveChangesAsync(ct);
            // Pudieron aparecer puertas nuevas (ya ubicadas donde está el equipo).
            EquipmentLocation.Changed(ctx);
            await audit.LogAsync(ctx, "access", "device-revalidated",
                targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                detail: $"Revalidó el equipo de control de acceso '{device.Name}' ({WhereOf(device)}): " +
                        $"{device.Model ?? "modelo desconocido"}, firmware {device.FirmwareVersion ?? "desconocido"}, {doorChanges}.");
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "access-devices", cancellationToken: ct);
            return Results.Ok(service.ToDto(device));
        });

        // Probar conexión (sin persistir). Con ?deviceId= reutiliza la clave guardada.
        app.MapPost("/api/access/probe", async (HttpContext ctx, AccessDeviceWriteDto request, VmsDbContext db,
            AccessDriverRegistry drivers, CredentialProtector protector, AuditService audit, IsupService isup, IsupHostManager isupHost,
            int? deviceId, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.AccessConfigure, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            AccessConnectionInfo conn;
            string target;
            if (drivers.Find(request.DriverKey)!.UsesIsup)
            {
                string? isupId = request.IsupDeviceId?.Trim();
                if (Core.Isup.IsupGateway.ValidateDeviceId(isupId) is { } idError) return Error(idError);
                target = $"ISUP {isupId}";
                if (!isup.IsOnline(isupId!))
                {
                    var why = await IsupEquipmentRules.NotConnectedMessageAsync(isup, isupHost, isupId!, "equipo", ct);
                    await audit.LogAsync(ctx, "access", "device-probed", targetType: "access-device", targetName: request.Name.Trim(),
                        detail: $"Probó la conexión con el equipo de control de acceso {target}: {why}", success: false);
                    return Results.Ok(new AccessProbeResultDto(false, why, null, null, null, null,
                        AccessDeviceKind.Unknown, 0, false, false, false, false, false, null, null, []));
                }
                conn = new AccessConnectionInfo("", 0, false, "", "", isupId);
            }
            else
            {
                string? password = request.Password;
                if (string.IsNullOrEmpty(password) && deviceId is int existingId &&
                    await db.AccessDevices.FindAsync([existingId], ct) is { } existing && existing.PasswordCiphertext.Length > 0)
                {
                    if (!StoredPassword.CanReuse(existing.Host, request.Host)) return Error(StoredPassword.RetypeMessage);
                    password = protector.Unprotect(existing.PasswordCiphertext);
                }
                if (string.IsNullOrEmpty(password) && drivers.Find(request.DriverKey)!.AuthMode == AccessAuthMode.UserPassword)
                    return Error("La contraseña del equipo es obligatoria.");
                string host = request.Host.Trim();
                conn = new AccessConnectionInfo(host, request.Port, request.UseHttps, request.Username, password ?? "");
                target = $"{host}:{request.Port}";
            }
            var (info, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
            await audit.LogAsync(ctx, "access", "device-probed", targetType: "access-device", targetName: request.Name.Trim(),
                detail: info is null
                    ? $"Probó la conexión con el equipo de control de acceso {target}: {probeError}"
                    : $"Probó la conexión con el equipo de control de acceso {target}: correcta " +
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
