using Microsoft.AspNetCore.SignalR;
using TrueCentralVms.Server.Auth;

namespace TrueCentralVms.Server.Hubs;

/// <summary>
/// Envíos del hub según el alcance por ubicación de cada usuario: quien no
/// puede ver un recurso no recibe sus eventos ni su estado. Mientras ningún
/// usuario tenga la vista filtrada (lo normal), todo sale a todos igual que
/// siempre. Cada conexión está en el grupo de su usuario (<see cref="VmsHub.UserGroup"/>).
/// </summary>
public sealed class ScopedHub(IHubContext<VmsHub> hub, UserScopeService scopes, ILogger<ScopedHub> logger)
{
    /// <summary>A quienes pueden ver el recurso del mensaje.</summary>
    public async Task SendAsync(string method, object? payload, Func<UserScope, bool> canView, CancellationToken ct = default)
    {
        if (await SnapshotAsync(ct) is not { } snapshot) return;
        if (!snapshot.AnyFiltering)
        {
            await hub.Clients.All.SendAsync(method, payload, ct);
            return;
        }
        var groups = snapshot.Users.Values.Where(canView).Select(s => VmsHub.UserGroup(s.UserId)).ToList();
        if (groups.Count > 0) await hub.Clients.Groups(groups).SendAsync(method, payload, ct);
    }

    /// <summary>
    /// Cada usuario recibe su versión: la completa si no filtra lo que ve, o
    /// la recortada a su alcance (p. ej. un panel con solo sus áreas); null =
    /// ese usuario no recibe nada.
    /// </summary>
    public async Task SendTrimmedAsync<T>(string method, T payload, Func<UserScope, T, T?> trim, CancellationToken ct = default)
        where T : class
    {
        if (await SnapshotAsync(ct) is not { } snapshot) return;
        if (!snapshot.AnyFiltering)
        {
            await hub.Clients.All.SendAsync(method, payload, ct);
            return;
        }
        var full = new List<string>();
        foreach (var scope in snapshot.Users.Values)
        {
            if (!scope.FiltersView)
            {
                full.Add(VmsHub.UserGroup(scope.UserId));
                continue;
            }
            if (trim(scope, payload) is { } mine)
                await hub.Clients.Group(VmsHub.UserGroup(scope.UserId)).SendAsync(method, mine, ct);
        }
        if (full.Count > 0) await hub.Clients.Groups(full).SendAsync(method, payload, ct);
    }

    /// <summary>
    /// A quienes cumplen el predicado, SIEMPRE evaluado (aunque nadie tenga la
    /// vista filtrada): para mensajes con destinatarios propios, como las
    /// alertas dirigidas a ciertos usuarios.
    /// </summary>
    public async Task SendToAsync(string method, object? payload, Func<UserScope, bool> recipient, CancellationToken ct = default)
    {
        if (await SnapshotAsync(ct) is not { } snapshot) return;
        var groups = snapshot.Users.Values.Where(recipient).Select(s => VmsHub.UserGroup(s.UserId)).ToList();
        if (groups.Count > 0) await hub.Clients.Groups(groups).SendAsync(method, payload, ct);
    }

    /// <summary>Solo a los administradores (lo que su API solo les entrega a ellos).</summary>
    public async Task ToAdminsAsync(string method, object? payload, CancellationToken ct = default)
    {
        if (await SnapshotAsync(ct) is not { } snapshot) return;
        var groups = snapshot.Users.Values.Where(s => s.IsAdmin).Select(s => VmsHub.UserGroup(s.UserId)).ToList();
        if (groups.Count > 0) await hub.Clients.Groups(groups).SendAsync(method, payload, ct);
    }

    /// <summary>A las conexiones de un usuario (p. ej. "su alcance cambió: recargue").</summary>
    public Task ToUserAsync(int userId, string method, object? payload, CancellationToken ct = default) =>
        hub.Clients.Group(VmsHub.UserGroup(userId)).SendAsync(method, payload, ct);

    private async Task<ScopeSnapshot?> SnapshotAsync(CancellationToken ct)
    {
        try
        {
            return await scopes.SnapshotAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sin saber quién puede ver qué, no se envía: antes perder un aviso en
            // tiempo real (queda en su historial) que mostrárselo a quien no debe.
            logger.LogWarning(ex, "Sin alcances por ubicación: no se envió un mensaje del hub.");
            return null;
        }
    }
}
