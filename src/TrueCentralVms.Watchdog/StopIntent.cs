using Microsoft.Win32;

namespace TrueCentralVms.Watchdog;

/// <summary>
/// La voluntad explícita de tener el servidor detenido: se marca al pulsar «Detener servicio» en el Watchdog y se borra
/// al iniciarlo (desde el Watchdog o desde cualquier otro lado). Mientras no esté marcada, el Watchdog vuelve a iniciar
/// el servicio si lo encuentra detenido.
///
/// Vive en el registro de la máquina (HKLM, el Watchdog corre elevado) y no en la memoria del Watchdog: sobrevive a
/// cerrarlo, a reiniciar el equipo y vale para todos los Watchdog abiertos (uno por cada administrador con sesión).
/// </summary>
internal static class StopIntent
{
    private const string KeyPath = @"SOFTWARE\CLRobotics\TrueCentralVMS\Watchdog";
    private const string ValueName = "StopRequestedUtc";

    /// <summary>Cuándo se pidió detener (UTC), o null si no hay pedido vigente.</summary>
    public static DateTime? Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string text
                && DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var when)
                ? when : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Set()
    {
        using var key = Registry.LocalMachine.CreateSubKey(KeyPath);
        key.SetValue(ValueName, DateTime.UtcNow.ToString("O"));
    }

    public static void Clear()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Sin permiso para escribir: la marca queda y el Watchdog sigue sin reiniciar; no es peligroso.
        }
    }
}
