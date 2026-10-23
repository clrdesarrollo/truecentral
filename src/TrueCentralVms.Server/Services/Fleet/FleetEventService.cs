using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Isup;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services.Isup;
using TrueCentralVms.Server.Services.Workflows;

namespace TrueCentralVms.Server.Services.Fleet;

/// <summary>
/// Eventos de los vehículos de la flota. Tres fuentes:
/// <list type="bullet">
/// <item>las alarmas ISUP del grabador (pánico, velocidad, choque, conducción, ADAS, entradas, video y disco: EN_ALARM_TYPE);</item>
/// <item>las geocercas, evaluadas con cada posición nueva (entrada, salida y exceso de velocidad dentro, con 20 m de histéresis
/// para que el ruido del GPS en el borde no genere entradas y salidas falsas);</item>
/// <item>la conexión del grabador (informativa: un vehículo apagado se desconecta, no es una falla).</item>
/// </list>
/// Cada evento se guarda con la posición conocida, va por el hub a quien ve el vehículo y dispara las automatizaciones de tipo
/// «Evento de vehículo». Un mismo evento del grabador repetido en menos de 60 s (por canal) se toma una sola vez.
/// </summary>
public sealed class FleetEventService(IsupService isup, FleetPositionService positions, IServiceScopeFactory scopes, ScopedHub hub,
    ILogger<FleetEventService> logger) : BackgroundService
{
    public const double ExitMarginM = 20;
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(60);

    private sealed record VehicleInfo(int Id, int DeviceId, string Code, string? Plate, bool Enabled);
    private sealed record Pending(VehicleInfo Vehicle, string Kind, string Description, DateTime AtUtc, int? Channel = null,
        int? GeofenceId = null, double? Latitude = null, double? Longitude = null, double? SpeedKmh = null);
    /// <summary>Estado de un vehículo frente a una geocerca (ver <see cref="Step"/>).</summary>
    public sealed class FenceState { public bool Known; public bool Inside; public bool Speeding; }

    private readonly Channel<Pending> _queue = System.Threading.Channels.Channel.CreateBounded<Pending>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly ConcurrentDictionary<string, DateTime> _recent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(int Vehicle, int Fence), FenceState> _fenceStates = new();
    private volatile Dictionary<string, VehicleInfo> _byIsupId = new(StringComparer.Ordinal);
    private volatile Dictionary<int, VehicleInfo> _byId = new();
    private volatile List<(Geofence Fence, double[][] Points, HashSet<int> Vehicles)> _fences = [];
    private DateTime _loadedUtc = DateTime.MinValue;
    private DateTime _lastRetentionUtc = DateTime.MinValue;

    /// <summary>Cambiaron los vehículos o las geocercas: se releen ahora (en segundo plano).</summary>
    public void Invalidate() => _ = Task.Run(async () =>
    {
        try { await ReloadAsync(CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Flota: no se pudieron releer los vehículos y las geocercas."); }
    });

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        isup.AlarmReceived += OnAlarm;
        isup.DeviceOnlineChanged += OnOnlineChanged;
        positions.PositionsAccepted += OnPositionsAsync;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            await ReloadAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tick.CancelAfter(TimeSpan.FromMinutes(1));
                try
                {
                    while (await _queue.Reader.WaitToReadAsync(tick.Token))
                        while (_queue.Reader.TryRead(out var p))
                            try { await SaveAsync(p, ct); }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                logger.LogWarning(ex, "Flota: no se pudo guardar el evento «{Kind}» de {Vehicle}.", p.Kind, p.Vehicle.Code);
                            }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* tic de mantenimiento */ }
                try
                {
                    if (DateTime.UtcNow - _loadedUtc > TimeSpan.FromMinutes(5)) await ReloadAsync(ct);
                    if (DateTime.UtcNow - _lastRetentionUtc > TimeSpan.FromHours(1)) await ApplyRetentionAsync(ct);
                    foreach (var old in _recent.Where(kv => DateTime.UtcNow - kv.Value > TimeSpan.FromMinutes(10)).Select(kv => kv.Key).ToList())
                        _recent.TryRemove(old, out _);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Flota: error de mantenimiento de eventos."); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            isup.AlarmReceived -= OnAlarm;
            isup.DeviceOnlineChanged -= OnOnlineChanged;
            positions.PositionsAccepted -= OnPositionsAsync;
        }
    }

    // ---------------- alarmas del grabador ----------------

    /// <summary>EN_ALARM_TYPE de HCISUPAlarm.h → tipo de evento de vehículo (los que no aplican a un vehículo, null).</summary>
    public static string? KindOf(int alarmType) => alarmType switch
    {
        13 => FleetEventKinds.Panic,                       // ALARM_TYPE_CALL_HELP
        10 => FleetEventKinds.Overspeed,                   // ALARM_TYPE_SPEEDLIMIT_EXCEED
        115 or 40965 => FleetEventKinds.Collision,
        114 or 40966 => FleetEventKinds.Rollover,
        40963 => FleetEventKinds.HarshAcceleration,
        40964 => FleetEventKinds.HarshBraking,
        40967 or 40968 => FleetEventKinds.HarshTurn,
        40962 or 40969 => FleetEventKinds.AbnormalDriving,
        40971 or 40972 => FleetEventKinds.LaneDeparture,
        40973 => FleetEventKinds.DoorOpenMoving,
        40970 => FleetEventKinds.Overload,
        40974 => FleetEventKinds.Adas,
        41009 => FleetEventKinds.Radar,
        40961 => FleetEventKinds.HighTemperature,
        6 => FleetEventKinds.AlarmInput,
        5 => FleetEventKinds.VideoLoss,
        7 => FleetEventKinds.VideoTamper,
        0 or 1 or 17 or 706 => FleetEventKinds.StorageFault,
        _ => null,   // 9 (formato de video que no coincide: canal sin cámara), movimiento, estados periódicos…
    };

    private void OnAlarm(IsupAlarm alarm)
    {
        if (alarm.Basic is not { Action: 0 } basic || KindOf(basic.AlarmType) is not { } kind) return;
        if (!TryVehicle(alarm.DeviceId, out var v)) return;
        int? channel = basic.Channel > 0 ? basic.Channel : null;
        string detail = kind switch
        {
            FleetEventKinds.AlarmInput when basic.AlarmInput > 0 => $"{FleetEventKinds.LabelOf(kind)} {basic.AlarmInput}",
            FleetEventKinds.VideoLoss or FleetEventKinds.VideoTamper when channel is int c => $"{FleetEventKinds.LabelOf(kind)} · cámara {c}",
            _ => FleetEventKinds.LabelOf(kind),
        };
        if (!string.IsNullOrWhiteSpace(basic.Remark)) detail += $" · {basic.Remark.Trim()}";
        if (Repeated($"{v.Id}|{kind}|{channel}|{basic.AlarmInput}")) return;
        var pos = positions.LatestOf(v.Id);
        Enqueue(new Pending(v, kind, Trim(detail), DateTime.UtcNow, channel, null, pos?.Latitude, pos?.Longitude, pos?.SpeedKmh));
    }

    private void OnOnlineChanged(string isupId, bool online)
    {
        if (!TryVehicle(isupId, out var v)) return;
        string kind = online ? FleetEventKinds.Connected : FleetEventKinds.Disconnected;
        if (Repeated($"{v.Id}|{kind}")) return;
        var pos = positions.LatestOf(v.Id);
        Enqueue(new Pending(v, kind, online ? "El grabador del vehículo se conectó al servidor." : "El grabador del vehículo se desconectó (vehículo apagado o sin cobertura).",
            DateTime.UtcNow, null, null, pos?.Latitude, pos?.Longitude, null));
    }

    private bool Repeated(string key)
    {
        var now = DateTime.UtcNow;
        if (_recent.TryGetValue(key, out var last) && now - last < RepeatWindow) return true;
        _recent[key] = now;
        return false;
    }

    private bool TryVehicle(string isupId, out VehicleInfo vehicle)
    {
        if (_byIsupId.TryGetValue(isupId, out vehicle!)) return vehicle.Enabled;
        // Un equipo que no conocemos: puede ser un vehículo recién creado (se relee, a lo más cada 30 s) o un equipo fijo.
        if (DateTime.UtcNow - _loadedUtc > TimeSpan.FromSeconds(30)) { _loadedUtc = DateTime.UtcNow; Invalidate(); }
        return false;
    }

    private void Enqueue(Pending p) => _queue.Writer.TryWrite(p);

    private static string Trim(string s) => s.Length > 256 ? s[..256] : s;

    // ---------------- geocercas ----------------

    private Task OnPositionsAsync(IReadOnlyList<FleetLivePositionDto> fresh, CancellationToken ct)
    {
        var fences = _fences;
        if (fences.Count == 0) return Task.CompletedTask;
        var byId = _byId;
        foreach (var p in fresh)
        {
            if (!byId.TryGetValue(p.VehicleId, out var v) || !v.Enabled) continue;
            foreach (var (fence, points, vehicles) in fences)
            {
                if (vehicles.Count > 0 && !vehicles.Contains(v.Id)) continue;
                double outside = Geo.DistanceOutside(fence.Kind, points, fence.RadiusM, p.Longitude, p.Latitude);
                var key = (v.Id, fence.Id);
                if (!_fenceStates.TryGetValue(key, out var st)) _fenceStates[key] = st = new FenceState();
                foreach (string kind in Step(st, outside, p.SpeedKmh, fence.AlertOnEnter, fence.AlertOnExit, fence.SpeedLimitKmh))
                    Enqueue(Fence(v, fence, kind, kind switch
                    {
                        FleetEventKinds.GeofenceEnter => $"Entró a la geocerca «{fence.Name}»",
                        FleetEventKinds.GeofenceExit => $"Salió de la geocerca «{fence.Name}»",
                        _ => $"{Math.Round(p.SpeedKmh)} km/h en «{fence.Name}» (máximo {fence.SpeedLimitKmh} km/h)",
                    }, p));
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Avanza el estado de un vehículo contra una geocerca con una posición nueva y devuelve los eventos que corresponden. La
    /// primera posición (arranque, geocerca o vehículo nuevos) solo fija el estado, sin avisar. Para salir hay que alejarse más de
    /// <see cref="ExitMarginM"/> del borde (el ruido del GPS no genera entradas y salidas falsas); el exceso de velocidad avisa
    /// una vez por cada vez que se pasa del límite y se rearma al bajar 5 km/h del límite o al salir.
    /// </summary>
    public static List<string> Step(FenceState st, double outsideM, double speedKmh, bool alertEnter, bool alertExit, int? speedLimitKmh)
    {
        var events = new List<string>();
        if (!st.Known)
        {
            st.Known = true;
            st.Inside = outsideM <= 0;
            return events;
        }
        if (!st.Inside && outsideM <= 0)
        {
            st.Inside = true;
            if (alertEnter) events.Add(FleetEventKinds.GeofenceEnter);
        }
        else if (st.Inside && outsideM > ExitMarginM)
        {
            st.Inside = false;
            st.Speeding = false;
            if (alertExit) events.Add(FleetEventKinds.GeofenceExit);
        }
        if (st.Inside && speedLimitKmh is int limit)
        {
            if (!st.Speeding && speedKmh > limit)
            {
                st.Speeding = true;
                events.Add(FleetEventKinds.GeofenceOverspeed);
            }
            else if (st.Speeding && speedKmh <= limit - 5) st.Speeding = false;
        }
        return events;
    }

    private static Pending Fence(VehicleInfo v, Geofence f, string kind, string text, FleetLivePositionDto p) =>
        new(v, kind, Trim(text), p.At, null, f.Id, p.Latitude, p.Longitude, p.SpeedKmh);

    // ---------------- guardar, avisar y disparar ----------------

    private async Task SaveAsync(Pending p, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var severity = FleetEventKinds.SeverityOf(p.Kind);
        var row = new FleetEvent
        {
            VehicleId = p.Vehicle.Id, At = p.AtUtc, Kind = p.Kind, Severity = severity, Description = p.Description,
            Channel = p.Channel, GeofenceId = p.GeofenceId, Latitude = p.Latitude, Longitude = p.Longitude,
            SpeedKmh = p.SpeedKmh is double s ? (float)s : null,
            // Los informativos (conexión) no esperan a nadie.
            AcknowledgedAt = severity == AlarmSeverity.Info ? DateTime.UtcNow : null,
        };
        db.FleetEvents.Add(row);
        await db.SaveChangesAsync(ct);
        var dto = ToDto(row, p.Vehicle.Code, p.Vehicle.DeviceId);
        await hub.SendAsync(VmsHubContract.FleetEvent, dto, s => s.CanViewDevice(p.Vehicle.DeviceId), ct);
        if (severity != AlarmSeverity.Info)
            logger.LogInformation("Flota: {Label} · {Vehicle} · {Detail}", dto.Label, p.Vehicle.Code, p.Description);
        // Resuelto al vuelo, como en los demás servicios: el motor depende de servicios que a su vez dependen de este.
        scope.ServiceProvider.GetRequiredService<WorkflowEngine>().Publish(
            WorkflowTrigger.FromFleetEvent(dto, p.Vehicle.Plate));
    }

    public static FleetEventDto ToDto(FleetEvent e, string code, int deviceId) => new(e.Id, e.VehicleId, code, deviceId,
        DateTime.SpecifyKind(e.At, DateTimeKind.Utc), e.Kind, FleetEventKinds.LabelOf(e.Kind), e.Severity, e.Description, e.Channel,
        e.GeofenceId, e.Latitude, e.Longitude, e.SpeedKmh,
        e.AcknowledgedAt is { } a ? DateTime.SpecifyKind(a, DateTimeKind.Utc) : null, e.AcknowledgedBy, e.AckNote);

    // ---------------- catálogo y retención ----------------

    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    private async Task ReloadAsync(CancellationToken ct)
    {
        await _reloadGate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var vehicles = await db.Vehicles.AsNoTracking()
                .Select(v => new { v.Id, v.DeviceId, v.Code, v.Plate, v.Enabled, v.DisabledByLicense, v.Device.IsupDeviceId }).ToListAsync(ct);
            var infos = vehicles.Select(v => (v.IsupDeviceId, Info: new VehicleInfo(v.Id, v.DeviceId, v.Code, v.Plate, v.Enabled && !v.DisabledByLicense))).ToList();
            _byId = infos.ToDictionary(x => x.Info.Id, x => x.Info);
            _byIsupId = infos.Where(x => x.IsupDeviceId is not null).ToDictionary(x => x.IsupDeviceId!, x => x.Info, StringComparer.Ordinal);
            var fences = await db.Geofences.AsNoTracking().Where(f => f.Enabled).ToListAsync(ct);
            var parsed = new List<(Geofence, double[][], HashSet<int>)>();
            foreach (var f in fences)
            {
                try
                {
                    var pts = JsonSerializer.Deserialize<double[][]>(f.PointsJson) ?? [];
                    var ids = JsonSerializer.Deserialize<int[]>(f.VehicleIdsJson) ?? [];
                    if (Geo.IsValid(f.Kind, pts, f.RadiusM)) parsed.Add((f, pts, ids.ToHashSet()));
                }
                catch (JsonException) { logger.LogWarning("Flota: la geocerca «{Name}» tiene puntos inválidos; se ignora.", f.Name); }
            }
            // Una geocerca editada se vuelve a tomar sin avisar (su forma cambió: el estado anterior ya no vale).
            var changed = _fences.Where(o => !parsed.Any(n => n.Item1.Id == o.Fence.Id && n.Item1.UpdatedAt == o.Fence.UpdatedAt))
                .Select(o => o.Fence.Id).ToHashSet();
            foreach (var key in _fenceStates.Keys.Where(k => changed.Contains(k.Fence) || !parsed.Any(n => n.Item1.Id == k.Fence)).ToList())
                _fenceStates.TryRemove(key, out _);
            _fences = parsed;
            _loadedUtc = DateTime.UtcNow;
        }
        finally { _reloadGate.Release(); }
    }

    private async Task ApplyRetentionAsync(CancellationToken ct)
    {
        _lastRetentionUtc = DateTime.UtcNow;
        int days = (await positions.LoadSettingsAsync(ct)).PositionRetentionDays;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var limit = DateTime.UtcNow.AddDays(-days);
        int n = await db.FleetEvents.Where(e => e.At < limit).ExecuteDeleteAsync(ct);
        if (n > 0) logger.LogInformation("Flota: retención de {Days} días, se borraron {Count} eventos de vehículos.", days, n);
    }
}

/// <summary>Geometría de las geocercas sobre una proyección local en metros (suficiente a escala de ciudad).</summary>
public static class Geo
{
    private const double EarthR = 6_371_000;

    public static bool IsValid(string kind, double[][] pts, double radiusM) => kind switch
    {
        "polygon" => pts.Length >= 3 && pts.All(Ok),
        "circle" => pts.Length == 1 && Ok(pts[0]) && radiusM is > 0 and <= 500_000,
        "corridor" => pts.Length >= 2 && pts.All(Ok) && radiusM is > 0 and <= 10_000,
        _ => false,
    };

    private static bool Ok(double[] p) => p.Length == 2 && p[0] is >= -180 and <= 180 && p[1] is >= -90 and <= 90;

    public static double Haversine(double lon1, double lat1, double lon2, double lat2)
    {
        double dLat = (lat2 - lat1) * Math.PI / 180, dLon = (lon2 - lon1) * Math.PI / 180;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthR * Math.Asin(Math.Sqrt(h));
    }

    /// <summary>Metros fuera de la geocerca (negativo o cero = dentro).</summary>
    public static double DistanceOutside(string kind, double[][] pts, double radiusM, double lon, double lat)
    {
        if (kind == "circle") return Haversine(pts[0][0], pts[0][1], lon, lat) - radiusM;
        // Proyección equirectangular centrada en el punto: el punto queda en (0, 0).
        double kx = Math.Cos(lat * Math.PI / 180) * Math.PI / 180 * EarthR, ky = Math.PI / 180 * EarthR;
        var xy = pts.Select(p => ((p[0] - lon) * kx, (p[1] - lat) * ky)).ToArray();
        double edge = double.MaxValue;
        int segments = kind == "polygon" ? xy.Length : xy.Length - 1;
        for (int i = 0; i < segments; i++)
            edge = Math.Min(edge, SegmentDistance(xy[i], xy[(i + 1) % xy.Length]));
        if (kind == "corridor") return edge - radiusM / 2;
        return Contains(xy) ? -edge : edge;
    }

    private static double SegmentDistance((double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
        double t = len == 0 ? 0 : Math.Clamp(-(a.X * dx + a.Y * dy) / len, 0, 1);
        double x = a.X + t * dx, y = a.Y + t * dy;
        return Math.Sqrt(x * x + y * y);
    }

    /// <summary>Rayo desde el origen hacia +X (el punto evaluado está en el origen).</summary>
    private static bool Contains((double X, double Y)[] poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > 0) != (poly[j].Y > 0) && 0 < (poly[j].X - poly[i].X) * (0 - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }
}
