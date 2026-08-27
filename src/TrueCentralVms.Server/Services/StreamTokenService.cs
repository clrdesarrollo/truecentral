using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TrueCentralVms.Server.Services;

/// <summary>Concesión emitida por /api/streams/request, pendiente de canje por MediaMTX.</summary>
public sealed record StreamGrant(
    int UserId,
    string Username,
    string Path,
    int DeviceId,
    string DeviceName,
    int RtspChannel,
    string Profile,
    DateTime ExpiresAt);

/// <summary>
/// Tokens de streaming de corta vida: autorizan INICIAR una lectura RTSP de
/// una ruta concreta de MediaMTX. El cliente los embebe en la URL
/// (?token=...) y MediaMTX los canjea contra /api/streaming/auth. Expirado el
/// token, las sesiones ya establecidas no se cortan; una reconexión requiere
/// concesión nueva.
/// </summary>
public sealed class StreamTokenService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, StreamGrant> _grants = new();

    public (string Token, StreamGrant Grant) Issue(int userId, string username, string path,
        int deviceId, string deviceName, int rtspChannel, string profile)
    {
        PruneExpired();
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var grant = new StreamGrant(userId, username, path, deviceId, deviceName, rtspChannel, profile,
            DateTime.UtcNow.Add(Lifetime));
        _grants[token] = grant;
        return (token, grant);
    }

    /// <summary>Valida el token contra la ruta solicitada. No lo consume: reintentos del reproductor dentro del TTL son válidos.</summary>
    public StreamGrant? Validate(string token, string path)
    {
        if (!_grants.TryGetValue(token, out var grant)) return null;
        if (grant.ExpiresAt < DateTime.UtcNow)
        {
            _grants.TryRemove(token, out _);
            _recorded.TryRemove(token, out _);
            return null;
        }
        return grant.Path.Equals(path, StringComparison.OrdinalIgnoreCase) ? grant : null;
    }

    private readonly ConcurrentDictionary<string, bool> _recorded = new();

    /// <summary>
    /// true solo la primera vez para cada token. MediaMTX autoriza DESCRIBE y
    /// PLAY por separado (dos callbacks por lector): la sesión de auditoría se
    /// registra una sola vez por concesión.
    /// </summary>
    public bool TryMarkSessionRecorded(string token) => _recorded.TryAdd(token, true);

    private void PruneExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _grants)
            if (pair.Value.ExpiresAt < now)
            {
                _grants.TryRemove(pair.Key, out _);
                _recorded.TryRemove(pair.Key, out _);
            }
    }
}
