using System.Net.Sockets;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Hikvision.Interop;

namespace TrueCentralVms.Drivers.Hikvision;

public sealed class HikvisionSdkSpeakerDriverFactory : ISpeakerDriverFactory
{
    public string DriverKey => HikvisionSdkSpeakerDriver.DriverKey;
    public string DisplayName => "Hikvision por SDK (audio bidireccional de cámaras, terminales y grabadores, puerto 8000)";
    public int DefaultPort => 8000;
    public bool DefaultHttps => false;
    public ISpeakerDriver Create() => new HikvisionSdkSpeakerDriver();
}

/// <summary>
/// El altavoz de un equipo Hikvision por su audio bidireccional del SDK
/// (<c>NET_DVR_StartVoiceCom_MR_V30</c>, el mismo de la citofonía): para los
/// equipos que no lo ofrecen por ISAPI, como los terminales faciales. Solo voz
/// en vivo y sonidos del servidor (se transmiten): no hay biblioteca, texto a
/// voz ni volumen. El sondeo de estado no inicia sesión: mira que el puerto
/// del SDK conteste (así no se gasta un intento con credenciales cada minuto).
/// </summary>
public sealed class HikvisionSdkSpeakerDriver : ISpeakerDriver
{
    public const string DriverKey = "hikvision-sdk-voice";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);

    private static NotSupportedException NoLibrary() =>
        new("El audio por SDK solo transmite en vivo: el equipo no tiene biblioteca de audios ni texto a voz.");

    public Task<SpeakerInfo> ProbeAsync(SpeakerConnectionInfo info, CancellationToken ct = default) => Task.Run(() =>
    {
        int userId = HikvisionSdkVoice.Login(info.Host, info.Port, info.Username, info.Password, "equipo");
        try
        {
            string codec = HikvisionSdkVoice.CodecOf(userId)
                ?? throw new DriverException("El equipo no tiene audio bidireccional por SDK con un códec que el VMS sepa generar (G.711µ o G.711A).");
            var caps = new SpeakerCapabilities(SupportsLibrary: false, SupportsTts: false, SupportsLiveAudio: true, SupportsVolume: false,
                LiveCodecs: [codec == "ulaw" ? "G.711ulaw" : "G.711alaw"], TtsLanguages: [], UploadFormats: [], MaxUploadBytes: 0);
            // Modelo y serie los pone su equipo (terminal o cámara): acá no hace falta otra consulta.
            return new SpeakerInfo(null, null, null, null, caps, null);
        }
        finally { CHCNetSDK.NET_DVR_Logout(userId); }
    }, ct);

    public Task<ISpeakerAudioSession> OpenLiveAudioAsync(SpeakerConnectionInfo info, CancellationToken ct = default) =>
        Task.Run<ISpeakerAudioSession>(() =>
        {
            int userId = HikvisionSdkVoice.Login(info.Host, info.Port, info.Username, info.Password, "equipo");
            try
            {
                string codec = HikvisionSdkVoice.CodecOf(userId)
                    ?? throw new DriverException("El equipo usa un códec de voz que el VMS no sabe generar (se admite G.711µ y G.711A).");
                var session = new HikvisionSdkVoiceSession(userId, codec, onAudio: null);
                session.Start(info.Host, "equipo");
                return session;
            }
            catch
            {
                CHCNetSDK.NET_DVR_Logout(userId);
                throw;
            }
        }, ct);

    /// <summary>Sondeo de estado: que el puerto del SDK conteste (sin iniciar sesión).</summary>
    public async Task<int?> GetVolumeAsync(SpeakerConnectionInfo info, CancellationToken ct = default)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try { await client.ConnectAsync(info.Host, info.Port, timeout.Token); }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new DriverException($"El equipo {info.Host}:{info.Port} no responde en el puerto del SDK.");
        }
        return null;
    }

    public Task SetVolumeAsync(SpeakerConnectionInfo info, int volume, CancellationToken ct = default) =>
        throw new NotSupportedException("El volumen de este equipo se ajusta en el propio equipo.");

    // Lo que suena por SDK es una transmisión del servidor: detenerla la corta el servidor.
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
