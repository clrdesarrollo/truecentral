using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services.Isup;
using TrueCentralVms.Server.Services.Licensing;
using TrueCentralVms.Server.Services.Supervisor;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Sonda periódica de disponibilidad: intenta abrir TCP al puerto de gestión
/// de cada dispositivo y publica los cambios de estado por SignalR. No usa el
/// SDK (sin logins repetidos): es solo alcance de red, suficiente para el
/// semáforo online/offline del panel y del cliente. Los equipos que se
/// conectan por ISUP están en línea mientras tengan su registro abierto con
/// el servidor; el primero de sus registros, además, los completa (datos y
/// canales), porque se pueden agregar antes de que se conecten.
/// </summary>
public sealed class DeviceStatusMonitor(
    IServiceScopeFactory scopeFactory,
    ScopedHub hub,
    IsupService isup,
    DriverRegistry drivers,
    CredentialProtector protector,
    LicenseService license,
    MediaMtxManager mtx,
    AuditService audit,
    ILogger<DeviceStatusMonitor> logger) : SupervisedBackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);

    protected override async Task RunAsync(CancellationToken ct)
    {
        // Un equipo ISUP que se conecta o se cae se refleja al instante, sin esperar la pasada.
        isup.DeviceOnlineChanged += (isupId, online) => _ = Task.Run(() => OnIsupChangedAsync(isupId, online, ct), ct);

        // Dejar que el servidor termine de arrancar antes de la primera pasada.
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAllAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Fallo en la pasada del monitor de dispositivos.");
            }
            await Task.Delay(Interval, ct);
        }
    }

    private async Task CheckAllAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();

        var devices = await db.Devices
            .Select(d => new { Device = d, ChannelCount = d.Channels.Count, EnabledCount = d.Channels.Count(c => c.Enabled) })
            .ToListAsync(ct);
        if (devices.Count == 0) return;

        var results = await Task.WhenAll(devices.Select(async x =>
            (x.Device, x.ChannelCount, x.EnabledCount, Reachable: x.Device.IsupDeviceId is { } isupId
                ? isup.IsOnline(isupId)
                // La cámara de un terminal de acceso: lo que importa es su RTSP (el SDK puede estar apagado en el terminal).
                : await IsReachableAsync(x.Device.Host, x.Device.AccessDeviceId is null ? x.Device.SdkPort : x.Device.RtspPort, ct))));

        var changed = new List<(DeviceDto Dto, DeviceStatus Previous, bool Mobile)>();
        foreach (var (device, channelCount, enabledCount, reachable) in results)
        {
            var newStatus = reachable ? DeviceStatus.Online : DeviceStatus.Offline;
            if (reachable)
                device.LastSeenAt = DateTime.UtcNow;
            if (device.Status != newStatus)
            {
                var previous = device.Status;
                device.Status = newStatus;
                changed.Add((new DeviceDto(
                    device.Id, device.Name, device.DeviceType, device.DriverKey, device.Host,
                    device.SdkPort, device.RtspPort, device.Username, device.Model, device.SerialNumber,
                    device.FirmwareVersion, channelCount, device.Status, device.LastSeenAt, device.CreatedAt,
                    device.AnprEnabled, enabledCount, IsupDeviceId: device.IsupDeviceId,
                    HasIsupKey: device.IsupKeyCiphertext is { Length: > 0 }), previous, device.IsMobile));
            }
        }
        await db.SaveChangesAsync(ct);

        // Resuelto al vuelo: el motor de automatizaciones contiene acciones
        // que a su vez dependen de otros servicios; por constructor sería un ciclo.
        var workflows = scope.ServiceProvider.GetRequiredService<Workflows.WorkflowEngine>();
        foreach (var (dto, previous, mobile) in changed)
        {
            logger.LogInformation("Dispositivo '{Name}' ({Host}) ahora está {Status}.", dto.Name, dto.IsupDeviceId is { } logId ? $"ISUP {logId}" : dto.Host, dto.Status);
            // Solo a quien ve alguno de sus canales (alcance por ubicación).
            await hub.SendAsync(VmsHubContract.DeviceStatusChanged, dto, s => s.CanViewDevice(dto.Id), ct);
            // La primera lectura tras arrancar (Desconocido → En línea) no es
            // una recuperación: no dispara automatizaciones.
            // Un vehículo apagado se desconecta: no es un equipo caído. Su conexión va como evento informativo del vehículo
            // (disparador «Evento de vehículo», FleetEventService), no como «Conexión de un equipo».
            if (mobile) continue;
            if (previous != DeviceStatus.Unknown || dto.Status != DeviceStatus.Online)
                workflows.Publish(Workflows.WorkflowTrigger.FromDeviceStatus("video", dto.Id, dto.Name,
                    dto.IsupDeviceId is { } isupId ? $"ISUP {isupId}" : dto.Host, dto.Model, dto.Status.ToString(),
                    dto.Status == DeviceStatus.Online ? null
                    : dto.IsupDeviceId is null ? "el equipo no responde en su puerto de gestión" : "el equipo cortó su conexión ISUP"));
        }
    }

    /// <summary>
    /// Un equipo de video ISUP se conectó o se cayó. Si está pendiente de leer (se agregó sin conexión, o cambió su ID o su clave
    /// sin estar conectado), se completa con sus datos y canales como en una revalidación; después se actualiza el semáforo.
    /// </summary>
    private async Task OnIsupChangedAsync(string isupId, bool online, CancellationToken ct)
    {
        try
        {
            if (online) await Task.Delay(TimeSpan.FromSeconds(3), ct);   // que termine el intercambio de registro del equipo
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
                var device = await db.Devices.Include(d => d.Channels).FirstOrDefaultAsync(d => d.IsupDeviceId == isupId, ct);
                if (device is null) return;
                // Sin canales (se agregó antes de conectarse) o sin modelo (cambió su ID o su clave sin estar conectado).
                if (online && (device.Channels.Count == 0 || device.Model is null) && drivers.Find(device.DriverKey) is { } factory)
                {
                    var info = await factory.Create().ProbeAsync(DeviceConnections.Of(device, protector), ct);
                    Api.DevicesApi.ApplyProbe(device, info, removeMissing: false);
                    int trimmed = await Api.DevicesApi.EnforceChannelQuotaAsync(license, db, device, ct);
                    await db.SaveChangesAsync(ct);
                    await audit.LogSystemAsync("devices", "device-revalidated", "device", device.Id.ToString(), device.Name,
                        $"El equipo '{device.Name}' se conectó por ISUP (ID {isupId}): {device.Model ?? "modelo desconocido"}, " +
                        $"{device.Channels.Count} canales, {device.Channels.Count(c => c.Enabled)} habilitados" +
                        (trimmed > 0 ? $"; {trimmed} quedaron deshabilitados por el cupo de canales de la licencia" : "") + ".",
                        origin: "server");
                    await mtx.RefreshPathsAsync(ct);
                    var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<VmsHub>>();
                    await hubContext.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "devices", cancellationToken: ct);
                }
            }
            await CheckAllAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning("No se pudo completar el equipo de video ISUP {Id} al conectarse: {Error}", isupId, ex.Message);
        }
    }

    private static async Task<bool> IsReachableAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
