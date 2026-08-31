using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// API del módulo Reconocimiento de patentes: consulta del historial, fotos de
/// cada lectura y administración de qué equipos son fuente.
///
/// Los reconocimientos en vivo NO se consultan por aquí: llegan empujados por
/// el hub (<see cref="VmsHubContract.PlateRecognized"/>). Estos endpoints son
/// para poblar la lista al abrir la pantalla y para buscar hacia atrás.
/// </summary>
public static class AnprApi
{
    /// <summary>Tope duro de la consulta: la pantalla trabaja con una ventana, no con toda la base.</summary>
    private const int MaxTake = 500;

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    public static void MapAnprApi(this WebApplication app)
    {
        // ------------------------------------------------------------------
        // Historial de reconocimientos
        // ------------------------------------------------------------------
        app.MapGet("/api/anpr/events", async (HttpContext ctx, VmsDbContext db,
            int? deviceId, string? plate, DateTime? from, DateTime? to, int? take) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;

            var query = db.PlateEvents.AsNoTracking().Include(p => p.Device).AsQueryable();
            if (deviceId is > 0)
                query = query.Where(p => p.DeviceId == deviceId);
            if (!string.IsNullOrWhiteSpace(plate))
            {
                // Búsqueda parcial y sin formato: "bb12" encuentra "BBBB12".
                string needle = new string(plate.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
                if (needle.Length > 0)
                    query = query.Where(p => p.PlateNumber.Contains(needle));
            }
            if (from is { } fromValue)
                query = query.Where(p => p.CapturedAt >= fromValue);
            if (to is { } toValue)
                query = query.Where(p => p.CapturedAt <= toValue);

            var events = await query
                .OrderByDescending(p => p.ReceivedAt)
                .Take(Math.Clamp(take ?? 100, 1, MaxTake))
                .ToListAsync();

            // Los nombres de canal se resuelven en un solo viaje (la lista
            // puede traer 500 filas de varios equipos).
            var deviceIds = events.Select(e => e.DeviceId).Distinct().ToList();
            var channels = await db.Channels.AsNoTracking()
                .Where(c => deviceIds.Contains(c.DeviceId))
                .Select(c => new { c.DeviceId, c.ChannelNumber, c.Name })
                .ToListAsync();
            var channelNames = channels.ToDictionary(c => (c.DeviceId, c.ChannelNumber), c => c.Name);

            return Results.Ok(events.Select(e => AnprMapper.ToDto(e, e.Device.Name,
                channelNames.GetValueOrDefault((e.DeviceId, e.ChannelNumber)) ?? $"Canal {e.ChannelNumber}")));
        });

        // ------------------------------------------------------------------
        // Fotos de un reconocimiento (escena completa y primer plano de la placa)
        // ------------------------------------------------------------------
        app.MapGet("/api/anpr/events/{id:long}/{kind}", async (HttpContext ctx, VmsDbContext db, AnprStore store,
            long id, string kind) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            if (kind is not ("scene" or "plate"))
                return Error("Imagen desconocida: use 'scene' o 'plate'.", StatusCodes.Status404NotFound);

            var record = await db.PlateEvents.AsNoTracking()
                .Where(p => p.Id == id)
                .Select(p => new { p.SceneImagePath, p.PlateImagePath })
                .FirstOrDefaultAsync();
            if (record is null)
                return Error("El reconocimiento no existe.", StatusCodes.Status404NotFound);

            string? relative = kind == "scene" ? record.SceneImagePath : record.PlateImagePath;
            if (store.FullPath(relative) is not { } path || !File.Exists(path))
                return Error("El equipo no envió esa imagen.", StatusCodes.Status404NotFound);

            // Las fotos son inmutables: se pueden cachear sin miedo.
            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(path, "image/jpeg");
        });

        // ------------------------------------------------------------------
        // Fuentes: qué equipos entregan patentes y cuáles están activos
        // ------------------------------------------------------------------
        app.MapGet("/api/anpr/sources", async (HttpContext ctx, VmsDbContext db, DriverRegistry drivers, AnprService anpr) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;

            var capableKeys = drivers.All
                .Where(f => f.Capabilities.SupportsAnpr)
                .Select(f => f.DriverKey)
                .ToList();

            var devices = await db.Devices.AsNoTracking()
                .Where(d => capableKeys.Contains(d.DriverKey))
                .OrderBy(d => d.Name)
                .ToListAsync();

            var counts = await db.PlateEvents.AsNoTracking()
                .GroupBy(p => p.DeviceId)
                .Select(g => new { DeviceId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.DeviceId, x => x.Count);

            return Results.Ok(devices.Select(d => new AnprSourceDto(
                d.Id, d.Name, d.DriverKey, d.Host, d.Status,
                d.AnprEnabled, anpr.IsLive(d.Id), anpr.LastErrorOf(d.Id), anpr.LastEventAtOf(d.Id),
                counts.GetValueOrDefault(d.Id))));
        });

        app.MapPut("/api/anpr/sources/{deviceId:int}", async (HttpContext ctx, VmsDbContext db, DriverRegistry drivers,
            AnprService anpr, IHubContext<VmsHub> hub, int deviceId, AnprSourceWriteDto request) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;

            var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId);
            if (device is null)
                return Error("El dispositivo no existe.", StatusCodes.Status404NotFound);
            if (request.Enabled && (drivers.Find(device.DriverKey) is not { } factory || !factory.Capabilities.SupportsAnpr))
                return Error($"El driver '{device.DriverKey}' no entrega reconocimientos de patentes.");

            if (device.AnprEnabled != request.Enabled)
            {
                device.AnprEnabled = request.Enabled;
                device.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                // Sin esperar al ciclo de 20 s: el operador quiere ver el
                // resultado de su clic de inmediato.
                if (!request.Enabled) await anpr.DetachAsync(device.Id);
                anpr.RequestReconcile();
                await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "anpr-sources");
            }
            return Results.Ok(new { ok = true, enabled = device.AnprEnabled });
        });

        // ------------------------------------------------------------------
        // Borrado del historial (administrador)
        // ------------------------------------------------------------------
        app.MapDelete("/api/anpr/events/{id:long}", async (HttpContext ctx, VmsDbContext db, AnprStore store, long id) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var record = await db.PlateEvents.FirstOrDefaultAsync(p => p.Id == id);
            if (record is null)
                return Error("El reconocimiento no existe.", StatusCodes.Status404NotFound);
            store.Delete([record.SceneImagePath, record.PlateImagePath]);
            db.PlateEvents.Remove(record);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });
    }
}
