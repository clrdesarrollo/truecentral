using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Hubs;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Sonda periódica de disponibilidad: intenta abrir TCP al puerto de gestión
/// de cada dispositivo y publica los cambios de estado por SignalR. No usa el
/// SDK (sin logins repetidos): es solo alcance de red, suficiente para el
/// semáforo online/offline del panel y del cliente.
/// </summary>
public sealed class DeviceStatusMonitor(
    IServiceScopeFactory scopeFactory,
    IHubContext<VmsHub> hub,
    ILogger<DeviceStatusMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
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
            (x.Device, x.ChannelCount, x.EnabledCount, Reachable: await IsReachableAsync(x.Device.Host, x.Device.SdkPort, ct))));

        var changed = new List<(DeviceDto Dto, DeviceStatus Previous)>();
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
                    device.AnprEnabled, enabledCount), previous));
            }
        }
        await db.SaveChangesAsync(ct);

        // Resuelto al vuelo: el motor de automatizaciones contiene acciones
        // que a su vez dependen de otros servicios; por constructor sería un ciclo.
        var workflows = scope.ServiceProvider.GetRequiredService<Workflows.WorkflowEngine>();
        foreach (var (dto, previous) in changed)
        {
            logger.LogInformation("Dispositivo '{Name}' ({Host}) ahora está {Status}.", dto.Name, dto.Host, dto.Status);
            await hub.Clients.All.SendAsync(VmsHubContract.DeviceStatusChanged, dto, ct);
            // La primera lectura tras arrancar (Desconocido → En línea) no es
            // una recuperación: no dispara automatizaciones.
            if (previous != DeviceStatus.Unknown || dto.Status != DeviceStatus.Online)
                workflows.Publish(Workflows.WorkflowTrigger.FromDeviceStatus("video", dto.Id, dto.Name, dto.Host, dto.Model,
                    dto.Status.ToString(), dto.Status == DeviceStatus.Online ? null : "el equipo no responde en su puerto de gestión"));
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
