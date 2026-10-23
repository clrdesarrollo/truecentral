using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Isup;
using TrueCentralVms.Server.Data;
using SystemBlob = TrueCentralVms.Server.Data.Entities.SystemBlob;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services.Isup;

namespace TrueCentralVms.Server.Services.Fleet;

/// <summary>
/// Posiciones GPS de la flota: recibe las que entrega el servicio ISUP, las guarda por lotes en la tabla particionada por día
/// <c>VehiclePositions</c> (creando las particiones a medida), mantiene la última de cada vehículo en memoria para el mapa y la
/// empuja por el hub cada pocos segundos (recortada al alcance de cada usuario). La retención borra particiones enteras.
/// </summary>
public sealed class FleetPositionService(IsupService isup, IServiceScopeFactory scopes, ScopedHub hub, EmbeddedPostgres pg,
    ILogger<FleetPositionService> logger) : BackgroundService
{
    private const string MapSettingName = "fleet.map";
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(2);

    private readonly Channel<IsupGpsFix> _incoming = System.Threading.Channels.Channel.CreateBounded<IsupGpsFix>(
        new BoundedChannelOptions(50_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly ConcurrentDictionary<int, FleetLivePositionDto> _latest = new();
    private readonly HashSet<string> _partitions = new(StringComparer.Ordinal);
    private volatile Dictionary<string, (int VehicleId, int DeviceId)> _vehiclesByIsupId = new(StringComparer.Ordinal);
    private DateTime _vehiclesLoadedUtc = DateTime.MinValue;
    private DateTime _lastRetentionUtc = DateTime.MinValue;
    private int _retentionDays = 90;

    /// <summary>Carpeta de mapas del servidor (el mapa base sin internet: <c>basemap.pmtiles</c>).</summary>
    public string MapsDirectory => Path.Combine(pg.DataRoot, "maps");
    public string PmtilesPath => Path.Combine(MapsDirectory, "basemap.pmtiles");

    /// <summary>Última posición de cada vehículo (con su estado de conexión al momento de pedirla).</summary>
    public IReadOnlyList<FleetLivePositionDto> Latest()
    {
        var online = OnlineByVehicle();
        return _latest.Values.Select(p => p with { Online = online.GetValueOrDefault(p.VehicleId) }).ToList();
    }

    /// <summary>El catálogo de vehículos cambió (alta, baja, cambio de ID ISUP): se relee en el próximo lote.</summary>
    public void InvalidateVehicles() => _vehiclesLoadedUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        isup.GpsReceived += OnGps;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            _retentionDays = (await LoadSettingsAsync(ct)).PositionRetentionDays;
            await LoadLatestAsync(ct);
            var batch = new List<IsupGpsFix>();
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(FlushEvery, ct);
                batch.Clear();
                while (batch.Count < 20_000 && _incoming.Reader.TryRead(out var fix)) batch.Add(fix);
                try
                {
                    if (batch.Count > 0) await FlushAsync(batch, ct);
                    if (DateTime.UtcNow - _lastRetentionUtc > TimeSpan.FromHours(1)) await ApplyRetentionAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Flota: no se pudo guardar un lote de {Count} posiciones.", batch.Count);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { isup.GpsReceived -= OnGps; }
    }

    private void OnGps(IsupGpsFix fix)
    {
        if (fix.Valid) _incoming.Writer.TryWrite(fix);
    }

    // ---------------- lote: guardar, actualizar la última y avisar ----------------

    private async Task FlushAsync(List<IsupGpsFix> batch, CancellationToken ct)
    {
        var vehicles = await VehiclesAsync(batch, ct);
        var now = DateTime.UtcNow;
        var rows = new Dictionary<(int, DateTime), (IsupGpsFix Fix, int VehicleId, int DeviceId, DateTime At)>();
        foreach (var fix in batch)
        {
            if (!vehicles.TryGetValue(fix.DeviceId, out var v)) continue;   // equipo ISUP que no es un vehículo
            var at = ToUtc(fix);
            // Un reloj descompuesto (fecha del año anterior, del futuro) no debe crear particiones ni ensuciar el recorrido.
            if (at > now.AddHours(1) || at < now.AddDays(-_retentionDays)) continue;
            at = new DateTime(at.Ticks - at.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            rows[(v.VehicleId, at)] = (fix, v.VehicleId, v.DeviceId, at);
        }
        if (rows.Count == 0) return;

        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync(ct);
        foreach (var day in rows.Values.Select(r => r.At.Date).Distinct()) await EnsurePartitionAsync(conn, day, ct);
        var list = rows.Values.ToList();
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO "VehiclePositions" ("VehicleId", "At", "ReceivedAt", "Latitude", "Longitude", "SpeedKmh", "HeadingDeg",
                "AltitudeM", "Satellites", "Hdop", "Retransmitted", "DailyMileageM")
            SELECT * FROM unnest(@v, @at, @rx, @lat, @lon, @spd, @hdg, @alt, @sat, @hdop, @rt, @mil)
            ON CONFLICT DO NOTHING
            """, conn))
        {
            cmd.Parameters.AddWithValue("v", list.Select(r => r.VehicleId).ToArray());
            cmd.Parameters.AddWithValue("at", list.Select(r => r.At).ToArray());
            cmd.Parameters.AddWithValue("rx", list.Select(_ => now).ToArray());
            cmd.Parameters.AddWithValue("lat", list.Select(r => r.Fix.Latitude).ToArray());
            cmd.Parameters.AddWithValue("lon", list.Select(r => r.Fix.Longitude).ToArray());
            cmd.Parameters.AddWithValue("spd", list.Select(r => (float)r.Fix.SpeedKmh).ToArray());
            cmd.Parameters.AddWithValue("hdg", list.Select(r => (float)r.Fix.HeadingDeg).ToArray());
            cmd.Parameters.AddWithValue("alt", list.Select(r => (float)r.Fix.AltitudeM).ToArray());
            cmd.Parameters.AddWithValue("sat", list.Select(r => (short)r.Fix.Satellites).ToArray());
            cmd.Parameters.AddWithValue("hdop", list.Select(r => (float)r.Fix.Hdop).ToArray());
            cmd.Parameters.AddWithValue("rt", list.Select(r => r.Fix.Retransmitted).ToArray());
            cmd.Parameters.AddWithValue("mil", list.Select(r => (int)Math.Min(r.Fix.DailyMileageM, int.MaxValue)).ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Última posición: la más nueva de cada vehículo (una retransmitida de horas atrás no mueve el marcador).
        var changed = new List<FleetLivePositionDto>();
        var online = OnlineByVehicle();
        foreach (var g in list.GroupBy(r => r.VehicleId))
        {
            var newest = g.MaxBy(r => r.At);
            if (_latest.TryGetValue(g.Key, out var prev) && prev.At >= newest.At) continue;
            var dto = new FleetLivePositionDto(g.Key, newest.DeviceId, newest.Fix.Latitude, newest.Fix.Longitude, newest.Fix.SpeedKmh,
                newest.Fix.HeadingDeg, newest.Fix.Satellites, newest.At, online.GetValueOrDefault(g.Key, true));
            _latest[g.Key] = dto;
            changed.Add(dto);
        }
        if (changed.Count > 0)
            await hub.SendTrimmedAsync(VmsHubContract.FleetPositions, new FleetPositionBatchDto(changed),
                (scope, b) => b.Positions.Where(p => scope.CanViewDevice(p.DeviceId)).ToList() is { Count: > 0 } mine
                    ? new FleetPositionBatchDto(mine) : null, ct);
    }

    /// <summary>
    /// Hora de la posición en UTC. Los grabadores informan su hora local; con el desfase informado se usa ese, si no se asume la
    /// zona horaria del servidor (los equipos de la flota están en la misma).
    /// </summary>
    private static DateTime ToUtc(IsupGpsFix fix)
    {
        var local = DateTime.SpecifyKind(fix.DeviceTime, DateTimeKind.Unspecified);
        if (fix.UtcOffsetMinutes is { } offset) return DateTime.SpecifyKind(local.AddMinutes(-offset), DateTimeKind.Utc);
        try { return TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local); }
        catch (ArgumentException) { return TimeZoneInfo.ConvertTimeToUtc(local.AddHours(1), TimeZoneInfo.Local); } // hora inexistente (cambio de horario)
    }

    private async Task<Dictionary<string, (int VehicleId, int DeviceId)>> VehiclesAsync(List<IsupGpsFix> batch, CancellationToken ct)
    {
        var map = _vehiclesByIsupId;
        bool stale = DateTime.UtcNow - _vehiclesLoadedUtc > TimeSpan.FromMinutes(5);
        bool missing = batch.Any(f => !map.ContainsKey(f.DeviceId)) && DateTime.UtcNow - _vehiclesLoadedUtc > TimeSpan.FromSeconds(30);
        if (!stale && !missing) return map;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        map = (await db.Vehicles.AsNoTracking().Where(v => v.Device.IsupDeviceId != null)
                .Select(v => new { v.Id, v.DeviceId, v.Device.IsupDeviceId }).ToListAsync(ct))
            .ToDictionary(v => v.IsupDeviceId!, v => (v.Id, v.DeviceId), StringComparer.Ordinal);
        _vehiclesByIsupId = map;
        _vehiclesLoadedUtc = DateTime.UtcNow;
        return map;
    }

    private Dictionary<int, bool> OnlineByVehicle() =>
        _vehiclesByIsupId.ToDictionary(kv => kv.Value.VehicleId, kv => isup.IsOnline(kv.Key));

    // ---------------- particiones y retención ----------------

    private static string PartitionName(DateTime day) => $"VehiclePositions_{day:yyyyMMdd}";

    private async Task EnsurePartitionAsync(NpgsqlConnection conn, DateTime day, CancellationToken ct)
    {
        string name = PartitionName(day);
        if (_partitions.Contains(name)) return;
        await using var cmd = new NpgsqlCommand($"""
            CREATE TABLE IF NOT EXISTS "{name}" PARTITION OF "VehiclePositions"
            FOR VALUES FROM ('{day:yyyy-MM-dd} 00:00:00+00') TO ('{day.AddDays(1):yyyy-MM-dd} 00:00:00+00')
            """, conn);
        await cmd.ExecuteNonQueryAsync(ct);
        _partitions.Add(name);
    }

    /// <summary>Borra las particiones de días anteriores a la retención (con su bitácora).</summary>
    private async Task ApplyRetentionAsync(CancellationToken ct)
    {
        _lastRetentionUtc = DateTime.UtcNow;
        _retentionDays = (await LoadSettingsAsync(ct)).PositionRetentionDays;
        var limit = DateTime.UtcNow.Date.AddDays(-_retentionDays);
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync(ct);
        var old = new List<string>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT c.relname FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid JOIN pg_class p ON p.oid = i.inhparent
            WHERE p.relname = 'VehiclePositions'
            """, conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                string name = reader.GetString(0);
                if (DateTime.TryParseExact(name.Replace("VehiclePositions_", ""), "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out var day) && day < limit)
                    old.Add(name);
            }
        foreach (var name in old)
        {
            await using var drop = new NpgsqlCommand($"""DROP TABLE IF EXISTS "{name}" """, conn);
            await drop.ExecuteNonQueryAsync(ct);
            _partitions.Remove(name);
        }
        if (old.Count > 0)
        {
            logger.LogInformation("Flota: retención de {Days} días, se borraron {Count} día(s) de posiciones.", _retentionDays, old.Count);
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AuditService>().LogSystemAsync("fleet", "positions-purged",
                detail: $"Borró las posiciones GPS de {old.Count} día(s) anteriores al {limit:yyyy-MM-dd} (retención de {_retentionDays} días).");
        }
    }

    /// <summary>Al arrancar: la última posición guardada de cada vehículo (el mapa no parte vacío tras un reinicio).</summary>
    private async Task LoadLatestAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var devices = await db.Vehicles.AsNoTracking().ToDictionaryAsync(v => v.Id, v => v.DeviceId, ct);
            await using var conn = new NpgsqlConnection(pg.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                SELECT DISTINCT ON ("VehicleId") "VehicleId", "At", "Latitude", "Longitude", "SpeedKmh", "HeadingDeg", "Satellites"
                FROM "VehiclePositions" WHERE "At" > now() - interval '30 days' ORDER BY "VehicleId", "At" DESC
                """, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                int id = reader.GetInt32(0);
                if (!devices.TryGetValue(id, out int deviceId)) continue;
                _latest[id] = new FleetLivePositionDto(id, deviceId, reader.GetDouble(2), reader.GetDouble(3), reader.GetFloat(4),
                    reader.GetFloat(5), reader.GetInt16(6), DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc), false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Flota: no se pudieron leer las últimas posiciones guardadas.");
        }
    }

    /// <summary>Recorrido de un vehículo entre dos horas UTC (como máximo 50 000 puntos).</summary>
    public async Task<List<FleetTrackPointDto>> TrackAsync(int vehicleId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var points = new List<FleetTrackPointDto>();
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT "At", "Latitude", "Longitude", "SpeedKmh", "HeadingDeg" FROM "VehiclePositions"
            WHERE "VehicleId" = @v AND "At" >= @from AND "At" < @to ORDER BY "At" LIMIT 50000
            """, conn);
        cmd.Parameters.AddWithValue("v", vehicleId);
        cmd.Parameters.AddWithValue("from", DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc));
        cmd.Parameters.AddWithValue("to", DateTime.SpecifyKind(toUtc, DateTimeKind.Utc));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            points.Add(new FleetTrackPointDto(DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc), reader.GetDouble(1),
                reader.GetDouble(2), reader.GetFloat(3), reader.GetFloat(4)));
        return points;
    }

    // ---------------- ajustes del mapa ----------------

    private sealed record StoredMapSettings(string Mode, string? RasterUrl, string? Attribution, double CenterLatitude,
        double CenterLongitude, int Zoom, int PositionRetentionDays);

    private static readonly StoredMapSettings Defaults = new("none", null, null, -33.45, -70.66, 10, 90);

    public async Task<FleetMapSettingsDto> LoadSettingsAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        string? json = await db.SystemBlobs.Where(b => b.Name == MapSettingName).Select(b => b.Data).FirstOrDefaultAsync(ct);
        StoredMapSettings s = Defaults;
        if (json is not null)
            try { s = JsonSerializer.Deserialize<StoredMapSettings>(json) ?? Defaults; } catch (JsonException) { }
        return new FleetMapSettingsDto(s.Mode, s.RasterUrl, s.Attribution, File.Exists(PmtilesPath), s.CenterLatitude,
            s.CenterLongitude, s.Zoom, s.PositionRetentionDays);
    }

    public async Task SaveSettingsAsync(FleetMapSettingsWriteDto w, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var row = await db.SystemBlobs.FirstOrDefaultAsync(b => b.Name == MapSettingName, ct);
        if (row is null) db.SystemBlobs.Add(row = new SystemBlob { Name = MapSettingName });
        row.Data = JsonSerializer.Serialize(new StoredMapSettings(w.Mode, w.RasterUrl, w.Attribution, w.CenterLatitude,
            w.CenterLongitude, w.Zoom, w.PositionRetentionDays));
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        _retentionDays = w.PositionRetentionDays;
        _lastRetentionUtc = DateTime.MinValue;   // la retención nueva se aplica en el próximo lote
    }
}
