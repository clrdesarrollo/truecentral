using Microsoft.AspNetCore.SignalR;
using TrueCentralVms.Server.Auth;

namespace TrueCentralVms.Server.Hubs;

/// <summary>
/// Hub de tiempo real del VMS. Solo push servidor→cliente (vía
/// IHubContext&lt;VmsHub&gt;); los clientes no invocan métodos. La conexión
/// exige sesión válida (el middleware de tokens acepta ?access_token= además
/// del header, que es como autentica el WebSocket de SignalR).
///
/// Cada conexión entra al grupo de su usuario (<see cref="UserGroup"/>) para
/// que un aviso dirigido a personas concretas llegue solo a ellas.
/// </summary>
public sealed class VmsHub : Hub
{
    /// <summary>Grupo de SignalR con todas las conexiones de un usuario.</summary>
    public static string UserGroup(int userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        if (Context.GetHttpContext() is not { } http || ApiSecurity.CurrentSession(http) is not { } session)
        {
            Context.Abort();
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(session.UserId));
        await base.OnConnectedAsync();
    }
}
