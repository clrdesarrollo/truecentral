using System.Text.Json;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Server.Services.SpeakerAgents;

/// <summary>
/// Driver de los parlantes «TrueCentral Speaker» (Raspberry Pi o PC con Windows con el agente instalado).
/// Vive en el servidor y no en un proyecto de drivers porque no habla con el equipo por la red: usa la
/// conexión que el propio agente abrió (<see cref="SpeakerAgentHub"/>).
/// </summary>
public sealed class AgentSpeakerDriverFactory(SpeakerAgentHub hub) : ISpeakerDriverFactory
{
    public string DriverKey => SpeakerAgentProtocol.DriverKey;
    public string DisplayName => "TrueCentral Speaker (Raspberry Pi / Windows)";
    public int DefaultPort => 0;
    public bool DefaultHttps => false;
    public bool UsesAgent => true;
    public ISpeakerDriver Create() => new AgentSpeakerDriver(hub);
}

/// <summary>Etapa S1: identificación, volumen, voz en vivo y sonidos del servidor (G.711 a-law a 8 kHz).</summary>
public sealed class AgentSpeakerDriver(SpeakerAgentHub hub) : ISpeakerDriver
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private AgentLink LinkOf(SpeakerConnectionInfo info) =>
        info.AgentId is null
            ? throw new DriverException("El parlante aún no se enrola: genere un código de enrolamiento en TrueCentral y escríbalo en el equipo.")
            : hub.Find(info.AgentId) ?? throw new DriverException("El TrueCentral Speaker no está conectado al servidor (revise que el equipo esté encendido y alcance al servidor).");

    public async Task<SpeakerInfo> ProbeAsync(SpeakerConnectionInfo info, CancellationToken ct = default)
    {
        var result = await LinkOf(info).RequestAsync<AgentProbeResult>(SpeakerAgentProtocol.Methods.Probe, null, RequestTimeout, ct)
                     ?? throw new DriverException("El TrueCentral Speaker no informó su identificación.");
        var capabilities = new SpeakerCapabilities(
            SupportsLibrary: false,
            SupportsTts: false,
            SupportsLiveAudio: true,
            SupportsVolume: true,
            LiveCodecs: result.LiveCodecs,
            TtsLanguages: [],
            UploadFormats: [],
            MaxUploadBytes: 0);
        return new SpeakerInfo(result.Model, result.SerialNumber, result.FirmwareVersion, result.DeviceType, capabilities, result.Volume);
    }

    public async Task<int?> GetVolumeAsync(SpeakerConnectionInfo info, CancellationToken ct = default) =>
        (await LinkOf(info).RequestAsync<AgentVolumeArgs>(SpeakerAgentProtocol.Methods.VolumeGet, null, RequestTimeout, ct))?.Volume;

    public Task SetVolumeAsync(SpeakerConnectionInfo info, int volume, CancellationToken ct = default) =>
        LinkOf(info).RequestAsync(SpeakerAgentProtocol.Methods.VolumeSet, new AgentVolumeArgs(Math.Clamp(volume, 0, 100)), RequestTimeout, ct);

    public Task StopAsync(SpeakerConnectionInfo info, CancellationToken ct = default) =>
        LinkOf(info).RequestAsync(SpeakerAgentProtocol.Methods.Stop, null, RequestTimeout, ct);

    public async Task<SpeakerPlaybackState> GetPlaybackStateAsync(SpeakerConnectionInfo info, CancellationToken ct = default)
    {
        var state = await LinkOf(info).RequestAsync<AgentStateResult>(SpeakerAgentProtocol.Methods.State, null, RequestTimeout, ct);
        return new SpeakerPlaybackState(state?.IsPlaying ?? false, state?.CurrentName);
    }

    /// <summary>Tono de prueba del altavoz (no forma parte de <see cref="ISpeakerDriver"/>: solo lo tienen los agentes).</summary>
    public Task TestToneAsync(SpeakerConnectionInfo info, int seconds, CancellationToken ct = default) =>
        LinkOf(info).RequestAsync(SpeakerAgentProtocol.Methods.TestTone, new AgentToneArgs(Math.Clamp(seconds, 1, 10)), RequestTimeout, ct);

    public async Task<ISpeakerAudioSession> OpenLiveAudioAsync(SpeakerConnectionInfo info, CancellationToken ct = default)
    {
        var link = LinkOf(info);
        // a-law: los sonidos del servidor ya están convertidos a G.711 y la voz del operador se codifica igual que para los Hikvision.
        const string codec = SpeakerAgentProtocol.Codecs.ALaw;
        var opened = await link.RequestAsync<AgentStreamArgs>(SpeakerAgentProtocol.Methods.LiveOpen, new AgentLiveOpenArgs(codec), RequestTimeout, ct)
                     ?? throw new DriverException("El TrueCentral Speaker no abrió el canal de audio.");
        return new AgentAudioSession(link, (ushort)opened.Stream, codec);
    }

    // La biblioteca y el texto a voz llegan en etapas posteriores (S2/S3): por ahora el agente declara que no los tiene.
    public Task<IReadOnlyList<SpeakerAudioItem>> GetLibraryAsync(SpeakerConnectionInfo info, CancellationToken ct = default) => throw NoLibrary();
    public Task<SpeakerAudioItem> UploadAudioAsync(SpeakerConnectionInfo info, string name, string format, byte[] content, CancellationToken ct = default) => throw NoLibrary();
    public Task DeleteAudioAsync(SpeakerConnectionInfo info, long audioId, CancellationToken ct = default) => throw NoLibrary();
    public Task<SpeakerAudioItem> RenameAudioAsync(SpeakerConnectionInfo info, long audioId, string newName, CancellationToken ct = default) => throw NoLibrary();
    public Task<(byte[] Content, string ContentType, string FileName)> DownloadAudioAsync(SpeakerConnectionInfo info, long audioId, CancellationToken ct = default) => throw NoLibrary();
    public Task PlayLibraryAsync(SpeakerConnectionInfo info, long audioId, CancellationToken ct = default) => throw NoLibrary();
    public Task<SpeakerAudioItem> CreateTtsAsync(SpeakerConnectionInfo info, string name, string text, string language, string voice,
        CancellationToken ct = default) => throw new NotSupportedException("Este TrueCentral Speaker aún no genera texto a voz.");

    private static NotSupportedException NoLibrary() => new("Este TrueCentral Speaker aún no tiene biblioteca de audios.");

    /// <summary>Flujo en vivo hacia el agente: cada escritura viaja como una trama binaria por el WebSocket.</summary>
    private sealed class AgentAudioSession : ISpeakerAudioSession
    {
        private readonly AgentLink _link;
        private readonly ushort _stream;
        private volatile bool _closedByAgent;
        private int _disposed;

        public AgentAudioSession(AgentLink link, ushort stream, string codec)
        {
            _link = link;
            _stream = stream;
            Codec = codec;
            _link.EventReceived += OnEvent;
        }

        public string Codec { get; }
        public bool IsAlive => _disposed == 0 && !_closedByAgent && _link.IsOpen;

        private void OnEvent(string name, System.Text.Json.JsonElement? data)
        {
            if (name != SpeakerAgentProtocol.Events.StreamClosed || data is not { } element) return;
            var closed = element.Deserialize<AgentStreamArgs>(SpeakerAgentProtocol.Json);
            if (closed?.Stream == _stream) _closedByAgent = true;
        }

        public Task WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
        {
            if (!IsAlive) throw new DriverException("El TrueCentral Speaker cerró el canal de audio.");
            return _link.SendAudioAsync(_stream, frame, ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _link.EventReceived -= OnEvent;
            if (_closedByAgent || !_link.IsOpen) return;
            try { await _link.RequestAsync(SpeakerAgentProtocol.Methods.LiveClose, new AgentStreamArgs(_stream), TimeSpan.FromSeconds(2), CancellationToken.None); }
            catch (DriverException) { /* el agente igual cierra el flujo solo tras unos segundos sin audio */ }
        }
    }
}
