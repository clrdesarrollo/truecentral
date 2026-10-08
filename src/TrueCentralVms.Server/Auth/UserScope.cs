using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Auth;

/// <summary>
/// Dónde está cada recurso en el árbol de ubicaciones, indexado como lo
/// nombran las APIs y los eventos (que no traen la ubicación): un canal por
/// id, por equipo + número o por equipo + canal RTSP; un área o una zona por
/// panel + número (la zona sin ubicación propia toma la de su área); una
/// puerta por id o por equipo + número. También a qué equipo pertenece cada
/// recurso y su nombre (para los alcances por recurso suelto).
/// </summary>
public sealed class ScopeIndex
{
    public static readonly ScopeIndex Empty = new();

    public IReadOnlyDictionary<int, int?> Channels { get; init; } = new Dictionary<int, int?>();
    public IReadOnlyDictionary<(int DeviceId, int Number), int> ChannelByNumber { get; init; } = new Dictionary<(int, int), int>();
    public IReadOnlyDictionary<(int DeviceId, int Rtsp), int> ChannelByRtsp { get; init; } = new Dictionary<(int, int), int>();
    public IReadOnlyDictionary<int, int[]> DeviceChannels { get; init; } = new Dictionary<int, int[]>();
    public IReadOnlyDictionary<(int PanelId, int Number), int?> Areas { get; init; } = new Dictionary<(int, int), int?>();
    public IReadOnlyDictionary<(int PanelId, int Number), int?> Zones { get; init; } = new Dictionary<(int, int), int?>();
    /// <summary>Área y zona por su id de fila (como las nombra Recursos: "Zone:3").</summary>
    public IReadOnlyDictionary<int, int?> AreaRows { get; init; } = new Dictionary<int, int?>();
    public IReadOnlyDictionary<int, int?> ZoneRows { get; init; } = new Dictionary<int, int?>();
    /// <summary>Id de fila de un área o zona por panel + número, y al revés.</summary>
    public IReadOnlyDictionary<(int PanelId, int Number), int> AreaIdByNumber { get; init; } = new Dictionary<(int, int), int>();
    public IReadOnlyDictionary<(int PanelId, int Number), int> ZoneIdByNumber { get; init; } = new Dictionary<(int, int), int>();
    public IReadOnlyDictionary<int, (int PanelId, int Number)> AreaKeyByRow { get; init; } = new Dictionary<int, (int, int)>();
    public IReadOnlyDictionary<int, (int PanelId, int Number)> ZoneKeyByRow { get; init; } = new Dictionary<int, (int, int)>();
    /// <summary>Área (fila) a la que pertenece cada zona (fila), si tiene.</summary>
    public IReadOnlyDictionary<int, int> ZoneArea { get; init; } = new Dictionary<int, int>();
    /// <summary>Filas de áreas y zonas de cada panel.</summary>
    public IReadOnlyDictionary<int, int[]> PanelAreaRows { get; init; } = new Dictionary<int, int[]>();
    public IReadOnlyDictionary<int, int[]> PanelZoneRows { get; init; } = new Dictionary<int, int[]>();
    public IReadOnlyDictionary<int, int?[]> PanelLocations { get; init; } = new Dictionary<int, int?[]>();
    public IReadOnlyDictionary<int, int?> Doors { get; init; } = new Dictionary<int, int?>();
    public IReadOnlyDictionary<(int DeviceId, int Number), int> DoorByNumber { get; init; } = new Dictionary<(int, int), int>();
    public IReadOnlyDictionary<int, int[]> AccessDeviceDoors { get; init; } = new Dictionary<int, int[]>();
    public IReadOnlyDictionary<int, int?> Cercos { get; init; } = new Dictionary<int, int?>();
    public IReadOnlyDictionary<int, int?> Speakers { get; init; } = new Dictionary<int, int?>();
    public IReadOnlyDictionary<int, int?> Intercoms { get; init; } = new Dictionary<int, int?>();
    /// <summary>Nombre legible por (tipo de <see cref="ScopeKinds"/>, id): "DVR Norte · Acceso".</summary>
    public IReadOnlyDictionary<(string Kind, int Id), string> Names { get; init; } = new Dictionary<(string, int), string>();
    /// <summary>Árbol de ubicaciones: id → (padre, nombre).</summary>
    public IReadOnlyDictionary<int, (int? ParentId, string Name)> Locations { get; init; } = new Dictionary<int, (int?, string)>();

    /// <summary>La ubicación con todas sus sububicaciones.</summary>
    public HashSet<int> Subtree(IEnumerable<int> roots)
    {
        var children = Locations.Where(l => l.Value.ParentId is not null)
            .GroupBy(l => l.Value.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Key).ToList());
        var result = new HashSet<int>();
        var stack = new Stack<int>(roots.Where(Locations.ContainsKey));
        while (stack.Count > 0)
        {
            int id = stack.Pop();
            if (!result.Add(id)) continue;
            if (children.TryGetValue(id, out var list))
                foreach (int child in list) stack.Push(child);
        }
        return result;
    }

    /// <summary>Las ubicaciones con todas las que las contienen (para armar el árbol que se muestra).</summary>
    public HashSet<int> WithAncestors(IEnumerable<int> ids)
    {
        var result = new HashSet<int>();
        foreach (int start in ids)
        {
            int? current = start;
            while (current is { } id && Locations.TryGetValue(id, out var node) && result.Add(id))
                current = node.ParentId;
        }
        return result;
    }

    /// <summary>"Casa matriz › Edificio A" (para mostrar el alcance de un usuario).</summary>
    public string PathOf(int locationId)
    {
        var parts = new List<string>();
        var seen = new HashSet<int>();
        int? current = locationId;
        while (current is { } id && seen.Add(id) && Locations.TryGetValue(id, out var node))
        {
            parts.Insert(0, node.Name);
            current = node.ParentId;
        }
        return string.Join(" › ", parts);
    }

    /// <summary>Nombre de un recurso del alcance, o null si ya no existe.</summary>
    public string? NameOf(string kind, int id) => Names.TryGetValue((kind, id), out var name) ? name : null;

    /// <summary>"Cámara DVR Norte · Acceso" (o "(ya no existe)").</summary>
    public string LabelOf(string kind, int id) =>
        $"{ScopeKinds.Labels.GetValueOrDefault(kind, kind)} {NameOf(kind, id) ?? $"#{id} (ya no existe)"}";

    internal static async Task<ScopeIndex> LoadAsync(VmsDbContext db, CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking().Select(d => new { d.Id, d.Name }).ToListAsync(ct);
        var channels = await db.Channels.AsNoTracking()
            .Select(c => new { c.Id, c.DeviceId, c.ChannelNumber, c.RtspChannel, c.LocationId, c.Name }).ToListAsync(ct);
        var panels = await db.AlarmPanels.AsNoTracking().Select(p => new { p.Id, p.Name }).ToListAsync(ct);
        var areas = await db.AlarmAreas.AsNoTracking()
            .Select(a => new { a.Id, a.AlarmPanelId, a.Number, a.LocationId, a.Name }).ToListAsync(ct);
        var zones = await db.AlarmZones.AsNoTracking()
            .Select(z => new { z.Id, z.AlarmPanelId, z.Number, z.AreaNumber, z.LocationId, z.Name }).ToListAsync(ct);
        var accessDevices = await db.AccessDevices.AsNoTracking().Select(d => new { d.Id, d.Name }).ToListAsync(ct);
        var doors = await db.AccessDoors.AsNoTracking()
            .Select(d => new { d.Id, d.AccessDeviceId, d.Number, d.LocationId, d.Name }).ToListAsync(ct);
        var cercos = await db.CercoPanels.AsNoTracking().Select(p => new { p.Id, p.LocationId, p.Name }).ToListAsync(ct);
        var speakers = await db.Speakers.AsNoTracking().Select(s => new { s.Id, s.LocationId, s.Name }).ToListAsync(ct);
        var intercoms = await db.Intercoms.AsNoTracking().Select(i => new { i.Id, i.LocationId, i.Name }).ToListAsync(ct);
        var locations = await db.Locations.AsNoTracking()
            .Select(l => new { l.Id, l.ParentId, l.Name }).ToListAsync(ct);

        var areaMap = new Dictionary<(int, int), int?>();
        var areaIds = new Dictionary<(int, int), int>();
        foreach (var a in areas)
        {
            areaMap[(a.AlarmPanelId, a.Number)] = a.LocationId;
            areaIds[(a.AlarmPanelId, a.Number)] = a.Id;
        }
        // La zona sin ubicación propia queda donde está su área.
        var zoneMap = new Dictionary<(int, int), int?>();
        var zoneIds = new Dictionary<(int, int), int>();
        var zoneRows = new Dictionary<int, int?>();
        var zoneArea = new Dictionary<int, int>();
        foreach (var z in zones)
        {
            int? location = z.LocationId
                ?? (z.AreaNumber is { } an ? areaMap.GetValueOrDefault((z.AlarmPanelId, an)) : null);
            zoneMap[(z.AlarmPanelId, z.Number)] = location;
            zoneIds[(z.AlarmPanelId, z.Number)] = z.Id;
            zoneRows[z.Id] = location;
            if (z.AreaNumber is { } number && areaIds.TryGetValue((z.AlarmPanelId, number), out int areaRow))
                zoneArea[z.Id] = areaRow;
        }

        var byNumber = new Dictionary<(int, int), int>();
        var byRtsp = new Dictionary<(int, int), int>();
        foreach (var c in channels)
        {
            byNumber.TryAdd((c.DeviceId, c.ChannelNumber), c.Id);
            byRtsp.TryAdd((c.DeviceId, c.RtspChannel), c.Id);
        }
        var doorByNumber = new Dictionary<(int, int), int>();
        foreach (var d in doors) doorByNumber.TryAdd((d.AccessDeviceId, d.Number), d.Id);

        var deviceNames = devices.ToDictionary(d => d.Id, d => d.Name);
        var panelNames = panels.ToDictionary(p => p.Id, p => p.Name);
        var accessNames = accessDevices.ToDictionary(d => d.Id, d => d.Name);
        var names = new Dictionary<(string, int), string>();
        foreach (var d in devices) names[(ScopeKinds.VideoDevice, d.Id)] = d.Name;
        foreach (var c in channels) names[(ScopeKinds.Camera, c.Id)] = $"{deviceNames.GetValueOrDefault(c.DeviceId, "?")} · {c.Name}";
        foreach (var p in panels) names[(ScopeKinds.AlarmPanel, p.Id)] = p.Name;
        foreach (var a in areas) names[(ScopeKinds.Partition, a.Id)] = $"{panelNames.GetValueOrDefault(a.AlarmPanelId, "?")} · {a.Name}";
        foreach (var z in zones) names[(ScopeKinds.Zone, z.Id)] = $"{panelNames.GetValueOrDefault(z.AlarmPanelId, "?")} · {z.Name}";
        foreach (var d in accessDevices) names[(ScopeKinds.AccessDevice, d.Id)] = d.Name;
        foreach (var d in doors) names[(ScopeKinds.Door, d.Id)] = $"{accessNames.GetValueOrDefault(d.AccessDeviceId, "?")} · {d.Name}";
        foreach (var p in cercos) names[(ScopeKinds.Fence, p.Id)] = p.Name;
        foreach (var s in speakers) names[(ScopeKinds.Speaker, s.Id)] = s.Name;
        foreach (var i in intercoms) names[(ScopeKinds.Intercom, i.Id)] = i.Name;

        return new ScopeIndex
        {
            Channels = channels.ToDictionary(c => c.Id, c => c.LocationId),
            ChannelByNumber = byNumber,
            ChannelByRtsp = byRtsp,
            DeviceChannels = channels.GroupBy(c => c.DeviceId).ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToArray()),
            Areas = areaMap,
            Zones = zoneMap,
            AreaRows = areas.ToDictionary(a => a.Id, a => a.LocationId),
            ZoneRows = zoneRows,
            AreaIdByNumber = areaIds,
            ZoneIdByNumber = zoneIds,
            AreaKeyByRow = areas.ToDictionary(a => a.Id, a => (a.AlarmPanelId, a.Number)),
            ZoneKeyByRow = zones.ToDictionary(z => z.Id, z => (z.AlarmPanelId, z.Number)),
            ZoneArea = zoneArea,
            PanelAreaRows = areas.GroupBy(a => a.AlarmPanelId).ToDictionary(g => g.Key, g => g.Select(a => a.Id).ToArray()),
            PanelZoneRows = zones.GroupBy(z => z.AlarmPanelId).ToDictionary(g => g.Key, g => g.Select(z => z.Id).ToArray()),
            PanelLocations = areaMap.Select(p => (p.Key.Item1, p.Value)).Concat(zoneMap.Select(p => (p.Key.Item1, p.Value)))
                .GroupBy(p => p.Item1).ToDictionary(g => g.Key, g => g.Select(p => p.Value).Distinct().ToArray()),
            Doors = doors.ToDictionary(d => d.Id, d => d.LocationId),
            DoorByNumber = doorByNumber,
            AccessDeviceDoors = doors.GroupBy(d => d.AccessDeviceId).ToDictionary(g => g.Key, g => g.Select(d => d.Id).ToArray()),
            Cercos = cercos.ToDictionary(p => p.Id, p => p.LocationId),
            Speakers = speakers.ToDictionary(s => s.Id, s => s.LocationId),
            Intercoms = intercoms.ToDictionary(i => i.Id, i => i.LocationId),
            Names = names,
            Locations = locations.ToDictionary(l => l.Id, l => (l.ParentId, l.Name)),
        };
    }
}

/// <summary>
/// Un alcance tal como se configuró: el de un rol o el límite propio de un
/// usuario. Sin <see cref="Restrict"/> no limita nada.
/// </summary>
public sealed record ScopeSpec(bool Restrict, bool ViewOutside, IReadOnlyCollection<int> Locations,
    IReadOnlyCollection<(string Kind, int Id)> Items)
{
    public static readonly ScopeSpec Everything = new(false, false, [], []);
}

/// <summary>Lo que un alcance limitado deja operar, recurso por recurso (ids de fila).</summary>
internal sealed class ScopeSets
{
    public static readonly ScopeSets Nothing = new();

    /// <summary>Ubicaciones COMPLETAS (con sus sububicaciones): las órdenes por ubicación.</summary>
    public HashSet<int> Locations { get; init; } = [];
    /// <summary>Ubicaciones que se le muestran en el árbol (las completas y donde hay algo suyo).</summary>
    public HashSet<int> VisibleTree { get; init; } = [];
    public HashSet<int> Channels { get; init; } = [];
    public HashSet<int> Doors { get; init; } = [];
    public HashSet<int> Areas { get; init; } = [];
    public HashSet<int> Zones { get; init; } = [];
    public HashSet<int> Cercos { get; init; } = [];
    public HashSet<int> Speakers { get; init; } = [];
    public HashSet<int> Intercoms { get; init; } = [];
}

/// <summary>
/// Alcance efectivo de un usuario: DÓNDE valen sus permisos. Un administrador
/// (o el superadministrador) no tiene restricción. El resto suma el alcance de
/// sus roles —un rol sin límite da todo; los limitados dan sus ubicaciones
/// (con sububicaciones) y sus recursos sueltos— y, si tiene un límite propio
/// por ubicación, de eso queda solo lo que está dentro de él. Lo limitado ve y
/// opera solo lo suyo; con <see cref="ViewOutside"/> además VE el resto, sin
/// operarlo. Lo que no se pudo ubicar en el índice queda fuera (falla cerrada).
/// </summary>
public sealed class UserScope
{
    private static readonly IReadOnlySet<int> NoIds = new HashSet<int>();

    private readonly ScopeIndex _index;
    /// <summary>null = sin restricción.</summary>
    private readonly ScopeSets? _sets;

    internal UserScope(int userId, bool isAdmin, bool isSuperAdmin, bool viewOutside, ScopeSets? sets,
        ScopeIndex index, IReadOnlySet<string>? permissions, IReadOnlyList<string>? labels,
        IReadOnlyList<int>? grantedLocations)
    {
        UserId = userId;
        IsAdmin = isAdmin || isSuperAdmin;
        IsSuperAdmin = isSuperAdmin;
        Permissions = permissions ?? new HashSet<string>();
        _sets = IsAdmin ? null : sets;
        ViewOutside = _sets is not null && viewOutside;
        Labels = labels ?? [];
        GrantedLocations = grantedLocations ?? [];
        _index = index;
    }

    public int UserId { get; }
    public bool IsAdmin { get; }
    /// <summary>El administrador creado al activar la plataforma (no le afectan los roles).</summary>
    public bool IsSuperAdmin { get; }
    /// <summary>Unión de los permisos de sus roles (el administrador los tiene todos, aunque no estén aquí).</summary>
    public IReadOnlySet<string> Permissions { get; }

    /// <summary>¿Su rol le permite esto? (QUÉ puede hacer; DÓNDE lo dicen CanView/CanOperate).</summary>
    public bool Has(string permission) => IsAdmin || Permissions.Contains(permission);

    /// <summary>Las claves de permiso efectivas (todas, si es administrador).</summary>
    public IReadOnlyCollection<string> EffectivePermissions =>
        IsAdmin ? Core.Domain.Permissions.All.Select(p => p.Key).ToList() : Permissions.Order().ToList();
    /// <summary>Ve y opera todo (administrador, o roles sin límite y sin límite propio).</summary>
    public bool Unrestricted => _sets is null;
    /// <summary>Limitado para operar, pero ve todo (supervisión).</summary>
    public bool ViewOutside { get; }
    /// <summary>Ubicaciones COMPLETAS que opera (con sus sububicaciones): las órdenes por ubicación.</summary>
    public IReadOnlySet<int> Locations => _sets?.Locations ?? NoIds;
    /// <summary>Las ubicaciones de sus roles y de su límite, tal como se configuraron (para mostrarlas).</summary>
    public IReadOnlyList<int> GrantedLocations { get; }
    /// <summary>El alcance en palabras: ubicaciones y recursos sueltos (vacío si no tiene restricción).</summary>
    public IReadOnlyList<string> Labels { get; }
    /// <summary>Hay que filtrar lo que ve (listas, eventos, video).</summary>
    public bool FiltersView => !Unrestricted && !ViewOutside;

    private static bool In(HashSet<int> set, int? id) => id is { } value && set.Contains(value);

    /// <summary>Una ubicación COMPLETA (órdenes y listados por ubicación).</summary>
    public bool CanView(int? locationId) => !FiltersView || In(_sets!.Locations, locationId);
    public bool CanOperate(int? locationId) => Unrestricted || In(_sets!.Locations, locationId);
    /// <summary>La ubicación se le muestra en el árbol (completa o porque hay algo suyo adentro).</summary>
    public bool CanSeeLocation(int locationId) => !FiltersView || _sets!.VisibleTree.Contains(locationId);
    /// <summary>Ubicaciones que se le muestran en el árbol (solo con la vista filtrada).</summary>
    public IReadOnlySet<int> VisibleLocations => _sets?.VisibleTree ?? NoIds;

    /// <summary>Ids de fila de lo que opera de un tipo (para filtrar en SQL cuando <see cref="FiltersView"/>).</summary>
    public IReadOnlySet<int> AllowedIds(ResourceKind kind) => _sets is not { } s ? NoIds : kind switch
    {
        ResourceKind.Camera => s.Channels,
        ResourceKind.Door => s.Doors,
        ResourceKind.Partition => s.Areas,
        ResourceKind.Zone => s.Zones,
        ResourceKind.Fence => s.Cercos,
        ResourceKind.Speaker => s.Speakers,
        ResourceKind.Intercom => s.Intercoms,
        _ => NoIds,
    };

    // ---------- Video ----------

    private int? ChannelId(int deviceId, int number) => _index.ChannelByNumber.TryGetValue((deviceId, number), out int id) ? id : null;
    private int? RtspChannelId(int deviceId, int rtsp) => _index.ChannelByRtsp.TryGetValue((deviceId, rtsp), out int id) ? id : null;

    public bool CanViewChannel(int channelId) => !FiltersView || _sets!.Channels.Contains(channelId);
    public bool CanOperateChannel(int channelId) => Unrestricted || _sets!.Channels.Contains(channelId);
    public bool CanViewChannel(int deviceId, int channelNumber) =>
        !FiltersView || (ChannelId(deviceId, channelNumber) is { } id && CanViewChannel(id));
    public bool CanOperateChannel(int deviceId, int channelNumber) =>
        Unrestricted || (ChannelId(deviceId, channelNumber) is { } id && CanOperateChannel(id));
    public bool CanViewRtsp(int deviceId, int rtspChannel) =>
        !FiltersView || (RtspChannelId(deviceId, rtspChannel) is { } id && CanViewChannel(id));
    /// <summary>Un equipo de video se ve si se ve alguno de sus canales.</summary>
    public bool CanViewDevice(int deviceId) =>
        !FiltersView || (_index.DeviceChannels.TryGetValue(deviceId, out var ids) && ids.Any(CanViewChannel));

    // ---------- Alarmas ----------

    private int? AreaRow(int panelId, int number) => _index.AreaIdByNumber.TryGetValue((panelId, number), out int id) ? id : null;
    private int? ZoneRow(int panelId, int number) => _index.ZoneIdByNumber.TryGetValue((panelId, number), out int id) ? id : null;

    public bool CanViewArea(int panelId, int number) => !FiltersView || In(_sets!.Areas, AreaRow(panelId, number));
    public bool CanOperateArea(int panelId, int number) => Unrestricted || In(_sets!.Areas, AreaRow(panelId, number));
    public bool CanViewZone(int panelId, int number) => !FiltersView || In(_sets!.Zones, ZoneRow(panelId, number));
    public bool CanOperateZone(int panelId, int number) => Unrestricted || In(_sets!.Zones, ZoneRow(panelId, number));
    /// <summary>Un panel se ve si se ve alguna de sus áreas o zonas.</summary>
    public bool CanViewPanel(int panelId) =>
        !FiltersView ||
        (_index.PanelAreaRows.TryGetValue(panelId, out var areas) && areas.Any(_sets!.Areas.Contains)) ||
        (_index.PanelZoneRows.TryGetValue(panelId, out var zones) && zones.Any(_sets!.Zones.Contains));
    /// <summary>Las órdenes de "todo el panel" (área 0) exigen poder operar todas sus áreas.</summary>
    public bool CanOperateAllAreas(int panelId) =>
        Unrestricted || (_index.PanelAreaRows.TryGetValue(panelId, out var rows) && rows.Length > 0 && rows.All(_sets!.Areas.Contains));
    /// <summary>Evento de un panel: el de su zona, si no el de su área, si no el del panel.</summary>
    public bool CanViewAlarmEvent(int panelId, int? areaNumber, int? zoneNumber) =>
        !FiltersView || (zoneNumber is { } z ? CanViewZone(panelId, z)
            : areaNumber is { } a ? CanViewArea(panelId, a)
            : CanViewPanel(panelId));

    /// <summary>Clave numérica (panel, número) para filtrar en SQL: <c>panel * 100000 + número</c>.</summary>
    public static long AlarmKey(int panelId, int number) => (long)panelId * 100_000 + number;

    /// <summary>Áreas y zonas que ve (como <see cref="AlarmKey"/>) y los paneles con algo visible.</summary>
    public (List<long> Areas, List<long> Zones, List<int> Panels) VisibleAlarmKeys()
    {
        var sets = _sets ?? ScopeSets.Nothing;
        var areas = sets.Areas.Where(_index.AreaKeyByRow.ContainsKey).Select(r => _index.AreaKeyByRow[r]).ToList();
        var zones = sets.Zones.Where(_index.ZoneKeyByRow.ContainsKey).Select(r => _index.ZoneKeyByRow[r]).ToList();
        return (
            areas.Select(k => AlarmKey(k.PanelId, k.Number)).ToList(),
            zones.Select(k => AlarmKey(k.PanelId, k.Number)).ToList(),
            areas.Select(k => k.PanelId).Concat(zones.Select(k => k.PanelId)).Distinct().ToList());
    }

    /// <summary>El panel con solo las áreas y zonas que este usuario ve (null si no ve ninguna).</summary>
    public AlarmPanelDto? Visible(AlarmPanelDto panel)
    {
        if (!FiltersView) return panel;
        var zones = panel.Zones.Where(z => CanViewZone(panel.Id, z.Number)).ToList();
        var areas = panel.Areas.Where(a => CanViewArea(panel.Id, a.Number))
            .Select(a => a with { ZoneCount = zones.Count(z => z.AreaNumber == a.Number) }).ToList();
        return areas.Count == 0 && zones.Count == 0 ? null : panel with { Areas = areas, Zones = zones };
    }

    // ---------- Control de acceso ----------

    public bool CanViewDoor(int doorId) => !FiltersView || _sets!.Doors.Contains(doorId);
    public bool CanOperateDoor(int doorId) => Unrestricted || _sets!.Doors.Contains(doorId);
    public bool CanViewDoor(int deviceId, int doorNumber) =>
        !FiltersView || (_index.DoorByNumber.TryGetValue((deviceId, doorNumber), out int id) && CanViewDoor(id));
    /// <summary>Un equipo de acceso se ve si se ve alguna de sus puertas.</summary>
    public bool CanViewAccessDevice(int deviceId) =>
        !FiltersView || (_index.AccessDeviceDoors.TryGetValue(deviceId, out var ids) && ids.Any(CanViewDoor));
    public bool CanOperateAccessDevice(int deviceId) =>
        Unrestricted || (_index.AccessDeviceDoors.TryGetValue(deviceId, out var ids) && ids.Any(CanOperateDoor));
    /// <summary>Evento de acceso: el de su puerta o, si no trae puerta, el del equipo.</summary>
    public bool CanViewAccessEvent(int deviceId, int? doorNumber) =>
        !FiltersView || (doorNumber is { } n ? CanViewDoor(deviceId, n) : CanViewAccessDevice(deviceId));

    /// <summary>El equipo con solo las puertas que este usuario ve (null si no ve ninguna).</summary>
    public AccessDeviceDto? Visible(AccessDeviceDto device)
    {
        if (!FiltersView) return device;
        var doors = device.Doors.Where(d => CanViewDoor(d.Id)).ToList();
        return doors.Count == 0 ? null : device with { Doors = doors, DoorCount = doors.Count };
    }

    // ---------- Cerco, parlantes y citofonía ----------

    public bool CanViewCerco(int panelId) => !FiltersView || _sets!.Cercos.Contains(panelId);
    public bool CanOperateCerco(int panelId) => Unrestricted || _sets!.Cercos.Contains(panelId);
    public bool CanViewSpeaker(int speakerId) => !FiltersView || _sets!.Speakers.Contains(speakerId);
    public bool CanOperateSpeaker(int speakerId) => Unrestricted || _sets!.Speakers.Contains(speakerId);
    public bool CanViewIntercom(int intercomId) => !FiltersView || _sets!.Intercoms.Contains(intercomId);
    public bool CanOperateIntercom(int intercomId) => Unrestricted || _sets!.Intercoms.Contains(intercomId);

    // ---------- Recursos (clave "Kind:Id" de Recursos y de las alertas) ----------

    /// <summary>Ubicación de un recurso por su tipo e id de fila (null = por ubicar o desconocido).</summary>
    public int? LocationOf(ResourceKind kind, int id) => kind switch
    {
        ResourceKind.Camera => _index.Channels.GetValueOrDefault(id),
        ResourceKind.Door => _index.Doors.GetValueOrDefault(id),
        ResourceKind.Partition => _index.AreaRows.GetValueOrDefault(id),
        ResourceKind.Zone => _index.ZoneRows.GetValueOrDefault(id),
        ResourceKind.Fence => _index.Cercos.GetValueOrDefault(id),
        ResourceKind.Speaker => _index.Speakers.GetValueOrDefault(id),
        ResourceKind.Intercom => _index.Intercoms.GetValueOrDefault(id),
        _ => null,
    };

    public bool CanViewResource(ResourceKind kind, int id) => !FiltersView || AllowedIds(kind).Contains(id);
    public bool CanOperateResource(ResourceKind kind, int id) => Unrestricted || AllowedIds(kind).Contains(id);

    /// <summary>Claves "Kind:Id" de todos los recursos que ve (para filtrar alertas en SQL).</summary>
    public List<string> VisibleResourceKeys()
    {
        var keys = new List<string>();
        foreach (var kind in Enum.GetValues<ResourceKind>())
            foreach (int id in AllowedIds(kind))
                keys.Add($"{kind}:{id}");
        return keys;
    }

    /// <summary>
    /// Lo que esta sesión puede operar, para que la interfaz no ofrezca órdenes
    /// que el servidor rechazaría. Mismas reglas que las validaciones de cada orden.
    /// </summary>
    public OperableDto Operable()
    {
        if (_sets is not { } s) return new OperableDto(true, [], [], [], [], [], [], [], [], []);
        static string Key((int PanelId, int Number) k) => $"{k.PanelId}/{k.Number}";
        return new OperableDto(
            false,
            s.Locations.Order().ToList(),
            s.Channels.Order().ToList(),
            s.Areas.Where(_index.AreaKeyByRow.ContainsKey).Select(r => Key(_index.AreaKeyByRow[r])).Order().ToList(),
            s.Zones.Where(_index.ZoneKeyByRow.ContainsKey).Select(r => Key(_index.ZoneKeyByRow[r])).Order().ToList(),
            _index.PanelAreaRows.Keys.Where(CanOperateAllAreas).Order().ToList(),
            s.Doors.Order().ToList(),
            s.Cercos.Order().ToList(),
            s.Speakers.Order().ToList(),
            s.Intercoms.Order().ToList());
    }

    /// <summary>Recurso por su clave "Kind:Id" (la de Recursos y de las alertas); sin clave no hay a qué restringir.</summary>
    public bool CanViewResourceKey(string? key)
    {
        if (!FiltersView || string.IsNullOrEmpty(key)) return true;
        var parts = key.Split(':');
        return parts.Length == 2 && Enum.TryParse<ResourceKind>(parts[0], out var kind) && int.TryParse(parts[1], out int id)
               && CanViewResource(kind, id);
    }

    /// <summary>
    /// ¿El alcance de <paramref name="other"/> cabe en este? (contra el
    /// escalamiento: nadie da más de lo que tiene). Sin restricción cabe todo;
    /// si no, el otro debe estar limitado, a recursos que este opera, y sin
    /// "ver el resto" si este no lo tiene.
    /// </summary>
    public bool Covers(UserScope other)
    {
        if (Unrestricted) return true;
        if (other.Unrestricted) return false;
        if (other.ViewOutside && !ViewOutside) return false;
        var a = _sets!;
        var b = other._sets!;
        return b.Locations.IsSubsetOf(a.Locations) && b.Channels.IsSubsetOf(a.Channels) &&
               b.Doors.IsSubsetOf(a.Doors) && b.Areas.IsSubsetOf(a.Areas) && b.Zones.IsSubsetOf(a.Zones) &&
               b.Cercos.IsSubsetOf(a.Cercos) && b.Speakers.IsSubsetOf(a.Speakers) && b.Intercoms.IsSubsetOf(a.Intercoms);
    }
}

/// <summary>
/// Arma el alcance efectivo de un usuario a partir de sus roles y de su límite
/// propio. Lo usa la foto de alcances y también la API de usuarios, para saber
/// ANTES de guardar con qué alcance quedaría alguien.
/// </summary>
public static class ScopeBuilder
{
    private const int MaxLabels = 12;

    public static UserScope Build(int userId, bool isAdmin, bool isSuperAdmin, IReadOnlySet<string> permissions,
        IReadOnlyCollection<ScopeSpec> roles, ScopeSpec own, ScopeIndex index)
    {
        if (isAdmin || isSuperAdmin)
            return new UserScope(userId, true, isSuperAdmin, false, null, index, permissions, null, null);

        var limited = roles.Where(r => r.Restrict).ToList();
        // Basta un rol sin límite para que sus roles den todo (los permisos también se suman).
        bool rolesAll = roles.Count == 0 || limited.Count < roles.Count;
        bool ownAll = !own.Restrict;
        if (rolesAll && ownAll)
            return new UserScope(userId, false, false, false, null, index, permissions, null, null);

        bool viewOutside = (rolesAll || limited.Any(r => r.ViewOutside)) && (ownAll || own.ViewOutside);
        var roleLocations = rolesAll ? [] : limited.SelectMany(r => r.Locations).Distinct().ToList();
        HashSet<int>? granted = rolesAll ? null : index.Subtree(roleLocations);
        HashSet<int>? limit = ownAll ? null : index.Subtree(own.Locations);
        var items = rolesAll ? new Expanded() : Expand(limited.SelectMany(r => r.Items), index);

        bool InLimit(int? location) => limit is null || (location is { } l && limit.Contains(l));
        bool InGrant(int? location) => granted is null || (location is { } l && granted.Contains(l));
        HashSet<int> Pick(IReadOnlyDictionary<int, int?> map, HashSet<int> explicitIds) =>
            map.Where(p => (InGrant(p.Value) || explicitIds.Contains(p.Key)) && InLimit(p.Value))
                .Select(p => p.Key).ToHashSet();

        var channels = Pick(index.Channels, items.Channels);
        var doors = Pick(index.Doors, items.Doors);
        var areas = Pick(index.AreaRows, items.Areas);
        var zones = Pick(index.ZoneRows, items.Zones);
        var cercos = Pick(index.Cercos, items.Cercos);
        var speakers = Pick(index.Speakers, items.Speakers);
        var intercoms = Pick(index.Intercoms, items.Intercoms);
        var wholeLocations = index.Locations.Keys.Where(id => InGrant(id) && InLimit(id)).ToHashSet();

        // El árbol que se le muestra: las ubicaciones completas y, para lo que
        // tiene suelto, la ubicación donde está con las que la contienen.
        var looseLocations = new List<int?>();
        void Add(IEnumerable<int> ids, IReadOnlyDictionary<int, int?> map)
        {
            foreach (int id in ids)
                if (map.GetValueOrDefault(id) is { } l && !wholeLocations.Contains(l)) looseLocations.Add(l);
        }
        Add(channels, index.Channels);
        Add(doors, index.Doors);
        Add(areas, index.AreaRows);
        Add(zones, index.ZoneRows);
        Add(cercos, index.Cercos);
        Add(speakers, index.Speakers);
        Add(intercoms, index.Intercoms);
        var tree = new HashSet<int>(wholeLocations);
        tree.UnionWith(index.WithAncestors(looseLocations.OfType<int>()));

        var sets = new ScopeSets
        {
            Locations = wholeLocations, VisibleTree = tree,
            Channels = channels, Doors = doors, Areas = areas, Zones = zones,
            Cercos = cercos, Speakers = speakers, Intercoms = intercoms,
        };

        // En palabras: lo de sus roles y el límite propio.
        var labels = new List<string>();
        if (!rolesAll)
        {
            var parts = roleLocations.Select(index.PathOf).Where(n => n.Length > 0).Order()
                .Concat(limited.SelectMany(r => r.Items).Distinct().Select(i => index.LabelOf(i.Kind, i.Id)).Order())
                .ToList();
            labels.AddRange(parts.Take(MaxLabels));
            if (parts.Count > MaxLabels) labels.Add($"… y {parts.Count - MaxLabels} más");
            if (parts.Count == 0) labels.Add("Nada (sus roles no tienen ubicaciones ni recursos)");
        }
        if (!ownAll)
        {
            var names = own.Locations.Select(index.PathOf).Where(n => n.Length > 0).Order().ToList();
            labels.Add(rolesAll
                ? (names.Count > 0 ? string.Join(", ", names) : "Ninguna ubicación")
                : "Solo dentro de: " + (names.Count > 0 ? string.Join(", ", names) : "ninguna ubicación"));
        }
        var grantedLocations = roleLocations.Concat(own.Locations).Distinct().Order().ToList();
        return new UserScope(userId, false, false, viewOutside, sets, index, permissions, labels, grantedLocations);
    }

    /// <summary>"Todo el sistema", o el alcance en una línea (para listas y bitácora).</summary>
    public static string Summary(UserScope scope) =>
        scope.Unrestricted
            ? "Todo el sistema"
            : string.Join("; ", scope.Labels) + (scope.ViewOutside ? " (ve el resto sin operarlo)" : "");

    /// <summary>Recursos sueltos expandidos a ids de fila (un equipo completo = todo lo que tiene).</summary>
    private sealed class Expanded
    {
        public HashSet<int> Channels { get; } = [];
        public HashSet<int> Doors { get; } = [];
        public HashSet<int> Areas { get; } = [];
        public HashSet<int> Zones { get; } = [];
        public HashSet<int> Cercos { get; } = [];
        public HashSet<int> Speakers { get; } = [];
        public HashSet<int> Intercoms { get; } = [];
    }

    private static Expanded Expand(IEnumerable<(string Kind, int Id)> items, ScopeIndex index)
    {
        var result = new Expanded();
        foreach (var (kind, id) in items)
        {
            switch (kind)
            {
                case ScopeKinds.Camera: result.Channels.Add(id); break;
                case ScopeKinds.VideoDevice:
                    if (index.DeviceChannels.TryGetValue(id, out var channels)) result.Channels.UnionWith(channels);
                    break;
                case ScopeKinds.Door: result.Doors.Add(id); break;
                case ScopeKinds.AccessDevice:
                    if (index.AccessDeviceDoors.TryGetValue(id, out var doors)) result.Doors.UnionWith(doors);
                    break;
                case ScopeKinds.Partition: result.Areas.Add(id); break;
                case ScopeKinds.Zone: result.Zones.Add(id); break;
                case ScopeKinds.AlarmPanel:
                    if (index.PanelAreaRows.TryGetValue(id, out var areas)) result.Areas.UnionWith(areas);
                    if (index.PanelZoneRows.TryGetValue(id, out var zones)) result.Zones.UnionWith(zones);
                    break;
                case ScopeKinds.Fence: result.Cercos.Add(id); break;
                case ScopeKinds.Speaker: result.Speakers.Add(id); break;
                case ScopeKinds.Intercom: result.Intercoms.Add(id); break;
            }
        }
        // Un área incluye sus zonas.
        foreach (var (zone, area) in index.ZoneArea)
            if (result.Areas.Contains(area)) result.Zones.Add(zone);
        return result;
    }
}

/// <summary>
/// Ruta legible de una ubicación ("Casa matriz › Edificio A") para los DTO que
/// se arman sin consultar la base (mapeadores, avisos del hub). Usa el árbol en
/// memoria de <see cref="UserScopeService"/>, que se relee al arrancar y en
/// cuanto cambian las ubicaciones.
/// </summary>
public static class LocationPaths
{
    private static ScopeIndex _index = ScopeIndex.Empty;

    internal static void Update(ScopeIndex index) => Volatile.Write(ref _index, index);

    public static string? Of(int? locationId) =>
        locationId is { } id && Volatile.Read(ref _index).PathOf(id) is { Length: > 0 } path ? path : null;
}

/// <summary>Foto de los alcances de todos los usuarios habilitados y de dónde está cada recurso.</summary>
public sealed class ScopeSnapshot
{
    public required ScopeIndex Index { get; init; }
    public required IReadOnlyDictionary<int, UserScope> Users { get; init; }
    /// <summary>Algún usuario habilitado tiene la vista filtrada (si no, todo sale a todos como siempre).</summary>
    public bool AnyFiltering => Users.Values.Any(u => u.FiltersView);

    public bool TryGet(int userId, out UserScope scope) => Users.TryGetValue(userId, out scope!);

    /// <summary>El alcance del usuario; uno desconocido (borrado, deshabilitado) no ve nada.</summary>
    public UserScope For(int userId) => TryGet(userId, out var scope)
        ? scope
        : new UserScope(userId, false, false, false, ScopeSets.Nothing, Index, null, null, null);
}

/// <summary>
/// Alcances de los usuarios, en memoria. Se rehacen al cambiar usuarios, roles,
/// ubicaciones o recursos (<see cref="Invalidate"/>) y, por las dudas, cada
/// 30 s: un recurso recién creado no tiene ubicación y queda fuera de todo
/// alcance limitado por ubicación hasta que se ubique.
/// </summary>
public sealed class UserScopeService(IServiceScopeFactory scopeFactory, ILogger<UserScopeService> logger)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ScopeSnapshot? _snapshot;
    /// <summary>La última buena: si la base falla, se sigue con ella antes que abrir de más.</summary>
    private ScopeSnapshot? _lastGood;
    private DateTime _builtAt;
    private int _version;

    public void Invalidate()
    {
        Interlocked.Increment(ref _version);
        Volatile.Write(ref _snapshot, null);
        // Se relee en segundo plano: las rutas de ubicación de los avisos
        // (LocationPaths) y el próximo pedido no esperan a la base.
        _ = Task.Run(async () =>
        {
            try { await SnapshotAsync(); }
            catch (Exception ex) { logger.LogDebug(ex, "No se pudieron releer los alcances; el próximo pedido reintenta."); }
        });
    }

    public async Task<ScopeSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _snapshot) is { } current && DateTime.UtcNow - _builtAt < MaxAge) return current;
        await _gate.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _snapshot) is { } again && DateTime.UtcNow - _builtAt < MaxAge) return again;
            int version = Volatile.Read(ref _version);
            ScopeSnapshot built;
            try
            {
                built = await BuildAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && _lastGood is not null)
            {
                logger.LogWarning(ex, "No se pudieron releer los alcances por ubicación; se usa la última versión.");
                return _lastGood;
            }
            _lastGood = built;
            // Si alguien invalidó mientras se leía, la próxima consulta vuelve a leer.
            if (version == Volatile.Read(ref _version))
            {
                _builtAt = DateTime.UtcNow;
                Volatile.Write(ref _snapshot, built);
            }
            return built;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UserScope> ForUserAsync(int userId, CancellationToken ct = default)
    {
        var snapshot = await SnapshotAsync(ct);
        if (snapshot.TryGet(userId, out var scope)) return scope;
        // Usuario creado o habilitado después de la foto: se relee una vez.
        Invalidate();
        return (await SnapshotAsync(ct)).For(userId);
    }

    /// <summary>El alcance que declara un rol (para armar el de sus usuarios).</summary>
    public static ScopeSpec SpecOf(Data.Entities.Role role) => new(
        role.RestrictScope, role.ViewOutsideScope,
        role.Locations.Select(l => l.LocationId).ToList(),
        role.Resources.Select(r => (r.Kind, r.ResourceId)).ToList());

    private async Task<ScopeSnapshot> BuildAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var index = await ScopeIndex.LoadAsync(db, ct);
        LocationPaths.Update(index);
        var users = await db.Users.AsNoTracking().Where(u => u.Enabled)
            .Select(u => new
            {
                u.Id, u.IsSuperAdmin, u.RestrictToLocations, u.ViewOutsideScope,
                Locations = u.Locations.Select(l => l.LocationId).ToList(),
                RoleIds = u.Roles.Select(r => r.RoleId).ToList(),
            })
            .ToListAsync(ct);
        var roles = await db.Roles.AsNoTracking()
            .Include(r => r.Permissions).Include(r => r.Locations).Include(r => r.Resources)
            .AsSplitQuery()
            .ToDictionaryAsync(r => r.Id, ct);

        var map = new Dictionary<int, UserScope>();
        foreach (var u in users)
        {
            var mine = u.RoleIds.Select(id => roles.GetValueOrDefault(id)).OfType<Data.Entities.Role>().ToList();
            bool admin = mine.Any(r => r.IsAdmin);
            // Permisos = unión de los de sus roles; las claves que ya no están
            // en el catálogo (versión anterior) no cuentan.
            var permissions = new HashSet<string>(mine.SelectMany(r => r.Permissions.Select(p => p.Permission))
                .Where(Core.Domain.Permissions.IsValid));
            var own = new ScopeSpec(u.RestrictToLocations, u.ViewOutsideScope, u.Locations, []);
            map[u.Id] = ScopeBuilder.Build(u.Id, admin, u.IsSuperAdmin, permissions, mine.Select(SpecOf).ToList(), own, index);
        }
        return new ScopeSnapshot { Index = index, Users = map };
    }
}

/// <summary>
/// Filtros por alcance dentro de las consultas (no después): así los topes de
/// filas y la paginación cuentan solo lo que el usuario puede ver.
/// </summary>
public static class ScopeQueries
{
    /// <summary>Eventos de acceso: el de una puerta va con su puerta; el del equipo (sin puerta), con el equipo.</summary>
    public static IQueryable<Data.Entities.AccessEvent> AccessEvents(VmsDbContext db,
        IQueryable<Data.Entities.AccessEvent> query, UserScope scope)
    {
        if (!scope.FiltersView) return query;
        var allowed = scope.AllowedIds(ResourceKind.Door).ToList();
        return query.Where(e =>
            (e.DoorNumber != null && db.AccessDoors.Any(d => d.AccessDeviceId == e.AccessDeviceId && d.Number == e.DoorNumber
                                                              && allowed.Contains(d.Id))) ||
            (e.DoorNumber == null && db.AccessDoors.Any(d => d.AccessDeviceId == e.AccessDeviceId && allowed.Contains(d.Id))));
    }
}

/// <summary>El alcance dentro de un endpoint, y la respuesta cuando algo queda fuera.</summary>
public static class ScopeHttp
{
    /// <summary>Alcance del usuario de la solicitud (se calcula una vez por solicitud).</summary>
    public static async Task<UserScope> ScopeAsync(this HttpContext ctx, SessionInfo session)
    {
        if (ctx.Items["scope"] is UserScope cached && cached.UserId == session.UserId) return cached;
        var scope = await ctx.RequestServices.GetRequiredService<UserScopeService>()
            .ForUserAsync(session.UserId, ctx.RequestAborted);
        ctx.Items["scope"] = scope;
        return scope;
    }

    /// <summary>
    /// 403 "fuera de su alcance", y queda en la bitácora (sin repetirse por
    /// cada reintento del mismo recurso): la interfaz no ofrece lo que está
    /// fuera, así que un intento suele ser una pantalla desactualizada o una
    /// solicitud armada a mano.
    /// </summary>
    public static async Task<IResult> OutOfScopeAsync(this HttpContext ctx, SessionInfo session,
        string targetType, string targetId, string? targetName, string attempted)
    {
        var audit = ctx.RequestServices.GetRequiredService<AuditService>();
        if (audit.ShouldLog($"scope-denied:{session.UserId}:{targetType}:{targetId}:{attempted}", TimeSpan.FromMinutes(5)))
            await audit.LogAsync(ctx, "auth", "scope-denied",
                targetType: targetType, targetId: targetId, targetName: targetName,
                detail: $"Intentó {attempted} '{targetName ?? targetId}', fuera de su alcance.",
                success: false);
        return Results.Json(new { error = "Ese recurso está fuera de su alcance (las ubicaciones y recursos de sus roles)." },
            statusCode: StatusCodes.Status403Forbidden);
    }
}
