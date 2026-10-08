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

/// <summary>
/// Mantenedor de usuarios (permiso "Usuarios"). Quien no es administrador no
/// otorga más de lo que tiene: solo asigna roles cuyos permisos son todos
/// suyos, solo dentro de su propio alcance, y no toca a usuarios con más
/// permisos o más alcance que él (ni a sí mismo).
/// </summary>
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

    private static UserDto ToDto(User u, IReadOnlyDictionary<int, Role> roles, bool editable) => new(
        u.Id, u.Username, u.Role, u.Enabled, u.CreatedAt, u.PasswordChangedAt,
        u.RestrictToLocations, u.ViewOutsideScope, u.Locations.Select(l => l.LocationId).Order().ToList(),
        u.Roles.Select(r => r.RoleId).Order().ToList(),
        u.Roles.Select(r => roles.GetValueOrDefault(r.RoleId)?.Name).OfType<string>().Order().ToList(),
        editable);

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    private static Task<Dictionary<int, Role>> RolesAsync(VmsDbContext db) =>
        db.Roles.Include(r => r.Permissions).AsNoTracking().ToDictionaryAsync(r => r.Id);

    /// <summary>
    /// ¿Puede <paramref name="caller"/> modificar o eliminar a este usuario?
    /// null = sí; si no, el motivo. Un administrador puede con todos; el resto,
    /// solo con quienes no tienen roles que él no podría asignar ni más alcance.
    /// </summary>
    private static string? CannotManage(UserScope caller, User target, IReadOnlyDictionary<int, Role> roles, ScopeIndex index)
    {
        if (caller.IsAdmin) return null;
        if (target.Id == caller.UserId)
            return "No puede modificar su propio usuario: pídaselo a otro administrador de usuarios.";
        if (target.Roles.Any(r => roles.TryGetValue(r.RoleId, out var role) && !RolesApi.CanGrant(caller, role)))
            return $"'{target.Username}' tiene permisos que usted no tiene: no puede modificarlo.";
        if (!caller.Unrestricted && !WithinCallerScope(caller, target.RestrictToLocations, target.ViewOutsideScope,
                target.Locations.Select(l => l.LocationId), index))
            return $"'{target.Username}' tiene un alcance mayor que el suyo: no puede modificarlo.";
        return null;
    }

    /// <summary>Un alcance cabe en el de quien lo asigna (restringido, a sus ubicaciones y sin "ver el resto" si él no lo tiene).</summary>
    private static bool WithinCallerScope(UserScope caller, bool restrict, bool viewOutside, IEnumerable<int> locationIds, ScopeIndex index) =>
        caller.Unrestricted ||
        (restrict && (!viewOutside || caller.ViewOutside) && index.Subtree(locationIds).All(caller.Locations.Contains));

    /// <summary>
    /// Roles pedidos: RoleIds; si no viene, según el Role de un cliente anterior
    /// ("Admin"/"Operator"); en edición sin ninguno de los dos, los actuales.
    /// </summary>
    private static (List<Role>? Roles, IResult? Error) ResolveRoles(UserWriteDto request, User? current,
        IReadOnlyDictionary<int, Role> roles, UserScope caller)
    {
        IEnumerable<int> ids;
        if (request.RoleIds is not null) ids = request.RoleIds;
        else if (!string.IsNullOrEmpty(request.Role))
        {
            if (!Roles.IsValid(request.Role)) return (null, Error("Rol inválido."));
            string key = request.Role == Roles.Admin ? Role.AdminKey : Role.OperatorKey;
            ids = roles.Values.Where(r => r.SystemKey == key).Select(r => r.Id);
        }
        else ids = current?.Roles.Select(r => r.RoleId) ?? [];

        var chosen = new List<Role>();
        foreach (int id in ids.Distinct())
        {
            if (!roles.TryGetValue(id, out var role))
                return (null, Error("Alguno de los roles elegidos ya no existe: recargue la página.", StatusCodes.Status404NotFound));
            chosen.Add(role);
        }
        if (chosen.Count == 0)
            return (null, Error("Asigne al menos un rol."));
        // Lo que ya tenía y se conserva no se revalida (otro administrador pudo
        // dárselo); lo nuevo, sí: nadie asigna un rol con permisos que no tiene.
        var kept = current?.Roles.Select(r => r.RoleId).ToHashSet() ?? [];
        if (chosen.Where(r => !kept.Contains(r.Id)).FirstOrDefault(r => !RolesApi.CanGrant(caller, r)) is { } denied)
            return (null, Error(denied.IsAdmin
                ? "Solo un administrador puede asignar el rol Administrador."
                : $"No puede asignar el rol '{denied.Name}': tiene permisos que usted no tiene.", StatusCodes.Status403Forbidden));
        return (chosen, null);
    }

    /// <summary>El alcance pedido, normalizado (null en un campo = conservar el actual).</summary>
    private sealed record ScopeRequest(bool Restrict, bool ViewOutside, List<int> LocationIds);

    private static async Task<(ScopeRequest? Scope, IResult? Error)> ResolveScopeAsync(
        VmsDbContext db, UserWriteDto request, User? current, bool admin, UserScope caller, ScopeIndex index)
    {
        // Un administrador siempre ve y opera todo: no se le guarda alcance.
        if (admin) return (new ScopeRequest(false, false, []), null);

        bool restrict = request.RestrictToLocations ?? current?.RestrictToLocations ?? false;
        bool viewOutside = restrict && (request.ViewOutsideScope ?? current?.ViewOutsideScope ?? false);
        var ids = restrict
            ? (request.LocationIds ?? current?.Locations.Select(l => l.LocationId).ToList() ?? []).Distinct().ToList()
            : [];
        if (!WithinCallerScope(caller, restrict, viewOutside, ids, index))
            return (null, Error(caller.ViewOutside || !viewOutside
                ? "Solo puede asignar ubicaciones dentro de su propio alcance."
                : "No puede dar \"ver el resto\": usted no lo tiene.", StatusCodes.Status403Forbidden));
        if (!restrict) return (new ScopeRequest(false, false, []), null);

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

    private static string RolesText(IEnumerable<int> ids, IReadOnlyDictionary<int, Role> roles) =>
        string.Join(", ", ids.Select(id => roles.GetValueOrDefault(id)?.Name ?? $"#{id}").Order());

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

    /// <summary>Asigna los roles y deriva el nivel (Admin si tiene el rol Administrador).</summary>
    private static void ApplyRoles(User user, List<Role> chosen)
    {
        var ids = chosen.Select(r => r.Id).ToHashSet();
        user.Roles.RemoveAll(r => !ids.Contains(r.RoleId));
        foreach (int id in ids.Where(id => user.Roles.All(r => r.RoleId != id)))
            user.Roles.Add(new UserRole { RoleId = id });
        user.Role = chosen.Any(r => r.IsAdmin) ? Roles.Admin : Roles.Operator;
    }

    /// <summary>¿Queda otro administrador habilitado además de <paramref name="exceptId"/>?</summary>
    private static Task<bool> OtherAdminExistsAsync(VmsDbContext db, int exceptId) =>
        db.Users.AnyAsync(u => u.Id != exceptId && u.Enabled &&
                               u.Roles.Any(ur => db.Roles.Any(r => r.Id == ur.RoleId && r.SystemKey == Role.AdminKey)));

    public static void MapUsersApi(this WebApplication app)
    {
        app.MapGet("/api/users", async (HttpContext ctx, VmsDbContext db, UserScopeService scopes) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.UsersManage, out _) is { } failure) return failure;
            var caller = (UserScope)ctx.Items["scope"]!;
            var roles = await RolesAsync(db);
            var index = (await scopes.SnapshotAsync(ctx.RequestAborted)).Index;
            var users = await db.Users.Include(u => u.Locations).Include(u => u.Roles).OrderBy(u => u.Username).ToListAsync();
            return Results.Ok(users.Select(u => ToDto(u, roles, CannotManage(caller, u, roles, index) is null)));
        });

        app.MapPost("/api/users", async (HttpContext ctx, UserWriteDto request, VmsDbContext db, LicenseService license,
            PasswordGovernance passwords, IHubContext<VmsHub> hub, UserScopeService scopes, AuditService audit) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.UsersManage, out _) is { } failure) return failure;
            var caller = (UserScope)ctx.Items["scope"]!;

            string username = request.Username?.Trim() ?? "";
            if (username.Length is < 3 or > 64)
                return Error("El nombre de usuario debe tener entre 3 y 64 caracteres.");
            var roles = await RolesAsync(db);
            var (chosen, rolesError) = ResolveRoles(request, null, roles, caller);
            if (rolesError is not null) return rolesError;
            if (await db.Users.AnyAsync(u => u.Username == username))
                return Error("Ya existe un usuario con ese nombre.", StatusCodes.Status409Conflict);
            if (string.IsNullOrEmpty(request.Password))
                return Error("La contraseña es obligatoria.");
            var index = (await scopes.SnapshotAsync()).Index;
            bool admin = chosen!.Any(r => r.IsAdmin);
            var (scope, scopeError) = await ResolveScopeAsync(db, request, null, admin, caller, index);
            if (scopeError is not null) return scopeError;
            if (request.Enabled && license.Deny(null, LicenseFeatures.MaxUsers, await db.Users.CountAsync(u => u.Enabled)) is { } denied)
                return await license.DenyAsync(ctx, denied, "user", username);
            if (await passwords.ValidateNewPasswordAsync(db, null, request.Password) is { } error)
                return Error(error);

            var user = new User { Username = username, Enabled = request.Enabled };
            ApplyRoles(user, chosen!);
            ApplyScope(user, scope!);
            passwords.SetPassword(user, request.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            scopes.Invalidate();

            await audit.LogAsync(ctx, "users", "user-created",
                targetType: "user", targetId: user.Id.ToString(), targetName: user.Username,
                detail: $"Creó el usuario '{user.Username}' (roles: {RolesText(chosen!.Select(r => r.Id), roles)}, " +
                        $"{(user.Enabled ? "habilitado" : "deshabilitado")}" +
                        (admin ? "" : $", alcance: {ScopeText(scope!.Restrict, scope.ViewOutside, scope.LocationIds, index)}") + ").",
                data: new { roleIds = chosen!.Select(r => r.Id).Order().ToList(), user.RestrictToLocations, user.ViewOutsideScope,
                            locationIds = scope!.LocationIds });
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "users");
            return Results.Ok(ToDto(user, roles, true));
        });

        app.MapPut("/api/users/{id:int}", async (HttpContext ctx, int id, UserWriteDto request, VmsDbContext db, LicenseService license,
            PasswordGovernance passwords, TokenService tokens, IHubContext<VmsHub> hub, ScopedHub scopedHub,
            UserScopeService scopes, MediaMtxManager mtx, AuditService audit) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.UsersManage, out var session) is { } failure) return failure;
            var caller = (UserScope)ctx.Items["scope"]!;

            var user = await db.Users.Include(u => u.Locations).Include(u => u.Roles).FirstOrDefaultAsync(u => u.Id == id);
            if (user is null) return Results.NotFound();
            var roles = await RolesAsync(db);
            var index = (await scopes.SnapshotAsync()).Index;
            if (CannotManage(caller, user, roles, index) is { } reason)
                return Error(reason, StatusCodes.Status403Forbidden);

            var (chosen, rolesError) = ResolveRoles(request, user, roles, caller);
            if (rolesError is not null) return rolesError;
            bool admin = chosen!.Any(r => r.IsAdmin);

            string username = request.Username?.Trim() ?? "";
            if (username.Length is < 3 or > 64)
                return Error("El nombre de usuario debe tener entre 3 y 64 caracteres.");
            if (await db.Users.AnyAsync(u => u.Id != id && u.Username == username))
                return Error("Ya existe un usuario con ese nombre.", StatusCodes.Status409Conflict);

            // Siempre debe quedar al menos un administrador habilitado.
            bool losesAdmin = user.Role == Roles.Admin && user.Enabled && (!admin || !request.Enabled);
            if (losesAdmin && !await OtherAdminExistsAsync(db, id))
                return Error("Debe existir al menos un administrador habilitado.");

            var (scope, scopeError) = await ResolveScopeAsync(db, request, user, admin, caller, index);
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
            if (user.Enabled != request.Enabled) changes.Add(request.Enabled ? "habilitado" : "deshabilitado");
            if (!string.IsNullOrEmpty(request.Password)) changes.Add("contraseña restablecida");

            var rolesBefore = user.Roles.Select(r => r.RoleId).Order().ToList();
            var rolesAfter = chosen!.Select(r => r.Id).Order().ToList();
            bool rolesChanged = !rolesBefore.SequenceEqual(rolesAfter);
            string levelBefore = user.Role;

            bool scopeChanged = !SameScope(user, scope!);
            string scopeBefore = ScopeText(user.RestrictToLocations, user.ViewOutsideScope, user.Locations.Select(l => l.LocationId), index);
            var dataBefore = new { user.RestrictToLocations, user.ViewOutsideScope, locationIds = user.Locations.Select(l => l.LocationId).Order().ToList() };

            user.Username = username;
            if (!user.Enabled && request.Enabled
                && license.Deny(null, LicenseFeatures.MaxUsers, await db.Users.CountAsync(u => u.Enabled && u.Id != id)) is { } denied)
                return await license.DenyAsync(ctx, denied, "user", user.Username);
            user.Enabled = request.Enabled;
            ApplyRoles(user, chosen!);
            ApplyScope(user, scope!);
            await db.SaveChangesAsync();
            scopes.Invalidate();
            // Se revoca con el cambio YA guardado: el cliente cortado intenta
            // renovar la sesión en el acto y, antes del guardado, todavía
            // entraría (habilitado, con la clave anterior o con el nivel viejo).
            // El nivel (administrador o no) viaja en la sesión: un administrador
            // degradado no sigue siéndolo hasta que venza. Los demás permisos se
            // leen en cada solicitud y valen de inmediato.
            bool levelChanged = levelBefore != user.Role;
            if (!user.Enabled || levelChanged || !string.IsNullOrEmpty(request.Password))
                tokens.RevokeUser(id);

            if (rolesChanged)
            {
                await audit.LogAsync(ctx, "users", "user-roles-updated",
                    targetType: "user", targetId: user.Id.ToString(), targetName: user.Username,
                    detail: $"Cambió los roles de '{user.Username}': {RolesText(rolesBefore, roles)} → {RolesText(rolesAfter, roles)}.",
                    data: new { before = rolesBefore, after = rolesAfter });
                // Su puesto rehace menús y botones con los permisos nuevos.
                await scopedHub.ToUserAsync(id, VmsHubContract.ConfigChanged, "permissions");
            }

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
            if (changes.Count > 0 || (!scopeChanged && !rolesChanged))
                await audit.LogAsync(ctx, "users", "user-updated",
                    targetType: "user", targetId: user.Id.ToString(), targetName: user.Username,
                    detail: $"Modificó el usuario '{user.Username}': " +
                            (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios."));
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "users");
            return Results.Ok(ToDto(user, roles, true));
        });

        app.MapDelete("/api/users/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            TokenService tokens, IHubContext<VmsHub> hub, UserScopeService scopes, AuditService audit) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.UsersManage, out var session) is { } failure) return failure;
            var caller = (UserScope)ctx.Items["scope"]!;

            var user = await db.Users.Include(u => u.Locations).Include(u => u.Roles).FirstOrDefaultAsync(u => u.Id == id);
            if (user is null) return Results.NotFound();
            if (session.UserId == id)
                return Error("No puede eliminar su propio usuario.");
            var roles = await RolesAsync(db);
            if (CannotManage(caller, user, roles, (await scopes.SnapshotAsync()).Index) is { } reason)
                return Error(reason, StatusCodes.Status403Forbidden);
            if (user.Role == Roles.Admin && user.Enabled && !await OtherAdminExistsAsync(db, id))
                return Error("Debe existir al menos un administrador habilitado.");

            string rolesText = RolesText(user.Roles.Select(r => r.RoleId), roles);
            db.Users.Remove(user);
            await db.SaveChangesAsync();
            tokens.RevokeUser(id);
            scopes.Invalidate();

            await audit.LogAsync(ctx, "users", "user-deleted",
                targetType: "user", targetId: id.ToString(), targetName: user.Username,
                detail: $"Eliminó el usuario '{user.Username}' (roles: {rolesText}).");
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "users");
            return Results.Ok();
        });
    }
}
