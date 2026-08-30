using System.Collections.Concurrent;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Dahua.Interop;

namespace TrueCentralVms.Drivers.Dahua;

/// <summary>
/// Caché de sesiones del NetSDK por dispositivo, para operaciones
/// interactivas (búsqueda de grabaciones) donde iniciar sesión en cada
/// consulta agregaría medio segundo de latencia. Las sesiones ociosas se
/// cierran a los 3 minutos; un fallo invalida la sesión y el siguiente
/// intento vuelve a iniciar sesión. Mismo criterio que
/// <c>HikvisionSessionCache</c>.
/// </summary>
internal static class DahuaSessionCache
{
    private sealed class Entry
    {
        public long LoginId;
        public DateTime LastUsed;
    }

    private static readonly ConcurrentDictionary<string, Entry> Sessions = new();
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);
    private static readonly Timer Cleanup = new(_ => PruneIdle(), null,
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

    private static string KeyOf(DeviceConnectionInfo info) =>
        $"{info.Host}|{info.Port}|{info.Username}|{info.Password.GetHashCode()}";

    /// <summary>Sesión vigente o login nuevo. Lanza DriverException si el equipo rechaza.</summary>
    public static long GetOrLogin(DeviceConnectionInfo info)
    {
        DahuaSdk.EnsureInitialized();
        string key = KeyOf(info);
        var entry = Sessions.GetOrAdd(key, _ => new Entry { LoginId = 0 });
        lock (entry)
        {
            if (entry.LoginId != 0)
            {
                entry.LastUsed = DateTime.UtcNow;
                return entry.LoginId;
            }
            (long loginId, _) = DahuaDeviceDriver.Login(info);
            entry.LoginId = loginId;
            entry.LastUsed = DateTime.UtcNow;
            return loginId;
        }
    }

    /// <summary>Cierra la sesión tras un fallo (el siguiente comando hace login limpio).</summary>
    public static void Invalidate(DeviceConnectionInfo info)
    {
        if (!Sessions.TryRemove(KeyOf(info), out var entry))
            return;
        lock (entry)
        {
            if (entry.LoginId != 0)
                NetSdk.CLIENT_Logout(entry.LoginId);
            entry.LoginId = 0;
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
                    if (entry.LoginId != 0)
                        NetSdk.CLIENT_Logout(entry.LoginId);
                    entry.LoginId = 0;
                }
            }
        }
    }
}
