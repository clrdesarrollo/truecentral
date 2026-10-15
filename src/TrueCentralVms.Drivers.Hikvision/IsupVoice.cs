using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Core.Isup;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Voz con un parlante o un frente de citofonía conectado por ISUP: la sesión de voz del servidor ISUP (no el SDK ni el
/// audioData de ISAPI, que no llegan a un equipo detrás de NAT). El equipo fija el códec y el tamaño de trama; lo que se escribe
/// se reparte en tramas de ese tamaño. Solo G.711: es lo que generan y reproducen los módulos del servidor.
/// </summary>
internal sealed class IsupVoiceSession : ISpeakerAudioSession, IIntercomVoiceSession
{
    private readonly IIsupVoiceSession _voice;
    private readonly byte[] _pending;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _filled;

    private IsupVoiceSession(IIsupVoiceSession voice, string codec)
    {
        _voice = voice;
        Codec = codec;
        _pending = new byte[Math.Max(voice.FrameBytes, 1)];
    }

    /// <summary>"ulaw" / "alaw" (lo que esperan los módulos del servidor).</summary>
    public string Codec { get; }

    public bool IsAlive => _voice.IsAlive;

    /// <param name="broadcast">Solo bajada (parlantes): el equipo no capta ni envía audio.</param>
    /// <param name="noun">Cómo se nombra el equipo en los mensajes ("parlante", "citófono").</param>
    public static async Task<IsupVoiceSession> OpenAsync(string isupId, Action<ReadOnlyMemory<byte>> onAudio, bool broadcast,
        string noun, CancellationToken ct)
    {
        var gateway = IsupGateway.Require();
        if (!gateway.IsOnline(isupId))
            throw new DriverException($"El {noun} ISUP «{isupId}» no está conectado al servidor en este momento.");
        var voice = await gateway.OpenVoiceAsync(isupId, 1, onAudio, broadcast, ct);
        string? codec = voice.Codec.ToUpperInvariant() switch
        {
            "G711U" => "ulaw",
            "G711A" => "alaw",
            _ => null,
        };
        if (codec is null)
        {
            await voice.DisposeAsync();
            throw new DriverException($"El {noun} usa audio {voice.Codec} por ISUP y el servidor solo maneja G.711: " +
                                      "cambie el códec de audio del equipo a G.711.");
        }
        return new IsupVoiceSession(voice, codec);
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        if (!_voice.IsAlive) throw new DriverException("El equipo cortó la sesión de voz ISUP.");
        await _writeLock.WaitAsync(ct);
        try
        {
            while (frame.Length > 0)
            {
                int n = Math.Min(frame.Length, _pending.Length - _filled);
                frame.Span[..n].CopyTo(_pending.AsSpan(_filled));
                _filled += n;
                frame = frame[n..];
                if (_filled < _pending.Length) continue;
                await _voice.SendAsync(_pending.ToArray(), ct);
                _filled = 0;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync() => _voice.DisposeAsync();
}
