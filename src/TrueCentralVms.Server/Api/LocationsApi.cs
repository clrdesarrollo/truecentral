using System.Globalization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Core.Domain;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Recursos y ubicaciones: el árbol lógico del inventario (sitio → edificio →
/// piso → sector → punto) y en qué ubicación está cada recurso. Consultar puede
/// cualquier usuario; modificar, solo un administrador. Todo cambio queda en la
/// bitácora (categoría "locations") y se anuncia por el hub para que los puestos
/// rehagan su árbol.
/// </summary>
public static class LocationsApi
{
    /// <summary>Niveles máximos del árbol: los cinco tipos y algo de holgura.</summary>
    private const int MaxDepth = 8;

    /// <summary>Tope de recursos por operación de ubicar en bloque.</summary>
    private const int MaxBatch = 2000;

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    private static LocationDto ToDto(Location l, int resourceCount) => new(
        l.Id, l.ParentId, l.Name, l.Kind, l.Description, l.Address, l.Latitude, l.Longitude, resourceCount, l.SortOrder);

    /// <summary>Orden de lectura de las hermanas nunca ordenadas a mano: "Bodega 2" antes que "Bodega 10".</summary>
    private static readonly StringComparer NaturalOrder =
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    /// <summary>
    /// Posición de una ubicación que llega a la rama <paramref name="parentId"/>
    /// (nueva o movida): al final si sus hermanas ya se ordenaron a mano; si no
    /// (todas en 0), 0, y sigue yendo por nombre como las demás.
    /// </summary>
    private static int NextSortOrder(IEnumerable<Location> all, int? parentId, int? exceptId = null)
    {
        var siblings = all.Where(l => l.ParentId == parentId && l.Id != exceptId).Select(l => l.SortOrder).ToList();
        return siblings.Any(order => order != 0) ? siblings.Max() + 1 : 0;
    }

    /// <summary>Hasta diez nombres entre comillas, para la bitácora.</summary>
    private static string NameList(IReadOnlyCollection<string> names) =>
        string.Join(", ", names.Take(10).Select(n => $"\"{n}\"")) + (names.Count > 10 ? $" y {names.Count - 10} más" : "");

    private static string KindLabel(LocationKind kind) => kind switch
    {
        LocationKind.Site => "sitio",
        LocationKind.Building => "edificio",
        LocationKind.Floor => "piso",
        LocationKind.Sector => "sector",
        LocationKind.Point => "punto",
        _ => kind.ToString(),
    };

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? Validate(LocationSaveRequest request)
    {
        string name = (request.Name ?? "").Trim();
        if (name.Length == 0) return "El nombre es obligatorio.";
        if (name.Length > 128) return "El nombre no puede superar los 128 caracteres.";
        if (!Enum.IsDefined(request.Kind)) return "El tipo de ubicación no es válido.";
        if ((Clean(request.Description)?.Length ?? 0) > 500) return "La descripción no puede superar los 500 caracteres.";
        if ((Clean(request.Address)?.Length ?? 0) > 255) return "La dirección no puede superar los 255 caracteres.";
        if ((request.Latitude is null) != (request.Longitude is null))
            return "Indique latitud y longitud juntas, o deje las dos vacías.";
        if (request.Latitude is < -90 or > 90 || request.Longitude is < -180 or > 180)
            return "Las coordenadas no son válidas (latitud entre -90 y 90, longitud entre -180 y 180).";
        return null;
    }

    /// <summary>Ruta legible "Casa matriz › Edificio A › Acceso" del nodo dado
    /// (incluido); "la raíz" si no hay nodo.</summary>
    private static string PathOf(int? id, IReadOnlyDictionary<int, Location> byId)
    {
        var parts = new List<string>();
        var seen = new HashSet<int>();
        while (id is { } current && byId.TryGetValue(current, out var node) && seen.Add(current))
        {
            parts.Add(node.Name);
            id = node.ParentId;
        }
        parts.Reverse();
        return parts.Count == 0 ? "la raíz" : string.Join(" › ", parts);
    }

    /// <summary>Nivel del nodo (1 = raíz; 0 = sin nodo).</summary>
    private static int DepthOf(int? id, IReadOnlyDictionary<int, Location> byId)
    {
        int depth = 0;
        var seen = new HashSet<int>();
        while (id is { } current && byId.TryGetValue(current, out var node) && seen.Add(current))
        {
            depth++;
            id = node.ParentId;
        }
        return depth;
    }

    /// <summary>El nodo y todos sus descendientes.</summary>
    private static List<int> SubtreeOf(int id, IReadOnlyCollection<Location> all)
    {
        var byParent = all.Where(l => l.ParentId is not null).ToLookup(l => l.ParentId!.Value);
        var result = new List<int>();
        var visited = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(id);
        while (pending.Count > 0)
        {
            int current = pending.Pop();
            if (!visited.Add(current)) continue;
            result.Add(current);
            foreach (var child in byParent[current]) pending.Push(child.Id);
        }
        return result;
    }

    /// <summary>Dos hermanas no pueden llamarse igual (sin distinguir mayúsculas).</summary>
    private static bool NameTaken(IEnumerable<Location> all, int? parentId, string name, int? exceptId) =>
        all.Any(l => l.ParentId == parentId && l.Id != exceptId &&
                     string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// El árbol o la ubicación de los recursos cambió: se rehacen los alcances
    /// por ubicación (mover una ubicación cambia lo que contiene cada alcance)
    /// y los puestos recargan.
    /// </summary>
    private static Task AnnounceAsync(HttpContext ctx, IHubContext<VmsHub> hub, CancellationToken ct)
    {
        ctx.RequestServices.GetRequiredService<UserScopeService>().Invalidate();
        return hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "locations", cancellationToken: ct);
    }

    public static void MapLocationsApi(this WebApplication app)
    {
        // Árbol completo en una lista plana (el panel y el cliente lo arman por ParentId).
        app.MapGet("/api/locations", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var locations = await db.Locations.AsNoTracking().OrderBy(l => l.SortOrder).ThenBy(l => l.Name).ToListAsync(ct);
            var counts = await ResourceCatalog.CountByLocationAsync(db, ct);
            var scope = await ctx.ScopeAsync(session);
            if (scope.FiltersView)
            {
                // Alcance limitado: solo su parte del árbol (las ubicaciones completas
                // y donde tiene recursos sueltos, con las que las contienen); lo de
                // más arriba de sus ubicaciones completas no se entrega y ellas
                // quedan como raíces.
                var visible = scope.VisibleLocations;
                return Results.Ok(locations.Where(l => visible.Contains(l.Id))
                    .Select(l => ToDto(l, counts.GetValueOrDefault(l.Id)) with
                    {
                        ParentId = l.ParentId is { } parent && visible.Contains(parent) ? parent : null,
                    }));
            }
            return Results.Ok(locations.Select(l => ToDto(l, counts.GetValueOrDefault(l.Id))));
        });

        app.MapPost("/api/locations", async (HttpContext ctx, LocationSaveRequest request, VmsDbContext db,
            IHubContext<VmsHub> hub, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.ResourcesManage, out _) is { } failure) return failure;
            if (Validate(request) is { } invalid) return Error(invalid);

            var all = await db.Locations.AsNoTracking().ToListAsync(ct);
            var byId = all.ToDictionary(l => l.Id);
            string name = request.Name.Trim();
            if (request.ParentId is { } parentId && !byId.ContainsKey(parentId))
                return Error("La ubicación superior ya no existe.", StatusCodes.Status404NotFound);
            if (DepthOf(request.ParentId, byId) + 1 > MaxDepth)
                return Error($"El árbol admite hasta {MaxDepth} niveles.");
            if (NameTaken(all, request.ParentId, name, null))
                return Error($"Ya hay una ubicación \"{name}\" en {PathOf(request.ParentId, byId)}.",
                    StatusCodes.Status409Conflict);

            var location = new Location
            {
                ParentId = request.ParentId,
                Name = name,
                Kind = request.Kind,
                SortOrder = NextSortOrder(all, request.ParentId),
                Description = Clean(request.Description),
                Address = Clean(request.Address),
                Latitude = request.Latitude,
                Longitude = request.Longitude,
            };
            db.Locations.Add(location);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "locations", "location-created",
                targetType: "location", targetId: location.Id.ToString(), targetName: location.Name,
                detail: $"Creó la ubicación \"{location.Name}\" ({KindLabel(location.Kind)}) en {PathOf(location.ParentId, byId)}.");
            await AnnounceAsync(ctx, hub, ct);
            return Results.Ok(ToDto(location, 0));
        });

        // La ubicación con ese nombre en esa rama: la que hay o una nueva. El
        // alta de un equipo con «Nueva ubicación con el nombre del equipo» pasa
        // por aquí antes de guardarlo (repetir el alta no la duplica).
        app.MapPost("/api/locations/ensure", async (HttpContext ctx, LocationEnsureRequest request, VmsDbContext db,
            IHubContext<VmsHub> hub, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.ResourcesManage, out _) is { } failure) return failure;
            if (Validate(new LocationSaveRequest(request.Name ?? "", LocationKind.Sector, request.ParentId)) is { } invalid)
                return Error(invalid);

            var all = await db.Locations.AsNoTracking().ToListAsync(ct);
            var byId = all.ToDictionary(l => l.Id);
            if (request.ParentId is { } parentId && !byId.ContainsKey(parentId))
                return Error("La ubicación superior ya no existe.", StatusCodes.Status404NotFound);
            var (location, created) = await EnsureLocationAsync(ctx, db, audit, all, byId,
                request.Name!.Trim(), request.ParentId, ct);
            if (location is null) return Error($"El árbol admite hasta {MaxDepth} niveles.");
            if (created) await AnnounceAsync(ctx, hub, ct);
            return Results.Ok(new LocationEnsureResult(location.Id, created, PathOf(location.Id, byId)));
        });

        // Modifica sus datos y, si cambia ParentId, la mueve con todo su contenido.
        app.MapPut("/api/locations/{id:int}", async (HttpContext ctx, int id, LocationSaveRequest request,
            VmsDbContext db, IHubContext<VmsHub> hub, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.ResourcesManage, out _) is { } failure) return failure;
            if (Validate(request) is { } invalid) return Error(invalid);

            var location = await db.Locations.FirstOrDefaultAsync(l => l.Id == id, ct);
            if (location is null) return Results.NotFound();
            var all = await db.Locations.AsNoTracking().ToListAsync(ct);
            var byId = all.ToDictionary(l => l.Id);
            string name = request.Name.Trim();

            bool moving = location.ParentId != request.ParentId;
            if (moving)
            {
                var subtree = SubtreeOf(id, all);
                if (request.ParentId is { } parentId)
                {
                    if (!byId.ContainsKey(parentId))
                        return Error("La ubicación de destino ya no existe.", StatusCodes.Status404NotFound);
                    if (subtree.Contains(parentId))
                        return Error("Una ubicación no se puede mover dentro de sí misma ni de sus sububicaciones.");
                }
                // Altura del subárbol que se mueve: debe caber completo bajo el destino.
                int height = subtree.Max(n => DepthOf(n, byId)) - DepthOf(id, byId) + 1;
                if (DepthOf(request.ParentId, byId) + height > MaxDepth)
                    return Error($"El árbol admite hasta {MaxDepth} niveles: ahí no cabe con todo lo que contiene.");
            }
            if (NameTaken(all, request.ParentId, name, id))
                return Error($"Ya hay una ubicación \"{name}\" en {PathOf(request.ParentId, byId)}.",
                    StatusCodes.Status409Conflict);

            var changes = new List<string>();
            if (location.Name != name) changes.Add($"nombre \"{location.Name}\" → \"{name}\"");
            if (location.Kind != request.Kind) changes.Add($"tipo {KindLabel(location.Kind)} → {KindLabel(request.Kind)}");
            if (location.Description != Clean(request.Description)) changes.Add("descripción");
            if (location.Address != Clean(request.Address)) changes.Add("dirección");
            if (location.Latitude != request.Latitude || location.Longitude != request.Longitude) changes.Add("coordenadas");
            string from = PathOf(location.ParentId, byId);
            string to = PathOf(request.ParentId, byId);

            location.Name = name;
            location.Kind = request.Kind;
            if (moving) location.SortOrder = NextSortOrder(all, request.ParentId, id);
            location.ParentId = request.ParentId;
            location.Description = Clean(request.Description);
            location.Address = Clean(request.Address);
            location.Latitude = request.Latitude;
            location.Longitude = request.Longitude;
            location.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            if (changes.Count > 0 || moving)
            {
                if (moving) changes.Add($"movida de {from} a {to}");
                bool onlyMoved = moving && changes.Count == 1;
                await audit.LogAsync(ctx, "locations", onlyMoved ? "location-moved" : "location-updated",
                    targetType: "location", targetId: id.ToString(), targetName: location.Name,
                    detail: onlyMoved
                        ? $"Movió la ubicación \"{location.Name}\" de {from} a {to}."
                        : $"Modificó la ubicación \"{location.Name}\": {string.Join(", ", changes)}.");
                await AnnounceAsync(ctx, hub, ct);
            }
            int count = (await ResourceCatalog.CountByLocationAsync(db, ct)).GetValueOrDefault(id);
            return Results.Ok(ToDto(location, count));
        });

        // Orden de las ubicaciones de una rama (arrastrarlas en el árbol de
        // Recursos). Lo respetan todos los árboles: panel, cliente y vista en vivo.
        // No cambia qué contiene cada alcance: solo se avisa a los puestos.
        app.MapPut("/api/locations/order", async (HttpContext ctx, LocationOrderRequest request, VmsDbContext db,
            IHubContext<VmsHub> hub, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.ResourcesManage, out _) is { } failure) return failure;
            if (request.Ids is not { Count: > 0 }) return Error("No se indicó ninguna ubicación.");

            var all = await db.Locations.ToListAsync(ct);
            var byId = all.ToDictionary(l => l.Id);
            if (request.ParentId is { } parentId && !byId.ContainsKey(parentId))
                return Error("La ubicación superior ya no existe.", StatusCodes.Status404NotFound);
            var siblings = all.Where(l => l.ParentId == request.ParentId)
                .OrderBy(l => l.SortOrder).ThenBy(l => l.Name, NaturalOrder).ToList();
            if (request.Ids.Any(id => siblings.All(s => s.Id != id)))
                return Error("El árbol cambió mientras lo ordenaba: recargue la página.", StatusCodes.Status409Conflict);

            var ordered = request.Ids.Distinct().Select(id => byId[id]).ToList();
            ordered.AddRange(siblings.Where(s => !ordered.Contains(s)));
            bool changed = false;
            for (int i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].SortOrder == i) continue;
                ordered[i].SortOrder = i;
                changed = true;
            }
            string path = PathOf(request.ParentId, byId);
            if (!changed) return Results.Ok(new { message = "No hubo cambios: ya estaban en ese orden." });
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "locations", "locations-reordered",
                targetType: "location", targetId: request.ParentId?.ToString(),
                targetName: request.ParentId is null ? null : path,
                detail: $"Cambió el orden de las ubicaciones de {path}: {NameList(ordered.Select(l => l.Name).ToList())}.");
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "locations", cancellationToken: ct);
            return Results.Ok(new { message = $"Se guardó el orden de las ubicaciones de {path}." });
        });

        // Orden de los recursos ubicados directamente en una ubicación (arrastrar
        // las filas en Recursos): el árbol del cliente y la vista en vivo lo siguen.
        app.MapPut("/api/locations/{id:int}/resources/order", async (HttpContext ctx, int id,
            ResourceOrderRequest request, VmsDbContext db, IHubContext<VmsHub> hub, AuditService audit,
            CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.ResourcesManage, out _) is { } failure) return failure;
            if (request.Resources is not { Count: > 0 }) return Error("No se indicó ningún recurso.");
            if (request.Resources.Count > MaxBatch)
                return Error($"Demasiados recursos en una sola operación (máximo {MaxBatch}).");

            var byId = await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id, ct);
            if (!byId.ContainsKey(id)) return Results.NotFound();
            var (changed, names, missing) = await ResourceCatalog.SetOrderAsync(db, id, request.Resources, ct);
            if (missing > 0)
                return Error("Algunos recursos ya no están en esta ubicación: recargue la página.", StatusCodes.Status409Conflict);
            string path = PathOf(id, byId);
            if (!changed) return Results.Ok(new { message = "No hubo cambios: ya estaban en ese orden." });
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "locations", "resources-reordered",
                targetType: "location", targetId: id.ToString(), targetName: path,
                detail: $"Cambió el orden de los recursos de {path}: {NameList(names)}.");
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "locations", cancellationToken: ct);
            return Results.Ok(new { message = $"Se guardó el orden de los recursos de {path}." });
        });

        // Solo vacía de sububicaciones; sus recursos quedan por ubicar (FK SET NULL).
        app.MapDelete("/api/locations/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            IHubContext<VmsHub> hub, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.ResourcesManage, out _) is { } failure) return failure;
            var location = await db.Locations.FirstOrDefaultAsync(l => l.Id == id, ct);
            if (location is null) return Results.NotFound();
            int children = await db.Locations.CountAsync(l => l.ParentId == id, ct);
            if (children > 0)
                return Error($"\"{location.Name}\" tiene {children} sububicación(es): muévalas o elimínelas antes.",
                    StatusCodes.Status409Conflict);
            // Borrarla cambiaría en silencio lo que esas personas ven y operan.
            var scopedUsers = await db.UserLocations.Where(ul => ul.LocationId == id)
                .Join(db.Users, ul => ul.UserId, u => u.Id, (ul, u) => u.Username).OrderBy(n => n).ToListAsync(ct);
            if (scopedUsers.Count > 0)
                return Error($"\"{location.Name}\" está en el alcance de {scopedUsers.Count} usuario(s) " +
                             $"({string.Join(", ", scopedUsers.Take(5))}{(scopedUsers.Count > 5 ? "…" : "")}): " +
                             "quítela de su alcance (Seguridad → Usuarios) antes de eliminarla.",
                    StatusCodes.Status409Conflict);
            var scopedRoles = await db.RoleLocations.Where(rl => rl.LocationId == id)
                .Join(db.Roles, rl => rl.RoleId, r => r.Id, (rl, r) => r.Name).OrderBy(n => n).ToListAsync(ct);
            if (scopedRoles.Count > 0)
                return Error($"\"{location.Name}\" está en el alcance de {scopedRoles.Count} rol(es) " +
                             $"({string.Join(", ", scopedRoles.Take(5))}{(scopedRoles.Count > 5 ? "…" : "")}): " +
                             "quítela de su alcance (Seguridad → Roles) antes de eliminarla.",
                    StatusCodes.Status409Conflict);

            var byId = await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id, ct);
            string path = PathOf(id, byId);
            int freed = (await ResourceCatalog.CountByLocationAsync(db, ct)).GetValueOrDefault(id);
            // Los equipos ubicados ahí (grabadores, paneles, controladoras) también quedan por ubicar.
            int equipment = await db.Devices.CountAsync(d => d.LocationId == id, ct)
                            + await db.AlarmPanels.CountAsync(p => p.LocationId == id, ct)
                            + await db.AccessDevices.CountAsync(a => a.LocationId == id, ct);
            string released = string.Join(" y ", new[]
            {
                freed > 0 ? $"{freed} recurso(s)" : null,
                equipment > 0 ? $"{equipment} equipo(s)" : null,
            }.Where(part => part is not null));
            db.Locations.Remove(location);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "locations", "location-deleted",
                targetType: "location", targetId: id.ToString(), targetName: location.Name,
                detail: $"Eliminó la ubicación {path}" +
                        (released.Length > 0 ? $"; sus {released} quedaron por ubicar." : "."));
            await AnnounceAsync(ctx, hub, ct);
            return Results.Ok(new
            {
                freed,
                message = released.Length > 0
                    ? $"Se eliminó \"{location.Name}\"; sus {released} quedaron por ubicar."
                    : $"Se eliminó \"{location.Name}\".",
            });
        });

        // Órdenes sobre la ubicación entera: armar o desarmar todas sus áreas de
        // alarma (con las de sus sububicaciones). Primero se pide con DryRun para
        // mostrar qué áreas se tocarían y confirmar. Cada área se ordena y se
        // audita igual que desde el módulo Alarmas, así su historial no cambia.
        app.MapPost("/api/locations/{id:int}/command", async (HttpContext ctx, int id, LocationCommandRequest request,
            VmsDbContext db, AlarmPanelService alarms, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.LocationsCommand, out var session) is { } failure) return failure;
            string command = (request.Command ?? "").Trim().ToLowerInvariant();
            if (command is not ("arm-away" or "arm-stay" or "disarm"))
                return Error("Orden no válida: use arm-away, arm-stay o disarm.");

            var all = await db.Locations.AsNoTracking().ToListAsync(ct);
            var location = all.FirstOrDefault(l => l.Id == id);
            if (location is null) return Results.NotFound();
            string path = PathOf(id, all.ToDictionary(l => l.Id));
            // Alcance por ubicación: el alcance incluye las sububicaciones, así que
            // poder operar la ubicación es poder operar todo lo que hay debajo.
            if (!(await ctx.ScopeAsync(session)).CanOperate(id))
                return await ctx.OutOfScopeAsync(session, "location", id.ToString(), path, "dar órdenes a las áreas de");
            var subtree = SubtreeOf(id, all);
            var areas = await db.AlarmAreas.AsNoTracking()
                .Where(a => a.LocationId != null && subtree.Contains(a.LocationId.Value) && a.Enabled && a.AlarmPanel.Enabled)
                .OrderBy(a => a.AlarmPanel.Name).ThenBy(a => a.Number)
                .Select(a => new { a.Number, a.Name, a.AlarmPanelId, Panel = a.AlarmPanel.Name })
                .ToListAsync(ct);

            string verb = command switch { "arm-away" => "armar total", "arm-stay" => "armar parcial", _ => "desarmar" };
            if (areas.Count == 0)
                return Results.Ok(new LocationCommandResultDto($"No hay áreas de alarma en {path}.", false, []));
            if (request.DryRun)
                return Results.Ok(new LocationCommandResultDto(
                    $"Se van a {verb} {areas.Count} área(s) de alarma de {path}.", false,
                    areas.Select(a => new LocationCommandItemDto(a.Name, a.Panel, true, null)).ToList()));

            var mode = command == "arm-stay" ? AlarmArmMode.Stay : AlarmArmMode.Away;
            string modeLabel = mode == AlarmArmMode.Stay ? "parcial (en casa)" : "total (fuera)";
            var items = new List<LocationCommandItemDto>();
            foreach (var group in areas.GroupBy(a => a.AlarmPanelId))
            {
                var panel = await db.AlarmPanels.AsNoTracking().Include(p => p.Areas).Include(p => p.Zones)
                    .AsSplitQuery().FirstOrDefaultAsync(p => p.Id == group.Key, ct);
                if (panel is null) continue;
                foreach (var area in group)
                {
                    string action = command == "disarm" ? "area-disarmed" : "area-armed";
                    string target = $"{panel.Name} · el área '{area.Name}'";
                    try
                    {
                        if (command == "disarm")
                            await alarms.ExecuteAsync(panel, (driver, conn) => driver.DisarmAsync(conn, area.Number, ct),
                                AlarmEventKind.Disarm, $"Desarmado desde el VMS (ubicación {path})", area.Number, null,
                                session.Username, ct);
                        else if (panel.PanelTamper)
                            throw new DriverException("la tapa del panel está abierta (sabotaje); ciérrela antes de armar");
                        else
                            await alarms.ExecuteAsync(panel, (driver, conn) => driver.ArmAsync(conn, area.Number, mode, ct),
                                AlarmEventKind.Arm, $"Armado {modeLabel} desde el VMS (ubicación {path})", area.Number, null,
                                session.Username, ct);
                        await audit.LogAsync(ctx, "alarms", action,
                            targetType: "alarm-area", targetId: $"{panel.Id}/{area.Number}", targetName: target,
                            detail: command == "disarm"
                                ? $"Desarmó el área '{area.Name}' del panel '{panel.Name}' (orden sobre la ubicación {path})."
                                : $"Armó ({modeLabel}) el área '{area.Name}' del panel '{panel.Name}' (orden sobre la ubicación {path}).");
                        items.Add(new LocationCommandItemDto(area.Name, panel.Name, true, null));
                    }
                    // Un panel que falla (sin conexión, credenciales, sabotaje) no
                    // puede dejar sin orden a las áreas de los demás paneles.
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await audit.LogAsync(ctx, "alarms", action,
                            targetType: "alarm-area", targetId: $"{panel.Id}/{area.Number}", targetName: target,
                            detail: $"El panel '{panel.Name}' rechazó {verb} el área '{area.Name}' (orden sobre la ubicación {path}): {ex.Message}",
                            success: false);
                        items.Add(new LocationCommandItemDto(area.Name, panel.Name, false, ex.Message));
                    }
                }
            }

            int accepted = items.Count(i => i.Success);
            await audit.LogAsync(ctx, "locations", "location-command",
                targetType: "location", targetId: id.ToString(), targetName: location.Name,
                detail: $"Ordenó {verb} las áreas de alarma de {path}: {accepted} de {items.Count} aceptaron.",
                success: accepted == items.Count);
            return Results.Ok(new LocationCommandResultDto(accepted == items.Count
                    ? $"Se ordenó {verb} {accepted} área(s) de {path}."
                    : $"{accepted} de {items.Count} área(s) de {path} aceptaron {verb}: revise las que fallaron.",
                true, items));
        });

        // Recursos de una ubicación (con sus sububicaciones), los por ubicar, o todos.
        app.MapGet("/api/resources", async (HttpContext ctx, int? locationId, bool? unassigned,
            VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            ResourceCatalog.Filter filter;
            if (unassigned == true)
            {
                // Lo "por ubicar" solo entra en un alcance limitado como recurso suelto de un rol.
                filter = new(UnassignedOnly: true);
            }
            else if (locationId is { } id)
            {
                var all = await db.Locations.AsNoTracking().ToListAsync(ct);
                if (all.All(l => l.Id != id) || !scope.CanSeeLocation(id)) return Results.NotFound();
                filter = new(LocationIds: SubtreeOf(id, all));
            }
            else
                filter = new();
            var resources = await ResourceCatalog.ListAsync(db, filter, ct);
            // Permiso Y alcance: cada tipo solo con un permiso de su módulo (un rol que
            // solo ve video recibe solo cámaras, aunque su ubicación tenga una alarma).
            // Quien arma recursos, alcances de roles y usuarios o tableros los ve todos.
            bool everyKind = new[] { Permissions.ResourcesManage, Permissions.RolesManage, Permissions.UsersManage,
                Permissions.DashboardsManage }.Any(scope.Has);
            var kinds = Enum.GetValues<ResourceKind>()
                .Where(kind => everyKind || ResourcePermissions(kind).Any(scope.Has)).ToHashSet();
            // Alcance limitado: recurso por recurso (sus roles pueden dar recursos sueltos).
            return Results.Ok(resources
                .Where(r => kinds.Contains(r.Kind) && (!scope.FiltersView || scope.CanViewResource(r.Kind, r.Id)))
                .ToList());
        });

        // Ubicar recursos en bloque (LocationId null = devolverlos a "por ubicar").
        app.MapPut("/api/resources/location", async (HttpContext ctx, ResourceLocationRequest request,
            VmsDbContext db, IHubContext<VmsHub> hub, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.ResourcesManage, out _) is { } failure) return failure;
            if (request.Resources is not { Count: > 0 }) return Error("No se indicó ningún recurso.");
            if (request.Resources.Count > MaxBatch)
                return Error($"Demasiados recursos en una sola operación (máximo {MaxBatch}).");

            var byId = await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id, ct);
            if (request.LocationId is { } target && !byId.ContainsKey(target))
                return Error("La ubicación de destino ya no existe.", StatusCodes.Status404NotFound);

            var changed = await ResourceCatalog.SetLocationAsync(db, request.LocationId, request.Resources, ct);
            if (changed.Count == 0)
                return Results.Ok(new ResourceLocationResult(0, "No hubo cambios: los recursos ya estaban ahí."));
            await db.SaveChangesAsync(ct);

            string summary = ResourceCatalog.Summarize(changed.Select(c => c.Kind));
            string names = string.Join(", ", changed.Take(10).Select(c => $"\"{c.Name}\"")) +
                           (changed.Count > 10 ? $" y {changed.Count - 10} más" : "");
            bool located = request.LocationId is not null;
            string path = PathOf(request.LocationId, byId);
            // La lista de claves ("Door:12") en los datos del evento es lo que
            // permite mostrar este cambio en el historial de cada recurso.
            await audit.LogAsync(ctx, "locations", located ? "resources-located" : "resources-unlocated",
                targetType: "location", targetId: request.LocationId?.ToString(), targetName: located ? path : null,
                detail: located ? $"Ubicó {summary} en {path}: {names}." : $"Dejó por ubicar {summary}: {names}.",
                data: new { resources = changed.Select(c => ResourceCatalog.Key(c.Kind, c.Id)).ToList() });
            await AnnounceAsync(ctx, hub, ct);
            return Results.Ok(new ResourceLocationResult(changed.Count, located
                ? $"{changed.Count} recurso(s) ubicado(s) en {path}."
                : $"{changed.Count} recurso(s) quedaron por ubicar."));
        });

        // Una ubicación por equipo: cada recurso va a la ubicación con el nombre
        // de su equipo dentro de ParentId (la que hay o una nueva), y el equipo
        // sin ubicación queda ahí también, para que lo que descubra después la herede.
        app.MapPost("/api/resources/locate-by-equipment", async (HttpContext ctx, ResourceEquipmentLocateRequest request,
            VmsDbContext db, IHubContext<VmsHub> hub, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.ResourcesManage, out _) is { } failure) return failure;
            if (request.Resources is not { Count: > 0 }) return Error("No se indicó ningún recurso.");
            if (request.Resources.Count > MaxBatch)
                return Error($"Demasiados recursos en una sola operación (máximo {MaxBatch}).");

            var all = await db.Locations.AsNoTracking().ToListAsync(ct);
            var byId = all.ToDictionary(l => l.Id);
            if (request.ParentId is { } parentId && !byId.ContainsKey(parentId))
                return Error("La ubicación superior ya no existe.", StatusCodes.Status404NotFound);
            if (DepthOf(request.ParentId, byId) + 1 > MaxDepth)
                return Error($"El árbol admite hasta {MaxDepth} niveles.");

            var wanted = request.Resources.Select(r => ResourceCatalog.Key(r.Kind, r.Id)).ToHashSet();
            var resources = (await ResourceCatalog.ListAsync(db, new ResourceCatalog.Filter(), ct))
                .Where(r => wanted.Contains(ResourceCatalog.Key(r.Kind, r.Id)))
                .ToList();
            if (resources.Count == 0) return Error("Los recursos marcados ya no existen: recargue la página.");

            // Dos equipos que se llaman igual comparten la ubicación.
            var groups = resources
                .GroupBy(EquipmentNameOf, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var plan = groups.Select(g => new EquipmentLocationPlan(g.Key,
                !NameTaken(all, request.ParentId, g.Key, null), g.Count())).ToList();
            string parentPath = PathOf(request.ParentId, byId);
            int toCreate = plan.Count(p => p.Created);
            if (request.DryRun)
                return Results.Ok(new ResourceEquipmentLocateResult(0,
                    $"{resources.Count} recurso(s) de {groups.Count} equipo(s) irían a una ubicación con el nombre " +
                    $"de su equipo en {parentPath}" + (toCreate > 0 ? $" ({toCreate} nueva(s))." : "."), plan));

            int updated = 0;
            foreach (var group in groups)
            {
                var (location, _) = await EnsureLocationAsync(ctx, db, audit, all, byId, group.Key, request.ParentId, ct);
                if (location is null) continue; // solo pasa si el árbol cambió entretanto
                var changed = await ResourceCatalog.SetLocationAsync(db, location.Id,
                    group.Select(r => new ResourceRef(r.Kind, r.Id)), ct);
                await LocateEquipmentAsync(db, group, location.Id, ct);
                await db.SaveChangesAsync(ct);
                if (changed.Count == 0) continue;
                updated += changed.Count;

                string path = PathOf(location.Id, byId);
                string names = string.Join(", ", changed.Take(10).Select(c => $"\"{c.Name}\"")) +
                               (changed.Count > 10 ? $" y {changed.Count - 10} más" : "");
                await audit.LogAsync(ctx, "locations", "resources-located",
                    targetType: "location", targetId: location.Id.ToString(), targetName: path,
                    detail: $"Ubicó {ResourceCatalog.Summarize(changed.Select(c => c.Kind))} del equipo " +
                            $"'{group.Key}' en {path}: {names}.",
                    data: new { resources = changed.Select(c => ResourceCatalog.Key(c.Kind, c.Id)).ToList() });
            }
            await AnnounceAsync(ctx, hub, ct);
            return Results.Ok(new ResourceEquipmentLocateResult(updated,
                $"{updated} recurso(s) ubicado(s) en {groups.Count} ubicación(es) con el nombre de su equipo" +
                (toCreate > 0 ? $" ({toCreate} nueva(s))." : "."), plan));
        });
    }

    /// <summary>Permisos que dejan ver un tipo de recurso (basta uno): los de su módulo, como en el cliente.</summary>
    private static string[] ResourcePermissions(ResourceKind kind) => kind switch
    {
        ResourceKind.Camera => [Permissions.LiveView, Permissions.PlaybackView, Permissions.DevicesManage],
        ResourceKind.Door => [Permissions.AccessMonitor, Permissions.AccessDoors, Permissions.AccessConfigure],
        ResourceKind.Partition or ResourceKind.Zone =>
            [Permissions.AlarmsMonitor, Permissions.AlarmsOperate, Permissions.AlarmsConfigure, Permissions.LocationsCommand],
        ResourceKind.Fence => [Permissions.CercoMonitor, Permissions.CercoOperate, Permissions.CercoConfigure],
        ResourceKind.Speaker => [Permissions.SpeakersPlay, Permissions.SpeakersConfigure],
        ResourceKind.Intercom => [Permissions.IntercomAnswer, Permissions.IntercomConfigure],
        ResourceKind.IoModule => [Permissions.IoOperate, Permissions.IoConfigure],
        _ => [],
    };

    /// <summary>
    /// El equipo de un recurso, para «una ubicación por equipo»: el grabador de
    /// un canal, la controladora de una puerta, el panel de un área o zona; el
    /// recurso mismo si es el equipo (parlante, citófono, cerco, módulo de E/S).
    /// </summary>
    private static string EquipmentNameOf(ResourceDto resource)
    {
        string name = resource.Kind is ResourceKind.Camera or ResourceKind.Door
            or ResourceKind.Partition or ResourceKind.Zone ? resource.Source : resource.Name;
        name = name.Trim();
        return name.Length == 0 ? "(sin nombre)" : name.Length > 128 ? name[..128] : name;
    }

    /// <summary>
    /// La ubicación <paramref name="name"/> dentro de <paramref name="parentId"/>:
    /// la que ya existe (sin distinguir mayúsculas) o una nueva de tipo sector,
    /// que se guarda y queda en la bitácora. Mantiene al día
    /// <paramref name="all"/> y <paramref name="byId"/>. Null si el árbol ya
    /// no admite otro nivel ahí.
    /// </summary>
    private static async Task<(Location? Location, bool Created)> EnsureLocationAsync(HttpContext ctx,
        VmsDbContext db, AuditService audit, List<Location> all, Dictionary<int, Location> byId,
        string name, int? parentId, CancellationToken ct)
    {
        var existing = all.FirstOrDefault(l => l.ParentId == parentId &&
                                              string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return (existing, false);
        if (DepthOf(parentId, byId) + 1 > MaxDepth) return (null, false);

        var location = new Location
        {
            ParentId = parentId, Name = name, Kind = LocationKind.Sector, SortOrder = NextSortOrder(all, parentId),
        };
        db.Locations.Add(location);
        await db.SaveChangesAsync(ct);
        all.Add(location);
        byId[location.Id] = location;
        await audit.LogAsync(ctx, "locations", "location-created",
            targetType: "location", targetId: location.Id.ToString(), targetName: location.Name,
            detail: $"Creó la ubicación \"{location.Name}\" ({KindLabel(location.Kind)}) en {PathOf(parentId, byId)} " +
                    "con el nombre del equipo.");
        return (location, true);
    }

    /// <summary>
    /// El grabador, la controladora o el panel de estos recursos queda en la
    /// ubicación si no tenía: así heredan esa ubicación los canales, puertas o
    /// áreas que descubra después. No guarda.
    /// </summary>
    private static async Task LocateEquipmentAsync(VmsDbContext db, IEnumerable<ResourceDto> resources,
        int locationId, CancellationToken ct)
    {
        foreach (var module in resources.GroupBy(r => r.SourceModule))
        {
            var ids = module.Select(r => r.SourceId).Distinct().ToList();
            switch (module.Key)
            {
                case "devices":
                    foreach (var d in await db.Devices.Where(d => ids.Contains(d.Id) && d.LocationId == null).ToListAsync(ct))
                        d.LocationId = locationId;
                    break;
                case "access":
                    foreach (var d in await db.AccessDevices.Where(d => ids.Contains(d.Id) && d.LocationId == null).ToListAsync(ct))
                        d.LocationId = locationId;
                    break;
                case "alarm-panels":
                    foreach (var p in await db.AlarmPanels.Where(p => ids.Contains(p.Id) && p.LocationId == null).ToListAsync(ct))
                        p.LocationId = locationId;
                    break;
            }
        }
    }
}
