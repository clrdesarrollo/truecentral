using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Core.Drivers;

/// <summary>
/// Lee y escribe la configuración PROPIA de un equipo de control de acceso
/// (parámetros de sus puertas y de sus lectores). Va aparte de
/// <see cref="IAccessControlDriver"/> porque no toda marca lo ofrece y porque
/// no es operación sino mantenimiento: se registra por clave de driver y el
/// servidor lo busca por ella. Lanza <see cref="DriverException"/> con mensaje
/// en español cuando el equipo no contesta o rechaza un valor.
/// </summary>
public interface IAccessDeviceSettingsProvider
{
    /// <summary>Clave del driver cuyos equipos sabe configurar ("hikvision-isapi").</summary>
    string DriverKey { get; }

    /// <summary>Lee del equipo todos sus bloques de configuración.</summary>
    Task<AccessDeviceSettingsDto> ReadAsync(AccessConnectionInfo info, CancellationToken ct = default);

    /// <summary>
    /// Escribe en el equipo los valores cambiados de un bloque y devuelve el
    /// bloque releído, para que la página muestre lo que el equipo dejó
    /// (que puede no ser exactamente lo pedido: redondeos, topes).
    /// </summary>
    Task<AccessSettingsSectionDto> ApplyAsync(AccessConnectionInfo info, string sectionKey,
        IReadOnlyDictionary<string, string?> values, CancellationToken ct = default);
}
