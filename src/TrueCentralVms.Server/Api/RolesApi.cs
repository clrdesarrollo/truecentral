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
/// Mantenedor de roles: QUÉ puede hacer (permisos) y DÓNDE (alcance: todo el
/// sistema, o ubicaciones y recursos sueltos). Regla contra el escalamiento:
/// nadie otorga lo que no tiene. Quien no es administrador solo crea,
/// modifica, borra o asigna roles cuyos permisos son todos suyos, y solo con
/// un alcance que cabe en el suyo; el rol Administrador no se edita ni se
/// borra y el Operador no se borra.
/// </summary>
public static class RolesApi
{
    /// <summary>Topes del alcance de un rol (cada ubicación ya incluye sus sububicaciones).</summary>
    private const int MaxScopeLocations = 200;
    private const int MaxScopeItems = 2000;

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    /// <summary>Los permisos de <paramref name="permissions"/> que <paramref name="caller"/> no tiene (ninguno si es administrador).</summary>
    public static List<string> Missing(UserScope caller, IEnumerable<string> permissions) =>
        caller.IsAdmin ? [] : permissions.Where(p => !caller.Has(p)).Distinct().ToList();

    /// <summary>
    /// ¿Esta sesión puede asignar el rol? (no es el Administrador, salvo para un
    /// administrador, y no da permisos que ella no tenga). El alcance se revisa
    /// al asignarlo, con el que le quedaría al usuario (roles + su límite propio).
    /// </summary>
    public static bool CanGrant(UserScope caller, Role role) =>
        caller.IsAdmin || (!role.IsAdmin && Missing(caller, role.Permissions.Select(p => p.Permission)).Count == 0);

    /// <summary>El alcance del rol como si fuera el único de un usuario (para compararlo con el de quien lo edita).</summary>
    public static UserScope ScopeOf(Role role, ScopeIndex index) =>
        ScopeBuilder.Build(0, role.IsAdmin, false, new HashSet<string>(), [UserScopeService.SpecOf(role)], ScopeSpec.Everything, index);

    /// <summary>¿Esta sesión puede modificar o borrar el rol? Además de los permisos,
    /// su alcance debe caber en el de ella: si no, lo cambiaría para todos sus usuarios.</summary>
    public static bool CanEdit(UserScope caller, Role role, ScopeIndex index) =>
        !role.IsAdmin && CanGrant(caller, role) && caller.Covers(ScopeOf(role, index));

    private static string Labels(IEnumerable<string> keys) =>
        string.Join(", ", keys.Select(Permissions.LabelOf).Order());

    /// <summary>"Todo el sistema" o "Sucursal Norte; Cámara DVR Sur · Acceso (ve el resto sin operarlo)".</summary>
    public static string ScopeText(Role role, ScopeIndex index)
    {
        if (role.IsAdmin || !role.RestrictScope) return "Todo el sistema";
        var parts = role.Locations.Select(l => index.PathOf(l.LocationId)).Where(n => n.Length > 0).Order()
            .Concat(role.Resources.Select(r => index.LabelOf(r.Kind, r.ResourceId)).Order())
            .ToList();
        string text = parts.Count == 0 ? "Nada (sin ubicaciones ni recursos)"
            : parts.Count <= 6 ? string.Join("; ", parts)
            : string.Join("; ", parts.Take(6)) + $"; … y {parts.Count - 6} más";
        return role.ViewOutsideScope ? text + " (ve el resto sin operarlo)" : text;
    }

    private static RoleDto ToDto(Role role, IReadOnlyList<string> users, UserScope caller, ScopeIndex index) => new(
        role.Id, role.Name, role.Description, role.SystemKey,
        role.IsAdmin ? Permissions.All.Select(p => p.Key).ToList() : role.Permissions.Select(p => p.Permission).Order().ToList(),
        users.Count, users,
        Editable: CanEdit(caller, role, index),
        Assignable: CanGrant(caller, role),
        role.UpdatedAt,
        RestrictScope: !role.IsAdmin && role.RestrictScope,
        ViewOutsideScope: !role.IsAdmin && role.RestrictScope && role.ViewOutsideScope,
        LocationIds: role.Locations.Select(l => l.LocationId).Order().ToList(),
        Items: role.Resources.OrderBy(r => r.Kind).ThenBy(r => r.ResourceId)
            .Select(r => new ScopeItemDto(r.Kind, r.ResourceId, index.NameOf(r.Kind, r.ResourceId))).ToList(),
        ScopeSummary: ScopeText(role, index));

    /// <summary>Usuarios de cada rol (nombre), para la lista y para avisar a sus puestos.</summary>
    private static async Task<Dictionary<int, List<(int Id, string Name)>>> UsersByRoleAsync(VmsDbContext db) =>
        (await db.UserRoles.Join(db.Users, ur => ur.UserId, u => u.Id, (ur, u) => new { ur.RoleId, u.Id, u.Username })
            .ToListAsync())
        .GroupBy(x => x.RoleId)
        .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Username).Select(x => (x.Id, x.Username)).ToList());

    private static IQueryable<Role> RolesWithScope(VmsDbContext db) =>
        db.Roles.Include(r => r.Permissions).Include(r => r.Locations).Include(r => r.Resources).AsSplitQuery();

    private sealed record Validated(string Name, string Description, HashSet<string> Permissions,
        bool Restrict, bool ViewOutside, List<int> Locations, List<(string Kind, int Id)> Items);

    /// <summary>Valida nombre, permisos y alcance; devuelve lo normalizado (permisos con lo que suponen).</summary>
    private static async Task<(Validated? Value, IResult? Error)> ValidateAsync(
        VmsDbContext db, RoleWriteDto request, Role? current, UserScope caller, ScopeIndex index)
    {
        string name = request.Name?.Trim() ?? "";
        string description = request.Description?.Trim() ?? "";
        if (name.Length is < 2 or > 64)
            return (null, Error("El nombre del rol debe tener entre 2 y 64 caracteres."));
        if (description.Length > 256)
            return (null, Error("La descripción admite hasta 256 caracteres."));
        int? id = current?.Id;
        if (await db.Roles.AnyAsync(r => r.Id != id && r.Name.ToLower() == name.ToLower()))
            return (null, Error("Ya existe un rol con ese nombre.", StatusCodes.Status409Conflict));
        var unknown = (request.Permissions ?? []).Where(p => !Permissions.IsValid(p)).ToList();
        if (unknown.Count > 0)
            return (null, Error($"Permisos desconocidos: {string.Join(", ", unknown)}. Recargue la página."));
        var permissions = Permissions.Normalize(request.Permissions);
        if (Missing(caller, permissions) is { Count: > 0 } missing)
            return (null, Error($"No puede otorgar permisos que usted no tiene: {Labels(missing)}.",
                StatusCodes.Status403Forbidden));

        // Alcance (null en un campo = conservar el actual: un panel anterior no lo borra).
        bool restrict = request.RestrictScope ?? current?.RestrictScope ?? false;
        bool viewOutside = restrict && (request.ViewOutsideScope ?? current?.ViewOutsideScope ?? false);
        var locations = !restrict ? []
            : (request.LocationIds?.ToList() ?? current?.Locations.Select(l => l.LocationId).ToList() ?? []).Distinct().ToList();
        var items = !restrict ? []
            : (request.Items?.Select(i => (i.Kind, i.Id)).ToList()
               ?? current?.Resources.Select(r => (r.Kind, r.ResourceId)).ToList() ?? []).Distinct().ToList();
        if (restrict)
        {
            if (locations.Count > MaxScopeLocations)
                return (null, Error($"Elija a lo más {MaxScopeLocations} ubicaciones (cada una ya incluye sus sububicaciones)."));
            if (items.Count > MaxScopeItems)
                return (null, Error($"Elija a lo más {MaxScopeItems} recursos sueltos (un equipo completo cuenta como uno)."));
            if (items.FirstOrDefault(i => !ScopeKinds.IsValid(i.Kind)) is { Kind: { } badKind })
                return (null, Error($"Tipo de recurso desconocido: '{badKind}'. Recargue la página."));
            if (locations.Any(l => !index.Locations.ContainsKey(l)))
                return (null, Error("Alguna de las ubicaciones elegidas ya no existe: recargue la página.", StatusCodes.Status404NotFound));
            // Lo que ya no existe no se guarda (no cuenta igual).
            items = items.Where(i => index.NameOf(i.Kind, i.Id) is not null).ToList();
            if (locations.Count == 0 && items.Count == 0 && !viewOutside)
                return (null, Error("Elija al menos una ubicación o un recurso, o marque que puede ver el resto (si solo debe supervisar)."));
        }

        var value = new Validated(name, description, permissions, restrict, viewOutside, locations, items);
        if (!caller.IsAdmin)
        {
            var probe = new Role
            {
                RestrictScope = restrict, ViewOutsideScope = viewOutside,
                Locations = locations.Select(l => new RoleLocation { LocationId = l }).ToList(),
                Resources = items.Select(i => new RoleResource { Kind = i.Kind, ResourceId = i.Id }).ToList(),
            };
            if (!caller.Covers(ScopeOf(probe, index)))
                return (null, Error(caller.Unrestricted || restrict
                    ? "El alcance del rol debe quedar dentro del suyo: solo ubicaciones y recursos que usted opera" +
                      (viewOutside && !caller.ViewOutside ? ", y sin \"ver el resto\" (usted no lo tiene)." : ".")
                    : "Usted tiene un alcance limitado: el rol también debe limitarse a ubicaciones o recursos dentro del suyo.",
                    StatusCodes.Status403Forbidden));
        }
        return (value, null);
    }

    private static void ApplyScope(Role role, Validated v)
    {
        role.RestrictScope = v.Restrict;
        role.ViewOutsideScope = v.ViewOutside;
        role.Locations.RemoveAll(l => !v.Locations.Contains(l.LocationId));
        foreach (int id in v.Locations.Where(id => role.Locations.All(l => l.LocationId != id)))
            role.Locations.Add(new RoleLocation { LocationId = id });
        var wanted = v.Items.ToHashSet();
        role.Resources.RemoveAll(r => !wanted.Contains((r.Kind, r.ResourceId)));
        foreach (var (kind, id) in v.Items.Where(i => !role.Resources.Any(r => r.Kind == i.Kind && r.ResourceId == i.Id)))
            role.Resources.Add(new RoleResource { Kind = kind, ResourceId = id });
    }

    /// <summary>Huella del alcance (para saber si cambió, aunque el texto se abrevie).</summary>
    private static string ScopeKey(Role role) =>
        $"{role.RestrictScope}|{role.ViewOutsideScope}|{string.Join(",", role.Locations.Select(l => l.LocationId).Order())}|" +
        string.Join(",", role.Resources.Select(r => $"{r.Kind}:{r.ResourceId}").Order());

    private static object ScopeData(Role role) => new
    {
        role.RestrictScope, role.ViewOutsideScope,
        locationIds = role.Locations.Select(l => l.LocationId).Order().ToList(),
        items = role.Resources.Select(r => $"{r.Kind}:{r.ResourceId}").Order().ToList(),
    };

    /// <summary>
    /// Cambió lo que el rol permite: sus usuarios dejan de ver lo que quedó
    /// fuera (video abierto incluido) y sus puestos recargan árboles y listas.
    /// </summary>
    internal static async Task<int> ApplyToMembersAsync(IEnumerable<int> userIds, VmsDbContext db, MediaMtxManager mtx,
        ScopedHub scopedHub, UserScopeService scopes, bool scopeChanged, bool permissionsChanged)
    {
        int kicked = 0;
        foreach (int userId in userIds)
        {
            if (permissionsChanged)
                await scopedHub.ToUserAsync(userId, VmsHubContract.ConfigChanged, "permissions");
            if (!scopeChanged) continue;
            kicked += await SessionAccounting.KickOutOfScopeAsync(db, mtx, scopedHub, await scopes.ForUserAsync(userId));
            foreach (string topic in UsersApi.ScopeTopics)
                await scopedHub.ToUserAsync(userId, VmsHubContract.ConfigChanged, topic);
        }
        return kicked;
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

        // Recursos elegibles para el alcance de un rol: equipos completos y
        // recursos sueltos, con su equipo y su ubicación. Quien tiene un alcance
        // limitado solo ve lo que él mismo opera (no puede dar más).
        app.MapGet("/api/roles/scope-catalog", async (HttpContext ctx, VmsDbContext db, UserScopeService scopes, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.RolesManage, out _) is { } failure) return failure;
            var caller = (UserScope)ctx.Items["scope"]!;
            var index = (await scopes.SnapshotAsync(ct)).Index;
            string? Path(int? id) => id is { } l && index.PathOf(l) is { Length: > 0 } p ? p : null;
            var list = new List<ScopeCatalogItemDto>();

            var devices = await db.Devices.AsNoTracking().Select(d => new { d.Id, d.Name, d.LocationId }).ToListAsync(ct);
            var channels = await db.Channels.AsNoTracking().Where(c => c.Enabled)
                .Select(c => new { c.Id, c.DeviceId, c.Name, c.LocationId }).ToListAsync(ct);
            foreach (var d in devices.OrderBy(d => d.Name))
            {
                var mine = channels.Where(c => c.DeviceId == d.Id).ToList();
                if (caller.Unrestricted || (mine.Count > 0 && mine.All(c => caller.CanOperateChannel(c.Id))))
                    list.Add(new(ScopeKinds.VideoDevice, d.Id, d.Name, null, d.LocationId, Path(d.LocationId)));
                foreach (var c in mine.Where(c => caller.CanOperateChannel(c.Id)))
                    list.Add(new(ScopeKinds.Camera, c.Id, c.Name, d.Name, c.LocationId, Path(c.LocationId)));
            }

            var accessDevices = await db.AccessDevices.AsNoTracking().Select(d => new { d.Id, d.Name, d.LocationId }).ToListAsync(ct);
            var doors = await db.AccessDoors.AsNoTracking().Select(d => new { d.Id, d.AccessDeviceId, d.Name, d.LocationId }).ToListAsync(ct);
            foreach (var d in accessDevices.OrderBy(d => d.Name))
            {
                var mine = doors.Where(x => x.AccessDeviceId == d.Id).ToList();
                if (caller.Unrestricted || (mine.Count > 0 && mine.All(x => caller.CanOperateDoor(x.Id))))
                    list.Add(new(ScopeKinds.AccessDevice, d.Id, d.Name, null, d.LocationId, Path(d.LocationId)));
                foreach (var x in mine.Where(x => caller.CanOperateDoor(x.Id)))
                    list.Add(new(ScopeKinds.Door, x.Id, x.Name, d.Name, x.LocationId, Path(x.LocationId)));
            }

            var panels = await db.AlarmPanels.AsNoTracking().Select(p => new { p.Id, p.Name, p.LocationId }).ToListAsync(ct);
            var areas = await db.AlarmAreas.AsNoTracking().Select(a => new { a.Id, a.AlarmPanelId, a.Number, a.Name, a.LocationId }).ToListAsync(ct);
            var zones = await db.AlarmZones.AsNoTracking().Select(z => new { z.Id, z.AlarmPanelId, z.Number, z.Name }).ToListAsync(ct);
            foreach (var p in panels.OrderBy(p => p.Name))
            {
                var myAreas = areas.Where(a => a.AlarmPanelId == p.Id).OrderBy(a => a.Number).ToList();
                if (caller.CanOperateAllAreas(p.Id))
                    list.Add(new(ScopeKinds.AlarmPanel, p.Id, p.Name, null, p.LocationId, Path(p.LocationId)));
                foreach (var a in myAreas.Where(a => caller.CanOperateArea(p.Id, a.Number)))
                    list.Add(new(ScopeKinds.Partition, a.Id, a.Name, p.Name, a.LocationId, Path(a.LocationId)));
                foreach (var z in zones.Where(z => z.AlarmPanelId == p.Id).OrderBy(z => z.Number)
                             .Where(z => caller.CanOperateZone(p.Id, z.Number)))
                    list.Add(new(ScopeKinds.Zone, z.Id, z.Name, p.Name, index.ZoneRows.GetValueOrDefault(z.Id), Path(index.ZoneRows.GetValueOrDefault(z.Id))));
            }

            foreach (var x in (await db.CercoPanels.AsNoTracking().Select(x => new { x.Id, x.Name, x.LocationId }).ToListAsync(ct))
                         .Where(x => caller.CanOperateCerco(x.Id)).OrderBy(x => x.Name))
                list.Add(new(ScopeKinds.Fence, x.Id, x.Name, null, x.LocationId, Path(x.LocationId)));
            foreach (var x in (await db.Speakers.AsNoTracking().Select(x => new { x.Id, x.Name, x.LocationId }).ToListAsync(ct))
                         .Where(x => caller.CanOperateSpeaker(x.Id)).OrderBy(x => x.Name))
                list.Add(new(ScopeKinds.Speaker, x.Id, x.Name, null, x.LocationId, Path(x.LocationId)));
            foreach (var x in (await db.Intercoms.AsNoTracking().Select(x => new { x.Id, x.Name, x.LocationId }).ToListAsync(ct))
                         .Where(x => caller.CanOperateIntercom(x.Id)).OrderBy(x => x.Name))
                list.Add(new(ScopeKinds.Intercom, x.Id, x.Name, null, x.LocationId, Path(x.LocationId)));
            return Results.Ok(list);
        });

        app.MapGet("/api/roles", async (HttpContext ctx, VmsDbContext db, UserScopeService scopes) =>
        {
            if (ApiSecurity.RequireAny(ctx, out _, Permissions.RolesManage, Permissions.UsersManage) is { } failure) return failure;
            var caller = (UserScope)ctx.Items["scope"]!;
            var index = (await scopes.SnapshotAsync(ctx.RequestAborted)).Index;
            var roles = await RolesWithScope(db).AsNoTracking().ToListAsync();
            var users = await UsersByRoleAsync(db);
            // De sistema primero (Administrador, Operador), luego por nombre.
            return Results.Ok(roles
                .OrderBy(r => r.SystemKey switch { Role.AdminKey => 0, Role.OperatorKey => 1, _ => 2 })
                .ThenBy(r => r.Name)
                .Select(r => ToDto(r, users.GetValueOrDefault(r.Id)?.Select(u => u.Name).ToList() ?? [], caller, index)));
        });

        app.MapPost("/api/roles", async (HttpContext ctx, RoleWriteDto request, VmsDbContext db,
            IHubContext<VmsHub> hub, UserScopeService scopes, AuditService audit) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.RolesManage, out _) is { } failure) return failure;
            var caller = (UserScope)ctx.Items["scope"]!;
            var index = (await scopes.SnapshotAsync(ctx.RequestAborted)).Index;
            var (v, error) = await ValidateAsync(db, request, null, caller, index);
            if (error is not null) return error;

            var role = new Role
            {
                Name = v!.Name, Description = v.Description,
                Permissions = v.Permissions.Select(p => new RolePermission { Permission = p }).ToList(),
            };
            ApplyScope(role, v);
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            await audit.LogAsync(ctx, "roles", "role-created",
                targetType: "role", targetId: role.Id.ToString(), targetName: role.Name,
                detail: $"Creó el rol '{role.Name}' con {v.Permissions.Count} permiso(s): " +
                        (v.Permissions.Count == 0 ? "ninguno" : Labels(v.Permissions)) +
                        $"; alcance: {ScopeText(role, index)}.",
                data: new { permissions = v.Permissions.Order().ToList(), scope = ScopeData(role) });
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "roles");
            return Results.Ok(ToDto(role, [], caller, index));
        });

        app.MapPut("/api/roles/{id:int}", async (HttpContext ctx, int id, RoleWriteDto request, VmsDbContext db,
            IHubContext<VmsHub> hub, ScopedHub scopedHub, UserScopeService scopes, MediaMtxManager mtx, AuditService audit) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.RolesManage, out _) is { } failure) return failure;
            var caller = (UserScope)ctx.Items["scope"]!;
            var index = (await scopes.SnapshotAsync(ctx.RequestAborted)).Index;
            var role = await RolesWithScope(db).FirstOrDefaultAsync(r => r.Id == id);
            if (role is null) return Results.NotFound();
            if (role.IsAdmin)
                return Error("El rol Administrador tiene todos los permisos y no se modifica.", StatusCodes.Status403Forbidden);
            if (!CanGrant(caller, role))
                return Error("Este rol tiene permisos que usted no tiene: no puede modificarlo.", StatusCodes.Status403Forbidden);
            if (!caller.Covers(ScopeOf(role, index)))
                return Error("Este rol abarca más que su propio alcance: no puede modificarlo.", StatusCodes.Status403Forbidden);

            var (v, error) = await ValidateAsync(db, request, role, caller, index);
            if (error is not null) return error;

            var before = role.Permissions.Select(p => p.Permission).ToHashSet();
            var added = v!.Permissions.Except(before).ToList();
            var removed = before.Except(v.Permissions).ToList();
            string scopeBefore = ScopeText(role, index);
            var scopeDataBefore = ScopeData(role);
            string scopeKeyBefore = ScopeKey(role);
            var changes = new List<string>();
            if (role.Name != v.Name) changes.Add($"nombre '{role.Name}' → '{v.Name}'");
            if (role.Description != v.Description) changes.Add("descripción");
            if (added.Count > 0) changes.Add($"agregó: {Labels(added)}");
            if (removed.Count > 0) changes.Add($"quitó: {Labels(removed)}");

            role.Name = v.Name;
            role.Description = v.Description;
            role.Permissions.RemoveAll(p => removed.Contains(p.Permission));
            role.Permissions.AddRange(added.Select(p => new RolePermission { Permission = p }));
            ApplyScope(role, v);
            string scopeAfter = ScopeText(role, index);
            bool scopeChanged = scopeKeyBefore != ScopeKey(role);
            if (scopeChanged) changes.Add($"alcance: {scopeBefore} → {scopeAfter}");
            role.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            scopes.Invalidate();

            var users = (await UsersByRoleAsync(db)).GetValueOrDefault(id) ?? [];
            // Sus usuarios: menús y botones con los permisos nuevos y, si cambió
            // el alcance, fuera lo que ya no les toca (video incluido).
            int kicked = await ApplyToMembersAsync(users.Select(u => u.Id), db, mtx, scopedHub, scopes,
                scopeChanged, added.Count > 0 || removed.Count > 0);

            await audit.LogAsync(ctx, "roles", "role-updated",
                targetType: "role", targetId: role.Id.ToString(), targetName: role.Name,
                detail: $"Modificó el rol '{role.Name}': " + (changes.Count > 0 ? string.Join("; ", changes) + "." : "sin cambios.") +
                        (kicked > 0 ? $" Se cortaron {kicked} sesión(es) de video que quedaron fuera de su alcance." : ""),
                data: new { added, removed, scopeBefore = scopeDataBefore, scopeAfter = ScopeData(role), kickedSessions = kicked });
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "roles");
            return Results.Ok(ToDto(role, users.Select(u => u.Name).ToList(), caller, index));
        });

        app.MapDelete("/api/roles/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            IHubContext<VmsHub> hub, UserScopeService scopes, AuditService audit) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.RolesManage, out _) is { } failure) return failure;
            var caller = (UserScope)ctx.Items["scope"]!;
            var index = (await scopes.SnapshotAsync(ctx.RequestAborted)).Index;
            var role = await RolesWithScope(db).FirstOrDefaultAsync(r => r.Id == id);
            if (role is null) return Results.NotFound();
            if (role.SystemKey is not null)
                return Error($"'{role.Name}' es un rol de sistema y no se puede eliminar.", StatusCodes.Status403Forbidden);
            if (!CanEdit(caller, role, index))
                return Error("Este rol tiene permisos o alcance que usted no tiene: no puede eliminarlo.", StatusCodes.Status403Forbidden);
            var users = (await UsersByRoleAsync(db)).GetValueOrDefault(id) ?? [];
            if (users.Count > 0)
                return Error($"El rol está asignado a {string.Join(", ", users.Select(u => u.Name))}: " +
                             "quíteselo antes de eliminarlo.", StatusCodes.Status409Conflict);

            string scopeText = ScopeText(role, index);
            db.Roles.Remove(role);
            await db.SaveChangesAsync();
            await audit.LogAsync(ctx, "roles", "role-deleted",
                targetType: "role", targetId: id.ToString(), targetName: role.Name,
                detail: $"Eliminó el rol '{role.Name}' ({role.Permissions.Count} permiso(s); alcance: {scopeText}).",
                data: new { permissions = role.Permissions.Select(p => p.Permission).Order().ToList(), scope = ScopeData(role) });
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "roles");
            return Results.Ok();
        });
    }
}
