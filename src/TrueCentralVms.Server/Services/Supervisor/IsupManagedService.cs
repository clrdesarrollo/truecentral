using TrueCentralVms.Server.Services.Isup;

namespace TrueCentralVms.Server.Services.Supervisor;

/// <summary>
/// El servicio ISUP interno visto por el watchdog. Proceso vivo no basta: se le pide su estado por la API interna (y con eso se ve
/// también si algún puerto no se pudo abrir). Mientras <see cref="IsupHostManager"/> lo relanza solo tras una caída, se informa
/// "Iniciando" en vez de arrancar otro encima.
/// </summary>
public sealed class IsupManagedService(IsupHostManager manager, IsupService service) : IManagedService
{
    public string Id => "isup";
    public string Name => "Conexión ISUP";
    public string Description => "Equipos Hikvision que se conectan al servidor (paneles AX PRO, equipos detrás de NAT, 4G o MPLS).";
    public string Kind => "process";
    public bool CanStop => true;
    public string? DisabledReason => manager.Enabled ? null : "Deshabilitado en la configuración (Isup:Enabled = false).";

    public Task StartAsync(CancellationToken ct) => manager.StartAsync(ct);
    public Task StopAsync(CancellationToken ct) => manager.StopAsync(ct);

    public async Task<ServiceHealth> CheckAsync(CancellationToken ct)
    {
        if (manager.IsRunning)
        {
            var status = await service.GetHostStatusAsync(ct);
            if (status is null)
            {
                if (manager.StartedAtUtc is { } started && DateTime.UtcNow - started < TimeSpan.FromSeconds(20))
                    return ServiceHealth.Restarting("Proceso iniciado; esperando su API interna.");
                return ServiceHealth.Down("El proceso está vivo pero su API interna no responde.");
            }
            int online = status.Devices.Count(d => d.Online);
            var closed = status.Listeners.Where(l => !l.Open).Select(l => $"{l.Name} ({l.Port})").ToList();
            var detail = $"PID {manager.ProcessId}, {online} de {status.Devices.Count} equipo(s) en línea, " + (status.PerDeviceAddress
                ? $"a cada equipo se le anuncia la IP por la que llega ({string.Join(", ", status.LocalAddresses ?? [])})"
                : (status.LocalAddresses?.Count ?? 0) > 1
                    ? $"los equipos de las redes del servidor reciben la IP de su red; los demás, {status.PublicAddress}"
                    : $"dirección anunciada {status.PublicAddress}");
            if (closed.Count > 0) detail += ". Puertos sin abrir: " + string.Join(", ", closed);
            return ServiceHealth.Ok(detail);
        }
        if (manager.RestartPending) return ServiceHealth.Restarting("El proceso terminó; se relanza solo en unos segundos.");
        if (!manager.ExecutableFound) return ServiceHealth.Down("No se encontró TrueCentralVms.IsupHost.exe junto al servidor.");
        return ServiceHealth.Down(manager.LastError ?? (manager.LastExitCode is { } code
            ? $"El proceso no está en ejecución (último código de salida {code})."
            : "El proceso no está en ejecución."));
    }
}
