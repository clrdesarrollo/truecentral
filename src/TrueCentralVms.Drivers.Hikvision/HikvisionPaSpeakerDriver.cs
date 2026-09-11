using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Hikvision;

public sealed class HikvisionPaSpeakerDriverFactory : ISpeakerDriverFactory
{
    public string DriverKey => "hikvision-pa-rtp";
    public string DisplayName => "Hikvision DS-PA (adaptador de audio, RTP)";
    public int DefaultPort => 80;
    public bool DefaultHttps => false;
    public ISpeakerDriver Create() => new HikvisionPaSpeakerDriver();
}

/// <summary>
/// Adaptadores y amplificadores de audio IP de la familia Hikvision DS-PA
/// (DS-PA0103 y hermanos). Aunque la marca es Hikvision, el equipo NO habla
/// ISAPI: su firmware es de Sinrey (<c>httpd/2.0.3</c>) y expone una API JSON
/// propia bajo <c>/api/*</c> con un token de sesión. Verificado el 2026-09-08
/// contra un DS-PA0103 (firmware V6.1.0b11).
/// <list type="bullet">
/// <item><b>Sesión</b>: <c>POST /api/user/login</c> con usuario y contraseña en
/// Base64 devuelve un token que viaja como <c>?token=</c> en cada llamada; todo
/// lo demás responde 401 sin él.</item>
/// <item><b>Audio</b>: el equipo no recibe audio por HTTP. Escucha un flujo
/// <b>RTP</b> (G.711, 20 ms por paquete) en la dirección configurada en
/// "Multicast Monitor" (<c>/api/sip-monitor</c>). Con <c>0.0.0.0:PUERTO</c>
/// acepta cualquier origen, así que el servidor le envía RTP por UDP directo.
/// Comprobado: con cabecera RTP el equipo sale de <c>idle</c> y reproduce; el
/// audio crudo sin cabecera lo descarta en silencio.</item>
/// <item><b>Volumen</b>: el del monitor, en <c>/api/sip-monitor</c> (1..100).</item>
/// </list>
/// No tiene biblioteca de audios ni texto a voz: los sonidos del sistema le
/// llegan por el mismo canal en vivo que la voz del operador, que es como el
/// <c>SpeakerService</c> reproduce la fuente <c>server</c>.
/// </summary>
public sealed class HikvisionPaSpeakerDriver : ISpeakerDriver
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        // El firmware habla HTTP/1.0 y cierra la conexión al terminar cada
        // respuesta: reutilizarla da "conexión interrumpida por el host remoto".
        // Cada petición pide cierre explícito (ver SendAsync) y el pool no
        // guarda sockets entre llamadas.
        PooledConnectionLifetime = TimeSpan.Zero,
        ConnectTimeout = TimeSpan.FromSeconds(6),
    })
    { Timeout = RequestTimeout };

    /// <summary>Tokens de sesión vivos, por equipo y usuario.</summary>
    private static readonly ConcurrentDictionary<string, string> Tokens = new();

    /// <summary>Puerto UDP del monitor cuando el equipo no tiene ninguno configurado.</summary>
    private const int DefaultMonitorPort = 9999;

    private static string BaseUrl(SpeakerConnectionInfo info) =>
        $"{(info.UseHttps ? "https" : "http")}://{info.Host}:{info.Port}";

    private static string KeyOf(SpeakerConnectionInfo info) =>
        $"{info.Host}:{info.Port}|{info.Username}";

    // ------------------------------------------------------------------
    // Sesión y llamadas
    // ------------------------------------------------------------------

    private static async Task<string> LoginAsync(SpeakerConnectionInfo info, CancellationToken ct)
    {
        string user = Convert.ToBase64String(Encoding.UTF8.GetBytes(info.Username));
        string pass = Convert.ToBase64String(Encoding.UTF8.GetBytes(info.Password));
        string body = JsonSerializer.Serialize(new { username = user, password = pass });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl(info)}/api/user/login?token=")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        using var response = await SendAsync(info, request, ct);
        string text = await HikvisionIsapiClient.ReadTextAsync(response, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new DriverException("El parlante rechazó las credenciales (usuario o contraseña incorrectos).");
        if (!response.IsSuccessStatusCode)
            throw new DriverException($"El parlante rechazó el inicio de sesión: HTTP {(int)response.StatusCode}.");

        string? token = Read(text, root => root.TryGetProperty("token", out var t) ? t.GetString() : null);
        if (string.IsNullOrEmpty(token))
            throw new DriverException("El equipo respondió algo que no es la API de un DS-PA: verifique la dirección y el puerto.");

        Tokens[KeyOf(info)] = token;
        return token;
    }

    private static async Task<HttpResponseMessage> SendAsync(SpeakerConnectionInfo info, HttpRequestMessage request,
        CancellationToken ct)
    {
        // El equipo cierra la conexión tras responder; decirlo aquí evita que el
        // pool intente reutilizar un socket que ya no existe.
        request.Headers.ConnectionClose = true;
        try { return await Http.SendAsync(request, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DriverException($"El parlante en {info.Host}:{info.Port} no respondió a tiempo.");
        }
        catch (HttpRequestException ex)
        {
            throw new DriverException($"No se pudo conectar con el parlante en {info.Host}:{info.Port}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Llama a la API con el token cacheado y repite el intento una vez tras
    /// renovar la sesión si el equipo responde 401 (token vencido o reiniciado).
    /// </summary>
    private static async Task<string> CallAsync(SpeakerConnectionInfo info, HttpMethod method, string path,
        string? json, CancellationToken ct)
    {
        if (!Tokens.TryGetValue(KeyOf(info), out var token)) token = await LoginAsync(info, ct);

        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, $"{BaseUrl(info)}{path}?token={token}");
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await SendAsync(info, request, ct);
            string text = await HikvisionIsapiClient.ReadTextAsync(response, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                token = await LoginAsync(info, ct);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new DriverException("El parlante rechazó las credenciales (usuario o contraseña incorrectos).");
            if (!response.IsSuccessStatusCode)
                throw new DriverException($"El parlante rechazó la orden ({path}): HTTP {(int)response.StatusCode}.");
            return text;
        }
    }

    private static T? Read<T>(string json, Func<JsonElement, T?> select)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return select(document.RootElement);
        }
        catch (JsonException) { return default; }
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // ------------------------------------------------------------------
    // Monitor: dónde escucha el equipo el audio
    // ------------------------------------------------------------------

    /// <summary>Configuración de "Multicast Monitor": destino del RTP y volumen.</summary>
    private sealed record MonitorConfig(IReadOnlyList<string> Addresses, int? Volume)
    {
        /// <summary>Primera dirección configurada, o null si están todas vacías.</summary>
        public string? Primary => Addresses.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
    }

    private static async Task<MonitorConfig> GetMonitorAsync(SpeakerConnectionInfo info, CancellationToken ct)
    {
        string body = await CallAsync(info, HttpMethod.Get, "/api/sip-monitor", null, ct);
        var config = Read(body, root =>
        {
            var addresses = new List<string>();
            if (root.TryGetProperty("monitor", out var monitor) && monitor.ValueKind == JsonValueKind.Array)
                addresses.AddRange(monitor.EnumerateArray().Select(e => Str(e, "address") ?? ""));
            int? volume = root.TryGetProperty("volume", out var v) && v.TryGetInt32(out int parsed) ? parsed : null;
            return new MonitorConfig(addresses, volume);
        });
        return config ?? throw new DriverException("El parlante entregó una configuración de monitor ilegible.");
    }

    /// <summary>
    /// Guarda el monitor completo: el equipo espera siempre las diez ranuras y
    /// el volumen juntos, así que hay que reenviar lo que ya tenía.
    /// </summary>
    private static async Task SetMonitorAsync(SpeakerConnectionInfo info, IReadOnlyList<string> addresses, int volume,
        CancellationToken ct)
    {
        var slots = new List<object>();
        for (int i = 0; i < Math.Max(10, addresses.Count); i++)
            slots.Add(new { address = i < addresses.Count ? addresses[i] : "" });

        string json = JsonSerializer.Serialize(new { volume, monitor = slots });
        await CallAsync(info, HttpMethod.Post, "/api/sip-monitor", json, ct);
    }

    /// <summary>
    /// Adónde mandar el RTP. La ranura guarda <c>IP:PUERTO</c>: con
    /// <c>0.0.0.0</c> el equipo acepta cualquier origen y se le envía por
    /// unicast a su propia IP; con una IP de multicast hay que emitir al grupo.
    /// </summary>
    private static IPEndPoint ResolveTarget(SpeakerConnectionInfo info, string address)
    {
        string[] parts = address.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out int port) || port is < 1 or > 65535
            || !IPAddress.TryParse(parts[0], out var ip))
            throw new DriverException(
                $"El parlante tiene una dirección de monitor ilegible ('{address}'): use IP:PUERTO en su página Monitor.");

        // 0.0.0.0 = escucha de cualquier origen: el destino es el propio equipo.
        if (Equals(ip, IPAddress.Any))
        {
            if (!IPAddress.TryParse(info.Host, out var host))
                throw new DriverException($"No se pudo resolver la dirección del parlante ('{info.Host}').");
            return new IPEndPoint(host, port);
        }
        return new IPEndPoint(ip, port);
    }

    /// <summary>
    /// Deja el equipo listo para recibir audio: si no tiene ninguna dirección de
    /// monitor configurada le pone <c>0.0.0.0:9999</c> (escuchar de cualquier
    /// origen), que es lo que necesita el canal en vivo del VMS.
    /// </summary>
    private static async Task<MonitorConfig> EnsureMonitorAsync(SpeakerConnectionInfo info, CancellationToken ct)
    {
        var monitor = await GetMonitorAsync(info, ct);
        if (monitor.Primary is not null) return monitor;

        var addresses = new List<string> { $"0.0.0.0:{DefaultMonitorPort}" };
        addresses.AddRange(Enumerable.Repeat("", 9));
        await SetMonitorAsync(info, addresses, monitor.Volume ?? 80, ct);
        return await GetMonitorAsync(info, ct);
    }

    // ------------------------------------------------------------------
    // Identificación y capacidades
    // ------------------------------------------------------------------

    public async Task<SpeakerInfo> ProbeAsync(SpeakerConnectionInfo info, CancellationToken ct = default)
    {
        string body = await CallAsync(info, HttpMethod.Get, "/api/overview", null, ct);
        var overview = Read(body, root => (JsonElement?)root.Clone());
        if (overview is not { ValueKind: JsonValueKind.Object } device || !device.TryGetProperty("device_type", out _))
            throw new DriverException("El equipo respondió algo que no es la API de un DS-PA: verifique la dirección y el puerto.");

        string? model = Str(device, "device_type");
        string? serial = Str(device, "serial_number");
        string? firmware = Str(device, "software_version");

        // Dejar el monitor listo: sin dirección configurada el equipo descarta
        // en silencio todo lo que le mande el servidor.
        var monitor = await EnsureMonitorAsync(info, ct);

        var capabilities = new SpeakerCapabilities(
            SupportsLibrary: false,
            SupportsTts: false,
            SupportsLiveAudio: monitor.Primary is not null,
            SupportsVolume: true,
            LiveCodecs: ["G.711ulaw"],
            TtsLanguages: [],
            UploadFormats: [],
            MaxUploadBytes: 0);

        return new SpeakerInfo(model, serial, firmware, "AudioAdapter", capabilities, monitor.Volume);
    }

    // ------------------------------------------------------------------
    // Audio en vivo (RTP)
    // ------------------------------------------------------------------

    public async Task<ISpeakerAudioSession> OpenLiveAudioAsync(SpeakerConnectionInfo info, CancellationToken ct = default)
    {
        var monitor = await EnsureMonitorAsync(info, ct);
        string address = monitor.Primary
            ?? throw new DriverException("El parlante no tiene dirección de monitor y no se pudo configurar sola.");
        return new RtpAudioSession(ResolveTarget(info, address));
    }

    /// <summary>
    /// Flujo RTP G.711 µ-law hacia el monitor del equipo. Quien escribe marca el
    /// ritmo (contrato de <see cref="ISpeakerAudioSession"/>); aquí solo se
    /// trocea en paquetes de 20 ms, que es lo que espera el parlante.
    /// </summary>
    private sealed class RtpAudioSession : ISpeakerAudioSession
    {
        /// <summary>160 muestras = 20 ms de G.711 a 8 kHz.</summary>
        private const int PayloadBytes = 160;

        /// <summary>Tipo de carga RTP estático de G.711 µ-law (RFC 3551).</summary>
        private const byte PayloadTypeMuLaw = 0;

        private const int HeaderBytes = 12;

        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly IPEndPoint _target;
        private readonly byte[] _packet = new byte[HeaderBytes + PayloadBytes];
        private readonly byte[] _pending = new byte[PayloadBytes];
        private readonly uint _ssrc;
        private int _pendingBytes;
        private ushort _sequence;
        private uint _timestamp;
        private bool _disposed;

        public RtpAudioSession(IPEndPoint target)
        {
            _target = target;
            _ssrc = (uint)Random.Shared.Next(1, int.MaxValue);
            _sequence = (ushort)Random.Shared.Next(0, ushort.MaxValue);
            _timestamp = (uint)Random.Shared.Next(0, int.MaxValue);
            if (IsMulticast(target.Address))
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 8);
        }

        private static bool IsMulticast(IPAddress address) =>
            address.AddressFamily == AddressFamily.InterNetwork && (address.GetAddressBytes()[0] & 0xF0) == 0xE0;

        public string Codec => "ulaw";

        public bool IsAlive => !_disposed;

        public async Task WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Completar primero lo que quedó a medio paquete en la escritura anterior.
            if (_pendingBytes > 0)
            {
                int take = Math.Min(PayloadBytes - _pendingBytes, frame.Length);
                frame.Span[..take].CopyTo(_pending.AsSpan(_pendingBytes));
                _pendingBytes += take;
                frame = frame[take..];
                if (_pendingBytes < PayloadBytes) return;
                await SendPacketAsync(_pending, ct);
                _pendingBytes = 0;
            }

            while (frame.Length >= PayloadBytes)
            {
                await SendPacketAsync(frame[..PayloadBytes], ct);
                frame = frame[PayloadBytes..];
            }

            if (frame.Length > 0)
            {
                frame.Span.CopyTo(_pending);
                _pendingBytes = frame.Length;
            }
        }

        private async Task SendPacketAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            var header = _packet.AsSpan(0, HeaderBytes);
            header[0] = 0x80;                       // versión 2, sin relleno ni extensión
            header[1] = PayloadTypeMuLaw;
            BinaryPrimitives.WriteUInt16BigEndian(header[2..], _sequence);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], _timestamp);
            BinaryPrimitives.WriteUInt32BigEndian(header[8..], _ssrc);
            payload.Span.CopyTo(_packet.AsSpan(HeaderBytes));

            unchecked { _sequence++; _timestamp += PayloadBytes; }

            try { await _socket.SendToAsync(_packet, SocketFlags.None, _target, ct); }
            catch (SocketException ex)
            {
                throw new DriverException($"No se pudo enviar audio al parlante en {_target}: {ex.Message}", ex);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    // ------------------------------------------------------------------
    // Volumen y estado
    // ------------------------------------------------------------------

    public async Task<int?> GetVolumeAsync(SpeakerConnectionInfo info, CancellationToken ct = default) =>
        (await GetMonitorAsync(info, ct)).Volume;

    public async Task SetVolumeAsync(SpeakerConnectionInfo info, int volume, CancellationToken ct = default)
    {
        // El equipo acepta 1..100; el VMS ofrece 0..100.
        int clamped = Math.Clamp(volume, 1, 100);
        var monitor = await GetMonitorAsync(info, ct);
        await SetMonitorAsync(info, monitor.Addresses, clamped, ct);
    }

    public async Task<SpeakerPlaybackState> GetPlaybackStateAsync(SpeakerConnectionInfo info, CancellationToken ct = default)
    {
        string body = await CallAsync(info, HttpMethod.Get, "/api/overview", null, ct);
        string status = Read(body, root => Str(root, "device_status")) ?? "";
        // En reposo dice "idle"; mientras suena el monitor deja de decirlo.
        return new SpeakerPlaybackState(!string.Equals(status, "idle", StringComparison.OrdinalIgnoreCase), null);
    }

    /// <summary>
    /// El equipo solo suena mientras el servidor le manda RTP: se detiene
    /// dejando de enviar, no hay nada que cortar en el propio parlante.
    /// </summary>
    public Task StopAsync(SpeakerConnectionInfo info, CancellationToken ct = default) => Task.CompletedTask;

    // ------------------------------------------------------------------
    // Sin biblioteca ni texto a voz
    // ------------------------------------------------------------------

    private const string NoLibrary =
        "Los adaptadores de audio DS-PA no guardan audios: el sonido se les envía en vivo desde el servidor.";

    public Task<IReadOnlyList<SpeakerAudioItem>> GetLibraryAsync(SpeakerConnectionInfo info, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SpeakerAudioItem>>([]);

    public Task<SpeakerAudioItem> UploadAudioAsync(SpeakerConnectionInfo info, string name, string format, byte[] content,
        CancellationToken ct = default) => throw new NotSupportedException(NoLibrary);

    public Task DeleteAudioAsync(SpeakerConnectionInfo info, long audioId, CancellationToken ct = default) =>
        throw new NotSupportedException(NoLibrary);

    public Task<SpeakerAudioItem> RenameAudioAsync(SpeakerConnectionInfo info, long audioId, string newName,
        CancellationToken ct = default) => throw new NotSupportedException(NoLibrary);

    public Task<(byte[] Content, string ContentType, string FileName)> DownloadAudioAsync(SpeakerConnectionInfo info,
        long audioId, CancellationToken ct = default) => throw new NotSupportedException(NoLibrary);

    public Task<SpeakerAudioItem> CreateTtsAsync(SpeakerConnectionInfo info, string name, string text, string language,
        string voice, CancellationToken ct = default) =>
        throw new NotSupportedException("Los adaptadores de audio DS-PA no generan texto a voz; el VMS lo envía como audio en vivo.");

    public Task PlayLibraryAsync(SpeakerConnectionInfo info, long audioId, CancellationToken ct = default) =>
        throw new NotSupportedException(NoLibrary);
}
