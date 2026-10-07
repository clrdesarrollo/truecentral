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
/// puerta por id o por equipo + número.
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
    public IReadOnlyDictionary<int, int?[]> PanelLocations { get; init; } = new Dictionary<int, int?[]>();
    public IReadOnlyDictionary<int, int?> Doors { get; init; } = new Dictionary<int, int?>();
    public IReadOnlyDictionary<(int DeviceId, int Number), int> DoorByNumber { get; init; } = new Dictionary<(int, int), int>();
    public IReadOnlyDictionary<int, int[]> AccessDeviceDoors { get; init; } = new Dictionary<int, int[]>();
    public IReadOnlyDictionary<int, int?> Cercos { get; init; } = new Dictionary<int, int?>();
    public IReadOnlyDictionary<int, int?> Speakers { get; init; } = new Dictionary<int, int?>();
    public IReadOnlyDictionary<int, int?> Intercoms { get; init; } = new Dictionary<int, int?>();
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

    internal static async Task<ScopeIndex> LoadAsync(VmsDbContext db, CancellationToken ct)
    {
        var channels = await db.Channels.AsNoTracking()
            .Select(c => new { c.Id, c.DeviceId, c.ChannelNumber, c.RtspChannel, c.LocationId }).ToListAsync(ct);
        var areas = await db.AlarmAreas.AsNoTracking()
            .Select(a => new { a.Id, a.AlarmPanelId, a.Number, a.LocationId }).ToListAsync(ct);
        var zones = await db.AlarmZones.AsNoTracking()
            .Select(z => new { z.Id, z.AlarmPanelId, z.Number, z.AreaNumber, z.LocationId }).ToListAsync(ct);
        var doors = await db.AccessDoors.AsNoTracking()
            .Select(d => new { d.Id, d.AccessDeviceId, d.Number, d.LocationId }).ToListAsync(ct);
        var locations = await db.Locations.AsNoTracking()
            .Select(l => new { l.Id, l.ParentId, l.Name }).ToListAsync(ct);

        var areaMap = new Dictionary<(int, int), int?>();
        foreach (var a in areas) areaMap[(a.AlarmPanelId, a.Number)] = a.LocationId;
        // La zona sin ubicación propia queda donde está su área.
        var zoneMap = new Dictionary<(int, int), int?>();
        var zoneRows = new Dictionary<int, int?>();
        foreach (var z in zones)
        {
            int? location = z.LocationId
                ?? (z.AreaNumber is { } an ? areaMap.GetValueOrDefault((z.AlarmPanelId, an)) : null);
            zoneMap[(z.AlarmPanelId, z.Number)] = location;
            zoneRows[z.Id] = location;
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
            PanelLocations = areaMap.Select(p => (p.Key.Item1, p.Value)).Concat(zoneMap.Select(p => (p.Key.Item1, p.Value)))
                .GroupBy(p => p.Item1).ToDictionary(g => g.Key, g => g.Select(p => p.Value).Distinct().ToArray()),
            Doors = doors.ToDictionary(d => d.Id, d => d.LocationId),
            DoorByNumber = doorByNumber,
            AccessDeviceDoors = doors.GroupBy(d => d.AccessDeviceId).ToDictionary(g => g.Key, g => g.Select(d => d.Id).ToArray()),
            Cercos = await db.CercoPanels.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.LocationId, ct),
            Speakers = await db.Speakers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.LocationId, ct),
            Intercoms = await db.Intercoms.AsNoTracking().ToDictionaryAsync(i => i.Id, i => i.LocationId, ct),
            Locations = locations.ToDictionary(l => l.Id, l => (l.ParentId, l.Name)),
        };
    }
}

/// <summary>
/// Alcance por ubicación de un usuario. Un administrador, o un operador con
/// "todas las ubicaciones", no tiene restricción. Un operador restringido ve y
/// opera solo los recursos de sus ubicaciones (cada una con sus
/// sububicaciones); con <see cref="ViewOutside"/> además VE el resto, sin
/// operarlo. Los recursos "por ubicar" quedan fuera de todo alcance
/// restringido, y lo que no se pudo ubicar en el índice también (falla cerrada).
/// </summary>
public sealed class UserScope
{
    private readonly ScopeIndex _index;

    internal UserScope(int userId, bool isAdmin, bool unrestricted, bool viewOutside,
        IReadOnlyList<int> assigned, IReadOnlySet<int> locations, ScopeIndex index)
    {
        UserId = userId;
        IsAdmin = isAdmin;
        Unrestricted = unrestricted;
        ViewOutside = viewOutside;
        AssignedLocations = assigned;
        Locations = locations;
        _index = index;
    }

    public int UserId { get; }
    public bool IsAdmin { get; }
    /// <summary>Ve y opera todo (administrador u operador con "todas las ubicaciones").</summary>
    public bool Unrestricted { get; }
    /// <summary>Restringido para operar, pero ve todo (supervisión).</summary>
    public bool ViewOutside { get; }
    /// <summary>Las ubicaciones asignadas, tal como se configuraron.</summary>
    public IReadOnlyList<int> AssignedLocations { get; }
    /// <summary>Las asignadas con todas sus sububicaciones.</summary>
    public IReadOnlySet<int> Locations { get; }
    /// <summary>Hay que filtrar lo que ve (listas, eventos, video).</summary>
    public bool FiltersView => !Unrestricted && !ViewOutside;

    private bool InScope(int? locationId) => locationId is { } id && Locations.Contains(id);

    public bool CanView(int? locationId) => !FiltersView || InScope(locationId);
    public bool CanOperate(int? locationId) => Unrestricted || InScope(locationId);

    // ---------- Video ----------

    private int? ChannelLocation(int channelId) => _index.Channels.GetValueOrDefault(channelId);
    private int? ChannelId(int deviceId, int number) => _index.ChannelByNumber.TryGetValue((deviceId, number), out int id) ? id : null;
    private int? RtspChannelId(int deviceId, int rtsp) => _index.ChannelByRtsp.TryGetValue((deviceId, rtsp), out int id) ? id : null;

    public bool CanViewChannel(int channelId) => !FiltersView || InScope(ChannelLocation(channelId));
    public bool CanOperateChannel(int channelId) => Unrestricted || InScope(ChannelLocation(channelId));
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

    private int? AreaLocation(int panelId, int number) => _index.Areas.GetValueOrDefault((panelId, number));
    private int? ZoneLocation(int panelId, int number) => _index.Zones.GetValueOrDefault((panelId, number));

    public bool CanViewArea(int panelId, int number) => !FiltersView || InScope(AreaLocation(panelId, number));
    public bool CanOperateArea(int panelId, int number) => Unrestricted || InScope(AreaLocation(panelId, number));
    public bool CanViewZone(int panelId, int number) => !FiltersView || InScope(ZoneLocation(panelId, number));
    public bool CanOperateZone(int panelId, int number) => Unrestricted || InScope(ZoneLocation(panelId, number));
    /// <summary>Un panel se ve si se ve alguna de sus áreas o zonas.</summary>
    public bool CanViewPanel(int panelId) =>
        !FiltersView || (_index.PanelLocations.TryGetValue(panelId, out var locations) && locations.Any(InScope));
    /// <summary>Las órdenes de "todo el panel" (área 0) exigen poder operar todas sus áreas.</summary>
    public bool CanOperateAllAreas(int panelId) =>
        Unrestricted || (_index.Areas.Where(a => a.Key.PanelId == panelId).ToList() is { Count: > 0 } all && all.All(a => InScope(a.Value)));
    /// <summary>Evento de un panel: el de su zona, si no el de su área, si no el del panel.</summary>
    public bool CanViewAlarmEvent(int panelId, int? areaNumber, int? zoneNumber) =>
        !FiltersView || (zoneNumber is { } z ? CanViewZone(panelId, z)
            : areaNumber is { } a ? CanViewArea(panelId, a)
            : CanViewPanel(panelId));

    /// <summary>Clave numérica (panel, número) para filtrar en SQL: <c>panel * 100000 + número</c>.</summary>
    public static long AlarmKey(int panelId, int number) => (long)panelId * 100_000 + number;

    /// <summary>Áreas y zonas que ve (como <see cref="AlarmKey"/>) y los paneles con algo visible.</summary>
    public (List<long> Areas, List<long> Zones, List<int> Panels) VisibleAlarmKeys() => (
        _index.Areas.Where(a => InScope(a.Value)).Select(a => AlarmKey(a.Key.PanelId, a.Key.Number)).ToList(),
        _index.Zones.Where(z => InScope(z.Value)).Select(z => AlarmKey(z.Key.PanelId, z.Key.Number)).ToList(),
        _index.PanelLocations.Where(p => p.Value.Any(InScope)).Select(p => p.Key).ToList());

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

    public bool CanViewDoor(int doorId) => !FiltersView || InScope(_index.Doors.GetValueOrDefault(doorId));
    public bool CanOperateDoor(int doorId) => Unrestricted || InScope(_index.Doors.GetValueOrDefault(doorId));
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

    public bool CanViewCerco(int panelId) => !FiltersView || InScope(_index.Cercos.GetValueOrDefault(panelId));
    public bool CanOperateCerco(int panelId) => Unrestricted || InScope(_index.Cercos.GetValueOrDefault(panelId));
    public bool CanViewSpeaker(int speakerId) => !FiltersView || InScope(_index.Speakers.GetValueOrDefault(speakerId));
    public bool CanOperateSpeaker(int speakerId) => Unrestricted || InScope(_index.Speakers.GetValueOrDefault(speakerId));
    public bool CanViewIntercom(int intercomId) => !FiltersView || InScope(_index.Intercoms.GetValueOrDefault(intercomId));
    public bool CanOperateIntercom(int intercomId) => Unrestricted || InScope(_index.Intercoms.GetValueOrDefault(intercomId));

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

    public bool CanViewResource(ResourceKind kind, int id) => !FiltersView || InScope(LocationOf(kind, id));
    public bool CanOperateResource(ResourceKind kind, int id) => Unrestricted || InScope(LocationOf(kind, id));

    /// <summary>Claves "Kind:Id" de todos los recursos que ve (para filtrar alertas en SQL).</summary>
    public List<string> VisibleResourceKeys()
    {
        var keys = new List<string>();
        void Add(ResourceKind kind, IReadOnlyDictionary<int, int?> map)
        {
            foreach (var (id, location) in map)
                if (InScope(location)) keys.Add($"{kind}:{id}");
        }
        Add(ResourceKind.Camera, _index.Channels);
        Add(ResourceKind.Door, _index.Doors);
        Add(ResourceKind.Partition, _index.AreaRows);
        Add(ResourceKind.Zone, _index.ZoneRows);
        Add(ResourceKind.Fence, _index.Cercos);
        Add(ResourceKind.Speaker, _index.Speakers);
        Add(ResourceKind.Intercom, _index.Intercoms);
        return keys;
    }

    /// <summary>
    /// Lo que esta sesión puede operar, para que la interfaz no ofrezca órdenes
    /// que el servidor rechazaría. Mismas reglas que las validaciones de cada orden.
    /// </summary>
    public OperableDto Operable()
    {
        if (Unrestricted) return new OperableDto(true, [], [], [], [], [], [], [], [], []);
        static List<int> Ids(IReadOnlyDictionary<int, int?> map, Func<int?, bool> inScope) =>
            map.Where(p => inScope(p.Value)).Select(p => p.Key).Order().ToList();
        static string Key((int PanelId, int Number) k) => $"{k.PanelId}/{k.Number}";
        return new OperableDto(
            false,
            Locations.Order().ToList(),
            Ids(_index.Channels, InScope),
            _index.Areas.Where(a => InScope(a.Value)).Select(a => Key(a.Key)).Order().ToList(),
            _index.Zones.Where(z => InScope(z.Value)).Select(z => Key(z.Key)).Order().ToList(),
            _index.Areas.Keys.Select(k => k.PanelId).Distinct().Where(CanOperateAllAreas).Order().ToList(),
            Ids(_index.Doors, InScope),
            Ids(_index.Cercos, InScope),
            Ids(_index.Speakers, InScope),
            Ids(_index.Intercoms, InScope));
    }

    /// <summary>Recurso por su clave "Kind:Id" (la de Recursos y de las alertas); sin clave no hay a qué restringir.</summary>
    public bool CanViewResourceKey(string? key)
    {
        if (!FiltersView || string.IsNullOrEmpty(key)) return true;
        var parts = key.Split(':');
        return parts.Length == 2 && Enum.TryParse<ResourceKind>(parts[0], out var kind) && int.TryParse(parts[1], out int id)
               && CanViewResource(kind, id);
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
        : new UserScope(userId, isAdmin: false, unrestricted: false, viewOutside: false, [], new HashSet<int>(), Index);
}

/// <summary>
/// Alcances por ubicación de los usuarios, en memoria. Se rehacen al cambiar
/// usuarios, ubicaciones o recursos de ubicación (<see cref="Invalidate"/>) y,
/// por las dudas, cada 30 s: un recurso recién creado no tiene ubicación y
/// queda fuera de todo alcance restringido hasta que se ubique.
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

    private async Task<ScopeSnapshot> BuildAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var index = await ScopeIndex.LoadAsync(db, ct);
        LocationPaths.Update(index);
        var users = await db.Users.AsNoTracking().Where(u => u.Enabled)
            .Select(u => new
            {
                u.Id, u.Role, u.RestrictToLocations, u.ViewOutsideScope,
                Locations = u.Locations.Select(l => l.LocationId).ToList(),
            })
            .ToListAsync(ct);

        var map = new Dictionary<int, UserScope>();
        foreach (var u in users)
        {
            bool admin = u.Role == Roles.Admin;
            bool unrestricted = admin || !u.RestrictToLocations;
            map[u.Id] = new UserScope(u.Id, admin, unrestricted, !unrestricted && u.ViewOutsideScope,
                u.Locations, unrestricted ? new HashSet<int>() : index.Subtree(u.Locations), index);
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
        var allowed = scope.Locations.ToList();
        return query.Where(e =>
            (e.DoorNumber != null && db.AccessDoors.Any(d => d.AccessDeviceId == e.AccessDeviceId && d.Number == e.DoorNumber
                                                              && d.LocationId != null && allowed.Contains(d.LocationId.Value))) ||
            (e.DoorNumber == null && db.AccessDoors.Any(d => d.AccessDeviceId == e.AccessDeviceId
                                                              && d.LocationId != null && allowed.Contains(d.LocationId.Value))));
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
                detail: $"Intentó {attempted} '{targetName ?? targetId}', fuera de su alcance por ubicación.",
                success: false);
        return Results.Json(new { error = "Ese recurso está fuera de su alcance (las ubicaciones que tiene asignadas)." },
            statusCode: StatusCodes.Status403Forbidden);
    }
}
