using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services.SpeakerAgents;

/// <summary>
/// Conexiones vivas de los agentes «TrueCentral Speaker». Cada agente abre un WebSocket hacia
/// <see cref="SpeakerAgentProtocol.ConnectPath"/>, se identifica (token) o se enrola (código de un solo
/// uso) y queda registrado aquí por su id; el driver <c>truecentral-agent</c> le manda las órdenes por
/// esa conexión. No depende de <see cref="SpeakerService"/> (que sí depende de él, a través del registro
/// de drivers): avisa conexiones y eventos con <see cref="AgentOnlineChanged"/> y <see cref="AgentEvent"/>.
/// </summary>
public sealed class SpeakerAgentHub(IServiceScopeFactory scopeFactory, AuditService audit, ILogger<SpeakerAgentHub> logger)
{
    /// <summary>Vigencia del código de enrolamiento que muestra la web.</summary>
    public static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    /// <summary>Intentos fallidos (código o token malo) tolerados por IP en <see cref="FailureWindow"/>.</summary>
    private const int MaxFailures = 10;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    /// <summary>Alfabeto del código: sin 0/O ni 1/I/L para que se pueda dictar y escribir sin errores.</summary>
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly ConcurrentDictionary<string, AgentLink> _links = new();
    private readonly ConcurrentDictionary<string, (int Count, DateTime Since)> _failures = new();

    /// <summary>(agentId, en línea). Se dispara después de registrar o quitar la conexión.</summary>
    public event Action<string, bool>? AgentOnlineChanged;

    /// <summary>(agentId, nombre del aviso, datos) para los <see cref="SpeakerAgentProtocol.Events"/> del agente.</summary>
    public event Action<string, string, JsonElement?>? AgentEvent;

    public bool IsOnline(string? agentId) => agentId is not null && _links.TryGetValue(agentId, out var link) && link.IsOpen;

    public AgentLink? Find(string? agentId) =>
        agentId is not null && _links.TryGetValue(agentId, out var link) && link.IsOpen ? link : null;

    public static string HashSecret(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>Código de 8 caracteres (≈ 40 bits), que se muestra como ABCD-EFGH.</summary>
    public static string NewEnrollmentCode() =>
        new(Enumerable.Range(0, 8).Select(_ => CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]).ToArray());

    public static string FormatCode(string code) => code.Length == 8 ? $"{code[..4]}-{code[4..]}" : code;

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Corta la conexión del agente (revocado, eliminado o pasado a otro driver).</summary>
    public async Task DisconnectAsync(string? agentId, int closeCode, string reason)
    {
        if (agentId is null || !_links.TryRemove(agentId, out var link)) return;
        await link.CloseAsync(closeCode, reason);
        AgentOnlineChanged?.Invoke(agentId, false);
    }

    // ------------------------------------------------------------------
    // Conexión de un agente
    // ------------------------------------------------------------------

    /// <summary>Atiende el WebSocket de un agente de principio a fin.</summary>
    public async Task HandleAsync(HttpContext ctx)
    {
        string ip = ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "?";
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15),
            KeepAliveTimeout = TimeSpan.FromSeconds(30),
        });
        var stopping = ctx.RequestAborted;

        AgentEnvelope? first;
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            handshake.CancelAfter(HandshakeTimeout);
            first = await AgentLink.ReceiveEnvelopeAsync(socket, handshake.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException)
        {
            logger.LogDebug("Un agente de parlante ({Ip}) no completó el saludo: {Error}", ip, ex.Message);
            return;
        }
        if (first is null) return;

        if (TooManyFailures(ip))
        {
            await RejectAsync(socket, "Demasiados intentos fallidos desde esta dirección; espere unos minutos.", SpeakerAgentProtocol.CloseCodes.Unauthorized);
            return;
        }
        if (first.Info is { } info && info.ProtocolVersion > SpeakerAgentProtocol.Version)
        {
            await RejectAsync(socket, $"El agente usa la versión {info.ProtocolVersion} del protocolo y este servidor entiende hasta la " +
                                      $"{SpeakerAgentProtocol.Version}: actualice TrueCentral.", WebSocketCloseStatus.PolicyViolation);
            return;
        }
        if (!SpeakerAgentProtocol.IsValidAgentId(first.AgentId))
        {
            await RejectAsync(socket, "Identificador de agente inválido.", SpeakerAgentProtocol.CloseCodes.Unauthorized);
            return;
        }
        string agentId = first.AgentId!;

        var (speakerId, speakerName, token, error) = first.Op switch
        {
            SpeakerAgentProtocol.Ops.Enroll => await EnrollAsync(agentId, first.Code, first.Info, ip, stopping),
            SpeakerAgentProtocol.Ops.Hello => await AuthenticateAsync(agentId, first.Token, ip, stopping),
            _ => (0, "", null, "Se esperaba hello o enroll."),
        };
        if (error is not null)
        {
            RecordFailure(ip);
            await RejectAsync(socket, error, SpeakerAgentProtocol.CloseCodes.Unauthorized);
            return;
        }

        var link = new AgentLink(socket, agentId, speakerId, first.Info, ip, logger);
        await link.SendEnvelopeAsync(new AgentEnvelope
        {
            Op = SpeakerAgentProtocol.Ops.Welcome,
            SpeakerId = speakerId,
            SpeakerName = speakerName,
            Token = token,
        }, stopping);

        if (_links.TryGetValue(agentId, out var previous) && _links.TryUpdate(agentId, link, previous))
            await previous.CloseAsync(SpeakerAgentProtocol.CloseCodes.Replaced, "Otra conexión del mismo equipo tomó su lugar.");
        else
            _links[agentId] = link;
        link.EventReceived += (name, data) => AgentEvent?.Invoke(agentId, name, data);
        logger.LogInformation("TrueCentral Speaker '{Name}' conectado desde {Ip} ({Version}, {Platform}).",
            speakerName, ip, first.Info?.Version ?? "?", first.Info?.Platform ?? "?");
        AgentOnlineChanged?.Invoke(agentId, true);

        try { await link.ReceiveLoopAsync(stopping); }
        finally
        {
            bool removed = _links.TryRemove(new KeyValuePair<string, AgentLink>(agentId, link));
            link.MarkClosed();
            if (removed)
            {
                logger.LogInformation("TrueCentral Speaker '{Name}' ({Ip}) se desconectó.", speakerName, ip);
                AgentOnlineChanged?.Invoke(agentId, false);
            }
        }
    }

    private async Task<(int SpeakerId, string Name, string? Token, string? Error)> EnrollAsync(string agentId, string? rawCode,
        AgentInfo? info, string ip, CancellationToken ct)
    {
        string code = SpeakerAgentProtocol.NormalizeCode(rawCode);
        if (code.Length != 8) return (0, "", null, "El código de enrolamiento tiene 8 caracteres (por ejemplo ABCD-EFGH).");
        string hash = HashSecret(code);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var speaker = await db.Speakers.FirstOrDefaultAsync(s => s.EnrollmentCodeHash == hash && s.DriverKey == SpeakerAgentProtocol.DriverKey, ct);
        if (speaker is null || speaker.EnrollmentExpiresAt is not { } expires || expires < DateTime.UtcNow)
        {
            await audit.LogSystemAsync("speakers", "agent-enroll-failed", "speaker", speaker?.Id.ToString(), speaker?.Name,
                $"Intento de enrolar un TrueCentral Speaker desde {ip} con un código {(speaker is null ? "desconocido" : "vencido")}.",
                success: false, clientIp: ip);
            return (0, "", null, speaker is null
                ? "Código de enrolamiento desconocido: revíselo o genere uno nuevo en TrueCentral."
                : "El código de enrolamiento venció: genere uno nuevo en TrueCentral.");
        }
        if (await db.Speakers.AsNoTracking().FirstOrDefaultAsync(s => s.AgentId == agentId && s.Id != speaker.Id, ct) is { } other)
            return (0, "", null, $"Este equipo ya está enrolado como el parlante '{other.Name}': revóquelo o elimínelo antes de enrolarlo otra vez.");

        string? replaced = speaker.AgentId is { } old && old != agentId ? old : null;
        string token = NewToken();
        speaker.AgentId = agentId;
        speaker.AgentTokenHash = HashSecret(token);
        speaker.EnrollmentCodeHash = null;
        speaker.EnrollmentExpiresAt = null;
        speaker.Host = ip;
        speaker.LastError = null;
        speaker.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        if (replaced is not null) await DisconnectAsync(replaced, SpeakerAgentProtocol.CloseCodes.Revoked, "El parlante se enroló en otro equipo.");

        await audit.LogSystemAsync("speakers", "agent-enrolled", "speaker", speaker.Id.ToString(), speaker.Name,
            $"Se enroló el TrueCentral Speaker '{speaker.Name}' desde {ip}" +
            (info is null ? "" : $" ({info.Hardware ?? info.Platform}, {info.Hostname}, versión {info.Version})") +
            (replaced is null ? "." : ", en reemplazo del equipo anterior."),
            data: new { agentId, info?.Hostname, info?.Platform, info?.Hardware, info?.Version }, clientIp: ip);
        return (speaker.Id, speaker.Name, token, null);
    }

    private async Task<(int SpeakerId, string Name, string? Token, string? Error)> AuthenticateAsync(string agentId, string? token,
        string ip, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token)) return (0, "", null, "Falta el token del equipo: vuelva a enrolarlo.");
        string hash = HashSecret(token);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var speaker = await db.Speakers.FirstOrDefaultAsync(s => s.AgentId == agentId && s.AgentTokenHash == hash, ct);
        if (speaker is null)
        {
            // Sin bitácora por cada reintento: un equipo revocado reintenta, y eso no es un incidente nuevo.
            logger.LogInformation("Se rechazó un TrueCentral Speaker desde {Ip}: token desconocido o revocado.", ip);
            return (0, "", null, "Este equipo no está enrolado en este servidor (o fue revocado): enrólelo con un código nuevo.");
        }
        if (speaker.Host != ip)
        {
            speaker.Host = ip;
            await db.SaveChangesAsync(ct);
        }
        return (speaker.Id, speaker.Name, null, null);
    }

    private bool TooManyFailures(string ip) =>
        _failures.TryGetValue(ip, out var entry) && entry.Count >= MaxFailures && DateTime.UtcNow - entry.Since < FailureWindow;

    private void RecordFailure(string ip) =>
        _failures.AddOrUpdate(ip, _ => (1, DateTime.UtcNow),
            (_, entry) => DateTime.UtcNow - entry.Since > FailureWindow ? (1, DateTime.UtcNow) : (entry.Count + 1, entry.Since));

    private static async Task RejectAsync(WebSocket socket, string reason, int closeCode) =>
        await RejectAsync(socket, reason, (WebSocketCloseStatus)closeCode);

    private static async Task RejectAsync(WebSocket socket, string reason, WebSocketCloseStatus status)
    {
        try
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new AgentEnvelope { Op = SpeakerAgentProtocol.Ops.Rejected, Reason = reason },
                SpeakerAgentProtocol.Json);
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);
            // La razón del cierre tiene tope de 123 bytes: el texto completo ya viajó en el mensaje.
            await socket.CloseAsync(status, null, CancellationToken.None);
        }
        catch (Exception) { /* el equipo ya se fue */ }
    }
}

/// <summary>
/// Un agente conectado: envío serializado (un WebSocket admite un solo emisor a la vez), peticiones con
/// correlación por id y lectura continua de respuestas y avisos.
/// </summary>
public sealed class AgentLink
{
    private const int MaxTextMessageBytes = 1024 * 1024;
    private readonly WebSocket _socket;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<AgentEnvelope>> _pending = new();
    private int _nextId;
    private volatile bool _closed;

    internal AgentLink(WebSocket socket, string agentId, int speakerId, AgentInfo? info, string remoteAddress, ILogger log)
    {
        _socket = socket;
        _log = log;
        AgentId = agentId;
        SpeakerId = speakerId;
        Info = info;
        RemoteAddress = remoteAddress;
    }

    public string AgentId { get; }
    public int SpeakerId { get; }
    public AgentInfo? Info { get; }
    public string RemoteAddress { get; }
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;
    public bool IsOpen => !_closed && _socket.State == WebSocketState.Open;

    /// <summary>Aviso del agente (nombre, datos).</summary>
    public event Action<string, JsonElement?>? EventReceived;

    /// <summary>Pide algo al agente y espera su respuesta. Lanza <see cref="Core.Drivers.DriverException"/> si el agente la rechaza o no contesta.</summary>
    public async Task<T?> RequestAsync<T>(string method, object? args, TimeSpan timeout, CancellationToken ct)
    {
        var data = await RequestAsync(method, args, timeout, ct);
        return data is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } element
            ? element.Deserialize<T>(SpeakerAgentProtocol.Json)
            : default;
    }

    public async Task<JsonElement?> RequestAsync(string method, object? args, TimeSpan timeout, CancellationToken ct)
    {
        if (!IsOpen) throw new Core.Drivers.DriverException("El TrueCentral Speaker no está conectado al servidor.");
        int id = Interlocked.Increment(ref _nextId);
        var waiter = new TaskCompletionSource<AgentEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;
        try
        {
            await SendEnvelopeAsync(new AgentEnvelope
            {
                Op = SpeakerAgentProtocol.Ops.Request,
                Id = id,
                Method = method,
                Args = args is null ? null : JsonSerializer.SerializeToElement(args, SpeakerAgentProtocol.Json),
            }, ct);
            AgentEnvelope response;
            try { response = await waiter.Task.WaitAsync(timeout, ct); }
            catch (TimeoutException)
            {
                throw new Core.Drivers.DriverException($"El TrueCentral Speaker no respondió a tiempo ({method}).");
            }
            if (response.Ok != true)
                throw new Core.Drivers.DriverException(response.Error ?? $"El TrueCentral Speaker rechazó la orden ({method}).");
            return response.Data;
        }
        catch (WebSocketException ex)
        {
            throw new Core.Drivers.DriverException($"Se perdió la conexión con el TrueCentral Speaker: {ex.Message}");
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public async Task SendAudioAsync(ushort stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (!IsOpen) throw new Core.Drivers.DriverException("El TrueCentral Speaker no está conectado al servidor.");
        byte[] frame = SpeakerAgentProtocol.AudioFrameOf(stream, payload.Span);
        await _send.WaitAsync(ct);
        try { await _socket.SendAsync(frame, WebSocketMessageType.Binary, true, ct); }
        catch (WebSocketException ex) { throw new Core.Drivers.DriverException($"Se perdió la conexión con el TrueCentral Speaker: {ex.Message}"); }
        finally { _send.Release(); }
    }

    internal async Task SendEnvelopeAsync(AgentEnvelope envelope, CancellationToken ct)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(envelope, SpeakerAgentProtocol.Json);
        await _send.WaitAsync(ct);
        try { await _socket.SendAsync(payload, WebSocketMessageType.Text, true, ct); }
        finally { _send.Release(); }
    }

    internal async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (IsOpen && !ct.IsCancellationRequested)
            {
                var envelope = await ReceiveEnvelopeAsync(_socket, ct);
                if (envelope is null) break;
                switch (envelope.Op)
                {
                    case SpeakerAgentProtocol.Ops.Response when envelope.Id is int id:
                        if (_pending.TryRemove(id, out var waiter)) waiter.TrySetResult(envelope);
                        break;
                    case SpeakerAgentProtocol.Ops.Event when envelope.Name is { } name:
                        try { EventReceived?.Invoke(name, envelope.Data); }
                        catch (Exception ex) { _log.LogWarning(ex, "Falló el manejo del aviso '{Name}' de un TrueCentral Speaker.", name); }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { _log.LogDebug("Conexión de TrueCentral Speaker cortada: {Error}", ex.Message); }
        catch (JsonException ex) { _log.LogWarning("Un TrueCentral Speaker mandó un mensaje ilegible: {Error}", ex.Message); }
        finally { MarkClosed(); }
    }

    internal void MarkClosed()
    {
        _closed = true;
        foreach (var waiter in _pending.Values)
            waiter.TrySetException(new Core.Drivers.DriverException("Se cerró la conexión con el TrueCentral Speaker."));
    }

    internal async Task CloseAsync(int closeCode, string reason)
    {
        MarkClosed();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _send.WaitAsync(timeout.Token);
            try
            {
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new AgentEnvelope { Op = SpeakerAgentProtocol.Ops.Rejected, Reason = reason },
                    SpeakerAgentProtocol.Json);
                await _socket.SendAsync(payload, WebSocketMessageType.Text, true, timeout.Token);
                await _socket.CloseOutputAsync((WebSocketCloseStatus)closeCode, null, timeout.Token);
            }
            finally { _send.Release(); }
        }
        catch (Exception) { _socket.Abort(); }
    }

    /// <summary>Lee el próximo mensaje de texto completo (los binarios del agente se ignoran en esta etapa). Null = cierre.</summary>
    internal static async Task<AgentEnvelope?> ReceiveEnvelopeAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxTextMessageBytes) throw new JsonException("Mensaje demasiado grande.");
                }
                if (!result.EndOfMessage) continue;
                if (result.MessageType != WebSocketMessageType.Text) { message.SetLength(0); continue; }
                return JsonSerializer.Deserialize<AgentEnvelope>(message.ToArray(), SpeakerAgentProtocol.Json);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
