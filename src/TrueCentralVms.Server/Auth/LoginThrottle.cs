using System.Collections.Concurrent;
using System.Net;

namespace TrueCentralVms.Server.Auth;

/// <summary>
/// Freno contra la fuerza bruta en el ingreso y en el cambio de clave (los dos prueban una contraseña sin sesión).
/// Cuenta los fallos por CUENTA y por IP de origen en una ventana móvil:
/// <list type="bullet">
/// <item>5 fallos seguidos de una cuenta la bloquean 15 minutos (desde cualquier IP), aunque después llegue la clave correcta.</item>
/// <item>20 fallos desde una misma IP (contra cualquier cuenta) bloquean esa IP 15 minutos. El propio servidor (loopback) no se
/// bloquea por IP, para no dejar al administrador sin consola.</item>
/// </list>
/// Un ingreso correcto limpia los fallos de la cuenta. Vive en memoria: un reinicio del servidor los olvida (aceptable: reiniciar
/// lo hace un administrador del equipo, y la bitácora conserva todos los intentos).
/// </summary>
public sealed class LoginThrottle(TimeProvider clock)
{
    public const int MaxAccountFailures = 5;
    public const int MaxIpFailures = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LockTime = TimeSpan.FromMinutes(15);

    private sealed class Counter
    {
        public int Failures;
        public DateTimeOffset FirstAt;
        public DateTimeOffset? LockedUntil;
    }

    private readonly ConcurrentDictionary<string, Counter> _accounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Counter> _ips = new(StringComparer.Ordinal);

    public LoginThrottle() : this(TimeProvider.System) { }

    private static string AccountKey(string? username) => (username ?? "").Trim().ToLowerInvariant();
    private static string IpKey(IPAddress? ip) => ip is null ? "" : (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();

    /// <summary>Si la cuenta o la IP están bloqueadas, cuánto falta; null = puede intentar.</summary>
    public TimeSpan? LockedFor(string? username, IPAddress? ip)
    {
        var now = clock.GetUtcNow();
        TimeSpan? left = null;
        foreach (var (map, key) in new[] { (_accounts, AccountKey(username)), (_ips, IpKey(ip)) })
        {
            if (key.Length == 0 || !map.TryGetValue(key, out var c)) continue;
            lock (c)
                if (c.LockedUntil is { } until && until > now && (left is null || until - now > left)) left = until - now;
        }
        return left;
    }

    /// <summary>Anota un fallo. Devuelve qué quedó bloqueado con este intento ("cuenta", "IP" o null).</summary>
    public string? RecordFailure(string? username, IPAddress? ip)
    {
        string? locked = null;
        if (Hit(_accounts, AccountKey(username), MaxAccountFailures)) locked = "cuenta";
        if (ip is not null && !IPAddress.IsLoopback(ip) && Hit(_ips, IpKey(ip), MaxIpFailures)) locked = locked is null ? "IP" : "cuenta e IP";
        return locked;
    }

    /// <summary>Cuentas e IP bloqueadas en este momento por intentos fallidos (para el estado del servidor).</summary>
    public (int Accounts, int Ips) LockedNow()
    {
        var now = clock.GetUtcNow();
        int Count(ConcurrentDictionary<string, Counter> map) =>
            map.Values.Count(c => { lock (c) return c.LockedUntil is { } until && until > now; });
        return (Count(_accounts), Count(_ips));
    }

    /// <summary>Ingreso correcto: la cuenta parte de cero (los fallos de la IP siguen contando).</summary>
    public void RecordSuccess(string? username) => _accounts.TryRemove(AccountKey(username), out _);

    private bool Hit(ConcurrentDictionary<string, Counter> map, string key, int max)
    {
        if (key.Length == 0) return false;
        var now = clock.GetUtcNow();
        var c = map.GetOrAdd(key, _ => new Counter { FirstAt = now });
        lock (c)
        {
            if (c.LockedUntil is { } until && until > now) return false;   // ya bloqueada: no se alarga
            if (c.LockedUntil is not null || now - c.FirstAt > Window)
            {
                c.Failures = 0;
                c.FirstAt = now;
                c.LockedUntil = null;
            }
            if (++c.Failures < max) return false;
            c.LockedUntil = now + LockTime;
            return true;
        }
    }

    /// <summary>Limpia lo vencido (lo llama el login de vez en cuando): sin esto, un barrido de usuarios inventados crece sin tope.</summary>
    public void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var map in new[] { _accounts, _ips })
            foreach (var (key, c) in map)
            {
                bool stale;
                lock (c) stale = (c.LockedUntil is null || c.LockedUntil <= now) && now - c.FirstAt > Window;
                if (stale) map.TryRemove(key, out _);
            }
    }

    /// <summary>"14 min" / "40 s" para el mensaje al usuario.</summary>
    public static string Describe(TimeSpan left) =>
        left.TotalMinutes >= 1 ? $"{Math.Ceiling(left.TotalMinutes):0} min" : $"{Math.Max(1, Math.Ceiling(left.TotalSeconds)):0} s";
}
