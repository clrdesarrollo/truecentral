using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Services.Workflows;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Escucha los eventos de analítica y alarma que EMPUJAN las cámaras y
/// grabadores (detección de movimiento, cruce de línea, intrusión, pérdida
/// de video, entradas de alarma...) y los publica al motor de
/// automatizaciones. El VMS no analiza video: las reglas se configuran en el
/// propio equipo.
///
/// A qué equipos se suscribe lo decide el motor (<see cref="SetWanted"/>): los
/// que piden las automatizaciones habilitadas con disparador "evento de
/// cámara". Sin automatizaciones que lo pidan no se abre ningún canal, así
/// que el operador no configura nada aparte.
///
/// Los eventos llegan en un hilo del SDK, que no se puede bloquear: el
/// manejador solo encola y un consumidor propio publica. Un mismo hecho
/// (movimiento continuo) llega repetido cada uno o dos segundos: se descarta
/// lo repetido por equipo/canal/tipo dentro de una ventana corta.
/// </summary>
public sealed class VideoEventService(
    IServiceScopeFactory scopeFactory,
    DriverRegistry drivers,
    CredentialProtector credentials,
    IServiceProvider services,
    IConfiguration config,
    ILogger<VideoEventService> logger) : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    private sealed class Source
    {
        public IDeviceEventSubscription? Subscription;
        public string Fingerprint = "";
        public string? LastError;
        public DateTime? LastEventAt;
        public DateTime NextRetryAt = DateTime.MinValue;
    }

    private readonly ConcurrentDictionary<int, Source> _sources = new();

    private readonly Channel<(int DeviceId, DeviceEvent Event)> _incoming =
        System.Threading.Channels.Channel.CreateBounded<(int, DeviceEvent)>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private readonly SemaphoreSlim _wake = new(0);

    // Lo que piden las automatizaciones (lo escribe el motor).
    private volatile bool _all;
    private volatile HashSet<int> _deviceIds = [];
    private volatile HashSet<int> _channelIds = [];

    /// <summary>Último evento aceptado por equipo/canal/tipo/regla (para descartar repeticiones).</summary>
    private readonly ConcurrentDictionary<string, DateTime> _recent = new();

    // ------------------------------------------------------------------
    // Estado consultable
    // ------------------------------------------------------------------

    public bool IsLive(int deviceId) =>
        _sources.TryGetValue(deviceId, out var s) && s.Subscription is { IsAlive: true };

    public string? LastErrorOf(int deviceId) =>
        _sources.TryGetValue(deviceId, out var s) ? s.LastError : null;

    public DateTime? LastEventAtOf(int deviceId) =>
        _sources.TryGetValue(deviceId, out var s) ? s.LastEventAt : null;

    /// <summary>
    /// Qué equipos quieren las automatizaciones: todos (alguna no filtra),
    /// una lista de equipos y/o los equipos dueños de una lista de canales.
    /// </summary>
    public void SetWanted(bool all, IReadOnlySet<int> deviceIds, IReadOnlySet<int> channelIds)
    {
        _all = all;
        _deviceIds = [.. deviceIds];
        _channelIds = [.. channelIds];
        RequestReconcile();
    }

    public void RequestReconcile()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    /// <summary>Suelta el canal de un equipo (al eliminarlo o cambiarle credenciales).</summary>
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
        var consumer = Task.Run(() => ConsumeAsync(ct), ct);

        try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await ReconcileAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Fallo en la reconciliación de la escucha de eventos de cámara."); }

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
        await consumer.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { }, CancellationToken.None);
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();

        var deviceIds = _deviceIds;
        var channelIds = _channelIds;
        List<Device> wanted;
        if (_all)
        {
            wanted = await db.Devices.AsNoTracking().ToListAsync(ct);
        }
        else if (deviceIds.Count == 0 && channelIds.Count == 0)
        {
            wanted = [];
        }
        else
        {
            var fromChannels = channelIds.Count == 0 ? [] : await db.Channels.AsNoTracking()
                .Where(c => channelIds.Contains(c.Id)).Select(c => c.DeviceId).Distinct().ToListAsync(ct);
            var ids = deviceIds.Union(fromChannels).ToHashSet();
            wanted = await db.Devices.AsNoTracking().Where(d => ids.Contains(d.Id)).ToListAsync(ct);
        }
        // Solo los equipos cuyo driver sabe recibir eventos.
        wanted = wanted.Where(d => drivers.Find(d.DriverKey)?.Capabilities.SupportsEvents == true).ToList();
        var wantedIds = wanted.Select(d => d.Id).ToHashSet();

        foreach (var pair in _sources.ToArray())
        {
            if (wantedIds.Contains(pair.Key)) continue;
            _sources.TryRemove(pair.Key, out _);
            await CloseAsync(pair.Key, pair.Value);
            logger.LogInformation("Escucha de eventos cerrada para el equipo {Id} (ninguna automatización la pide).", pair.Key);
        }

        foreach (var device in wanted)
        {
            var source = _sources.GetOrAdd(device.Id, _ => new Source());
            var factory = drivers.Find(device.DriverKey)!;

            if (device.Status == Core.Contracts.DeviceStatus.Offline)
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
            if (DateTime.UtcNow < source.NextRetryAt) continue;

            try
            {
                int deviceId = device.Id;
                source.Subscription = await factory.Create().SubscribeEventsAsync(connection, evt => Enqueue(deviceId, evt), ct);
                source.Fingerprint = fingerprint;
                source.LastError = null;
                source.NextRetryAt = DateTime.MinValue;
                logger.LogInformation("Escucha de eventos de cámara activa en '{Name}' ({Host}).", device.Name, device.Host);
            }
            catch (Exception ex)
            {
                source.LastError = ex is DriverException ? ex.Message : $"No se pudo abrir el canal de eventos: {ex.Message}";
                source.NextRetryAt = DateTime.UtcNow + RetryDelay;
                logger.LogWarning("No se pudo escuchar los eventos de '{Name}' ({Host}): {Error}",
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
        catch (Exception ex) { logger.LogDebug(ex, "Error cerrando el canal de eventos del equipo {Id}.", deviceId); }
    }

    // ------------------------------------------------------------------
    // Recepción y publicación
    // ------------------------------------------------------------------

    /// <summary>Llamado desde el hilo del SDK: solo encolar y volver.</summary>
    private void Enqueue(int deviceId, DeviceEvent evt)
    {
        if (_sources.TryGetValue(deviceId, out var source))
            source.LastEventAt = DateTime.UtcNow;
        _incoming.Writer.TryWrite((deviceId, evt));
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (deviceId, evt) in _incoming.Reader.ReadAllAsync(ct))
            {
                try { await PublishAsync(deviceId, evt, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogWarning(ex, "No se pudo publicar un evento de cámara."); }
            }
        }
        catch (OperationCanceledException) { /* cierre del servidor */ }
    }

    private async Task PublishAsync(int deviceId, DeviceEvent evt, CancellationToken ct)
    {
        // Repeticiones del mismo hecho: una cámara con movimiento continuo
        // informa cada segundo. La automatización tiene además su propio
        // tiempo mínimo entre ejecuciones; esto solo evita inundar la cola.
        int debounce = Math.Clamp(config.GetValue("Workflows:VideoEventDebounceSeconds", 5), 0, 300);
        string key = $"{deviceId}|{evt.ChannelNumber}|{evt.Kind}|{evt.RuleName}|{evt.AlarmInput}";
        var now = DateTime.UtcNow;
        if (debounce > 0 && _recent.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(debounce))
            return;
        _recent[key] = now;
        if (_recent.Count > 2000)
            foreach (var stale in _recent.Where(p => now - p.Value > TimeSpan.FromMinutes(10)).Select(p => p.Key).ToList())
                _recent.TryRemove(stale, out _);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var device = await db.Devices.AsNoTracking().Include(d => d.Channels).FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null) return;
        var channel = evt.ChannelNumber > 0
            ? device.Channels.FirstOrDefault(c => c.ChannelNumber == evt.ChannelNumber)
              ?? (device.Channels.Count == 1 ? device.Channels[0] : null)
            : null;

        logger.LogInformation("Evento de cámara en '{Device}' · {Channel}: {Description}",
            device.Name, channel?.Name ?? $"canal {evt.ChannelNumber}", evt.Description);
        services.GetRequiredService<WorkflowEngine>().Publish(WorkflowTrigger.FromVideoEvent(device, channel, evt));
    }
}
