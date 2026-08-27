using TrueCentralVms.Drivers.Dahua.Interop;

namespace TrueCentralVms.Drivers.Dahua;

/// <summary>
/// Ciclo de vida global del NetSDK de Dahua: CLIENT_Init una sola vez por
/// proceso y CLIENT_Cleanup al salir. Todas las sesiones lo comparten.
/// </summary>
public static class DahuaSdk
{
    private static readonly object Sync = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        lock (Sync)
        {
            if (_initialized) return;

            if (!NetSdk.CLIENT_Init(IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException(
                    $"CLIENT_Init falló (NetSDK Dahua, error {NetSdk.CLIENT_GetLastError()}).");

            // Timeout de conexión 5 s con 1 reintento (igual criterio que Hikvision).
            NetSdk.CLIENT_SetConnectTime(5000, 1);

            AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
            _initialized = true;
        }
    }

    public static void Cleanup()
    {
        lock (Sync)
        {
            if (!_initialized) return;
            NetSdk.CLIENT_Cleanup();
            _initialized = false;
        }
    }

    /// <summary>Mensaje corto para el usuario según el nError del login.</summary>
    public static string DescribeLoginError(int code) => code switch
    {
        1 => "contraseña incorrecta",
        2 => "el usuario no existe",
        3 => "tiempo de espera agotado al iniciar sesión",
        4 => "la cuenta ya está en uso",
        5 => "cuenta bloqueada",
        6 => "cuenta en lista negra del equipo",
        7 => "el equipo está ocupado o sin recursos",
        9 => "no se pudo encontrar el equipo en la red",
        _ => "no se pudo conectar con el dispositivo (IP/puerto inaccesible o credenciales rechazadas)",
    };
}
