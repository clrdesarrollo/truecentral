using TrueCentralVms.Drivers.Hikvision.Interop;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>Error reportado por HCNetSDK, con el código devuelto por NET_DVR_GetLastError.</summary>
public sealed class HikvisionException : Exception
{
    public uint ErrorCode { get; }

    public HikvisionException(string operation, uint errorCode)
        : base($"{operation} falló (HCNetSDK error {errorCode}: {Describe(errorCode)})")
    {
        ErrorCode = errorCode;
    }

    public static HikvisionException FromLastError(string operation) =>
        new(operation, CHCNetSDK.NET_DVR_GetLastError());

    /// <summary>Mensaje corto apto para mostrar al usuario final.</summary>
    public static string DescribeForUser(uint code) => Describe(code);

    private static string Describe(uint code) => code switch
    {
        1 => "usuario o contraseña incorrectos",
        2 => "permisos insuficientes",
        4 => "canal inválido",
        5 => "demasiadas conexiones al dispositivo",
        7 => "no se pudo conectar con el dispositivo (IP/puerto inaccesible)",
        10 => "timeout al recibir datos del dispositivo",
        12 => "llamada a la API en orden incorrecto",
        17 => "parámetro inválido",
        23 => "función no soportada por el dispositivo",
        29 => "operación fallida en el dispositivo",
        43 => "memoria insuficiente en el dispositivo",
        47 => "usuario no existe",
        153 => "usuario bloqueado por intentos fallidos",
        _ => "ver tabla de errores de HCNetSDK",
    };
}
