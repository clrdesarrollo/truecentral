using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Vista unificada de los recursos —canales, puertas, áreas y zonas de alarma,
/// cercos, parlantes y citófonos— para la página Recursos. Cada recurso sigue
/// viviendo en la tabla de su módulo: aquí solo se proyecta a una forma común
/// (<see cref="ResourceDto"/>) y se cambia su <c>LocationId</c>.
/// </summary>
public static class ResourceCatalog
{
    /// <summary>Qué recursos traer: los de un conjunto de ubicaciones, los que
    /// no tienen ubicación, uno solo (<paramref name="Only"/>) o todos.</summary>
    public sealed record Filter(List<int>? LocationIds = null, bool UnassignedOnly = false, ResourceRef? Only = null)
    {
        public bool Wants(ResourceKind kind) => Only is null || Only.Kind == kind;
    }

    /// <summary>Clave estable de un recurso ("Door:12"): la usa la bitácora para encontrarlo.</summary>
    public static string Key(ResourceKind kind, int id) => $"{kind}:{id}";

    public static async Task<List<ResourceDto>> ListAsync(VmsDbContext db, Filter filter, CancellationToken ct)
    {
        var result = new List<ResourceDto>();

        if (filter.Wants(ResourceKind.Camera))
        {
            var channels = await Scope(db.Channels, filter)
                .Select(c => new
                {
                    c.Id, c.Name, c.LocationId, c.ChannelNumber, c.Enabled, c.IsOnline,
                    c.DeviceId, Device = c.Device.Name, DeviceStatus = c.Device.Status,
                })
                .ToListAsync(ct);
            foreach (var c in channels)
            {
                var (health, state) = !c.Enabled ? (ResourceHealth.Disabled, "Deshabilitada")
                    : c.DeviceStatus switch
                    {
                        DeviceStatus.Online when c.IsOnline => (ResourceHealth.Ok, "En línea"),
                        DeviceStatus.Online => (ResourceHealth.Offline, "Sin señal"),
                        DeviceStatus.Unknown => (ResourceHealth.Unknown, "—"),
                        _ => (ResourceHealth.Offline, "Equipo sin conexión"),
                    };
                result.Add(new ResourceDto(ResourceKind.Camera, c.Id, c.Name, c.LocationId,
                    c.Device, "devices", c.DeviceId, $"Canal {c.ChannelNumber}", health, state, c.Enabled));
            }
        }

        if (filter.Wants(ResourceKind.Door))
        {
            var doors = await Scope(db.AccessDoors, filter)
                .Select(d => new
                {
                    d.Id, d.Name, d.LocationId, d.Number, d.Enabled, d.Mode, d.IsOpen,
                    d.AccessDeviceId, Device = d.AccessDevice!.Name,
                    DeviceEnabled = d.AccessDevice!.Enabled, DeviceStatus = d.AccessDevice!.Status,
                })
                .ToListAsync(ct);
            foreach (var d in doors)
            {
                var (health, state) =
                    !d.DeviceEnabled ? (ResourceHealth.Disabled, "Equipo pausado")
                    : !d.Enabled ? (ResourceHealth.Disabled, "Deshabilitada")
                    : d.DeviceStatus is AccessDeviceStatus.Offline or AccessDeviceStatus.AuthFailed
                        ? (ResourceHealth.Offline, "Equipo sin conexión")
                    : d.Mode == AccessDoorMode.RemainOpen ? (ResourceHealth.Warning, "Siempre abierta")
                    : d.Mode == AccessDoorMode.RemainLocked ? (ResourceHealth.Warning, "Siempre cerrada")
                    : d.IsOpen == true ? (ResourceHealth.Warning, "Abierta")
                    : d.IsOpen == false ? (ResourceHealth.Ok, "Cerrada")
                    : d.DeviceStatus == AccessDeviceStatus.Online ? (ResourceHealth.Ok, "En línea")
                    : (ResourceHealth.Unknown, "—");
                result.Add(new ResourceDto(ResourceKind.Door, d.Id, d.Name, d.LocationId,
                    d.Device, "access", d.AccessDeviceId, $"Puerta {d.Number}", health, state, d.Enabled));
            }
        }

        if (filter.Wants(ResourceKind.Partition))
        {
            var areas = await Scope(db.AlarmAreas, filter)
                .Select(a => new
                {
                    a.Id, a.Name, a.LocationId, a.Number, a.Enabled, a.ArmState, a.InAlarm,
                    a.AlarmPanelId, Panel = a.AlarmPanel.Name,
                    PanelEnabled = a.AlarmPanel.Enabled, PanelStatus = a.AlarmPanel.Status,
                })
                .ToListAsync(ct);
            foreach (var a in areas)
            {
                var (health, state) =
                    !a.PanelEnabled ? (ResourceHealth.Disabled, "Panel pausado")
                    : !a.Enabled ? (ResourceHealth.Disabled, "Deshabilitada")
                    : a.PanelStatus is AlarmPanelStatus.Offline or AlarmPanelStatus.AuthFailed
                        ? (ResourceHealth.Offline, "Panel sin conexión")
                    : a.InAlarm ? (ResourceHealth.Alarm, "En alarma")
                    : a.ArmState switch
                    {
                        AlarmArmState.Away => (ResourceHealth.Ok, "Armada (total)"),
                        AlarmArmState.Stay => (ResourceHealth.Ok, "Armada (parcial)"),
                        AlarmArmState.Vacation => (ResourceHealth.Ok, "Armada (vacaciones)"),
                        AlarmArmState.Arming => (ResourceHealth.Warning, "Armando…"),
                        AlarmArmState.Disarmed => (ResourceHealth.Ok, "Desarmada"),
                        _ => (ResourceHealth.Unknown, "—"),
                    };
                result.Add(new ResourceDto(ResourceKind.Partition, a.Id, a.Name, a.LocationId,
                    a.Panel, "alarm-panels", a.AlarmPanelId, $"Área {a.Number}", health, state, a.Enabled));
            }
        }

        if (filter.Wants(ResourceKind.Zone))
        {
            var zones = await Scope(db.AlarmZones, filter)
                .Select(z => new
                {
                    z.Id, z.Name, z.LocationId, z.Number, z.Status, z.Bypassed, z.InAlarm, z.Tamper, z.LowBattery,
                    z.AlarmPanelId, Panel = z.AlarmPanel.Name,
                    PanelEnabled = z.AlarmPanel.Enabled, PanelStatus = z.AlarmPanel.Status,
                })
                .ToListAsync(ct);
            foreach (var z in zones)
            {
                var (health, state) =
                    !z.PanelEnabled ? (ResourceHealth.Disabled, "Panel pausado")
                    : z.PanelStatus is AlarmPanelStatus.Offline or AlarmPanelStatus.AuthFailed
                        ? (ResourceHealth.Offline, "Panel sin conexión")
                    : z.InAlarm ? (ResourceHealth.Alarm, "En alarma")
                    : z.Tamper ? (ResourceHealth.Alarm, "Sabotaje")
                    : z.Bypassed ? (ResourceHealth.Warning, "Anulada")
                    : z.Status switch
                    {
                        AlarmZoneStatus.Triggered => (ResourceHealth.Warning, "Activada"),
                        AlarmZoneStatus.Fault => (ResourceHealth.Warning, "Falla"),
                        AlarmZoneStatus.Offline => (ResourceHealth.Offline, "Sin comunicación"),
                        AlarmZoneStatus.NotConfigured => (ResourceHealth.Disabled, "Sin configurar"),
                        AlarmZoneStatus.Normal when z.LowBattery => (ResourceHealth.Warning, "Batería baja"),
                        AlarmZoneStatus.Normal => (ResourceHealth.Ok, "Normal"),
                        _ => (ResourceHealth.Unknown, "—"),
                    };
                result.Add(new ResourceDto(ResourceKind.Zone, z.Id, z.Name, z.LocationId,
                    z.Panel, "alarm-panels", z.AlarmPanelId, $"Zona {z.Number}", health, state,
                    z.Status != AlarmZoneStatus.NotConfigured));
            }
        }

        if (filter.Wants(ResourceKind.Fence))
        {
            var fences = await Scope(db.CercoPanels, filter)
                .Select(p => new
                {
                    p.Id, p.Name, p.LocationId, p.DeviceId, p.Model, p.Enabled, p.Enrolled, p.Status,
                    p.Armed, p.Siren, p.HvOk, p.ArcFault,
                })
                .ToListAsync(ct);
            foreach (var p in fences)
            {
                var (health, state) =
                    !p.Enabled ? (ResourceHealth.Disabled, "Pausado")
                    : !p.Enrolled ? (ResourceHealth.Unknown, "Sin enrolar")
                    : p.Status == CercoPanelStatus.Offline ? (ResourceHealth.Offline, "Sin conexión")
                    : p.Status == CercoPanelStatus.Unknown ? (ResourceHealth.Unknown, "—")
                    : p.Siren ? (ResourceHealth.Alarm, "Sirena activa")
                    : p.ArcFault ? (ResourceHealth.Warning, "Falla de arco")
                    : !p.HvOk ? (ResourceHealth.Warning, "Falla de alta tensión")
                    : p.Armed ? (ResourceHealth.Ok, "Armado")
                    : (ResourceHealth.Ok, "Desarmado");
                result.Add(new ResourceDto(ResourceKind.Fence, p.Id, p.Name, p.LocationId,
                    p.Model ?? "Panel de cerco", "cerco-panels", p.Id, $"ID {p.DeviceId}", health, state, p.Enabled));
            }
        }

        if (filter.Wants(ResourceKind.Speaker))
        {
            var speakers = await Scope(db.Speakers, filter)
                .Select(s => new { s.Id, s.Name, s.LocationId, s.Host, s.Model, s.Enabled, s.Status })
                .ToListAsync(ct);
            foreach (var s in speakers)
            {
                var (health, state) = !s.Enabled ? (ResourceHealth.Disabled, "Pausado")
                    : s.Status switch
                    {
                        SpeakerStatus.Online => (ResourceHealth.Ok, "En línea"),
                        SpeakerStatus.Offline => (ResourceHealth.Offline, "Sin conexión"),
                        SpeakerStatus.AuthFailed => (ResourceHealth.Offline, "Credenciales rechazadas"),
                        _ => (ResourceHealth.Unknown, "—"),
                    };
                result.Add(new ResourceDto(ResourceKind.Speaker, s.Id, s.Name, s.LocationId,
                    s.Model ?? "Parlante IP", "speakers", s.Id, s.Host, health, state, s.Enabled));
            }
        }

        if (filter.Wants(ResourceKind.Intercom))
        {
            var intercoms = await Scope(db.Intercoms, filter)
                .Select(i => new { i.Id, i.Name, i.LocationId, i.Host, i.Model, i.Enabled, i.Status })
                .ToListAsync(ct);
            foreach (var i in intercoms)
            {
                var (health, state) = !i.Enabled ? (ResourceHealth.Disabled, "Pausado")
                    : i.Status switch
                    {
                        IntercomStatus.Online => (ResourceHealth.Ok, "En línea"),
                        IntercomStatus.Offline => (ResourceHealth.Offline, "Sin conexión"),
                        IntercomStatus.AuthFailed => (ResourceHealth.Offline, "Credenciales rechazadas"),
                        _ => (ResourceHealth.Unknown, "—"),
                    };
                result.Add(new ResourceDto(ResourceKind.Intercom, i.Id, i.Name, i.LocationId,
                    i.Model ?? "Frente de citofonía", "intercoms", i.Id, i.Host, health, state, i.Enabled));
            }
        }

        return result;
    }

    /// <summary>El recurso dado, o null si ya no existe.</summary>
    public static async Task<ResourceDto?> FindAsync(VmsDbContext db, ResourceKind kind, int id, CancellationToken ct) =>
        (await ListAsync(db, new Filter(Only: new ResourceRef(kind, id)), ct)).FirstOrDefault();

    /// <summary>Recursos ubicados directamente en cada ubicación (sin sumar las sububicaciones).</summary>
    public static async Task<Dictionary<int, int>> CountByLocationAsync(VmsDbContext db, CancellationToken ct)
    {
        var counts = new Dictionary<int, int>();
        async Task AddAsync<T>(IQueryable<T> set) where T : class, ILocatable
        {
            var rows = await set.AsNoTracking()
                .Where(x => x.LocationId != null)
                .GroupBy(x => x.LocationId!.Value)
                .Select(g => new { LocationId = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            foreach (var row in rows)
                counts[row.LocationId] = counts.GetValueOrDefault(row.LocationId) + row.Count;
        }
        await AddAsync(db.Channels);
        await AddAsync(db.AccessDoors);
        await AddAsync(db.AlarmAreas);
        await AddAsync(db.AlarmZones);
        await AddAsync(db.CercoPanels);
        await AddAsync(db.Speakers);
        await AddAsync(db.Intercoms);
        return counts;
    }

    /// <summary>
    /// Cambia la ubicación de los recursos pedidos (null = por ubicar) y
    /// devuelve los que efectivamente cambiaron, con su tipo, id y nombre para
    /// la bitácora. Los que ya estaban ahí, o que ya no existen, se ignoran.
    /// No guarda: el llamador hace SaveChanges.
    /// </summary>
    public static async Task<List<(ResourceKind Kind, int Id, string Name)>> SetLocationAsync(
        VmsDbContext db, int? locationId, IEnumerable<ResourceRef> resources, CancellationToken ct)
    {
        var changed = new List<(ResourceKind, int, string)>();
        foreach (var group in resources.GroupBy(r => r.Kind))
        {
            var ids = group.Select(r => r.Id).Distinct().ToList();
            switch (group.Key)
            {
                case ResourceKind.Camera: await ApplyAsync(db.Channels, c => c.Name); break;
                case ResourceKind.Door: await ApplyAsync(db.AccessDoors, d => d.Name); break;
                case ResourceKind.Partition: await ApplyAsync(db.AlarmAreas, a => a.Name); break;
                case ResourceKind.Zone: await ApplyAsync(db.AlarmZones, z => z.Name); break;
                case ResourceKind.Fence: await ApplyAsync(db.CercoPanels, p => p.Name); break;
                case ResourceKind.Speaker: await ApplyAsync(db.Speakers, s => s.Name); break;
                case ResourceKind.Intercom: await ApplyAsync(db.Intercoms, i => i.Name); break;
            }

            async Task ApplyAsync<T>(DbSet<T> set, Func<T, string> nameOf) where T : class, ILocatable
            {
                var items = await set.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
                foreach (var item in items.Where(x => x.LocationId != locationId))
                {
                    item.LocationId = locationId;
                    changed.Add((group.Key, item.Id, nameOf(item)));
                }
            }
        }
        return changed;
    }

    /// <summary>Nombres desde la raíz hasta la ubicación dada (incluida); vacío si no hay ubicación.</summary>
    public static List<string> PathNames(int? id, IReadOnlyDictionary<int, Location> byId)
    {
        var parts = new List<string>();
        var seen = new HashSet<int>();
        while (id is { } current && byId.TryGetValue(current, out var node) && seen.Add(current))
        {
            parts.Add(node.Name);
            id = node.ParentId;
        }
        parts.Reverse();
        return parts;
    }

    /// <summary>Etiqueta en plural para resumir en la bitácora ("3 cámaras, 1 puerta").</summary>
    public static string Summarize(IEnumerable<ResourceKind> kinds) =>
        string.Join(", ", kinds.GroupBy(k => k).OrderBy(g => g.Key)
            .Select(g => $"{g.Count()} {Label(g.Key, g.Count() != 1)}"));

    public static string Label(ResourceKind kind, bool plural = false) => kind switch
    {
        ResourceKind.Camera => plural ? "cámaras" : "cámara",
        ResourceKind.Door => plural ? "puertas" : "puerta",
        ResourceKind.Partition => plural ? "áreas de alarma" : "área de alarma",
        ResourceKind.Zone => plural ? "zonas" : "zona",
        ResourceKind.Fence => plural ? "cercos" : "cerco",
        ResourceKind.Speaker => plural ? "parlantes" : "parlante",
        ResourceKind.Intercom => plural ? "citófonos" : "citófono",
        _ => plural ? "recursos" : "recurso",
    };

    private static IQueryable<T> Scope<T>(IQueryable<T> set, Filter filter) where T : class, ILocatable
    {
        var query = set.AsNoTracking();
        if (filter.Only is { } only) return query.Where(x => x.Id == only.Id);
        if (filter.UnassignedOnly) return query.Where(x => x.LocationId == null);
        if (filter.LocationIds is { } ids) return query.Where(x => x.LocationId != null && ids.Contains(x.LocationId.Value));
        return query;
    }
}
