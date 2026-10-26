using System.Collections.Concurrent;
using System.Diagnostics;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.PatrolAgent;

/// <summary>Resultado de entregar una descarga: confirmada por el servidor o guardada en la cola local.</summary>
public sealed record SubmitOutcome(PatrolUploadResultDto? Result, bool Queued, string? QueuePath, string? Error);

/// <summary>
/// Servicio de la base: descubre los puertos del bastón, late cada 3 s con el
/// estado de cada uno (y recibe la hora del servidor y las órdenes del panel) y
/// reintenta lo que quedó en la cola local.
/// </summary>
public sealed class PatrolAgentService(AgentStore store, AgentOptions options, ProgressBroadcaster progress,
    ILogger<PatrolAgentService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, DockWorker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, PendingCommand> _commands = new();
    private readonly ConcurrentDictionary<long, string> _errors = new();
    private readonly SemaphoreSlim _kick = new(0);
    private readonly Stopwatch _sinceHeartbeat = new();
    private DateTime? _serverLocal;
    private ServerClient? _client;

    /// <summary>Con el servidor caído, vaciar igual si la memoria supera este uso (la descarga ya está en la cola local).</summary>
    public int SpoolEraseThreshold => options.SpoolEraseThreshold;

    public void Kick() => _kick.Release();

    public void Publish(PatrolDockProgress p) => progress.Publish(p);

    public PendingCommand? TakeCommand(long serial) => _commands.TryRemove(serial, out var c) ? c : null;

    public void ReportError(long? serial, string message)
    {
        if (serial is { } s) _errors[s] = message;
        logger.LogWarning("{Message}", message);
    }

    /// <summary>Hora local del servidor, si hubo un latido hace poco (sin ella no se pone en hora el bastón).</summary>
    public DateTime? ServerNow() =>
        _serverLocal is { } local && _sinceHeartbeat.Elapsed < TimeSpan.FromMinutes(10) ? local + _sinceHeartbeat.Elapsed : null;

    public async Task<SubmitOutcome> SubmitAsync(PatrolAgentUpload upload, CancellationToken ct)
    {
        string path = store.Enqueue(upload);
        if (_client is null) return new(null, true, path, "Sin conexión con el servidor.");
        try
        {
            var result = await _client.UploadAsync(upload, ct);
            AgentStore.Dequeue(path);
            logger.LogInformation("Descarga entregada: {Message}", result.Message);
            return new(result, false, null, null);
        }
        catch (ServerRejectedException ex) when (ex.Status is 400 or 422)
        {
            File.Move(path, path + ".rechazado", overwrite: true);
            logger.LogError("El servidor rechazó la descarga: {Message}", ex.Message);
            return new(null, false, null, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ServerRejectedException)
        {
            logger.LogWarning("Descarga guardada en la cola local (se reintenta): {Message}", ex.Message);
            return new(null, true, path, ex.Message);
        }
    }

    public void AfterDownload(long serial, SubmitOutcome outcome, bool erased, string? eraseError,
        PendingCommand? command, CancellationToken ct)
    {
        _errors.TryRemove(serial, out _);
        if (outcome.Result is { } result && (erased || eraseError is not null))
            Try(() => _client!.EraseReportAsync(new PatrolAgentEraseReport(result.DownloadId, erased, eraseError), ct));
        if (outcome.Result is null && outcome.Queued && erased && outcome.QueuePath is not null)
            File.WriteAllText(outcome.QueuePath + ".vaciado", DateTime.UtcNow.ToString("O"));
        if (command is not null)
            Try(() => _client!.CommandResultAsync(new PatrolAgentCommandResult(command.Id, outcome.Result is not null || outcome.Queued,
                outcome.Result?.Message ?? outcome.Error), ct));
        Kick();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !store.IsEnrolled)
        {
            logger.LogWarning("La base no está enrolada. Ejecute: truecentral-patrol enroll --server https://<servidor> --code <código>");
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
            if (File.Exists(Path.Combine(store.DataDirectory, "state.json"))) store.Update(_ => { });
        }
        var state = store.Snapshot;
        _client = new ServerClient(state.ServerUrl!, state.PinnedSpki, store.Token);
        logger.LogInformation("Base «{Name}» conectando a {Server}.", state.AgentName, state.ServerUrl);

        var lastFlush = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            SyncWorkers();
            await HeartbeatAsync(ct);
            if (lastFlush.Elapsed > TimeSpan.FromSeconds(15)) { await FlushQueueAsync(ct); lastFlush.Restart(); }
            await _kick.WaitAsync(TimeSpan.FromSeconds(PatrolAgentProtocol.HeartbeatSeconds), ct);
        }
        foreach (var w in _workers.Values) w.Stop();
    }

    private void SyncWorkers()
    {
        var ports = Cp210xPorts.Find(options.Ports);
        foreach (var port in ports.Where(p => !_workers.ContainsKey(p)))
        {
            var worker = new DockWorker(port, this, logger);
            _workers[port] = worker;
            worker.Start();
            logger.LogInformation("Atendiendo el puerto {Port}.", port);
        }
        foreach (var gone in _workers.Keys.Where(p => !ports.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList())
            if (_workers.TryRemove(gone, out var w)) { w.Stop(); logger.LogInformation("Puerto {Port} desconectado.", gone); }
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        var readers = _workers.Values.Where(w => w.Serial is not null)
            .Select(w => new PatrolAgentReaderState(w.Serial!.Value, w.Port, w.Docked, w.Error)).ToList();
        string? error = _workers.Values.Select(w => w.Error).FirstOrDefault(e => e is not null);
        try
        {
            var response = await _client!.HeartbeatAsync(new PatrolAgentHeartbeat(Program.Version, readers, error), ct);
            _serverLocal = response.ServerLocal;
            _sinceHeartbeat.Restart();
            foreach (var c in response.Commands) _commands.TryAdd(c.Serial, new PendingCommand(c.Id, c.Kind));
            ForwardReminders(response.Reminders ?? []);
        }
        catch (ServerRejectedException ex) when (ex.Status == 401)
        {
            logger.LogError("El servidor rechazó el token de la base: vuelva a enrolarla con un código nuevo.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ServerRejectedException)
        {
            logger.LogWarning("Sin conexión con el servidor: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Recordatorios de ronda hacia el aviso en pantalla: se muestran al aparecer o cambiar de tipo y se
    /// repiten cada RepeatMinutes; cuando la ronda ya no figura (el bastón salió) se cierra el aviso.
    /// </summary>
    private void ForwardReminders(IReadOnlyList<PatrolAgentReminder> reminders)
    {
        var now = DateTime.UtcNow;
        foreach (var r in reminders)
        {
            bool show = !_reminders.TryGetValue(r.RunId, out var last) || last.Kind != r.Kind
                        || now - last.At >= TimeSpan.FromMinutes(r.RepeatMinutes);
            if (!show) continue;
            _reminders[r.RunId] = (r.Kind, now);
            string text = r.Kind switch
            {
                "upcoming" => $"«{r.RouteName}» a las {r.ExpectedStartLocal:HH:mm}. Prepare el bastón.",
                "due" => $"«{r.RouteName}», programada a las {r.ExpectedStartLocal:HH:mm}. Tiene hasta las {r.DeadlineLocal:HH:mm} para salir.",
                _ => $"«{r.RouteName}», programada a las {r.ExpectedStartLocal:HH:mm}. Se avisó a la central.",
            };
            progress.Publish(new PatrolDockProgress("", null, "reminder-" + r.Kind, 0, 0, text, DateTime.Now) { RunId = r.RunId });
        }
        foreach (var gone in _reminders.Keys.Where(id => reminders.All(r => r.RunId != id)).ToList())
        {
            _reminders.Remove(gone);
            progress.Publish(new PatrolDockProgress("", null, "reminder-clear", 0, 0, null, DateTime.Now) { RunId = gone });
        }
    }

    private readonly Dictionary<long, (string Kind, DateTime At)> _reminders = [];

    private async Task FlushQueueAsync(CancellationToken ct)
    {
        foreach (var (path, upload) in store.Queued())
        {
            try
            {
                var result = await _client!.UploadAsync(upload, ct);
                if (File.Exists(path + ".vaciado"))
                {
                    await _client.EraseReportAsync(new PatrolAgentEraseReport(result.DownloadId, true,
                        null), ct);
                    File.Delete(path + ".vaciado");
                }
                AgentStore.Dequeue(path);
                logger.LogInformation("Descarga pendiente entregada: {Message}", result.Message);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ServerRejectedException)
            {
                logger.LogDebug("La cola sigue pendiente: {Message}", ex.Message);
                return;
            }
        }
    }

    private void Try(Func<Task> action)
    {
        try { action().GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ServerRejectedException)
        {
            logger.LogWarning("No se pudo informar al servidor: {Message}", ex.Message);
        }
    }
}

/// <summary>Opciones de línea de comandos del servicio.</summary>
public sealed record AgentOptions(IReadOnlyList<string>? Ports, int SpoolEraseThreshold);
