using Microsoft.AspNetCore.SignalR;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Hubs;

/// <summary>
/// Canal en tiempo real con las pantallas de alarma de las casetas. Es aparte
/// del hub de los operadores: otra credencial (la de la pantalla, no una
/// sesión de usuario) y otros mensajes. El middleware ya identificó la
/// pantalla y rechazó con 401 una credencial inválida antes de abrir la
/// conexión; aquí se vuelve a exigir por si acaso.
/// </summary>
public sealed class AlarmScreenHub(AlarmScreenService screens) : Hub
{
    private const string ScreenItem = "screen";

    public override async Task OnConnectedAsync()
    {
        if (Context.GetHttpContext() is not { } http || AlarmScreenService.CurrentScreen(http) is not { } screen)
        {
            Context.Abort();
            return;
        }
        Context.Items[ScreenItem] = screen;
        await Groups.AddToGroupAsync(Context.ConnectionId, AlarmScreenService.Group(screen.Id));
        await screens.ConnectedAsync(Context, screen, RemoteIp());
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await screens.DisconnectedAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    private int ScreenId => (Context.Items[ScreenItem] as ScreenIdentity)?.Id
                            ?? throw new HubException("Pantalla no identificada.");

    /// <summary>La pantalla recibió el aviso: es la mitad de la constancia de tiempos.</summary>
    public Task ConfirmDelivery(long alertId) => screens.ConfirmDeliveriesAsync(ScreenId, [alertId]);

    /// <summary>Confirmación en lote de lo recibido al resincronizar después de una caída.</summary>
    public Task ConfirmDeliveries(long[] alertIds) => screens.ConfirmDeliveriesAsync(ScreenId, alertIds);

    /// <summary>El guardia lo marcó como visto: se cierra en todas las pantallas y en TrueCentral.</summary>
    public Task Acknowledge(long alertId) => screens.AcknowledgeFromScreenAsync(alertId, ScreenId, RemoteIp());

    /// <summary>«Todas vistas».</summary>
    public Task AcknowledgeAll() => screens.AcknowledgeAllFromScreenAsync(ScreenId, RemoteIp());

    /// <summary>Señal de vida. Sin esto, una Pi colgada parece una caseta tranquila.</summary>
    public Task Heartbeat() => screens.TouchAsync(ScreenId, RemoteIp());

    private string? RemoteIp() => Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString();
}
