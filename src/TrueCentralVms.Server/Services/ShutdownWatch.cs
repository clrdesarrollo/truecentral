using Microsoft.Extensions.Options;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Vigila el apagado ordenado del servidor. Si el host no termina de detener
/// sus servicios en <see cref="HostOptions.ShutdownTimeout"/>, el
/// WindowsServiceLifetime de .NET lanza OperationCanceledException desde
/// app.Run() y el proceso se cae (evento 1000/1026 en el registro de Windows)
/// aunque PostgreSQL ya se haya detenido. Esta clase deja en el registro QUIÉN
/// seguía deteniéndose (para corregirlo en el servicio culpable) y permite que
/// Program trate ese caso como un apagado lento y no como una caída.
/// </summary>
/// <remarks>
/// Todo se registra mientras el host sigue vivo: cuando app.Run() devuelve o
/// lanza, el host ya se desechó (sus servicios y el logger del registro de
/// eventos incluidos).
/// </remarks>
internal static class ShutdownWatch
{
    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(10);
    private static volatile bool _stopping;

    public static void Attach(WebApplication app)
    {
        var lifetime = app.Lifetime;
        var logger = app.Logger;
        var services = app.Services;
        var limit = services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout;
        DateTime started = default;

        lifetime.ApplicationStopping.Register(() =>
        {
            _stopping = true;
            started = DateTime.UtcNow;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(ReportEvery, lifetime.ApplicationStopped);
                        if (Pending(services) is { Length: > 0 } pending)
                            logger.LogWarning("Apagado en curso hace {Seconds:0} s; siguen deteniéndose: {Pending}.",
                                (DateTime.UtcNow - started).TotalSeconds, pending);
                    }
                }
                catch (OperationCanceledException) { /* el host terminó de detenerse */ }
                catch (Exception ex) { logger.LogDebug(ex, "Vigilancia del apagado interrumpida."); }
            });
        });

        // ApplicationStopped corre con el host todavía vivo (antes del tope del
        // WindowsServiceLifetime): último momento para dejar constancia.
        lifetime.ApplicationStopped.Register(() =>
        {
            var took = DateTime.UtcNow - started;
            if (took < limit) return;
            logger.LogWarning("El apagado superó el tope de {Limit:0} s ({Seconds:0} s); quedaron sin terminar: {Pending}.",
                limit.TotalSeconds, took.TotalSeconds,
                Pending(services) is { Length: > 0 } p ? p : "(ninguno en segundo plano)");
        });
    }

    /// <summary>
    /// Excepción que el host lanza solo porque el apagado superó el tope, con
    /// todo lo demás ya detenido: no es una falla del servidor.
    /// </summary>
    public static bool IsShutdownTimeout(Exception ex) =>
        _stopping && ex switch
        {
            OperationCanceledException => true,
            AggregateException agg => agg.Flatten().InnerExceptions.All(e => e is OperationCanceledException),
            _ => false,
        };

    /// <summary>Servicios en segundo plano cuyo ciclo aún no termina.</summary>
    private static string Pending(IServiceProvider services) =>
        string.Join(", ", services.GetServices<IHostedService>()
            .OfType<BackgroundService>()
            .Where(s => s.ExecuteTask is { IsCompleted: false })
            .Select(s => s.GetType().Name)
            .Distinct());
}
