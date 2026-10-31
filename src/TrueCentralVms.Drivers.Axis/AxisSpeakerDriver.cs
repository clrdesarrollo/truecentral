using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Axis;

public sealed class AxisSpeakerDriverFactory : ISpeakerDriverFactory
{
    public string DriverKey => AxisSpeakerDriver.DriverKey;
    public string DisplayName => "Axis (audio por VAPIX: cámaras con altavoz, bocinas y parlantes de red)";
    public int DefaultPort => 80;
    public bool DefaultHttps => false;
    public ISpeakerDriver Create() => new AxisSpeakerDriver();
}

/// <summary>
/// El altavoz de un equipo Axis: la salida de audio de una cámara o un
/// parlante/bocina de red, por <c>POST /axis-cgi/audio/transmit.cgi</c> con
/// G.711 µ-law (audio/basic) mientras dure la transmisión. Solo voz en vivo y
/// sonidos del servidor (que se transmiten): la biblioteca de clips de los
/// parlantes Axis queda para su driver propio.
/// </summary>
public sealed class AxisSpeakerDriver : ISpeakerDriver
{
    public const string DriverKey = "axis-vapix-audio";

    private static NotSupportedException NoLibrary() =>
        new("El audio de este equipo Axis se transmite en vivo: no tiene biblioteca de audios ni texto a voz en el VMS.");

    internal static DeviceConnectionInfo Connection(SpeakerConnectionInfo info) => new(info.Host, info.Port, info.Username, info.Password);

    /// <summary>¿Tiene salida de audio? Properties.Audio.Audio=yes y un modo dúplex que admite recibir (full, half o post).</summary>
    internal static bool HasSpeaker(Dictionary<string, string> properties)
    {
        if (properties.GetValueOrDefault("Properties.Audio.Audio")?.Equals("yes", StringComparison.OrdinalIgnoreCase) != true) return false;
        string modes = properties.GetValueOrDefault("Properties.Audio.DuplexMode") ?? "";
        return modes.Length == 0 || modes.Split(',').Any(m => m.Trim() is "full" or "half" or "post");
    }

    public async Task<SpeakerInfo> ProbeAsync(SpeakerConnectionInfo info, CancellationToken ct = default)
    {
        using var client = new AxisVapixClient(Connection(info));
        var properties = await client.GetParamsAsync("Properties", ct);
        if (!HasSpeaker(properties))
            throw new DriverException("El equipo Axis no tiene salida de audio (o está deshabilitada en su configuración).");
        var caps = new SpeakerCapabilities(SupportsLibrary: false, SupportsTts: false, SupportsLiveAudio: true, SupportsVolume: false,
            LiveCodecs: ["G.711ulaw"], TtsLanguages: [], UploadFormats: [], MaxUploadBytes: 0);
        // Modelo y serie los pone su equipo (la cámara): acá no hace falta otra consulta.
        return new SpeakerInfo(null, null, null, null, caps, null);
    }

    public Task<ISpeakerAudioSession> OpenLiveAudioAsync(SpeakerConnectionInfo info, CancellationToken ct = default) =>
        AxisAudioSession.OpenAsync(Connection(info), ct);

    /// <summary>Sondeo de estado: una consulta liviana autenticada (no hay volumen que leer).</summary>
    public async Task<int?> GetVolumeAsync(SpeakerConnectionInfo info, CancellationToken ct = default)
    {
        using var client = new AxisVapixClient(Connection(info), TimeSpan.FromSeconds(5));
        await client.GetParamsAsync("Properties.Audio", ct);
        return null;
    }

    public Task SetVolumeAsync(SpeakerConnectionInfo info, int volume, CancellationToken ct = default) =>
        throw new NotSupportedException("El volumen de este equipo se ajusta en el propio equipo.");

    public Task StopAsync(SpeakerConnectionInfo info, CancellationToken ct = default) => Task.CompletedTask;

    public Task<SpeakerPlaybackState> GetPlaybackStateAsync(SpeakerConnectionInfo info, CancellationToken ct = default) =>
        Task.FromResult(new SpeakerPlaybackState(false, null));

    public Task<IReadOnlyList<SpeakerAudioItem>> GetLibraryAsync(SpeakerConnectionInfo info, CancellationToken ct = default) => throw NoLibrary();
    public Task<SpeakerAudioItem> UploadAudioAsync(SpeakerConnectionInfo info, string name, string format, byte[] content, CancellationToken ct = default) => throw NoLibrary();
    public Task DeleteAudioAsync(SpeakerConnectionInfo info, long audioId, CancellationToken ct = default) => throw NoLibrary();
    public Task<SpeakerAudioItem> RenameAudioAsync(SpeakerConnectionInfo info, long audioId, string newName, CancellationToken ct = default) => throw NoLibrary();
    public Task<(byte[] Content, string ContentType, string FileName)> DownloadAudioAsync(SpeakerConnectionInfo info, long audioId, CancellationToken ct = default) => throw NoLibrary();
    public Task<SpeakerAudioItem> CreateTtsAsync(SpeakerConnectionInfo info, string name, string text, string language, string voice, CancellationToken ct = default) => throw NoLibrary();
    public Task PlayLibraryAsync(SpeakerConnectionInfo info, long audioId, CancellationToken ct = default) => throw NoLibrary();
}

/// <summary>
/// Una transmisión a transmit.cgi: el cuerpo del POST es el audio y dura lo que
/// dure la sesión. Como el cuerpo se va escribiendo en vivo, el desafío Digest
/// se obtiene antes con una consulta liviana y el POST sale ya firmado
/// (<see cref="DigestAuthenticator"/>).
/// </summary>
internal sealed class AxisAudioSession : ISpeakerAudioSession
{
    /// <summary>Largo "infinito" que propone la documentación de VAPIX para un envío continuo.</summary>
    private const long StreamLength = 9_999_999;

    private readonly HttpClient _http;
    private readonly Pipe _pipe = new();
    private readonly CancellationTokenSource _cts = new();
    private Task<HttpResponseMessage>? _send;
    private long _written;

    private AxisAudioSession(HttpClient http) => _http = http;

    public string Codec => "ulaw";
    public bool IsAlive => _send is { IsCompleted: false };

    public static async Task<ISpeakerAudioSession> OpenAsync(DeviceConnectionInfo info, CancellationToken ct)
    {
        var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            BaseAddress = new Uri($"http://{info.Host}:{info.Port}/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        try
        {
            var digest = new DigestAuthenticator(info.Username, info.Password);
            using (var probe = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, probe.Token))
            using (var challenge = await http.GetAsync("axis-cgi/param.cgi?action=list&group=Properties.Audio", linked.Token))
            {
                if (challenge.StatusCode != HttpStatusCode.Unauthorized || !digest.ReadChallenge(challenge))
                    throw new DriverException("La cámara Axis no pidió autenticación como se esperaba: no se puede abrir el audio.");
            }

            var session = new AxisAudioSession(http);
            var uri = new Uri(http.BaseAddress!, "axis-cgi/audio/transmit.cgi");
            var content = new StreamContent(session._pipe.Reader.AsStream());
            content.Headers.ContentType = new MediaTypeHeaderValue("audio/basic");
            content.Headers.ContentLength = StreamLength;
            var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
            request.Headers.Authorization = digest.Build("POST", uri);
            session._send = http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, session._cts.Token);
            return session;
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        if (_send is { IsCompleted: true } done)
        {
            string why = done.IsFaulted ? done.Exception!.GetBaseException().Message
                : done.IsCanceled ? "cancelado" : $"HTTP {(int)done.Result.StatusCode}";
            throw new DriverException($"La cámara Axis cerró el canal de audio ({why}).");
        }
        if (_written + frame.Length >= StreamLength) throw new DriverException("Se alcanzó el largo máximo de la transmisión: vuelva a abrirla.");
        await _pipe.Writer.WriteAsync(frame, ct);
        _written += frame.Length;
    }

    public async ValueTask DisposeAsync()
    {
        // Primero se termina de escribir y se da un momento para que salga lo último;
        // después se corta (el cuerpo "infinito" no termina solo).
        await _pipe.Writer.CompleteAsync();
        if (_send is not null) await Task.WhenAny(_send, Task.Delay(250));
        _cts.Cancel();
        if (_send is not null)
            try { (await _send).Dispose(); } catch (Exception) { }
        _http.Dispose();
        _cts.Dispose();
    }
}
