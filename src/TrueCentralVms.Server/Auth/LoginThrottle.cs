using System.Collections.Concurrent;
using System.Net;

namespace TrueCentralVms.Server.Auth;

/// <summary>
/// Freno contra la fuerza bruta en el ingreso y en el cambio de clave (los dos prueban una contraseña sin sesión).
/// Cuenta los fallos por CUENTA y por IP de origen en una ventana móvil:
/// <list type="bullet">
/// <item>5 fallos seguidos de una cuenta la bloquean (desde cualquier IP), aunque después llegue la clave correcta.
/// Los bloqueos seguidos duran 15 min, 30 min y 1 h; desde el cuarto, la cuenta queda bloqueada hasta que un
/// administrador la desbloquee (Seguridad → Usuarios). El superadministrador no se bloquea indefinidamente (sería
/// dejar el sistema sin quien desbloquee): se queda en 1 h. La reincidencia vuelve a cero con un ingreso correcto,
/// con el desbloqueo o después de 24 h sin otro bloqueo.</item>
/// <item>20 fallos desde una misma IP (contra cualquier cuenta) bloquean esa IP 15 minutos. El propio servidor
/// (loopback) no se bloquea por IP, para no dejar al administrador sin consola.</item>
/// </list>
/// Vive en memoria: un reinicio del servidor lo olvida (aceptable: reiniciar lo hace un administrador del equipo, y la
/// bitácora conserva todos los intentos).
/// </summary>
public sealed class LoginThrottle(TimeProvider clock)
{
    public const int MaxAccountFailures = 5;
    public const int MaxIpFailures = 20;
    /// <summary>Bloqueos con tiempo que tiene una cuenta antes del que dura hasta el desbloqueo.</summary>
    public const int TimedLocks = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    /// <summary>Duración del primer bloqueo (y de los de una IP); cada bloqueo seguido de una cuenta dura el doble.</summary>
    public static readonly TimeSpan LockTime = TimeSpan.FromMinutes(15);
    /// <summary>Tiempo sin bloqueos después del cual la reincidencia vuelve a cero.</summary>
    public static readonly TimeSpan StreakMemory = TimeSpan.FromHours(24);

    /// <summary>Marca de "bloqueada hasta que un administrador la desbloquee".</summary>
    private static readonly DateTimeOffset Indefinite = DateTimeOffset.MaxValue;

    private sealed class Counter
    {
        public int Failures;
        public DateTimeOffset FirstAt;
        public DateTimeOffset? LockedUntil;
        /// <summary>Bloqueos seguidos (sin un ingreso correcto ni 24 h tranquilas entre medio).</summary>
        public int Streak;
    }

    /// <summary>Estado de bloqueo de una cuenta, para la lista de usuarios.</summary>
    /// <param name="LockedUntil">Hasta cuándo está bloqueada; null = no lo está (o lo está sin plazo).</param>
    /// <param name="Indefinite">Bloqueada hasta que un administrador la desbloquee.</param>
    /// <param name="Streak">Cuántos bloqueos seguidos lleva (el vigente incluido).</param>
    public sealed record AccountState(bool Locked, DateTimeOffset? LockedUntil, bool Indefinite, int Streak);

    /// <summary>Bloqueo vigente: cuánto falta (null si es hasta el desbloqueo) y si es de la cuenta o de la IP.</summary>
    public sealed record Lock(TimeSpan? Left, bool Account);

    private readonly ConcurrentDictionary<string, Counter> _accounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Counter> _ips = new(StringComparer.Ordinal);

    public LoginThrottle() : this(TimeProvider.System) { }

    private static string AccountKey(string? username) => (username ?? "").Trim().ToLowerInvariant();
    private static string IpKey(IPAddress? ip) => ip is null ? "" : (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();

    /// <summary>
    /// Duración del bloqueo número <paramref name="streak"/> de una cuenta (1 = el primero): 15 min, 30 min, 1 h y
    /// después null (hasta que un administrador la desbloquee), salvo <paramref name="exempt"/> (superadministrador),
    /// que se queda en 1 h.
    /// </summary>
    public static TimeSpan? LockTimeFor(int streak, bool exempt = false) =>
        streak > TimedLocks && !exempt ? null : LockTime * Math.Pow(2, Math.Clamp(streak - 1, 0, TimedLocks - 1));

    /// <summary>Si la cuenta o la IP están bloqueadas, cuánto falta (TimeSpan.MaxValue = hasta el desbloqueo); null = puede intentar.</summary>
    public TimeSpan? LockedFor(string? username, IPAddress? ip) =>
        LockOf(username, ip) is { } l ? l.Left ?? TimeSpan.MaxValue : null;

    /// <summary>El bloqueo vigente de la cuenta o de la IP (el que dura más); null = puede intentar.</summary>
    public Lock? LockOf(string? username, IPAddress? ip)
    {
        var now = clock.GetUtcNow();
        Lock? result = null;
        foreach (var (map, key, isAccount) in new[] { (_accounts, AccountKey(username), true), (_ips, IpKey(ip), false) })
        {
            if (key.Length == 0 || !map.TryGetValue(key, out var c)) continue;
            lock (c)
            {
                if (c.LockedUntil is not { } until || until <= now) continue;
                TimeSpan? left = until == Indefinite ? null : until - now;
                if (result is null || left is null || result.Left is { } other && left > other)
                    result = new Lock(left, isAccount);
            }
        }
        return result;
    }

    /// <summary>
    /// Anota un fallo. Devuelve qué quedó bloqueado con este intento ("cuenta", "IP", "cuenta e IP" o null).
    /// <paramref name="exempt"/>: la cuenta es la del superadministrador (no se bloquea sin plazo).
    /// </summary>
    public string? RecordFailure(string? username, IPAddress? ip, bool exempt = false)
    {
        string? locked = null;
        if (Hit(_accounts, AccountKey(username), MaxAccountFailures, escalate: true, exempt)) locked = "cuenta";
        if (ip is not null && !IPAddress.IsLoopback(ip) && Hit(_ips, IpKey(ip), MaxIpFailures, escalate: false, exempt: true))
            locked = locked is null ? "IP" : "cuenta e IP";
        return locked;
    }

    /// <summary>
    /// Intentos que le quedan a la cuenta antes del próximo bloqueo y cuánto duraría (null = hasta el desbloqueo).
    /// Se calcula igual exista o no la cuenta, así que decírselo a quien está ingresando no revela qué usuarios existen.
    /// </summary>
    public (int Remaining, TimeSpan? NextLock) AccountAttempts(string? username, bool exempt = false)
    {
        string key = AccountKey(username);
        if (key.Length == 0 || !_accounts.TryGetValue(key, out var c)) return (MaxAccountFailures, LockTimeFor(1, exempt));
        var now = clock.GetUtcNow();
        lock (c)
        {
            int streak = StreakAt(c, now);
            if (c.LockedUntil is { } until && until > now) return (0, LockTimeFor(streak + 1, exempt));
            int failures = c.LockedUntil is not null || now - c.FirstAt > Window ? 0 : c.Failures;
            return (Math.Max(0, MaxAccountFailures - failures), LockTimeFor(streak + 1, exempt));
        }
    }

    /// <summary>Estado de bloqueo de la cuenta (null si no tiene un bloqueo ni una reincidencia que recordar).</summary>
    public AccountState? StateOf(string? username)
    {
        string key = AccountKey(username);
        if (key.Length == 0 || !_accounts.TryGetValue(key, out var c)) return null;
        var now = clock.GetUtcNow();
        lock (c)
        {
            bool locked = c.LockedUntil is { } u && u > now;
            bool indefinite = locked && c.LockedUntil == Indefinite;
            int streak = StreakAt(c, now);
            return !locked && streak == 0 ? null
                : new AccountState(locked, locked && !indefinite ? c.LockedUntil : null, indefinite, streak);
        }
    }

    /// <summary>
    /// Desbloquea la cuenta (lo pide un administrador): borra los fallos, el bloqueo vigente y la reincidencia.
    /// Devuelve si estaba bloqueada.
    /// </summary>
    public bool Unlock(string? username)
    {
        if (!_accounts.TryRemove(AccountKey(username), out var c)) return false;
        lock (c) return c.LockedUntil is { } until && until > clock.GetUtcNow();
    }

    /// <summary>Cuentas e IP bloqueadas en este momento por intentos fallidos (para el estado del servidor).</summary>
    public (int Accounts, int Ips) LockedNow()
    {
        var now = clock.GetUtcNow();
        int Count(ConcurrentDictionary<string, Counter> map) =>
            map.Values.Count(c => { lock (c) return c.LockedUntil is { } until && until > now; });
        return (Count(_accounts), Count(_ips));
    }

    /// <summary>Ingreso correcto: la cuenta parte de cero, sin reincidencia (los fallos de la IP siguen contando).</summary>
    public void RecordSuccess(string? username) => _accounts.TryRemove(AccountKey(username), out _);

    /// <summary>Bloqueos seguidos que se recuerdan: 0 si el último terminó hace más de <see cref="StreakMemory"/>.</summary>
    private static int StreakAt(Counter c, DateTimeOffset now) =>
        c.LockedUntil is { } until && until != Indefinite && now - until > StreakMemory ? 0 : c.Streak;

    private bool Hit(ConcurrentDictionary<string, Counter> map, string key, int max, bool escalate, bool exempt)
    {
        if (key.Length == 0) return false;
        var now = clock.GetUtcNow();
        var c = map.GetOrAdd(key, _ => new Counter { FirstAt = now });
        lock (c)
        {
            if (c.LockedUntil is { } until && until > now) return false;   // ya bloqueada: no se alarga
            if (c.LockedUntil is not null || now - c.FirstAt > Window)
            {
                c.Streak = StreakAt(c, now);
                c.Failures = 0;
                c.FirstAt = now;
                c.LockedUntil = null;
            }
            if (++c.Failures < max) return false;
            c.Streak++;
            var duration = escalate ? LockTimeFor(c.Streak, exempt) : LockTime;
            c.LockedUntil = duration is { } d ? now + d : Indefinite;
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
                lock (c)
                    stale = (c.LockedUntil is null || c.LockedUntil <= now) && now - c.FirstAt > Window && StreakAt(c, now) == 0;
                if (stale) map.TryRemove(key, out _);
            }
    }

    /// <summary>"14 min" / "40 s" / "1 h" para el mensaje al usuario; null = hasta el desbloqueo.</summary>
    public static string Describe(TimeSpan? left)
    {
        if (left is not { } l || l >= TimeSpan.MaxValue - TimeSpan.FromDays(1)) return "hasta que un administrador la desbloquee";
        if (l.TotalMinutes < 1) return $"{Math.Max(1, Math.Ceiling(l.TotalSeconds)):0} s";
        int minutes = (int)Math.Ceiling(l.TotalMinutes);
        if (minutes < 60) return $"{minutes} min";
        int hours = minutes / 60, rest = minutes % 60;
        return rest == 0 ? $"{hours} h" : $"{hours} h {rest} min";
    }
}
