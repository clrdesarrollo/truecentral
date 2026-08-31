namespace TrueCentralVms.Core.Drivers;

/// <summary>
/// Capacidad opcional de un driver: volcar el estado real del equipo
/// (configuración de muro, ventanas, estado de decodificación) en texto,
/// para depuración desde el panel de administración.
/// </summary>
public interface IDecoderDiagnostics
{
    Task<string> GetDiagnosticsAsync(CancellationToken ct = default);
}
