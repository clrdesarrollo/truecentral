using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TrueCentralVms.Server.Auth;

/// <summary><paramref name="ClientId"/> identifica al cliente de escritorio
/// (cabecera X-TCVMS-Client: versión + máquina); null para el panel web.
/// Cuenta como un puesto del cupo de clientes simultáneos de la licencia.</summary>
public sealed record SessionInfo(int UserId, string Username, string Role, DateTime ExpiresAt, string? ClientId = null);

/// <summary>
/// Sesiones por token opaco en memoria. Suficiente para el v1; si más adelante
/// hace falta escalar a varios nodos, se sustituye por JWT sin tocar la API.
/// </summary>
public sealed class TokenService
{
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly TimeSpan _lifetime;

    public TokenService(TimeSpan? lifetime = null)
    {
        _lifetime = lifetime ?? TimeSpan.FromHours(12);
    }

    public (string Token, SessionInfo Session) Issue(int userId, string username, string role, string? clientId = null)
    {
        // Un cliente de escritorio que vuelve a entrar (reinicio, caída) no
        // debe ocupar dos puestos: se revocan sus sesiones anteriores.
        if (clientId is not null)
            foreach (var pair in _sessions.Where(p => p.Value.ClientId == clientId).ToList())
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

    public void Revoke(string token) => _sessions.TryRemove(token, out _);

    /// <summary>Puestos de cliente de escritorio en uso (máquinas distintas con sesión vigente).</summary>
    public int CountDesktopSessions(string? excludingClientId = null)
    {
        var now = DateTime.UtcNow;
        return _sessions.Values
            .Where(s => s.ClientId is not null && s.ExpiresAt >= now && s.ClientId != excludingClientId)
            .Select(s => s.ClientId)
            .Distinct()
            .Count();
    }

    public void RevokeUser(int userId)
    {
        foreach (var pair in _sessions.Where(p => p.Value.UserId == userId).ToList())
            _sessions.TryRemove(pair.Key, out _);
    }
}
