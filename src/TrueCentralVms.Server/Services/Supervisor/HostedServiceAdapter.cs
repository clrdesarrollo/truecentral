namespace TrueCentralVms.Server.Services.Supervisor;

/// <summary>
/// Envuelve un <see cref="BackgroundService"/> del servidor para que el
/// watchdog lo pueda vigilar y reiniciar. La salud sale de su tarea de
/// ejecución: viva = sano; terminada con error o por sí sola = caído.
///
/// Un BackgroundService admite Stop + Start otra vez (cada StartAsync crea un
/// token nuevo y vuelve a llamar a ExecuteAsync); los servicios del sistema
/// están escritos para arrancar limpios en cada ejecución.
/// </summary>
public sealed class HostedServiceAdapter(
    string id, string name, string description, BackgroundService service,
    Func<string?>? disabledReason = null) : IManagedService
{
    public string Id => id;
    public string Name => name;
    public string Description => description;
    public string Kind => "subsystem";
    public bool CanStop => true;
    public string? DisabledReason => disabledReason?.Invoke();

    public Task StartAsync(CancellationToken ct)
    {
        // Una ejecución anterior que no terminó (detención vencida) no se
        // duplica: correrían dos copias del mismo servicio.
        if (service.ExecuteTask is { IsCompleted: false })
            throw new InvalidOperationException("La ejecución anterior del servicio aún no terminó.");
        // BackgroundService ENLAZA su token de parada al que recibe aquí: si se le
        // pasara el token con tope del supervisor, el servicio se detendría solo
        // al vencer ese tope. El tope de arranque lo aplica el supervisor por fuera.
        return service.StartAsync(CancellationToken.None);
    }

    public Task StopAsync(CancellationToken ct) => service.StopAsync(ct);

    public Task<ServiceHealth> CheckAsync(CancellationToken ct)
    {
        var task = service.ExecuteTask;
        if (task is null)
            return Task.FromResult(ServiceHealth.Down("El servicio nunca se inició."));
        if (task.IsFaulted)
        {
            string reason = task.Exception?.GetBaseException().Message ?? "error desconocido";
            return Task.FromResult(ServiceHealth.Down($"El servicio terminó con error: {reason}"));
        }
        if (task.IsCompleted)
            return Task.FromResult(ServiceHealth.Down("El servicio terminó por sí solo."));
        return Task.FromResult(ServiceHealth.Ok());
    }
}
