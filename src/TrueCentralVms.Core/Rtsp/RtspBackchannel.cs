using System.Security.Cryptography;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Core.Rtsp;

/// <summary>
/// Audio hacia el altavoz de un equipo por el canal de vuelta RTSP del
/// estándar ONVIF (Perfil T, "backchannel"): se pide la descripción con la
/// cabecera <c>Require: www.onvif.org/ver20/backchannel</c>, el equipo
/// agrega una pista de audio marcada <c>sendonly</c>, se prepara solo esa
/// pista sobre TCP y lo que se escribe viaja como RTP entrelazado. Lo usan
/// Hanwha (Wisenet no tiene audio de vuelta por HTTP) y sirve igual para
/// cualquier equipo ONVIF que lo ofrezca. Solo G.711 (µ-law o A-law, 8 kHz).
/// </summary>
public sealed class RtspBackchannel : ISpeakerAudioSession
{
    private const string Require = "www.onvif.org/ver20/backchannel";

    private readonly RtspConnection _rtsp;
    private readonly string _playUrl;
    private readonly int _payloadType;
    private readonly int _channel;
    private readonly uint _ssrc = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
    private readonly CancellationTokenSource _cts = new();
    private Task? _drain, _keepAlive;
    private ushort _sequence = (ushort)Random.Shared.Next(ushort.MaxValue);
    private uint _timestamp = (uint)Random.Shared.Next();
    private bool _first = true;
    private volatile bool _alive = true;

    private RtspBackchannel(RtspConnection rtsp, string playUrl, string codec, int payloadType, int channel)
    {
        _rtsp = rtsp;
        _playUrl = playUrl;
        Codec = codec;
        _payloadType = payloadType;
        _channel = channel;
    }

    public string Codec { get; }
    public bool IsAlive => _alive;

    /// <summary>Pista de audio de vuelta que ofrece el equipo.</summary>
    internal sealed record Track(string Control, int PayloadType, string Codec);

    /// <summary>
    /// Abre el canal. <paramref name="url"/> es una URL RTSP en vivo del equipo
    /// con las credenciales embebidas (usuario:clave@). <paramref name="noun"/>
    /// nombra al equipo en los mensajes ("cámara Hanwha").
    /// </summary>
    public static async Task<RtspBackchannel> OpenAsync(string url, string noun, CancellationToken ct)
    {
        var rtsp = new RtspConnection(url);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try { await rtsp.ConnectAsync(timeout.Token); }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new DriverException($"No se pudo conectar al RTSP de la {noun}.", ex);
            }

            var describe = await rtsp.SendAsync("DESCRIBE", rtsp.BaseUrl, timeout.Token,
                [("Accept", "application/sdp"), ("Require", Require)]);
            if (describe.Status == 401) throw new DriverException($"La {noun} rechazó el usuario o la contraseña del RTSP.");
            if (describe.Status == 551)
                throw new DriverException($"La {noun} no ofrece audio de vuelta (backchannel ONVIF): revise que tenga salida de audio habilitada.");
            if (describe.Status != 200) throw new DriverException($"La {noun} respondió {describe.Status} a DESCRIBE.");

            string baseUrl = describe.Header("Content-Base") ?? rtsp.BaseUrl;
            if (FindTrack(describe.Body, baseUrl) is not { } track)
                throw new DriverException($"La {noun} no ofrece audio de vuelta en G.711: revise que tenga salida de audio y el códec en G.711.");

            var setup = await rtsp.SendAsync("SETUP", track.Control, timeout.Token,
                [("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"), ("Require", Require)]);
            if (setup.Status != 200) throw new DriverException($"La {noun} respondió {setup.Status} al preparar el audio de vuelta.");
            rtsp.SessionId = setup.Header("Session")?.Split(';')[0].Trim();
            int channel = InterleavedChannel(setup.Header("Transport")) ?? 0;

            var play = await rtsp.SendAsync("PLAY", baseUrl.TrimEnd('/'), timeout.Token, [("Range", "npt=0-"), ("Require", Require)]);
            if (play.Status != 200) throw new DriverException($"La {noun} respondió {play.Status} al iniciar el audio de vuelta.");

            var session = new RtspBackchannel(rtsp, baseUrl.TrimEnd('/'), track.Codec, track.PayloadType, channel);
            session.Start();
            return session;
        }
        catch
        {
            await rtsp.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// La pista de audio "sendonly" (la que el equipo recibe) con G.711:
    /// su control (absoluto o relativo a Content-Base) y su tipo de carga.
    /// </summary>
    internal static Track? FindTrack(string sdp, string baseUrl)
    {
        var sections = new List<List<string>>();
        foreach (string raw in sdp.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("m=", StringComparison.Ordinal)) sections.Add([line]);
            else if (sections.Count > 0) sections[^1].Add(line);
        }
        foreach (var section in sections)
        {
            if (!section[0].StartsWith("m=audio", StringComparison.OrdinalIgnoreCase) || !section.Contains("a=sendonly")) continue;
            string[] formats = section[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(3).ToArray();
            (int Pt, string Codec)? pick = null;
            foreach (string f in formats)
            {
                if (!int.TryParse(f, out int pt)) continue;
                string? map = section.FirstOrDefault(l => l.StartsWith($"a=rtpmap:{pt} ", StringComparison.Ordinal));
                string name = map?.Split(' ', 2)[1].Split('/')[0].ToUpperInvariant() ?? (pt == 0 ? "PCMU" : pt == 8 ? "PCMA" : "");
                if (name == "PCMU") { pick = (pt, "ulaw"); break; }
                if (name == "PCMA") pick ??= (pt, "alaw");
            }
            if (pick is null) continue;
            string control = section.FirstOrDefault(l => l.StartsWith("a=control:", StringComparison.Ordinal))?[10..] ?? "";
            string url = control.Contains("://", StringComparison.Ordinal) ? control
                : control.Length == 0 || control == "*" ? baseUrl.TrimEnd('/')
                : baseUrl.TrimEnd('/') + "/" + control;
            return new Track(url, pick.Value.Pt, pick.Value.Codec);
        }
        return null;
    }

    private static int? InterleavedChannel(string? transport)
    {
        if (transport is null) return null;
        int at = transport.IndexOf("interleaved=", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        string value = transport[(at + 12)..].Split(';')[0].Split('-')[0];
        return int.TryParse(value, out int channel) ? channel : null;
    }

    private void Start()
    {
        // El equipo puede mandar RTCP o respuestas: se leen y descartan para que no llene el búfer;
        // si la conexión se cierra, el canal queda muerto.
        _drain = Task.Run(async () =>
        {
            byte[] buffer = new byte[64 * 1024];
            try
            {
                while (!_cts.IsCancellationRequested)
                    if (await _rtsp.ReadFrameAsync(buffer, _cts.Token) is null) break;
            }
            catch (Exception) { }
            finally { _alive = false; }
        });
        // Sin señales de vida, los equipos cierran la sesión a los 60 s.
        _keepAlive = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(25), _cts.Token);
                    await _rtsp.SendWithoutReplyAsync("GET_PARAMETER", _playUrl, _cts.Token);
                }
            }
            catch (Exception) { }
        });
    }

    /// <summary>Un trozo de G.711 (un byte por muestra a 8 kHz) → un paquete RTP.</summary>
    public async Task WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        if (!_alive) throw new DriverException("El equipo cerró el canal de audio.");
        if (frame.IsEmpty) return;
        byte[] packet = new byte[12 + frame.Length];
        packet[0] = 0x80;
        packet[1] = (byte)((_first ? 0x80 : 0) | (_payloadType & 0x7F));
        _first = false;
        packet[2] = (byte)(_sequence >> 8);
        packet[3] = (byte)_sequence;
        packet[4] = (byte)(_timestamp >> 24);
        packet[5] = (byte)(_timestamp >> 16);
        packet[6] = (byte)(_timestamp >> 8);
        packet[7] = (byte)_timestamp;
        packet[8] = (byte)(_ssrc >> 24);
        packet[9] = (byte)(_ssrc >> 16);
        packet[10] = (byte)(_ssrc >> 8);
        packet[11] = (byte)_ssrc;
        frame.CopyTo(packet.AsMemory(12));
        _sequence++;
        _timestamp += (uint)frame.Length;
        try { await _rtsp.WriteFrameAsync(_channel, packet, ct); }
        catch (IOException ex)
        {
            _alive = false;
            throw new DriverException("Se cortó el canal de audio con el equipo.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _alive = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _rtsp.SendWithoutReplyAsync("TEARDOWN", _playUrl, timeout.Token);
        }
        catch (Exception) { }
        _cts.Cancel();
        await _rtsp.DisposeAsync();
        foreach (var task in new[] { _drain, _keepAlive })
            if (task is not null) try { await task; } catch (Exception) { }
        _cts.Dispose();
    }
}
