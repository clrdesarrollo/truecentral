using System.Globalization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Hora y mantenimiento de los equipos: ver el reloj de cada uno (hora, zona,
/// horario de verano, NTP y desfase), ponerlos en hora, fijar la política que
/// aplica la supervisión, reiniciarlos y restablecerlos. Hoy cubre control de
/// acceso; las rutas llevan la familia del equipo (<c>/devices/access/12/…</c>)
/// para sumar video, paneles y citofonía sin cambiar su forma.
///
/// Mirar es de cualquier usuario (dentro de su alcance por ubicación);
/// cambiar, reiniciar y restablecer es de administradores, y todo queda en la
/// bitácora (categoría <c>maintenance</c>).
/// </summary>
public static class DeviceMaintenanceApi
{
    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    private const string LocalFormat = "yyyy-MM-dd'T'HH:mm:ss";

    public static void MapDeviceMaintenanceApi(this WebApplication app)
    {
        // ------------------------------------------------------------------
        // Lo que se ve
        // ------------------------------------------------------------------

        app.MapGet("/api/maintenance/clocks", async (HttpContext ctx, VmsDbContext db, AccessControlService access,
            DeviceClockService clocks, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            return Results.Ok(await OverviewAsync(ctx, session, db, access, clocks, ct));
        });

        app.MapGet("/api/maintenance/time-zones", (HttpContext ctx) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.Ok(TimeZoneInfo.GetSystemTimeZones()
                .Select(tz => new TimeZoneOptionDto(tz.Id, tz.DisplayName, DeviceTimeZones.Describe(tz))));
        });

        // Leer ya (sin corregir): la lista muestra lo leído en la última vuelta.
        app.MapPost("/api/maintenance/clocks/check", async (HttpContext ctx, DeviceSelectionDto? request, VmsDbContext db,
            AccessControlService access, DeviceClockService clocks, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var policy = await DeviceClockService.LoadPolicyAsync(db, ct);
            var devices = await VisibleAccessDevicesAsync(ctx, session, db, access, request?.Devices, ct);
            using var limiter = new SemaphoreSlim(4);
            await Task.WhenAll(devices.Where(d => d.Enabled && d.Status == AccessDeviceStatus.Online).Select(async d =>
            {
                await limiter.WaitAsync(ct);
                try { await clocks.CheckOneAsync(d, policy, allowCorrection: false, ct); }
                finally { limiter.Release(); }
            }));
            return Results.Ok(await OverviewAsync(ctx, session, db, access, clocks, ct));
        });

        // ------------------------------------------------------------------
        // Política
        // ------------------------------------------------------------------

        app.MapPut("/api/maintenance/clock-policy", async (HttpContext ctx, DeviceClockPolicyWriteDto request,
            VmsDbContext db, DeviceClockService clocks, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (request.TimeZoneId is { Length: > 0 } zoneId && FindZone(zoneId) is null)
                return Error($"Zona horaria desconocida: '{zoneId}'.");
            if (request.Mode == DeviceTimeMode.Ntp && string.IsNullOrWhiteSpace(request.NtpServer))
                return Error("Para usar NTP hay que indicar el servidor.");
            if (request.NtpServer?.Trim().Length > 255) return Error("El servidor NTP es demasiado largo.");
            if (request.ThresholdSeconds is < 5 or > 86400) return Error("El desfase tolerado va de 5 segundos a 1 día.");
            if (request.CheckMinutes is < 1 or > 1440) return Error("La revisión va de cada 1 minuto a cada 24 horas.");
            if (request.NtpIntervalMinutes is < 1 or > 10080) return Error("El intervalo de NTP va de 1 minuto a 7 días.");

            var policy = await db.DeviceClockPolicies.OrderBy(p => p.Id).FirstOrDefaultAsync(ct);
            var before = policy is null ? DeviceClockPolicy.Default() : Copy(policy);
            if (policy is null)
            {
                policy = new DeviceClockPolicy { Id = 1 };
                db.DeviceClockPolicies.Add(policy);
            }
            policy.TimeZoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? null : request.TimeZoneId.Trim();
            policy.Mode = request.Mode == DeviceTimeMode.Ntp ? DeviceTimeMode.Ntp : DeviceTimeMode.Manual;
            policy.NtpServer = string.IsNullOrWhiteSpace(request.NtpServer) ? null : request.NtpServer.Trim();
            policy.NtpIntervalMinutes = request.NtpIntervalMinutes;
            policy.AutoCorrect = request.AutoCorrect;
            policy.ThresholdSeconds = request.ThresholdSeconds;
            policy.CheckMinutes = request.CheckMinutes;
            policy.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "maintenance", "clock-policy-changed", targetType: "clock-policy", targetId: "1",
                targetName: "Hora de los equipos", detail: $"Cambió la política de hora de los equipos: {PolicyDiff(before, policy)}.");
            clocks.PolicyChanged();
            return Results.Ok(PolicyDto(policy));
        });

        // ------------------------------------------------------------------
        // Poner en hora
        // ------------------------------------------------------------------

        // En bloque, según la política: la acción de todos los días.
        app.MapPost("/api/maintenance/clocks/sync", async (HttpContext ctx, DeviceSelectionDto? request, VmsDbContext db,
            AccessControlService access, DeviceClockService clocks, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out var session) is { } failure) return failure;
            var policy = await DeviceClockService.LoadPolicyAsync(db, ct);
            var zone = policy.Zone();
            var devices = await VisibleAccessDevicesAsync(ctx, session, db, access, request?.Devices, ct);

            var results = new List<DeviceActionResultDto>();
            foreach (var device in devices)
            {
                IAccessControlDriver driver;
                try { driver = access.DriverOf(device); }
                catch (DriverException ex) { results.Add(new(DeviceClockService.AccessKind, device.Id, device.Name, false, ex.Message)); continue; }
                if (!driver.SupportsClock)
                {
                    results.Add(new(DeviceClockService.AccessKind, device.Id, device.Name, false, "No admite que el VMS le cambie la hora."));
                    continue;
                }
                if (!device.Enabled || device.Status != AccessDeviceStatus.Online)
                {
                    results.Add(new(DeviceClockService.AccessKind, device.Id, device.Name, false, "Sin conexión."));
                    continue;
                }
                var setting = DeviceClockService.SettingFor(policy, zone, driver);
                try
                {
                    string? note = await clocks.ApplyAsync(device, setting, ct);
                    results.Add(new(DeviceClockService.AccessKind, device.Id, device.Name, true, note));
                    await audit.LogAsync(ctx, "maintenance", "clock-set",
                        targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                        detail: $"Puso en hora el equipo '{device.Name}' ({device.Host}) según la política: " +
                                $"{Describe(setting)}{(note is null ? "" : $". Nota: {note}")}.");
                }
                catch (DriverException ex)
                {
                    results.Add(new(DeviceClockService.AccessKind, device.Id, device.Name, false, ex.Message));
                    await audit.LogAsync(ctx, "maintenance", "clock-set",
                        targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                        detail: $"No se pudo poner en hora el equipo '{device.Name}' ({device.Host}): {ex.Message}", success: false);
                }
            }
            return Results.Ok(results);
        });

        // Uno, con lo que el operador elija (zona, hora a mano o NTP).
        app.MapPut("/api/maintenance/devices/{kind}/{id:int}/clock", async (HttpContext ctx, string kind, int id,
            DeviceClockWriteDto request, VmsDbContext db, AccessControlService access, DeviceClockService clocks,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out var session) is { } failure) return failure;
            if (await FindAccessAsync(ctx, session, kind, id, db, access, ct) is not { } device) return Results.NotFound();

            var policy = await DeviceClockService.LoadPolicyAsync(db, ct);
            TimeZoneInfo zone = policy.Zone();
            if (request.TimeZoneId is { Length: > 0 } zoneId)
            {
                if (FindZone(zoneId) is not { } chosen) return Error($"Zona horaria desconocida: '{zoneId}'.");
                zone = chosen;
            }
            DateTime? local = null;
            if (request.Mode != DeviceTimeMode.Ntp && !string.IsNullOrWhiteSpace(request.LocalTime))
            {
                if (!DateTime.TryParseExact(request.LocalTime.Trim(), [LocalFormat, "yyyy-MM-dd'T'HH:mm"],
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    return Error("La hora tiene que venir como AAAA-MM-DDTHH:MM:SS.");
                local = parsed;
            }
            if (request.Mode == DeviceTimeMode.Ntp && string.IsNullOrWhiteSpace(request.NtpServer))
                return Error("Para usar NTP hay que indicar el servidor.");
            var setting = new DeviceClockSetting(zone,
                request.Mode == DeviceTimeMode.Ntp ? DeviceTimeMode.Ntp : DeviceTimeMode.Manual, local,
                request.NtpServer?.Trim(), Math.Clamp(request.NtpIntervalMinutes ?? 60, 1, 10080));

            try
            {
                string? note = await clocks.ApplyAsync(device, setting, ct);
                await audit.LogAsync(ctx, "maintenance", "clock-set",
                    targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                    detail: $"Ajustó la hora del equipo '{device.Name}' ({device.Host}): {Describe(setting)}" +
                            $"{(note is null ? "" : $". Nota: {note}")}.");
                return Results.Ok(new { note, device = clocks.ToDto(device, access.ToDto(device).Location, policy) });
            }
            catch (DriverException ex)
            {
                await audit.LogAsync(ctx, "maintenance", "clock-set",
                    targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                    detail: $"No se pudo ajustar la hora del equipo '{device.Name}' ({device.Host}): {ex.Message}", success: false);
                return Error(ex.Message, StatusCodes.Status502BadGateway);
            }
        });

        app.MapPut("/api/maintenance/devices/{kind}/{id:int}/auto-correct", async (HttpContext ctx, string kind, int id,
            DeviceAutoCorrectDto request, VmsDbContext db, AccessControlService access, AuditService audit,
            DeviceClockService clocks, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out var session) is { } failure) return failure;
            if (await FindAccessAsync(ctx, session, kind, id, db, access, ct) is not { } device) return Results.NotFound();
            if (device.ClockAutoCorrect == request.Enabled) return Results.Ok();
            device.ClockAutoCorrect = request.Enabled;
            device.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "maintenance", "clock-autocorrect-changed",
                targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                detail: request.Enabled
                    ? $"Activó la corrección automática de hora del equipo '{device.Name}'."
                    : $"Apagó la corrección automática de hora del equipo '{device.Name}': el VMS ya no lo pone en hora solo.");
            if (request.Enabled) clocks.RequestCheck();
            return Results.Ok();
        });

        // ------------------------------------------------------------------
        // Reiniciar y restablecer
        // ------------------------------------------------------------------

        app.MapPost("/api/maintenance/devices/{kind}/{id:int}/reboot", async (HttpContext ctx, string kind, int id,
            VmsDbContext db, AccessControlService access, DeviceClockService clocks, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out var session) is { } failure) return failure;
            if (await FindAccessAsync(ctx, session, kind, id, db, access, ct) is not { } device) return Results.NotFound();
            try
            {
                var driver = access.DriverOf(device);
                if (!driver.SupportsReboot) return Error("Este equipo no acepta reinicios remotos.");
                await driver.RebootAsync(access.ConnectionOf(device), ct);
            }
            catch (DriverException ex)
            {
                await audit.LogAsync(ctx, "maintenance", "device-rebooted",
                    targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                    detail: $"No se pudo reiniciar el equipo '{device.Name}' ({device.Host}): {ex.Message}", success: false);
                return Error(ex.Message, StatusCodes.Status502BadGateway);
            }
            clocks.Forget(DeviceClockService.AccessKind, device.Id);
            await audit.LogAsync(ctx, "maintenance", "device-rebooted",
                targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                detail: $"Reinició el equipo '{device.Name}' ({device.Host}).");
            return Results.Ok(new { message = "El equipo aceptó el reinicio: vuelve en uno o dos minutos." });
        });

        app.MapPost("/api/maintenance/devices/{kind}/{id:int}/reset", async (HttpContext ctx, string kind, int id,
            DeviceResetRequestDto request, VmsDbContext db, AccessControlService access, DeviceClockService clocks,
            IHubContext<VmsHub> hub, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out var session) is { } failure) return failure;
            if (await FindAccessAsync(ctx, session, kind, id, db, access, ct) is not { } device) return Results.NotFound();
            // La confirmación se revisa también acá: la pantalla la exige, pero
            // un restablecimiento no puede depender solo del navegador.
            if (!string.Equals(request.Confirm?.Trim(), device.Name.Trim(), StringComparison.Ordinal))
                return Error("Para confirmar hay que escribir el nombre del equipo exactamente como está.");

            string mode = request.Mode == DeviceResetMode.Full ? "de fábrica completo" : "conservando la red y las cuentas";
            try
            {
                var driver = access.DriverOf(device);
                var supported = request.Mode == DeviceResetMode.Full ? DeviceResetModes.Full : DeviceResetModes.KeepNetwork;
                if (!driver.SupportedResets.HasFlag(supported))
                    return Error($"Este equipo no acepta restablecerse {mode} desde el VMS.");
                await driver.ResetAsync(access.ConnectionOf(device), request.Mode, ct);
            }
            catch (DriverException ex)
            {
                await audit.LogAsync(ctx, "maintenance", "device-reset",
                    targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                    detail: $"No se pudo restablecer ({mode}) el equipo '{device.Name}' ({device.Host}): {ex.Message}", success: false);
                return Error(ex.Message, StatusCodes.Status502BadGateway);
            }

            // El equipo perdió lo que el VMS le había escrito (o puede haberlo
            // perdido): todo su padrón queda pendiente y se reescribe solo cuando
            // vuelva a estar en línea. Reescribir lo que sí sobrevivió no daña.
            int marked = await AccessSyncService.MarkForResendAsync(db, device.Id, ct);
            device.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            access.ForgetSessionOf(device);
            clocks.Forget(DeviceClockService.AccessKind, device.Id);

            await audit.LogAsync(ctx, "maintenance", "device-reset",
                targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                detail: $"Restableció el equipo '{device.Name}' ({device.Host}) {mode}. " +
                        $"{marked} persona(s) quedan pendientes de reescribir cuando vuelva." +
                        (request.Mode == DeviceResetMode.Full
                            ? " El equipo queda desactivado: hay que activarlo de nuevo con la misma contraseña para que el VMS lo recupere."
                            : ""));
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "access-devices", cancellationToken: ct);
            return Results.Ok(new
            {
                message = request.Mode == DeviceResetMode.Full
                    ? "El equipo aceptó volver a fábrica. Queda desactivado: actívelo de nuevo (Dispositivos → búsqueda en la red) " +
                      "con la misma contraseña y el VMS le reescribe el padrón solo."
                    : "El equipo aceptó restablecerse. Vuelve en unos minutos con la misma red y clave, y el VMS le reescribe el padrón solo.",
                pending = marked,
            });
        });
    }

    // ------------------------------------------------------------------

    private static async Task<DeviceClockOverviewDto> OverviewAsync(HttpContext ctx, SessionInfo session, VmsDbContext db,
        AccessControlService access, DeviceClockService clocks, CancellationToken ct)
    {
        var policy = await DeviceClockService.LoadPolicyAsync(db, ct);
        var scope = await ctx.ScopeAsync(session);
        var devices = (await db.AccessDevices.AsNoTracking().Include(d => d.Doors).ToListAsync(ct))
            .Select(d => (Device: d, Dto: scope.Visible(access.ToDto(d))))
            .Where(x => x.Dto is not null)
            .OrderBy(x => x.Dto!.Location ?? "￿", StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Device.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => clocks.ToDto(x.Device, x.Dto!.Location, policy))
            .ToList();
        var local = TimeZoneInfo.Local;
        return new DeviceClockOverviewDto(DateTime.UtcNow, DateTime.Now.ToString(LocalFormat, CultureInfo.InvariantCulture),
            local.Id, local.DisplayName, DeviceTimeZones.Describe(local), PolicyDto(policy), devices);
    }

    /// <summary>Los equipos de acceso pedidos (o todos) que el usuario ve, con sus puertas.</summary>
    private static async Task<List<AccessDevice>> VisibleAccessDevicesAsync(HttpContext ctx, SessionInfo session,
        VmsDbContext db, AccessControlService access, IReadOnlyList<DeviceRefDto>? requested, CancellationToken ct)
    {
        var ids = requested?.Where(r => r.Kind == DeviceClockService.AccessKind).Select(r => r.Id).ToHashSet();
        var scope = await ctx.ScopeAsync(session);
        return (await db.AccessDevices.Include(d => d.Doors)
                .Where(d => ids == null || ids.Count == 0 || ids.Contains(d.Id)).ToListAsync(ct))
            .Where(d => scope.Visible(access.ToDto(d)) is not null)
            .ToList();
    }

    /// <summary>El equipo pedido si es de una familia conocida y el usuario lo ve; si no, null (404).</summary>
    private static async Task<AccessDevice?> FindAccessAsync(HttpContext ctx, SessionInfo session, string kind, int id,
        VmsDbContext db, AccessControlService access, CancellationToken ct)
    {
        if (kind != DeviceClockService.AccessKind) return null;
        var device = await db.AccessDevices.Include(d => d.Doors).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null) return null;
        return (await ctx.ScopeAsync(session)).Visible(access.ToDto(device)) is null ? null : device;
    }

    private static TimeZoneInfo? FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    private static DeviceClockPolicyDto PolicyDto(DeviceClockPolicy p)
    {
        var zone = p.Zone();
        return new DeviceClockPolicyDto(p.TimeZoneId, zone.DisplayName, DeviceTimeZones.Describe(zone), p.Mode, p.NtpServer,
            p.NtpIntervalMinutes, p.AutoCorrect, p.ThresholdSeconds, p.CheckMinutes);
    }

    private static DeviceClockPolicy Copy(DeviceClockPolicy p) => new()
    {
        TimeZoneId = p.TimeZoneId, Mode = p.Mode, NtpServer = p.NtpServer, NtpIntervalMinutes = p.NtpIntervalMinutes,
        AutoCorrect = p.AutoCorrect, ThresholdSeconds = p.ThresholdSeconds, CheckMinutes = p.CheckMinutes,
    };

    /// <summary>Qué cambió de la política, en palabras.</summary>
    private static string PolicyDiff(DeviceClockPolicy a, DeviceClockPolicy b)
    {
        var changes = new List<string>();
        if (a.TimeZoneId != b.TimeZoneId)
            changes.Add($"zona {(a.TimeZoneId ?? "del servidor")} → {(b.TimeZoneId ?? "del servidor")}");
        if (a.Mode != b.Mode || a.NtpServer != b.NtpServer || a.NtpIntervalMinutes != b.NtpIntervalMinutes)
            changes.Add($"hora {ModeText(a)} → {ModeText(b)}");
        if (a.AutoCorrect != b.AutoCorrect) changes.Add(b.AutoCorrect ? "corrección automática activada" : "corrección automática apagada");
        if (a.ThresholdSeconds != b.ThresholdSeconds) changes.Add($"desfase tolerado {a.ThresholdSeconds} s → {b.ThresholdSeconds} s");
        if (a.CheckMinutes != b.CheckMinutes) changes.Add($"revisión cada {a.CheckMinutes} min → {b.CheckMinutes} min");
        return changes.Count > 0 ? string.Join(", ", changes) : "sin cambios";

        static string ModeText(DeviceClockPolicy p) => p.Mode == DeviceTimeMode.Ntp
            ? $"por NTP ({p.NtpServer}, cada {p.NtpIntervalMinutes} min)" : "del servidor";
    }

    /// <summary>Lo que se le pidió al equipo, en palabras.</summary>
    private static string Describe(DeviceClockSetting s) =>
        $"zona {DeviceTimeZones.Describe(s.Zone)}, " + (s.Mode == DeviceTimeMode.Ntp
            ? $"hora por NTP desde {s.NtpServer} cada {s.NtpIntervalMinutes} min"
            : s.LocalTime is { } t ? $"hora fijada a mano: {t:dd-MM-yyyy HH:mm:ss}" : "hora del servidor");
}
