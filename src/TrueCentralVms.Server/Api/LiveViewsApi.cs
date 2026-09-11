using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Vistas guardadas del monitoreo en vivo (las "Custom View" de iVMS-4200):
/// división de la grilla + canal de cada cuadro. Cada operador ve las suyas y
/// las que estén marcadas como compartidas; modificarlas o borrarlas solo
/// puede su dueño (o un administrador).
/// </summary>
public static class LiveViewsApi
{
    /// <summary>Tope de cuadros por vista: la división más grande del cliente (64).</summary>
    private const int MaxCells = 64;

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    private static bool CanEdit(LiveView view, SessionInfo session) =>
        view.OwnerUserId == session.UserId || session.Role == Roles.Admin;

    private static LiveViewDto ToDto(LiveView view, SessionInfo session) => new(
        view.Id, view.Name, view.LayoutName, view.Columns, view.Rows,
        view.Shared, view.OwnerName, CanEdit(view, session), view.UpdatedAt,
        view.Items.OrderBy(i => i.CellIndex)
            .Select(i => new LiveViewItemDto(i.CellIndex, i.ChannelId, i.StreamType))
            .ToList());

    /// <summary>División guardada, acotada a lo que el cliente sabe dibujar.</summary>
    private static string LayoutNameOf(LiveViewSaveRequest request)
    {
        string name = (request.LayoutName ?? "").Trim();
        return name.Length is > 0 and <= 32 ? name : "4";
    }

    /// <summary>
    /// Deja la petición en una lista de cuadros utilizable: sin índices fuera
    /// de la grilla, sin dos cámaras en el mismo cuadro y sin canales que ya no
    /// estén en el inventario (el operador pudo guardar la vista con un equipo
    /// que después se dio de baja).
    /// </summary>
    private static async Task<List<LiveViewItem>> BuildItemsAsync(
        LiveViewSaveRequest request, int cellCount, VmsDbContext db, CancellationToken ct)
    {
        var wanted = (request.Items ?? [])
            .Where(i => i.CellIndex >= 0 && i.CellIndex < cellCount)
            .GroupBy(i => i.CellIndex)
            .Select(g => g.First())
            .ToList();
        if (wanted.Count == 0) return [];

        var ids = wanted.Select(i => i.ChannelId).Distinct().ToList();
        var known = await db.Channels.Where(c => ids.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct);
        return wanted
            .Where(i => known.Contains(i.ChannelId))
            .Select(i => new LiveViewItem
            {
                CellIndex = i.CellIndex,
                ChannelId = i.ChannelId,
                StreamType = i.StreamType == 1 ? 1 : 0,
            })
            .ToList();
    }

    /// <summary>Valida nombre y división; devuelve el error a retornar o null.</summary>
    private static IResult? Validate(LiveViewSaveRequest request, out int cellCount)
    {
        cellCount = 0;
        if (string.IsNullOrWhiteSpace(request.Name))
            return Error("El nombre de la vista es obligatorio.");
        if (request.Name.Trim().Length > 128)
            return Error("El nombre de la vista no puede superar los 128 caracteres.");
        if (request.Columns is < 1 or > 16 || request.Rows is < 1 or > 16)
            return Error("La división guardada no es válida.");
        cellCount = Math.Min(request.Columns * request.Rows, MaxCells);
        return null;
    }

    public static void MapLiveViewsApi(this WebApplication app)
    {
        // Las propias del usuario + las compartidas por cualquiera.
        app.MapGet("/api/live-views", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var views = await db.LiveViews
                .Include(v => v.Items)
                .Where(v => v.OwnerUserId == session.UserId || v.Shared)
                .OrderBy(v => v.Name)
                .AsSplitQuery()
                .AsNoTracking()
                .ToListAsync(ct);
            return Results.Ok(views.Select(v => ToDto(v, session)));
        });

        app.MapPost("/api/live-views", async (HttpContext ctx, LiveViewSaveRequest request,
            VmsDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            if (Validate(request, out int cellCount) is { } invalid) return invalid;

            string name = request.Name.Trim();
            if (await db.LiveViews.AnyAsync(v => v.OwnerUserId == session.UserId && v.Name == name, ct))
                return Error("Ya tiene una vista guardada con ese nombre.", StatusCodes.Status409Conflict);

            var view = new LiveView
            {
                Name = name,
                OwnerUserId = session.UserId,
                OwnerName = session.Username,
                Shared = request.Shared,
                LayoutName = LayoutNameOf(request),
                Columns = request.Columns,
                Rows = request.Rows,
                Items = await BuildItemsAsync(request, cellCount, db, ct),
            };
            db.LiveViews.Add(view);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "live", "custom-view-saved",
                targetType: "live-view", targetId: view.Id.ToString(), targetName: view.Name,
                detail: $"Guardó la vista \"{view.Name}\" (división {view.LayoutName}, " +
                        $"{view.Items.Count} cámara(s))" +
                        (view.Shared ? ", compartida con todos los puestos." : "."));
            return Results.Ok(ToDto(view, session));
        });

        // Reemplaza el contenido de una vista: sirve para renombrarla y para
        // actualizarla con lo que hay ahora en la grilla.
        app.MapPut("/api/live-views/{id:int}", async (HttpContext ctx, int id, LiveViewSaveRequest request,
            VmsDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            if (Validate(request, out int cellCount) is { } invalid) return invalid;

            var view = await db.LiveViews.Include(v => v.Items).FirstOrDefaultAsync(v => v.Id == id, ct);
            if (view is null) return Results.NotFound();
            if (!CanEdit(view, session))
                return Error("La vista es de otro usuario.", StatusCodes.Status403Forbidden);

            string name = request.Name.Trim();
            if (await db.LiveViews.AnyAsync(v => v.Id != id && v.OwnerUserId == view.OwnerUserId && v.Name == name, ct))
                return Error("Ya existe una vista con ese nombre.", StatusCodes.Status409Conflict);

            view.Name = name;
            view.Shared = request.Shared;
            view.LayoutName = LayoutNameOf(request);
            view.Columns = request.Columns;
            view.Rows = request.Rows;
            view.UpdatedAt = DateTime.UtcNow;
            db.LiveViewItems.RemoveRange(view.Items);
            view.Items = await BuildItemsAsync(request, cellCount, db, ct);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "live", "custom-view-updated",
                targetType: "live-view", targetId: view.Id.ToString(), targetName: view.Name,
                detail: $"Actualizó la vista \"{view.Name}\" (división {view.LayoutName}, " +
                        $"{view.Items.Count} cámara(s)).");
            return Results.Ok(ToDto(view, session));
        });

        app.MapDelete("/api/live-views/{id:int}", async (HttpContext ctx, int id,
            VmsDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var view = await db.LiveViews.FirstOrDefaultAsync(v => v.Id == id, ct);
            if (view is null) return Results.NotFound();
            if (!CanEdit(view, session))
                return Error("La vista es de otro usuario.", StatusCodes.Status403Forbidden);

            db.LiveViews.Remove(view);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "live", "custom-view-deleted",
                targetType: "live-view", targetId: id.ToString(), targetName: view.Name,
                detail: $"Eliminó la vista \"{view.Name}\".");
            return Results.Ok();
        });

        // El puesto arma la grilla por su cuenta; pasa por aquí para pedir la
        // versión vigente (otro operador pudo actualizar una vista compartida)
        // y para que la bitácora registre quién cargó qué vista y cuándo.
        app.MapPost("/api/live-views/{id:int}/apply", async (HttpContext ctx, int id,
            VmsDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var view = await db.LiveViews.Include(v => v.Items)
                .AsSplitQuery().AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == id && (v.OwnerUserId == session.UserId || v.Shared), ct);
            if (view is null) return Results.NotFound();
            await audit.LogAsync(ctx, "live", "custom-view-applied",
                targetType: "live-view", targetId: view.Id.ToString(), targetName: view.Name,
                detail: $"Cargó la vista \"{view.Name}\" ({view.Items.Count} cámara(s)).");
            return Results.Ok(ToDto(view, session));
        });
    }
}
