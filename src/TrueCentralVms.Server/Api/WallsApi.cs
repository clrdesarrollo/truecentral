using TrueCentralVms.Server.Services.Licensing;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Core.Domain;

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

    /// <summary>
    /// Igual que <see cref="RunAsync"/>, pero dejando la operación en la
    /// bitácora de auditoría (también cuando el equipo la rechaza: el intento
    /// es tan relevante como el éxito). El marcador {channel} del detalle se
    /// reemplaza por "Equipo · Canal" resuelto desde el inventario.
    /// </summary>
    private static async Task<IResult> RunAuditedAsync<T>(HttpContext ctx, AuditService audit, VmsDbContext db,
        int wallId, string action, string detailTemplate, Func<Task<T>> operation, int? channelId = null)
    {
        string wallName = await db.Walls.Where(w => w.Id == wallId).Select(w => w.Name).FirstOrDefaultAsync()
            ?? $"muro {wallId}";
        string detail = detailTemplate;
        if (channelId is int cid)
        {
            string? channelName = await db.Channels.Where(c => c.Id == cid)
                .Select(c => c.Device.Name + " · " + c.Name).FirstOrDefaultAsync();
            // El muro es compartido, pero solo se le envían cámaras del propio alcance.
            if (ApiSecurity.CurrentSession(ctx) is { } session && !(await ctx.ScopeAsync(session)).CanViewChannel(cid))
                return await ctx.OutOfScopeAsync(session, "channel", cid.ToString(), channelName, "enviar al muro");
            detail = detail.Replace("{channel}", channelName ?? $"canal id {cid}");
        }
        detail = $"{detail} en el muro '{wallName}'";

        try
        {
            var result = await operation();
            await audit.LogAsync(ctx, "wall", action,
                targetType: "wall", targetId: wallId.ToString(), targetName: wallName, detail: detail + ".");
            return Results.Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex)
        {
            await audit.LogAsync(ctx, "wall", action,
                targetType: "wall", targetId: wallId.ToString(), targetName: wallName,
                detail: $"{detail}: {ex.Message}", success: false);
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

    /// <summary>Resultado de planificar las pantallas de un muro al crearlo o editarlo.</summary>
    /// <param name="Kept">Pantallas existentes que siguen igual (conservan ventanas y cámaras).</param>
    /// <param name="Added">Pantallas nuevas o cambiadas, con canales de decodificación ya asignados.</param>
    /// <param name="Removed">Pantallas existentes que salen del muro.</param>
    private sealed record ScreenPlan(List<WallScreen> Kept, List<WallScreen> Added, List<WallScreen> Removed, string? Warning);

    /// <summary>
    /// Arma las pantallas del muro con lo que pide el editor. Una pantalla que
    /// no cambió (misma posición, misma salida y misma división) CONSERVA sus
    /// ventanas, sus canales de decodificación y sus cámaras: renombrar el muro
    /// o sumarle un monitor ya no apaga lo que se está viendo. Las ventanas
    /// nuevas toman canales LIBRES del decodificador, descontando los de las
    /// flotantes y los de otros muros del mismo equipo, así dos ventanas nunca
    /// comparten canal. El operador nunca elige canales a mano.
    /// </summary>
    private static async Task<ScreenPlan> PlanScreensAsync(VmsDbContext db, DecoderSessionManager sessions,
        Decoder decoder, WallWriteDto request, VideoWall? existing, CancellationToken ct)
    {
        int rows = Math.Max(1, request.Rows), columns = Math.Max(1, request.Columns);
        var requested = (request.Screens ?? []).Where(s => s.DisplayChannel > 0).ToList();
        if (requested.Any(s => s.Row < 0 || s.Row >= rows || s.Col < 0 || s.Col >= columns))
            throw new InvalidOperationException("Hay monitores fuera de la grilla del muro: revise filas y columnas.");
        if (requested.GroupBy(s => (s.Row, s.Col)).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Hay dos salidas en la misma posición del muro.");
        if (requested.GroupBy(s => s.DisplayChannel).FirstOrDefault(g => g.Count() > 1) is { } repeated)
            throw new InvalidOperationException(
                $"La salida {repeated.First().Label} está en dos posiciones del muro: cada salida alimenta un solo monitor.");

        // Una salida física alimenta un solo monitor: si ya está en otro muro
        // del mismo decodificador, se rechaza en vez de pisarla.
        var others = await db.Walls.AsNoTracking()
            .Include(w => w.Screens).ThenInclude(s => s.Windows)
            .Include(w => w.Floating)
            .Where(w => w.DecoderId == decoder.Id && (existing == null || w.Id != existing.Id))
            .AsSplitQuery()
            .ToListAsync(ct);
        foreach (var s in requested)
        {
            var taken = others.FirstOrDefault(w => w.Screens.Any(x => x.DisplayChannel == s.DisplayChannel));
            if (taken is not null)
                throw new InvalidOperationException(
                    $"La salida {s.Label} ya alimenta un monitor del muro '{taken.Name}'. Quítela de ese muro primero.");
        }

        bool sameDecoder = existing is not null && existing.DecoderId == decoder.Id;
        var current = sameDecoder ? existing!.Screens.ToDictionary(s => (s.Row, s.Col)) : [];
        var kept = new List<WallScreen>();
        var pending = new List<ScreenConfigDto>();
        foreach (var s in requested)
        {
            if (current.TryGetValue((s.Row, s.Col), out var old)
                && old.DisplayChannel == s.DisplayChannel && old.WindowMode == Math.Max(1, s.WindowMode))
            {
                old.Label = s.Label;
                kept.Add(old);
            }
            else pending.Add(s);
        }
        var removed = existing?.Screens.Where(s => !kept.Contains(s)).ToList() ?? [];
        if (pending.Count == 0) return new ScreenPlan(kept, [], removed, null);   // nada que asignar: no hace falta el equipo

        int start = 1;
        int? capacity = null;
        string? warning = null;
        try
        {
            var caps = await sessions.WithDriverAsync(decoder, d => d.GetCapabilitiesAsync(ct), ct);
            start = caps.DecodeChannelStart;
            capacity = caps.DecodeChannelCount;
        }
        catch (Exception ex)
        {
            warning = $"No se pudo leer el decodificador para validar canales (se asignan desde el canal 1): {ex.Message}";
        }

        var used = kept.SelectMany(s => s.Windows).Select(x => x.DecodeChannel)
            .Concat(sameDecoder ? existing!.Floating.Select(f => f.DecodeChannel) : [])
            .Concat(others.SelectMany(w => w.Screens).SelectMany(s => s.Windows).Select(x => x.DecodeChannel))
            .Concat(others.SelectMany(w => w.Floating).Select(f => f.DecodeChannel))
            .Where(c => c > 0)
            .ToHashSet();
        // Los canales de las pantallas que salen se reutilizan solo si no queda
        // otro libre: el equipo los cierra antes de abrir las ventanas nuevas.
        var leaving = removed.SelectMany(s => s.Windows).Select(x => x.DecodeChannel).ToHashSet();
        int needed = pending.Sum(s => Math.Max(1, s.WindowMode));
        var free = new List<int>();
        for (int c = start; free.Count < needed && (capacity is not int cap0 || c < start + cap0) && c < start + 4096; c++)
            if (!used.Contains(c) && !leaving.Contains(c)) free.Add(c);
        free.AddRange(leaving.Where(c => !used.Contains(c)).OrderBy(c => c).Take(needed - free.Count));
        if (free.Count < needed)
        {
            int total = used.Count + needed;
            throw new InvalidOperationException(capacity is int cap
                ? $"La configuración necesita {total} canales de decodificación (contando flotantes y otros muros del equipo) " +
                  $"pero el decodificador solo tiene {cap}. Reduzca la cantidad de ventanas."
                : "No quedan canales de decodificación libres para las ventanas nuevas.");
        }

        int next = 0;
        var added = new List<WallScreen>();
        foreach (var s in pending.OrderBy(x => x.Row).ThenBy(x => x.Col))
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
                screen.Windows.Add(new ScreenWindow { WindowIndex = i, DecodeChannel = free[next++] });
            added.Add(screen);
        }

        return new ScreenPlan(kept, added, removed, warning);
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

        app.MapPost("/api/walls", async (HttpContext ctx, WallWriteDto request, VmsDbContext db, LicenseService license,
            WallService walls, DecoderSessionManager sessions, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.DevicesManage, out _) is { } failure) return failure;
            if (string.IsNullOrWhiteSpace(request.Name))
                return Error("El nombre es obligatorio.");
            if (license.Deny(LicenseFeatures.ModuleVideowall, LicenseFeatures.Videowalls, await db.Walls.CountAsync(ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "wall", request.Name.Trim());
            var decoder = await db.Decoders.FindAsync([request.DecoderId], ct);
            if (decoder is null)
                return Error("El decodificador indicado no existe.");

            ScreenPlan plan;
            try { plan = await PlanScreensAsync(db, sessions, decoder, request, null, ct); }
            catch (InvalidOperationException ex) { return Error(ex.Message); }
            if (plan.Added.Count == 0) return Error("Ubique al menos una salida del decodificador en el muro.");
            string? warning = plan.Warning;

            var wall = new VideoWall
            {
                Name = request.Name.Trim(),
                DecoderId = request.DecoderId,
                Rows = Math.Max(1, request.Rows),
                Columns = Math.Max(1, request.Columns),
                Screens = plan.Added,
            };
            db.Walls.Add(wall);
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "wall", "wall-created",
                targetType: "wall", targetId: wall.Id.ToString(), targetName: wall.Name,
                detail: $"Creó el muro '{wall.Name}' ({wall.Rows}×{wall.Columns}, decodificador '{decoder.Name}').");
            var sync = await walls.SyncToDecoderAsync(wall.Id);
            await walls.BroadcastConfigChangedAsync();
            return Results.Ok(new { wall = await walls.GetWallDtoAsync(wall.Id), warning, sync });
        });

        app.MapPut("/api/walls/{id:int}", async (HttpContext ctx, int id, WallWriteDto request, VmsDbContext db,
            WallService walls, DecoderSessionManager sessions, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.DevicesManage, out _) is { } failure) return failure;
            var wall = await db.Walls.Include(w => w.Screens).ThenInclude(s => s.Windows)
                .Include(w => w.Floating)
                .AsSplitQuery()
                .FirstOrDefaultAsync(w => w.Id == id, ct);
            if (wall is null) return Results.NotFound();
            var decoder = await db.Decoders.FindAsync([request.DecoderId], ct);
            if (decoder is null)
                return Error("El decodificador indicado no existe.");

            ScreenPlan plan;
            try { plan = await PlanScreensAsync(db, sessions, decoder, request, wall, ct); }
            catch (InvalidOperationException ex) { return Error(ex.Message); }
            if (plan.Kept.Count + plan.Added.Count == 0) return Error("Ubique al menos una salida del decodificador en el muro.");
            string? warning = plan.Warning;

            var oldDecoder = await db.Decoders.FindAsync([wall.DecoderId], ct);
            bool decoderChanged = wall.DecoderId != request.DecoderId;
            bool structureChanged = decoderChanged || plan.Added.Count > 0 || plan.Removed.Count > 0
                || wall.Rows != Math.Max(1, request.Rows) || wall.Columns != Math.Max(1, request.Columns);
            // Lo que sale del muro se apaga en el equipo: las pantallas quitadas
            // o cambiadas y, si cambia el decodificador, también las flotantes
            // (sus canales eran del equipo anterior).
            var released = plan.Removed.SelectMany(s => s.Windows).Select(x => x.DecodeChannel).ToList();
            var removedWindowIds = plan.Removed.SelectMany(s => s.Windows).Select(x => x.Id).ToHashSet();
            if (decoderChanged)
            {
                released.AddRange(wall.Floating.Select(f => f.DecodeChannel));
                db.WallFloatingWindows.RemoveRange(wall.Floating);
            }
            if (wall.FullscreenWindowId is int wallWin && removedWindowIds.Contains(wallWin))
                wall.FullscreenWindowId = null;

            wall.Name = request.Name.Trim();
            wall.DecoderId = request.DecoderId;
            wall.Rows = Math.Max(1, request.Rows);
            wall.Columns = Math.Max(1, request.Columns);
            foreach (var screen in plan.Removed)
            {
                wall.Screens.Remove(screen);
                db.WallScreens.Remove(screen);
            }
            wall.Screens.AddRange(plan.Added);
            await db.SaveChangesAsync(ct);

            if (oldDecoder is not null && released.Count > 0)
                await walls.ReleaseChannelsAsync(oldDecoder, released.Where(c => c > 0).Distinct());

            int kept = plan.Kept.Count;
            await audit.LogAsync(ctx, "wall", "wall-updated",
                targetType: "wall", targetId: wall.Id.ToString(), targetName: wall.Name,
                detail: $"Modificó el muro '{wall.Name}' ({wall.Rows}×{wall.Columns}, decodificador '{decoder.Name}')" +
                        (structureChanged
                            ? $": {plan.Added.Count} monitor(es) nuevo(s) o cambiado(s), {plan.Removed.Count} quitado(s)" +
                              (kept > 0 ? $"; {kept} sin cambios conservan sus cámaras." : ".")
                            : ": solo nombres, sin cambios en el equipo."));
            // Si solo cambiaron nombres no se toca el equipo: sincronizar
            // cancelaría las pantallas completas vigentes sin necesidad.
            var sync = structureChanged ? await walls.SyncToDecoderAsync(wall.Id) : [];
            await walls.BroadcastConfigChangedAsync();
            if (structureChanged) await walls.BroadcastWallStateAsync(wall.Id);
            return Results.Ok(new { wall = await walls.GetWallDtoAsync(wall.Id), warning, sync });
        });

        app.MapDelete("/api/walls/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            WallService walls, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.DevicesManage, out _) is { } failure) return failure;
            var wall = await db.Walls.FindAsync([id], ct);
            if (wall is null) return Results.NotFound();
            db.Walls.Remove(wall);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "wall", "wall-deleted",
                targetType: "wall", targetId: id.ToString(), targetName: wall.Name,
                detail: $"Eliminó el muro '{wall.Name}'.");
            await walls.BroadcastConfigChangedAsync();
            return Results.Ok();
        });

        // Re-empuja al decoder la división configurada (por si el equipo estaba
        // apagado al guardar o alguien lo cambió con otra herramienta).
        app.MapPost("/api/walls/{id:int}/sync", (HttpContext ctx, int id, WallService walls,
            VmsDbContext db, AuditService audit) =>
            ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                ? Task.FromResult(failure)
                : RunAuditedAsync(ctx, audit, db, id, "wall-synced",
                    "Re-sincronizó la configuración contra el decodificador",
                    () => walls.SyncToDecoderAsync(id)));

        // ------------------------------------------------------------------
        // Operación (cualquier sesión)
        // ------------------------------------------------------------------
        app.MapPost("/api/walls/{id:int}/assign", (HttpContext ctx, int id, AssignRequest request, WallService walls,
            VmsDbContext db, AuditService audit) =>
            ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                ? Task.FromResult(failure)
                : RunAuditedAsync(ctx, audit, db, id, "window-assigned",
                    $"Puso {{channel}} en la ventana {request.WindowId}",
                    () => walls.AssignAsync(id, request), channelId: request.ChannelId));

        // Proyección: una ventana muestra una URL externa (el PC del operador
        // publica su pantalla por RTSP y el decodificador la consume).
        app.MapPost("/api/walls/{id:int}/assign-external",
            (HttpContext ctx, int id, ExternalAssignRequest request, WallService walls,
                VmsDbContext db, AuditService audit) =>
                ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAuditedAsync(ctx, audit, db, id, "external-assigned",
                        $"Proyectó la fuente externa '{request.Label}' en la ventana {request.WindowId}",
                        () => walls.AssignExternalAsync(id, request)));

        app.MapPost("/api/walls/{id:int}/clear", (HttpContext ctx, int id, ClearRequest request, WallService walls,
            VmsDbContext db, AuditService audit) =>
            ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                ? Task.FromResult(failure)
                : RunAuditedAsync(ctx, audit, db, id, "window-cleared",
                    $"Limpió la ventana {request.WindowId}",
                    () => walls.ClearAsync(id, request.WindowId)));

        app.MapPost("/api/walls/{id:int}/clear-all", (HttpContext ctx, int id, WallService walls,
            VmsDbContext db, AuditService audit) =>
            ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                ? Task.FromResult(failure)
                : RunAuditedAsync(ctx, audit, db, id, "wall-cleared",
                    "Limpió todas las ventanas",
                    () => walls.ClearAllAsync(id)));

        app.MapPost("/api/walls/{id:int}/swap", (HttpContext ctx, int id, SwapRequest request, WallService walls,
            VmsDbContext db, AuditService audit) =>
            ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                ? Task.FromResult(failure)
                : RunAuditedAsync(ctx, audit, db, id, "windows-swapped",
                    $"Intercambió las ventanas {request.WindowAId} y {request.WindowBId}",
                    () => walls.SwapWindowsAsync(id, request.WindowAId, request.WindowBId)));

        // Cambia la división de un monitor. La estructura del muro (filas ×
        // columnas) sigue siendo exclusiva del administrador.
        app.MapPut("/api/walls/{id:int}/screens/{screenId:int}/window-mode",
            (HttpContext ctx, int id, int screenId, ScreenWindowModeRequest request, WallService walls,
                VmsDbContext db, AuditService audit) =>
                ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAuditedAsync(ctx, audit, db, id, "window-mode-changed",
                        $"Cambió la división del monitor {screenId} a {request.WindowMode} ventana(s)",
                        () => walls.ChangeScreenWindowModeAsync(id, screenId, request.WindowMode)));

        app.MapPost("/api/walls/{id:int}/screens/{screenId:int}/group",
            (HttpContext ctx, int id, int screenId, GroupRequest request, WallService walls,
                VmsDbContext db, AuditService audit) =>
                ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAuditedAsync(ctx, audit, db, id, "windows-grouped",
                        $"Agrupó {request.WindowIds.Count} ventanas del monitor {screenId}",
                        () => walls.GroupWindowsAsync(id, screenId, request.WindowIds)));

        app.MapPost("/api/walls/{id:int}/windows/{windowId:int}/ungroup",
            (HttpContext ctx, int id, int windowId, WallService walls, VmsDbContext db, AuditService audit) =>
                ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAuditedAsync(ctx, audit, db, id, "window-ungrouped",
                        $"Desagrupó la ventana {windowId}",
                        () => walls.UngroupWindowAsync(id, windowId)));

        app.MapPost("/api/walls/{id:int}/windows/{windowId:int}/subdivide",
            (HttpContext ctx, int id, int windowId, SubdivideRequest request, WallService walls,
                VmsDbContext db, AuditService audit) =>
                ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAuditedAsync(ctx, audit, db, id, "window-subdivided",
                        $"Subdividió la ventana {windowId} en {request.Parts} partes",
                        () => walls.SubdivideWindowAsync(id, windowId, request.Parts)));

        // Pantalla completa (doble clic sobre una ventana) y muro completo.
        app.MapPost("/api/walls/{id:int}/fullscreen",
            (HttpContext ctx, int id, FullscreenRequest request, WallService walls,
                VmsDbContext db, AuditService audit) =>
                ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAuditedAsync(ctx, audit, db, id, "fullscreen-entered",
                        $"Puso la ventana {request.WindowId} a pantalla completa de su monitor",
                        () => walls.EnterFullscreenAsync(id, request.WindowId)));

        app.MapPost("/api/walls/{id:int}/screens/{screenId:int}/exit-fullscreen",
            (HttpContext ctx, int id, int screenId, WallService walls, VmsDbContext db, AuditService audit) =>
                ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAuditedAsync(ctx, audit, db, id, "fullscreen-exited",
                        $"Sacó el monitor {screenId} de pantalla completa",
                        () => walls.ExitFullscreenAsync(id, screenId)));

        app.MapPost("/api/walls/{id:int}/wall-fullscreen",
            (HttpContext ctx, int id, FullscreenRequest request, WallService walls,
                VmsDbContext db, AuditService audit) =>
                ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAuditedAsync(ctx, audit, db, id, "wall-fullscreen-entered",
                        $"Puso la ventana {request.WindowId} a pantalla completa del muro",
                        () => walls.EnterWallFullscreenAsync(id, request.WindowId)));

        app.MapPost("/api/walls/{id:int}/exit-wall-fullscreen",
            (HttpContext ctx, int id, WallService walls, VmsDbContext db, AuditService audit) =>
                ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAuditedAsync(ctx, audit, db, id, "wall-fullscreen-exited",
                        "Sacó el muro de pantalla completa",
                        () => walls.ExitWallFullscreenAsync(id)));

        // ------------------------------------------------------------------
        // Ventanas flotantes (rect libre dibujado encima del mosaico)
        // ------------------------------------------------------------------
        app.MapPost("/api/walls/{id:int}/floating",
            (HttpContext ctx, int id, FloatingCreateRequest request, WallService walls,
                VmsDbContext db, AuditService audit) =>
                ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAuditedAsync(ctx, audit, db, id, "floating-created",
                        "Creó una ventana flotante con {channel}",
                        () => walls.CreateFloatingAsync(id, request), channelId: request.ChannelId));

        app.MapPost("/api/walls/{id:int}/floating-external",
            (HttpContext ctx, int id, FloatingExternalCreateRequest request, WallService walls,
                VmsDbContext db, AuditService audit) =>
                ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAuditedAsync(ctx, audit, db, id, "floating-created",
                        $"Creó una ventana flotante con la fuente externa '{request.Label}'",
                        () => walls.CreateFloatingExternalAsync(id, request)));

        app.MapPost("/api/walls/{id:int}/floating/{floatingId:int}/assign-external",
            (HttpContext ctx, int id, int floatingId, FloatingExternalAssignRequest request, WallService walls,
                VmsDbContext db, AuditService audit) =>
                ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAuditedAsync(ctx, audit, db, id, "floating-assigned",
                        $"Proyectó la fuente externa '{request.Label}' en la flotante {floatingId}",
                        () => walls.AssignFloatingExternalAsync(id, floatingId, request)));

        app.MapPut("/api/walls/{id:int}/floating/{floatingId:int}",
            (HttpContext ctx, int id, int floatingId, FloatingMoveRequest request, WallService walls,
                VmsDbContext db, AuditService audit) =>
                ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAuditedAsync(ctx, audit, db, id, "floating-moved",
                        $"Movió o redimensionó la ventana flotante {floatingId}",
                        () => walls.MoveFloatingAsync(id, floatingId, request)));

        app.MapPost("/api/walls/{id:int}/floating/{floatingId:int}/fullscreen",
            (HttpContext ctx, int id, int floatingId, WallService walls, VmsDbContext db, AuditService audit) =>
                ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAuditedAsync(ctx, audit, db, id, "floating-fullscreen",
                        $"Alternó la pantalla completa de la flotante {floatingId}",
                        () => walls.ToggleFloatingFullscreenAsync(id, floatingId)));

        app.MapPost("/api/walls/{id:int}/floating/{floatingId:int}/assign",
            (HttpContext ctx, int id, int floatingId, FloatingAssignRequest request, WallService walls,
                VmsDbContext db, AuditService audit) =>
                ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAuditedAsync(ctx, audit, db, id, "floating-assigned",
                        $"Puso {{channel}} en la ventana flotante {floatingId}",
                        () => walls.AssignFloatingAsync(id, floatingId, request), channelId: request.ChannelId));

        app.MapDelete("/api/walls/{id:int}/floating/{floatingId:int}",
            (HttpContext ctx, int id, int floatingId, WallService walls, VmsDbContext db, AuditService audit) =>
                ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure
                    ? Task.FromResult(failure)
                    : RunAuditedAsync(ctx, audit, db, id, "floating-deleted",
                        $"Eliminó la ventana flotante {floatingId}",
                        () => walls.DeleteFloatingAsync(id, floatingId)));

        // ------------------------------------------------------------------
        // Layouts guardados
        // ------------------------------------------------------------------
        app.MapGet("/api/walls/{id:int}/layouts", async (HttpContext ctx, int id, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WallOperate, out _) is { } failure) return failure;
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
            VmsDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WallLayouts, out _) is { } failure) return failure;
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
            await audit.LogAsync(ctx, "wall", "layout-saved",
                targetType: "wall", targetId: id.ToString(), targetName: wall.Name,
                detail: $"Guardó el layout '{layout.Name}' del muro '{wall.Name}' ({layout.Items.Count} cámaras).");
            return Results.Ok(ToDto(layout));
        });

        app.MapDelete("/api/walls/{id:int}/layouts/{layoutId:int}",
            async (HttpContext ctx, int id, int layoutId, VmsDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WallLayouts, out _) is { } failure) return failure;
            var layout = await db.WallLayouts.FirstOrDefaultAsync(l => l.Id == layoutId && l.VideoWallId == id, ct);
            if (layout is null) return Results.NotFound();
            db.WallLayouts.Remove(layout);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "wall", "layout-deleted",
                targetType: "wall", targetId: id.ToString(),
                detail: $"Eliminó el layout '{layout.Name}' del muro {id}.");
            return Results.Ok();
        });

        app.MapPost("/api/walls/{id:int}/layouts/{layoutId:int}/apply",
            async (HttpContext ctx, int id, int layoutId, WallService walls, VmsDbContext db, AuditService audit) =>
            {
                if (ApiSecurity.Require(ctx, Permissions.WallOperate, out var session) is { } failure) return failure;
                string layoutName = await db.WallLayouts.Where(l => l.Id == layoutId && l.VideoWallId == id)
                    .Select(l => l.Name).FirstOrDefaultAsync() ?? $"layout {layoutId}";
                // Un layout con cámaras fuera del alcance no se aplica (pondría en el muro lo que no puede ver).
                var scope = await ctx.ScopeAsync(session);
                if (scope.FiltersView)
                {
                    var channels = await db.WallLayouts.Where(l => l.Id == layoutId && l.VideoWallId == id)
                        .SelectMany(l => l.Items.Select(i => i.ChannelId)).ToListAsync();
                    if (channels.Any(c => !scope.CanViewChannel(c)))
                        return await ctx.OutOfScopeAsync(session, "wall-layout", layoutId.ToString(), layoutName,
                            "aplicar el layout (tiene cámaras fuera de su alcance)");
                }
                return await RunAuditedAsync(ctx, audit, db, id, "layout-applied",
                    $"Aplicó el layout '{layoutName}'",
                    () => walls.ApplyLayoutAsync(id, layoutId));
            });
    }
}
