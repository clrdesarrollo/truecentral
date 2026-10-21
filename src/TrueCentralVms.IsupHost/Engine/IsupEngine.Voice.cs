using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using TrueCentralVms.IsupHost.Native;

namespace TrueCentralVms.IsupHost.Engine;

/// <summary>
/// Voz bidireccional con un equipo por el servidor de medios ISUP: el equipo se conecta al puerto de voz y por ese enlace llega su audio
/// (<see cref="AudioReceived"/>) y se le manda el nuestro en tramas de su codificación.
/// </summary>
public sealed class VoiceSession
{
    internal VoiceSession(string deviceId, int channel, int userId) { DeviceId = deviceId; Channel = channel; UserId = userId; }

    public string DeviceId { get; }
    public int Channel { get; }
    internal int UserId;
    internal int Session = -1;
    internal int LinkHandle = -1;
    internal readonly TaskCompletionSource Linked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile bool _closed;

    /// <summary>"G722", "G711U", "G711A", "G726", "AAC", "MP2L2", "PCM" o "MP3" según lo que informe el equipo al conectarse.</summary>
    public string Codec { get; internal set; } = "G711U";
    /// <summary>Bytes por trama que espera el equipo (160 en G.711 = 20 ms; 80 en G.722).</summary>
    public int FrameBytes => Codec == "G722" ? 80 : 160;
    public bool IsClosed => _closed;

    /// <summary>Audio que manda el equipo (en su codificación). Corre en un hilo del SDK: encolar y volver.</summary>
    public event Action<byte[]>? AudioReceived;
    /// <summary>La sesión terminó (la cerró el equipo, el VMS o se cortó la conexión).</summary>
    public event Action? Closed;

    internal void Deliver(byte[] data) { try { AudioReceived?.Invoke(data); } catch { /* el lector nunca debe tumbar el hilo del SDK */ } }

    internal void MarkClosed()
    {
        if (_closed) return;
        _closed = true;
        Linked.TrySetResult();
        try { Closed?.Invoke(); } catch { }
    }

    internal static string CodecName(int encodeType) => encodeType switch
    {
        0 => "G722", 1 => "G711U", 2 => "G711A", 3 => "G726", 4 => "AAC", 5 => "MP2L2", 6 => "PCM", 7 => "MP3", _ => $"tipo {encodeType}",
    };
}

public sealed partial class IsupEngine
{
    readonly ConcurrentDictionary<int, VoiceSession> _voiceSessions = new();   // sesión ISUP → voz
    readonly ConcurrentDictionary<int, VoiceSession> _voiceByLink = new();     // handle del enlace → voz

    /// <summary>
    /// Abre la voz con un canal de audio del equipo (desde 1). Con <paramref name="broadcast"/> se pide solo bajada (parlantes: el equipo
    /// no manda audio). Espera a que el equipo se conecte al puerto de voz (hasta 10 s). Lanza <see cref="IsupCommandException"/> si falla.
    /// </summary>
    public async Task<VoiceSession> OpenVoiceAsync(string deviceId, int channel, bool broadcast, CancellationToken ct)
    {
        if (!_userByDevice.TryGetValue(deviceId, out int userId)) throw new IsupCommandException(-1, "El equipo no está conectado al servidor por ISUP.");
        if (_hVoice < 0) throw new IsupCommandException(-1, "El puerto de voz ISUP no está abierto en el servidor.");
        var session = new VoiceSession(deviceId, channel, userId);
        var input = new NET_EHOME_VOICE_TALK_IN { dwVoiceChan = (uint)Math.Max(1, channel), byBroadcast = (byte)(broadcast ? 1 : 0), byBroadLevel = 15, byBroadVolume = broadcast ? (byte)15 : (byte)0 };
        Isup.SetIp(ref input.struStreamSever, AddressFor(deviceId), (ushort)_options.VoicePort);
        var output = new NET_EHOME_VOICE_TALK_OUT();
        lock (GateOf(deviceId))
        {
            if (!Isup.NET_ECMS_StartVoiceWithStmServer(userId, ref input, ref output))
            {
                int err = Isup.NET_ECMS_GetLastError();
                throw new IsupCommandException(err, $"El equipo rechazó la voz (error {err}{ErrorHint(err)}).");
            }
            session.Session = output.lSessionID;
            _voiceSessions[session.Session] = session;
            var push = new NET_EHOME_PUSHVOICE_IN { dwSize = (uint)Marshal.SizeOf<NET_EHOME_PUSHVOICE_IN>(), lSessionID = output.lSessionID };
            var pushOut = new NET_EHOME_PUSHVOICE_OUT { dwSize = (uint)Marshal.SizeOf<NET_EHOME_PUSHVOICE_OUT>() };
            if (!Isup.NET_ECMS_StartPushVoiceStream(userId, ref push, ref pushOut))
            {
                int err = Isup.NET_ECMS_GetLastError();
                CloseVoice(session);
                throw new IsupCommandException(err, $"El equipo no abrió la voz (error {err}{ErrorHint(err)}).");
            }
        }
        var linked = await Task.WhenAny(session.Linked.Task, Task.Delay(TimeSpan.FromSeconds(10), ct));
        if (linked != session.Linked.Task || session.LinkHandle < 0)
        {
            CloseVoice(session);
            throw new IsupCommandException(10, "El equipo aceptó la voz pero no se conectó al puerto de voz del servidor (¿está abierto en el firewall?).");
        }
        return session;
    }

    /// <summary>Manda una trama de audio (en la codificación del equipo). false si la sesión ya no sirve.</summary>
    public bool SendVoice(VoiceSession session, ReadOnlySpan<byte> frame)
    {
        if (session.IsClosed || session.LinkHandle < 0 || frame.Length == 0) return false;
        IntPtr p = Marshal.AllocHGlobal(frame.Length);
        try
        {
            Marshal.Copy(frame.ToArray(), 0, p, frame.Length);
            var data = new NET_EHOME_VOICETALK_DATA { pSendBuf = p, dwDataLen = (uint)frame.Length, dwTimeout = 2000 };
            return Isup.NET_ESTREAM_SendVoiceTalkData(session.LinkHandle, ref data) > 0;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    public void CloseVoice(VoiceSession session)
    {
        _voiceSessions.TryRemove(session.Session, out _);
        if (session.LinkHandle >= 0) _voiceByLink.TryRemove(session.LinkHandle, out _);
        bool wasOpen = !session.IsClosed;
        session.MarkClosed();
        if (!_started || !wasOpen) return;
        if (session.LinkHandle >= 0) Isup.NET_ESTREAM_StopVoiceTalk(session.LinkHandle);
        if (_userByDevice.ContainsKey(session.DeviceId) && session.Session >= 0) Isup.NET_ECMS_StopVoiceTalkWithStmServer(session.UserId, session.Session);
    }

    int OnVoiceNewLink(int hLink, IntPtr pInfo, IntPtr user)
    {
        try
        {
            int sessionId = Marshal.ReadInt32(pInfo, Layout.VoiceSessionId);
            if (!_voiceSessions.TryGetValue(sessionId, out var session))
            {
                // Se empareja por equipo y canal si el equipo llega con otra sesión.
                var dev = Isup.ReadCString(pInfo + Layout.VoiceDeviceId, 256);
                int chan = Marshal.ReadInt32(pInfo, Layout.VoiceAudioChannel);
                session = _voiceSessions.Values.FirstOrDefault(s => s.LinkHandle < 0 && s.DeviceId == dev && (chan <= 0 || s.Channel == chan));
            }
            if (session == null) { Emit($"Enlace de voz con sesión desconocida ({sessionId}); se rechaza."); return 0; }
            session.Codec = VoiceSession.CodecName(Marshal.ReadInt32(pInfo, Layout.VoiceEncodeType));
            session.LinkHandle = hLink;
            _voiceByLink[hLink] = session;
            var cb = new NET_EHOME_VOICETALK_DATA_CB_PARAM { fnVoiceTalkDataCB = Marshal.GetFunctionPointerForDelegate(_voiceDataCb) };
            if (!Isup.NET_ESTREAM_SetVoiceTalkDataCB(hLink, ref cb))
                Emit($"Voz de '{session.DeviceId}': no se pudo recibir su audio (error {Isup.NET_ESTREAM_GetLastError()}).");
            Emit($"Voz con '{session.DeviceId}' canal {session.Channel} abierta ({session.Codec}).");
            session.Linked.TrySetResult();
            return 1;
        }
        catch (Exception ex) { Emit("Error en el enlace de voz: " + ex.Message); return 0; }
    }

    void OnVoiceData(int hLink, IntPtr pInfo, IntPtr user)
    {
        try
        {
            if (!_voiceByLink.TryGetValue(hLink, out var session)) return;
            IntPtr data = Marshal.ReadIntPtr(pInfo, 0);
            int len = Marshal.ReadInt32(pInfo, 8);
            if (data == IntPtr.Zero || len <= 0 || len > 64 * 1024) return;
            var buf = new byte[len];
            Marshal.Copy(data, buf, 0, len);
            session.Deliver(buf);
        }
        catch (Exception ex) { Emit("Error recibiendo voz: " + ex.Message); }
    }
}
