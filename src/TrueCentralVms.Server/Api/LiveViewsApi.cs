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
/// Tableros del Centro de mando (antes "vistas guardadas"): división de la
/// grilla + lo que hay en cada cuadro (cámara, recurso, tarjeta o página web).
/// Cada usuario ve los suyos y los compartidos con todos o con alguno de sus
/// roles. Los personales los crea y cambia su dueño con "Crear tableros
/// propios"; los compartidos, solo quien tiene "Administrar tableros
/// compartidos" (así un guardia no cambia ni quita lo que el turno necesita).
/// También la lista de páginas web que se pueden poner en un cuadro.
/// </summary>
public static class LiveViewsApi
{
    /// <summary>Tope de cuadros por tablero: la división más grande del cliente (64).</summary>
    private const int MaxCells = 64;

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    /// <summary>Lo que necesita armar la respuesta: roles por id y páginas de la lista.</summary>
    private sealed record Lookup(IReadOnlyDictionary<int, string> Roles, IReadOnlyDictionary<int, WebLink> Links);

    private static async Task<Lookup> LookupAsync(VmsDbContext db, CancellationToken ct) => new(
        await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name, ct),
        await db.WebLinks.AsNoTracking().ToDictionaryAsync(l => l.Id, ct));

    private static Task<List<int>> MyRolesAsync(VmsDbContext db, SessionInfo session, CancellationToken ct) =>
        db.UserRoles.Where(r => r.UserId == session.UserId).Select(r => r.RoleId).ToListAsync(ct);

    /// <summary>
    /// Los compartidos los cambia quien administra los tableros compartidos;
    /// los personales, su dueño mientras pueda crear tableros propios.
    /// </summary>
    private static bool CanEdit(LiveView view, SessionInfo session, UserScope scope) => view.Shared
        ? scope.Has(Permissions.DashboardsManage)
        : view.OwnerUserId == session.UserId && scope.Has(Permissions.LiveViewsManage);

    /// <summary>Lo ve: es suyo, o es compartido con todos, con uno de sus roles o él los administra.</summary>
    private static IQueryable<LiveView> Visible(IQueryable<LiveView> views, SessionInfo session,
        UserScope scope, List<int> myRoles)
    {
        bool manager = scope.Has(Permissions.DashboardsManage);
        return views.Where(v => v.OwnerUserId == session.UserId ||
                                (v.Shared && (manager || !v.RestrictToRoles ||
                                              v.Roles.Any(r => myRoles.Contains(r.RoleId)))));
    }

    /// <summary>
    /// Un tablero compartido puede tener cámaras y recursos de otras
    /// ubicaciones: cada uno ve lo de su alcance (el resto queda como cuadro
    /// libre). Las páginas de la lista se leen al día (nombre y dirección).
    /// </summary>
    private static LiveViewDto ToDto(LiveView view, SessionInfo session, UserScope scope, Lookup lookup)
    {
        var items = new List<LiveViewItemDto>();
        foreach (var item in view.Items.OrderBy(i => i.CellIndex))
        {
            switch (item.Kind)
            {
                case DashboardItemKinds.Resource:
                    if (Enum.TryParse<ResourceKind>(item.Ref, out var kind) && item.RefId is { } id && scope.CanViewResource(kind, id))
                        items.Add(new(item.CellIndex, 0, 0, item.Kind, id, item.Ref, item.Title));
                    break;
                case DashboardItemKinds.Card:
                    items.Add(new(item.CellIndex, 0, 0, item.Kind, null, item.Ref, item.Title));
                    break;
                case DashboardItemKinds.Web when item.RefId is { } linkId:
                    if (lookup.Links.TryGetValue(linkId, out var link))
                        items.Add(new(item.CellIndex, 0, 0, item.Kind, linkId, link.Url, link.Name, item.Zoom));
                    break;
                case DashboardItemKinds.Web:
                    items.Add(new(item.CellIndex, 0, 0, item.Kind, null, item.Ref, item.Title, item.Zoom));
                    break;
                default:
                    if (item.ChannelId is { } channelId && scope.CanViewChannel(channelId))
                        items.Add(new(item.CellIndex, channelId, item.StreamType, DashboardItemKinds.Camera));
                    break;
            }
        }
        var roleIds = view.Roles.Select(r => r.RoleId).Order().ToList();
        return new LiveViewDto(
            view.Id, view.Name, view.LayoutName, view.Columns, view.Rows,
            view.Shared, view.OwnerName, CanEdit(view, session, scope), view.UpdatedAt, items,
            view.RestrictToRoles ? roleIds : [],
            view.RestrictToRoles ? roleIds.Select(id => lookup.Roles.GetValueOrDefault(id, $"rol #{id}")).ToList() : [],
            !view.Shared && view.OwnerUserId == session.UserId, view.Shared && view.RestrictToRoles);
    }

    /// <summary>División guardada, acotada a lo que el cliente sabe dibujar.</summary>
    private static string LayoutNameOf(LiveViewSaveRequest request)
    {
        string name = (request.LayoutName ?? "").Trim();
        return name.Length is > 0 and <= 32 ? name : "4";
    }

    /// <summary>Dirección web aceptable para un cuadro: absoluta, http o https.</summary>
    internal static string? CleanUrl(string? url)
    {
        string text = (url ?? "").Trim();
        return text.Length is > 0 and <= 2048 &&
               Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.ToString() : null;
    }

    private static string? Clip(string? text, int max)
    {
        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }

    /// <summary>
    /// Deja la petición en una lista de cuadros utilizable: sin índices fuera
    /// de la grilla ni dos cosas en el mismo cuadro, sin cámaras ni recursos
    /// que ya no estén en el inventario o fuera de su alcance, sin tarjetas
    /// desconocidas ni direcciones web inválidas. Una dirección libre nueva
    /// pide "Abrir cualquier página web" (las que el tablero ya tenía se conservan).
    /// </summary>
    private static async Task<(List<LiveViewItem> Items, string? Error)> BuildItemsAsync(
        LiveViewSaveRequest request, int cellCount, VmsDbContext db, UserScope scope,
        IReadOnlyCollection<string> previousUrls, CancellationToken ct)
    {
        var wanted = (request.Items ?? [])
            .Where(i => i.CellIndex >= 0 && i.CellIndex < cellCount)
            .GroupBy(i => i.CellIndex)
            .Select(g => g.First())
            .ToList();
        if (wanted.Count == 0) return ([], null);

        var channelIds = wanted.Where(i => DashboardItemKinds.IsCamera(i.Kind)).Select(i => i.ChannelId).Distinct().ToList();
        var knownChannels = await db.Channels.Where(c => channelIds.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct);
        var linkIds = wanted.Where(i => i.Kind == DashboardItemKinds.Web && i.RefId is not null).Select(i => i.RefId!.Value).ToList();
        var knownLinks = await db.WebLinks.Where(l => linkIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);

        var items = new List<LiveViewItem>();
        foreach (var i in wanted)
        {
            switch (i.Kind)
            {
                case null or "" or DashboardItemKinds.Camera:
                    if (knownChannels.Contains(i.ChannelId) && scope.CanViewChannel(i.ChannelId))
                        items.Add(new LiveViewItem
                        {
                            CellIndex = i.CellIndex, Kind = DashboardItemKinds.Camera,
                            ChannelId = i.ChannelId, StreamType = i.StreamType == 1 ? 1 : 0,
                        });
                    break;
                case DashboardItemKinds.Resource:
                    if (!Enum.TryParse<ResourceKind>(i.Ref, out var kind) || !Enum.IsDefined(kind) || kind == ResourceKind.Camera ||
                        i.RefId is not { } id || !scope.CanViewResource(kind, id) ||
                        await ResourceCatalog.FindAsync(db, kind, id, ct) is null)
                        break;
                    items.Add(new LiveViewItem
                    {
                        CellIndex = i.CellIndex, Kind = DashboardItemKinds.Resource,
                        Ref = kind.ToString(), RefId = id, Title = Clip(i.Title, 128),
                    });
                    break;
                case DashboardItemKinds.Card:
                    if (DashboardItemKinds.Cards.Contains(i.Ref ?? ""))
                        items.Add(new LiveViewItem { CellIndex = i.CellIndex, Kind = DashboardItemKinds.Card, Ref = i.Ref });
                    break;
                case DashboardItemKinds.Web when i.RefId is { } linkId:
                    if (knownLinks.TryGetValue(linkId, out var linkName))
                        items.Add(new LiveViewItem
                        {
                            CellIndex = i.CellIndex, Kind = DashboardItemKinds.Web, RefId = linkId, Title = linkName,
                            Zoom = DashboardItemKinds.CleanZoom(i.Zoom),
                        });
                    break;
                case DashboardItemKinds.Web:
                    if (CleanUrl(i.Ref) is not { } url)
                        return ([], $"La dirección web del cuadro {i.CellIndex + 1} no es válida (debe empezar con http:// o https://).");
                    if (!scope.Has(Permissions.DashboardsWebUrl) && !previousUrls.Contains(url))
                        return ([], "Sus roles no incluyen \"Abrir cualquier página web\": use una página de la lista del servidor.");
                    items.Add(new LiveViewItem
                    {
                        CellIndex = i.CellIndex, Kind = DashboardItemKinds.Web, Ref = url,
                        Title = Clip(i.Title, 128) ?? new Uri(url).Host,
                        Zoom = DashboardItemKinds.CleanZoom(i.Zoom),
                    });
                    break;
            }
        }
        return (items, null);
    }

    /// <summary>Valida nombre y división; devuelve el error a retornar o null.</summary>
    private static IResult? Validate(LiveViewSaveRequest request, out int cellCount)
    {
        cellCount = 0;
        if (string.IsNullOrWhiteSpace(request.Name))
            return Error("El nombre del tablero es obligatorio.");
        if (request.Name.Trim().Length > 128)
            return Error("El nombre del tablero no puede superar los 128 caracteres.");
        if (request.Columns is < 1 or > 16 || request.Rows is < 1 or > 16)
            return Error("La división guardada no es válida.");
        cellCount = Math.Min(request.Columns * request.Rows, MaxCells);
        return null;
    }

    /// <summary>Roles pedidos que existen (vacío = compartido con todos).</summary>
    private static async Task<List<int>> RolesOfAsync(LiveViewSaveRequest request, VmsDbContext db, CancellationToken ct)
    {
        var ids = (request.RoleIds ?? []).Distinct().ToList();
        return ids.Count == 0 ? [] : await db.Roles.Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct);
    }

    private static string Audience(LiveView view, Lookup lookup) =>
        !view.Shared ? "personal"
        : !view.RestrictToRoles ? "compartido con todos"
        : view.Roles.Count == 0 ? "compartido sin roles (solo administradores de tableros)"
        : "compartido con " + string.Join(", ", view.Roles.Select(r => lookup.Roles.GetValueOrDefault(r.RoleId, $"rol #{r.RoleId}")));

    private static string Contents(LiveView view)
    {
        var parts = view.Items.GroupBy(i => i.Kind).Select(g => g.Key switch
        {
            DashboardItemKinds.Resource => $"{g.Count()} recurso(s)",
            DashboardItemKinds.Card => $"{g.Count()} tarjeta(s)",
            DashboardItemKinds.Web => $"{g.Count()} página(s) web",
            _ => $"{g.Count()} cámara(s)",
        });
        return string.Join(", ", parts.DefaultIfEmpty("sin cuadros"));
    }

    /// <summary>Los compartidos cambiaron: los puestos rehacen su lista de tableros.</summary>
    private static Task AnnounceAsync(HttpContext ctx, string topic, CancellationToken ct) =>
        ctx.RequestServices.GetRequiredService<IHubContext<VmsHub>>()
            .Clients.All.SendAsync(VmsHubContract.ConfigChanged, topic, ct);

    public static void MapLiveViewsApi(this WebApplication app)
    {
        // Los propios del usuario + los compartidos que le tocan.
        app.MapGet("/api/live-views", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            // Flota usa los mismos tableros (con cámaras de vehículos): basta con poder ver su video.
            if (ApiSecurity.RequireAny(ctx, out var session, Permissions.LiveView, Permissions.WallOperate, Permissions.FleetLive) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var myRoles = await MyRolesAsync(db, session, ct);
            var views = await Visible(db.LiveViews.Include(v => v.Items).Include(v => v.Roles), session, scope, myRoles)
                .OrderBy(v => v.Name)
                .AsSplitQuery()
                .AsNoTracking()
                .ToListAsync(ct);
            var lookup = await LookupAsync(db, ct);
            return Results.Ok(views.Select(v => ToDto(v, session, scope, lookup)));
        });

        // Roles a los que se puede asignar un tablero compartido.
        app.MapGet("/api/live-views/roles", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.DashboardsManage, out _) is { } failure) return failure;
            var roles = await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
            return Results.Ok(roles.Select(r => new DashboardRoleDto(r.Id, r.Name)));
        });

        app.MapPost("/api/live-views", async (HttpContext ctx, LiveViewSaveRequest request,
            VmsDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.LiveViewsManage, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            if (Validate(request, out int cellCount) is { } invalid) return invalid;
            if (request.Shared && !scope.Has(Permissions.DashboardsManage))
                return Error("Sus roles no incluyen \"Administrar tableros compartidos\": guárdelo como tablero propio.",
                    StatusCodes.Status403Forbidden);

            string name = request.Name.Trim();
            if (request.Shared
                    ? await db.LiveViews.AnyAsync(v => v.Shared && v.Name == name, ct)
                    : await db.LiveViews.AnyAsync(v => !v.Shared && v.OwnerUserId == session.UserId && v.Name == name, ct))
                return Error(request.Shared ? "Ya hay un tablero compartido con ese nombre." : "Ya tiene un tablero con ese nombre.",
                    StatusCodes.Status409Conflict);

            var (items, itemsError) = await BuildItemsAsync(request, cellCount, db, scope, [], ct);
            if (itemsError is not null) return Error(itemsError);
            var roleIds = request.Shared ? await RolesOfAsync(request, db, ct) : [];
            var view = new LiveView
            {
                Name = name,
                OwnerUserId = session.UserId,
                OwnerName = session.Username,
                Shared = request.Shared,
                RestrictToRoles = roleIds.Count > 0,
                Roles = roleIds.Select(id => new LiveViewRole { RoleId = id }).ToList(),
                LayoutName = LayoutNameOf(request),
                Columns = request.Columns,
                Rows = request.Rows,
                Items = items,
            };
            db.LiveViews.Add(view);
            await db.SaveChangesAsync(ct);
            var lookup = await LookupAsync(db, ct);
            await audit.LogAsync(ctx, "live", "custom-view-saved",
                targetType: "live-view", targetId: view.Id.ToString(), targetName: view.Name,
                detail: $"Guardó el tablero \"{view.Name}\" ({Audience(view, lookup)}; división {view.LayoutName}; {Contents(view)}).");
            if (view.Shared) await AnnounceAsync(ctx, "live-views", ct);
            return Results.Ok(ToDto(view, session, scope, lookup));
        });

        // Reemplaza el contenido de un tablero: sirve para renombrarlo, cambiar
        // con quién se comparte y actualizarlo con lo que hay ahora en la grilla.
        app.MapPut("/api/live-views/{id:int}", async (HttpContext ctx, int id, LiveViewSaveRequest request,
            VmsDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.LiveViewsManage, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            if (Validate(request, out int cellCount) is { } invalid) return invalid;

            var view = await db.LiveViews.Include(v => v.Items).Include(v => v.Roles)
                .AsSplitQuery().FirstOrDefaultAsync(v => v.Id == id, ct);
            if (view is null) return Results.NotFound();
            if (!CanEdit(view, session, scope))
                return Error(view.Shared
                    ? "Es un tablero compartido: solo lo cambia quien administra los tableros compartidos."
                    : "El tablero es de otro usuario.", StatusCodes.Status403Forbidden);
            if (request.Shared != view.Shared && !scope.Has(Permissions.DashboardsManage))
                return Error("Sus roles no incluyen \"Administrar tableros compartidos\".", StatusCodes.Status403Forbidden);

            string name = request.Name.Trim();
            if (request.Shared
                    ? await db.LiveViews.AnyAsync(v => v.Id != id && v.Shared && v.Name == name, ct)
                    : await db.LiveViews.AnyAsync(v => v.Id != id && !v.Shared && v.OwnerUserId == view.OwnerUserId && v.Name == name, ct))
                return Error("Ya existe un tablero con ese nombre.", StatusCodes.Status409Conflict);

            var previousUrls = view.Items.Where(i => i.Kind == DashboardItemKinds.Web && i.Ref is not null)
                .Select(i => i.Ref!).ToList();
            var (items, itemsError) = await BuildItemsAsync(request, cellCount, db, scope, previousUrls, ct);
            if (itemsError is not null) return Error(itemsError);

            bool wasShared = view.Shared;
            view.Name = name;
            view.Shared = request.Shared;
            // Con quién se comparte (solo llega aquí un compartido si administra los compartidos).
            var roleIds = request.Shared ? await RolesOfAsync(request, db, ct) : [];
            view.RestrictToRoles = roleIds.Count > 0;
            foreach (var gone in view.Roles.Where(r => !roleIds.Contains(r.RoleId)).ToList())
                view.Roles.Remove(gone);
            foreach (var added in roleIds.Where(r => view.Roles.All(x => x.RoleId != r)))
                view.Roles.Add(new LiveViewRole { LiveViewId = view.Id, RoleId = added });
            view.LayoutName = LayoutNameOf(request);
            view.Columns = request.Columns;
            view.Rows = request.Rows;
            view.UpdatedAt = DateTime.UtcNow;
            db.LiveViewItems.RemoveRange(view.Items);
            view.Items = items;
            await db.SaveChangesAsync(ct);
            var lookup = await LookupAsync(db, ct);
            await audit.LogAsync(ctx, "live", "custom-view-updated",
                targetType: "live-view", targetId: view.Id.ToString(), targetName: view.Name,
                detail: $"Actualizó el tablero \"{view.Name}\" ({Audience(view, lookup)}; división {view.LayoutName}; {Contents(view)}).");
            if (view.Shared || wasShared) await AnnounceAsync(ctx, "live-views", ct);
            return Results.Ok(ToDto(view, session, scope, lookup));
        });

        app.MapDelete("/api/live-views/{id:int}", async (HttpContext ctx, int id,
            VmsDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.LiveViewsManage, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var view = await db.LiveViews.FirstOrDefaultAsync(v => v.Id == id, ct);
            if (view is null) return Results.NotFound();
            if (!CanEdit(view, session, scope))
                return Error(view.Shared
                    ? "Es un tablero compartido: solo lo borra quien administra los tableros compartidos."
                    : "El tablero es de otro usuario.", StatusCodes.Status403Forbidden);

            db.LiveViews.Remove(view);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "live", "custom-view-deleted",
                targetType: "live-view", targetId: id.ToString(), targetName: view.Name,
                detail: $"Eliminó el tablero {(view.Shared ? "compartido " : "")}\"{view.Name}\".");
            if (view.Shared) await AnnounceAsync(ctx, "live-views", ct);
            return Results.Ok();
        });

        // El puesto arma la grilla por su cuenta; pasa por aquí para pedir la
        // versión vigente (pudieron cambiar un tablero compartido) y para que
        // la bitácora registre quién cargó qué tablero y cuándo.
        app.MapPost("/api/live-views/{id:int}/apply", async (HttpContext ctx, int id,
            VmsDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAny(ctx, out var session, Permissions.LiveView, Permissions.WallOperate, Permissions.FleetLive) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var myRoles = await MyRolesAsync(db, session, ct);
            var view = await Visible(db.LiveViews.Include(v => v.Items).Include(v => v.Roles), session, scope, myRoles)
                .AsSplitQuery().AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == id, ct);
            if (view is null) return Results.NotFound();
            await audit.LogAsync(ctx, "live", "custom-view-applied",
                targetType: "live-view", targetId: view.Id.ToString(), targetName: view.Name,
                detail: $"Cargó el tablero \"{view.Name}\" ({Contents(view)}).");
            return Results.Ok(ToDto(view, session, scope, await LookupAsync(db, ct)));
        });

        MapWebLinks(app);
    }

    // -----------------------------------------------------------------------
    // Páginas web de la lista del servidor
    // -----------------------------------------------------------------------

    private static WebLinkDto ToDto(WebLink link) => new(link.Id, link.Name, link.Url, link.Description);

    private static IResult? Validate(WebLinkSaveRequest request, out string name, out string url)
    {
        name = (request.Name ?? "").Trim();
        url = CleanUrl(request.Url) ?? "";
        if (name.Length == 0) return Error("El nombre es obligatorio.");
        if (name.Length > 128) return Error("El nombre no puede superar los 128 caracteres.");
        if (url.Length == 0) return Error("La dirección debe ser completa y empezar con http:// o https://.");
        if ((request.Description?.Trim().Length ?? 0) > 500) return Error("La descripción no puede superar los 500 caracteres.");
        return null;
    }

    private static void MapWebLinks(WebApplication app)
    {
        app.MapGet("/api/web-links", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAny(ctx, out _, Permissions.LiveView, Permissions.DashboardsManage) is { } failure) return failure;
            var links = await db.WebLinks.AsNoTracking().OrderBy(l => l.Name).ToListAsync(ct);
            return Results.Ok(links.Select(ToDto));
        });

        app.MapPost("/api/web-links", async (HttpContext ctx, WebLinkSaveRequest request, VmsDbContext db,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.DashboardsManage, out _) is { } failure) return failure;
            if (Validate(request, out var name, out var url) is { } invalid) return invalid;
            if (await db.WebLinks.AnyAsync(l => l.Name == name, ct))
                return Error("Ya hay una página con ese nombre.", StatusCodes.Status409Conflict);
            var link = new WebLink { Name = name, Url = url, Description = Clip(request.Description, 500) };
            db.WebLinks.Add(link);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "live", "web-link-created",
                targetType: "web-link", targetId: link.Id.ToString(), targetName: link.Name,
                detail: $"Agregó la página web \"{link.Name}\" ({link.Url}) a la lista de los tableros.");
            await AnnounceAsync(ctx, "web-links", ct);
            return Results.Ok(ToDto(link));
        });

        app.MapPut("/api/web-links/{id:int}", async (HttpContext ctx, int id, WebLinkSaveRequest request,
            VmsDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.DashboardsManage, out _) is { } failure) return failure;
            if (Validate(request, out var name, out var url) is { } invalid) return invalid;
            var link = await db.WebLinks.FirstOrDefaultAsync(l => l.Id == id, ct);
            if (link is null) return Results.NotFound();
            if (await db.WebLinks.AnyAsync(l => l.Id != id && l.Name == name, ct))
                return Error("Ya hay una página con ese nombre.", StatusCodes.Status409Conflict);
            string before = link.Url;
            link.Name = name;
            link.Url = url;
            link.Description = Clip(request.Description, 500);
            link.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "live", "web-link-updated",
                targetType: "web-link", targetId: link.Id.ToString(), targetName: link.Name,
                detail: $"Modificó la página web \"{link.Name}\"" +
                        (before != link.Url ? $": dirección {before} → {link.Url}." : "."));
            await AnnounceAsync(ctx, "web-links", ct);
            return Results.Ok(ToDto(link));
        });

        // Los cuadros de tableros que la usaban quedan libres.
        app.MapDelete("/api/web-links/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.DashboardsManage, out _) is { } failure) return failure;
            var link = await db.WebLinks.FirstOrDefaultAsync(l => l.Id == id, ct);
            if (link is null) return Results.NotFound();
            var cells = await db.LiveViewItems.Where(i => i.Kind == DashboardItemKinds.Web && i.RefId == id).ToListAsync(ct);
            db.LiveViewItems.RemoveRange(cells);
            db.WebLinks.Remove(link);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "live", "web-link-deleted",
                targetType: "web-link", targetId: id.ToString(), targetName: link.Name,
                detail: $"Quitó la página web \"{link.Name}\" de la lista" +
                        (cells.Count > 0 ? $"; {cells.Count} cuadro(s) de tableros quedaron libres." : "."));
            await AnnounceAsync(ctx, "web-links", ct);
            return Results.Ok();
        });
    }
}
