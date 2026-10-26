using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace TrueCentralVms.Server.Services.Supervisor;

/// <summary>
/// Ciclo de vida de servicio de Windows que distingue quién pidió detener el servidor.
///
/// Las acciones de recuperación del instalador (<c>sc failure</c> + <c>failureflag 1</c>) solo reinician el servicio si
/// se cae o si termina informando un código de error. Cuando el propio servidor se detiene (un BackgroundService falla y
/// el host aplica StopHost), el <see cref="WindowsServiceLifetime"/> estándar informa "detenido" con código 0: para
/// Windows es una detención normal y el servicio queda abajo. Así quedó el servidor de pruebas el 23-10-2026 (0.5.27).
///
/// Por eso, si el apagado empieza sin que lo pida Windows (detener, reiniciar, desinstalar o apagar el equipo), el
/// servicio termina con ERROR_PROCESS_ABORTED y el SCM lo vuelve a levantar.
/// </summary>
/// <remarks>
/// Con el pedido de Windows, OnStop/OnShutdown corren ANTES de ApplicationStopping (son los que llaman a
/// StopApplication). Cuando el servidor se detiene solo, ApplicationStopping corre primero y OnStop llega después, desde
/// StopAsync del host: el aviso se decide en ApplicationStopping.
/// </remarks>
public sealed class ScmAwareServiceLifetime : WindowsServiceLifetime
{
    private const int ErrorProcessAborted = 1067;

    private volatile bool _stopRequestedByScm;

    public ScmAwareServiceLifetime(IHostEnvironment environment, IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory, IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        var logger = loggerFactory.CreateLogger<ScmAwareServiceLifetime>();
        applicationLifetime.ApplicationStopping.Register(() =>
        {
            if (_stopRequestedByScm) return;
            RunJournal.ReportStop(StopCause.SelfStop);
            ExitCode = ErrorProcessAborted;
            Environment.ExitCode = ErrorProcessAborted;
            logger.LogError("El servidor se detiene sin que Windows lo haya pedido: el servicio terminará con código " +
                "{Code} para que las acciones de recuperación lo vuelvan a iniciar.", ErrorProcessAborted);
        });
    }

    protected override void OnStop()
    {
        _stopRequestedByScm = true;
        RunJournal.ReportStop(StopCause.ServiceStop);
        base.OnStop();
    }

    protected override void OnShutdown()
    {
        _stopRequestedByScm = true;
        RunJournal.ReportStop(StopCause.SystemShutdown);
        base.OnShutdown();
    }
}
