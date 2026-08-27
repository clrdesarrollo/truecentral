using System.Collections.Concurrent;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Hikvision.Interop;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Caché de sesiones HCNetSDK por dispositivo, para operaciones interactivas
/// (PTZ) donde el login por comando agregaría medio segundo de latencia. Las
/// sesiones ociosas se cierran a los 3 minutos; un fallo de comando invalida
/// la sesión y el siguiente intento vuelve a iniciar sesión.
/// </summary>
internal static class HikvisionSessionCache
{
    private sealed class Entry
    {
        public int UserId;
        public DateTime LastUsed;
    }

    private static readonly ConcurrentDictionary<string, Entry> Sessions = new();
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);
    private static readonly Timer Cleanup = new(_ => PruneIdle(), null,
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

    private static string KeyOf(DeviceConnectionInfo info) =>
        $"{info.Host}|{info.Port}|{info.Username}|{info.Password.GetHashCode()}";

    /// <summary>Sesión vigente o login nuevo. Lanza DriverException si el equipo rechaza.</summary>
    public static int GetOrLogin(DeviceConnectionInfo info)
    {
        HikvisionSdk.EnsureInitialized();
        string key = KeyOf(info);
        var entry = Sessions.GetOrAdd(key, _ => new Entry { UserId = -1 });
        lock (entry)
        {
            if (entry.UserId >= 0)
            {
                entry.LastUsed = DateTime.UtcNow;
                return entry.UserId;
            }
            var deviceInfo = new CHCNetSDK.NET_DVR_DEVICEINFO_V30();
            int userId = CHCNetSDK.NET_DVR_Login_V30(info.Host, info.Port, info.Username, info.Password, ref deviceInfo);
            if (userId < 0)
            {
                uint code = CHCNetSDK.NET_DVR_GetLastError();
                throw new DriverException(
                    $"No se pudo iniciar sesión en {info.Host}:{info.Port}: {HikvisionException.DescribeForUser(code)} (error {code}).");
            }
            entry.UserId = userId;
            entry.LastUsed = DateTime.UtcNow;
            return userId;
        }
    }

    /// <summary>Cierra la sesión tras un fallo (el siguiente comando hace login limpio).</summary>
    public static void Invalidate(DeviceConnectionInfo info)
    {
        if (!Sessions.TryRemove(KeyOf(info), out var entry))
            return;
        lock (entry)
        {
            if (entry.UserId >= 0)
                CHCNetSDK.NET_DVR_Logout(entry.UserId);
            entry.UserId = -1;
        }
    }

    private static void PruneIdle()
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;
        foreach (var pair in Sessions)
        {
            if (pair.Value.LastUsed >= cutoff)
                continue;
            if (Sessions.TryRemove(pair.Key, out var entry))
            {
                lock (entry)
                {
                    if (entry.UserId >= 0)
                        CHCNetSDK.NET_DVR_Logout(entry.UserId);
                    entry.UserId = -1;
                }
            }
        }
    }
}
