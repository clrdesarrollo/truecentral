using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Foto del momento de cada paso por una puerta, para cruzarla con la foto del
/// padrón (la tarjeta «Últimos accesos» del Centro de mando). Solo de los pasos
/// (no de los eventos del sistema, <see cref="AccessEventRelevance"/>) y de dos
/// fuentes, que se piden a la vez:
///
/// <list type="number">
/// <item><b>La captura del terminal</b>: la que toma el terminal facial al
/// autenticar. Es la del instante exacto, aunque el evento llegue tarde por el
/// sondeo. Se le pide al equipo de a un evento; al que no guarda capturas se
/// le deja de preguntar un rato.</item>
/// <item><b>Una foto de la cámara de la puerta</b> (la del terminal o la
/// primera de la ficha de la puerta), solo si el paso es de hace pocos segundos
/// (<c>Access:EventPhotoCameraSeconds</c>, 10): más tarde la foto ya no
/// muestra a quien pasó. Queda dicho de qué cámara es y cuándo se tomó.</item>
/// </list>
///
/// La del terminal gana. Las fotos son de personas: van cifradas en disco
/// (como los rostros del padrón), en una carpeta junto a los datos, y se purgan
/// a los <c>Access:EventPhotoRetentionDays</c> (30) días. <c>Access:EventPhotos
/// = false</c> lo apaga del todo (no se le pide nada más a los equipos).
/// </summary>
public sealed class AccessEventPhotos
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AccessControlService _access;
    private readonly DriverRegistry _drivers;
    private readonly CredentialProtector _protector;
    private readonly ScopedHub _hub;
    private readonly IConfiguration _config;
    private readonly ILogger<AccessEventPhotos> _logger;

    /// <summary>Hasta cuánto después del paso se le pide la captura al terminal (la guarda en su memoria).</summary>
    private static readonly TimeSpan TerminalWindow = TimeSpan.FromMinutes(10);

    /// <summary>Fotos que se toman a la vez (cada una es una consulta a un equipo).</summary>
    private const int Workers = 3;

    /// <summary>Capturas fallidas seguidas de un terminal antes de dejar de preguntarle, y por cuánto.</summary>
    private const int TerminalMisses = 5;
    private static readonly TimeSpan TerminalPause = TimeSpan.FromHours(1);

    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CameraCache = TimeSpan.FromMinutes(1);

    private readonly System.Threading.Channels.Channel<Job> _queue = System.Threading.Channels.Channel.CreateBounded<Job>(
        new System.Threading.Channels.BoundedChannelOptions(500)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
        });

    /// <summary>Por terminal: capturas fallidas seguidas, cuándo falló la última y si la última salió.</summary>
    private readonly ConcurrentDictionary<int, (int Misses, DateTime LastMiss, bool LastOk)> _terminals = new();

    /// <summary>La cámara de cada puerta (equipo + número), leída hace poco.</summary>
    private readonly ConcurrentDictionary<(int Device, int? Door), (DateTime At, int? ChannelId)> _cameras = new();

    private sealed record Job(long EventId, int DeviceId, int? DoorNumber, long? SerialNo, DateTime Timestamp,
        AccessEventKind Kind, bool TerminalCaptures);

    public AccessEventPhotos(IServiceScopeFactory scopeFactory, AccessControlService access, DriverRegistry drivers,
        CredentialProtector protector, ScopedHub hub, IConfiguration config, EmbeddedPostgres postgres,
        ILogger<AccessEventPhotos> logger)
    {
        _scopeFactory = scopeFactory;
        _access = access;
        _drivers = drivers;
        _protector = protector;
        _hub = hub;
        _config = config;
        _logger = logger;
        string? configured = config["Access:EventPhotoDirectory"];
        RootDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetDirectoryName(postgres.DataDirectory) ?? postgres.DataDirectory, "access-photos")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
    }

    public string RootDirectory { get; }

    private bool Enabled => _config.GetValue("Access:EventPhotos", true);

    private TimeSpan CameraDelay =>
        TimeSpan.FromSeconds(Math.Clamp(_config.GetValue("Access:EventPhotoCameraSeconds", 10), 0, 120));

    /// <summary>Días que se guardan las fotos (nunca más que el historial de accesos).</summary>
    private int RetentionDays => Math.Min(
        Math.Clamp(_config.GetValue("Access:EventPhotoRetentionDays", 30), 1, 3650),
        Math.Clamp(_config.GetValue("Access:EventRetentionDays", 365), 7, 3650));

    /// <summary>Pasos recién guardados: se les busca foto en segundo plano (no demora el evento).</summary>
    public void Offer(AccessDevice device, IEnumerable<AccessEvent> stored)
    {
        if (!Enabled) return;
        var now = DateTime.UtcNow;
        // Los faciales (o con cámara) son los que guardan capturas; a una controladora de tarjetas no se le pregunta.
        bool terminalCaptures = device.SupportsFace || device.CameraRtspPort is not null;
        foreach (var e in stored)
        {
            if (AccessEventRelevance.IsSystem(e.Kind, e.Credential) || now - e.Timestamp > TerminalWindow) continue;
            _queue.Writer.TryWrite(new Job(e.Id, device.Id, e.DoorNumber, e.SerialNo, e.Timestamp, e.Kind, terminalCaptures));
        }
    }

    /// <summary>Atiende la cola hasta que se apague el servidor (lo corre el servicio de eventos de acceso).</summary>
    public Task RunAsync(CancellationToken ct) =>
        Task.WhenAll(Enumerable.Range(0, Workers).Select(_ => WorkAsync(ct)));

    private async Task WorkAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(ct))
            {
                try { await TakeAsync(job, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { _logger.LogDebug(ex, "No se pudo tomar la foto del evento de acceso {Event}.", job.EventId); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task TakeAsync(Job job, CancellationToken ct)
    {
        // A la vez: la cámara no puede esperar a que conteste el terminal (el paso ya pasó).
        var terminal = AskTerminal(job) ? TerminalPictureAsync(job, ct) : Task.FromResult<byte[]?>(null);
        var camera = CameraPhotoAsync(job, ct);
        byte[]? fromTerminal = await terminal;
        var fromCamera = await camera;

        (byte[] Jpeg, string? Camera, DateTime At)? photo =
            fromTerminal is not null ? (fromTerminal, null, job.Timestamp)
            : fromCamera is { } shot ? (shot.Jpeg, shot.Camera, shot.At)
            : null;
        if (photo is not { } chosen) return;

        string? relative = Save(job, chosen.Jpeg);
        if (relative is null) return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        int updated = await db.AccessEvents.Where(e => e.Id == job.EventId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.PhotoPath, relative)
                .SetProperty(e => e.PhotoCamera, chosen.Camera)
                .SetProperty(e => e.PhotoAt, chosen.At), ct);
        if (updated == 0)
        {
            Delete(relative);
            return;
        }
        await _hub.SendAsync(VmsHubContract.AccessEventPhotoReady, new AccessEventPhotoDto(job.EventId, chosen.Camera, chosen.At),
            s => s.CanViewAccessEvent(job.DeviceId, job.DoorNumber), ct);
    }

    // ------------------------------------------------------------------
    // La captura del terminal
    // ------------------------------------------------------------------

    /// <summary>
    /// Solo las autenticaciones (concedidas o rechazadas): el terminal captura al
    /// leer una credencial, no con el botón de salida ni una apertura remota. Que
    /// esas no cuenten como fallas evita pausar a un terminal que sí captura.
    /// </summary>
    private bool AskTerminal(Job job) =>
        job.TerminalCaptures && job.SerialNo is not null && job.Kind is (AccessEventKind.Granted or AccessEventKind.Denied) &&
        !(_terminals.TryGetValue(job.DeviceId, out var state) && state.Misses >= TerminalMisses &&
          DateTime.UtcNow - state.LastMiss < TerminalPause);

    private async Task<byte[]?> TerminalPictureAsync(Job job, CancellationToken ct)
    {
        byte[]? picture = null;
        try
        {
            AccessDevice? device;
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
                device = await db.AccessDevices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == job.DeviceId, ct);
            }
            if (device is not { Enabled: true }) return null;
            picture = await _access.DriverOf(device).FetchEventPictureAsync(_access.ConnectionOf(device), job.SerialNo!.Value, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "El equipo de acceso {Device} no entregó la captura del evento {Serial}.", job.DeviceId, job.SerialNo);
        }

        _terminals.AddOrUpdate(job.DeviceId,
            _ => picture is null ? (1, DateTime.UtcNow, false) : (0, DateTime.MinValue, true),
            (_, old) => picture is null ? (old.Misses + 1, DateTime.UtcNow, false) : (0, DateTime.MinValue, true));
        if (picture is null && _terminals.TryGetValue(job.DeviceId, out var state) && state.Misses == TerminalMisses)
            _logger.LogInformation(
                "El equipo de acceso {Device} no entregó la captura de {Count} pasos seguidos: no se le pide por una hora " +
                "(revise que guarde las fotos de autenticación).", job.DeviceId, TerminalMisses);
        return picture;
    }

    // ------------------------------------------------------------------
    // La foto de la cámara de la puerta
    // ------------------------------------------------------------------

    private async Task<(byte[] Jpeg, string Camera, DateTime At)?> CameraPhotoAsync(Job job, CancellationToken ct)
    {
        // Si el terminal viene entregando sus capturas, la de la cámara sobra.
        if (_terminals.TryGetValue(job.DeviceId, out var state) && state.LastOk && AskTerminal(job)) return null;
        if (DateTime.UtcNow - job.Timestamp > CameraDelay) return null;

        Channel? channel;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            if (await CameraOfAsync(db, job.DeviceId, job.DoorNumber, ct) is not int channelId) return null;
            channel = await db.Channels.AsNoTracking().Include(c => c.Device).FirstOrDefaultAsync(c => c.Id == channelId, ct);
        }
        if (channel is null || !channel.Enabled) return null;
        var factory = _drivers.Find(channel.Device.DriverKey);
        if (factory is null || !factory.Capabilities.SupportsSnapshot) return null;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SnapshotTimeout);
            var jpeg = await factory.Create().CaptureSnapshotAsync(DeviceConnections.Of(channel.Device, _protector),
                channel.ChannelNumber, timeout.Token);
            return jpeg is { Length: > 0 } ? (jpeg, channel.Name, DateTime.UtcNow) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo tomar la foto de la cámara {Channel} para un paso.", channel.Id);
            return null;
        }
    }

    /// <summary>
    /// La cámara de la puerta: la del propio terminal o, si no tiene, la primera
    /// de la ficha de la puerta (el mismo orden que la ficha). null = sin cámara.
    /// </summary>
    private async Task<int?> CameraOfAsync(VmsDbContext db, int deviceId, int? doorNumber, CancellationToken ct)
    {
        var key = (deviceId, doorNumber);
        if (_cameras.TryGetValue(key, out var known) && DateTime.UtcNow - known.At < CameraCache) return known.ChannelId;

        int? channelId = await db.Channels.AsNoTracking()
            .Where(c => c.Device.AccessDeviceId == deviceId && c.Enabled)
            .OrderBy(c => c.ChannelNumber).Select(c => (int?)c.Id).FirstOrDefaultAsync(ct);
        if (channelId is null)
        {
            var doors = await db.AccessDoors.AsNoTracking().Where(d => d.AccessDeviceId == deviceId)
                .Select(d => new { d.Id, d.Number }).ToListAsync(ct);
            var door = doorNumber is int number ? doors.FirstOrDefault(d => d.Number == number)
                : doors.Count == 1 ? doors[0] : null;
            if (door is not null)
                channelId = await ResourceProfile.Of(db.ResourceProfiles.AsNoTracking(), ResourceKind.Door, door.Id)
                    .SelectMany(p => p.Cameras)
                    .Where(c => c.Channel.Enabled)
                    .OrderBy(c => c.Position).Select(c => (int?)c.ChannelId).FirstOrDefaultAsync(ct);
        }
        _cameras[key] = (DateTime.UtcNow, channelId);
        return channelId;
    }

    // ------------------------------------------------------------------
    // Disco (cifrado)
    // ------------------------------------------------------------------

    private string? Save(Job job, byte[] jpeg)
    {
        try
        {
            var local = job.Timestamp.ToLocalTime();
            string folder = $"{local:yyyy}/{local:MM}/{local:dd}";
            Directory.CreateDirectory(Path.Combine(RootDirectory, folder.Replace('/', Path.DirectorySeparatorChar)));
            string relative = $"{folder}/{job.EventId}.jpg.enc";
            File.WriteAllBytes(FullPath(relative)!, _protector.ProtectBytes(jpeg));
            return relative;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo guardar la foto del evento de acceso {Event}.", job.EventId);
            return null;
        }
    }

    /// <summary>La foto de un evento, descifrada; null si ya no está.</summary>
    public byte[]? Read(string? relative)
    {
        if (FullPath(relative) is not { } path || !File.Exists(path)) return null;
        try { return _protector.UnprotectBytes(File.ReadAllBytes(path)); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer la foto de evento {Path}.", relative);
            return null;
        }
    }

    private void Delete(string relative)
    {
        try { if (FullPath(relative) is { } path) File.Delete(path); }
        catch (IOException) { }
    }

    private string? FullPath(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        // Defensa contra rutas trepadoras: el archivo se entrega por HTTP.
        string full = Path.GetFullPath(Path.Combine(RootDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(RootDirectory, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>Borra las fotos más viejas que su retención (los días enteros) y las desata de sus eventos.</summary>
    public async Task PurgeAsync(VmsDbContext db, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        int unlinked = await db.AccessEvents.Where(e => e.PhotoPath != null && e.Timestamp < cutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.PhotoPath, (string?)null), ct);
        int removed = 0;
        if (Directory.Exists(RootDirectory))
        {
            var cutoffDay = cutoff.ToLocalTime().Date;
            foreach (string yearDir in Directory.EnumerateDirectories(RootDirectory))
            foreach (string monthDir in Directory.EnumerateDirectories(yearDir))
            foreach (string dayDir in Directory.EnumerateDirectories(monthDir))
            {
                if (!DateTime.TryParseExact($"{Path.GetFileName(yearDir)}-{Path.GetFileName(monthDir)}-{Path.GetFileName(dayDir)}",
                        "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var day) || day >= cutoffDay)
                    continue;
                try
                {
                    removed += Directory.EnumerateFiles(dayDir).Count();
                    Directory.Delete(dayDir, recursive: true);
                }
                catch (IOException ex) { _logger.LogDebug(ex, "No se pudo borrar {Dir}.", dayDir); }
            }
        }
        if (unlinked > 0 || removed > 0)
            _logger.LogInformation("Fotos de pasos: {Count} archivo(s) más viejos que {Days} días eliminados.", removed, RetentionDays);
    }
}
