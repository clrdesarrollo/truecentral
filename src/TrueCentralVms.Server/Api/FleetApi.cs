using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Core.Isup;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Isup;
using TrueCentralVms.Server.Services.Licensing;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Módulo Flota: vehículos con su grabador móvil (MDVR por ISUP). El grabador es un <see cref="Device"/> marcado
/// <see cref="Device.IsMobile"/>: el vivo va por las mismas concesiones que cualquier cámara (con el permiso de Flota), y aquí se
/// administran el vehículo y su grabador juntos. Licencia: un cupo <c>fleet_vehicles</c> por vehículo habilitado; sus canales no
/// consumen <c>video_channels</c> y tienen un tope por vehículo.
/// </summary>
public static class FleetApi
{
    private const string IsupDriver = "hikvision-isup";

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    private static VehicleDto ToDto(Vehicle v, IsupService isup, string? warning = null)
    {
        var d = v.Device;
        bool online = d.Status == DeviceStatus.Online;
        FleetPositionDto? position = null;
        if (d.IsupDeviceId is { } isupId && isup.GpsTracks.TryGetValue(isupId, out var track) && track.LastValid is { } fix)
            position = new FleetPositionDto(fix.Latitude, fix.Longitude, fix.SpeedKmh, fix.HeadingDeg, fix.Satellites, fix.DeviceTime, track.ReceivedAt);
        return new VehicleDto(v.Id, v.Code, v.Plate, v.Kind, v.Notes, v.Enabled, v.DisabledByLicense, d.Id, d.IsupDeviceId,
            d.IsupKeyCiphertext is { Length: > 0 }, d.Status, d.LastSeenAt, d.Model, d.FirmwareVersion, d.LocationId,
            LocationPaths.Of(d.LocationId),
            d.Channels.OrderBy(c => c.ChannelNumber)
                .Select(c => new FleetChannelDto(c.Id, c.ChannelNumber, c.RtspChannel, c.Name, c.Enabled, c.IsOnline && online)).ToList(),
            position, v.CreatedAt, warning);
    }

    private static string? Validate(VehicleWriteDto r, bool keyStored)
    {
        if (string.IsNullOrWhiteSpace(r.Code) || r.Code.Trim().Length > 64)
            return "El código del vehículo es obligatorio (máximo 64 caracteres).";
        if (r.Plate is { Length: > 16 }) return "La patente admite hasta 16 caracteres.";
        if (r.Kind is { Length: > 64 }) return "El tipo admite hasta 64 caracteres.";
        if (r.Notes is { Length: > 1024 }) return "Las notas admiten hasta 1024 caracteres.";
        return IsupEquipmentRules.Validate(r.IsupDeviceId, r.IsupKey, keyStored);
    }

    /// <summary>Los servicios de posiciones y de eventos releen qué grabador es de qué vehículo.</summary>
    private static void VehiclesChanged(HttpContext ctx)
    {
        ctx.RequestServices.GetRequiredService<Services.Fleet.FleetPositionService>().InvalidateVehicles();
        ctx.RequestServices.GetRequiredService<Services.Fleet.FleetEventService>().Invalidate();
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Licencia para un vehículo habilitado más (null = permitido).</summary>
    private static async Task<string?> DenyVehicleAsync(LicenseService license, VmsDbContext db, int? exceptId, CancellationToken ct) =>
        license.Deny(LicenseFeatures.ModuleFleet, LicenseFeatures.FleetVehicles,
            await db.Vehicles.CountAsync(v => v.Enabled && v.Id != exceptId, ct));

    /// <summary>
    /// Crea el vehículo y su grabador (sin guardar). Si el grabador ya está conectado por ISUP se leen sus datos y canales; si no,
    /// se completa en su primer contacto (como cualquier equipo ISUP). Devuelve el aviso para el panel o el error.
    /// </summary>
    private static async Task<(Vehicle? Vehicle, string? Warning, string? Error)> BuildAsync(VehicleWriteDto r, int? locationId,
        VmsDbContext db, DriverRegistry drivers, CredentialProtector protector, LicenseService license, IsupService isup,
        IsupHostManager isupHost, CancellationToken ct)
    {
        string code = r.Code.Trim(), isupId = r.IsupDeviceId.Trim();
        if (await db.Vehicles.AnyAsync(v => v.Code == code, ct))
            return (null, null, $"Ya existe un vehículo con el código {code}.");
        if (await db.Devices.AnyAsync(d => d.IsupDeviceId == isupId, ct))
            return (null, null, $"El ID ISUP {isupId} ya está en uso por otro equipo de video o vehículo.");
        if (await IsupEquipmentRules.KeyConflictAsync(db, protector, isupId, r.IsupKey, IsupEquipmentRules.Video, null, ct) is { } conflict)
            return (null, null, conflict);
        if (r.Enabled && await DenyVehicleAsync(license, db, null, ct) is { } denied)
            return (null, null, denied);

        var device = new Device
        {
            Name = code,
            DriverKey = IsupDriver,
            Host = "",
            SdkPort = 0,
            RtspPort = 0,
            Username = "",
            PasswordCiphertext = [],
            IsupDeviceId = isupId,
            IsupKeyCiphertext = IsupEquipmentRules.ProtectKey(r.IsupKey, protector),
            LocationId = locationId,
            IsMobile = true,
        };
        var vehicle = new Vehicle
        {
            Code = code, Plate = Clean(r.Plate)?.ToUpperInvariant(), Kind = Clean(r.Kind), Notes = Clean(r.Notes),
            Device = device, Enabled = r.Enabled,
        };
        string? warning = null;
        if (!isup.IsOnline(isupId))
            warning = "Guardado. Sus cámaras se leen cuando el grabador se conecte. " +
                      await IsupEquipmentRules.NotConnectedMessageAsync(isup, isupHost, isupId, "grabador", ct);
        else
        {
            var (info, probeError) = await DevicesApi.ProbeAsync(drivers, IsupDriver, DeviceConnections.Of(device, protector), ct);
            if (info is null) return (null, null, $"El grabador está conectado pero no respondió: {probeError}");
            DevicesApi.ApplyProbe(device, info);
            int trimmed = await DevicesApi.EnforceChannelQuotaAsync(license, db, device, ct);
            if (trimmed > 0)
                warning = $"{trimmed} cámara(s) quedaron deshabilitadas: un vehículo admite hasta {LicenseFeatures.MaxChannelsPerVehicle} habilitadas.";
        }
        if (!vehicle.Enabled) foreach (var c in device.Channels) c.Enabled = false;
        return (vehicle, warning, null);
    }

    public static void MapFleetApi(this WebApplication app)
    {
        MapPositionsApi(app);

        // Árbol de Flota y mantenedor: vehículos con sus cámaras, estado y última posición. Alcance: el del grabador.
        app.MapGet("/api/fleet/vehicles", async (HttpContext ctx, VmsDbContext db, IsupService isup, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAny(ctx, out var session, Permissions.FleetView, Permissions.FleetManage) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var vehicles = await db.Vehicles.AsNoTracking().Include(v => v.Device).ThenInclude(d => d.Channels)
                .OrderBy(v => v.Code).ToListAsync(ct);
            bool manage = ApiSecurity.Has(ctx, Permissions.FleetManage);
            return Results.Ok(vehicles
                .Where(v => !scope.FiltersView || scope.CanViewDevice(v.DeviceId) || (manage && v.Device.Channels.Count == 0))
                .Select(v =>
                {
                    var dto = ToDto(v, isup);
                    // Con alcance limitado, solo las cámaras que puede ver.
                    return scope.FiltersView ? dto with { Channels = dto.Channels.Where(c => scope.CanViewChannel(c.Id)).ToList() } : dto;
                }));
        });

        // Equipos que intentan registrarse por ISUP sin estar dados de alta: candidatos a vehículo.
        app.MapGet("/api/fleet/detected", async (HttpContext ctx, IsupService isup, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.FleetManage, out _) is { } failure) return failure;
            var status = await isup.GetHostStatusAsync(ct);
            var known = (await IsupApi.EquipmentByIsupIdAsync(db, ct)).Keys.ToHashSet(StringComparer.Ordinal);
            return Results.Ok((status?.Rejections ?? [])
                .Where(r => !known.Contains(r.DeviceId))
                .OrderByDescending(r => r.LastAt)
                .Select(r => new FleetDetectedDto(r.DeviceId, r.Reason, r.FirstAt, r.LastAt, r.Count)));
        });

        app.MapPost("/api/fleet/vehicles", async (HttpContext ctx, VehicleWriteDto request, VmsDbContext db, DriverRegistry drivers,
            CredentialProtector protector, LicenseService license, IsupService isup, IsupHostManager isupHost, MediaMtxManager mtx,
            AuditService audit, IHubContext<VmsHub> hub, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.FleetManage, out _) is { } failure) return failure;
            if (license.Deny(LicenseFeatures.ModuleFleet, null, 0, 0) is { } noModule)
                return await license.DenyAsync(ctx, noModule, "vehicle", request.Code?.Trim());
            if (Validate(request, false) is { } invalid) return Error(invalid);
            var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, null, ct);
            if (locationError is not null) return Error(locationError, StatusCodes.Status404NotFound);

            var (vehicle, warning, error) = await BuildAsync(request, locationId, db, drivers, protector, license, isup, isupHost, ct);
            if (vehicle is null)
            {
                await audit.LogAsync(ctx, "fleet", "vehicle-created", "vehicle", null, request.Code?.Trim(),
                    $"Alta de vehículo rechazada: {error}", success: false);
                return Error(error!, error!.StartsWith("Ya existe") || error.Contains("ya está en uso") ? StatusCodes.Status409Conflict
                    : StatusCodes.Status422UnprocessableEntity);
            }
            db.Vehicles.Add(vehicle);
            await db.SaveChangesAsync(ct);
            isup.InvalidateDirectory(); VehiclesChanged(ctx);
            await audit.LogAsync(ctx, "fleet", "vehicle-created", "vehicle", vehicle.Id.ToString(), vehicle.Code,
                $"Agregó el vehículo {vehicle.Code}{(vehicle.Plate is { } p ? $" (patente {p})" : "")} con su grabador ISUP " +
                $"{vehicle.Device.IsupDeviceId}: " + (vehicle.Device.Channels.Count > 0
                    ? $"{vehicle.Device.Channels.Count} cámaras, {vehicle.Device.Channels.Count(c => c.Enabled)} habilitadas"
                    : "a la espera de que se conecte para leer sus cámaras") +
                (vehicle.Enabled ? "" : ", deshabilitado") +
                (locationId is null ? "" : $", en {EquipmentLocation.Describe(locationId)}") + ".",
                data: new { vehicle.Code, vehicle.Plate, vehicle.Device.IsupDeviceId, vehicle.Device.Model, locationId });
            EquipmentLocation.Changed(ctx);
            await mtx.RefreshPathsAsync(ct);
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "fleet", cancellationToken: ct);
            return Results.Ok(ToDto(vehicle, isup, warning));
        });

        app.MapPut("/api/fleet/vehicles/{id:int}", async (HttpContext ctx, int id, VehicleWriteDto request, VmsDbContext db,
            CredentialProtector protector, LicenseService license, IsupService isup, MediaMtxManager mtx, AuditService audit,
            IHubContext<VmsHub> hub, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.FleetManage, out _) is { } failure) return failure;
            var vehicle = await db.Vehicles.Include(v => v.Device).ThenInclude(d => d.Channels).FirstOrDefaultAsync(v => v.Id == id, ct);
            if (vehicle is null) return Results.NotFound();
            var device = vehicle.Device;
            if (Validate(request, device.IsupKeyCiphertext is { Length: > 0 }) is { } invalid) return Error(invalid);
            string code = request.Code.Trim(), isupId = request.IsupDeviceId.Trim();
            if (await db.Vehicles.AnyAsync(v => v.Id != id && v.Code == code, ct))
                return Error($"Ya existe otro vehículo con el código {code}.", StatusCodes.Status409Conflict);
            if (await db.Devices.AnyAsync(d => d.Id != device.Id && d.IsupDeviceId == isupId, ct))
                return Error($"El ID ISUP {isupId} ya está en uso por otro equipo de video o vehículo.", StatusCodes.Status409Conflict);
            string isupKey = IsupEquipmentRules.EffectiveKey(request.IsupKey, device.IsupKeyCiphertext, protector);
            if (await IsupEquipmentRules.KeyConflictAsync(db, protector, isupId, isupKey, IsupEquipmentRules.Video, device.Id, ct) is { } conflict)
                return Error(conflict, StatusCodes.Status409Conflict);
            if (request.Enabled && !vehicle.Enabled && await DenyVehicleAsync(license, db, id, ct) is { } denied)
                return await license.DenyAsync(ctx, denied, "vehicle", vehicle.Code);
            var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, device.LocationId, ct);
            if (locationError is not null) return Error(locationError, StatusCodes.Status404NotFound);

            var changes = new List<string>();
            void Track(string label, string? before, string? after) { if ((before ?? "") != (after ?? "")) changes.Add($"{label} '{before ?? "—"}' → '{after ?? "—"}'"); }
            Track("código", vehicle.Code, code);
            Track("patente", vehicle.Plate, Clean(request.Plate)?.ToUpperInvariant());
            Track("tipo", vehicle.Kind, Clean(request.Kind));
            if ((vehicle.Notes ?? "") != (Clean(request.Notes) ?? "")) changes.Add("notas");
            Track("ID ISUP", device.IsupDeviceId, isupId);
            if (!string.IsNullOrEmpty(request.IsupKey)) changes.Add("clave ISUP");
            if (vehicle.Enabled != request.Enabled) changes.Add(request.Enabled ? "habilitado" : "deshabilitado");
            int? previousLocation = device.LocationId;
            if (locationId != previousLocation) changes.Add(EquipmentLocation.Change(previousLocation, locationId));
            var followers = EquipmentLocation.Follow(device.Channels, previousLocation, locationId);

            bool isupChanged = device.IsupDeviceId != isupId || !string.IsNullOrEmpty(request.IsupKey);
            bool enabledChanged = vehicle.Enabled != request.Enabled;
            vehicle.Code = code;
            vehicle.Plate = Clean(request.Plate)?.ToUpperInvariant();
            vehicle.Kind = Clean(request.Kind);
            vehicle.Notes = Clean(request.Notes);
            vehicle.Enabled = request.Enabled;
            vehicle.UpdatedAt = DateTime.UtcNow;
            device.Name = code;
            device.IsupDeviceId = isupId;
            if (!string.IsNullOrEmpty(request.IsupKey)) device.IsupKeyCiphertext = protector.Protect(request.IsupKey);
            device.LocationId = locationId;
            device.UpdatedAt = DateTime.UtcNow;
            // Deshabilitar el vehículo apaga sus cámaras; habilitarlo enciende hasta el tope por vehículo.
            if (enabledChanged)
            {
                int allowed = LicenseFeatures.MaxChannelsPerVehicle;
                foreach (var c in device.Channels.OrderBy(c => c.ChannelNumber))
                    c.Enabled = request.Enabled && c.IsOnline && allowed-- > 0;
            }
            await db.SaveChangesAsync(ct);
            if (isupChanged) isup.InvalidateDirectory();
            VehiclesChanged(ctx);
            await audit.LogAsync(ctx, "fleet", "vehicle-updated", "vehicle", id.ToString(), vehicle.Code,
                $"Modificó el vehículo {vehicle.Code}: " + (changes.Count > 0 ? string.Join(", ", changes) : "sin cambios") + ".");
            await EquipmentLocation.LogFollowersAsync(ctx, audit, "device", device.Id, device.Name, locationId,
                followers.Select(c => (ResourceKind.Camera, c.Id, c.Name)).ToList());
            if (locationId != previousLocation) await EquipmentLocation.MovedAsync(ctx, ct);
            if (enabledChanged) await mtx.RefreshPathsAsync(ct);
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "fleet", cancellationToken: ct);
            return Results.Ok(ToDto(vehicle, isup));
        });

        app.MapDelete("/api/fleet/vehicles/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, IsupService isup,
            MediaMtxManager mtx, AuditService audit, IHubContext<VmsHub> hub, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.FleetManage, out _) is { } failure) return failure;
            var vehicle = await db.Vehicles.Include(v => v.Device).FirstOrDefaultAsync(v => v.Id == id, ct);
            if (vehicle is null) return Results.NotFound();
            db.Devices.Remove(vehicle.Device);   // el vehículo y los canales caen por cascada
            await db.SaveChangesAsync(ct);
            isup.InvalidateDirectory(); VehiclesChanged(ctx);
            await audit.LogAsync(ctx, "fleet", "vehicle-deleted", "vehicle", id.ToString(), vehicle.Code,
                $"Eliminó el vehículo {vehicle.Code} y su grabador ISUP {vehicle.Device.IsupDeviceId} con todas sus cámaras.");
            EquipmentLocation.Changed(ctx);
            await mtx.RefreshPathsAsync(ct);
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "fleet", cancellationToken: ct);
            return Results.Ok();
        });

        // Importación masiva (una flota trae cientos de vehículos): código;patente;tipo;id_isup[;clave_isup].
        app.MapPost("/api/fleet/vehicles/import", async (HttpContext ctx, VehicleImportRequestDto request, VmsDbContext db,
            DriverRegistry drivers, CredentialProtector protector, LicenseService license, IsupService isup, IsupHostManager isupHost,
            MediaMtxManager mtx, AuditService audit, IHubContext<VmsHub> hub, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.FleetManage, out _) is { } failure) return failure;
            if (license.Deny(LicenseFeatures.ModuleFleet, null, 0, 0) is { } noModule)
                return await license.DenyAsync(ctx, noModule, "vehicle", "importación");
            if (string.IsNullOrWhiteSpace(request.Csv) || request.Csv.Length > 2_000_000)
                return Error("El archivo está vacío o es demasiado grande (máximo 2 MB).");
            var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, null, ct);
            if (locationError is not null) return Error(locationError, StatusCodes.Status404NotFound);

            var rows = new List<VehicleImportRowDto>();
            var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            int created = 0, lineNo = 0;
            foreach (var raw in request.Csv.Replace("\r\n", "\n").Split('\n'))
            {
                lineNo++;
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var f = line.Split(line.Contains(';') ? ';' : ',').Select(x => x.Trim().Trim('"')).ToArray();
                if (lineNo == 1 && f[0].Contains("digo", StringComparison.OrdinalIgnoreCase)) continue;   // encabezados
                string code = f.ElementAtOrDefault(0) ?? "", isupId = f.ElementAtOrDefault(3) ?? "";
                var write = new VehicleWriteDto(code, f.ElementAtOrDefault(1), f.ElementAtOrDefault(2), null, isupId,
                    f.ElementAtOrDefault(4) is { Length: > 0 } k ? k : null, locationId);
                string? error = Validate(write, false);
                if (error is null && !seenCodes.Add(code)) error = $"El código {code} se repite en el archivo.";
                if (error is null && !seenIds.Add(isupId)) error = $"El ID ISUP {isupId} se repite en el archivo.";
                Vehicle? vehicle = null;
                if (error is null)
                {
                    (vehicle, _, error) = await BuildAsync(write, locationId, db, drivers, protector, license, isup, isupHost, ct);
                    if (vehicle is not null && !request.DryRun)
                    {
                        db.Vehicles.Add(vehicle);
                        await db.SaveChangesAsync(ct);   // de a uno: el cupo de la licencia cuenta los ya importados
                        created++;
                    }
                    else if (vehicle is not null) created++;
                }
                rows.Add(new VehicleImportRowDto(lineNo, code, isupId.Length > 0 ? isupId : null, error is null, error));
                if (rows.Count >= 5000) break;
            }
            int failed = rows.Count(r => !r.Ok);
            await audit.LogAsync(ctx, "fleet", "vehicles-imported", "vehicle", null, null,
                (request.DryRun ? "Revisó una importación de vehículos" : "Importó vehículos desde CSV") +
                $": {created} {(request.DryRun ? "válidos" : "agregados")}, {failed} con error" +
                (locationId is null ? "" : $", en {EquipmentLocation.Describe(locationId)}") + ".",
                data: new { created, failed, dryRun = request.DryRun }, success: failed == 0 || created > 0);
            if (!request.DryRun && created > 0)
            {
                isup.InvalidateDirectory(); VehiclesChanged(ctx);
                EquipmentLocation.Changed(ctx);
                await mtx.RefreshPathsAsync(ct);
                await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "fleet", cancellationToken: ct);
            }
            return Results.Ok(new VehicleImportResultDto(created, failed, request.DryRun, rows));
        });
    }

    // ---------------- posiciones y mapa (fase 2) ----------------

    private static void MapPositionsApi(WebApplication app)
    {
        // Última posición de cada vehículo visible (el mapa la pide al abrir; después llegan por el hub).
        app.MapGet("/api/fleet/positions", async (HttpContext ctx, Services.Fleet.FleetPositionService positions) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.FleetView, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            return Results.Ok(positions.Latest().Where(p => scope.CanViewDevice(p.DeviceId)));
        });

        // Recorrido de un vehículo: dato personal del conductor, con permiso propio y bitácora.
        app.MapGet("/api/fleet/vehicles/{id:int}/track", async (HttpContext ctx, int id, DateTime from, DateTime to,
            VmsDbContext db, Services.Fleet.FleetPositionService positions, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.FleetHistory, out var session) is { } failure) return failure;
            var vehicle = await db.Vehicles.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
            if (vehicle is null) return Results.NotFound();
            if (!(await ctx.ScopeAsync(session)).CanViewDevice(vehicle.DeviceId))
                return await ctx.OutOfScopeAsync(session, "vehicle", id.ToString(), vehicle.Code, "ver su recorrido");
            var fromUtc = from.ToUniversalTime();
            var toUtc = to.ToUniversalTime();
            if (toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(7))
                return Error("El rango debe ser de hasta 7 días y terminar después de empezar.");
            var points = await positions.TrackAsync(id, fromUtc, toUtc, ct);
            if (audit.ShouldLog($"fleet-track:{session.UserId}:{id}", TimeSpan.FromMinutes(10)))
                await audit.LogAsync(ctx, "fleet", "track-viewed", "vehicle", id.ToString(), vehicle.Code,
                    $"Consultó el recorrido de {vehicle.Code} entre {fromUtc.ToLocalTime():yyyy-MM-dd HH:mm} y {toUtc.ToLocalTime():yyyy-MM-dd HH:mm} ({points.Count} puntos).");
            return Results.Ok(points);
        });

        app.MapGet("/api/fleet/map/settings", async (HttpContext ctx, Services.Fleet.FleetPositionService positions, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAny(ctx, out _, Permissions.FleetView, Permissions.FleetManage) is { } failure) return failure;
            // Con teselas en línea, el navegador de cada operador las pide directo al proveedor: la plantilla (con su clave, si
            // la lleva) va a todos los que ven el mapa.
            return Results.Ok(await positions.LoadSettingsAsync(ct));
        });

        app.MapPut("/api/fleet/map/settings", async (HttpContext ctx, FleetMapSettingsWriteDto request,
            Services.Fleet.FleetPositionService positions, AuditService audit, IHubContext<VmsHub> hub, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.FleetManage, out _) is { } failure) return failure;
            if (request.Mode is not ("none" or "raster" or "pmtiles")) return Error("Modo de mapa base desconocido.");
            if (request.Mode == "raster" && (request.RasterUrl is not { Length: > 10 } url || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || !url.Contains("{z}") || !url.Contains("{x}") || !url.Contains("{y}")))
                return Error("La plantilla de teselas debe ser https://… con {z}, {x} e {y}.");
            if (request.PositionRetentionDays is < 7 or > 3650) return Error("La retención de posiciones va de 7 a 3650 días.");
            if (request.Zoom is < 2 or > 18 || Math.Abs(request.CenterLatitude) > 85 || Math.Abs(request.CenterLongitude) > 180)
                return Error("Posición inicial inválida.");
            var before = await positions.LoadSettingsAsync(ct);
            await positions.SaveSettingsAsync(request with { RasterUrl = request.Mode == "raster" ? request.RasterUrl!.Trim() : null }, ct);
            await audit.LogAsync(ctx, "fleet", "map-settings-updated", detail:
                $"Mapa base: {ModeLabel(request.Mode)}; retención de posiciones {request.PositionRetentionDays} días" +
                (before.PositionRetentionDays != request.PositionRetentionDays ? $" (antes {before.PositionRetentionDays})" : "") + ".");
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "fleet-map", cancellationToken: ct);
            return Results.NoContent();
        });

        // Mapa base sin internet (PMTiles): lo lee el navegador por rangos, con la sesión del usuario.
        app.MapGet("/api/fleet/map/basemap.pmtiles", (HttpContext ctx, Services.Fleet.FleetPositionService positions) =>
        {
            if (ApiSecurity.RequireAny(ctx, out _, Permissions.FleetView, Permissions.FleetManage) is { } failure) return failure;
            return File.Exists(positions.PmtilesPath)
                ? Results.File(positions.PmtilesPath, "application/octet-stream", enableRangeProcessing: true)
                : Results.NotFound();
        });
    }

    private static string ModeLabel(string mode) => mode switch
    {
        "raster" => "teselas de un proveedor en línea",
        "pmtiles" => "archivo del servidor (sin internet)",
        _ => "sin mapa base",
    };
}
