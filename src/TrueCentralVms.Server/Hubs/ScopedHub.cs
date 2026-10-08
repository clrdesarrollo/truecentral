using Microsoft.AspNetCore.SignalR;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
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
    /// <summary>
    /// Mensajes que solo reciben quienes tienen el permiso de verlos (además de
    /// su alcance): los eventos que llenan el historial de un módulo. Los de
    /// estado (un panel cambió, una puerta se abrió) siguen saliendo según el
    /// alcance, porque los usan varias pantallas.
    /// </summary>
    private static readonly Dictionary<string, string> MethodPermission = new()
    {
        [VmsHubContract.AlarmEventReceived] = Permissions.AlarmsMonitor,
        [VmsHubContract.AccessEventReceived] = Permissions.AccessMonitor,
        [VmsHubContract.PlateRecognized] = Permissions.AnprView,
        [VmsHubContract.CercoEventReceived] = Permissions.CercoMonitor,
        [VmsHubContract.IntercomCallChanged] = Permissions.IntercomAnswer,
        [VmsHubContract.AccessPersonSyncChanged] = Permissions.PersonsView,
    };

    /// <summary>El permiso que exige el mensaje y si algún usuario no lo tiene (si todos lo tienen, no hay que filtrar por él).</summary>
    private static (string? Permission, bool Filters) PermissionOf(string method, ScopeSnapshot snapshot) =>
        MethodPermission.TryGetValue(method, out var permission)
            ? (permission, snapshot.Users.Values.Any(u => !u.Has(permission)))
            : (null, false);

    /// <summary>A quienes pueden ver el recurso del mensaje.</summary>
    public async Task SendAsync(string method, object? payload, Func<UserScope, bool> canView, CancellationToken ct = default)
    {
        if (await SnapshotAsync(ct) is not { } snapshot) return;
        var (permission, byPermission) = PermissionOf(method, snapshot);
        if (!snapshot.AnyFiltering && !byPermission)
        {
            await hub.Clients.All.SendAsync(method, payload, ct);
            return;
        }
        var groups = snapshot.Users.Values.Where(s => (permission is null || s.Has(permission)) && canView(s))
            .Select(s => VmsHub.UserGroup(s.UserId)).ToList();
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
        var (permission, byPermission) = PermissionOf(method, snapshot);
        if (!snapshot.AnyFiltering && !byPermission)
        {
            await hub.Clients.All.SendAsync(method, payload, ct);
            return;
        }
        var full = new List<string>();
        foreach (var scope in snapshot.Users.Values)
        {
            if (permission is not null && !scope.Has(permission)) continue;
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

    /// <summary>Solo a quienes tienen el permiso (lo que su API solo les entrega a ellos).</summary>
    public async Task ToPermittedAsync(string permission, string method, object? payload, CancellationToken ct = default)
    {
        if (await SnapshotAsync(ct) is not { } snapshot) return;
        var groups = snapshot.Users.Values.Where(s => s.Has(permission)).Select(s => VmsHub.UserGroup(s.UserId)).ToList();
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
