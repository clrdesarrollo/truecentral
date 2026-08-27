using TrueCentralVms.Drivers.Hikvision.Interop;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Ciclo de vida global de HCNetSDK: NET_DVR_Init debe llamarse una sola vez por proceso
/// y NET_DVR_Cleanup al salir. Todas las sesiones comparten esta inicialización.
/// </summary>
public static class HikvisionSdk
{
    private static readonly object Sync = new();
    private static bool _initialized;

    /// <summary>Directorio opcional para logs del SDK (se activa si se define antes del primer uso).</summary>
    public static string? LogDirectory { get; set; }

    public static void EnsureInitialized()
    {
        lock (Sync)
        {
            if (_initialized) return;

            if (!CHCNetSDK.NET_DVR_Init())
                throw HikvisionException.FromLastError("NET_DVR_Init");

            if (!string.IsNullOrWhiteSpace(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
                CHCNetSDK.NET_DVR_SetLogToFile(3, LogDirectory, true);
            }

            // Timeout de conexión 5s con 1 reintento; reconexión automática cada 10s.
            CHCNetSDK.NET_DVR_SetConnectTime(5000, 1);
            CHCNetSDK.NET_DVR_SetReconnect(10000, 1);

            AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
            _initialized = true;
        }
    }

    public static void Cleanup()
    {
        lock (Sync)
        {
            if (!_initialized) return;
            CHCNetSDK.NET_DVR_Cleanup();
            _initialized = false;
        }
    }
}
