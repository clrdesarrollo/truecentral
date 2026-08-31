using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Muros de video: estructura (solo administrador) y operación (cualquier
/// sesión). Toda la lógica contra el equipo vive en <see cref="WallService"/>;
/// aquí solo se validan entradas y se traducen excepciones a códigos HTTP.
/// </summary>
public static class WallsApi
{
    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    /// <summary>
    /// Ejecuta una operación del muro traduciendo sus excepciones esperadas:
    /// 404 si el muro/ventana no existe, 422 si el equipo o los datos la
    /// rechazan.
    /// </summary>
    private static async Task<IResult> RunAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return Results.Ok(await operation());
        }
        catch (KeyNotFoundException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }
    }

    private static LayoutPresetDto ToDto(WallLayoutPreset l) => new(
        l.Id, l.Name, l.CreatedAt,
        l.Screens.Select(s => new LayoutScreenDto(s.Row, s.Col, s.WindowMode)).ToList(),
        l.Items
            .Select(i => new LayoutItemDto(i.Row, i.Col, i.WindowIndex, i.ChannelId, i.StreamType,
                Math.Max(1, i.SpanCols), Math.Max(1, i.SpanRows)))
            .ToList());

    /// <summary>
    /// Asigna canales de decodificación automáticamente: recorre las pantallas
    /// en orden (fila, columna) y entrega canales consecutivos del pool del
    /// decoder a cada sub-ventana. El operador nunca elige canales a mano.
    /// </summary>
    private static async Task<(List<WallScreen> Screens, string? Warning)> BuildScreensAsync(
        DecoderSessionManager sessions, Decoder decoder, WallWriteDto request, CancellationToken ct)
    {
        int startChannel = 1;
        int? capacity = null;
        string? warning = null;
        try
        {
            var caps = await sessions.WithDriverAsync(decoder, d => d.GetCapabilitiesAsync(ct), ct);
            startChannel = caps.DecodeChannelStart;
            capacity = caps.DecodeChannelCount;
        }
        catch (Exception ex)
        {
            warning = $"No se pudo leer el decodificador para validar canales (se asignan desde el canal 1): {ex.Message}";
        }

        int next = startChannel;
        var screens = new List<WallScreen>();
        foreach (var s in request.Screens.OrderBy(x => x.Row).ThenBy(x => x.Col))
        {
            int windowCount = Math.Max(1, s.WindowMode);
            var screen = new WallScreen
            {
                Row = s.Row,
                Col = s.Col,
                Label = s.Label,
                DisplayChannel = s.DisplayChannel,
                WindowMode = windowCount,
            };
            for (int i = 0; i < windowCount; i++)
                screen.Windows.Add(new ScreenWindow { WindowIndex = i, DecodeChannel = next++ });
            screens.Add(screen);
        }

        int used = next - startChannel;
        if (capacity is int cap && used > cap)
            throw new InvalidOperationException(
                $"La configuración necesita {used} canales de decodificación pero el decodificador solo tiene {cap}. " +
                "Reduzca la cantidad de ventanas.");

        return (screens, warning);
    }

    public static void MapWallsApi(this IEndpointRouteBuilder app)
    {
        // ------------------------------------------------------------------
        // Estructura del muro (administrador)
        // ------------------------------------------------------------------
        app.MapGet("/api/walls", async (HttpContext ctx, WallService walls) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.Ok(await walls.GetWallDtosAsync());
        });

        app.MapGet("/api/walls/{id:int}", async (HttpContext ctx, int id, WallService walls) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var dto = await walls.GetWallDtoAsync(id);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        });

        app.MapPost("/api/walls", async (HttpContext ctx, WallWriteDto request, VmsDbContext db,
            WallService walls, DecoderSessionManager sessions, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (string.IsNullOrWhiteSpace(request.Name))
                return Error("El nombre es obligatorio.");
            var decoder = await db.Decoders.FindAsync([request.DecoderId], ct);
            if (decoder is null)
                return Error("El decodificador indicado no existe.");

            List<WallScreen> screens;
            string? warning;
            try { (screens, warning) = await BuildScreensAsync(sessions, decoder, request, ct); }
            catch (InvalidOperationException ex) { return Error(ex.Message); }

            var wall = new VideoWall
            {
                Name = request.Name.Trim(),
                DecoderId = request.DecoderId,
                Rows = Math.Max(1, request.Rows),
                Columns = Math.Max(1, request.Columns),
                Screens = screens,
            };
            db.Walls.Add(wall);
            await db.SaveChangesAsync(ct);

            var sync = await walls.SyncToDecoderAsync(wall.Id);
            await walls.BroadcastConfigChangedAsync();
            return Results.Ok(new { wall = await walls.GetWallDtoAsync(wall.Id), warning, sync });
        });

        app.MapPut("/api/walls/{id:int}", async (HttpContext ctx, int id, WallWriteDto request, VmsDbContext db,
            WallService walls, DecoderSessionManager sessions, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var wall = await db.Walls.Include(w => w.Screens).ThenInclude(s => s.Windows)
                .FirstOrDefaultAsync(w => w.Id == id, ct);
            if (wall is null) return Results.NotFound();
            var decoder = await db.Decoders.FindAsync([request.DecoderId], ct);
            if (decoder is null)
                return Error("El decodificador indicado no existe.");

            List<WallScreen> screens;
            string? warning;
            try { (screens, warning) = await BuildScreensAsync(sessions, decoder, request, ct); }
            catch (InvalidOperationException ex) { return Error(ex.Message); }

            var oldDecoder = await db.Decoders.FindAsync([wall.DecoderId], ct);
            var oldChannels = wall.Screens.SelectMany(s => s.Windows).Select(x => x.DecodeChannel).ToHashSet();

            wall.Name = request.Name.Trim();
            wall.DecoderId = request.DecoderId;
            wall.Rows = Math.Max(1, request.Rows);
            wall.Columns = Math.Max(1, request.Columns);

            // Reemplazo completo de pantallas: la reconfiguración física del
            // muro invalida las asignaciones actuales (los layouts guardados
            // que referencien ventanas eliminadas se reportan al aplicarlos).
            db.WallScreens.RemoveRange(wall.Screens);
            wall.Screens = screens;

            await db.SaveChangesAsync(ct);

            // Liberar en el equipo los canales/ventanas que salieron del esquema.
            var newChannels = screens.SelectMany(s => s.Windows).Select(x => x.DecodeChannel).ToHashSet();
            if (oldDecoder is not null)
                await walls.ReleaseChannelsAsync(oldDecoder, oldChannels.Except(newChannels));

            var sync = await walls.SyncToDecoderAsync(wall.Id);
            await walls.BroadcastConfigChangedAsync();
            return Results.Ok(new { wall = await walls.GetWallDtoAsync(wall.Id), warning, sync });
        });

        app.MapDelete("/api/walls/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            WallService walls, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var wall = await db.Walls.FindAsync([id], ct);
            if (wall is null) return Results.NotFound();
            db.Walls.Remove(wall);
            await db.SaveChangesAsync(ct);
            await walls.BroadcastConfigChangedAsync();
            return Results.Ok();
        });

        // Re-empuja al decoder la división configurada (por si el equipo estaba
        // apagado al guardar o alguien lo cambió con otra herramienta).
        app.MapPost("/api/walls/{id:int}/sync", (HttpContext ctx, int id, WallService walls) =>
            ApiSecurity.RequireUser(ctx, out _) is { } failure
                ? Task.FromResult(failure)
                : RunAsync(() => walls.SyncToDecoderAsync(id)));

        // ------------------------------------------------------------------
        // Operación (cualquier sesión)
        // ------------------------------------------------------------------
        app.MapPost("/api/walls/{id:int}/assign", (HttpContext ctx, int id, AssignRequest request, WallService walls) =>
            ApiSecurity.RequireUser(ctx, out _) is { } failure
                ? Task.FromResult(failure)
                : RunAsync(() => walls.AssignAsync(id, request)));

        // Proyección: una ventana muestra una URL externa (el PC del operador
        // publica su pantalla por RTSP y el decodificador la consume).
        app.MapPost("/api/walls/{id:int}/assign-external",
            (HttpContext ctx, int id, ExternalAssignRequest request, WallService walls) =>
                ApiSecurity.RequireUser(ctx, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAsync(() => walls.AssignExternalAsync(id, request)));

        app.MapPost("/api/walls/{id:int}/clear", (HttpContext ctx, int id, ClearRequest request, WallService walls) =>
            ApiSecurity.RequireUser(ctx, out _) is { } failure
                ? Task.FromResult(failure)
                : RunAsync(() => walls.ClearAsync(id, request.WindowId)));

        app.MapPost("/api/walls/{id:int}/clear-all", (HttpContext ctx, int id, WallService walls) =>
            ApiSecurity.RequireUser(ctx, out _) is { } failure
                ? Task.FromResult(failure)
                : RunAsync(() => walls.ClearAllAsync(id)));

        app.MapPost("/api/walls/{id:int}/swap", (HttpContext ctx, int id, SwapRequest request, WallService walls) =>
            ApiSecurity.RequireUser(ctx, out _) is { } failure
                ? Task.FromResult(failure)
                : RunAsync(() => walls.SwapWindowsAsync(id, request.WindowAId, request.WindowBId)));

        // Cambia la división de un monitor. La estructura del muro (filas ×
        // columnas) sigue siendo exclusiva del administrador.
        app.MapPut("/api/walls/{id:int}/screens/{screenId:int}/window-mode",
            (HttpContext ctx, int id, int screenId, ScreenWindowModeRequest request, WallService walls) =>
                ApiSecurity.RequireUser(ctx, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAsync(() => walls.ChangeScreenWindowModeAsync(id, screenId, request.WindowMode)));

        app.MapPost("/api/walls/{id:int}/screens/{screenId:int}/group",
            (HttpContext ctx, int id, int screenId, GroupRequest request, WallService walls) =>
                ApiSecurity.RequireUser(ctx, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAsync(() => walls.GroupWindowsAsync(id, screenId, request.WindowIds)));

        app.MapPost("/api/walls/{id:int}/windows/{windowId:int}/ungroup",
            (HttpContext ctx, int id, int windowId, WallService walls) =>
                ApiSecurity.RequireUser(ctx, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAsync(() => walls.UngroupWindowAsync(id, windowId)));

        app.MapPost("/api/walls/{id:int}/windows/{windowId:int}/subdivide",
            (HttpContext ctx, int id, int windowId, SubdivideRequest request, WallService walls) =>
                ApiSecurity.RequireUser(ctx, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAsync(() => walls.SubdivideWindowAsync(id, windowId, request.Parts)));

        // Pantalla completa (doble clic sobre una ventana) y muro completo.
        app.MapPost("/api/walls/{id:int}/fullscreen",
            (HttpContext ctx, int id, FullscreenRequest request, WallService walls) =>
                ApiSecurity.RequireUser(ctx, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAsync(() => walls.EnterFullscreenAsync(id, request.WindowId)));

        app.MapPost("/api/walls/{id:int}/screens/{screenId:int}/exit-fullscreen",
            (HttpContext ctx, int id, int screenId, WallService walls) =>
                ApiSecurity.RequireUser(ctx, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAsync(() => walls.ExitFullscreenAsync(id, screenId)));

        app.MapPost("/api/walls/{id:int}/wall-fullscreen",
            (HttpContext ctx, int id, FullscreenRequest request, WallService walls) =>
                ApiSecurity.RequireUser(ctx, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAsync(() => walls.EnterWallFullscreenAsync(id, request.WindowId)));

        app.MapPost("/api/walls/{id:int}/exit-wall-fullscreen",
            (HttpContext ctx, int id, WallService walls) =>
                ApiSecurity.RequireUser(ctx, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAsync(() => walls.ExitWallFullscreenAsync(id)));

        // ------------------------------------------------------------------
        // Ventanas flotantes (rect libre dibujado encima del mosaico)
        // ------------------------------------------------------------------
        app.MapPost("/api/walls/{id:int}/floating",
            (HttpContext ctx, int id, FloatingCreateRequest request, WallService walls) =>
                ApiSecurity.RequireUser(ctx, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAsync(() => walls.CreateFloatingAsync(id, request)));

        app.MapPost("/api/walls/{id:int}/floating-external",
            (HttpContext ctx, int id, FloatingExternalCreateRequest request, WallService walls) =>
                ApiSecurity.RequireUser(ctx, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAsync(() => walls.CreateFloatingExternalAsync(id, request)));

        app.MapPost("/api/walls/{id:int}/floating/{floatingId:int}/assign-external",
            (HttpContext ctx, int id, int floatingId, FloatingExternalAssignRequest request, WallService walls) =>
                ApiSecurity.RequireUser(ctx, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAsync(() => walls.AssignFloatingExternalAsync(id, floatingId, request)));

        app.MapPut("/api/walls/{id:int}/floating/{floatingId:int}",
            (HttpContext ctx, int id, int floatingId, FloatingMoveRequest request, WallService walls) =>
                ApiSecurity.RequireUser(ctx, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAsync(() => walls.MoveFloatingAsync(id, floatingId, request)));

        app.MapPost("/api/walls/{id:int}/floating/{floatingId:int}/fullscreen",
            (HttpContext ctx, int id, int floatingId, WallService walls) =>
                ApiSecurity.RequireUser(ctx, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAsync(() => walls.ToggleFloatingFullscreenAsync(id, floatingId)));

        app.MapPost("/api/walls/{id:int}/floating/{floatingId:int}/assign",
            (HttpContext ctx, int id, int floatingId, FloatingAssignRequest request, WallService walls) =>
                ApiSecurity.RequireUser(ctx, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAsync(() => walls.AssignFloatingAsync(id, floatingId, request)));

        app.MapDelete("/api/walls/{id:int}/floating/{floatingId:int}",
            (HttpContext ctx, int id, int floatingId, WallService walls) =>
                ApiSecurity.RequireUser(ctx, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAsync(() => walls.DeleteFloatingAsync(id, floatingId)));

        // ------------------------------------------------------------------
        // Layouts guardados
        // ------------------------------------------------------------------
        app.MapGet("/api/walls/{id:int}/layouts", async (HttpContext ctx, int id, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var layouts = await db.WallLayouts
                .Include(l => l.Items).Include(l => l.Screens)
                .Where(l => l.VideoWallId == id)
                .OrderBy(l => l.Name)
                .AsSplitQuery()
                .AsNoTracking()
                .ToListAsync(ct);
            return Results.Ok(layouts.Select(ToDto));
        });

        // Guarda un layout como foto del estado actual del muro (división de
        // cada monitor + cámaras asignadas por posición estable).
        app.MapPost("/api/walls/{id:int}/layouts", async (HttpContext ctx, int id, LayoutSaveRequest request,
            VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            if (string.IsNullOrWhiteSpace(request.Name))
                return Error("El nombre del layout es obligatorio.");
            var wall = await db.Walls.Include(w => w.Screens).ThenInclude(s => s.Windows)
                .AsSplitQuery().FirstOrDefaultAsync(w => w.Id == id, ct);
            if (wall is null) return Results.NotFound();
            if (await db.WallLayouts.AnyAsync(l => l.VideoWallId == id && l.Name == request.Name.Trim(), ct))
                return Error("Ya existe un layout con ese nombre en este muro.", StatusCodes.Status409Conflict);

            var layout = new WallLayoutPreset
            {
                VideoWallId = id,
                Name = request.Name.Trim(),
                Screens = wall.Screens
                    .Select(s => new WallLayoutScreen { Row = s.Row, Col = s.Col, WindowMode = s.WindowMode })
                    .ToList(),
                Items = wall.Screens
                    .SelectMany(s => s.Windows
                        .Where(x => x.AssignedChannelId is not null)
                        .Select(x => new WallLayoutItem
                        {
                            Row = s.Row,
                            Col = s.Col,
                            WindowIndex = x.WindowIndex,
                            ChannelId = x.AssignedChannelId!.Value,
                            StreamType = x.AssignedStreamType,
                            SpanCols = Math.Max(1, x.SpanCols),
                            SpanRows = Math.Max(1, x.SpanRows),
                        }))
                    .ToList(),
            };
            db.WallLayouts.Add(layout);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(layout));
        });

        app.MapDelete("/api/walls/{id:int}/layouts/{layoutId:int}",
            async (HttpContext ctx, int id, int layoutId, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var layout = await db.WallLayouts.FirstOrDefaultAsync(l => l.Id == layoutId && l.VideoWallId == id, ct);
            if (layout is null) return Results.NotFound();
            db.WallLayouts.Remove(layout);
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        app.MapPost("/api/walls/{id:int}/layouts/{layoutId:int}/apply",
            (HttpContext ctx, int id, int layoutId, WallService walls) =>
                ApiSecurity.RequireUser(ctx, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAsync(() => walls.ApplyLayoutAsync(id, layoutId)));
    }
}
