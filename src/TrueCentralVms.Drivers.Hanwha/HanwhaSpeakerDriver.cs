using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Core.Rtsp;

namespace TrueCentralVms.Drivers.Hanwha;

public sealed class HanwhaSpeakerDriverFactory : ISpeakerDriverFactory
{
    public string DriverKey => HanwhaSpeakerDriver.DriverKey;
    public string DisplayName => "Hanwha / Wisenet (audio de vuelta por RTSP: cámaras con salida de audio)";
    public int DefaultPort => 80;
    public bool DefaultHttps => false;
    public ISpeakerDriver Create() => new HanwhaSpeakerDriver();
}

/// <summary>
/// La salida de audio de una cámara Wisenet. SUNAPI no recibe audio por HTTP:
/// la voz viaja por el canal de vuelta RTSP de ONVIF (<see cref="RtspBackchannel"/>)
/// sobre el stream en vivo del primer canal. El puerto configurado es el HTTP
/// (SUNAPI): el RTSP se le pregunta al equipo. Solo voz en vivo y sonidos del
/// servidor.
/// </summary>
public sealed class HanwhaSpeakerDriver : ISpeakerDriver
{
    public const string DriverKey = "hanwha-sunapi-audio";

    private static NotSupportedException NoLibrary() =>
        new("El audio de esta cámara Hanwha se transmite en vivo: no tiene biblioteca de audios ni texto a voz.");

    internal static DeviceConnectionInfo Connection(SpeakerConnectionInfo info) => new(info.Host, info.Port, info.Username, info.Password);

    /// <summary>¿Tiene salida de audio? media.cgi audiooutput responde (NG = el modelo no la trae).</summary>
    internal static async Task<bool> HasSpeakerAsync(SunapiClient client, CancellationToken ct)
    {
        try
        {
            // Una salida apagada en la configuración igual existe: se ofrece y el aviso lo da al abrir el audio.
            return (await client.GetAsync("media.cgi", "msubmenu=audiooutput&action=view", ct)).Count > 0;
        }
        catch (SunapiErrorException) { return false; }
    }

    public async Task<SpeakerInfo> ProbeAsync(SpeakerConnectionInfo info, CancellationToken ct = default)
    {
        using var client = new SunapiClient(Connection(info));
        if (!await HasSpeakerAsync(client, ct))
            throw new DriverException("La cámara Hanwha no tiene salida de audio (o está deshabilitada en su configuración).");
        var caps = new SpeakerCapabilities(SupportsLibrary: false, SupportsTts: false, SupportsLiveAudio: true, SupportsVolume: false,
            LiveCodecs: ["G.711ulaw", "G.711alaw"], TtsLanguages: [], UploadFormats: [], MaxUploadBytes: 0);
        return new SpeakerInfo(null, null, null, null, caps, null);
    }

    public async Task<ISpeakerAudioSession> OpenLiveAudioAsync(SpeakerConnectionInfo info, CancellationToken ct = default)
    {
        var connection = Connection(info);
        int rtspPort = 554;
        HanwhaRecordings.DeviceShape shape;
        using (var client = new SunapiClient(connection))
        {
            shape = await HanwhaRecordings.ShapeAsync(client, connection, ct);
            try
            {
                var rtsp = await client.GetAsync("network.cgi", "msubmenu=rtsp&action=view", ct);
                if (int.TryParse(rtsp.GetValueOrDefault("Port") ?? rtsp.GetValueOrDefault("RTSPPort"), out int port) && port > 0) rtspPort = port;
            }
            catch (SunapiErrorException) { }
        }
        string path = shape.ChannelCount > 1 ? "/0/profile1/media.smp" : "/profile1/media.smp";
        string url = $"rtsp://{Uri.EscapeDataString(info.Username)}:{Uri.EscapeDataString(info.Password)}@{info.Host}:{rtspPort}{path}";
        return await RtspBackchannel.OpenAsync(url, "cámara Hanwha", ct);
    }

    /// <summary>Sondeo de estado: una consulta liviana autenticada (no hay volumen que leer).</summary>
    public async Task<int?> GetVolumeAsync(SpeakerConnectionInfo info, CancellationToken ct = default)
    {
        using var client = new SunapiClient(Connection(info), TimeSpan.FromSeconds(5));
        await client.GetAsync("system.cgi", "msubmenu=deviceinfo&action=view", ct);
        return null;
    }

    public Task SetVolumeAsync(SpeakerConnectionInfo info, int volume, CancellationToken ct = default) =>
        throw new NotSupportedException("El volumen de esta cámara se ajusta en la propia cámara.");

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
