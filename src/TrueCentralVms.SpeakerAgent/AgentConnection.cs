using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.SpeakerAgent.Audio;

namespace TrueCentralVms.SpeakerAgent;

/// <summary>
/// El WebSocket hacia el servidor: se conecta con el token guardado, atiende las órdenes y recibe el audio.
/// Reintenta solo (1 s → 30 s); si el servidor rechaza el token espera a que el equipo se vuelva a enrolar.
/// </summary>
public sealed class AgentConnection(AgentStore store, AudioEngine audio, ILogger<AgentConnection> log) : BackgroundService
{
    private static readonly TimeSpan WelcomeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RejectedRetry = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0);
    private ClientWebSocket? _socket;
    private DateTime _stamp;

    public bool Connected => _socket?.State == WebSocketState.Open && ConnectedSince is not null;
    public DateTime? ConnectedSince { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Reconectar ya (se enroló de nuevo, cambió el servidor o se olvidó el enrolamiento).</summary>
    public void Restart()
    {
        try { _socket?.Abort(); } catch (Exception) { }
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        audio.StreamClosed += stream => _ = SendEventAsync(SpeakerAgentProtocol.Events.StreamClosed, new AgentStreamArgs(stream));
        audio.VolumeChangedLocally += volume => _ = SendEventAsync(SpeakerAgentProtocol.Events.VolumeChanged, new AgentVolumeArgs(volume));
        _stamp = store.FileStamp;

        var delay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            // El comando "enroll" pudo escribir el estado con el servicio corriendo.
            if (store.FileStamp != _stamp) { store.Reload(); _stamp = store.FileStamp; }
            var state = store.Snapshot;
            string? token = store.Token;
            if (state.ServerUrl is null || token is null)
            {
                LastError = "Sin enrolar: ingrese el servidor y el código en la página del equipo (puerto 8088) o con el comando enroll.";
                await WaitAsync(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            TimeSpan next;
            try
            {
                await RunSessionAsync(state, token, stoppingToken);
                next = delay = TimeSpan.FromSeconds(1);
            }
            catch (AgentRejectedException ex)
            {
                LastError = $"El servidor rechazó el equipo: {ex.Message}";
                log.LogWarning("{Error}", LastError);
                next = RejectedRetry;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                LastError = $"Sin conexión con {state.ServerUrl}: {ex.Message}";
                log.LogInformation("{Error} (reintento en {Seconds} s)", LastError, delay.TotalSeconds);
                next = delay;
                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
            }
            finally
            {
                ConnectedSince = null;
                audio.StopAll();
            }
            await WaitAsync(next, stoppingToken);
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await _wake.WaitAsync(delay, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    private async Task RunSessionAsync(AgentState state, string token, CancellationToken ct)
    {
        using var socket = CreateSocket(state.PinnedCertificate, captured: null);
        await socket.ConnectAsync(ServerAddress.ToConnectUri(state.ServerUrl!), ct);
        await SendRawAsync(socket, new AgentEnvelope
        {
            Op = SpeakerAgentProtocol.Ops.Hello,
            AgentId = state.AgentId,
            Token = token,
            Info = DeviceIdentity.Info,
        }, ct);
        var welcome = await ReceiveWelcomeAsync(socket, ct);
        if (welcome.SpeakerName != state.SpeakerName || welcome.SpeakerId != state.SpeakerId)
            store.Update(s => { s.SpeakerName = welcome.SpeakerName; s.SpeakerId = welcome.SpeakerId; });

        _socket = socket;
        ConnectedSince = DateTime.UtcNow;
        LastError = null;
        log.LogInformation("Conectado a {Server} como el parlante '{Name}'.", state.ServerUrl, welcome.SpeakerName);
        try { await ReceiveLoopAsync(socket, ct); }
        finally { _socket = null; }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                // El servidor explica el motivo en un "rejected" antes de cerrar (revocado, reemplazado).
                // Reemplazado = otro equipo con la MISMA identidad (tarjeta SD clonada): no se pelean la conexión cada segundo.
                if (socket.CloseStatus is { } status && (int)status is SpeakerAgentProtocol.CloseCodes.Revoked
                        or SpeakerAgentProtocol.CloseCodes.Unauthorized or SpeakerAgentProtocol.CloseCodes.Replaced)
                    throw new AgentRejectedException(LastError ?? "el equipo fue revocado.");
                throw new WebSocketException($"el servidor cerró la conexión ({socket.CloseStatus}).");
            }
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var data = message.GetBuffer().AsSpan(0, (int)message.Length);
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                if (data.Length > SpeakerAgentProtocol.AudioHeaderBytes && data[0] == SpeakerAgentProtocol.AudioFrame)
                    audio.Write(data[1] | data[2] << 8, data[SpeakerAgentProtocol.AudioHeaderBytes..]);
            }
            else
            {
                var envelope = JsonSerializer.Deserialize<AgentEnvelope>(data, SpeakerAgentProtocol.Json);
                if (envelope?.Op == SpeakerAgentProtocol.Ops.Request && envelope.Id is int id)
                    _ = Task.Run(() => AnswerAsync(id, envelope.Method, envelope.Args, ct), ct);
                else if (envelope?.Op == SpeakerAgentProtocol.Ops.Rejected)
                    LastError = envelope.Reason;
            }
            message.SetLength(0);
        }
    }

    private async Task AnswerAsync(int id, string? method, JsonElement? args, CancellationToken ct)
    {
        AgentEnvelope response;
        try
        {
            object? data = Handle(method, args);
            response = new AgentEnvelope
            {
                Op = SpeakerAgentProtocol.Ops.Response,
                Id = id,
                Ok = true,
                Data = data is null ? null : JsonSerializer.SerializeToElement(data, data.GetType(), SpeakerAgentProtocol.Json),
            };
        }
        catch (Exception ex)
        {
            log.LogInformation("Orden '{Method}' rechazada: {Error}", method, ex.Message);
            response = new AgentEnvelope { Op = SpeakerAgentProtocol.Ops.Response, Id = id, Ok = false, Error = ex.Message };
        }
        try
        {
            if (_socket is { } socket) await SendRawAsync(socket, response, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    private T ArgsOf<T>(JsonElement? args) =>
        args is { } element ? element.Deserialize<T>(SpeakerAgentProtocol.Json) ?? throw new ArgumentException("Faltan los datos de la orden.")
            : throw new ArgumentException("Faltan los datos de la orden.");

    /// <summary>Una orden del servidor. Lanza con mensaje en español si no se puede cumplir.</summary>
    private object? Handle(string? method, JsonElement? args)
    {
        switch (method)
        {
            case SpeakerAgentProtocol.Methods.Probe:
                return new AgentProbeResult(
                    Model: $"TrueCentral Speaker ({DeviceIdentity.Hardware})",
                    SerialNumber: DeviceIdentity.SerialNumber ?? store.Snapshot.AgentId[..12].ToUpperInvariant(),
                    FirmwareVersion: DeviceIdentity.Version,
                    DeviceType: "TrueCentralSpeaker",
                    Volume: audio.Volume,
                    LiveCodecs: [SpeakerAgentProtocol.Codecs.ALaw, SpeakerAgentProtocol.Codecs.MuLaw, SpeakerAgentProtocol.Codecs.Pcm16],
                    OutputDevice: audio.OutputDevice);
            case SpeakerAgentProtocol.Methods.VolumeGet:
                return new AgentVolumeArgs(audio.Volume);
            case SpeakerAgentProtocol.Methods.VolumeSet:
                audio.SetVolume(ArgsOf<AgentVolumeArgs>(args).Volume, fromServer: true);
                return new AgentVolumeArgs(audio.Volume);
            case SpeakerAgentProtocol.Methods.LiveOpen:
                if (audio.OutputError is { } outputError) throw new InvalidOperationException($"La salida de audio del equipo falla: {outputError}");
                return new AgentStreamArgs(audio.OpenStream(ArgsOf<AgentLiveOpenArgs>(args).Codec));
            case SpeakerAgentProtocol.Methods.LiveClose:
                audio.CloseStream(ArgsOf<AgentStreamArgs>(args).Stream);
                return null;
            case SpeakerAgentProtocol.Methods.Stop:
                audio.StopAll();
                return null;
            case SpeakerAgentProtocol.Methods.State:
                return new AgentStateResult(audio.IsPlaying, audio.CurrentName);
            case SpeakerAgentProtocol.Methods.TestTone:
                audio.PlayTone(ArgsOf<AgentToneArgs>(args).Seconds);
                return null;
            default:
                throw new NotSupportedException($"Este TrueCentral Speaker (versión {DeviceIdentity.Version}) no conoce la orden '{method}'.");
        }
    }

    private async Task SendEventAsync(string name, object data)
    {
        if (_socket is not { State: WebSocketState.Open } socket) return;
        try
        {
            await SendRawAsync(socket, new AgentEnvelope
            {
                Op = SpeakerAgentProtocol.Ops.Event,
                Name = name,
                Data = JsonSerializer.SerializeToElement(data, data.GetType(), SpeakerAgentProtocol.Json),
            }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { }
    }

    private async Task SendRawAsync(WebSocket socket, AgentEnvelope envelope, CancellationToken ct)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(envelope, SpeakerAgentProtocol.Json);
        await _send.WaitAsync(ct);
        try { await socket.SendAsync(payload, WebSocketMessageType.Text, true, ct); }
        finally { _send.Release(); }
    }

    // ------------------------------------------------------------------
    // Enrolamiento (página local o comando "enroll")
    // ------------------------------------------------------------------

    /// <summary>
    /// Se enrola con el código de un solo uso y guarda el token. Devuelve el nombre del parlante en TrueCentral.
    /// Lanza con un mensaje en español si el servidor no responde o rechaza el código.
    /// </summary>
    public static async Task<string> EnrollAsync(AgentStore store, string serverUrl, string code, CancellationToken ct)
    {
        string server = ServerAddress.Normalize(serverUrl);
        var uri = ServerAddress.ToConnectUri(server);
        var captured = new CertificateCapture();
        using var socket = CreateSocket(pinned: null, captured);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await socket.ConnectAsync(uri, timeout.Token); }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException)
        {
            throw new InvalidOperationException($"No se pudo conectar con {server}: {(ex.InnerException ?? ex).Message}");
        }

        var state = store.Snapshot;
        var envelope = new AgentEnvelope
        {
            Op = SpeakerAgentProtocol.Ops.Enroll,
            AgentId = state.AgentId,
            Code = SpeakerAgentProtocol.NormalizeCode(code),
            Info = DeviceIdentity.Info,
        };
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(envelope, SpeakerAgentProtocol.Json), WebSocketMessageType.Text, true, timeout.Token);
        AgentEnvelope welcome;
        try { welcome = await ReceiveWelcomeAsync(socket, timeout.Token); }
        catch (AgentRejectedException ex) { throw new InvalidOperationException(ex.Message); }
        if (welcome.Token is not { Length: > 0 } token)
            throw new InvalidOperationException("El servidor aceptó el código pero no entregó el token del equipo.");

        store.Update(s =>
        {
            s.ServerUrl = server;
            s.ProtectedToken = AgentStore.Protect(token);
            s.PinnedCertificate = captured.Thumbprint;
            s.SpeakerId = welcome.SpeakerId;
            s.SpeakerName = welcome.SpeakerName;
            s.EnrolledAt = DateTime.UtcNow;
        });
        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
        catch (Exception) { }
        return welcome.SpeakerName ?? "(sin nombre)";
    }

    private static async Task<AgentEnvelope> ReceiveWelcomeAsync(WebSocket socket, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(WelcomeTimeout);
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new AgentRejectedException("el servidor cerró la conexión sin explicar el motivo.");
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var envelope = JsonSerializer.Deserialize<AgentEnvelope>(message.ToArray(), SpeakerAgentProtocol.Json);
            message.SetLength(0);
            if (envelope?.Op == SpeakerAgentProtocol.Ops.Welcome) return envelope;
            if (envelope?.Op == SpeakerAgentProtocol.Ops.Rejected) throw new AgentRejectedException(envelope.Reason ?? "sin motivo.");
        }
    }

    /// <summary>
    /// wss: en el enrolamiento se acepta el certificado que presente el servidor (suele ser propio) y se guarda su huella;
    /// después solo se acepta ese mismo certificado o uno válido para el sistema.
    /// </summary>
    private static ClientWebSocket CreateSocket(string? pinned, CertificateCapture? captured)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null) return false;
            string thumbprint = Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData()));
            if (captured is not null) { captured.Thumbprint = thumbprint; return true; }
            return errors == SslPolicyErrors.None || thumbprint == pinned;
        };
        return socket;
    }

    private sealed class CertificateCapture
    {
        public string? Thumbprint { get; set; }
    }
}

/// <summary>El servidor rechazó el token o el código (no tiene sentido reintentar seguido).</summary>
public sealed class AgentRejectedException(string message) : Exception(message);

/// <summary>Cómo se escribe el servidor: "192.168.1.10", "192.168.1.10:5090", "http://vms.local:5090", "https://...".</summary>
public static class ServerAddress
{
    public const int DefaultPort = 5090;

    public static string Normalize(string input)
    {
        string text = input.Trim().TrimEnd('/');
        if (text.Length == 0) throw new ArgumentException("Indique la dirección del servidor de TrueCentral.");
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"Dirección de servidor inválida: '{input}'.");
        // Sin puerto explícito se asume el de TrueCentral, no el 80.
        string authority = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/')[0];
        bool explicitPort = System.Text.RegularExpressions.Regex.IsMatch(authority, @":\d+$");
        int port = explicitPort ? uri.Port : DefaultPort;
        return $"{uri.Scheme}://{uri.Host}:{port}";
    }

    public static Uri ToConnectUri(string server)
    {
        var uri = new Uri(Normalize(server));
        return new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws", Path = SpeakerAgentProtocol.ConnectPath }.Uri;
    }
}
