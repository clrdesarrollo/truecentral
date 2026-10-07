using Microsoft.AspNetCore.SignalR;
using TrueCentralVms.Server.Auth;

namespace TrueCentralVms.Server.Hubs;

/// <summary>
/// Hub de tiempo real del VMS. Solo push servidor→cliente (vía
/// IHubContext&lt;VmsHub&gt;); los clientes no invocan métodos. La conexión
/// exige sesión válida (el middleware de tokens acepta ?access_token= además
/// del header, que es como autentica el WebSocket de SignalR, y sin sesión
/// vigente responde 401 antes de abrirla).
///
/// Cada conexión entra al grupo de su usuario (<see cref="UserGroup"/>) para
/// que un aviso dirigido a personas concretas llegue solo a ellas, y queda
/// registrada en <see cref="HubConnections"/> para cortarla cuando su sesión
/// se revoque (SignalR no vuelve a validar después de conectar).
/// </summary>
public sealed class VmsHub(HubConnections connections, TokenService tokens) : Hub
{
    /// <summary>Grupo de SignalR con todas las conexiones de un usuario.</summary>
    public static string UserGroup(int userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        if (Context.GetHttpContext() is not { } http || ApiSecurity.CurrentSession(http) is not { } session
            || ApiSecurity.CurrentToken(http) is not { } token)
        {
            Context.Abort();
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(session.UserId));

        // Primero se registra y recién después se confirma que la sesión sigue
        // vigente: si la revocan justo entre el middleware y este punto, o la
        // revocación ya ve la conexión y la corta, o esta comprobación ve el
        // token revocado.
        connections.Add(Context, session, token);
        if (tokens.Validate(token) is null)
        {
            connections.Remove(Context.ConnectionId);
            Context.Abort();
            return;
        }
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
