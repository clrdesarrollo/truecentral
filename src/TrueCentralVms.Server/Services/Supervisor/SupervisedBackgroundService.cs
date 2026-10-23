namespace TrueCentralVms.Server.Services.Supervisor;

/// <summary>
/// Base de los <see cref="BackgroundService"/> que vigila el supervisor (los del catálogo de
/// <see cref="ServiceSupervisor"/>, envueltos en <see cref="HostedServiceAdapter"/>). Implementan <see cref="RunAsync"/>
/// en vez de ExecuteAsync.
///
/// El host de .NET espera la PRIMERA ejecución de cada BackgroundService y, si termina cancelada o con error sin que el
/// servidor se esté apagando, detiene el servidor completo (BackgroundServiceExceptionBehavior.StopHost). Con el
/// supervisor eso es una trampa: detener un servicio para reiniciarlo cancela su ejecución, y el host lo tomaba como una
/// falla y botaba todo, cámaras incluidas. Por eso la ejecución de estos servicios nunca termina cancelada ni con error:
/// - Cancelada porque se detuvo (el supervisor, un administrador o el apagado del servidor): es un término normal.
/// - Con error: queda en <see cref="Failure"/>; el supervisor lo ve caído, lo registra y lo reinicia según sus reglas.
///
/// Los BackgroundService que no están en el catálogo siguen con el comportamiento del host: si fallan, se detiene el
/// proceso y las acciones de recuperación del SCM lo vuelven a levantar.
/// </summary>
public abstract class SupervisedBackgroundService : BackgroundService
{
    /// <summary>Error con el que terminó la última ejecución; null mientras corre o si terminó sin error.</summary>
    public Exception? Failure { get; private set; }

    protected abstract Task RunAsync(CancellationToken stoppingToken);

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Failure = null;
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Detenido: término normal.
        }
        catch (Exception ex)
        {
            Failure = ex;
        }
    }
}
