using System.Collections.Concurrent;
using System.Security.Cryptography;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Auth;

/// <summary><paramref name="ClientId"/> identifica al cliente de escritorio
/// (cabecera X-TCVMS-Client: versión + máquina); null para el panel web.
/// Cuenta como un puesto del cupo de clientes simultáneos de la licencia.</summary>
public sealed record SessionInfo(int UserId, string Username, string Role, DateTime ExpiresAt, string? ClientId = null);

/// <summary>
/// Sesiones por token opaco en memoria. Suficiente para el v1; si más adelante
/// hace falta escalar a varios nodos, se sustituye por JWT sin tocar la API.
///
/// Una sesión que deja de valer se lleva sus conexiones del hub, que solo
/// valida al conectarse: <see cref="Revoke"/> corta las de ese token,
/// <see cref="RevokeUser"/> todas las del usuario, y un barrido periódico las
/// de sesiones vencidas o reemplazadas. Ninguna conexión en tiempo real
/// sobrevive a su sesión.
/// </summary>
public sealed class TokenService : IDisposable
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly TimeSpan _lifetime;
    private readonly HubConnections _connections;
    private readonly AuditService _audit;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<TokenService> _logger;
    private readonly Timer _sweep;

    public TokenService(HubConnections connections, AuditService audit, IHttpContextAccessor http,
        ILogger<TokenService> logger, TimeSpan? lifetime = null)
    {
        _connections = connections;
        _audit = audit;
        _http = http;
        _logger = logger;
        _lifetime = lifetime ?? TimeSpan.FromHours(12);
        _sweep = new Timer(_ => Sweep(), null, SweepInterval, SweepInterval);
    }

    public (string Token, SessionInfo Session) Issue(int userId, string username, string role, string? clientId = null)
    {
        // Un cliente de escritorio que vuelve a entrar (reinicio, caída, o una
        // VERSIÓN NUEVA del cliente en el mismo equipo) no debe ocupar dos
        // puestos: se revocan las sesiones anteriores de ese equipo. Sus
        // conexiones del hub, si alguna sigue viva, las corta el barrido.
        if (SeatOf(clientId) is { } seat)
            foreach (var pair in _sessions.Where(p => SeatOf(p.Value.ClientId) == seat).ToList())
                _sessions.TryRemove(pair.Key, out _);
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var session = new SessionInfo(userId, username, role, DateTime.UtcNow.Add(_lifetime), clientId);
        _sessions[token] = session;
        return (token, session);
    }

    public SessionInfo? Validate(string token)
    {
        if (!_sessions.TryGetValue(token, out var session)) return null;
        if (session.ExpiresAt < DateTime.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }
        return session;
    }

    /// <summary>
    /// Cierre de sesión: revoca ese token y corta solo sus conexiones del hub;
    /// las demás sesiones del usuario (otro puesto, el panel web) siguen.
    /// </summary>
    public void Revoke(string token)
    {
        if (_sessions.TryRemove(token, out _))
            _connections.AbortToken(token, "cierre de sesión");
    }

    /// <summary>Puestos de cliente de escritorio en uso (equipos distintos con sesión vigente).</summary>
    public int CountDesktopSessions(string? excludingClientId = null)
    {
        var now = DateTime.UtcNow;
        string? excluded = SeatOf(excludingClientId);
        return _sessions.Values
            .Where(s => s.ClientId is not null && s.ExpiresAt >= now)
            .Select(s => SeatOf(s.ClientId))
            .Where(seat => seat != excluded)
            .Distinct()
            .Count();
    }

    /// <summary>
    /// El puesto de un cliente de escritorio es el EQUIPO, no la versión: la
    /// cabecera trae "wpf/0.5.10 (PC-GUARDIA)" y el puesto es "PC-GUARDIA".
    /// Contar por la cabecera completa hacía que actualizar el cliente en un
    /// mismo PC ocupara un puesto nuevo por versión mientras la sesión vieja
    /// seguía vigente (hasta 12 h), y una licencia de 3 puestos se llenaba con
    /// un solo equipo.
    /// </summary>
    public static string? SeatOf(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return null;
        int open = clientId.LastIndexOf('('), close = clientId.LastIndexOf(')');
        string seat = open >= 0 && close > open ? clientId[(open + 1)..close].Trim() : clientId.Trim();
        return (seat.Length > 0 ? seat : clientId.Trim()).ToUpperInvariant();
    }

    /// <summary>Versión del cliente según su cabecera ("wpf/0.5.10 (PC)" → "0.5.10").</summary>
    public static string? VersionOf(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return null;
        int slash = clientId.IndexOf('/'), open = clientId.IndexOf('(');
        if (slash < 0) return null;
        string version = (open > slash ? clientId[(slash + 1)..open] : clientId[(slash + 1)..]).Trim();
        return version.Length > 0 ? version : null;
    }

    /// <summary>Un puesto de cliente de escritorio, como lo muestra el panel.</summary>
    /// <param name="Connected">Tiene el canal en tiempo real abierto: el cliente está corriendo.
    /// Sin él, la sesión quedó colgada (se cerró el cliente sin cerrar sesión).</param>
    public sealed record DesktopSeat(string Seat, string? Version, string Username, string Role,
        DateTime StartedAt, DateTime ExpiresAt, bool Connected);

    /// <summary>Puestos de cliente de escritorio en uso, con la sesión más reciente de cada uno.</summary>
    public IReadOnlyList<DesktopSeat> DesktopSeats()
    {
        var now = DateTime.UtcNow;
        return _sessions
            .Where(p => p.Value.ClientId is not null && p.Value.ExpiresAt >= now)
            .GroupBy(p => SeatOf(p.Value.ClientId)!)
            .Select(g =>
            {
                var latest = g.OrderByDescending(p => p.Value.ExpiresAt).First().Value;
                bool connected = g.Any(p => _connections.HasToken(p.Key));
                return new DesktopSeat(g.Key, VersionOf(latest.ClientId), latest.Username, latest.Role,
                    latest.ExpiresAt - _lifetime, latest.ExpiresAt, connected);
            })
            .OrderBy(s => s.Seat, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Equipos a los que un administrador les cortó la sesión: no pueden volver
    /// a entrar por un rato. Sin esto, un cliente que sigue abierto renovaría
    /// solo la sesión al instante y recuperaría el puesto sin que nadie lo note.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _releasedSeats = new();

    /// <summary>Cuánto dura el bloqueo de un puesto liberado por un administrador.</summary>
    public static readonly TimeSpan ReleasedSeatBlock = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Libera un puesto: cierra todas las sesiones de ese equipo, corta su canal
    /// en tiempo real y lo bloquea un minuto. Devuelve cuántas sesiones cerró.
    /// </summary>
    public int ReleaseSeat(string seat)
    {
        string key = seat.Trim().ToUpperInvariant();
        int closed = 0;
        foreach (var pair in _sessions.Where(p => SeatOf(p.Value.ClientId) == key).ToList())
        {
            if (!_sessions.TryRemove(pair.Key, out _)) continue;
            _connections.AbortToken(pair.Key, "puesto liberado por un administrador");
            closed++;
        }
        _releasedSeats[key] = DateTime.UtcNow.Add(ReleasedSeatBlock);
        return closed;
    }

    /// <summary>El equipo de ese cliente fue liberado hace menos de un minuto (no puede entrar todavía).</summary>
    public bool IsSeatReleased(string? clientId)
    {
        if (SeatOf(clientId) is not { } seat || !_releasedSeats.TryGetValue(seat, out var until)) return false;
        if (until > DateTime.UtcNow) return true;
        _releasedSeats.TryRemove(seat, out _);
        return false;
    }

    /// <summary>
    /// Revoca todas las sesiones del usuario (deshabilitado, eliminado, clave
    /// restablecida o cambiada, cambio de rol) y corta en el acto todas sus
    /// conexiones del hub, también las de tokens que ya no estaban: si no,
    /// seguiría recibiendo alarmas, accesos y avisos hasta desconectarse solo.
    /// Se llama con el cambio ya guardado: el cliente cortado intenta renovar
    /// la sesión de inmediato y, antes del guardado, todavía lo lograría.
    /// </summary>
    public void RevokeUser(int userId)
    {
        var revoked = new List<SessionInfo>();
        foreach (var pair in _sessions.Where(p => p.Value.UserId == userId).ToList())
            if (_sessions.TryRemove(pair.Key, out var session))
                revoked.Add(session);
        string? username = revoked.FirstOrDefault()?.Username ?? _connections.UsernameOf(userId);
        int cut = _connections.AbortUser(userId, "sesiones revocadas");
        if (revoked.Count > 0 || cut > 0)
            AuditRevocation(userId, username ?? $"#{userId}", revoked.FirstOrDefault()?.Role, revoked.Count, cut);
    }

    /// <summary>
    /// Evidencia de que el acceso se cortó en el acto (ISO 27001): cuántas
    /// sesiones se cerraron y cuántas conexiones en tiempo real se cortaron. El
    /// actor es quien hizo la solicitud en curso: el administrador que cambió al
    /// usuario o, sin sesión en la solicitud (cambio de clave con la clave
    /// actual), el propio usuario. Fuera de una solicitud, el sistema.
    /// </summary>
    private void AuditRevocation(int userId, string username, string? role, int sessions, int connections)
    {
        string detail = $"Se revocaron las sesiones de '{username}': {sessions} sesión(es) cerrada(s) y " +
                        $"{connections} conexión(es) en tiempo real cortada(s) en el acto.";
        var data = new { sessions, connections };
        string id = userId.ToString();
        var ctx = _http.HttpContext;
        // Sin esperar: RevokeUser es sincrónico y la bitácora nunca lanza.
        _ = ctx is null
            ? _audit.LogSystemAsync("auth", "sessions-revoked", "user", id, username, detail, data: data)
            : ApiSecurity.CurrentSession(ctx) is not null
                ? _audit.LogAsync(ctx, "auth", "sessions-revoked", "user", id, username, detail, data: data)
                : _audit.LogAsAsync(ctx, userId, username, role, "auth", "sessions-revoked", "user", id, username, detail, data: data);
    }

    /// <summary>
    /// Barrido periódico: borra las sesiones vencidas (<see cref="Validate"/>
    /// solo las borra cuando alguien las presenta) y corta las conexiones del
    /// hub que quedaron sin sesión: vencida, o reemplazada por un nuevo inicio
    /// de sesión del mismo puesto. Las revocaciones explícitas ya cortan en el acto.
    /// </summary>
    private void Sweep()
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var pair in _sessions.Where(p => p.Value.ExpiresAt < now).ToList())
                _sessions.TryRemove(pair);
            _connections.AbortWithoutSession(_sessions.ContainsKey, "sesión vencida o reemplazada");
        }
        catch (Exception ex)
        {
            // Una excepción que escape del timer tumbaría el proceso.
            _logger.LogWarning(ex, "Falló el barrido de sesiones vencidas.");
        }
    }

    public void Dispose() => _sweep.Dispose();
}
