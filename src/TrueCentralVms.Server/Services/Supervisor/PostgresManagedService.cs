using Npgsql;
using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services.Supervisor;

/// <summary>
/// PostgreSQL embebido visto por el watchdog. Es ESENCIAL: no se puede dejar
/// detenido (sin base no hay sistema), solo reiniciar. La salud se comprueba
/// con una consulta real (SELECT 1) y no con pg_ctl, que lanza un proceso por
/// cada comprobación.
/// </summary>
public sealed class PostgresManagedService(EmbeddedPostgres postgres, ILogger<PostgresManagedService> logger) : IManagedService
{
    public string Id => "postgres";
    public string Name => "Base de datos (PostgreSQL embebido)";
    public string Description => "Clúster privado del sistema: usuarios, dispositivos, eventos y bitácora.";
    public string Kind => "process";
    public bool CanStop => false;
    public string? DisabledReason => null;

    public Task StartAsync(CancellationToken ct) => postgres.StartAsync(logger, ct);

    public Task StopAsync(CancellationToken ct)
    {
        postgres.Stop(logger);
        return Task.CompletedTask;
    }

    public async Task<ServiceHealth> CheckAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(postgres.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync(ct);
            return ServiceHealth.Ok($"127.0.0.1:{postgres.Port}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ServiceHealth.Down("La base de datos no respondió a tiempo.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ServiceHealth.Down($"La base de datos no responde: {ex.Message}");
        }
    }
}
