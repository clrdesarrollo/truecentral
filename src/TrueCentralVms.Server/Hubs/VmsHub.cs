using Microsoft.AspNetCore.SignalR;
using TrueCentralVms.Server.Auth;

namespace TrueCentralVms.Server.Hubs;

/// <summary>
/// Hub de tiempo real del VMS. Solo push servidor→cliente (vía
/// IHubContext&lt;VmsHub&gt;); los clientes no invocan métodos. La conexión
/// exige sesión válida (el middleware de tokens acepta ?access_token= además
/// del header, que es como autentica el WebSocket de SignalR).
/// </summary>
public sealed class VmsHub : Hub
{
    public override Task OnConnectedAsync()
    {
        if (Context.GetHttpContext() is not { } http || ApiSecurity.CurrentSession(http) is null)
        {
            Context.Abort();
            return Task.CompletedTask;
        }
        return base.OnConnectedAsync();
    }
}
