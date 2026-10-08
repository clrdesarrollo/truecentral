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
/// Mantenedor de roles. Regla contra el escalamiento: nadie otorga lo que no
/// tiene. Quien no es administrador solo crea, modifica, borra o asigna roles
/// cuyos permisos son todos suyos; el rol Administrador no se edita ni se borra
/// y el Operador no se borra.
/// </summary>
public static class RolesApi
{
    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    /// <summary>Los permisos de <paramref name="permissions"/> que <paramref name="caller"/> no tiene (ninguno si es administrador).</summary>
    public static List<string> Missing(UserScope caller, IEnumerable<string> permissions) =>
        caller.IsAdmin ? [] : permissions.Where(p => !caller.Has(p)).Distinct().ToList();

    /// <summary>¿Esta sesión puede modificar o asignar el rol? (no es el Administrador, salvo para un administrador, y no da más de lo suyo).</summary>
    public static bool CanGrant(UserScope caller, Role role) =>
        caller.IsAdmin || (!role.IsAdmin && Missing(caller, role.Permissions.Select(p => p.Permission)).Count == 0);

    private static string Labels(IEnumerable<string> keys) =>
        string.Join(", ", keys.Select(Permissions.LabelOf).Order());

    private static RoleDto ToDto(Role role, IReadOnlyList<string> users, UserScope caller) => new(
        role.Id, role.Name, role.Description, role.SystemKey,
        role.IsAdmin ? Permissions.All.Select(p => p.Key).ToList() : role.Permissions.Select(p => p.Permission).Order().ToList(),
        users.Count, users,
        Editable: !role.IsAdmin && CanGrant(caller, role),
        Assignable: CanGrant(caller, role),
        role.UpdatedAt);

    /// <summary>Usuarios de cada rol (nombre), para la lista y para avisar a sus puestos.</summary>
    private static async Task<Dictionary<int, List<(int Id, string Name)>>> UsersByRoleAsync(VmsDbContext db) =>
        (await db.UserRoles.Join(db.Users, ur => ur.UserId, u => u.Id, (ur, u) => new { ur.RoleId, u.Id, u.Username })
            .ToListAsync())
        .GroupBy(x => x.RoleId)
        .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Username).Select(x => (x.Id, x.Username)).ToList());

    /// <summary>Valida nombre y permisos; devuelve los permisos normalizados (con lo que suponen).</summary>
    private static async Task<(string Name, string Description, HashSet<string> Permissions, IResult? Error)> ValidateAsync(
        VmsDbContext db, RoleWriteDto request, int? id, UserScope caller)
    {
        string name = request.Name?.Trim() ?? "";
        string description = request.Description?.Trim() ?? "";
        if (name.Length is < 2 or > 64)
            return (name, description, [], Error("El nombre del rol debe tener entre 2 y 64 caracteres."));
        if (description.Length > 256)
            return (name, description, [], Error("La descripción admite hasta 256 caracteres."));
        if (await db.Roles.AnyAsync(r => r.Id != id && r.Name.ToLower() == name.ToLower()))
            return (name, description, [], Error("Ya existe un rol con ese nombre.", StatusCodes.Status409Conflict));
        var unknown = (request.Permissions ?? []).Where(p => !Permissions.IsValid(p)).ToList();
        if (unknown.Count > 0)
            return (name, description, [], Error($"Permisos desconocidos: {string.Join(", ", unknown)}. Recargue la página."));
        var permissions = Permissions.Normalize(request.Permissions);
        if (Missing(caller, permissions) is { Count: > 0 } missing)
            return (name, description, [], Error($"No puede otorgar permisos que usted no tiene: {Labels(missing)}.",
                StatusCodes.Status403Forbidden));
        return (name, description, permissions, null);
    }

    public static void MapRolesApi(this WebApplication app)
    {
        app.MapGet("/api/roles/catalog", (HttpContext ctx) =>
        {
            if (ApiSecurity.RequireAny(ctx, out _, Permissions.RolesManage, Permissions.UsersManage) is { } failure) return failure;
            var caller = (UserScope)ctx.Items["scope"]!;
            return Results.Ok(new PermissionCatalogDto(
                Permissions.Groups,
                Permissions.All.Select(p => new PermissionDto(p.Key, p.Group, p.Label, p.Description, p.Requires, p.Sensitive)).ToList(),
                Permissions.Templates.Select(t => new RoleTemplateDto(t.Key, t.Name, t.Description,
                    Permissions.Normalize(t.Permissions).Order().ToList())).ToList(),
                caller.EffectivePermissions.ToList()));
        });

        app.MapGet("/api/roles", async (HttpContext ctx, VmsDbContext db) =>
        {
            if (ApiSecurity.RequireAny(ctx, out _, Permissions.RolesManage, Permissions.UsersManage) is { } failure) return failure;
            var caller = (UserScope)ctx.Items["scope"]!;
            var roles = await db.Roles.Include(r => r.Permissions).AsNoTracking().ToListAsync();
            var users = await UsersByRoleAsync(db);
            // De sistema primero (Administrador, Operador), luego por nombre.
            return Results.Ok(roles
                .OrderBy(r => r.SystemKey switch { Role.AdminKey => 0, Role.OperatorKey => 1, _ => 2 })
                .ThenBy(r => r.Name)
                .Select(r => ToDto(r, users.GetValueOrDefault(r.Id)?.Select(u => u.Name).ToList() ?? [], caller)));
        });

        app.MapPost("/api/roles", async (HttpContext ctx, RoleWriteDto request, VmsDbContext db,
            IHubContext<VmsHub> hub, AuditService audit) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.RolesManage, out _) is { } failure) return failure;
            var caller = (UserScope)ctx.Items["scope"]!;
            var (name, description, permissions, error) = await ValidateAsync(db, request, null, caller);
            if (error is not null) return error;

            var role = new Role
            {
                Name = name, Description = description,
                Permissions = permissions.Select(p => new RolePermission { Permission = p }).ToList(),
            };
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            await audit.LogAsync(ctx, "roles", "role-created",
                targetType: "role", targetId: role.Id.ToString(), targetName: role.Name,
                detail: $"Creó el rol '{role.Name}' con {permissions.Count} permiso(s): " +
                        (permissions.Count == 0 ? "ninguno" : Labels(permissions)) + ".",
                data: new { permissions = permissions.Order().ToList() });
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "roles");
            return Results.Ok(ToDto(role, [], caller));
        });

        app.MapPut("/api/roles/{id:int}", async (HttpContext ctx, int id, RoleWriteDto request, VmsDbContext db,
            IHubContext<VmsHub> hub, ScopedHub scopedHub, UserScopeService scopes, AuditService audit) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.RolesManage, out _) is { } failure) return failure;
            var caller = (UserScope)ctx.Items["scope"]!;
            var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id);
            if (role is null) return Results.NotFound();
            if (role.IsAdmin)
                return Error("El rol Administrador tiene todos los permisos y no se modifica.", StatusCodes.Status403Forbidden);
            if (!CanGrant(caller, role))
                return Error("Este rol tiene permisos que usted no tiene: no puede modificarlo.", StatusCodes.Status403Forbidden);

            var (name, description, permissions, error) = await ValidateAsync(db, request, id, caller);
            if (error is not null) return error;

            var before = role.Permissions.Select(p => p.Permission).ToHashSet();
            var added = permissions.Except(before).ToList();
            var removed = before.Except(permissions).ToList();
            var changes = new List<string>();
            if (role.Name != name) changes.Add($"nombre '{role.Name}' → '{name}'");
            if (role.Description != description) changes.Add("descripción");
            if (added.Count > 0) changes.Add($"agregó: {Labels(added)}");
            if (removed.Count > 0) changes.Add($"quitó: {Labels(removed)}");

            role.Name = name;
            role.Description = description;
            role.Permissions.RemoveAll(p => removed.Contains(p.Permission));
            role.Permissions.AddRange(added.Select(p => new RolePermission { Permission = p }));
            role.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            scopes.Invalidate();

            await audit.LogAsync(ctx, "roles", "role-updated",
                targetType: "role", targetId: role.Id.ToString(), targetName: role.Name,
                detail: $"Modificó el rol '{role.Name}': " + (changes.Count > 0 ? string.Join("; ", changes) + "." : "sin cambios."),
                data: new { added, removed });

            var users = (await UsersByRoleAsync(db)).GetValueOrDefault(id) ?? [];
            // Los puestos de sus usuarios rehacen menús y botones con los permisos nuevos.
            if (added.Count > 0 || removed.Count > 0)
                foreach (var (userId, _) in users)
                    await scopedHub.ToUserAsync(userId, VmsHubContract.ConfigChanged, "permissions");
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "roles");
            return Results.Ok(ToDto(role, users.Select(u => u.Name).ToList(), caller));
        });

        app.MapDelete("/api/roles/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            IHubContext<VmsHub> hub, AuditService audit) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.RolesManage, out _) is { } failure) return failure;
            var caller = (UserScope)ctx.Items["scope"]!;
            var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id);
            if (role is null) return Results.NotFound();
            if (role.SystemKey is not null)
                return Error($"'{role.Name}' es un rol de sistema y no se puede eliminar.", StatusCodes.Status403Forbidden);
            if (!CanGrant(caller, role))
                return Error("Este rol tiene permisos que usted no tiene: no puede eliminarlo.", StatusCodes.Status403Forbidden);
            var users = (await UsersByRoleAsync(db)).GetValueOrDefault(id) ?? [];
            if (users.Count > 0)
                return Error($"El rol está asignado a {string.Join(", ", users.Select(u => u.Name))}: " +
                             "quíteselo antes de eliminarlo.", StatusCodes.Status409Conflict);

            db.Roles.Remove(role);
            await db.SaveChangesAsync();
            await audit.LogAsync(ctx, "roles", "role-deleted",
                targetType: "role", targetId: id.ToString(), targetName: role.Name,
                detail: $"Eliminó el rol '{role.Name}' ({role.Permissions.Count} permiso(s)).",
                data: new { permissions = role.Permissions.Select(p => p.Permission).Order().ToList() });
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "roles");
            return Results.Ok();
        });
    }
}
