using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using TrueCentralVms.IsupHost.Native;

namespace TrueCentralVms.IsupHost.Engine;

public sealed partial class IsupEngine
{
    readonly ConcurrentDictionary<int, MediaStream> _liveSessions = new();   // sesión de preview → flujo
    readonly ConcurrentDictionary<int, MediaStream> _liveByLink = new();     // handle del enlace → flujo
    readonly ConcurrentDictionary<MediaStream, byte> _liveAwaiting = new();  // pedidos cuyo enlace aún no llegó
    readonly ConcurrentDictionary<int, MediaStream> _pbSessions = new();
    readonly ConcurrentDictionary<int, MediaStream> _pbByLink = new();

    // ====================== video en vivo ======================

    /// <summary>
    /// Pide al equipo el video de un canal (0 principal, 1 secundario): el equipo se conecta al puerto de video y empieza a mandar.
    /// Lanza <see cref="IsupCommandException"/> si no está en línea o rechaza el canal.
    /// </summary>
    public MediaStream StartLive(string deviceId, int channel, int streamType)
    {
        if (!_userByDevice.TryGetValue(deviceId, out int userId)) throw new IsupCommandException(-1, "El equipo no está conectado al servidor por ISUP.");
        if (_hStream < 0) throw new IsupCommandException(-1, "El puerto de video ISUP no está abierto en el servidor.");
        var stream = new MediaStream(deviceId, channel, playback: false) { UserId = userId, Legacy = IsLegacy(deviceId) };
        var inp = new NET_EHOME_PREVIEWINFO_IN { iChannel = channel, dwStreamType = (uint)streamType, dwLinkMode = 0 };
        Isup.SetIp(ref inp.struStreamSever, AddressFor(deviceId), (ushort)_options.StreamPort);
        var outp = new NET_EHOME_PREVIEWINFO_OUT();
        _liveAwaiting[stream] = 0;   // antes de pedirlo: en 2.0/4.0 el enlace puede llegar antes de que la llamada vuelva
        if (!Isup.NET_ECMS_StartGetRealStream(userId, ref inp, ref outp))
        {
            _liveAwaiting.TryRemove(stream, out _);
            int err = Isup.NET_ECMS_GetLastError();
            throw new IsupCommandException(err, $"El equipo rechazó el video del canal {channel} (error {err}{ErrorHint(err)}).");
        }
        stream.Session = outp.lSessionID;
        _liveSessions[stream.Session] = stream;
        if (stream.Legacy) return stream;   // antes de 4.0 el equipo manda en cuanto recibe la petición

        var push = new NET_EHOME_PUSHSTREAM_IN { dwSize = (uint)Marshal.SizeOf<NET_EHOME_PUSHSTREAM_IN>(), lSessionID = outp.lSessionID };
        var pushOut = new NET_EHOME_PUSHSTREAM_OUT { dwSize = (uint)Marshal.SizeOf<NET_EHOME_PUSHSTREAM_OUT>() };
        if (!Isup.NET_ECMS_StartPushRealStream(userId, ref push, ref pushOut))
        {
            int err = Isup.NET_ECMS_GetLastError();
            StopLive(stream);
            throw new IsupCommandException(err, $"El equipo no empezó a enviar el video del canal {channel} (error {err}{ErrorHint(err)}).");
        }
        return stream;
    }

    public void StopLive(MediaStream stream)
    {
        _liveSessions.TryRemove(stream.Session, out _);
        _liveAwaiting.TryRemove(stream, out _);
        if (stream.LinkHandle >= 0) _liveByLink.TryRemove(stream.LinkHandle, out _);
        stream.Close();
        if (!_started) return;
        // En 2.0/4.0 la sesión que devuelve el SDK no es la del enlace: se cierra además la conexión por su handle.
        if (stream.Legacy && stream.LinkHandle >= 0) Isup.NET_ESTREAM_StopPreview(stream.LinkHandle);
        if (_userByDevice.ContainsKey(stream.DeviceId)) Isup.NET_ECMS_StopGetRealStream(stream.UserId, stream.Session);
    }

    int OnNewLink(int hLink, IntPtr pMsg, IntPtr user)
    {
        try
        {
            // En ISUP 5.0 el deviceId del enlace viene vacío: lo que lo identifica es la sesión.
            int session = Marshal.ReadInt32(pMsg, Layout.LinkSessionId);
            if (!_liveSessions.TryGetValue(session, out var stream))
            {
                // 2.0/4.0: el equipo puede llegar con otra sesión o antes de que la petición devuelva la suya; se empareja por equipo y canal.
                var devId = Isup.ReadCString(pMsg + Layout.LinkDeviceId, Isup.MaxDeviceIdLen);
                int channel = Marshal.ReadInt32(pMsg, Layout.LinkChannel);
                stream = _liveAwaiting.Keys.FirstOrDefault(s => s.DeviceId == devId && s.Channel == channel);
            }
            if (stream == null) { Emit($"Enlace de video con sesión desconocida ({session}); se rechaza."); return 0; }
            _liveAwaiting.TryRemove(stream, out _);
            stream.LinkHandle = hLink;
            _liveByLink[hLink] = stream;
            var cb = new NET_EHOME_PREVIEW_DATA_CB_PARAM { fnPreviewDataCB = Marshal.GetFunctionPointerForDelegate(_dataCb) };
            if (!Isup.NET_ESTREAM_SetPreviewDataCB(hLink, ref cb))
                Emit($"No se pudo recibir el video de '{stream.DeviceId}' canal {stream.Channel} (error {Isup.NET_ESTREAM_GetLastError()}).");
            return 1;
        }
        catch (Exception ex) { Emit("Error en el enlace de video: " + ex.Message); return 0; }
    }

    void OnPreviewData(int hLink, IntPtr pMsg, IntPtr user) => DeliverData(_liveByLink, hLink, pMsg);

    void DeliverData(ConcurrentDictionary<int, MediaStream> map, int hLink, IntPtr pMsg)
    {
        try
        {
            if (!map.TryGetValue(hLink, out var stream)) return;
            byte type = Marshal.ReadByte(pMsg, Layout.DataType);
            IntPtr data = Marshal.ReadIntPtr(pMsg, Layout.DataPointer);
            int len = Marshal.ReadInt32(pMsg, Layout.DataLength);
            if (data == IntPtr.Zero || len <= 0 || len > 8 * 1024 * 1024) return;
            if (type == Isup.NET_DVR_SYSHEAD) return;   // cabecera propietaria de 40 bytes ("IMKH"): no es parte del flujo PS
            var buf = new byte[len];
            Marshal.Copy(data, buf, 0, len);
            stream.Push(buf);
        }
        catch (Exception ex) { Emit("Error recibiendo video: " + ex.Message); }
    }

    // ====================== reproducción de grabaciones ======================

    /// <summary>
    /// Pide al equipo la reproducción de un canal entre dos horas LOCALES del equipo (las que muestra su imagen). En ISUP 5.0 la
    /// reproducción es por hora; los equipos 2.0/4.0 solo reproducen por nombre de archivo y no se admiten aquí.
    /// </summary>
    public MediaStream StartPlayback(string deviceId, int channel, DateTime startLocal, DateTime stopLocal)
    {
        if (!_userByDevice.TryGetValue(deviceId, out int userId)) throw new IsupCommandException(-1, "El equipo no está conectado al servidor por ISUP.");
        if (_hPlayback < 0) throw new IsupCommandException(-1, "El puerto de reproducción ISUP no está abierto en el servidor.");
        if (IsLegacy(deviceId)) throw new IsupCommandException(NotSupportedError, "El equipo usa ISUP 2.0/4.0: solo reproduce por nombre de archivo, que el VMS aún no admite.");
        var stream = new MediaStream(deviceId, channel, playback: true) { UserId = userId };
        var inp = new NET_EHOME_PLAYBACK_INFO_IN
        {
            dwSize = (uint)Marshal.SizeOf<NET_EHOME_PLAYBACK_INFO_IN>(),
            dwChannel = (uint)channel,
            byPlayBackMode = 1, byStreamPackage = 0, byLinkMode = 0, byLinkEncrypt = 0,
            struStartTime = ToEhomeTime(startLocal), struStopTime = ToEhomeTime(stopLocal),
            byLocalOrUTC = 0, byDuplicateSegment = 0,
        };
        Isup.SetIp(ref inp.struStreamSever, AddressFor(deviceId), (ushort)_options.PlaybackPort);
        var outp = new NET_EHOME_PLAYBACK_INFO_OUT();
        if (!Isup.NET_ECMS_StartPlayBack(userId, ref inp, ref outp))
        {
            int err = Isup.NET_ECMS_GetLastError();
            throw new IsupCommandException(err, $"El equipo rechazó la reproducción del canal {channel} (error {err}{ErrorHint(err)}).");
        }
        stream.Session = outp.lSessionID;
        _pbSessions[stream.Session] = stream;
        var push = new NET_EHOME_PUSHSTREAM_IN { dwSize = (uint)Marshal.SizeOf<NET_EHOME_PUSHSTREAM_IN>(), lSessionID = outp.lSessionID };
        var pushOut = new NET_EHOME_PUSHSTREAM_OUT { dwSize = (uint)Marshal.SizeOf<NET_EHOME_PUSHSTREAM_OUT>() };
        if (!Isup.NET_ECMS_StartPushPlayBack(userId, ref push, ref pushOut))
        {
            int err = Isup.NET_ECMS_GetLastError();
            StopPlayback(stream);
            throw new IsupCommandException(err, $"El equipo no empezó a enviar la reproducción (error {err}{ErrorHint(err)}).");
        }
        return stream;
    }

    public void StopPlayback(MediaStream stream)
    {
        _pbSessions.TryRemove(stream.Session, out _);
        if (stream.LinkHandle >= 0) _pbByLink.TryRemove(stream.LinkHandle, out _);
        stream.Close();
        if (_started && _userByDevice.ContainsKey(stream.DeviceId)) Isup.NET_ECMS_StopPlayBack(stream.UserId, stream.Session);
    }

    int OnPlaybackNewLink(int hLink, IntPtr pMsg, IntPtr user)
    {
        try
        {
            int session = Marshal.ReadInt32(pMsg, Layout.LinkSessionId);
            if (!_pbSessions.TryGetValue(session, out var stream)) { Emit($"Enlace de reproducción con sesión desconocida ({session}); se rechaza."); return 0; }
            stream.LinkHandle = hLink;
            _pbByLink[hLink] = stream;
            var cb = new NET_EHOME_PREVIEW_DATA_CB_PARAM { fnPreviewDataCB = Marshal.GetFunctionPointerForDelegate(_pbDataCb) };
            if (!Isup.NET_ESTREAM_SetPlayBackDataCB(hLink, ref cb))
                Emit($"No se pudo recibir la reproducción de '{stream.DeviceId}' (error {Isup.NET_ESTREAM_GetLastError()}).");
            return 1;
        }
        catch (Exception ex) { Emit("Error en el enlace de reproducción: " + ex.Message); return 0; }
    }

    void OnPlaybackData(int hLink, IntPtr pMsg, IntPtr user) => DeliverData(_pbByLink, hLink, pMsg);

    static NET_EHOME_TIME ToEhomeTime(DateTime t) => new()
    {
        wYear = (ushort)t.Year, byMonth = (byte)t.Month, byDay = (byte)t.Day,
        byHour = (byte)t.Hour, byMinute = (byte)t.Minute, bySecond = (byte)t.Second,
    };
}
