using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Módulo Reconocimiento de patentes: mantiene abierto el canal de eventos
/// ANPR de cada equipo marcado como fuente, persiste lo que llega y lo empuja
/// a los clientes por SignalR.
///
/// Las lecturas llegan en un hilo del SDK del fabricante, que no se puede
/// bloquear: el manejador solo encola y un consumidor propio hace el trabajo
/// (guardar las fotos, insertar la fila, avisar). La cola es acotada y descarta
/// lo más viejo — con una ráfaga de tránsito es preferible perder un evento
/// antes que frenar el SDK o quedarse sin memoria.
///
/// Las suscripciones se reconcilian periódicamente contra la base: un equipo
/// que se apaga suelta su canal y lo recupera solo al volver a estar en línea.
/// </summary>
public sealed class AnprService(
    IServiceScopeFactory scopeFactory,
    DriverRegistry drivers,
    CredentialProtector credentials,
    AnprStore store,
    IHubContext<VmsHub> hub,
    IConfiguration config,
    ILogger<AnprService> logger) : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(20);
    /// <summary>Espera antes de reintentar una fuente que falló (no martillar un equipo caído).</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(6);

    private sealed class Source
    {
        public IPlateSubscription? Subscription;
        public string Fingerprint = "";
        public string? LastError;
        public DateTime? LastEventAt;
        public DateTime NextRetryAt = DateTime.MinValue;
    }

    private readonly ConcurrentDictionary<int, Source> _sources = new();

    /// <summary>Cola de lo recibido: la escribe el hilo del SDK, la lee el consumidor.</summary>
    private readonly Channel<(int DeviceId, PlateRecognition Plate)> _incoming =
        // Calificado: 'Channel' también es la entidad de canal de video.
        System.Threading.Channels.Channel.CreateBounded<(int, PlateRecognition)>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    /// <summary>Despierta al reconciliador apenas cambia la configuración de fuentes.</summary>
    private readonly SemaphoreSlim _wake = new(0);

    // ------------------------------------------------------------------
    // Estado consultable por la API
    // ------------------------------------------------------------------

    public bool IsLive(int deviceId) =>
        _sources.TryGetValue(deviceId, out var s) && s.Subscription is { IsAlive: true };

    public string? LastErrorOf(int deviceId) =>
        _sources.TryGetValue(deviceId, out var s) ? s.LastError : null;

    public DateTime? LastEventAtOf(int deviceId) =>
        _sources.TryGetValue(deviceId, out var s) ? s.LastEventAt : null;

    /// <summary>Reconcilia ya (tras encender/apagar una fuente o editar un equipo).</summary>
    public void RequestReconcile()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    /// <summary>Suelta el canal de eventos de un equipo (al eliminarlo o cambiarle credenciales).</summary>
    public async Task DetachAsync(int deviceId)
    {
        if (!_sources.TryRemove(deviceId, out var source)) return;
        await CloseAsync(deviceId, source);
    }

    // ------------------------------------------------------------------
    // Ciclo de vida
    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // El consumidor de la cola corre en paralelo al reconciliador.
        var consumer = Task.Run(() => ConsumeAsync(ct), ct);
        var purger = Task.Run(() => PurgeLoopAsync(ct), ct);

        // Dejar que el servidor termine de arrancar (y que el monitor de
        // dispositivos publique el primer estado en línea).
        try { await Task.Delay(TimeSpan.FromSeconds(8), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Fallo en la reconciliación de fuentes de patentes.");
            }

            // Espera hasta el próximo ciclo, o hasta que alguien pida reconciliar.
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(ReconcileInterval);
            try { await _wake.WaitAsync(wait.Token); }
            catch (OperationCanceledException) { /* venció el intervalo o se cierra el servidor */ }
        }

        foreach (var pair in _sources.ToArray())
        {
            _sources.TryRemove(pair.Key, out _);
            await CloseAsync(pair.Key, pair.Value);
        }
        await Task.WhenAll(consumer, purger).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { }, CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // Reconciliación de suscripciones
    // ------------------------------------------------------------------

    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var wanted = await db.Devices.AsNoTracking().Where(d => d.AnprEnabled).ToListAsync(ct);
        var wantedIds = wanted.Select(d => d.Id).ToHashSet();

        // Equipos que dejaron de ser fuente (o que ya no existen).
        foreach (var pair in _sources.ToArray())
        {
            if (wantedIds.Contains(pair.Key)) continue;
            _sources.TryRemove(pair.Key, out _);
            await CloseAsync(pair.Key, pair.Value);
        }

        foreach (var device in wanted)
        {
            var source = _sources.GetOrAdd(device.Id, _ => new Source());

            if (drivers.Find(device.DriverKey) is not { } factory || !factory.Capabilities.SupportsAnpr)
            {
                await CloseAsync(device.Id, source);
                source.LastError = $"El driver '{device.DriverKey}' no entrega reconocimientos de patentes.";
                continue;
            }

            // Un equipo caído no puede sostener el canal: se suelta y se
            // recupera solo cuando el monitor lo vuelva a ver en línea.
            if (device.Status == DeviceStatus.Offline)
            {
                await CloseAsync(device.Id, source);
                source.LastError = "El equipo está fuera de línea.";
                continue;
            }

            var connection = ConnectionOf(device);
            string fingerprint = $"{device.DriverKey}|{connection.Host}|{connection.Port}|{connection.Username}|{connection.Password}";
            if (source.Subscription is { IsAlive: true } && source.Fingerprint == fingerprint)
                continue;

            await CloseAsync(device.Id, source);
            if (DateTime.UtcNow < source.NextRetryAt)
                continue;

            try
            {
                int deviceId = device.Id;
                var subscription = await factory.Create()
                    .SubscribePlatesAsync(connection, plate => Enqueue(deviceId, plate), ct);
                source.Subscription = subscription;
                source.Fingerprint = fingerprint;
                source.LastError = null;
                source.NextRetryAt = DateTime.MinValue;
                logger.LogInformation("Reconocimiento de patentes activo en '{Name}' ({Host}).", device.Name, device.Host);
            }
            catch (Exception ex)
            {
                source.LastError = ex is DriverException ? ex.Message : $"No se pudo abrir el canal de eventos: {ex.Message}";
                source.NextRetryAt = DateTime.UtcNow + RetryDelay;
                logger.LogWarning("No se pudo activar el reconocimiento de patentes en '{Name}' ({Host}): {Error}",
                    device.Name, device.Host, source.LastError);
            }
        }
    }

    private DeviceConnectionInfo ConnectionOf(Device device) => new(
        device.Host, device.SdkPort, device.Username,
        device.PasswordCiphertext.Length == 0 ? "" : credentials.Unprotect(device.PasswordCiphertext));

    private async Task CloseAsync(int deviceId, Source source)
    {
        var subscription = Interlocked.Exchange(ref source.Subscription, null);
        if (subscription is null) return;
        try { await subscription.DisposeAsync(); }
        catch (Exception ex) { logger.LogDebug(ex, "Error cerrando el canal ANPR del equipo {Id}.", deviceId); }
    }

    // ------------------------------------------------------------------
    // Recepción y persistencia
    // ------------------------------------------------------------------

    /// <summary>Llamado desde el hilo del SDK: solo encolar y volver.</summary>
    private void Enqueue(int deviceId, PlateRecognition plate)
    {
        if (_sources.TryGetValue(deviceId, out var source))
            source.LastEventAt = DateTime.UtcNow;
        _incoming.Writer.TryWrite((deviceId, plate));
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (deviceId, plate) in _incoming.Reader.ReadAllAsync(ct))
            {
                try { await PersistAsync(deviceId, plate, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "No se pudo registrar el reconocimiento de la patente {Plate}.", plate.PlateNumber);
                }
            }
        }
        catch (OperationCanceledException) { /* cierre del servidor */ }
    }

    private async Task PersistAsync(int deviceId, PlateRecognition plate, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();

        var device = await db.Devices.Include(d => d.Channels)
            .FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null) return; // el equipo se eliminó mientras el evento viajaba

        // El canal que informa el SDK es el índice ITS de la cámara; si no calza
        // con el inventario (típico en cámaras de un solo canal) se usa el primero.
        var channel = device.Channels.FirstOrDefault(c => c.ChannelNumber == plate.ChannelNumber)
                      ?? device.Channels.OrderBy(c => c.ChannelNumber).FirstOrDefault();

        var entity = new PlateEvent
        {
            DeviceId = device.Id,
            ChannelNumber = channel?.ChannelNumber ?? plate.ChannelNumber,
            PlateNumber = Truncate(plate.PlateNumber, 24) ?? "",
            CapturedAt = plate.CapturedAt,
            ReceivedAt = DateTime.UtcNow,
            Confidence = plate.Confidence,
            CharConfidences = plate.CharConfidences.Count == 0 ? null : string.Join(',', plate.CharConfidences),
            PlateColor = Truncate(plate.PlateColor, 32),
            PlateType = Truncate(plate.PlateType, 32),
            VehicleType = Truncate(plate.VehicleType, 32),
            VehicleColor = Truncate(plate.VehicleColor, 32),
            VehicleBrand = Truncate(plate.VehicleBrand, 32),
            VehicleAttributes = Truncate(plate.VehicleAttributes, 160),
            SpeedKmh = plate.SpeedKmh,
            VehicleLengthCm = plate.VehicleLengthCm,
            Direction = Truncate(plate.Direction, 32),
            Lane = plate.Lane,
            DetectionMethod = Truncate(plate.DetectionMethod, 32),
            Violation = Truncate(plate.Violation, 64),
            PlateX = plate.PlateX,
            PlateY = plate.PlateY,
            PlateWidth = plate.PlateWidth,
            PlateHeight = plate.PlateHeight,
            SceneImagePath = store.Save(plate.SceneImage, plate.CapturedAt, "escena"),
            PlateImagePath = store.Save(plate.PlateImage, plate.CapturedAt, "placa"),
        };

        db.PlateEvents.Add(entity);
        await db.SaveChangesAsync(ct);

        var dto = AnprMapper.ToDto(entity, device.Name, channel?.Name ?? $"Canal {entity.ChannelNumber}");
        await hub.Clients.All.SendAsync(VmsHubContract.PlateRecognized, dto, ct);
        logger.LogInformation("Patente {Plate} reconocida en '{Device}' ({Confidence}%).",
            entity.PlateNumber, device.Name, entity.Confidence);
    }

    /// <summary>Recorta al largo de la columna: un firmware raro no debe reventar el insert.</summary>
    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    // ------------------------------------------------------------------
    // Purga por antigüedad y por volumen
    // ------------------------------------------------------------------

    private async Task PurgeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PurgeAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Fallo en la purga de reconocimientos de patentes.");
            }
            try { await Task.Delay(PurgeInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PurgeAsync(CancellationToken ct)
    {
        int retentionDays = config.GetValue("Anpr:RetentionDays", 90);
        int maxEvents = config.GetValue("Anpr:MaxEvents", 200_000);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();

        var doomed = new List<PlateEvent>();
        if (retentionDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            doomed.AddRange(await db.PlateEvents.Where(p => p.ReceivedAt < cutoff).ToListAsync(ct));
        }
        if (maxEvents > 0)
        {
            int total = await db.PlateEvents.CountAsync(ct);
            int excess = total - doomed.Count - maxEvents;
            if (excess > 0)
            {
                var alreadyDoomed = doomed.Select(d => d.Id).ToList();
                var extra = await db.PlateEvents
                    .Where(p => !alreadyDoomed.Contains(p.Id))
                    .OrderBy(p => p.ReceivedAt)
                    .Take(excess)
                    .ToListAsync(ct);
                doomed.AddRange(extra);
            }
        }
        if (doomed.Count == 0) return;

        store.Delete(doomed.SelectMany(p => new[] { p.SceneImagePath, p.PlateImagePath }));
        db.PlateEvents.RemoveRange(doomed);
        await db.SaveChangesAsync(ct);
        store.PruneEmptyDirectories();
        logger.LogInformation("Purga de patentes: {Count} reconocimiento(s) eliminado(s).", doomed.Count);
    }
}
