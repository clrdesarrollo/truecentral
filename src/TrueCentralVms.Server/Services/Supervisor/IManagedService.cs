namespace TrueCentralVms.Server.Services.Supervisor;

/// <summary>
/// Resultado de una comprobación de salud. <see cref="Transitioning"/> marca un
/// servicio que está sano pero en medio de un arranque propio (p. ej. MediaMTX
/// relanzándose tras una caída): el watchdog lo muestra "Iniciando" y no lo
/// reinicia encima.
/// </summary>
public readonly record struct ServiceHealth(bool Healthy, string? Detail = null, bool Transitioning = false)
{
    public static ServiceHealth Ok(string? detail = null) => new(true, detail);
    public static ServiceHealth Down(string reason) => new(false, reason);
    public static ServiceHealth Restarting(string detail) => new(true, detail, Transitioning: true);
}

/// <summary>
/// Un servicio que el watchdog puede vigilar y controlar. Para supervisar algo
/// nuevo basta con implementar esto (o envolver un BackgroundService con
/// <see cref="HostedServiceAdapter"/>) y agregarlo al catálogo de
/// <see cref="ServiceSupervisor"/>: aparece solo en el panel.
/// </summary>
public interface IManagedService
{
    /// <summary>Clave estable (va en la API y en la bitácora).</summary>
    string Id { get; }
    string Name { get; }
    string Description { get; }

    /// <summary>"process" (proceso hijo) o "subsystem" (hilo interno del servidor).</summary>
    string Kind { get; }

    /// <summary>false en los esenciales: solo se pueden reiniciar, nunca dejar detenidos.</summary>
    bool CanStop { get; }

    /// <summary>Motivo por el que está deshabilitado en la configuración, o null si aplica supervisión.</summary>
    string? DisabledReason { get; }

    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task<ServiceHealth> CheckAsync(CancellationToken ct);
}
