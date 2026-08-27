using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TrueCentralVms.Server.Data;

/// <summary>
/// Fábrica para las herramientas de diseño (dotnet ef migrations add ...).
/// Solo construye el MODELO: la cadena de conexión es un marcador y nunca se
/// usa para conectarse (las migraciones se aplican en el arranque del servidor
/// contra el PostgreSQL embebido real).
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<VmsDbContext>
{
    public VmsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<VmsDbContext>()
            .UseNpgsql($"Host=127.0.0.1;Port={EmbeddedPostgres.DefaultPort};Database=truecentral_vms;Username=postgres;Password=design-time")
            .Options;
        return new VmsDbContext(options);
    }
}
