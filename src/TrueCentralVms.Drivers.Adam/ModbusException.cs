using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Adam;

/// <summary>
/// El módulo respondió, pero rechazó la petición con un código de excepción
/// Modbus. La conexión sigue sana: no hace falta reconectar.
/// </summary>
public sealed class ModbusException(byte function, byte code)
    : DriverException($"El módulo rechazó la petición (función {function}): {Describe(code)}.")
{
    /// <summary>Código de función Modbus de la petición rechazada.</summary>
    public byte Function { get; } = function;

    /// <summary>Código de excepción Modbus que devolvió el módulo.</summary>
    public byte Code { get; } = code;

    private static string Describe(byte code) => code switch
    {
        1 => "función no soportada",
        2 => "dirección fuera del mapa del módulo (¿modelo equivocado?)",
        3 => "valor no válido",
        4 => "falla interna del módulo",
        6 => "el módulo está ocupado",
        _ => $"código de excepción {code}",
    };
}
