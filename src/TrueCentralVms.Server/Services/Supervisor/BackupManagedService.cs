using TrueCentralVms.Server.Services.Backup;

namespace TrueCentralVms.Server.Services.Supervisor;

/// <summary>
/// Respaldos en el panel de servicios. Solo la vida del programador decide si está caído (y el watchdog lo reinicia).
/// Que no haya un respaldo bueno reciente o que falle el segundo destino es una advertencia: se ve y queda en la
/// bitácora, pero reiniciar el programador no hace un respaldo, así que el watchdog no lo reinicia. El programador
/// reintenta solo (ver <see cref="BackupService"/>) y un administrador puede respaldar a pedido en Sistema → Respaldos.
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
        return healthy ? ServiceHealth.Ok(detail) : ServiceHealth.Warning(detail);
    }
}
