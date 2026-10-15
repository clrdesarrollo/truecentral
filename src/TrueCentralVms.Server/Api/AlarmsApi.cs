using TrueCentralVms.Server.Services.Licensing;
using System.Net;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Isup;
using TrueCentralVms.Core.Isup;
using TrueCentralVms.Core.Domain;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// API del módulo Paneles de alarma: mantenedor de paneles (administrador),
/// estado de áreas y zonas, órdenes de armado/desarmado/anulación (cualquier
/// usuario con sesión, auditadas) e historial de eventos.
///
/// Los cambios de estado y los eventos en vivo NO se consultan por aquí:
/// llegan empujados por el hub (<see cref="VmsHubContract.AlarmPanelStateChanged"/>
/// y <see cref="VmsHubContract.AlarmEventReceived"/>).
/// </summary>
public static class AlarmsApi
{
    private const int MaxTake = 500;

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    /// <summary>
    /// Mensaje del choque de unicidad: un panel por IP se identifica por dirección y puerto; uno por ISUP, por su ID ISUP.
    /// </summary>
    private static string DuplicateMessage(string? isupId, bool other = false) =>
        $"Ya existe {(other ? "otro" : "un")} panel con " + (isupId is null ? "esa dirección y puerto." : $"el ID ISUP {isupId}.");

    /// <summary>¿La base rechazó la escritura por una clave única (PostgreSQL 23505)?</summary>
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    private static string? ValidateWrite(AlarmPanelWriteDto request, AlarmDriverRegistry drivers)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
            return "El nombre es obligatorio (máximo 128 caracteres).";
        if (drivers.Find(request.DriverKey) is not { } factory)
            return $"Driver desconocido: '{request.DriverKey}'.";
        // Por ISUP el panel no tiene dirección ni credenciales: lo identifican su ID y su clave (se validan aparte, porque en
        // edición la clave puede quedar la guardada).
        if (factory.UsesIsup) return null;
        if (string.IsNullOrWhiteSpace(request.Host))
            return "La dirección (IP o hostname) es obligatoria.";
        if (request.Port is < 1 or > 65535)
            return "El puerto debe estar entre 1 y 65535.";
        if (string.IsNullOrWhiteSpace(request.Username))
            return "El usuario del panel es obligatorio.";
        return null;
    }

    private static bool UsesIsup(AlarmDriverRegistry drivers, string driverKey) => drivers.Find(driverKey)?.UsesIsup == true;

    private static async Task<(AlarmPanelInfo? Info, AlarmPanelState? State, string? Error)> ProbeAsync(
        AlarmDriverRegistry drivers, string driverKey, AlarmConnectionInfo conn, CancellationToken ct)
    {
        var factory = drivers.Find(driverKey)!;
        try
        {
            var driver = factory.Create();
            var info = await driver.ProbeAsync(conn, ct);
            AlarmPanelState? state = null;
            try { state = await driver.GetStateAsync(conn, ct); }
            catch (DriverException) { /* el panel validó pero no entregó estado: se leerá en el sondeo */ }
            return (info, state, null);
        }
        catch (DriverException ex)
        {
            return (null, null, ex.Message);
        }
        catch (Exception ex)
        {
            return (null, null, $"Error inesperado al comunicarse con el panel: {ex.Message}");
        }
    }

    private static AlarmConnectionInfo ConnectionOf(AlarmPanel panel, CredentialProtector protector) =>
        new(panel.Host, panel.Port, panel.UseHttps, panel.Username, protector.Unprotect(panel.PasswordCiphertext), panel.IsupDeviceId);

    /// <summary>Cómo se muestra dónde está el panel en la bitácora: su dirección, o su ID ISUP.</summary>
    private static string Where(AlarmPanel panel) => panel.IsupDeviceId is { } isupId ? $"ISUP {isupId}" : $"{panel.Host}:{panel.Port}";

    public static void MapAlarmsApi(this WebApplication app)
    {
        // ------------------------------------------------------------------
        // Drivers disponibles
        // ------------------------------------------------------------------
        app.MapGet("/api/alarms/drivers", (HttpContext ctx, AlarmDriverRegistry drivers) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.Ok(drivers.All.Select(f => new AlarmDriverDto(f.DriverKey, f.DisplayName, f.DefaultPort, f.DefaultHttps, f.UsesIsup)));
        });

        // ------------------------------------------------------------------
        // Paneles
        // ------------------------------------------------------------------
        app.MapGet("/api/alarms/panels", async (HttpContext ctx, VmsDbContext db, AlarmPanelService service) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var panels = await db.AlarmPanels.AsNoTracking()
                .Include(p => p.Areas).Include(p => p.Zones).AsSplitQuery()
                .OrderBy(p => p.Name)
                .ToListAsync();
            // Alcance por ubicación: cada panel con solo sus áreas y zonas; sin ninguna, no aparece.
            var scope = await ctx.ScopeAsync(session);
            return Results.Ok(panels.Select(p => scope.Visible(AlarmMapper.ToDto(p, service.IsLive(p.Id), service.LastErrorOf(p.Id))))
                .Where(p => p is not null));
        });

        app.MapGet("/api/alarms/panels/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, AlarmPanelService service) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var panel = await db.AlarmPanels.AsNoTracking()
                .Include(p => p.Areas).Include(p => p.Zones).AsSplitQuery()
                .FirstOrDefaultAsync(p => p.Id == id);
            if (panel is null) return Results.NotFound();
            return (await ctx.ScopeAsync(session)).Visible(AlarmMapper.ToDto(panel, service.IsLive(id), service.LastErrorOf(id))) is { } dto
                ? Results.Ok(dto)
                : Results.NotFound();
        });

        app.MapPost("/api/alarms/panels", async (HttpContext ctx, AlarmPanelWriteDto request, VmsDbContext db, LicenseService license,
            AlarmDriverRegistry drivers, CredentialProtector protector, IHubContext<VmsHub> hub,
            AlarmPanelService service, AuditService audit, IsupService isup, IsupHostManager isupHost, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.AlarmsConfigure, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            bool viaIsup = UsesIsup(drivers, request.DriverKey);
            string? isupId = viaIsup ? request.IsupDeviceId?.Trim() : null;
            string? isupKey = viaIsup ? request.IsupKey : null;
            if (viaIsup)
            {
                if (IsupEquipmentRules.Validate(isupId, isupKey, keyStored: false) is { } isupError) return Error(isupError);
                if (await db.AlarmPanels.AnyAsync(p => p.IsupDeviceId == isupId, ct))
                    return Error(DuplicateMessage(isupId), StatusCodes.Status409Conflict);
                if (await IsupEquipmentRules.KeyConflictAsync(db, protector, isupId!, isupKey, IsupEquipmentRules.Alarm, null, ct) is { } conflict)
                    return Error(conflict, StatusCodes.Status409Conflict);
            }
            else
            {
                if (string.IsNullOrEmpty(request.Password))
                    return Error("La contraseña del panel es obligatoria.");
                if (await db.AlarmPanels.AnyAsync(p => p.IsupDeviceId == null && p.Host == request.Host.Trim() && p.Port == request.Port, ct))
                    return Error(DuplicateMessage(null), StatusCodes.Status409Conflict);
            }
            if (license.Deny(LicenseFeatures.ModuleAlarms, request.Enabled ? LicenseFeatures.AlarmPanels : null,
                    await db.AlarmPanels.CountAsync(p => p.Enabled, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "alarm-panel", request.Name?.Trim());
            var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, null, ct);
            if (locationError is not null) return Error(locationError, StatusCodes.Status404NotFound);

            var conn = viaIsup
                ? new AlarmConnectionInfo("", 0, false, "", "", isupId)
                : new AlarmConnectionInfo(request.Host.Trim(), request.Port, request.UseHttps, request.Username, request.Password!);
            // Por IP el panel tiene que contestar para darlo de alta. Por ISUP es al revés: el panel se conecta recién cuando el VMS
            // lo conoce, así que se guarda igual y queda esperándolo (si ya estaba conectado, se lee de una vez).
            AlarmPanelInfo? info = null;
            AlarmPanelState? state = null;
            string? waiting = null;
            if (!viaIsup || isup.IsOnline(isupId!))
            {
                (info, state, var probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
                if (info is null)
                {
                    await audit.LogAsync(ctx, "alarms", "panel-created",
                        targetType: "alarm-panel", targetName: request.Name.Trim(),
                        detail: $"Alta de panel rechazada por el equipo {(viaIsup ? $"ISUP {isupId}" : $"{request.Host}:{request.Port}")}: {probeError}",
                        success: false);
                    return Error(probeError!);
                }
            }
            else
            {
                waiting = await IsupEquipmentRules.NotConnectedMessageAsync(isup, isupHost, isupId!, "panel", ct);
            }

            var panel = new AlarmPanel
            {
                Name = request.Name.Trim(),
                DriverKey = request.DriverKey,
                Host = viaIsup ? "" : request.Host.Trim(),
                Port = viaIsup ? 0 : request.Port,
                UseHttps = !viaIsup && request.UseHttps,
                Username = viaIsup ? "" : request.Username,
                PasswordCiphertext = protector.Protect(viaIsup ? "" : request.Password!),
                IsupDeviceId = isupId,
                IsupKeyCiphertext = isupKey is null ? null : protector.Protect(isupKey),
                Enabled = request.Enabled,
                Model = info?.Model,
                SerialNumber = info?.SerialNumber,
                FirmwareVersion = info?.FirmwareVersion,
                Status = info is null ? AlarmPanelStatus.Offline : AlarmPanelStatus.Online,
                LastError = waiting,
                LastSeenAt = info is null ? null : DateTime.UtcNow,
                // Antes de sembrar: sus áreas y zonas entran en la misma ubicación.
                LocationId = locationId,
            };
            if (state is not null) SeedState(panel, state);
            db.AlarmPanels.Add(panel);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            // Carrera con otro administrador: antes llegaba al navegador como un «Error 500» pelado.
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                await audit.LogAsync(ctx, "alarms", "panel-created",
                    targetType: "alarm-panel", targetName: request.Name.Trim(),
                    detail: $"Alta de panel rechazada: ya existe otro panel {(viaIsup ? $"con el ID ISUP {isupId}" : $"en {request.Host.Trim()}:{request.Port}")}.",
                    success: false);
                return Error(DuplicateMessage(isupId), StatusCodes.Status409Conflict);
            }
            // El servicio ISUP acepta el registro del panel recién cuando está en el directorio.
            if (viaIsup) isup.InvalidateDirectory();

            await audit.LogAsync(ctx, "alarms", "panel-created",
                targetType: "alarm-panel", targetId: panel.Id.ToString(), targetName: panel.Name,
                detail: $"Agregó el panel de alarma '{panel.Name}' ({panel.DriverKey}, {Where(panel)}" +
                        (info is null ? ", a la espera de que se conecte por ISUP" : $", {panel.Areas.Count} áreas, {panel.Zones.Count} zonas") +
                        (panel.LocationId is null ? "" : $", en {EquipmentLocation.Describe(panel.LocationId)}") + ").",
                data: new { panel.Host, panel.Port, panel.UseHttps, panel.DriverKey, panel.IsupDeviceId, panel.Model, panel.SerialNumber, panel.LocationId });
            EquipmentLocation.Changed(ctx);
            service.RequestReconcile();
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "alarm-panels", cancellationToken: ct);
            return Results.Ok(AlarmMapper.ToDto(panel, false, waiting));
        });

        app.MapPut("/api/alarms/panels/{id:int}", async (HttpContext ctx, int id, AlarmPanelWriteDto request, VmsDbContext db, LicenseService license,
            AlarmDriverRegistry drivers, CredentialProtector protector, IHubContext<VmsHub> hub,
            AlarmPanelService service, AuditService audit, IsupService isup, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.AlarmsConfigure, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);

            var panel = await db.AlarmPanels.Include(p => p.Areas).Include(p => p.Zones).AsSplitQuery()
                .FirstOrDefaultAsync(p => p.Id == id, ct);
            if (panel is null) return Results.NotFound();
            bool viaIsup = UsesIsup(drivers, request.DriverKey);
            string? isupId = viaIsup ? request.IsupDeviceId?.Trim() : null;
            string? isupKey = viaIsup && !string.IsNullOrEmpty(request.IsupKey) ? request.IsupKey : null;
            // La clave guardada vale solo si el ID no cambió (es la de ese equipo).
            bool keyStored = panel.IsupKeyCiphertext is { Length: > 0 } && panel.IsupDeviceId == isupId;
            if (viaIsup)
            {
                if (IsupEquipmentRules.Validate(isupId, isupKey, keyStored) is { } isupError) return Error(isupError);
                if (await db.AlarmPanels.AnyAsync(p => p.Id != id && p.IsupDeviceId == isupId, ct))
                    return Error(DuplicateMessage(isupId, other: true), StatusCodes.Status409Conflict);
                string effectiveKey = IsupEquipmentRules.EffectiveKey(isupKey, panel.IsupKeyCiphertext, protector);
                if (await IsupEquipmentRules.KeyConflictAsync(db, protector, isupId!, effectiveKey, IsupEquipmentRules.Alarm, id, ct) is { } conflict)
                    return Error(conflict, StatusCodes.Status409Conflict);
            }
            else if (await db.AlarmPanels.AnyAsync(p => p.Id != id && p.IsupDeviceId == null && p.Host == request.Host.Trim() && p.Port == request.Port, ct))
                return Error(DuplicateMessage(null, other: true), StatusCodes.Status409Conflict);
            var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, panel.LocationId, ct);
            if (locationError is not null) return Error(locationError, StatusCodes.Status404NotFound);
            // Antes de releer el panel: las áreas y zonas nuevas entran ya en la ubicación nueva.
            int? previousLocation = panel.LocationId;
            panel.LocationId = locationId;

            string password = viaIsup ? ""
                : string.IsNullOrEmpty(request.Password) ? protector.Unprotect(panel.PasswordCiphertext) : request.Password;
            if (!viaIsup && password.Length == 0) return Error("La contraseña del panel es obligatoria.");
            string newHost = viaIsup ? "" : request.Host.Trim();
            int newPort = viaIsup ? 0 : request.Port;
            string newUser = viaIsup ? "" : request.Username;
            bool isupChanged = panel.IsupDeviceId != isupId || isupKey is not null;
            bool connectionChanged =
                panel.Host != newHost || panel.Port != newPort || panel.UseHttps != (!viaIsup && request.UseHttps) ||
                panel.Username != newUser || panel.DriverKey != request.DriverKey ||
                (!viaIsup && !string.IsNullOrEmpty(request.Password)) || isupChanged;
            if (connectionChanged && (!viaIsup || (!isupChanged && isup.IsOnline(isupId!))))
            {
                // Por IP (o ISUP sin cambiar ID ni clave, ya conectado) se valida contra el equipo antes de guardar. Con ID o clave
                // ISUP nuevos el panel tiene que volver a registrarse con ellos: se guarda y el sondeo lo lee cuando se conecte.
                var conn = new AlarmConnectionInfo(newHost, newPort, !viaIsup && request.UseHttps, newUser, password, isupId);
                var (info, state, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
                if (info is null) return Error(probeError!);
                panel.Model = info.Model;
                panel.SerialNumber = info.SerialNumber;
                panel.FirmwareVersion = info.FirmwareVersion;
                if (state is not null) SeedState(panel, state);
            }
            // La conexión cacheada apunta a las credenciales viejas.
            if (connectionChanged) HikvisionSessionForget(panel, protector);

            var changes = new List<string>();
            if (panel.Name != request.Name.Trim()) changes.Add($"nombre '{panel.Name}' → '{request.Name.Trim()}'");
            if (panel.DriverKey != request.DriverKey) changes.Add($"conexión {panel.DriverKey} → {request.DriverKey}");
            if (!viaIsup && (panel.Host != newHost || panel.Port != newPort))
                changes.Add($"dirección {Where(panel)} → {newHost}:{newPort}");
            if (!viaIsup && panel.UseHttps != request.UseHttps) changes.Add(request.UseHttps ? "HTTPS activado" : "HTTPS desactivado");
            if (!viaIsup && panel.Username != newUser) changes.Add($"usuario '{panel.Username}' → '{newUser}'");
            if (panel.IsupDeviceId != isupId) changes.Add($"ID ISUP '{panel.IsupDeviceId}' → '{isupId}'");
            if (!viaIsup && !string.IsNullOrEmpty(request.Password)) changes.Add("contraseña cambiada");
            if (isupKey is not null) changes.Add("clave ISUP cambiada");
            if (panel.Enabled != request.Enabled) changes.Add(request.Enabled ? "monitoreo activado" : "monitoreo desactivado");
            if (locationId != previousLocation)
                changes.Add(EquipmentLocation.Change(previousLocation, locationId));
            // Las áreas y zonas que estaban con el panel lo siguen; las ubicadas aparte se quedan.
            var movedAreas = EquipmentLocation.Follow(panel.Areas, previousLocation, locationId);
            var movedZones = EquipmentLocation.Follow(panel.Zones, previousLocation, locationId);

            panel.Name = request.Name.Trim();
            panel.DriverKey = request.DriverKey;
            panel.Host = newHost;
            panel.Port = newPort;
            panel.UseHttps = !viaIsup && request.UseHttps;
            panel.Username = newUser;
            panel.PasswordCiphertext = protector.Protect(password);
            panel.IsupDeviceId = isupId;
            if (!viaIsup) panel.IsupKeyCiphertext = null;
            else if (isupKey is not null) panel.IsupKeyCiphertext = protector.Protect(isupKey);
            if (!panel.Enabled && request.Enabled
                && license.Deny(LicenseFeatures.ModuleAlarms, LicenseFeatures.AlarmPanels,
                    await db.AlarmPanels.CountAsync(p => p.Enabled && p.Id != id, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "alarm-panel", panel.Name);
            panel.Enabled = request.Enabled;
            panel.UpdatedAt = DateTime.UtcNow;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                await audit.LogAsync(ctx, "alarms", "panel-updated",
                    targetType: "alarm-panel", targetId: panel.Id.ToString(), targetName: request.Name.Trim(),
                    detail: $"Modificación rechazada: ya existe otro panel {(isupId is null ? $"en {newHost}:{newPort}" : $"con el ID ISUP {isupId}")}.",
                    success: false);
                return Error(DuplicateMessage(isupId, other: true), StatusCodes.Status409Conflict);
            }
            if (isupChanged) isup.InvalidateDirectory();

            await audit.LogAsync(ctx, "alarms", "panel-updated",
                targetType: "alarm-panel", targetId: panel.Id.ToString(), targetName: panel.Name,
                detail: $"Modificó el panel '{panel.Name}': " + (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios."));
            await EquipmentLocation.LogFollowersAsync(ctx, audit, "alarm-panel", panel.Id, panel.Name, locationId,
                movedAreas.Where(x => x.Id != 0).Select(x => (ResourceKind.Partition, x.Id, x.Name))
                    .Concat(movedZones.Where(x => x.Id != 0).Select(x => (ResourceKind.Zone, x.Id, x.Name))).ToList());
            if (locationId != previousLocation) await EquipmentLocation.MovedAsync(ctx, ct);
            else if (connectionChanged) EquipmentLocation.Changed(ctx);

            if (connectionChanged || !panel.Enabled) await service.DetachAsync(panel.Id);
            service.RequestReconcile();
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "alarm-panels", cancellationToken: ct);
            return Results.Ok(AlarmMapper.ToDto(panel, service.IsLive(panel.Id), null));
        });

        app.MapDelete("/api/alarms/panels/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            CredentialProtector protector, IHubContext<VmsHub> hub, AlarmPanelService service, AuditService audit,
            IsupService isup, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.AlarmsConfigure, out _) is { } failure) return failure;
            var panel = await db.AlarmPanels.FindAsync([id], ct);
            if (panel is null) return Results.NotFound();

            await service.DetachAsync(id);
            HikvisionSessionForget(panel, protector);
            db.AlarmPanels.Remove(panel); // áreas y zonas caen por cascada; el historial se conserva
            await db.SaveChangesAsync(ct);
            // Sin panel en el VMS, el servicio ISUP deja de aceptar su registro (y lo desconecta si estaba conectado).
            if (panel.IsupDeviceId is not null) isup.InvalidateDirectory();
            await audit.LogAsync(ctx, "alarms", "panel-deleted",
                targetType: "alarm-panel", targetId: id.ToString(), targetName: panel.Name,
                detail: $"Eliminó el panel de alarma '{panel.Name}' ({Where(panel)}). El historial de eventos se conserva.");
            EquipmentLocation.Changed(ctx);
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "alarm-panels", cancellationToken: ct);
            return Results.Ok();
        });

        // ------------------------------------------------------------------
        // Probar conexión (sin persistir). Con ?panelId= reutiliza la contraseña guardada.
        // ------------------------------------------------------------------
        app.MapPost("/api/alarms/panels/probe", async (HttpContext ctx, AlarmPanelWriteDto request, VmsDbContext db,
            AlarmDriverRegistry drivers, CredentialProtector protector, AuditService audit, IsupService isup, IsupHostManager isupHost,
            int? panelId, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.AlarmsConfigure, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);

            AlarmConnectionInfo conn;
            string target;
            if (UsesIsup(drivers, request.DriverKey))
            {
                // Por ISUP no hay nada que «probar» desde aquí: o el panel ya está conectado al servidor, o se explica qué falta.
                string? isupId = request.IsupDeviceId?.Trim();
                if (IsupGateway.ValidateDeviceId(isupId) is { } idError) return Error(idError);
                target = $"ISUP {isupId}";
                if (!isup.IsOnline(isupId!))
                {
                    var why = await IsupEquipmentRules.NotConnectedMessageAsync(isup, isupHost, isupId!, "panel", ct);
                    await audit.LogAsync(ctx, "alarms", "panel-probed", targetType: "alarm-panel", targetName: request.Name.Trim(),
                        detail: $"Probó la conexión con el panel {target}: {why}", success: false);
                    return Results.Ok(new AlarmPanelProbeResultDto(false, why, null, null, null, [], []));
                }
                conn = new AlarmConnectionInfo("", 0, false, "", "", isupId);
            }
            else
            {
                string? password = request.Password;
                if (string.IsNullOrEmpty(password) && panelId is int existingId &&
                    await db.AlarmPanels.FindAsync([existingId], ct) is { } existing)
                    password = protector.Unprotect(existing.PasswordCiphertext);
                if (string.IsNullOrEmpty(password))
                    return Error("La contraseña del panel es obligatoria.");
                conn = new AlarmConnectionInfo(request.Host.Trim(), request.Port, request.UseHttps, request.Username, password);
                target = $"{conn.Host}:{conn.Port}";
            }

            var (info, state, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
            await audit.LogAsync(ctx, "alarms", "panel-probed",
                targetType: "alarm-panel", targetName: request.Name.Trim(),
                detail: info is null
                    ? $"Probó la conexión con el panel {target}: {probeError}"
                    : $"Probó la conexión con el panel {target}: correcta ({info.Model}).",
                success: info is not null);
            if (info is null)
                return Results.Ok(new AlarmPanelProbeResultDto(false, probeError, null, null, null, [], []));

            var areas = state?.Areas.Select(a => new AlarmAreaDto(a.Number, a.Name, a.Enabled, a.ArmState, a.InAlarm,
                state.Zones.Count(z => z.AreaNumber == a.Number))).ToList() ?? [];
            var zones = state?.Zones.Select(z => new AlarmZoneDto(z.Number, z.AreaNumber, z.Name, z.ZoneType, z.DetectorType,
                z.Status, z.Bypassed, z.Armed, z.InAlarm, z.Tamper, z.LowBattery, z.Signal, z.Model)).ToList() ?? [];
            return Results.Ok(new AlarmPanelProbeResultDto(true, null, info.Model, info.SerialNumber, info.FirmwareVersion, areas, zones));
        });

        // ------------------------------------------------------------------
        // Refresco a pedido del estado
        // ------------------------------------------------------------------
        app.MapPost("/api/alarms/panels/{id:int}/refresh", async (HttpContext ctx, int id, VmsDbContext db,
            AlarmPanelService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAny(ctx, out var session, Permissions.AlarmsMonitor, Permissions.AlarmsConfigure) is { } failure) return failure;
            var panel = await db.AlarmPanels.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            if (panel is null) return Results.NotFound();
            var scope = await ctx.ScopeAsync(session);
            if (!scope.CanViewPanel(id))
                return await ctx.OutOfScopeAsync(session, "alarm-panel", id.ToString(), panel.Name, "actualizar el estado de");
            if (!panel.Enabled) return Error("El panel tiene el monitoreo desactivado.");

            if (audit.ShouldLog($"alarm-refresh:{session.UserId}:{id}", TimeSpan.FromMinutes(5)))
                await audit.LogAsync(ctx, "alarms", "panel-refreshed",
                    targetType: "alarm-panel", targetId: id.ToString(), targetName: panel.Name,
                    detail: $"Pidió actualizar el estado del panel '{panel.Name}'.");
            var dto = await service.RefreshAsync(id, ct);
            if (dto is null)
                return Error(service.LastErrorOf(id) ?? "El panel no respondió.", StatusCodes.Status502BadGateway);
            return Results.Ok(scope.Visible(dto));
        });

        // ------------------------------------------------------------------
        // Notificación HTTP empujada por el panel ("HTTP Host Notification")
        // ------------------------------------------------------------------
        // Anónima a propósito: los paneles Hikvision solo saben empujar sin
        // autenticación (httpAuthenticationMethod=none). Se acepta únicamente
        // desde la dirección de un panel habilitado, y el cuerpo nunca se
        // toma como verdad: sirve para adelantar la relectura del estado y,
        // si el driver lo entiende, para registrar el evento con su código.
        // En el panel: Notificación HTTP (httpHosts) → http://<servidor>:5090/api/alarms/push
        app.MapGet("/api/alarms/push", () => Results.Text("CLR TrueCentral VMS OK"));
        app.MapPost("/api/alarms/push", async (HttpContext ctx, VmsDbContext db, AlarmDriverRegistry drivers,
            AlarmPanelService service, ILoggerFactory loggers, CancellationToken ct) =>
        {
            const int maxBody = 256 * 1024;
            var log = loggers.CreateLogger("TrueCentralVms.Server.Api.AlarmsPush");
            var remote = ctx.Connection.RemoteIpAddress;
            if (remote is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            string ip = remote.ToString();

            var panels = await db.AlarmPanels.AsNoTracking().Where(p => p.Enabled).ToListAsync(ct);
            var panel = panels.FirstOrDefault(p => string.Equals(p.Host, ip, StringComparison.OrdinalIgnoreCase))
                        ?? panels.FirstOrDefault(p => IPAddress.TryParse(p.Host, out var host) && host.Equals(remote));
            if (panel is null)
            {
                log.LogWarning("Notificación de panel desde {Ip} ignorada: no hay panel habilitado con esa dirección.", ip);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            if (ctx.Request.ContentLength is > maxBody)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            byte[] body;
            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await ctx.Request.Body.ReadAsync(chunk, ct)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > maxBody) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }
                body = buffer.ToArray();
            }

            AlarmPanelEvent? evt = null;
            if (drivers.Find(panel.DriverKey) is { } factory)
            {
                try { evt = factory.Create().ParsePushedEvent(ctx.Request.ContentType ?? "", body); }
                catch (Exception ex) { log.LogDebug(ex, "Notificación del panel '{Name}' no traducible.", panel.Name); }
            }
            service.NotifyPushed(panel.Id, evt);
            log.LogDebug("Notificación del panel '{Name}' ({Ip}, {Bytes} bytes): {Result}.", panel.Name, ip, body.Length,
                evt is null ? "relectura de estado" : $"evento {evt.Kind} — {evt.Description}");
            return Results.Ok();
        });

        // ------------------------------------------------------------------
        // Órdenes: armar / desarmar / borrar alarma (área 0 = todas)
        // ------------------------------------------------------------------
        app.MapPost("/api/alarms/panels/{id:int}/areas/{area:int}/arm", async (HttpContext ctx, int id, int area,
            AlarmArmRequestDto request, VmsDbContext db, AlarmPanelService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.AlarmsOperate, out var session) is { } failure) return failure;
            var panel = await LoadForCommandAsync(db, id, ct);
            if (panel is null) return Results.NotFound();
            if (area > 0 && panel.Areas.All(a => a.Number != area)) return Error("El área no existe en el panel.");
            if (await AreaOutOfScopeAsync(ctx, session, panel, area, "armar") is { } outOfScope) return outOfScope;
            if (panel.PanelTamper)
                return Error("No se puede armar: la tapa del panel está abierta (sabotaje). Ciérrela antes de armar.");
            string areaName = AreaLabel(panel, area);
            string modeLabel = request.Mode == AlarmArmMode.Stay ? "parcial (en casa)" : "total (fuera)";
            try
            {
                var dto = await service.ExecuteAsync(panel,
                    (driver, conn) => driver.ArmAsync(conn, area, request.Mode, ct),
                    AlarmEventKind.Arm, $"Armado {modeLabel} desde el VMS", area, null, session.Username, ct);
                await audit.LogAsync(ctx, "alarms", "area-armed",
                    targetType: "alarm-area", targetId: $"{id}/{area}", targetName: $"{panel.Name} · {areaName}",
                    detail: $"Armó ({modeLabel}) {areaName} del panel '{panel.Name}'.");
                return Results.Ok((await ctx.ScopeAsync(session)).Visible(dto));
            }
            catch (DriverException ex)
            {
                await audit.LogAsync(ctx, "alarms", "area-armed",
                    targetType: "alarm-area", targetId: $"{id}/{area}", targetName: $"{panel.Name} · {areaName}",
                    detail: $"El panel '{panel.Name}' rechazó armar {areaName}: {ex.Message}", success: false);
                return Error(ex.Message, StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/api/alarms/panels/{id:int}/areas/{area:int}/disarm", async (HttpContext ctx, int id, int area,
            VmsDbContext db, AlarmPanelService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.AlarmsOperate, out var session) is { } failure) return failure;
            var panel = await LoadForCommandAsync(db, id, ct);
            if (panel is null) return Results.NotFound();
            if (area > 0 && panel.Areas.All(a => a.Number != area)) return Error("El área no existe en el panel.");
            if (await AreaOutOfScopeAsync(ctx, session, panel, area, "desarmar") is { } outOfScope) return outOfScope;
            string areaName = AreaLabel(panel, area);
            try
            {
                var dto = await service.ExecuteAsync(panel,
                    (driver, conn) => driver.DisarmAsync(conn, area, ct),
                    AlarmEventKind.Disarm, "Desarmado desde el VMS", area, null, session.Username, ct);
                await audit.LogAsync(ctx, "alarms", "area-disarmed",
                    targetType: "alarm-area", targetId: $"{id}/{area}", targetName: $"{panel.Name} · {areaName}",
                    detail: $"Desarmó {areaName} del panel '{panel.Name}'.");
                return Results.Ok((await ctx.ScopeAsync(session)).Visible(dto));
            }
            catch (DriverException ex)
            {
                await audit.LogAsync(ctx, "alarms", "area-disarmed",
                    targetType: "alarm-area", targetId: $"{id}/{area}", targetName: $"{panel.Name} · {areaName}",
                    detail: $"El panel '{panel.Name}' rechazó desarmar {areaName}: {ex.Message}", success: false);
                return Error(ex.Message, StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/api/alarms/panels/{id:int}/areas/{area:int}/clear-alarm", async (HttpContext ctx, int id, int area,
            VmsDbContext db, AlarmPanelService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.AlarmsOperate, out var session) is { } failure) return failure;
            var panel = await LoadForCommandAsync(db, id, ct);
            if (panel is null) return Results.NotFound();
            if (area > 0 && panel.Areas.All(a => a.Number != area)) return Error("El área no existe en el panel.");
            if (await AreaOutOfScopeAsync(ctx, session, panel, area, "borrar la alarma de") is { } outOfScope) return outOfScope;
            string areaName = AreaLabel(panel, area);
            try
            {
                var dto = await service.ExecuteAsync(panel,
                    (driver, conn) => driver.ClearAlarmAsync(conn, area, ct),
                    AlarmEventKind.Info, "Alarma borrada/silenciada desde el VMS", area, null, session.Username, ct);
                await audit.LogAsync(ctx, "alarms", "alarm-cleared",
                    targetType: "alarm-area", targetId: $"{id}/{area}", targetName: $"{panel.Name} · {areaName}",
                    detail: $"Borró/silenció la alarma de {areaName} del panel '{panel.Name}'.");
                return Results.Ok((await ctx.ScopeAsync(session)).Visible(dto));
            }
            catch (DriverException ex)
            {
                await audit.LogAsync(ctx, "alarms", "alarm-cleared",
                    targetType: "alarm-area", targetId: $"{id}/{area}", targetName: $"{panel.Name} · {areaName}",
                    detail: $"El panel '{panel.Name}' rechazó borrar la alarma de {areaName}: {ex.Message}", success: false);
                return Error(ex.Message, StatusCodes.Status502BadGateway);
            }
        });

        // ------------------------------------------------------------------
        // Zonas: anular (bypass) / restituir
        // ------------------------------------------------------------------
        app.MapPost("/api/alarms/panels/{id:int}/zones/{zone:int}/bypass", async (HttpContext ctx, int id, int zone,
            AlarmZoneBypassRequestDto request, VmsDbContext db, AlarmPanelService service, AuditService audit,
            CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.AlarmsOperate, out var session) is { } failure) return failure;
            var panel = await LoadForCommandAsync(db, id, ct);
            if (panel is null) return Results.NotFound();
            var target = panel.Zones.FirstOrDefault(z => z.Number == zone);
            if (target is null) return Error("La zona no existe en el panel.");
            if (!(await ctx.ScopeAsync(session)).CanOperateZone(panel.Id, target.Number))
                return await ctx.OutOfScopeAsync(session, "alarm-zone", $"{id}/{zone}", $"{panel.Name} · {target.Name}",
                    request.Bypassed ? "anular" : "restituir");
            string action = request.Bypassed ? "zone-bypassed" : "zone-restored";
            try
            {
                var dto = await service.ExecuteAsync(panel,
                    (driver, conn) => driver.SetZoneBypassAsync(conn, zone, request.Bypassed, ct),
                    AlarmEventKind.Bypass,
                    request.Bypassed ? "Zona anulada (bypass) desde el VMS" : "Zona restituida desde el VMS",
                    target.AreaNumber, zone, session.Username, ct);
                await audit.LogAsync(ctx, "alarms", action,
                    targetType: "alarm-zone", targetId: $"{id}/{zone}", targetName: $"{panel.Name} · {target.Name}",
                    detail: $"{(request.Bypassed ? "Anuló" : "Restituyó")} la zona '{target.Name}' del panel '{panel.Name}'.");
                return Results.Ok((await ctx.ScopeAsync(session)).Visible(dto));
            }
            catch (DriverException ex)
            {
                await audit.LogAsync(ctx, "alarms", action,
                    targetType: "alarm-zone", targetId: $"{id}/{zone}", targetName: $"{panel.Name} · {target.Name}",
                    detail: $"El panel '{panel.Name}' rechazó {(request.Bypassed ? "anular" : "restituir")} la zona '{target.Name}': {ex.Message}",
                    success: false);
                return Error(ex.Message, StatusCodes.Status502BadGateway);
            }
        });

        // ------------------------------------------------------------------
        // Historial de eventos
        // ------------------------------------------------------------------
        app.MapGet("/api/alarms/events", async (HttpContext ctx, VmsDbContext db, AuditService audit,
            int? panelId, string? kind, string? severity, DateTime? from, DateTime? to, string? text, int? take) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.AlarmsMonitor, out var session) is { } failure) return failure;

            bool filtered = panelId is > 0 || !string.IsNullOrWhiteSpace(kind) || !string.IsNullOrWhiteSpace(severity)
                            || from is not null || to is not null || !string.IsNullOrWhiteSpace(text);
            if (filtered || audit.ShouldLog($"alarm-search:{session.UserId}", TimeSpan.FromMinutes(5)))
                await audit.LogAsync(ctx, "alarms", "search",
                    detail: filtered
                        ? "Buscó en el historial de eventos de alarma" +
                          (panelId is > 0 ? $" (panel {panelId})" : "") +
                          (string.IsNullOrWhiteSpace(kind) ? "" : $" (tipo {kind})") +
                          (string.IsNullOrWhiteSpace(text) ? "" : $" (texto '{text.Trim()}')") +
                          (from is { } f1 ? $" desde {f1:dd-MM-yyyy HH:mm}" : "") +
                          (to is { } t1 ? $" hasta {t1:dd-MM-yyyy HH:mm}" : "") + "."
                        : "Consultó el historial de eventos de alarma.",
                    data: new { panelId, kind, severity, from, to, text, take });

            var query = db.AlarmEvents.AsNoTracking().AsQueryable();
            if (panelId is > 0) query = query.Where(e => e.AlarmPanelId == panelId);
            // Uno o varios tipos separados por coma ("ZoneTriggered,ZoneRestored" = sensores).
            if (!string.IsNullOrWhiteSpace(kind))
            {
                var kinds = kind.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(k => Enum.TryParse<AlarmEventKind>(k, true, out var parsed) ? parsed : (AlarmEventKind?)null)
                    .Where(k => k is not null).Select(k => k!.Value).ToList();
                if (kinds.Count == 1) { var k = kinds[0]; query = query.Where(e => e.Kind == k); }
                else if (kinds.Count > 1) query = query.Where(e => kinds.Contains(e.Kind));
            }
            if (!string.IsNullOrWhiteSpace(severity) && Enum.TryParse<AlarmSeverity>(severity, true, out var s))
                query = query.Where(e => e.Severity == s);
            if (from is { } fromValue) query = query.Where(e => e.Timestamp >= DateTime.SpecifyKind(fromValue, DateTimeKind.Utc));
            if (to is { } toValue) query = query.Where(e => e.Timestamp <= DateTime.SpecifyKind(toValue, DateTimeKind.Utc));
            if (!string.IsNullOrWhiteSpace(text))
            {
                string needle = $"%{text.Trim()}%";
                query = query.Where(e => EF.Functions.ILike(e.Description, needle)
                                         || (e.ZoneName != null && EF.Functions.ILike(e.ZoneName, needle))
                                         || (e.AreaName != null && EF.Functions.ILike(e.AreaName, needle))
                                         || (e.Operator != null && EF.Functions.ILike(e.Operator, needle)));
            }
            // Alcance por ubicación: el evento de una zona va con su zona, el de un
            // área con su área y el del panel entero (corriente, sabotaje) con el
            // panel. En la consulta, para que el tope de filas cuente solo los suyos.
            var scope = await ctx.ScopeAsync(session);
            if (scope.FiltersView)
            {
                var (areaKeys, zoneKeys, panelIds) = scope.VisibleAlarmKeys();
                query = query.Where(e =>
                    (e.ZoneNumber != null && zoneKeys.Contains((long)e.AlarmPanelId * 100_000 + e.ZoneNumber.Value)) ||
                    (e.ZoneNumber == null && e.AreaNumber != null && areaKeys.Contains((long)e.AlarmPanelId * 100_000 + e.AreaNumber.Value)) ||
                    (e.ZoneNumber == null && e.AreaNumber == null && panelIds.Contains(e.AlarmPanelId)));
            }

            var events = await query
                .OrderByDescending(e => e.ReceivedAt)
                .Take(Math.Clamp(take ?? 200, 1, MaxTake))
                .ToListAsync();
            return Results.Ok(events.Select(AlarmMapper.ToDto));
        });
    }

    private static Task<AlarmPanel?> LoadForCommandAsync(VmsDbContext db, int id, CancellationToken ct) =>
        db.AlarmPanels.AsNoTracking().Include(p => p.Areas).Include(p => p.Zones).AsSplitQuery().FirstOrDefaultAsync(p => p.Id == id, ct);

    /// <summary>
    /// Alcance por ubicación de una orden sobre un área. El área 0 ("todas")
    /// exige poder operar todas las del panel: un operador de un edificio no
    /// arma el panel entero si comparte áreas con otro.
    /// </summary>
    private static async Task<IResult?> AreaOutOfScopeAsync(HttpContext ctx, SessionInfo session, AlarmPanel panel, int area,
        string attempted)
    {
        var scope = await ctx.ScopeAsync(session);
        bool allowed = scope.Unrestricted || (area == 0
            ? panel.Areas.Count > 0 && panel.Areas.All(a => scope.CanOperateArea(panel.Id, a.Number))
            : panel.Areas.FirstOrDefault(a => a.Number == area) is { } target && scope.CanOperateArea(panel.Id, target.Number));
        return allowed
            ? null
            : await ctx.OutOfScopeAsync(session, "alarm-area", $"{panel.Id}/{area}", $"{panel.Name} · {AreaLabel(panel, area)}", attempted);
    }

    private static string AreaLabel(AlarmPanel panel, int area) =>
        area <= 0 ? "todas las áreas" : $"el área '{panel.Areas.First(a => a.Number == area).Name}'";

    /// <summary>
    /// Carga áreas y zonas leídas en el sondeo de alta/edición. Las que ya
    /// existían se actualizan EN SU LUGAR (por número) en vez de recrearse: así
    /// cada una conserva su id y, con él, lo que un administrador le asignó en
    /// Recursos (ubicación, ficha, cámaras asociadas).
    /// </summary>
    private static void SeedState(AlarmPanel panel, AlarmPanelState state)
    {
        var now = DateTime.UtcNow;
        var areas = panel.Areas.ToDictionary(a => a.Number);
        var seenAreas = new HashSet<int>();
        foreach (var a in state.Areas)
        {
            if (!seenAreas.Add(a.Number)) continue; // número repetido en la lectura: vale el primero
            if (!areas.TryGetValue(a.Number, out var area))
                panel.Areas.Add(area = new AlarmArea { Number = a.Number, LocationId = panel.LocationId });
            area.Name = a.Name;
            area.Enabled = a.Enabled;
            area.ArmState = a.ArmState;
            area.InAlarm = a.InAlarm;
            area.UpdatedAt = now;
        }
        panel.Areas.RemoveAll(a => !seenAreas.Contains(a.Number));

        var zones = panel.Zones.ToDictionary(z => z.Number);
        var seenZones = new HashSet<int>();
        foreach (var z in state.Zones)
        {
            if (!seenZones.Add(z.Number)) continue;
            if (!zones.TryGetValue(z.Number, out var zone))
                panel.Zones.Add(zone = new AlarmZone { Number = z.Number, LocationId = panel.LocationForNewZone(z.AreaNumber) });
            zone.AreaNumber = z.AreaNumber;
            zone.Name = z.Name;
            zone.ZoneType = z.ZoneType;
            zone.DetectorType = z.DetectorType;
            zone.Status = z.Status;
            zone.Bypassed = z.Bypassed;
            zone.Armed = z.Armed;
            zone.InAlarm = z.InAlarm;
            zone.Tamper = z.Tamper;
            zone.LowBattery = z.LowBattery;
            zone.Signal = z.Signal;
            zone.Model = z.Model;
            zone.UpdatedAt = now;
        }
        panel.Zones.RemoveAll(z => !seenZones.Contains(z.Number));
        panel.LastStateAt = now;
    }

    /// <summary>Olvida la conexión HTTP cacheada del driver Hikvision (credenciales cambiadas / panel eliminado).</summary>
    private static void HikvisionSessionForget(AlarmPanel panel, CredentialProtector protector)
    {
        if (panel.PasswordCiphertext.Length == 0) return;
        try
        {
            Drivers.Hikvision.HikvisionIsapiClient.Forget(ConnectionOf(panel, protector));
        }
        catch { /* mejor esfuerzo */ }
    }
}
