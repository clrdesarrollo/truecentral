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
using TrueCentralVms.Drivers.Hikvision;

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

    private static string? ValidateWrite(AlarmPanelWriteDto request, AlarmDriverRegistry drivers)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
            return "El nombre es obligatorio (máximo 128 caracteres).";
        if (string.IsNullOrWhiteSpace(request.Host))
            return "La dirección (IP o hostname) es obligatoria.";
        if (request.Port is < 1 or > 65535)
            return "El puerto debe estar entre 1 y 65535.";
        if (string.IsNullOrWhiteSpace(request.Username))
            return "El usuario del panel es obligatorio.";
        if (drivers.Find(request.DriverKey) is null)
            return $"Driver desconocido: '{request.DriverKey}'.";
        return null;
    }

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
        new(panel.Host, panel.Port, panel.UseHttps, panel.Username, protector.Unprotect(panel.PasswordCiphertext), panel.GatewayDeviceId);

    /// <summary>
    /// ¿La dirección apunta a la receptora que el instalador dejó en este mismo
    /// equipo? Se aceptan las tres formas de nombrar el loopback.
    /// </summary>
    private static bool IsLocalReceiver(LocalIpReceiverService local, string? host, int port) =>
        local.Enabled && port == local.Port && host is not null &&
        (string.Equals(host.Trim(), local.Host, StringComparison.OrdinalIgnoreCase) ||
         host.Trim() is "127.0.0.1" or "localhost" or "::1" or "[::1]");

    /// <summary>
    /// Contraseña que el servidor guarda de la receptora local. Permite agregar
    /// paneles sin que nadie escriba —ni conozca— esa credencial: la generó el
    /// propio servidor al activar la receptora.
    /// </summary>
    private static string? LocalReceiverPassword(LocalIpReceiverService local, string? host, int port) =>
        IsLocalReceiver(local, host, port) ? local.GetPassword() : null;

    /// <summary>
    /// Arma la conexión con la receptora. La contraseña puede venir en el
    /// cuerpo o tomarse de un panel ya guardado (PanelId), para no obligar a
    /// reescribirla cada vez que se administra la lista de equipos.
    /// </summary>
    private static async Task<(AlarmConnectionInfo? Connection, string? Error)> ReceiverConnectionAsync(
        AlarmReceiverConnectionDto? request, VmsDbContext db, CredentialProtector protector,
        LocalIpReceiverService local, CancellationToken ct)
    {
        if (request is null)
            return (null, "Faltan los datos de la receptora.");

        string host = (request.Host ?? "").Trim();
        string username = (request.Username ?? "").Trim();
        int port = request.Port;
        bool useHttps = request.UseHttps;
        string? password = request.Password;

        if (request.PanelId is int panelId)
        {
            var panel = await db.AlarmPanels.AsNoTracking().FirstOrDefaultAsync(p => p.Id == panelId, ct);
            if (panel is null)
                return (null, "El panel indicado ya no existe.");
            if (host.Length == 0) host = panel.Host;
            if (username.Length == 0) username = panel.Username;
            if (port is < 1 or > 65535) { port = panel.Port; useHttps = panel.UseHttps; }
            if (string.IsNullOrEmpty(password)) password = protector.Unprotect(panel.PasswordCiphertext);
        }

        // Receptora local: la credencial la pone el servidor.
        if (string.IsNullOrEmpty(password) && LocalReceiverPassword(local, host, port) is { } localPassword)
        {
            password = localPassword;
            if (username.Length == 0) username = LocalIpReceiverService.AdminUser;
        }

        if (host.Length == 0) return (null, "La dirección de la receptora es obligatoria.");
        if (port is < 1 or > 65535) return (null, "El puerto debe estar entre 1 y 65535.");
        if (username.Length == 0) return (null, "El usuario de la receptora es obligatorio.");
        if (string.IsNullOrEmpty(password)) return (null, "La contraseña de la receptora es obligatoria.");

        return (new AlarmConnectionInfo(host, port, useHttps, username, password), null);
    }

    private static string? DeviceIdOf(AlarmPanelWriteDto request) =>
        string.IsNullOrWhiteSpace(request.DeviceId) ? null : request.DeviceId.Trim();

    public static void MapAlarmsApi(this WebApplication app)
    {
        // ------------------------------------------------------------------
        // Drivers disponibles
        // ------------------------------------------------------------------
        app.MapGet("/api/alarms/drivers", (HttpContext ctx, AlarmDriverRegistry drivers) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.Ok(drivers.All.Select(f => new AlarmDriverDto(f.DriverKey, f.DisplayName, f.DefaultPort, f.DefaultHttps, f.NeedsDeviceId)));
        });

        // ------------------------------------------------------------------
        // Paneles
        // ------------------------------------------------------------------
        app.MapGet("/api/alarms/panels", async (HttpContext ctx, VmsDbContext db, AlarmPanelService service) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var panels = await db.AlarmPanels.AsNoTracking()
                .Include(p => p.Areas).Include(p => p.Zones).AsSplitQuery()
                .OrderBy(p => p.Name)
                .ToListAsync();
            return Results.Ok(panels.Select(p => AlarmMapper.ToDto(p, service.IsLive(p.Id), service.LastErrorOf(p.Id))));
        });

        app.MapGet("/api/alarms/panels/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, AlarmPanelService service) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var panel = await db.AlarmPanels.AsNoTracking()
                .Include(p => p.Areas).Include(p => p.Zones).AsSplitQuery()
                .FirstOrDefaultAsync(p => p.Id == id);
            if (panel is null) return Results.NotFound();
            return Results.Ok(AlarmMapper.ToDto(panel, service.IsLive(id), service.LastErrorOf(id)));
        });

        app.MapPost("/api/alarms/panels", async (HttpContext ctx, AlarmPanelWriteDto request, VmsDbContext db, LicenseService license,
            AlarmDriverRegistry drivers, CredentialProtector protector, IHubContext<VmsHub> hub,
            AlarmPanelService service, AuditService audit, LocalIpReceiverService local, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            // Con la receptora que instala la suite no se pide contraseña: la
            // generó el servidor al activarla y nadie tiene que conocerla.
            request = request with { Password = request.Password is { Length: > 0 } typed ? typed
                : LocalReceiverPassword(local, request.Host, request.Port) };
            if (string.IsNullOrEmpty(request.Password))
                return Error("La contraseña del panel es obligatoria.");
            if (license.Deny(LicenseFeatures.ModuleAlarms, request.Enabled ? LicenseFeatures.AlarmPanels : null,
                    await db.AlarmPanels.CountAsync(p => p.Enabled, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "alarm-panel", request.Name?.Trim());
            string? deviceId = DeviceIdOf(request);
            // En una pasarela varios paneles comparten dirección y puerto: lo
            // que no puede repetirse es el equipo dentro de ella.
            if (await db.AlarmPanels.AnyAsync(p => p.Host == request.Host.Trim() && p.Port == request.Port && p.GatewayDeviceId == deviceId, ct))
                return Error(deviceId is null ? "Ya existe un panel con esa dirección y puerto." : "Ya existe un panel con ese equipo en esa pasarela.",
                    StatusCodes.Status409Conflict);

            var conn = new AlarmConnectionInfo(request.Host.Trim(), request.Port, request.UseHttps, request.Username, request.Password, deviceId);
            var (info, state, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
            if (info is null)
            {
                await audit.LogAsync(ctx, "alarms", "panel-created",
                    targetType: "alarm-panel", targetName: request.Name.Trim(),
                    detail: $"Alta de panel rechazada por el equipo {request.Host}:{request.Port}: {probeError}", success: false);
                return Error(probeError!);
            }

            var panel = new AlarmPanel
            {
                Name = request.Name.Trim(),
                DriverKey = request.DriverKey,
                Host = request.Host.Trim(),
                Port = request.Port,
                UseHttps = request.UseHttps,
                Username = request.Username,
                PasswordCiphertext = protector.Protect(request.Password),
                GatewayDeviceId = deviceId,
                Enabled = request.Enabled,
                Model = info.Model,
                SerialNumber = info.SerialNumber,
                FirmwareVersion = info.FirmwareVersion,
                Status = AlarmPanelStatus.Online,
                LastSeenAt = DateTime.UtcNow,
            };
            if (state is not null) SeedState(panel, state);
            db.AlarmPanels.Add(panel);
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "alarms", "panel-created",
                targetType: "alarm-panel", targetId: panel.Id.ToString(), targetName: panel.Name,
                detail: $"Agregó el panel de alarma '{panel.Name}' ({panel.DriverKey}, {panel.Host}:{panel.Port}" +
                        (panel.GatewayDeviceId is null ? "" : $", equipo '{panel.GatewayDeviceId}' en la pasarela") +
                        $", {panel.Areas.Count} áreas, {panel.Zones.Count} zonas).",
                data: new { panel.Host, panel.Port, panel.UseHttps, panel.DriverKey, panel.GatewayDeviceId, panel.Model, panel.SerialNumber });
            service.RequestReconcile();
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "alarm-panels", cancellationToken: ct);
            return Results.Ok(AlarmMapper.ToDto(panel, false, null));
        });

        app.MapPut("/api/alarms/panels/{id:int}", async (HttpContext ctx, int id, AlarmPanelWriteDto request, VmsDbContext db, LicenseService license,
            AlarmDriverRegistry drivers, CredentialProtector protector, IHubContext<VmsHub> hub,
            AlarmPanelService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);

            var panel = await db.AlarmPanels.Include(p => p.Areas).Include(p => p.Zones).AsSplitQuery()
                .FirstOrDefaultAsync(p => p.Id == id, ct);
            if (panel is null) return Results.NotFound();
            string? deviceId = DeviceIdOf(request);
            if (await db.AlarmPanels.AnyAsync(p => p.Id != id && p.Host == request.Host.Trim() && p.Port == request.Port && p.GatewayDeviceId == deviceId, ct))
                return Error(deviceId is null ? "Ya existe otro panel con esa dirección y puerto." : "Ya existe otro panel con ese equipo en esa pasarela.",
                    StatusCodes.Status409Conflict);

            string password = string.IsNullOrEmpty(request.Password)
                ? protector.Unprotect(panel.PasswordCiphertext)
                : request.Password;
            bool connectionChanged =
                panel.Host != request.Host.Trim() || panel.Port != request.Port || panel.UseHttps != request.UseHttps ||
                panel.Username != request.Username || panel.DriverKey != request.DriverKey ||
                panel.GatewayDeviceId != deviceId || !string.IsNullOrEmpty(request.Password);
            if (connectionChanged)
            {
                var conn = new AlarmConnectionInfo(request.Host.Trim(), request.Port, request.UseHttps, request.Username, password, deviceId);
                var (info, state, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
                if (info is null) return Error(probeError!);
                panel.Model = info.Model;
                panel.SerialNumber = info.SerialNumber;
                panel.FirmwareVersion = info.FirmwareVersion;
                if (state is not null) SeedState(panel, state);
                // La conexión cacheada apunta a las credenciales viejas.
                HikvisionSessionForget(panel, protector);
            }

            var changes = new List<string>();
            if (panel.Name != request.Name.Trim()) changes.Add($"nombre '{panel.Name}' → '{request.Name.Trim()}'");
            if (panel.Host != request.Host.Trim() || panel.Port != request.Port)
                changes.Add($"dirección {panel.Host}:{panel.Port} → {request.Host.Trim()}:{request.Port}");
            if (panel.UseHttps != request.UseHttps) changes.Add(request.UseHttps ? "HTTPS activado" : "HTTPS desactivado");
            if (panel.Username != request.Username) changes.Add($"usuario '{panel.Username}' → '{request.Username}'");
            if (panel.GatewayDeviceId != deviceId) changes.Add($"equipo en la pasarela '{panel.GatewayDeviceId}' → '{deviceId}'");
            if (!string.IsNullOrEmpty(request.Password)) changes.Add("contraseña cambiada");
            if (panel.Enabled != request.Enabled) changes.Add(request.Enabled ? "monitoreo activado" : "monitoreo desactivado");

            panel.Name = request.Name.Trim();
            panel.DriverKey = request.DriverKey;
            panel.Host = request.Host.Trim();
            panel.Port = request.Port;
            panel.UseHttps = request.UseHttps;
            panel.Username = request.Username;
            panel.PasswordCiphertext = protector.Protect(password);
            panel.GatewayDeviceId = deviceId;
            if (!panel.Enabled && request.Enabled
                && license.Deny(LicenseFeatures.ModuleAlarms, LicenseFeatures.AlarmPanels,
                    await db.AlarmPanels.CountAsync(p => p.Enabled && p.Id != id, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "alarm-panel", panel.Name);
            panel.Enabled = request.Enabled;
            panel.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "alarms", "panel-updated",
                targetType: "alarm-panel", targetId: panel.Id.ToString(), targetName: panel.Name,
                detail: $"Modificó el panel '{panel.Name}': " + (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios."));

            if (connectionChanged || !panel.Enabled) await service.DetachAsync(panel.Id);
            service.RequestReconcile();
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "alarm-panels", cancellationToken: ct);
            return Results.Ok(AlarmMapper.ToDto(panel, service.IsLive(panel.Id), null));
        });

        app.MapDelete("/api/alarms/panels/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            CredentialProtector protector, IHubContext<VmsHub> hub, AlarmPanelService service, AuditService audit,
            CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var panel = await db.AlarmPanels.FindAsync([id], ct);
            if (panel is null) return Results.NotFound();

            await service.DetachAsync(id);
            HikvisionSessionForget(panel, protector);
            db.AlarmPanels.Remove(panel); // áreas y zonas caen por cascada; el historial se conserva
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "alarms", "panel-deleted",
                targetType: "alarm-panel", targetId: id.ToString(), targetName: panel.Name,
                detail: $"Eliminó el panel de alarma '{panel.Name}' ({panel.Host}:{panel.Port}). El historial de eventos se conserva.");
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "alarm-panels", cancellationToken: ct);
            return Results.Ok();
        });

        // ------------------------------------------------------------------
        // Probar conexión (sin persistir). Con ?panelId= reutiliza la clave guardada.
        // ------------------------------------------------------------------
        app.MapPost("/api/alarms/panels/probe", async (HttpContext ctx, AlarmPanelWriteDto request, VmsDbContext db,
            AlarmDriverRegistry drivers, CredentialProtector protector, AuditService audit, LocalIpReceiverService local,
            int? panelId, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);

            string? password = request.Password;
            if (string.IsNullOrEmpty(password) && panelId is int existingId &&
                await db.AlarmPanels.FindAsync([existingId], ct) is { } existing)
                password = protector.Unprotect(existing.PasswordCiphertext);
            password ??= LocalReceiverPassword(local, request.Host, request.Port);
            if (string.IsNullOrEmpty(password))
                return Error("La contraseña del panel es obligatoria.");

            var conn = new AlarmConnectionInfo(request.Host.Trim(), request.Port, request.UseHttps, request.Username, password, DeviceIdOf(request));
            var (info, state, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
            await audit.LogAsync(ctx, "alarms", "panel-probed",
                targetType: "alarm-panel", targetName: request.Name.Trim(),
                detail: info is null
                    ? $"Probó la conexión con el panel {request.Host.Trim()}:{request.Port}: {probeError}"
                    : $"Probó la conexión con el panel {request.Host.Trim()}:{request.Port}: correcta ({info.Model}).",
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
        // Equipos DENTRO de la receptora (Hik IP Receiver Pro)
        //
        // Alta y baja de paneles en la pasarela desde el VMS: evita tener que
        // entrar a la interfaz web del fabricante. Las credenciales van en el
        // cuerpo (nunca en la URL) y pueden tomarse de un panel ya guardado.
        // ------------------------------------------------------------------
        app.MapGet("/api/alarms/receiver/local", async (HttpContext ctx, LocalIpReceiverService local, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var state = await local.EnsureActivatedAsync(ct);
            // La contraseña NO se expone nunca: solo si el servidor la tiene.
            return Results.Ok(new AlarmLocalReceiverDto(state.Present, state.Ready && local.HasCredentials,
                local.Host, local.Port, LocalIpReceiverService.AdminUser, state.Message));
        });

        // La credencial de la receptora propia la genera y guarda el sistema, y no
        // hace falta para operar. Pero un administrador puede necesitarla para
        // entrar a la interfaz del fabricante en un diagnóstico, así que se
        // puede consultar aquí: solo administradores y queda en la bitácora.
        app.MapGet("/api/alarms/receiver/local/credential", async (HttpContext ctx, LocalIpReceiverService local,
            AuditService audit) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (local.GetPassword() is not { Length: > 0 } password)
                return Error("El sistema todavía no activó la receptora de este servidor: no hay credencial que mostrar.");

            await audit.LogAsync(ctx, "alarms", "receiver-credential-viewed",
                targetType: "alarm-receiver", targetName: $"{local.Host}:{local.Port}",
                detail: "Consultó la credencial que el sistema generó para la receptora de este servidor.");
            return Results.Ok(new AlarmLocalReceiverCredentialDto(local.Host, local.Port,
                LocalIpReceiverService.AdminUser, password));
        });

        app.MapPost("/api/alarms/receiver/devices/list", async (HttpContext ctx, AlarmReceiverConnectionDto request,
            VmsDbContext db, CredentialProtector protector, AuditService audit, LocalIpReceiverService local,
            string? search, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var (conn, error) = await ReceiverConnectionAsync(request, db, protector, local, ct);
            if (conn is null) return Error(error!);

            try
            {
                var devices = await HikvisionIpReceiverDriver.ListGatewayDevicesAsync(conn, search, ct);
                return Results.Ok(devices.Select(d => new AlarmReceiverDeviceDto(
                    d.DevIndex, d.Name, d.Serial, d.AccountId, d.IsupId, d.Model, d.Version, d.Status)).ToList());
            }
            catch (DriverException ex)
            {
                await audit.LogAsync(ctx, "alarms", "receiver-devices-listed",
                    targetType: "alarm-receiver", targetName: $"{conn.Host}:{conn.Port}",
                    detail: $"No pudo listar los equipos de la receptora: {ex.Message}", success: false);
                return Error(ex.Message);
            }
        });

        app.MapPost("/api/alarms/receiver/devices", async (HttpContext ctx, AlarmReceiverAddDeviceDto request,
            VmsDbContext db, CredentialProtector protector, AuditService audit, LocalIpReceiverService local,
            CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (string.IsNullOrWhiteSpace(request.DeviceId))
                return Error("El ID del equipo es obligatorio.");
            var (conn, error) = await ReceiverConnectionAsync(request.Receiver, db, protector, local, ct);
            if (conn is null) return Error(error!);

            string name = string.IsNullOrWhiteSpace(request.Name) ? request.DeviceId.Trim() : request.Name.Trim();
            string target = $"{conn.Host}:{conn.Port}";
            try
            {
                string devIndex = await HikvisionIpReceiverDriver.AddGatewayDeviceAsync(conn,
                    new HikvisionIpReceiverDriver.GatewayDeviceSpec(request.Protocol, request.DeviceId, request.DeviceKey,
                        name, request.DeviceType, request.AccountId, request.Remark), ct);
                await audit.LogAsync(ctx, "alarms", "receiver-device-added",
                    targetType: "alarm-receiver", targetName: target,
                    detail: $"Agregó el equipo «{name}» (ID {request.DeviceId.Trim()}) a la receptora {target}; uuid {devIndex}.");
                return Results.Ok(new AlarmReceiverDeviceDto(devIndex, name, null, request.AccountId, request.DeviceId.Trim(), null, null, null));
            }
            catch (DriverException ex)
            {
                await audit.LogAsync(ctx, "alarms", "receiver-device-added",
                    targetType: "alarm-receiver", targetName: target,
                    detail: $"No pudo agregar el equipo «{name}» (ID {request.DeviceId.Trim()}) a la receptora {target}: {ex.Message}",
                    success: false);
                return Error(ex.Message);
            }
        });

        app.MapPost("/api/alarms/receiver/devices/delete", async (HttpContext ctx, AlarmReceiverDeleteDeviceDto request,
            VmsDbContext db, CredentialProtector protector, AuditService audit, LocalIpReceiverService local,
            CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (string.IsNullOrWhiteSpace(request.DevIndex))
                return Error("Indique el equipo a quitar de la receptora.");
            var (conn, error) = await ReceiverConnectionAsync(request.Receiver, db, protector, local, ct);
            if (conn is null) return Error(error!);

            string target = $"{conn.Host}:{conn.Port}";
            // Un panel del VMS que apunte a ese equipo quedaría sin comunicación.
            string devIndex = request.DevIndex.Trim();
            bool inUse = await db.AlarmPanels.AnyAsync(p =>
                p.Host == conn.Host && p.Port == conn.Port && p.GatewayDeviceId == devIndex, ct);
            if (inUse)
                return Error("Ese equipo está en uso por un panel del sistema: elimine primero el panel en el VMS.");

            try
            {
                await HikvisionIpReceiverDriver.DeleteGatewayDeviceAsync(conn, devIndex, ct);
                await audit.LogAsync(ctx, "alarms", "receiver-device-removed",
                    targetType: "alarm-receiver", targetName: target,
                    detail: $"Quitó el equipo {devIndex} de la receptora {target}.");
                return Results.NoContent();
            }
            catch (DriverException ex)
            {
                await audit.LogAsync(ctx, "alarms", "receiver-device-removed",
                    targetType: "alarm-receiver", targetName: target,
                    detail: $"No pudo quitar el equipo {devIndex} de la receptora {target}: {ex.Message}", success: false);
                return Error(ex.Message);
            }
        });

        // ------------------------------------------------------------------
        // Refresco a pedido del estado
        // ------------------------------------------------------------------
        app.MapPost("/api/alarms/panels/{id:int}/refresh", async (HttpContext ctx, int id, VmsDbContext db,
            AlarmPanelService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var panel = await db.AlarmPanels.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            if (panel is null) return Results.NotFound();
            if (!panel.Enabled) return Error("El panel tiene el monitoreo desactivado.");

            if (audit.ShouldLog($"alarm-refresh:{session.UserId}:{id}", TimeSpan.FromMinutes(5)))
                await audit.LogAsync(ctx, "alarms", "panel-refreshed",
                    targetType: "alarm-panel", targetId: id.ToString(), targetName: panel.Name,
                    detail: $"Pidió actualizar el estado del panel '{panel.Name}'.");
            var dto = await service.RefreshAsync(id, ct);
            if (dto is null)
                return Error(service.LastErrorOf(id) ?? "El panel no respondió.", StatusCodes.Status502BadGateway);
            return Results.Ok(dto);
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
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var panel = await LoadForCommandAsync(db, id, ct);
            if (panel is null) return Results.NotFound();
            if (area > 0 && panel.Areas.All(a => a.Number != area)) return Error("El área no existe en el panel.");
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
                return Results.Ok(dto);
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
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var panel = await LoadForCommandAsync(db, id, ct);
            if (panel is null) return Results.NotFound();
            if (area > 0 && panel.Areas.All(a => a.Number != area)) return Error("El área no existe en el panel.");
            string areaName = AreaLabel(panel, area);
            try
            {
                var dto = await service.ExecuteAsync(panel,
                    (driver, conn) => driver.DisarmAsync(conn, area, ct),
                    AlarmEventKind.Disarm, "Desarmado desde el VMS", area, null, session.Username, ct);
                await audit.LogAsync(ctx, "alarms", "area-disarmed",
                    targetType: "alarm-area", targetId: $"{id}/{area}", targetName: $"{panel.Name} · {areaName}",
                    detail: $"Desarmó {areaName} del panel '{panel.Name}'.");
                return Results.Ok(dto);
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
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var panel = await LoadForCommandAsync(db, id, ct);
            if (panel is null) return Results.NotFound();
            if (area > 0 && panel.Areas.All(a => a.Number != area)) return Error("El área no existe en el panel.");
            string areaName = AreaLabel(panel, area);
            try
            {
                var dto = await service.ExecuteAsync(panel,
                    (driver, conn) => driver.ClearAlarmAsync(conn, area, ct),
                    AlarmEventKind.Info, "Alarma borrada/silenciada desde el VMS", area, null, session.Username, ct);
                await audit.LogAsync(ctx, "alarms", "alarm-cleared",
                    targetType: "alarm-area", targetId: $"{id}/{area}", targetName: $"{panel.Name} · {areaName}",
                    detail: $"Borró/silenció la alarma de {areaName} del panel '{panel.Name}'.");
                return Results.Ok(dto);
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
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var panel = await LoadForCommandAsync(db, id, ct);
            if (panel is null) return Results.NotFound();
            var target = panel.Zones.FirstOrDefault(z => z.Number == zone);
            if (target is null) return Error("La zona no existe en el panel.");
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
                return Results.Ok(dto);
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
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;

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
            if (!string.IsNullOrWhiteSpace(kind) && Enum.TryParse<AlarmEventKind>(kind, true, out var k))
                query = query.Where(e => e.Kind == k);
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

            var events = await query
                .OrderByDescending(e => e.ReceivedAt)
                .Take(Math.Clamp(take ?? 200, 1, MaxTake))
                .ToListAsync();
            return Results.Ok(events.Select(AlarmMapper.ToDto));
        });
    }

    private static Task<AlarmPanel?> LoadForCommandAsync(VmsDbContext db, int id, CancellationToken ct) =>
        db.AlarmPanels.AsNoTracking().Include(p => p.Areas).Include(p => p.Zones).AsSplitQuery().FirstOrDefaultAsync(p => p.Id == id, ct);

    private static string AreaLabel(AlarmPanel panel, int area) =>
        area <= 0 ? "todas las áreas" : $"el área '{panel.Areas.First(a => a.Number == area).Name}'";

    /// <summary>Carga áreas y zonas leídas en el sondeo de alta/edición.</summary>
    private static void SeedState(AlarmPanel panel, AlarmPanelState state)
    {
        panel.Areas.Clear();
        panel.Zones.Clear();
        foreach (var a in state.Areas)
            panel.Areas.Add(new AlarmArea { Number = a.Number, Name = a.Name, Enabled = a.Enabled, ArmState = a.ArmState, InAlarm = a.InAlarm });
        foreach (var z in state.Zones)
            panel.Zones.Add(new AlarmZone
            {
                Number = z.Number, AreaNumber = z.AreaNumber, Name = z.Name, ZoneType = z.ZoneType, DetectorType = z.DetectorType,
                Status = z.Status, Bypassed = z.Bypassed, Armed = z.Armed, InAlarm = z.InAlarm, Tamper = z.Tamper,
                LowBattery = z.LowBattery, Signal = z.Signal, Model = z.Model,
            });
        panel.LastStateAt = DateTime.UtcNow;
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
