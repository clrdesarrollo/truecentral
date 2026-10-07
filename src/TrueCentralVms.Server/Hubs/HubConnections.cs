using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using TrueCentralVms.Server.Auth;

namespace TrueCentralVms.Server.Hubs;

/// <summary>
/// Conexiones vivas del hub, con el usuario y el token con que entraron. El hub
/// valida la sesión solo al conectarse; esto es lo que permite cortarlas cuando
/// la sesión deja de valer, para que un usuario deshabilitado o eliminado no
/// siga recibiendo alarmas, accesos y avisos hasta desconectarse solo (mientras
/// nadie tenga la vista filtrada por ubicación, todo sale por Clients.All).
///
/// Cortar es <see cref="HubCallerContext.Abort"/>: SignalR le pide al cliente
/// que no se reconecte solo, y su próximo intento choca con el 401 que el
/// middleware de tokens da en la ruta del hub. Los cortes los ordena
/// <see cref="TokenService"/> al revocar o barrer sesiones.
/// </summary>
public sealed class HubConnections(ILogger<HubConnections> logger)
{
    private sealed record Entry(HubCallerContext Context, int UserId, string Username, string Token);

    /// <summary>Por id de conexión. Son pocas (una por puesto o pestaña del
    /// panel): los cortes las recorren todas sin índices aparte.</summary>
    private readonly ConcurrentDictionary<string, Entry> _connections = new();

    public void Add(HubCallerContext context, SessionInfo session, string token) =>
        _connections[context.ConnectionId] = new Entry(context, session.UserId, session.Username, token);

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    /// <summary>Nombre del usuario según sus conexiones (para la bitácora, si ya no le quedaban sesiones).</summary>
    public string? UsernameOf(int userId) =>
        _connections.Values.FirstOrDefault(e => e.UserId == userId)?.Username;

    /// <summary>Hay alguna conexión viva que entró con ese token (el cliente está corriendo).</summary>
    public bool HasToken(string token) => _connections.Values.Any(e => e.Token == token);

    /// <summary>Corta las conexiones que entraron con ese token.</summary>
    public int AbortToken(string token, string reason) => Abort(e => e.Token == token, reason);

    /// <summary>Corta todas las conexiones del usuario, con cualquier token.</summary>
    public int AbortUser(int userId, string reason) => Abort(e => e.UserId == userId, reason);

    /// <summary>Corta las conexiones cuyo token ya no tiene sesión.</summary>
    public int AbortWithoutSession(Func<string, bool> hasSession, string reason) =>
        Abort(e => !hasSession(e.Token), reason);

    private int Abort(Func<Entry, bool> match, string reason)
    {
        int count = 0;
        foreach (var (connectionId, entry) in _connections)
        {
            if (!match(entry) || !_connections.TryRemove(connectionId, out _)) continue;
            try
            {
                entry.Context.Abort();
                count++;
                logger.LogInformation("Hub: conexión {ConnectionId} de '{Username}' cortada ({Reason}).",
                    connectionId, entry.Username, reason);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Hub: no se pudo cortar la conexión {ConnectionId} de '{Username}'.",
                    connectionId, entry.Username);
            }
        }
        return count;
    }
}
