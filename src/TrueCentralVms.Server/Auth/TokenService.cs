using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TrueCentralVms.Server.Auth;

public sealed record SessionInfo(int UserId, string Username, string Role, DateTime ExpiresAt);

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

    public (string Token, SessionInfo Session) Issue(int userId, string username, string role)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var session = new SessionInfo(userId, username, role, DateTime.UtcNow.Add(_lifetime));
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

    public void RevokeUser(int userId)
    {
        foreach (var pair in _sessions.Where(p => p.Value.UserId == userId).ToList())
            _sessions.TryRemove(pair.Key, out _);
    }
}
