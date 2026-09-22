using System.Collections.Concurrent;
using System.Threading.Channels;
using Channel = System.Threading.Channels.Channel;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Módulo Citofonía. Por cada frente habilitado el servicio mantiene:
/// <list type="bullet">
/// <item><b>El enlace de llamadas</b> (SDK): por él llega el timbre y salen
/// contestar/rechazar/colgar. El frente lo cierra tras unos minutos sin
/// llamadas: se reabre al instante (si no, el frente llama solo al monitor).</item>
/// <item><b>Un sondeo del estado de línea</b> cada 2 s (ISAPI
/// <c>callStatus</c>): dice si el frente está en línea y es el RESPALDO del
/// enlace —si el enlace no avisó, igual se detecta que suena o que colgaron—.</item>
/// <item><b>La llamada en curso</b> (una por frente) y su máquina de estados:
/// Sonando → En conversación → Terminada, o Sonando → No contestada /
/// Rechazada. Cada cambio se guarda en el historial, se audita y se empuja por
/// el hub para que suene (o deje de sonar) en todos los clientes.</item>
/// <item><b>La voz</b>: un solo canal por frente, tomado por el operador que
/// contestó (o por quien lo llama a mano cuando no hay llamada).</item>
/// </list>
/// Las órdenes de los operadores (API) y las señales del equipo pasan por el
/// mismo candado por frente, así nunca se pisan dos transiciones.
/// </summary>
public sealed class IntercomService(
    IServiceScopeFactory scopeFactory,
    IntercomDriverRegistry drivers,
    CredentialProtector credentials,
    IHubContext<VmsHub> hub,
    AuditService audit,
    IConfiguration config,
    ILogger<IntercomService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReloadInterval = TimeSpan.FromSeconds(30);
    /// <summary>Espera antes de reintentar un enlace que NO se pudo abrir (equipo apagado, credenciales...).</summary>
    private static readonly TimeSpan LinkRetry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LinkRefresh = TimeSpan.FromMinutes(10);
    /// <summary>Fallas seguidas del sondeo antes de declarar el frente sin conexión (~20 s).</summary>
    private const int OfflineAfterFailures = 2;

    /// <summary>Tope de timbre sin respuesta (el frente suele cortar antes, a los ~65 s).</summary>
    private TimeSpan MaxRing => TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Intercom:MaxRingSeconds", 90), 15, 600));
    /// <summary>Tope de una conversación (el frente también corta según su "tiempo de conversación").</summary>
    private TimeSpan MaxTalk => TimeSpan.FromMinutes(Math.Clamp(config.GetValue("Intercom:MaxTalkMinutes", 10), 1, 60));
    /// <summary>Ganancia de la voz del operador antes de codificar (1 = sin cambio).</summary>
    public float TalkGain => Math.Clamp(config.GetValue("Intercom:TalkGain", 2.0f), 0.1f, 8f);

    private readonly ConcurrentDictionary<int, Line> _lines = new();
    private readonly Channel<(int IntercomId, IntercomCallSignal Signal)> _signals =
        Channel.CreateUnbounded<(int, IntercomCallSignal)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _wake = new(0);
    private int _reloadRequested = 1;

    /// <summary>Estado vivo de un frente habilitado.</summary>
    private sealed class Line
    {
        public required Intercom Intercom;
        public required IntercomConnectionInfo Connection;
        public required IIntercomDriver Driver;
        public readonly SemaphoreSlim Gate = new(1, 1);
        public IIntercomCallLink? Link;
        public DateTime NextLinkAttempt = DateTime.MinValue;
        public DateTime LinkOpenedAt;
        public string? LinkError;
        public int PollFailures;
        public DateTime NextPoll = DateTime.MinValue;
        public IntercomCall? Call;
        /// <summary>El sondeo ISAPI llegó a ver la línea ocupada durante esta llamada (solo entonces su "idle" cuenta como fin).</summary>
        public bool LineSeenBusy;
        public VoiceSession? Voice;
    }

    public void RequestReconcile()
    {
        Interlocked.Exchange(ref _reloadRequested, 1);
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    public IntercomConnectionInfo ConnectionOf(Intercom intercom) =>
        new(intercom.Host, intercom.Port, intercom.HttpPort, intercom.Username, credentials.Unprotect(intercom.PasswordCiphertext));

    public IIntercomDriver DriverOf(Intercom intercom) =>
        drivers.Find(intercom.DriverKey)?.Create()
        ?? throw new DriverException($"El frente '{intercom.Name}' usa un driver desconocido ('{intercom.DriverKey}').");

    public IntercomCallDto? ActiveCallOf(int intercomId) =>
        _lines.TryGetValue(intercomId, out var line) && line.Call is { } call ? ToDto(call, line.Intercom) : null;

    public IntercomDto ToDto(Intercom i) => new(i.Id, i.Name, i.DriverKey, i.Host, i.Port, i.HttpPort, i.Username,
        i.Model, i.SerialNumber, i.FirmwareVersion, i.GroupName, i.Enabled, i.Status, i.LastError, i.LastSeenAt,
        i.ChannelId, i.Channel is { } ch ? (ch.Device is { } d ? $"{d.Name} · {ch.Name}" : ch.Name) : null,
        i.DoorCount, i.CallCenterEnabled, ActiveCallOf(i.Id), i.CreatedAt, i.UpdatedAt);

    public static IntercomCallDto ToDto(IntercomCall c, Intercom? intercom) => new(c.Id, c.IntercomId, c.IntercomName, c.State,
        c.StartedAt, c.AnsweredAt, c.EndedAt, c.AnsweredBy, c.AnsweredByUserId, c.Origin, c.EndReason, c.DoorOpened, c.DoorOpenedBy,
        intercom?.ChannelId, intercom?.DoorCount ?? 1);

    /// <summary>Llamadas sonando o en conversación en este momento.</summary>
    public IReadOnlyList<IntercomCallDto> ActiveCalls() =>
        _lines.Values.Where(l => l.Call is not null).Select(l => ToDto(l.Call!, l.Intercom)).OrderBy(c => c.StartedAt).ToList();

    // ------------------------------------------------------------------
    // Ciclo de fondo
    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        await CloseStaleCallsAsync(stoppingToken);
        var consumer = ConsumeSignalsAsync(stoppingToken);
        var lastReload = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Interlocked.Exchange(ref _reloadRequested, 0) == 1 || DateTime.UtcNow - lastReload > ReloadInterval)
                {
                    await ReloadAsync(stoppingToken);
                    lastReload = DateTime.UtcNow;
                }
                await Task.WhenAll(_lines.Values.Select(line => TickAsync(line, stoppingToken)));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "El ciclo de citofonía falló."); }

            try { await _wake.WaitAsync(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        // El supervisor puede volver a arrancar este mismo servicio: dejar todo
        // limpio (el canal de señales se reutiliza; el consumidor termina con el token).
        foreach (var line in _lines.Values) await ShutdownLineAsync(line, "servidor detenido");
        _lines.Clear();
        Interlocked.Exchange(ref _reloadRequested, 1);
        try { await consumer; } catch { /* apagado */ }
    }

    /// <summary>Llamadas que quedaron abiertas si el servidor se cayó a mitad: se cierran al arrancar.</summary>
    private async Task CloseStaleCallsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var stale = await db.IntercomCalls
            .Where(c => c.State == IntercomCallState.Ringing || c.State == IntercomCallState.InCall).ToListAsync(ct);
        foreach (var call in stale)
        {
            call.State = call.State == IntercomCallState.InCall ? IntercomCallState.Completed : IntercomCallState.Missed;
            call.EndedAt ??= DateTime.UtcNow;
            call.EndReason = "el servidor se reinició durante la llamada";
        }
        if (stale.Count > 0) await db.SaveChangesAsync(ct);
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        List<Intercom> enabled;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            enabled = await db.Intercoms.AsNoTracking().Include(i => i.Channel).ThenInclude(c => c!.Device)
                .Where(i => i.Enabled).ToListAsync(ct);
        }

        foreach (var (id, line) in _lines)
        {
            var current = enabled.FirstOrDefault(i => i.Id == id);
            // Cambiaron los datos de conexión (o se deshabilitó/borró): rearmar desde cero.
            if (current is null || current.Host != line.Intercom.Host || current.Port != line.Intercom.Port
                || current.HttpPort != line.Intercom.HttpPort || current.Username != line.Intercom.Username
                || !current.PasswordCiphertext.AsSpan().SequenceEqual(line.Intercom.PasswordCiphertext)
                || current.DriverKey != line.Intercom.DriverKey)
            {
                if (_lines.TryRemove(id, out var removed))
                    await ShutdownLineAsync(removed, current is null ? "el frente se deshabilitó o se eliminó" : "cambió la configuración del frente");
                continue;
            }
            line.Intercom = current; // nombre, canal, puertas...
        }

        foreach (var intercom in enabled.Where(i => !_lines.ContainsKey(i.Id)))
        {
            try
            {
                _lines[intercom.Id] = new Line { Intercom = intercom, Connection = ConnectionOf(intercom), Driver = DriverOf(intercom) };
            }
            catch (Exception ex) { logger.LogWarning(ex, "No se pudo preparar el frente {Name}.", intercom.Name); }
        }
    }

    private async Task ShutdownLineAsync(Line line, string reason)
    {
        await line.Gate.WaitAsync();
        try
        {
            if (line.Call is not null)
                await EndCallAsync(line, line.Call.State == IntercomCallState.InCall ? IntercomCallState.Completed : IntercomCallState.Missed,
                    reason, CancellationToken.None);
            if (line.Voice is { } voice) await voice.CloseAsync(reason);
            if (line.Link is { } link) await link.DisposeAsync();
            line.Link = null;
        }
        catch (Exception ex) { logger.LogDebug(ex, "Error al cerrar el frente {Name}.", line.Intercom.Name); }
        finally { line.Gate.Release(); }
    }

    private async Task TickAsync(Line line, CancellationToken ct)
    {
        // 1) Enlace de llamadas. Se cae solo (el frente lo cierra tras unos
        // minutos sin llamadas) y mientras está caído el frente llama SOLO al
        // monitor interior: se reabre en el siguiente ciclo (≤ 2 s). Por si
        // alguna vez muere sin aviso del SDK, además se renueva cada 10 min
        // cuando no hay llamada en curso.
        if (line.Link is { IsAlive: false } dead)
        {
            await dead.DisposeAsync();
            line.Link = null;
            line.NextLinkAttempt = DateTime.MinValue;
            logger.LogInformation("Se cortó el enlace de llamadas con el frente {Name}; se reabre.", line.Intercom.Name);
        }
        else if (line.Link is { } aged && line.Call is null && line.Voice is null && DateTime.UtcNow - line.LinkOpenedAt > LinkRefresh)
        {
            await aged.DisposeAsync();
            line.Link = null;
            line.NextLinkAttempt = DateTime.MinValue;
        }
        if (line.Link is null && DateTime.UtcNow >= line.NextLinkAttempt)
        {
            line.NextLinkAttempt = DateTime.UtcNow + LinkRetry;
            int id = line.Intercom.Id;
            try
            {
                line.Link = await line.Driver.OpenCallLinkAsync(line.Connection,
                    signal => _signals.Writer.TryWrite((id, signal)), ct);
                line.LinkError = null;
                line.LinkOpenedAt = DateTime.UtcNow;
                logger.LogInformation("Enlace de llamadas abierto con el frente {Name}.", line.Intercom.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                line.LinkError = ex.Message;
                logger.LogWarning("Enlace de llamadas con {Name} no disponible: {Error}", line.Intercom.Name, ex.Message);
            }
        }

        // 2) Estado de línea (y de conexión). Cada 10 s: el frente NO refleja en
        // callStatus las llamadas a la central (verificado: sigue en "idle"
        // mientras suena), así que esto sirve sobre todo para saber si está en línea.
        IntercomLineState? state = DateTime.UtcNow >= line.NextPoll ? await PollStatusAsync(line, ct) : null;

        // 3) Respaldo del enlace y topes de tiempo.
        await line.Gate.WaitAsync(ct);
        try
        {
            var call = line.Call;
            if (state is IntercomLineState.Ringing or IntercomLineState.InCall)
            {
                if (call is null && state == IntercomLineState.Ringing)
                    await StartRingingAsync(line, null, "detectada por el estado del frente", ct);
                else if (call is not null) line.LineSeenBusy = true;
            }
            else if (state == IntercomLineState.Idle && call is not null && line.LineSeenBusy
                     && DateTime.UtcNow - call.StartedAt > TimeSpan.FromSeconds(3))
            {
                await EndCallAsync(line, call.State == IntercomCallState.InCall ? IntercomCallState.Completed : IntercomCallState.Missed,
                    call.State == IntercomCallState.InCall ? "la llamada terminó en el frente" : "el visitante cortó", ct,
                    keepVoice: call.State == IntercomCallState.InCall);
            }

            call = line.Call;
            if (call?.State == IntercomCallState.Ringing && DateTime.UtcNow - call.StartedAt > MaxRing)
                await EndCallAsync(line, IntercomCallState.Missed, "nadie contestó", ct);
            else if (call?.State == IntercomCallState.InCall && DateTime.UtcNow - (call.AnsweredAt ?? call.StartedAt) > MaxTalk)
            {
                await TrySendAsync(line, IntercomCommand.HangUp, ct);
                await EndCallAsync(line, IntercomCallState.Completed, $"tope de {MaxTalk.TotalMinutes:0} minutos de conversación", ct);
            }
        }
        finally { line.Gate.Release(); }
    }

    /// <summary>Consulta el estado de línea y actualiza el estado de conexión; null si el frente no contestó.</summary>
    private async Task<IntercomLineState?> PollStatusAsync(Line line, CancellationToken ct)
    {
        line.NextPoll = DateTime.UtcNow + StatusPollInterval;
        IntercomLineState? state = null;
        string? error = null;
        bool authFailed = false;
        try { state = await line.Driver.GetLineStateAsync(line.Connection, ct); }
        catch (DriverException ex)
        {
            error = ex.Message;
            authFailed = ex.Message.Contains("credenciales", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.Message; }

        if (state is null)
        {
            line.PollFailures++;
            if (line.PollFailures >= OfflineAfterFailures || authFailed)
                await SetStatusAsync(line, authFailed ? IntercomStatus.AuthFailed : IntercomStatus.Offline, error, ct);
        }
        else
        {
            line.PollFailures = 0;
            await SetStatusAsync(line, IntercomStatus.Online, line.Link is null ? line.LinkError : null, ct);
        }
        return state;
    }

    private async Task SetStatusAsync(Line line, IntercomStatus status, string? error, CancellationToken ct)
    {
        var previous = line.Intercom.Status;
        bool errorChanged = line.Intercom.LastError != error;
        bool seenStale = status == IntercomStatus.Online
                         && (line.Intercom.LastSeenAt is null || DateTime.UtcNow - line.Intercom.LastSeenAt > TimeSpan.FromMinutes(1));
        if (previous == status && !errorChanged && !seenStale) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var intercom = await db.Intercoms.Include(i => i.Channel).ThenInclude(c => c!.Device)
            .FirstOrDefaultAsync(i => i.Id == line.Intercom.Id, ct);
        if (intercom is null) return;
        intercom.Status = status;
        intercom.LastError = error is { Length: > 512 } ? error[..512] : error;
        if (status == IntercomStatus.Online) intercom.LastSeenAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        line.Intercom.Status = status;
        line.Intercom.LastError = intercom.LastError;
        line.Intercom.LastSeenAt = intercom.LastSeenAt;
        if (previous == status && !errorChanged) return;

        if (previous != status)
        {
            if (status == IntercomStatus.Online && previous != IntercomStatus.Unknown)
                await audit.LogSystemAsync("intercom", "intercom-online", targetType: "intercom", targetId: intercom.Id.ToString(),
                    targetName: intercom.Name, detail: $"El frente '{intercom.Name}' ({intercom.Host}) recuperó la conexión.");
            else if (status != IntercomStatus.Online)
                await audit.LogSystemAsync("intercom", "intercom-offline", targetType: "intercom", targetId: intercom.Id.ToString(),
                    targetName: intercom.Name, detail: $"El frente '{intercom.Name}' ({intercom.Host}) no responde: {error}", success: false);
        }
        await hub.Clients.All.SendAsync(VmsHubContract.IntercomStatusChanged, ToDto(intercom), ct);
    }

    // ------------------------------------------------------------------
    // Señales del frente
    // ------------------------------------------------------------------

    private async Task ConsumeSignalsAsync(CancellationToken ct)
    {
        await foreach (var (intercomId, signal) in _signals.Reader.ReadAllAsync(ct))
        {
            if (!_lines.TryGetValue(intercomId, out var line)) continue;
            await line.Gate.WaitAsync(ct);
            try { await HandleSignalAsync(line, signal, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "No se pudo procesar la señal {Kind} del frente {Name}.", signal.Kind, line.Intercom.Name);
            }
            finally { line.Gate.Release(); }
        }
    }

    private async Task HandleSignalAsync(Line line, IntercomCallSignal signal, CancellationToken ct)
    {
        logger.LogInformation("Citofonía {Name}: señal {Kind} ({Origin}).", line.Intercom.Name, signal.Kind, signal.Origin ?? "-");
        var call = line.Call;
        switch (signal.Kind)
        {
            case IntercomSignalKind.Ringing:
                if (call is null) await StartRingingAsync(line, signal.Origin, "avisada por el frente", ct);
                break;
            case IntercomSignalKind.Cancelled:
            case IntercomSignalKind.RingTimeout:
                if (call?.State == IntercomCallState.Ringing)
                    await EndCallAsync(line, IntercomCallState.Missed,
                        signal.Kind == IntercomSignalKind.Cancelled ? "el visitante cortó" : "se cumplió el tiempo de timbre", ct);
                break;
            case IntercomSignalKind.HungUp:
                if (call is not null)
                    await EndCallAsync(line, call.State == IntercomCallState.InCall ? IntercomCallState.Completed : IntercomCallState.Missed,
                        call.State == IntercomCallState.InCall ? "el frente cortó la llamada" : "el visitante cortó", ct,
                        keepVoice: call.State == IntercomCallState.InCall);
                break;
            case IntercomSignalKind.Answered:
                // Si contestamos nosotros ya está En conversación; si no, la tomó otro receptor (monitor interior).
                if (call?.State == IntercomCallState.Ringing)
                {
                    call.AnsweredBy = "otro receptor (monitor interior)";
                    call.AnsweredAt = DateTime.UtcNow;
                    await EndCallAsync(line, IntercomCallState.Completed, "contestada en otro equipo", ct);
                }
                break;
            case IntercomSignalKind.Rejected:
            case IntercomSignalKind.Busy:
                if (call?.State == IntercomCallState.Ringing)
                    await EndCallAsync(line, IntercomCallState.Missed,
                        signal.Kind == IntercomSignalKind.Busy ? "el frente está ocupado en otra llamada" : "rechazada en otro equipo", ct);
                break;
        }
    }

    // ------------------------------------------------------------------
    // Transiciones (siempre con line.Gate tomado)
    // ------------------------------------------------------------------

    private async Task StartRingingAsync(Line line, string? origin, string how, CancellationToken ct)
    {
        var call = new IntercomCall
        {
            IntercomId = line.Intercom.Id,
            IntercomName = line.Intercom.Name,
            State = IntercomCallState.Ringing,
            StartedAt = DateTime.UtcNow,
            Origin = origin,
        };
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            db.IntercomCalls.Add(call);
            await db.SaveChangesAsync(ct);
        }
        line.Call = call;
        line.LineSeenBusy = false;
        await PublishAsync(line, ct);
        await audit.LogSystemAsync("intercom", "call-ringing", targetType: "intercom", targetId: line.Intercom.Id.ToString(),
            targetName: line.Intercom.Name,
            detail: $"Tocaron el timbre en '{line.Intercom.Name}'{(origin is null ? "" : $" ({origin})")}; llamada {how}.",
            data: new { callId = call.Id });
    }

    /// <param name="keepVoice">
    /// El FRENTE dio por terminada la conversación (cuelga solo al abrir la
    /// puerta y al cumplir su tope de conversación, 90–120 s, y ninguna de las
    /// dos cosas se puede desactivar en el DS-KB8113). El canal de voz del SDK
    /// no depende de la llamada —verificado: sigue funcionando con el frente en
    /// reposo—, así que la conversación del operador continúa como voz manual
    /// hasta que él cuelgue.
    /// </param>
    private async Task EndCallAsync(Line line, IntercomCallState final, string reason, CancellationToken ct, bool keepVoice = false)
    {
        var call = line.Call;
        if (call is null) return;
        call.State = final;
        call.EndedAt = DateTime.UtcNow;
        call.EndReason = reason;
        await SaveCallAsync(call, ct);
        line.Call = null;
        line.LineSeenBusy = false;
        if (line.Voice is { } voice && voice.ForCall == call.Id)
        {
            if (keepVoice && voice.IsAlive)
            {
                voice.Detach(reason);
                reason += " (la conversación siguió abierta en el VMS)";
                call.EndReason = reason;
                await SaveCallAsync(call, ct);
            }
            else await voice.CloseAsync(reason);
        }
        await hub.Clients.All.SendAsync(VmsHubContract.IntercomCallChanged, ToDto(call, line.Intercom), ct);

        if (final == IntercomCallState.Missed)
            await audit.LogSystemAsync("intercom", "call-missed", targetType: "intercom", targetId: line.Intercom.Id.ToString(),
                targetName: line.Intercom.Name, detail: $"Llamada de '{line.Intercom.Name}' no contestada: {reason}.",
                data: new { callId = call.Id });
        else if (final == IntercomCallState.Completed)
        {
            string duration = call.AnsweredAt is { } at ? $" ({(call.EndedAt.Value - at).TotalSeconds:0} s de conversación)" : "";
            await audit.LogSystemAsync("intercom", "call-ended", targetType: "intercom", targetId: line.Intercom.Id.ToString(),
                targetName: line.Intercom.Name,
                detail: $"Terminó la llamada de '{line.Intercom.Name}' atendida por {call.AnsweredBy ?? "—"}{duration}: {reason}.",
                data: new { callId = call.Id });
        }
    }

    private async Task SaveCallAsync(IntercomCall call, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        db.IntercomCalls.Update(call);
        await db.SaveChangesAsync(ct);
    }

    private async Task PublishAsync(Line line, CancellationToken ct)
    {
        if (line.Call is { } call)
            await hub.Clients.All.SendAsync(VmsHubContract.IntercomCallChanged, ToDto(call, line.Intercom), ct);
    }

    /// <summary>Envía la orden por el enlace; si está cortado o la rechaza, reintenta por ISAPI.</summary>
    private async Task TrySendAsync(Line line, IntercomCommand command, CancellationToken ct)
    {
        if (line.Link is { IsAlive: true } link)
        {
            try
            {
                await link.SendAsync(command, ct);
                return;
            }
            catch (DriverException ex)
            {
                logger.LogInformation("El enlace de {Name} rechazó {Command} ({Error}); se reintenta por ISAPI.",
                    line.Intercom.Name, command, ex.Message);
            }
        }
        await line.Driver.SendCommandAsync(line.Connection, command, ct);
    }

    // ------------------------------------------------------------------
    // Órdenes de los operadores
    // ------------------------------------------------------------------

    /// <summary>Resultado de una orden: la llamada resultante o el motivo del rechazo (y el código HTTP sugerido).</summary>
    public sealed record CommandResult(bool Success, string Message, IntercomCallDto? Call, int StatusCode = 200, string? IntercomName = null);

    private Line? LineOfCall(long callId) => _lines.Values.FirstOrDefault(l => l.Call?.Id == callId);

    public async Task<CommandResult> AnswerAsync(long callId, int userId, string username, CancellationToken ct)
    {
        var line = LineOfCall(callId);
        if (line is null) return new(false, "La llamada ya terminó.", null, 409);
        await line.Gate.WaitAsync(ct);
        try
        {
            var call = line.Call;
            if (call is null || call.Id != callId) return new(false, "La llamada ya terminó.", null, 409, line.Intercom.Name);
            if (call.State == IntercomCallState.InCall)
                return call.AnsweredByUserId == userId
                    ? new(true, "Ya está en conversación.", ToDto(call, line.Intercom), 200, line.Intercom.Name)
                    : new(false, $"La llamada ya la contestó {call.AnsweredBy}.", ToDto(call, line.Intercom), 409, line.Intercom.Name);
            try { await TrySendAsync(line, IntercomCommand.Answer, ct); }
            catch (DriverException ex) { return new(false, $"El frente no aceptó contestar: {ex.Message}", ToDto(call, line.Intercom), 502, line.Intercom.Name); }

            call.State = IntercomCallState.InCall;
            call.AnsweredAt = DateTime.UtcNow;
            call.AnsweredByUserId = userId;
            call.AnsweredBy = username;
            await SaveCallAsync(call, ct);
            await PublishAsync(line, ct);
            return new(true, "Llamada contestada.", ToDto(call, line.Intercom), 200, line.Intercom.Name);
        }
        finally { line.Gate.Release(); }
    }

    public async Task<CommandResult> RejectAsync(long callId, int userId, string username, CancellationToken ct)
    {
        var line = LineOfCall(callId);
        if (line is null) return new(false, "La llamada ya terminó.", null, 409);
        await line.Gate.WaitAsync(ct);
        try
        {
            var call = line.Call;
            if (call is null || call.Id != callId) return new(false, "La llamada ya terminó.", null, 409, line.Intercom.Name);
            if (call.State != IntercomCallState.Ringing)
                return new(false, $"La llamada ya la contestó {call.AnsweredBy}.", ToDto(call, line.Intercom), 409, line.Intercom.Name);
            string? warning = null;
            try { await TrySendAsync(line, IntercomCommand.Reject, ct); }
            catch (DriverException ex) { warning = ex.Message; }
            call.AnsweredByUserId = userId;
            call.AnsweredBy = username;
            await EndCallAsync(line, IntercomCallState.Rejected, $"rechazada por {username}", ct);
            return new(true, warning is null ? "Llamada rechazada." : $"Llamada rechazada en el VMS (el frente respondió: {warning}).",
                ToDto(call, line.Intercom), 200, line.Intercom.Name);
        }
        finally { line.Gate.Release(); }
    }

    public async Task<CommandResult> HangUpAsync(long callId, int userId, string username, bool isAdmin, CancellationToken ct)
    {
        var line = LineOfCall(callId);
        if (line is null) return new(false, "La llamada ya terminó.", null, 409);
        await line.Gate.WaitAsync(ct);
        try
        {
            var call = line.Call;
            if (call is null || call.Id != callId) return new(false, "La llamada ya terminó.", null, 409, line.Intercom.Name);
            if (call.State == IntercomCallState.InCall && call.AnsweredByUserId != userId && !isAdmin)
                return new(false, $"La conversación es de {call.AnsweredBy}; solo esa persona o un administrador puede cortarla.",
                    ToDto(call, line.Intercom), 403, line.Intercom.Name);
            string? warning = null;
            try { await TrySendAsync(line, call.State == IntercomCallState.Ringing ? IntercomCommand.Reject : IntercomCommand.HangUp, ct); }
            catch (DriverException ex) { warning = ex.Message; }
            var final = call.State == IntercomCallState.InCall ? IntercomCallState.Completed : IntercomCallState.Rejected;
            await EndCallAsync(line, final, $"colgó {username}", ct);
            return new(true, warning is null ? "Llamada terminada." : $"Llamada terminada en el VMS (el frente respondió: {warning}).",
                ToDto(call, line.Intercom), 200, line.Intercom.Name);
        }
        finally { line.Gate.Release(); }
    }

    /// <summary>Abre una puerta del frente; si hay una llamada en curso queda anotado en ella.</summary>
    public async Task<CommandResult> OpenDoorAsync(int intercomId, int door, string username, CancellationToken ct)
    {
        Line? line = _lines.TryGetValue(intercomId, out var l) ? l : null;
        Intercom intercom;
        IntercomConnectionInfo connection;
        IIntercomDriver driver;
        if (line is not null)
        {
            (intercom, connection, driver) = (line.Intercom, line.Connection, line.Driver);
        }
        else
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var found = await db.Intercoms.AsNoTracking().FirstOrDefaultAsync(i => i.Id == intercomId, ct);
            if (found is null) return new(false, "El frente no existe.", null, 404);
            (intercom, connection, driver) = (found, ConnectionOf(found), DriverOf(found));
        }
        if (door < 1 || door > Math.Max(1, intercom.DoorCount))
            return new(false, $"El frente '{intercom.Name}' no tiene la puerta {door}.", null, 422, intercom.Name);

        try { await driver.OpenDoorAsync(connection, door, ct); }
        catch (DriverException ex) { return new(false, ex.Message, null, 502, intercom.Name); }

        IntercomCallDto? callDto = null;
        if (line is not null)
        {
            await line.Gate.WaitAsync(ct);
            try
            {
                if (line.Call is { } call)
                {
                    call.DoorOpened = true;
                    call.DoorOpenedBy = username;
                    await SaveCallAsync(call, ct);
                    await PublishAsync(line, ct);
                    callDto = ToDto(call, line.Intercom);
                }
            }
            finally { line.Gate.Release(); }
        }
        return new(true, $"Puerta {door} de '{intercom.Name}' abierta.", callDto, 200, intercom.Name);
    }

    // ------------------------------------------------------------------
    // Voz
    // ------------------------------------------------------------------

    /// <summary>
    /// Conversación de un operador con el frente. El PCM del micrófono del
    /// operador entra por <see cref="WritePcmAsync"/>; lo que capta el frente
    /// sale ya en PCM por <see cref="Downlink"/>. <see cref="Closed"/> se
    /// cancela cuando la llamada termina por otro lado (el visitante cortó,
    /// colgó un administrador...).
    /// </summary>
    public sealed class VoiceSession
    {
        private readonly CancellationTokenSource _closed = new();
        private readonly Action _release;
        private int _closedFlag;

        internal VoiceSession(IIntercomVoiceSession device, int userId, long? forCall, float gain, Action release)
        {
            Device = device;
            UserId = userId;
            ForCall = forCall;
            Gain = gain;
            _release = release;
        }

        internal IIntercomVoiceSession Device { get; }
        public int UserId { get; }
        /// <summary>Llamada a la que pertenece (null = conversación manual sin llamada).</summary>
        public long? ForCall { get; private set; }

        /// <summary>La llamada terminó en el frente pero la voz sigue (payload: motivo). La escucha el WebSocket para avisar al cliente.</summary>
        public event Action<string>? Detached;

        /// <summary>Suelta la conversación de su llamada: desde ahora es voz manual y cerrarla ya no cuelga nada.</summary>
        internal void Detach(string reason)
        {
            ForCall = null;
            Detached?.Invoke(reason);
        }
        private float Gain { get; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public long BytesUp { get; private set; }
        public long BytesDown { get; private set; }
        public string? CloseReason { get; private set; }

        public Channel<byte[]> Downlink { get; } = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        public CancellationToken Closed => _closed.Token;

        internal void OnDeviceAudio(ReadOnlyMemory<byte> g711)
        {
            var pcm = G711.Decode(g711.Span, Device.Codec == "alaw");
            BytesDown += g711.Length;
            Downlink.Writer.TryWrite(pcm);
        }

        public async Task WritePcmAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            if (pcm16.Length < 2 || !Device.IsAlive) return;
            var buffer = pcm16.ToArray();
            G711.ApplyGain(buffer, Gain);
            var encoded = G711.Encode(buffer, Device.Codec == "alaw");
            BytesUp += encoded.Length;
            await Device.WriteAsync(encoded, ct);
        }

        public bool IsAlive => Volatile.Read(ref _closedFlag) == 0 && Device.IsAlive;

        public async Task CloseAsync(string reason)
        {
            if (Interlocked.Exchange(ref _closedFlag, 1) != 0) return;
            CloseReason = reason;
            try { _closed.Cancel(); } catch (ObjectDisposedException) { }
            Downlink.Writer.TryComplete();
            try { await Device.DisposeAsync(); } catch { /* el equipo ya cerró */ }
            _release();
        }
    }

    /// <summary>
    /// Abre la voz con el frente. Con una llamada en conversación solo puede
    /// hablar quien la contestó; sin llamada cualquier operador puede hablar
    /// (para llamar al visitante que está frente a la cámara). Una conversación
    /// por frente.
    /// </summary>
    public async Task<(VoiceSession? Session, string? Error, string IntercomName, IntercomCallDto? Call)> OpenVoiceAsync(
        int intercomId, int userId, CancellationToken ct)
    {
        if (!_lines.TryGetValue(intercomId, out var line))
            return (null, "El frente no está habilitado o no existe.", "", null);
        await line.Gate.WaitAsync(ct);
        try
        {
            var call = line.Call;
            if (call?.State == IntercomCallState.Ringing)
                return (null, "La llamada está sonando: contéstela antes de hablar.", line.Intercom.Name, ToDto(call, line.Intercom));
            if (call?.State == IntercomCallState.InCall && call.AnsweredByUserId != userId)
                return (null, $"La conversación es de {call.AnsweredBy}.", line.Intercom.Name, ToDto(call, line.Intercom));
            if (line.Voice is { IsAlive: true } busy)
                return (null, busy.UserId == userId
                    ? "Ya tiene una conversación abierta con este frente (en otra ventana)."
                    : "Otro operador está hablando con este frente.", line.Intercom.Name, call is null ? null : ToDto(call, line.Intercom));

            VoiceSession? session = null;
            var device = await line.Driver.OpenVoiceAsync(line.Connection, frame => session?.OnDeviceAudio(frame), ct);
            session = new VoiceSession(device, userId, call?.Id, TalkGain, () =>
            {
                if (ReferenceEquals(line.Voice, session)) line.Voice = null;
            });
            line.Voice = session;
            return (session, null, line.Intercom.Name, call is null ? null : ToDto(call, line.Intercom));
        }
        catch (DriverException ex)
        {
            return (null, ex.Message, line.Intercom.Name, null);
        }
        finally { line.Gate.Release(); }
    }

    /// <summary>El WebSocket de voz se cerró: si era la conversación de una llamada, se cuelga.</summary>
    public async Task OnVoiceClosedAsync(int intercomId, VoiceSession session, string username, CancellationToken ct)
    {
        await session.CloseAsync("se cerró la conexión de voz");
        if (session.ForCall is not { } callId || !_lines.TryGetValue(intercomId, out var line)) return;
        await line.Gate.WaitAsync(ct);
        try
        {
            if (line.Call is { State: IntercomCallState.InCall } call && call.Id == callId && call.AnsweredByUserId == session.UserId)
            {
                try { await TrySendAsync(line, IntercomCommand.HangUp, ct); }
                catch (DriverException ex) { logger.LogInformation("No se pudo colgar en el frente {Name}: {Error}", line.Intercom.Name, ex.Message); }
                await EndCallAsync(line, IntercomCallState.Completed, $"{username} cerró la conversación", ct);
            }
        }
        finally { line.Gate.Release(); }
    }
}
