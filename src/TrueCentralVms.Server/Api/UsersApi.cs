using TrueCentralVms.Server.Services.Licensing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

/// <summary>Mantenedor de usuarios (solo administradores).</summary>
public static class UsersApi
{
    /// <summary>Máximo de ubicaciones en el alcance de un usuario (cada una ya incluye sus sububicaciones).</summary>
    private const int MaxScopeLocations = 200;

    /// <summary>
    /// Tópicos que el puesto de un usuario recarga cuando cambia su alcance:
    /// los que ya escuchan sus módulos ("devices" rehace el árbol de cámaras,
    /// las fuentes de patentes y el muro; "locations", la página Recursos),
    /// más "scope" (cerco, alertas y el alcance que muestra el menú).
    /// </summary>
    private static readonly string[] ScopeTopics =
        ["devices", "locations", "alarm-panels", "access-devices", "speakers", "intercoms", "scope"];

    private static UserDto ToDto(User u) => new(u.Id, u.Username, u.Role, u.Enabled, u.CreatedAt, u.PasswordChangedAt,
        u.RestrictToLocations, u.ViewOutsideScope, u.Locations.Select(l => l.LocationId).Order().ToList());

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    /// <summary>El alcance pedido, normalizado (null en un campo = conservar el actual).</summary>
    private sealed record ScopeRequest(bool Restrict, bool ViewOutside, List<int> LocationIds);

    private static async Task<(ScopeRequest? Scope, IResult? Error)> ResolveScopeAsync(
        VmsDbContext db, UserWriteDto request, User? current)
    {
        // Un administrador siempre ve y opera todo: no se le guarda alcance.
        if (request.Role == Roles.Admin) return (new ScopeRequest(false, false, []), null);

        bool restrict = request.RestrictToLocations ?? current?.RestrictToLocations ?? false;
        if (!restrict) return (new ScopeRequest(false, false, []), null);

        bool viewOutside = request.ViewOutsideScope ?? current?.ViewOutsideScope ?? false;
        var ids = (request.LocationIds ?? current?.Locations.Select(l => l.LocationId).ToList() ?? [])
            .Distinct().ToList();
        if (ids.Count > MaxScopeLocations)
            return (null, Error($"Elija a lo más {MaxScopeLocations} ubicaciones (cada una ya incluye sus sububicaciones)."));
        if (ids.Count == 0 && !viewOutside)
            return (null, Error("Elija al menos una ubicación, o marque que puede ver el resto (si solo debe supervisar)."));
        int existing = await db.Locations.CountAsync(l => ids.Contains(l.Id));
        if (existing != ids.Count)
            return (null, Error("Alguna de las ubicaciones elegidas ya no existe: recargue la página.", StatusCodes.Status404NotFound));
        return (new ScopeRequest(true, viewOutside, ids), null);
    }

    /// <summary>"todas las ubicaciones" o "solo Casa matriz › Edificio A, Portería (ve el resto sin operarlo)".</summary>
    private static string ScopeText(bool restrict, bool viewOutside, IEnumerable<int> ids, ScopeIndex index)
    {
        if (!restrict) return "todas las ubicaciones";
        var names = ids.Select(index.PathOf).Where(n => n.Length > 0).Order().ToList();
        string text = names.Count == 0 ? "ninguna ubicación" : "solo " + string.Join(", ", names);
        return viewOutside ? text + " (ve el resto sin operarlo)" : text;
    }

    private static bool SameScope(User user, ScopeRequest scope) =>
        user.RestrictToLocations == scope.Restrict && user.ViewOutsideScope == scope.ViewOutside &&
        user.Locations.Select(l => l.LocationId).Order().SequenceEqual(scope.LocationIds.Order());

    private static void ApplyScope(User user, ScopeRequest scope)
    {
        user.RestrictToLocations = scope.Restrict;
        user.ViewOutsideScope = scope.ViewOutside;
        user.Locations.RemoveAll(l => !scope.LocationIds.Contains(l.LocationId));
        foreach (int id in scope.LocationIds.Where(id => user.Locations.All(l => l.LocationId != id)))
            user.Locations.Add(new UserLocation { LocationId = id });
    }

    public static void MapUsersApi(this WebApplication app)
    {
        app.MapGet("/api/users", async (HttpContext ctx, VmsDbContext db) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var users = await db.Users.Include(u => u.Locations).OrderBy(u => u.Username).ToListAsync();
            return Results.Ok(users.Select(ToDto));
        });

        app.MapPost("/api/users", async (HttpContext ctx, UserWriteDto request, VmsDbContext db, LicenseService license,
            PasswordGovernance passwords, IHubContext<VmsHub> hub, UserScopeService scopes, AuditService audit) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;

            string username = request.Username?.Trim() ?? "";
            if (username.Length is < 3 or > 64)
                return Error("El nombre de usuario debe tener entre 3 y 64 caracteres.");
            if (!Roles.IsValid(request.Role))
                return Error("Rol inválido.");
            if (await db.Users.AnyAsync(u => u.Username == username))
                return Error("Ya existe un usuario con ese nombre.", StatusCodes.Status409Conflict);
            if (string.IsNullOrEmpty(request.Password))
                return Error("La contraseña es obligatoria.");
            var (scope, scopeError) = await ResolveScopeAsync(db, request, null);
            if (scopeError is not null) return scopeError;
            if (request.Enabled && license.Deny(null, LicenseFeatures.MaxUsers, await db.Users.CountAsync(u => u.Enabled)) is { } denied)
                return await license.DenyAsync(ctx, denied, "user", username);
            if (await passwords.ValidateNewPasswordAsync(db, null, request.Password) is { } error)
                return Error(error);

            var user = new User { Username = username, Role = request.Role, Enabled = request.Enabled };
            ApplyScope(user, scope!);
            passwords.SetPassword(user, request.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            scopes.Invalidate();

            var index = (await scopes.SnapshotAsync()).Index;
            await audit.LogAsync(ctx, "users", "user-created",
                targetType: "user", targetId: user.Id.ToString(), targetName: user.Username,
                detail: $"Creó el usuario '{user.Username}' (rol {user.Role}, {(user.Enabled ? "habilitado" : "deshabilitado")}" +
                        (user.Role == Roles.Admin ? "" : $", alcance: {ScopeText(scope!.Restrict, scope.ViewOutside, scope.LocationIds, index)}") + ").",
                data: new { user.RestrictToLocations, user.ViewOutsideScope, locationIds = scope!.LocationIds });
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "users");
            return Results.Ok(ToDto(user));
        });

        app.MapPut("/api/users/{id:int}", async (HttpContext ctx, int id, UserWriteDto request, VmsDbContext db, LicenseService license,
            PasswordGovernance passwords, TokenService tokens, IHubContext<VmsHub> hub, ScopedHub scopedHub,
            UserScopeService scopes, MediaMtxManager mtx, AuditService audit) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out var session) is { } failure) return failure;

            var user = await db.Users.Include(u => u.Locations).FirstOrDefaultAsync(u => u.Id == id);
            if (user is null) return Results.NotFound();

            if (!Roles.IsValid(request.Role))
                return Error("Rol inválido.");

            string username = request.Username?.Trim() ?? "";
            if (username.Length is < 3 or > 64)
                return Error("El nombre de usuario debe tener entre 3 y 64 caracteres.");
            if (await db.Users.AnyAsync(u => u.Id != id && u.Username == username))
                return Error("Ya existe un usuario con ese nombre.", StatusCodes.Status409Conflict);

            // Siempre debe quedar al menos un administrador habilitado.
            bool losesAdmin = user.Role == Roles.Admin && user.Enabled && (request.Role != Roles.Admin || !request.Enabled);
            if (losesAdmin && !await db.Users.AnyAsync(u => u.Id != id && u.Role == Roles.Admin && u.Enabled))
                return Error("Debe existir al menos un administrador habilitado.");

            var (scope, scopeError) = await ResolveScopeAsync(db, request, user);
            if (scopeError is not null) return scopeError;

            if (!string.IsNullOrEmpty(request.Password))
            {
                if (await passwords.ValidateNewPasswordAsync(db, id, request.Password) is { } error)
                    return Error(error);
                passwords.SetPassword(user, request.Password);
            }

            // Cambios efectivos, para que el detalle auditado diga QUÉ cambió.
            var changes = new List<string>();
            if (user.Username != username) changes.Add($"nombre '{user.Username}' → '{username}'");
            bool roleChanged = user.Role != request.Role;
            if (roleChanged) changes.Add($"rol {user.Role} → {request.Role}");
            if (user.Enabled != request.Enabled) changes.Add(request.Enabled ? "habilitado" : "deshabilitado");
            if (!string.IsNullOrEmpty(request.Password)) changes.Add("contraseña restablecida");

            var index = (await scopes.SnapshotAsync()).Index;
            bool scopeChanged = !SameScope(user, scope!);
            string scopeBefore = ScopeText(user.RestrictToLocations, user.ViewOutsideScope, user.Locations.Select(l => l.LocationId), index);
            var dataBefore = new { user.RestrictToLocations, user.ViewOutsideScope, locationIds = user.Locations.Select(l => l.LocationId).Order().ToList() };

            user.Username = username;
            user.Role = request.Role;
            if (!user.Enabled && request.Enabled
                && license.Deny(null, LicenseFeatures.MaxUsers, await db.Users.CountAsync(u => u.Enabled && u.Id != id)) is { } denied)
                return await license.DenyAsync(ctx, denied, "user", user.Username);
            user.Enabled = request.Enabled;
            ApplyScope(user, scope!);
            await db.SaveChangesAsync();
            scopes.Invalidate();
            // Se revoca con el cambio YA guardado: el cliente cortado intenta
            // renovar la sesión en el acto y, antes del guardado, todavía
            // entraría (habilitado, con la clave anterior o con el ROL viejo).
            // El rol viaja en la sesión: un administrador degradado no sigue
            // siéndolo hasta que venza.
            if (!user.Enabled || roleChanged || !string.IsNullOrEmpty(request.Password))
                tokens.RevokeUser(id);

            if (scopeChanged)
            {
                // Lo que quedó fuera deja de verse ya: video abierto incluido.
                var newScope = await scopes.ForUserAsync(id);
                int kicked = await SessionAccounting.KickOutOfScopeAsync(db, mtx, scopedHub, newScope);
                string scopeAfter = ScopeText(user.RestrictToLocations, user.ViewOutsideScope, scope!.LocationIds, index);
                await audit.LogAsync(ctx, "users", "user-scope-updated",
                    targetType: "user", targetId: user.Id.ToString(), targetName: user.Username,
                    detail: $"Cambió el alcance de '{user.Username}': {scopeBefore} → {scopeAfter}." +
                            (kicked > 0 ? $" Se cortaron {kicked} sesión(es) de video que quedaron fuera." : ""),
                    data: new
                    {
                        before = dataBefore,
                        after = new { user.RestrictToLocations, user.ViewOutsideScope, locationIds = scope.LocationIds.Order().ToList() },
                        kickedSessions = kicked,
                    });
                // Sus puestos recargan árboles y listas con el alcance nuevo.
                foreach (string topic in ScopeTopics)
                    await scopedHub.ToUserAsync(id, VmsHubContract.ConfigChanged, topic);
            }
            if (changes.Count > 0 || !scopeChanged)
                await audit.LogAsync(ctx, "users", "user-updated",
                    targetType: "user", targetId: user.Id.ToString(), targetName: user.Username,
                    detail: $"Modificó el usuario '{user.Username}': " +
                            (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios."));
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "users");
            return Results.Ok(ToDto(user));
        });

        app.MapDelete("/api/users/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            TokenService tokens, IHubContext<VmsHub> hub, UserScopeService scopes, AuditService audit) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out var session) is { } failure) return failure;

            var user = await db.Users.FindAsync(id);
            if (user is null) return Results.NotFound();
            if (session.UserId == id)
                return Error("No puede eliminar su propio usuario.");
            if (user.Role == Roles.Admin && user.Enabled &&
                !await db.Users.AnyAsync(u => u.Id != id && u.Role == Roles.Admin && u.Enabled))
                return Error("Debe existir al menos un administrador habilitado.");

            db.Users.Remove(user);
            await db.SaveChangesAsync();
            tokens.RevokeUser(id);
            scopes.Invalidate();

            await audit.LogAsync(ctx, "users", "user-deleted",
                targetType: "user", targetId: id.ToString(), targetName: user.Username,
                detail: $"Eliminó el usuario '{user.Username}' (rol {user.Role}).");
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "users");
            return Results.Ok();
        });
    }
}
