using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Purga por retención de la bitácora de auditoría. Con Audit:RetentionDays en
/// 0 (el valor de fábrica) no borra NADA: la política de retención es una
/// decisión del administrador, y cada purga queda a su vez auditada.
/// </summary>
public sealed class AuditRetentionService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    AuditService audit,
    ILogger<AuditRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Margen para que el arranque (migraciones incluidas) termine primero.
        await Task.Delay(TimeSpan.FromMinutes(1), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PurgeAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "La purga por retención de la bitácora falló; se reintenta en la próxima pasada.");
            }
            await Task.Delay(Interval, ct);
        }
    }

    private async Task PurgeAsync(CancellationToken ct)
    {
        int days = config.GetValue("Audit:RetentionDays", 0);
        if (days <= 0) return;

        var cutoff = DateTime.UtcNow.AddDays(-days);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        int removed = await db.AuditEvents.Where(e => e.Timestamp < cutoff).ExecuteDeleteAsync(ct);
        if (removed == 0) return;

        logger.LogInformation("Bitácora: purgados {Count} eventos anteriores a {Cutoff:d} (retención {Days} días).",
            removed, cutoff, days);
        await audit.LogSystemAsync("system", "audit-purged",
            detail: $"Se purgaron {removed} eventos anteriores a {cutoff:dd-MM-yyyy} (retención {days} días).",
            data: new { removed, cutoff, retentionDays = days });
    }
}
