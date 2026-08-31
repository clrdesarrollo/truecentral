using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;

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

    private static DeviceDto ToDto(Device d, int channelCount) => new(
        d.Id, d.Name, d.DeviceType, d.DriverKey, d.Host, d.SdkPort, d.RtspPort, d.Username,
        d.Model, d.SerialNumber, d.FirmwareVersion, channelCount, d.Status, d.LastSeenAt, d.CreatedAt,
        d.AnprEnabled);

    /// <summary>
    /// Un canal solo se reporta en línea si su equipo también lo está: con el
    /// DVR/NVR caído ninguno de sus canales puede tener señal, aunque el
    /// último sondeo los haya visto activos.
    /// </summary>
    private static ChannelDto ToDto(Channel c, DeviceStatus deviceStatus) =>
        new(c.Id, c.DeviceId, c.ChannelNumber, c.RtspChannel, c.Name, c.Enabled,
            c.IsOnline && deviceStatus == DeviceStatus.Online, c.SupportsPtz, c.UseFfmpegProxy);

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
                .Select(d => new { Device = d, ChannelCount = d.Channels.Count })
                .OrderBy(x => x.Device.Name)
                .ToListAsync();
            return Results.Ok(devices.Select(x => ToDto(x.Device, x.ChannelCount)));
        });

        app.MapPost("/api/devices", async (HttpContext ctx, DeviceWriteDto request, VmsDbContext db,
            DriverRegistry drivers, CredentialProtector protector, IHubContext<VmsHub> hub,
            Services.MediaMtxManager mtx, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            if (string.IsNullOrEmpty(request.Password))
                return Error("La contraseña del dispositivo es obligatoria.");
            if (await db.Devices.AnyAsync(d => d.Host == request.Host && d.SdkPort == request.SdkPort, ct))
                return Error("Ya existe un dispositivo con esa dirección y puerto.", StatusCodes.Status409Conflict);

            // Validación real contra el equipo: login + datos + canales.
            var conn = new DeviceConnectionInfo(request.Host.Trim(), request.SdkPort, request.Username, request.Password);
            var (info, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
            if (info is null)
                return Error(probeError!);

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
            db.Devices.Add(device);
            await db.SaveChangesAsync(ct);

            await mtx.RefreshPathsAsync(ct);
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "devices", cancellationToken: ct);
            return Results.Ok(ToDto(device, device.Channels.Count));
        });

        app.MapPut("/api/devices/{id:int}", async (HttpContext ctx, int id, DeviceWriteDto request, VmsDbContext db,
            DriverRegistry drivers, CredentialProtector protector, IHubContext<VmsHub> hub,
            Services.MediaMtxManager mtx, Services.AnprService anpr, CancellationToken ct) =>
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
            if (connectionChanged)
            {
                var conn = new DeviceConnectionInfo(request.Host.Trim(), request.SdkPort, request.Username, password);
                var (info, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
                if (info is null)
                    return Error(probeError!);
                ApplyProbe(device, info);
            }

            device.Name = request.Name.Trim();
            device.DriverKey = request.DriverKey;
            device.Host = request.Host.Trim();
            device.SdkPort = request.SdkPort;
            device.RtspPort = request.RtspPort;
            device.Username = request.Username;
            device.PasswordCiphertext = protector.Protect(password);
            device.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            // El canal de eventos de patentes cuelga de estas credenciales: si
            // la conexión cambió hay que rehacerlo con las nuevas.
            if (connectionChanged)
            {
                await anpr.DetachAsync(device.Id);
                anpr.RequestReconcile();
            }

            await mtx.RefreshPathsAsync(ct);
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "devices", cancellationToken: ct);
            return Results.Ok(ToDto(device, device.Channels.Count));
        });

        app.MapDelete("/api/devices/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            IHubContext<VmsHub> hub, Services.MediaMtxManager mtx, Services.WallService walls,
            Services.AnprService anpr, CancellationToken ct) =>
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
            Services.MediaMtxManager mtx, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var device = await db.Devices.Include(d => d.Channels).FirstOrDefaultAsync(d => d.Id == id, ct);
            if (device is null) return Results.NotFound();

            var conn = new DeviceConnectionInfo(device.Host, device.SdkPort, device.Username,
                protector.Unprotect(device.PasswordCiphertext));
            var (info, probeError) = await ProbeAsync(drivers, device.DriverKey, conn, ct);
            if (info is null)
                return Error(probeError!);

            ApplyProbe(device, info);
            await db.SaveChangesAsync(ct);
            await mtx.RefreshPathsAsync(ct);
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "devices", cancellationToken: ct);
            return Results.Ok(ToDto(device, device.Channels.Count));
        });

        // ------------------------------------------------------------------
        // Sondeo sin persistir: botón "Probar conexión" del asistente. Con
        // ?deviceId= reutiliza la contraseña guardada cuando no se escribe una.
        // ------------------------------------------------------------------
        app.MapPost("/api/devices/probe", async (HttpContext ctx, DeviceWriteDto request, VmsDbContext db,
            DriverRegistry drivers, CredentialProtector protector, int? deviceId, CancellationToken ct) =>
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
            if (info is null)
                return Results.Ok(new DeviceProbeResultDto(false, probeError, null, null, null, null, 0, 0, []));

            var suggested = Enum.TryParse<DeviceType>(info.SuggestedType, out var t) ? t : (DeviceType?)null;
            return Results.Ok(new DeviceProbeResultDto(
                true, null, info.Model, info.SerialNumber, info.FirmwareVersion, suggested,
                info.AnalogChannelCount, info.IpChannelCount,
                info.Channels.Select(c => new ProbedChannelDto(c.ChannelNumber, c.RtspChannel, c.Name, c.IsOnline)).ToList(),
                info.RtspPort));
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
            Services.MediaMtxManager mtx, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var channel = await db.Channels.Include(c => c.Device)
                .FirstOrDefaultAsync(c => c.Id == channelId && c.DeviceId == id, ct);
            if (channel is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
                return Error("El nombre del canal es obligatorio (máximo 128 caracteres).");

            bool pathsChanged = channel.Enabled != request.Enabled
                || channel.UseFfmpegProxy != request.UseFfmpegProxy;
            channel.Name = request.Name.Trim();
            channel.Enabled = request.Enabled;
            channel.SupportsPtz = request.SupportsPtz;
            channel.UseFfmpegProxy = request.UseFfmpegProxy;
            await db.SaveChangesAsync(ct);
            if (pathsChanged)
                await mtx.RefreshPathsAsync(ct); // habilitar/deshabilitar o proxy cambian la ruta
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "channels", cancellationToken: ct);
            return Results.Ok(ToDto(channel, channel.Device.Status));
        });

        // ------------------------------------------------------------------
        // Control PTZ (solo canales que reportan soporte). Movimiento
        // continuo: el cliente envía stop=false al presionar y stop=true al
        // soltar. Cualquier usuario con sesión puede operar PTZ.
        // ------------------------------------------------------------------
        app.MapPost("/api/devices/{id:int}/channels/{channelNumber:int}/ptz", async (HttpContext ctx, int id,
            int channelNumber, PtzRequestDto request, VmsDbContext db, DriverRegistry drivers,
            CredentialProtector protector, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;

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
            return ok
                ? Results.Ok()
                : Error("El equipo rechazó la orden PTZ.", StatusCodes.Status502BadGateway);
        });

        // ------------------------------------------------------------------
        // Presets PTZ: ir / guardar / borrar (índice 1..300)
        // ------------------------------------------------------------------
        app.MapPost("/api/devices/{id:int}/channels/{channelNumber:int}/ptz-preset", async (HttpContext ctx, int id,
            int channelNumber, PtzPresetRequestDto request, VmsDbContext db, DriverRegistry drivers,
            CredentialProtector protector, CancellationToken ct) =>
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
            return ok
                ? Results.Ok()
                : Error("El equipo rechazó la operación de preset.", StatusCodes.Status502BadGateway);
        });

        // ------------------------------------------------------------------
        // Snapshot JPEG de un canal (cache 25 s, máximo 4 capturas simultáneas)
        // ------------------------------------------------------------------
        app.MapGet("/api/devices/{id:int}/snapshot/{channelNumber:int}", async (HttpContext ctx, int id, int channelNumber,
            VmsDbContext db, DriverRegistry drivers, CredentialProtector protector, IMemoryCache cache, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;

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
