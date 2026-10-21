using TrueCentralVms.Server.Services.Backup;

namespace TrueCentralVms.Server.Services.Supervisor;

/// <summary>
/// Respaldos en el panel de servicios. Además de la vida del programador, la salud dice si hay un respaldo bueno
/// reciente y si el segundo destino funciona: así una falla de respaldos se ve (y avisa) como cualquier servicio caído.
/// Reiniciarlo hace que el programador reintente de inmediato si no hay un respaldo reciente.
/// </summary>
public sealed class BackupManagedService(BackupService backups) : IManagedService
{
    private readonly HostedServiceAdapter _inner = new("backups", "Respaldos",
        "Respaldo diario cifrado de la base y los archivos del sistema, y antes de cada actualización.", backups);

    public string Id => _inner.Id;
    public string Name => _inner.Name;
    public string Description => _inner.Description;
    public string Kind => _inner.Kind;
    public bool CanStop => true;
    public string? DisabledReason => backups.Settings.Enabled ? null : "Respaldos automáticos desactivados en Sistema → Respaldos.";

    public Task StartAsync(CancellationToken ct) => _inner.StartAsync(ct);
    public Task StopAsync(CancellationToken ct) => _inner.StopAsync(ct);

    public async Task<ServiceHealth> CheckAsync(CancellationToken ct)
    {
        var running = await _inner.CheckAsync(ct);
        if (!running.Healthy) return running;
        var (healthy, detail) = backups.Health();
        return healthy ? ServiceHealth.Ok(detail) : ServiceHealth.Down(detail);
    }
}
