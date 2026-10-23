using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using TrueCentralVms.Core.Isup;
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

    readonly ConcurrentDictionary<MediaStream, byte> _pbAwaiting = new();   // reproducciones 2.0/4.0 cuyo enlace aún no llegó
    // Las reproducciones 2.0/4.0 se piden DE A UNA (en todo el servidor) hasta que llega su enlace: el equipo abre el enlace con
    // sesión 0 y sin su ID ni el canal, así que con dos pedidos en vuelo no se sabe de cuál es (probado: 4 cámaras a la vez).
    readonly SemaphoreSlim _legacyPbGate = new(1, 1);
    const int LegacyLinkWaitMs = 20_000;

    /// <summary>Saca una reproducción 2.0/4.0 de la espera de su enlace y deja pasar a la siguiente (una sola vez).</summary>
    bool EndLinkWait(MediaStream stream)
    {
        if (!_pbAwaiting.TryRemove(stream, out _)) return false;
        _legacyPbGate.Release();
        return true;
    }

    /// <summary>
    /// Archivos grabados de un canal (equipos ISUP 2.0/4.0, sin ISAPI): NET_ECMS_StartFindFile_V11 + FindNextFile_V11, como isuptest.
    /// Cada archivo es un tramo con su nombre, que es lo que se pide para reproducirlo. Bloqueante (por 4G la respuesta llega por páginas).
    /// </summary>
    public unsafe List<IsupRecordingFile> GetRecordingFiles(string deviceId, int channel, DateTime fromLocal, DateTime toLocal)
    {
        if (!_userByDevice.TryGetValue(deviceId, out int userId)) throw new IsupCommandException(-1, "El equipo no está conectado al servidor por ISUP.");
        var cond = new NET_EHOME_REC_FILE_COND
        {
            dwChannel = (uint)channel, dwRecType = 0xff,
            struStartTime = ToEhomeTime(fromLocal), struStopTime = ToEhomeTime(toLocal),
            dwStartIndex = 0, dwMaxFileCountPer = 8, byLocalOrUTC = 0,
        };
        int h = Isup.NET_ECMS_StartFindFile_V11(userId, Isup.ENUM_SEARCH_RECORD_FILE, ref cond, (uint)sizeof(NET_EHOME_REC_FILE_COND));
        if (h < 0)
        {
            int err = Isup.NET_ECMS_GetLastError();
            throw new IsupCommandException(err, $"El equipo no aceptó la búsqueda de grabaciones (error {err}{ErrorHint(err)}).");
        }
        var result = new List<IsupRecordingFile>();
        try
        {
            long deadline = Environment.TickCount64 + 60_000;
            while (Environment.TickCount64 < deadline && result.Count < 5000)
            {
                var f = new NET_EHOME_REC_FILE { dwSize = (uint)sizeof(NET_EHOME_REC_FILE) };
                int r = Isup.NET_ECMS_FindNextFile_V11(h, ref f, (uint)sizeof(NET_EHOME_REC_FILE));
                if (r == Isup.ENUM_GET_NEXT_STATUS_NEED_WAIT) { Thread.Sleep(20); continue; }
                if (r is Isup.ENUM_GET_NEXT_STATUS_NO_FILE or Isup.ENUM_GET_NEXT_STATUS_FINISH) break;
                if (r == Isup.ENUM_GET_NEXT_STATUS_NOT_SUPPORT) throw new IsupCommandException(NotSupportedError, "El equipo no admite la búsqueda de grabaciones.");
                if (r != Isup.ENUM_GET_NEXT_STATUS_SUCCESS)
                    throw new IsupCommandException(r, $"La búsqueda de grabaciones falló (estado {r}, error {Isup.NET_ECMS_GetLastError()}).");
                var name = Isup.ReadCString((IntPtr)f.sFileName, 100);
                if (name.Length > 0 && FromEhomeTime(f.struStartTime) is { } start && FromEhomeTime(f.struStopTime) is { } end && end > start)
                    result.Add(new IsupRecordingFile(start, end, f.dwFileSize, name, (int)f.dwFileMainType));
            }
        }
        finally { Isup.NET_ECMS_StopFindFile(h); }
        return result.OrderBy(x => x.Start).ToList();
    }

    static DateTime? FromEhomeTime(NET_EHOME_TIME t)
    {
        try { return new DateTime(t.wYear, t.byMonth, t.byDay, t.byHour, t.byMinute, t.bySecond); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>
    /// Reproducción de un equipo ISUP 2.0/4.0: solo por nombre de archivo (byPlayBackMode = 0) con desplazamiento en bytes. Se
    /// busca el archivo que contiene la hora pedida (o el siguiente) y se reproduce desde ahí hasta el final de ESE archivo: el
    /// equipo no avisa el final, así que el siguiente tramo se pide aparte (la línea de tiempo salta al siguiente archivo).
    /// </summary>
    unsafe MediaStream StartLegacyPlayback(string deviceId, int userId, int channel, DateTime startLocal, DateTime stopLocal)
    {
        if (!_legacyPbGate.Wait(TimeSpan.FromSeconds(60)))
            throw new IsupCommandException(-1, "Hay otras reproducciones de grabadores 2.0/4.0 iniciándose; reintente en unos segundos.");
        bool waiting = false;
        try { return StartLegacyPlaybackGated(deviceId, userId, channel, startLocal, stopLocal, out waiting); }
        finally { if (!waiting) _legacyPbGate.Release(); }
    }

    unsafe MediaStream StartLegacyPlaybackGated(string deviceId, int userId, int channel, DateTime startLocal, DateTime stopLocal, out bool waiting)
    {
        waiting = false;
        var files = GetRecordingFiles(deviceId, channel, startLocal.AddHours(-3), stopLocal);
        var file = files.FirstOrDefault(f => f.Start <= startLocal && f.End > startLocal) ?? files.FirstOrDefault(f => f.Start >= startLocal && f.Start < stopLocal)
            ?? throw new IsupCommandException(-1, $"El equipo no tiene grabación del canal {channel} desde {startLocal:yyyy-MM-dd HH:mm:ss}.");
        int offset = Math.Max(0, (int)(startLocal - file.Start).TotalSeconds);
        // A mitad del archivo: el desplazamiento por segundos (dwSeekType = 1) lo acepta el equipo pero no envía nada (probado con un
        // MDVR V4.1.2); por bytes sí. Se estima en proporción al tamaño del archivo: la hora estampada en la imagen es la real.
        double span = (file.End - file.Start).TotalSeconds;
        uint byteOffset = offset > 0 && span > 0 && file.Size > 0 ? (uint)Math.Min(uint.MaxValue, (long)(file.Size * (offset / span))) : 0;
        var stream = new MediaStream(deviceId, channel, playback: true) { UserId = userId, Legacy = true };
        var inp = new NET_EHOME_PLAYBACK_INFO_IN
        {
            dwSize = (uint)Marshal.SizeOf<NET_EHOME_PLAYBACK_INFO_IN>(),
            dwChannel = (uint)channel,
            byPlayBackMode = 0, byStreamPackage = 0, byLinkMode = 0, byLinkEncrypt = 0,
            dwSeekType = 0, dwFileOffset = byteOffset, dwFileSpan = 0, byNameStreamType = 0,
        };
        var nb = System.Text.Encoding.ASCII.GetBytes(file.Name);
        for (int i = 0; i < nb.Length && i < 99; i++) inp.szFileName[i] = nb[i];
        Isup.SetIp(ref inp.struStreamSever, AddressFor(deviceId), (ushort)_options.PlaybackPort);
        // Antes de pedirlo: el enlace puede llegar antes de que la llamada vuelva. Desde aquí la compuerta la suelta EndLinkWait
        // (al llegar el enlace, al detener, al fallar o a los 20 s).
        _pbAwaiting[stream] = 0;
        waiting = true;
        var outp = new NET_EHOME_PLAYBACK_INFO_OUT();
        if (!Isup.NET_ECMS_StartPlayBack(userId, ref inp, ref outp))
        {
            EndLinkWait(stream);
            int err = Isup.NET_ECMS_GetLastError();
            throw new IsupCommandException(err, $"El equipo rechazó la reproducción del archivo {file.Name} (error {err}{ErrorHint(err)}).");
        }
        stream.Session = outp.lSessionID;
        if (stream.Session != 0) _pbSessions[stream.Session] = stream;
        _ = Task.Delay(LegacyLinkWaitMs).ContinueWith(_ =>
        {
            if (EndLinkWait(stream)) Emit($"El grabador '{deviceId}' no abrió el enlace de la reproducción del canal {channel} en {LegacyLinkWaitMs / 1000} s.");
        }, TaskScheduler.Default);
        Emit($"Reproducción 2.0/4.0 de '{deviceId}' canal {channel}: archivo {file.Name} ({file.Start:HH:mm:ss}-{file.End:HH:mm:ss}) desde el segundo {offset} (byte {byteOffset} de {file.Size}).");
        return stream;
    }

    /// <summary>
    /// Pide al equipo la reproducción de un canal entre dos horas LOCALES del equipo (las que muestra su imagen). En ISUP 5.0 la
    /// reproducción es por hora; los equipos 2.0/4.0 reproducen por nombre de archivo (<see cref="StartLegacyPlayback"/>).
    /// </summary>
    public MediaStream StartPlayback(string deviceId, int channel, DateTime startLocal, DateTime stopLocal)
    {
        if (!_userByDevice.TryGetValue(deviceId, out int userId)) throw new IsupCommandException(-1, "El equipo no está conectado al servidor por ISUP.");
        if (_hPlayback < 0) throw new IsupCommandException(-1, "El puerto de reproducción ISUP no está abierto en el servidor.");
        if (IsLegacy(deviceId)) return StartLegacyPlayback(deviceId, userId, channel, startLocal, stopLocal);
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
        _pbSessions.TryRemove(new KeyValuePair<int, MediaStream>(stream.Session, stream));
        EndLinkWait(stream);
        if (stream.LinkHandle >= 0) _pbByLink.TryRemove(stream.LinkHandle, out _);
        stream.Close();
        if (!_started) return;
        // En 2.0/4.0 se cierra además la conexión del enlace por su handle (como el vivo).
        if (stream.Legacy && stream.LinkHandle >= 0) Isup.NET_ESTREAM_StopPlayBack(stream.LinkHandle);
        if (_userByDevice.ContainsKey(stream.DeviceId)) Isup.NET_ECMS_StopPlayBack(stream.UserId, stream.Session);
    }

    int OnPlaybackNewLink(int hLink, IntPtr pMsg, IntPtr user)
    {
        try
        {
            int session = Marshal.ReadInt32(pMsg, Layout.LinkSessionId);
            string devId = "";
            int channel = 0;
            MediaStream? stream = null;
            if (session == 0 || !_pbSessions.TryGetValue(session, out stream))
            {
                // 2.0/4.0: el enlace llega antes de que la petición devuelva y con sesión 0; se empareja por equipo y canal y, si el
                // equipo no los informa, con la única reproducción 2.0/4.0 que esté esperando su enlace.
                devId = Isup.ReadCString(pMsg + Layout.LinkDeviceId, Isup.MaxDeviceIdLen);
                channel = Marshal.ReadInt32(pMsg, Layout.LinkChannel);
                stream = _pbAwaiting.Keys.FirstOrDefault(s => s.DeviceId == devId && s.Channel == channel)
                    ?? _pbAwaiting.Keys.FirstOrDefault(s => s.DeviceId == devId);
                if (stream == null && _pbAwaiting.Count == 1) stream = _pbAwaiting.Keys.First();
            }
            if (stream == null) { Emit($"Enlace de reproducción con sesión desconocida ({session}, equipo '{devId}', canal {channel}); se rechaza."); return 0; }
            EndLinkWait(stream);
            if (stream.Legacy) Emit($"Enlace de reproducción 2.0/4.0 de '{stream.DeviceId}' canal {stream.Channel} abierto.");
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
