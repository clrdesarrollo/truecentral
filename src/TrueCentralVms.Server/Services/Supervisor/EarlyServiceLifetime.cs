using Microsoft.Extensions.Hosting.WindowsServices;

namespace TrueCentralVms.Server.Services.Supervisor;

/// <summary>
/// Ciclo de vida de servicio de Windows que se conecta con el administrador de
/// servicios APENAS se construye el host, no al final del arranque.
///
/// Windows le da a un servicio 30 s para conectarse; si no, lo da por caído
/// ("did not respond in a timely fashion"). El servidor hace antes de
/// <c>app.Run()</c> lo pesado: inicia PostgreSQL, genera el certificado HTTPS
/// y, con migraciones pendientes, respalda la base completa antes de migrarla.
/// Con una base real eso pasa los 30 s (0.5.22 en el servidor de pruebas). Así
/// el servicio queda "en ejecución" de inmediato; quien espera a que atienda
/// (instalador, Watchdog) mira /api/health, que responde recién al terminar.
///
/// Envuelve el <see cref="WindowsServiceLifetime"/> estándar: el host vuelve a
/// llamar <see cref="WaitForStartAsync"/> al arrancar, y la segunda llamada
/// devuelve la misma tarea en vez de conectar otra vez.
/// </summary>
public sealed class EarlyServiceLifetime(WindowsServiceLifetime inner) : IHostLifetime
{
    private readonly Lock _sync = new();
    private Task? _started;

    public Task WaitForStartAsync(CancellationToken cancellationToken)
    {
        lock (_sync) return _started ??= inner.WaitForStartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => inner.StopAsync(cancellationToken);
}
