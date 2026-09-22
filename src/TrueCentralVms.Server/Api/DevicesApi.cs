using TrueCentralVms.Server.Services.Licensing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Mantenedor de dispositivos (cámaras, DVR, NVR, XVR). Al crear o editar con
/// credenciales nuevas SIEMPRE se valida contra el equipo real vía el driver:
/// si el login falla, no se guarda nada y el error del SDK llega al panel.
/// </summary>
public static class DevicesApi
{
    /// <summary>Máximo de capturas simultáneas hacia los equipos (no saturar DVR/NVR).</summary>
    private static readonly SemaphoreSlim SnapshotThrottle = new(4, 4);

    private static DeviceDto ToDto(Device d, int channelCount, int enabledChannelCount, int disabledWithSignal,
        string? warning = null) => new(
        d.Id, d.Name, d.DeviceType, d.DriverKey, d.Host, d.SdkPort, d.RtspPort, d.Username,
        d.Model, d.SerialNumber, d.FirmwareVersion, channelCount, d.Status, d.LastSeenAt, d.CreatedAt,
        d.AnprEnabled, enabledChannelCount, warning,
        // Mismo criterio que ToDto(Channel): sin el equipo en línea no hay señal.
        d.Status == DeviceStatus.Online ? disabledWithSignal : 0);

    /// <summary>DTO de un equipo con sus canales ya cargados (respuestas de alta/edición/revalidación).</summary>
    private static DeviceDto ToDto(Device d, int trimmedByQuota = 0) =>
        ToDto(d, d.Channels.Count, d.Channels.Count(c => c.Enabled), d.Channels.Count(c => !c.Enabled && c.IsOnline),
            QuotaWarning(d, trimmedByQuota));

    /// <summary>
    /// Un canal solo se reporta en línea si su equipo también lo está: con el
    /// DVR/NVR caído ninguno de sus canales puede tener señal, aunque el
    /// último sondeo los haya visto activos.
    /// </summary>
    private static ChannelDto ToDto(Channel c, DeviceStatus deviceStatus) =>
        new(c.Id, c.DeviceId, c.ChannelNumber, c.RtspChannel, c.Name, c.Enabled,
            c.IsOnline && deviceStatus == DeviceStatus.Online, c.SupportsPtz, c.UseFfmpegProxy, c.DisabledByLicense);

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    private static string? ValidateWrite(DeviceWriteDto request, DriverRegistry drivers)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
            return "El nombre es obligatorio (máximo 128 caracteres).";
        if (string.IsNullOrWhiteSpace(request.Host))
            return "La dirección (IP o hostname) es obligatoria.";
        if (request.SdkPort is < 1 or > 65535 || request.RtspPort is < 1 or > 65535)
            return "Los puertos deben estar entre 1 y 65535.";
        if (string.IsNullOrWhiteSpace(request.Username))
            return "El usuario del dispositivo es obligatorio.";
        if (drivers.Find(request.DriverKey) is null)
            return $"Driver desconocido: '{request.DriverKey}'.";
        return null;
    }

    /// <summary>Sondea el equipo con el driver; errores del driver se devuelven como mensaje.</summary>
    private static async Task<(DeviceProbeInfo? Info, string? Error)> ProbeAsync(
        DriverRegistry drivers, string driverKey, DeviceConnectionInfo conn, CancellationToken ct)
    {
        var factory = drivers.Find(driverKey)!;
        try
        {
            var info = await factory.Create().ProbeAsync(conn, ct);
            return (info, null);
        }
        catch (DriverException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex)
        {
            return (null, $"Error inesperado al comunicarse con el dispositivo: {ex.Message}");
        }
    }

    /// <summary>Aplica los datos del sondeo al dispositivo y sincroniza sus canales.</summary>
    private static void ApplyProbe(Device device, DeviceProbeInfo info)
    {
        // El tipo lo decide el equipo, no el usuario: se recalcula en cada
        // sondeo según los canales que reporta.
        if (Enum.TryParse<DeviceType>(info.SuggestedType, out var detectedType))
            device.DeviceType = detectedType;
        // Ídem el puerto RTSP: si el SDK lo reporta, manda sobre el formulario
        // (evita rutas de streaming apuntando al 554 en equipos con puerto propio).
        if (info.RtspPort is int rtspPort and > 0)
            device.RtspPort = rtspPort;
        device.Model = info.Model;
        device.SerialNumber = info.SerialNumber;
        device.FirmwareVersion = info.FirmwareVersion;
        device.Status = DeviceStatus.Online;
        device.LastSeenAt = DateTime.UtcNow;
        device.UpdatedAt = DateTime.UtcNow;

        // Sincronización de canales: los existentes conservan nombre y
        // habilitado (decisiones del administrador); los nuevos entran con el
        // nombre reportado por el equipo; los que ya no existen se eliminan.
        var byNumber = device.Channels.ToDictionary(c => c.ChannelNumber);
        var seen = new HashSet<int>();
        foreach (var probed in info.Channels)
        {
            seen.Add(probed.ChannelNumber);
            if (byNumber.TryGetValue(probed.ChannelNumber, out var existing))
            {
                existing.RtspChannel = probed.RtspChannel;
                existing.IsOnline = probed.IsOnline;
                existing.RtspMainUrl = probed.MainStreamUrl;
                existing.RtspSubUrl = probed.SubStreamUrl;
                // La detección solo puede ENCENDER el PTZ: una marca manual del
                // administrador (domo ONVIF detrás de un DVR, RS-485 sin
                // retorno de posición) sobrevive a las revalidaciones.
                existing.SupportsPtz = existing.SupportsPtz || probed.SupportsPtz;
            }
            else
            {
                device.Channels.Add(new Channel
                {
                    ChannelNumber = probed.ChannelNumber,
                    RtspChannel = probed.RtspChannel,
                    Name = probed.Name,
                    // Canales que el equipo reporta deshabilitados o sin cámara
                    // entran ocultos (sin ruta de streaming ni presencia en el
                    // cliente); el administrador puede habilitarlos en el panel.
                    Enabled = probed.IsOnline,
                    IsOnline = probed.IsOnline,
                    RtspMainUrl = probed.MainStreamUrl,
                    RtspSubUrl = probed.SubStreamUrl,
                    SupportsPtz = probed.SupportsPtz,
                });
            }
        }
        device.Channels.RemoveAll(c => !seen.Contains(c.ChannelNumber));
    }

    /// <summary>
    /// Cupo de canales de video de la licencia: los canales habilitados de
    /// este equipo que no caben (contando los ya habilitados en los demás)
    /// entran deshabilitados, de mayor a menor número, y quedan marcados
    /// (<see cref="Channel.DisabledByLicense"/>) para habilitarse solos cuando
    /// la licencia tenga cupo. El equipo se agrega igual. Devuelve cuántos.
    /// </summary>
    private static async Task<int> EnforceChannelQuotaAsync(LicenseService license, VmsDbContext db, Device device, CancellationToken ct)
    {
        int quota = license.IsModuleEnabled(LicenseFeatures.ModuleVideo) ? license.Quota(LicenseFeatures.VideoChannels) : 0;
        int elsewhere = await db.Channels.CountAsync(c => c.Enabled && c.DeviceId != device.Id, ct);
        int allowed = Math.Max(0, quota - elsewhere);
        int trimmed = 0;
        foreach (var channel in device.Channels.Where(c => c.Enabled).OrderBy(c => c.ChannelNumber))
        {
            if (allowed > 0) { allowed--; continue; }
            channel.Enabled = false;
            channel.DisabledByLicense = true;
            trimmed++;
        }
        return trimmed;
    }

    /// <summary>Cupo de canales de video de la licencia: total, en uso (sin contar
    /// <paramref name="excludeDeviceId"/>) y cuántos quedan libres.</summary>
    private static async Task<(int Quota, int InUse, int Available)> VideoChannelBudgetAsync(
        LicenseService license, VmsDbContext db, int? excludeDeviceId, CancellationToken ct)
    {
        int quota = license.IsModuleEnabled(LicenseFeatures.ModuleVideo) ? license.Quota(LicenseFeatures.VideoChannels) : 0;
        int inUse = await db.Channels.CountAsync(c => c.Enabled && c.DeviceId != excludeDeviceId, ct);
        return (quota, inUse, Math.Max(0, quota - inUse));
    }

    /// <summary>
    /// Alta: el equipo no puede entrar con más canales habilitados que los que
    /// deja libres la licencia. Sin selección explícita se habilitan los canales
    /// activos si caben; si no caben, se exige elegir cuáles (a lo sumo los
    /// disponibles). Devuelve el error a responder, o null si quedó resuelto.
    /// </summary>
    private static IResult? ApplyChannelSelection(Device device, IReadOnlyList<int>? selection, (int Quota, int InUse, int Available) budget)
    {
        if (selection is null)
        {
            int active = device.Channels.Count(c => c.Enabled);
            if (active <= budget.Available) return null;
            return ChannelSelectionRequired(device, budget,
                $"El equipo reporta {active} canales activos y la licencia solo tiene {budget.Available} disponibles " +
                $"({budget.InUse} de {budget.Quota} en uso). Seleccione hasta {budget.Available} canales para habilitar.");
        }

        var chosen = selection.Distinct().ToHashSet();
        var unknown = chosen.Where(n => device.Channels.All(c => c.ChannelNumber != n)).ToList();
        if (unknown.Count > 0)
            return Error($"El equipo no reporta el canal {unknown[0]}.");
        if (chosen.Count > budget.Available)
            return ChannelSelectionRequired(device, budget,
                $"Seleccionó {chosen.Count} canales, pero la licencia solo tiene {budget.Available} disponibles " +
                $"({budget.InUse} de {budget.Quota} en uso).");
        // Si faltaba cupo, los canales activos que quedaron fuera de la
        // selección se marcan: se habilitan solos cuando la licencia tenga cupo.
        bool shortage = device.Channels.Count(c => c.Enabled) > budget.Available;
        foreach (var channel in device.Channels)
        {
            bool wasActive = channel.Enabled;
            channel.Enabled = chosen.Contains(channel.ChannelNumber);
            channel.DisabledByLicense = shortage && wasActive && !channel.Enabled;
        }
        return null;
    }

    /// <summary>422 con los datos que el asistente necesita para mostrar el selector de canales.</summary>
    private static IResult ChannelSelectionRequired(Device device, (int Quota, int InUse, int Available) budget, string message) =>
        Results.Json(new
        {
            error = message,
            channelSelectionRequired = true,
            videoChannelQuota = budget.Quota,
            videoChannelsInUse = budget.InUse,
            availableVideoChannels = budget.Available,
            channels = device.Channels.OrderBy(c => c.ChannelNumber)
                .Select(c => new ProbedChannelDto(c.ChannelNumber, c.RtspChannel, c.Name, c.IsOnline)).ToList(),
        }, statusCode: StatusCodes.Status422UnprocessableEntity);

    private static string QuotaNote(int trimmed) =>
        trimmed == 0 ? "" : $"; {trimmed} canal(es) quedaron deshabilitados por el cupo de canales de la licencia";

    /// <summary>Aviso para el panel: el equipo se guardó, pero parte de sus canales no caben en la licencia.</summary>
    private static string? QuotaWarning(Device device, int trimmed) =>
        trimmed == 0 ? null
            : $"{trimmed} de {device.Channels.Count} canales quedaron deshabilitados por el cupo de canales de la licencia. " +
              "Se habilitarán solos cuando la licencia tenga cupo (amplíela o libere canales en otros equipos).";

    public static void MapDevicesApi(this WebApplication app)
    {
        // ------------------------------------------------------------------
        // Drivers disponibles (para el dropdown del asistente)
        // ------------------------------------------------------------------
        app.MapGet("/api/drivers", (HttpContext ctx, DriverRegistry drivers) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.Ok(drivers.All.Select(f => new DriverDto(
                f.DriverKey, f.DisplayName,
                f.Capabilities.SupportsSnapshot, f.Capabilities.SupportsDiscovery,
                f.Capabilities.DefaultSdkPort, f.Capabilities.DefaultRtspPort)));
        });

        // ------------------------------------------------------------------
        // CRUD de dispositivos
        // ------------------------------------------------------------------
        app.MapGet("/api/devices", async (HttpContext ctx, VmsDbContext db) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var devices = await db.Devices
                .Select(d => new
                {
                    Device = d,
                    ChannelCount = d.Channels.Count,
                    EnabledCount = d.Channels.Count(c => c.Enabled),
                    DisabledWithSignal = d.Channels.Count(c => !c.Enabled && c.IsOnline),
                })
                .OrderBy(x => x.Device.Name)
                .ToListAsync();
            return Results.Ok(devices.Select(x => ToDto(x.Device, x.ChannelCount, x.EnabledCount, x.DisabledWithSignal)));
        });

        app.MapPost("/api/devices", async (HttpContext ctx, DeviceWriteDto request, VmsDbContext db,
            DriverRegistry drivers, CredentialProtector protector, IHubContext<VmsHub> hub,
            Services.MediaMtxManager mtx, AuditService audit, LicenseService license, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            if (license.Deny(LicenseFeatures.ModuleVideo, null, 0, 0) is { } denied)
                return await license.DenyAsync(ctx, denied, "device", request.Name?.Trim());
            // Sin canales libres en la licencia no tiene sentido dar de alta el
            // equipo: ninguna de sus cámaras podría verse.
            var budget = await VideoChannelBudgetAsync(license, db, null, ct);
            if (budget.Available <= 0)
                return await license.DenyAsync(ctx,
                    $"No hay canales de video disponibles en la licencia ({budget.InUse} de {budget.Quota} en uso). " +
                    "Amplíe la licencia o deshabilite canales en otros equipos antes de agregar este.",
                    "device", request.Name?.Trim());
            if (string.IsNullOrEmpty(request.Password))
                return Error("La contraseña del dispositivo es obligatoria.");
            if (await db.Devices.AnyAsync(d => d.Host == request.Host && d.SdkPort == request.SdkPort, ct))
                return Error("Ya existe un dispositivo con esa dirección y puerto.", StatusCodes.Status409Conflict);

            // Validación real contra el equipo: login + datos + canales.
            var conn = new DeviceConnectionInfo(request.Host.Trim(), request.SdkPort, request.Username, request.Password);
            var (info, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
            if (info is null)
            {
                await audit.LogAsync(ctx, "devices", "device-created",
                    targetType: "device", targetName: request.Name?.Trim(),
                    detail: $"Alta de dispositivo rechazada por el equipo {request.Host}:{request.SdkPort}: {probeError}",
                    success: false);
                return Error(probeError!);
            }

            var device = new Device
            {
                Name = request.Name.Trim(),
                DriverKey = request.DriverKey,
                Host = request.Host.Trim(),
                SdkPort = request.SdkPort,
                RtspPort = request.RtspPort,
                Username = request.Username,
                PasswordCiphertext = protector.Protect(request.Password),
            };
            ApplyProbe(device, info);
            if (ApplyChannelSelection(device, request.EnabledChannels, budget) is { } selectionError)
            {
                await audit.LogAsync(ctx, "devices", "device-created",
                    targetType: "device", targetName: device.Name,
                    detail: $"Alta de '{device.Name}' pendiente de selección de canales: {device.Channels.Count} canales, " +
                            $"{budget.Available} disponibles en la licencia ({budget.InUse} de {budget.Quota} en uso).",
                    success: false);
                return selectionError;
            }
            db.Devices.Add(device);
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "devices", "device-created",
                targetType: "device", targetId: device.Id.ToString(), targetName: device.Name,
                detail: $"Agregó el dispositivo '{device.Name}' ({device.DriverKey}, {device.Host}:{device.SdkPort}, " +
                        $"{device.Channels.Count} canales, {device.Channels.Count(c => c.Enabled)} habilitados).",
                data: new { device.Host, device.SdkPort, device.RtspPort, device.DriverKey, device.Model, device.SerialNumber });
            await mtx.RefreshPathsAsync(ct);
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "devices", cancellationToken: ct);
            return Results.Ok(ToDto(device));
        });

        app.MapPut("/api/devices/{id:int}", async (HttpContext ctx, int id, DeviceWriteDto request, VmsDbContext db,
            DriverRegistry drivers, CredentialProtector protector, IHubContext<VmsHub> hub,
            Services.MediaMtxManager mtx, Services.AnprService anpr, AuditService audit, LicenseService license,
            CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);

            var device = await db.Devices.Include(d => d.Channels).FirstOrDefaultAsync(d => d.Id == id, ct);
            if (device is null) return Results.NotFound();
            if (await db.Devices.AnyAsync(d => d.Id != id && d.Host == request.Host && d.SdkPort == request.SdkPort, ct))
                return Error("Ya existe otro dispositivo con esa dirección y puerto.", StatusCodes.Status409Conflict);

            string password = string.IsNullOrEmpty(request.Password)
                ? protector.Unprotect(device.PasswordCiphertext)
                : request.Password;

            // Si cambió algo que afecta la conexión, se revalida contra el equipo.
            bool connectionChanged =
                device.Host != request.Host.Trim() ||
                device.SdkPort != request.SdkPort ||
                device.Username != request.Username ||
                device.DriverKey != request.DriverKey ||
                !string.IsNullOrEmpty(request.Password);
            int trimmed = 0;
            if (connectionChanged)
            {
                var conn = new DeviceConnectionInfo(request.Host.Trim(), request.SdkPort, request.Username, password);
                var (info, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
                if (info is null)
                    return Error(probeError!);
                ApplyProbe(device, info);
                trimmed = await EnforceChannelQuotaAsync(license, db, device, ct);
            }

            var changes = new List<string>();
            if (device.Name != request.Name.Trim()) changes.Add($"nombre '{device.Name}' → '{request.Name.Trim()}'");
            if (device.Host != request.Host.Trim() || device.SdkPort != request.SdkPort)
                changes.Add($"dirección {device.Host}:{device.SdkPort} → {request.Host.Trim()}:{request.SdkPort}");
            if (device.RtspPort != request.RtspPort) changes.Add($"puerto RTSP {device.RtspPort} → {request.RtspPort}");
            if (device.Username != request.Username) changes.Add($"usuario del equipo '{device.Username}' → '{request.Username}'");
            if (device.DriverKey != request.DriverKey) changes.Add($"driver {device.DriverKey} → {request.DriverKey}");
            if (!string.IsNullOrEmpty(request.Password)) changes.Add("contraseña del equipo cambiada");

            device.Name = request.Name.Trim();
            device.DriverKey = request.DriverKey;
            device.Host = request.Host.Trim();
            device.SdkPort = request.SdkPort;
            device.RtspPort = request.RtspPort;
            device.Username = request.Username;
            device.PasswordCiphertext = protector.Protect(password);
            device.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "devices", "device-updated",
                targetType: "device", targetId: device.Id.ToString(), targetName: device.Name,
                detail: $"Modificó el dispositivo '{device.Name}': " +
                        (changes.Count > 0 ? string.Join(", ", changes) : "sin cambios de conexión") + QuotaNote(trimmed) + ".");

            // El canal de eventos de patentes cuelga de estas credenciales: si
            // la conexión cambió hay que rehacerlo con las nuevas.
            if (connectionChanged)
            {
                await anpr.DetachAsync(device.Id);
                anpr.RequestReconcile();
            }

            await mtx.RefreshPathsAsync(ct);
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "devices", cancellationToken: ct);
            return Results.Ok(ToDto(device, trimmed));
        });

        app.MapDelete("/api/devices/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            IHubContext<VmsHub> hub, Services.MediaMtxManager mtx, Services.WallService walls,
            Services.AnprService anpr, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var device = await db.Devices.FindAsync([id], ct);
            if (device is null) return Results.NotFound();

            // El canal de eventos de patentes es una sesión viva contra el
            // equipo: se cierra antes de que la fila desaparezca.
            await anpr.DetachAsync(id);

            // El muro de video sigue decodificando por su cuenta: hay que
            // apagar sus ventanas ANTES de que el borrado en cascada se lleve
            // los canales, o el decodificador queda pintando una cámara que el
            // sistema ya no conoce.
            await walls.ReleaseDeviceAsync(id);

            db.Devices.Remove(device); // canales caen por cascada
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "devices", "device-deleted",
                targetType: "device", targetId: id.ToString(), targetName: device.Name,
                detail: $"Eliminó el dispositivo '{device.Name}' ({device.Host}:{device.SdkPort}) y todos sus canales.");
            await mtx.RefreshPathsAsync(ct);
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "devices", cancellationToken: ct);
            return Results.Ok();
        });

        // ------------------------------------------------------------------
        // Revalidación: vuelve a sondear el equipo con las credenciales
        // guardadas y refresca modelo/firmware/canales/puerto RTSP.
        // ------------------------------------------------------------------
        app.MapPost("/api/devices/{id:int}/revalidate", async (HttpContext ctx, int id, VmsDbContext db,
            DriverRegistry drivers, CredentialProtector protector, IHubContext<VmsHub> hub,
            Services.MediaMtxManager mtx, AuditService audit, LicenseService license, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var device = await db.Devices.Include(d => d.Channels).FirstOrDefaultAsync(d => d.Id == id, ct);
            if (device is null) return Results.NotFound();

            var conn = new DeviceConnectionInfo(device.Host, device.SdkPort, device.Username,
                protector.Unprotect(device.PasswordCiphertext));
            var (info, probeError) = await ProbeAsync(drivers, device.DriverKey, conn, ct);
            if (info is null)
            {
                await audit.LogAsync(ctx, "devices", "device-revalidated",
                    targetType: "device", targetId: id.ToString(), targetName: device.Name,
                    detail: $"Revalidación de '{device.Name}' fallida: {probeError}", success: false);
                return Error(probeError!);
            }

            ApplyProbe(device, info);
            int trimmed = await EnforceChannelQuotaAsync(license, db, device, ct);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "devices", "device-revalidated",
                targetType: "device", targetId: id.ToString(), targetName: device.Name,
                detail: $"Revalidó '{device.Name}': {device.Channels.Count} canales, firmware {device.FirmwareVersion ?? "—"}{QuotaNote(trimmed)}.");
            await mtx.RefreshPathsAsync(ct);
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "devices", cancellationToken: ct);
            return Results.Ok(ToDto(device, trimmed));
        });

        // ------------------------------------------------------------------
        // Sondeo sin persistir: botón "Probar conexión" del asistente. Con
        // ?deviceId= reutiliza la contraseña guardada cuando no se escribe una.
        // ------------------------------------------------------------------
        app.MapPost("/api/devices/probe", async (HttpContext ctx, DeviceWriteDto request, VmsDbContext db,
            DriverRegistry drivers, CredentialProtector protector, AuditService audit, LicenseService license,
            int? deviceId, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);

            string? password = request.Password;
            if (string.IsNullOrEmpty(password) && deviceId is int existingId &&
                await db.Devices.FindAsync([existingId], ct) is { } existing)
                password = protector.Unprotect(existing.PasswordCiphertext);
            if (string.IsNullOrEmpty(password))
                return Error("La contraseña del dispositivo es obligatoria.");

            var conn = new DeviceConnectionInfo(request.Host.Trim(), request.SdkPort, request.Username, password);
            var (info, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
            await audit.LogAsync(ctx, "devices", "device-probed",
                targetType: "device", targetName: request.Name?.Trim(),
                detail: info is null
                    ? $"Probó la conexión con {request.Host.Trim()}:{request.SdkPort} ({request.DriverKey}): {probeError}"
                    : $"Probó la conexión con {request.Host.Trim()}:{request.SdkPort} ({request.DriverKey}): correcta.",
                success: info is not null);
            if (info is null)
                return Results.Ok(new DeviceProbeResultDto(false, probeError, null, null, null, null, 0, 0, []));

            var suggested = Enum.TryParse<DeviceType>(info.SuggestedType, out var t) ? t : (DeviceType?)null;
            var budget = await VideoChannelBudgetAsync(license, db, deviceId, ct);
            return Results.Ok(new DeviceProbeResultDto(
                true, null, info.Model, info.SerialNumber, info.FirmwareVersion, suggested,
                info.AnalogChannelCount, info.IpChannelCount,
                info.Channels.Select(c => new ProbedChannelDto(c.ChannelNumber, c.RtspChannel, c.Name, c.IsOnline)).ToList(),
                info.RtspPort, budget.Quota, budget.InUse, budget.Available));
        });

        // ------------------------------------------------------------------
        // Canales
        // ------------------------------------------------------------------
        app.MapGet("/api/devices/{id:int}/channels", async (HttpContext ctx, int id, VmsDbContext db) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var device = await db.Devices.FindAsync(id);
            if (device is null) return Results.NotFound();
            var channels = await db.Channels
                .Where(c => c.DeviceId == id)
                .OrderBy(c => c.ChannelNumber)
                .ToListAsync();
            return Results.Ok(channels.Select(c => ToDto(c, device.Status)));
        });

        app.MapPut("/api/devices/{id:int}/channels/{channelId:int}", async (HttpContext ctx, int id, int channelId,
            ChannelWriteDto request, VmsDbContext db, IHubContext<VmsHub> hub,
            Services.MediaMtxManager mtx, AuditService audit, LicenseService license, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var channel = await db.Channels.Include(c => c.Device)
                .FirstOrDefaultAsync(c => c.Id == channelId && c.DeviceId == id, ct);
            if (channel is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
                return Error("El nombre del canal es obligatorio (máximo 128 caracteres).");

            if (!channel.Enabled && request.Enabled
                && license.Deny(LicenseFeatures.ModuleVideo, LicenseFeatures.VideoChannels,
                    await db.Channels.CountAsync(c => c.Enabled && c.Id != channelId, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "channel", channel.Name);

            var changes = new List<string>();
            if (channel.Name != request.Name.Trim()) changes.Add($"nombre '{channel.Name}' → '{request.Name.Trim()}'");
            if (channel.Enabled != request.Enabled) changes.Add(request.Enabled ? "habilitado" : "deshabilitado");
            if (channel.SupportsPtz != request.SupportsPtz) changes.Add(request.SupportsPtz ? "PTZ activado" : "PTZ desactivado");
            if (channel.UseFfmpegProxy != request.UseFfmpegProxy) changes.Add(request.UseFfmpegProxy ? "proxy FFmpeg activado" : "proxy FFmpeg desactivado");

            bool pathsChanged = channel.Enabled != request.Enabled
                || channel.UseFfmpegProxy != request.UseFfmpegProxy;
            // Habilitar o deshabilitar a mano es decisión del administrador:
            // el canal deja de esperar cupo de la licencia.
            if (channel.Enabled != request.Enabled) channel.DisabledByLicense = false;
            channel.Name = request.Name.Trim();
            channel.Enabled = request.Enabled;
            channel.SupportsPtz = request.SupportsPtz;
            channel.UseFfmpegProxy = request.UseFfmpegProxy;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "devices", "channel-updated",
                targetType: "channel", targetId: $"{id}/{channel.ChannelNumber}",
                targetName: $"{channel.Device.Name} · {channel.Name}",
                detail: $"Modificó el canal {channel.ChannelNumber} de '{channel.Device.Name}': " +
                        (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios."));
            if (pathsChanged)
                await mtx.RefreshPathsAsync(ct); // habilitar/deshabilitar o proxy cambian la ruta
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "channels", cancellationToken: ct);
            return Results.Ok(ToDto(channel, channel.Device.Status));
        });

        // Habilita en bloque los canales CON SEÑAL que están deshabilitados,
        // hasta donde alcance la licencia. Sirve para los que el cupo dejó
        // apagados antes de existir la marca DisabledByLicense (p. ej. durante
        // la prueba de 16 canales) y para cámaras conectadas después del alta.
        // Los que no caben quedan marcados y se habilitan solos al haber cupo.
        app.MapPost("/api/devices/{id:int}/channels/enable-online", async (HttpContext ctx, int id, VmsDbContext db,
            IHubContext<VmsHub> hub, Services.MediaMtxManager mtx, AuditService audit, LicenseService license,
            CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var device = await db.Devices.Include(d => d.Channels).FirstOrDefaultAsync(d => d.Id == id, ct);
            if (device is null) return Results.NotFound();
            if (device.Status != DeviceStatus.Online)
                return Error($"'{device.Name}' no está en línea: no se sabe qué canales tienen señal. Revalídelo e intente de nuevo.");

            var candidates = device.Channels
                .Where(c => !c.Enabled && c.IsOnline)
                .OrderBy(c => c.ChannelNumber)
                .ToList();
            if (candidates.Count == 0)
                return Results.Ok(new ChannelBulkEnableResultDto(0, 0, $"'{device.Name}' no tiene canales con señal deshabilitados."));

            var budget = await VideoChannelBudgetAsync(license, db, null, ct);
            var enabled = candidates.Take(Math.Max(0, budget.Available)).ToList();
            var left = candidates.Skip(enabled.Count).ToList();
            foreach (var channel in enabled)
            {
                channel.Enabled = true;
                channel.DisabledByLicense = false;
            }
            foreach (var channel in left)
                channel.DisabledByLicense = true;
            await db.SaveChangesAsync(ct);

            if (enabled.Count == 0)
                return await license.DenyAsync(ctx,
                    $"No hay canales de video disponibles en la licencia ({budget.InUse} de {budget.Quota} en uso): " +
                    $"los {left.Count} canales con señal de '{device.Name}' quedan esperando cupo y se habilitarán solos cuando lo haya.",
                    "device", device.Name);

            string message = $"Se habilitaron {enabled.Count} canal(es) con señal de '{device.Name}'" +
                (left.Count > 0
                    ? $"; {left.Count} quedan esperando cupo de la licencia ({budget.Quota} canales) y se habilitarán solos cuando lo haya."
                    : ".");
            await audit.LogAsync(ctx, "devices", "channels-enabled-bulk",
                targetType: "device", targetId: id.ToString(), targetName: device.Name,
                detail: $"{message} Canales habilitados: {string.Join(", ", enabled.Select(c => c.ChannelNumber))}.");
            await mtx.RefreshPathsAsync(ct);
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "channels", cancellationToken: ct);
            return Results.Ok(new ChannelBulkEnableResultDto(enabled.Count, left.Count, message));
        });

        // ------------------------------------------------------------------
        // Control PTZ (solo canales que reportan soporte). Movimiento
        // continuo: el cliente envía stop=false al presionar y stop=true al
        // soltar. Cualquier usuario con sesión puede operar PTZ.
        // ------------------------------------------------------------------
        app.MapPost("/api/devices/{id:int}/channels/{channelNumber:int}/ptz", async (HttpContext ctx, int id,
            int channelNumber, PtzRequestDto request, VmsDbContext db, DriverRegistry drivers,
            CredentialProtector protector, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;

            var channel = await db.Channels.Include(c => c.Device)
                .FirstOrDefaultAsync(c => c.DeviceId == id && c.ChannelNumber == channelNumber, ct);
            if (channel is null) return Results.NotFound();
            if (!channel.SupportsPtz)
                return Error("Este canal no tiene PTZ.");
            var factory = drivers.Find(channel.Device.DriverKey);
            if (factory is null)
                return Error("Driver no disponible.");

            var conn = new DeviceConnectionInfo(channel.Device.Host, channel.Device.SdkPort,
                channel.Device.Username, protector.Unprotect(channel.Device.PasswordCiphertext));
            bool ok;
            try
            {
                ok = await factory.Create().PtzControlAsync(conn, channelNumber, request.Command, request.Speed, request.Stop, ct);
            }
            catch (DriverException ex)
            {
                return Error(ex.Message, StatusCodes.Status502BadGateway);
            }

            // Las órdenes continuas llegan de a pares (partir/detener) y a
            // ráfagas: se audita UNA operación por usuario/canal cada 2 min,
            // no cada gesto (quedaría constancia sin ruido).
            if (!request.Stop && audit.ShouldLog($"ptz:{session.UserId}:{id}:{channelNumber}", TimeSpan.FromMinutes(2)))
                await audit.LogAsync(ctx, "ptz", "ptz-moved",
                    targetType: "channel", targetId: $"{id}/{channelNumber}",
                    targetName: $"{channel.Device.Name} · {channel.Name}",
                    detail: $"Operó el PTZ de '{channel.Device.Name}' canal {channelNumber}.",
                    success: ok);
            return ok
                ? Results.Ok()
                : Error("El equipo rechazó la orden PTZ.", StatusCodes.Status502BadGateway);
        });

        // ------------------------------------------------------------------
        // Presets PTZ: ir / guardar / borrar (índice 1..300)
        // ------------------------------------------------------------------
        app.MapPost("/api/devices/{id:int}/channels/{channelNumber:int}/ptz-preset", async (HttpContext ctx, int id,
            int channelNumber, PtzPresetRequestDto request, VmsDbContext db, DriverRegistry drivers,
            CredentialProtector protector, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            if (request.Index is < 1 or > 300)
                return Error("El preset debe estar entre 1 y 300.");

            var channel = await db.Channels.Include(c => c.Device)
                .FirstOrDefaultAsync(c => c.DeviceId == id && c.ChannelNumber == channelNumber, ct);
            if (channel is null) return Results.NotFound();
            if (!channel.SupportsPtz)
                return Error("Este canal no tiene PTZ.");
            var factory = drivers.Find(channel.Device.DriverKey);
            if (factory is null)
                return Error("Driver no disponible.");

            var conn = new DeviceConnectionInfo(channel.Device.Host, channel.Device.SdkPort,
                channel.Device.Username, protector.Unprotect(channel.Device.PasswordCiphertext));
            bool ok;
            try
            {
                ok = await factory.Create().PtzPresetAsync(conn, channelNumber, request.Action, request.Index, ct);
            }
            catch (DriverException ex)
            {
                return Error(ex.Message, StatusCodes.Status502BadGateway);
            }

            var (presetAction, presetVerb) = request.Action switch
            {
                Core.Drivers.PtzPresetAction.Set => ("ptz-preset-saved", "Guardó"),
                Core.Drivers.PtzPresetAction.Clear => ("ptz-preset-deleted", "Borró"),
                _ => ("ptz-preset-goto", "Fue al"),
            };
            await audit.LogAsync(ctx, "ptz", presetAction,
                targetType: "channel", targetId: $"{id}/{channelNumber}",
                targetName: $"{channel.Device.Name} · {channel.Name}",
                detail: $"{presetVerb} preset {request.Index} de '{channel.Device.Name}' canal {channelNumber}.",
                success: ok);
            return ok
                ? Results.Ok()
                : Error("El equipo rechazó la operación de preset.", StatusCodes.Status502BadGateway);
        });

        // ------------------------------------------------------------------
        // Snapshot JPEG de un canal (cache 25 s, máximo 4 capturas simultáneas)
        // ------------------------------------------------------------------
        app.MapGet("/api/devices/{id:int}/snapshot/{channelNumber:int}", async (HttpContext ctx, int id, int channelNumber,
            VmsDbContext db, DriverRegistry drivers, CredentialProtector protector, IMemoryCache cache,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;

            // Las miniaturas se piden solas por tandas (mantenedor de canales,
            // muro): se audita una vez por usuario/equipo cada 5 min.
            if (audit.ShouldLog($"snap:{session.UserId}:{id}", TimeSpan.FromMinutes(5)))
                await audit.LogAsync(ctx, "live", "snapshot-web",
                    targetType: "device", targetId: id.ToString(),
                    detail: $"Solicitó miniaturas del dispositivo {id} (canal {channelNumber} y siguientes).");

            string cacheKey = $"snapshot:{id}:{channelNumber}";
            if (cache.TryGetValue(cacheKey, out byte[]? cached) && cached is not null)
                return Results.File(cached, "image/jpeg");

            var device = await db.Devices.FindAsync([id], ct);
            if (device is null) return Results.NotFound();
            var factory = drivers.Find(device.DriverKey);
            if (factory is null || !factory.Capabilities.SupportsSnapshot)
                return Error("El driver de este dispositivo no soporta capturas.", StatusCodes.Status501NotImplemented);

            await SnapshotThrottle.WaitAsync(ct);
            byte[]? jpeg;
            try
            {
                var conn = new DeviceConnectionInfo(device.Host, device.SdkPort, device.Username,
                    protector.Unprotect(device.PasswordCiphertext));
                jpeg = await factory.Create().CaptureSnapshotAsync(conn, channelNumber, ct);
            }
            finally
            {
                SnapshotThrottle.Release();
            }

            if (jpeg is null or { Length: 0 })
                return Error("El dispositivo no entregó imagen para ese canal.", StatusCodes.Status502BadGateway);

            cache.Set(cacheKey, jpeg, TimeSpan.FromSeconds(25));
            return Results.File(jpeg, "image/jpeg");
        });
    }
}
