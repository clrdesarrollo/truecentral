using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Del evento al recurso: cuando una zona, una puerta o un cerco avisa algo,
/// encuentra su recurso y arma lo que el operador necesita para verificarlo
/// (<see cref="ResourceBriefingDto"/>: ubicación, consignas y cámaras asociadas).
/// Lo usan el puesto del operador y la acción "Avisar" de las automatizaciones.
/// </summary>
public static class ResourceBriefings
{
    /// <summary>Lo que un evento dice de su origen (cada módulo llena lo suyo).</summary>
    public sealed record EventRef(
        int? AlarmPanelId = null, int? AreaNumber = null, int? ZoneNumber = null,
        int? CercoPanelId = null,
        int? AccessDeviceId = null, int? DoorNumber = null,
        int? IntercomId = null,
        int? ChannelId = null, int? DeviceId = null, int? ChannelNumber = null);

    /// <summary>Recursos a los que apunta el evento, del más específico al más general (la zona antes que su área).</summary>
    public static async Task<List<ResourceRef>> CandidatesAsync(VmsDbContext db, EventRef e, CancellationToken ct)
    {
        var list = new List<ResourceRef>();

        if (e.AlarmPanelId is { } panel)
        {
            int? area = e.AreaNumber is > 0 ? e.AreaNumber : null;
            if (e.ZoneNumber is { } zoneNumber)
            {
                var zone = await db.AlarmZones.AsNoTracking()
                    .Where(z => z.AlarmPanelId == panel && z.Number == zoneNumber)
                    .Select(z => new { z.Id, z.AreaNumber }).FirstOrDefaultAsync(ct);
                if (zone is not null)
                {
                    list.Add(new ResourceRef(ResourceKind.Zone, zone.Id));
                    area ??= zone.AreaNumber;
                }
            }
            if (area is { } areaNumber)
            {
                int? areaId = await db.AlarmAreas.AsNoTracking()
                    .Where(a => a.AlarmPanelId == panel && a.Number == areaNumber)
                    .Select(a => (int?)a.Id).FirstOrDefaultAsync(ct);
                if (areaId is { } id) list.Add(new ResourceRef(ResourceKind.Partition, id));
            }
        }

        if (e.CercoPanelId is { } cerco && await db.CercoPanels.AnyAsync(p => p.Id == cerco, ct))
            list.Add(new ResourceRef(ResourceKind.Fence, cerco));

        if (e.AccessDeviceId is { } accessDevice && e.DoorNumber is { } door)
        {
            int? doorId = await db.AccessDoors.AsNoTracking()
                .Where(d => d.AccessDeviceId == accessDevice && d.Number == door)
                .Select(d => (int?)d.Id).FirstOrDefaultAsync(ct);
            if (doorId is { } id) list.Add(new ResourceRef(ResourceKind.Door, id));
        }

        if (e.IntercomId is { } intercom && await db.Intercoms.AnyAsync(i => i.Id == intercom, ct))
            list.Add(new ResourceRef(ResourceKind.Intercom, intercom));

        int? channelId = e.ChannelId;
        if (channelId is null && e.DeviceId is { } device && e.ChannelNumber is { } number)
            channelId = await db.Channels.AsNoTracking()
                .Where(c => c.DeviceId == device && c.ChannelNumber == number)
                .Select(c => (int?)c.Id).FirstOrDefaultAsync(ct);
        if (channelId is { } channel && await db.Channels.AnyAsync(c => c.Id == channel, ct))
            list.Add(new ResourceRef(ResourceKind.Camera, channel));

        return list;
    }

    /// <summary>
    /// El primer candidato que tenga consignas o cámaras asociadas; si ninguno
    /// tiene, el más específico (para al menos decir dónde está). null si el
    /// evento no apunta a ningún recurso conocido. El primero que existe es el
    /// ORIGEN del evento (<see cref="ResourceBriefingDto.OriginKey"/>). Con
    /// <paramref name="scope"/> (el puesto de un operador): si el origen está
    /// fuera de su alcance no hay resumen, se saltan los demás recursos ajenos
    /// y quedan solo las cámaras que puede ver.
    /// </summary>
    public static async Task<ResourceBriefingDto?> ForEventAsync(VmsDbContext db, EventRef e, CancellationToken ct,
        Auth.UserScope? scope = null)
    {
        ResourceBriefingDto? fallback = null;
        string? origin = null;
        string? originPath = null;
        foreach (var candidate in await CandidatesAsync(db, e, ct))
        {
            var detail = await ResourceDetails.LoadAsync(db, candidate.Kind, candidate.Id, ct);
            if (detail is null) continue;
            bool visible = scope is null || scope.CanViewResource(candidate.Kind, candidate.Id);
            if (origin is null)
            {
                // El recurso del evento decide quién lo ve (no el que aporta las
                // consignas) y dónde pasó: una zona ubicada aparte de su área
                // avisa con SU ubicación aunque use las consignas del área.
                origin = ResourceCatalog.Key(candidate.Kind, candidate.Id);
                originPath = detail.LocationPath;
                if (!visible) return null;
            }
            if (!visible) continue;
            var cameras = scope is { FiltersView: true }
                ? detail.Cameras.Where(c => scope.CanViewChannel(c.ChannelId)).ToList()
                : detail.Cameras;
            var briefing = new ResourceBriefingDto(candidate.Kind, candidate.Id, detail.Resource.Name,
                originPath ?? detail.LocationPath, detail.Description, detail.Instructions, cameras, origin);
            if (briefing.HasGuidance) return briefing;
            // Sin consignas ni cámaras: sirve igual para decir dónde está, y
            // vale más el que tenga ubicación que el que no.
            if (fallback is null || (fallback.LocationPath is null && briefing.LocationPath is not null))
                fallback = briefing;
        }
        return fallback;
    }
}
