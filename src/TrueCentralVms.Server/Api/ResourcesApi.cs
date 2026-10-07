using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Ficha del recurso: General (nombre, ubicación, descripción, consignas),
/// Equipo, Cámaras asociadas, Automatizaciones e Historial. Consultar puede
/// cualquier usuario; modificar, solo un administrador, y cada cambio queda en
/// la bitácora con el recurso como destino ("resource", "Door:12").
/// El tipo va en la ruta por su nombre: /api/resources/door/12.
/// </summary>
public static class ResourcesApi
{
    /// <summary>Tope de cámaras asociadas por recurso.</summary>
    private const int MaxCameras = 16;

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>"door", "Door" → ResourceKind.Door; números u otros nombres → null.</summary>
    private static ResourceKind? ParseKind(string kind) =>
        !int.TryParse(kind, out _) && Enum.TryParse<ResourceKind>(kind, ignoreCase: true, out var value) && Enum.IsDefined(value)
            ? value : null;

    public static void MapResourcesApi(this WebApplication app)
    {
        // Para el puesto del operador: del evento (panel+zona, equipo+puerta,
        // cerco, citófono, cámara) al resumen del recurso que lo causó.
        app.MapGet("/api/resources/briefing", async (HttpContext ctx, int? alarmPanel, int? area, int? zone,
            int? cercoPanel, int? accessDevice, int? door, int? intercom, int? channel, int? device, int? channelNumber,
            VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            // Con su alcance: lo que no ve no tiene resumen, y solo van sus cámaras.
            var briefing = await ResourceBriefings.ForEventAsync(db, new ResourceBriefings.EventRef(
                AlarmPanelId: alarmPanel, AreaNumber: area, ZoneNumber: zone, CercoPanelId: cercoPanel,
                AccessDeviceId: accessDevice, DoorNumber: door, IntercomId: intercom,
                ChannelId: channel, DeviceId: device, ChannelNumber: channelNumber), ct, await ctx.ScopeAsync(session));
            return briefing is null ? Results.NotFound() : Results.Ok(briefing);
        });

        app.MapGet("/api/resources/{kind}/{id:int}", async (HttpContext ctx, string kind, int id,
            VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            if (ParseKind(kind) is not { } k) return Results.NotFound();
            var scope = await ctx.ScopeAsync(session);
            if (!scope.CanViewResource(k, id)) return Results.NotFound();
            var detail = await ResourceDetails.LoadAsync(db, k, id, ct);
            if (detail is null) return Results.NotFound();
            // Las cámaras asociadas de otras ubicaciones no se le muestran (no podría abrirlas).
            return Results.Ok(scope.FiltersView
                ? detail with { Cameras = detail.Cameras.Where(c => scope.CanViewChannel(c.ChannelId)).ToList() }
                : detail);
        });

        // Pestaña General: nombre (si el tipo lo permite), ubicación, descripción y consignas.
        app.MapPut("/api/resources/{kind}/{id:int}", async (HttpContext ctx, string kind, int id,
            ResourceProfileUpdateRequest request, VmsDbContext db, IHubContext<VmsHub> hub, ScopedHub scopedHub,
            UserScopeService scopes, AccessControlService access, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (ParseKind(kind) is not { } k) return Results.NotFound();
            var before = await ResourceDetails.LoadAsync(db, k, id, ct);
            if (before is null) return Results.NotFound();

            string? description = Clean(request.Description);
            string? instructions = Clean(request.Instructions);
            if (description?.Length > 1000) return Error("La descripción no puede superar los 1000 caracteres.");
            if (instructions?.Length > 4000) return Error("Las consignas no pueden superar los 4000 caracteres.");
            string? name = request.Name?.Trim();
            if (name is not null && name != before.Resource.Name)
            {
                if (name.Length is 0 or > 128) return Error("El nombre es obligatorio (máximo 128 caracteres).");
                if (!before.CanRename) return Error(before.RenameNote ?? "Este recurso no se renombra desde la ficha.");
            }
            var locations = await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id, ct);
            if (request.LocationId is { } locationId && !locations.ContainsKey(locationId))
                return Error("La ubicación elegida ya no existe.", StatusCodes.Status404NotFound);

            var now = DateTime.UtcNow;
            var changes = new List<string>();
            var topics = new HashSet<string>();
            AccessDoor? renamedDoor = null;

            // El nombre vive en la tabla de su módulo y sigue sus reglas: una
            // puerta renombrada deja de tomar el nombre del equipo.
            if (name is not null && name != before.Resource.Name)
            {
                changes.Add($"nombre \"{before.Resource.Name}\" → \"{name}\"");
                switch (k)
                {
                    case ResourceKind.Camera:
                        (await db.Channels.FirstAsync(c => c.Id == id, ct)).Name = name;
                        topics.Add("channels");
                        break;
                    case ResourceKind.Door:
                        renamedDoor = await db.AccessDoors.Include(d => d.AccessDevice).FirstAsync(d => d.Id == id, ct);
                        renamedDoor.Name = name;
                        renamedDoor.NameFromDevice = false;
                        renamedDoor.UpdatedAt = now;
                        break;
                    case ResourceKind.Speaker:
                        var speaker = await db.Speakers.FirstAsync(s => s.Id == id, ct);
                        speaker.Name = name;
                        speaker.UpdatedAt = now;
                        topics.Add("speakers");
                        break;
                    case ResourceKind.Intercom:
                        var intercom = await db.Intercoms.FirstAsync(i => i.Id == id, ct);
                        intercom.Name = name;
                        intercom.UpdatedAt = now;
                        topics.Add("intercoms");
                        break;
                }
            }

            if (request.LocationId != before.Resource.LocationId)
            {
                await ResourceCatalog.SetLocationAsync(db, request.LocationId, [new ResourceRef(k, id)], ct);
                var path = ResourceCatalog.PathNames(request.LocationId, locations);
                changes.Add(request.LocationId is null ? "quedó por ubicar" : $"ubicación → {string.Join(" › ", path)}");
                topics.Add("locations");
            }

            if (description != before.Description || instructions != before.Instructions)
            {
                if (description != before.Description) changes.Add(description is null ? "descripción borrada" : "descripción");
                if (instructions != before.Instructions) changes.Add(instructions is null ? "consignas borradas" : "consignas");
                var profile = await ResourceProfile.Of(db.ResourceProfiles, k, id).Include(p => p.Cameras).FirstOrDefaultAsync(ct);
                if (profile is null)
                {
                    profile = ResourceProfile.For(k, id);
                    db.ResourceProfiles.Add(profile);
                }
                profile.Description = description;
                profile.Instructions = instructions;
                profile.UpdatedAt = now;
                // Una ficha vacía no se guarda (si era nueva, Remove solo la descarta).
                if (description is null && instructions is null && profile.Cameras.Count == 0)
                    db.ResourceProfiles.Remove(profile);
            }

            if (changes.Count == 0) return Results.Ok(before);
            await db.SaveChangesAsync(ct);
            // Cambió dónde está: cambia quién lo ve (alcance por ubicación).
            if (topics.Contains("locations")) scopes.Invalidate();

            await audit.LogAsync(ctx, "locations", "resource-updated",
                targetType: "resource", targetId: ResourceCatalog.Key(k, id), targetName: name ?? before.Resource.Name,
                detail: $"Modificó la ficha de {ResourceCatalog.Label(k)} \"{name ?? before.Resource.Name}\" " +
                        $"({before.Resource.Source}): {string.Join(", ", changes)}.");
            foreach (var topic in topics)
                await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, topic, cancellationToken: ct);
            if (renamedDoor is not null)
                await scopedHub.SendAsync(VmsHubContract.AccessDoorStateChanged, access.ToDoorDto(renamedDoor),
                    s => s.CanViewDoor(renamedDoor.Id), ct);
            return Results.Ok(await ResourceDetails.LoadAsync(db, k, id, ct));
        });

        // Cámaras asociadas: la lista completa, en orden (la primera es la principal).
        app.MapPut("/api/resources/{kind}/{id:int}/cameras", async (HttpContext ctx, string kind, int id,
            ResourceCamerasRequest request, VmsDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (ParseKind(kind) is not { } k) return Results.NotFound();
            if (k == ResourceKind.Camera) return Error("Una cámara no tiene cámaras asociadas.");
            var detail = await ResourceDetails.LoadAsync(db, k, id, ct);
            if (detail is null) return Results.NotFound();

            // Las fijas (p. ej. la del frente de un citófono) se configuran en su módulo.
            var fixedIds = detail.Cameras.Where(c => c.Fixed).Select(c => c.ChannelId).ToHashSet();
            var wanted = (request.ChannelIds ?? []).Distinct().Where(c => !fixedIds.Contains(c)).ToList();
            if (wanted.Count > MaxCameras) return Error($"Se pueden asociar hasta {MaxCameras} cámaras.");
            var channels = await db.Channels.AsNoTracking().Include(c => c.Device)
                .Where(c => wanted.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
            if (channels.Count != wanted.Count)
                return Error("Alguna de las cámaras ya no existe.", StatusCodes.Status404NotFound);

            var profile = await ResourceProfile.Of(db.ResourceProfiles, k, id).Include(p => p.Cameras).FirstOrDefaultAsync(ct);
            var current = profile?.Cameras.OrderBy(c => c.Position).Select(c => c.ChannelId).ToList() ?? [];
            if (current.SequenceEqual(wanted)) return Results.Ok(detail);

            if (profile is null)
            {
                profile = ResourceProfile.For(k, id);
                db.ResourceProfiles.Add(profile);
            }
            var existing = profile.Cameras.ToDictionary(c => c.ChannelId);
            foreach (var gone in profile.Cameras.Where(c => !wanted.Contains(c.ChannelId)).ToList())
                db.ResourceProfileCameras.Remove(gone);
            for (int i = 0; i < wanted.Count; i++)
            {
                if (existing.TryGetValue(wanted[i], out var row)) row.Position = i;
                else profile.Cameras.Add(new ResourceProfileCamera { ChannelId = wanted[i], Position = i });
            }
            profile.UpdatedAt = DateTime.UtcNow;
            if (wanted.Count == 0 && profile.Description is null && profile.Instructions is null)
                db.ResourceProfiles.Remove(profile);
            await db.SaveChangesAsync(ct);

            string list = wanted.Count == 0 ? "sin cámaras asociadas"
                : string.Join(", ", wanted.Select((c, i) =>
                    $"\"{channels[c].Name}\" ({channels[c].Device.Name}){(i == 0 ? " como principal" : "")}"));
            await audit.LogAsync(ctx, "locations", "resource-cameras-updated",
                targetType: "resource", targetId: ResourceCatalog.Key(k, id), targetName: detail.Resource.Name,
                detail: $"Cámaras asociadas de {ResourceCatalog.Label(k)} \"{detail.Resource.Name}\": {list}.");
            return Results.Ok(await ResourceDetails.LoadAsync(db, k, id, ct));
        });

        app.MapGet("/api/resources/{kind}/{id:int}/workflows", async (HttpContext ctx, string kind, int id,
            VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            if (ParseKind(kind) is not { } k) return Results.NotFound();
            if (!(await ctx.ScopeAsync(session)).CanViewResource(k, id)) return Results.NotFound();
            var uses = await ResourceUsage.FindAsync(db, k, id, ct);
            return uses is null ? Results.NotFound() : Results.Ok(uses);
        });

        app.MapGet("/api/resources/{kind}/{id:int}/history", async (HttpContext ctx, string kind, int id, int? take,
            VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            if (ParseKind(kind) is not { } k) return Results.NotFound();
            if (!(await ctx.ScopeAsync(session)).CanViewResource(k, id)) return Results.NotFound();
            var history = await ResourceHistory.LoadAsync(db, k, id, take ?? 60, ct);
            return history is null ? Results.NotFound() : Results.Ok(history);
        });
    }
}
