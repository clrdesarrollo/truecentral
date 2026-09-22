using System.Runtime.InteropServices;
using System.Text;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Hikvision.Interop;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Recepción de los eventos de analítica y alarma que empujan las cámaras y
/// grabadores Hikvision por el canal de alarma de HCNetSDK: detección de
/// movimiento, pérdida de video, cámara tapada, entradas de alarma
/// (<c>COMM_ALARM_V30</c>/<c>V40</c>), reglas de comportamiento —cruce de
/// línea, intrusión, entrada/salida de región, merodeo...— (<c>COMM_ALARM_RULE</c>),
/// rostros, conteo de personas y anomalías de audio/enfoque.
///
/// Las reglas se configuran EN EL EQUIPO (Evento → Analítica inteligente en
/// su web); el VMS solo escucha. Sesión de login propia por suscripción: la
/// caché de sesiones de PTZ se poda por ocio y se llevaría el canal con ella.
/// </summary>
internal static class HikvisionEvents
{
    public static IDeviceEventSubscription Subscribe(DeviceConnectionInfo info, Action<DeviceEvent> onEvent)
    {
        HikvisionSdk.EnsureInitialized();
        HikvisionAlarmChannel.EnsureInstalled();

        var deviceInfo = new CHCNetSDK.NET_DVR_DEVICEINFO_V30();
        int userId = CHCNetSDK.NET_DVR_Login_V30(info.Host, info.Port, info.Username, info.Password, ref deviceInfo);
        if (userId < 0)
        {
            uint code = CHCNetSDK.NET_DVR_GetLastError();
            throw new DriverException(
                $"No se pudo iniciar sesión en {info.Host}:{info.Port}: {HikvisionException.DescribeForUser(code)} (error {code}).");
        }

        var subscription = new Subscription(userId, info.Host);
        HikvisionAlarmChannel.Register(userId, (command, data, length) => Handle(command, data, length, subscription, onEvent));
        var param = new ItsInterop.NET_DVR_SETUPALARM_PARAM
        {
            dwSize = (uint)Marshal.SizeOf<ItsInterop.NET_DVR_SETUPALARM_PARAM>(),
            byLevel = 1,             // prioridad media
            byAlarmInfoType = 1,
            byRetAlarmTypeV40 = 0,   // alarmas de canal en el formato fijo V30 (64 canales)
            byRetDevInfoVersion = 0,
            byRes1 = new byte[2],
        };
        int handle = ItsInterop.NET_DVR_SetupAlarmChan_V41(userId, ref param);
        if (handle < 0)
        {
            uint code = CHCNetSDK.NET_DVR_GetLastError();
            HikvisionAlarmChannel.Unregister(userId);
            CHCNetSDK.NET_DVR_Logout(userId);
            throw new DriverException(
                $"El equipo {info.Host} no aceptó el canal de eventos: " +
                $"{HikvisionException.DescribeForUser(code)} (error {code}).");
        }
        subscription.AlarmHandle = handle;
        subscription.RefreshChannelMap();
        return subscription;
    }

    /// <summary>
    /// Sesión de eventos de un equipo. Guarda además el mapa "cámara IP →
    /// canal del grabador": en un DVR/NVR las alarmas inteligentes
    /// (<c>NET_VCA_DEV_INFO</c>) vienen identificadas por la IP y el canal
    /// propio de la cámara (casi siempre 1), NO por el canal del grabador
    /// (33, 34...). Sin este mapa un cruce de línea de la cámara IP D2 se
    /// atribuiría al canal analógico 1.
    /// </summary>
    private sealed class Subscription(int userId, string host) : IDeviceEventSubscription
    {
        private static readonly TimeSpan RefreshThrottle = TimeSpan.FromMinutes(1);

        private int _disposed;
        private int _refreshing;
        private DateTime _lastRefreshUtc = DateTime.MinValue;

        /// <summary>(IP de la cámara, canal en la cámara) → canal del grabador.</summary>
        private volatile Dictionary<(string Ip, int Channel), int> _byIpAndChannel = [];

        /// <summary>IP de la cámara → canal del grabador (solo cámaras con un canal en el grabador).</summary>
        private volatile Dictionary<string, int> _byIp = [];

        public int AlarmHandle { get; set; } = -1;

        public bool IsAlive => Volatile.Read(ref _disposed) == 0;

        /// <summary>
        /// Canal del grabador al que pertenece el equipo frontal de una alarma.
        /// Si el equipo frontal es el propio grabador (o una cámara sola), el
        /// canal viene tal cual; si es una cámara IP conocida se traduce; si es
        /// una cámara que el mapa aún no conoce (agregada hace poco) se pide
        /// refrescar el mapa y el evento sale sin canal (0), nunca al canal
        /// equivocado.
        /// </summary>
        public int ResolveChannel(CHCNetSDK.NET_VCA_DEV_INFO front)
        {
            string ip = front.struDevIP.sIpV4?.Trim() ?? "";
            if (ip.Length == 0 || ip == "0.0.0.0" || string.Equals(ip, host, StringComparison.OrdinalIgnoreCase))
                return front.byChannel;
            if (_byIpAndChannel.TryGetValue((ip, front.byChannel), out int channel)) return channel;
            if (_byIp.TryGetValue(ip, out channel)) return channel;
            RequestRefresh();
            return 0;
        }

        private void RequestRefresh()
        {
            if (!IsAlive || DateTime.UtcNow - _lastRefreshUtc < RefreshThrottle) return;
            if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
            // Nunca consultar al equipo desde el hilo del SDK.
            _ = Task.Run(() =>
            {
                try { if (IsAlive) RefreshChannelMap(); }
                catch { /* se reintenta con el siguiente evento */ }
                finally { Volatile.Write(ref _refreshing, 0); }
            });
        }

        /// <summary>Lee la configuración de canales IP del grabador (NET_DVR_GET_IPPARACFG_V40) y arma el mapa.</summary>
        public void RefreshChannelMap()
        {
            _lastRefreshUtc = DateTime.UtcNow;
            if (HikvisionDeviceDriver.TryGetIpParaCfg(userId) is not { } cfg) return;
            if (cfg.struStreamMode is null || cfg.struIPDevInfo is null) return;

            var byIpAndChannel = new Dictionary<(string, int), int>();
            var byIp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int start = (int)cfg.dwStartDChan;
            for (int i = 0; i < cfg.struStreamMode.Length; i++)
            {
                var mode = cfg.struStreamMode[i];
                // Solo canales que toman el flujo directo de un equipo IP:
                // NET_DVR_IPCHANINFO = byEnable, byIPID, byChannel, byIPIDHigh...
                if (mode.byGetStreamType != 0 || mode.uGetStream.byUnion is not { Length: >= 4 } u || u[0] == 0) continue;
                int ipId = u[1] | (u[3] << 8);
                if (ipId < 1 || ipId > cfg.struIPDevInfo.Length) continue;
                var dev = cfg.struIPDevInfo[ipId - 1];
                string ip = dev.struIP.sIpV4?.Trim() ?? "";
                if (ip.Length == 0) continue;
                int recorderChannel = start + i;
                byIpAndChannel[(ip, u[2])] = recorderChannel;
                if (byIp.ContainsKey(ip)) ambiguous.Add(ip);
                else byIp[ip] = recorderChannel;
            }
            foreach (string ip in ambiguous) byIp.Remove(ip);

            _byIpAndChannel = byIpAndChannel;
            _byIp = byIp;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return ValueTask.CompletedTask;
            HikvisionAlarmChannel.Unregister(userId);
            if (AlarmHandle >= 0) CHCNetSDK.NET_DVR_CloseAlarmChan_V30(AlarmHandle);
            CHCNetSDK.NET_DVR_Logout(userId);
            return ValueTask.CompletedTask;
        }
    }

    // ------------------------------------------------------------------
    // Parseo (hilo del SDK: copiar y salir)
    // ------------------------------------------------------------------

    private static void Handle(int command, IntPtr data, uint length, Subscription subscription, Action<DeviceEvent> onEvent)
    {
        switch (command)
        {
            case CHCNetSDK.COMM_ALARM_V30:
                foreach (var evt in ParseV30(data, length)) Deliver(onEvent, evt);
                break;
            case CHCNetSDK.COMM_ALARM_V40:
                foreach (var evt in ParseV40(data, length)) Deliver(onEvent, evt);
                break;
            case CHCNetSDK.COMM_ALARM_RULE:
                if (ParseRule(data, length, subscription) is { } rule) Deliver(onEvent, rule);
                break;
            case CHCNetSDK.COMM_ALARM_FACE:
                if (ParseFace(data, length, subscription) is { } face) Deliver(onEvent, face);
                break;
            case CHCNetSDK.COMM_ALARM_PDC:
                if (ParsePdc(data, length, subscription) is { } pdc) Deliver(onEvent, pdc);
                break;
            case CHCNetSDK.COMM_ALARM_AUDIOEXCEPTION:
                if (ParseAudio(data, length, subscription) is { } audio) Deliver(onEvent, audio);
                break;
            case CHCNetSDK.COMM_ALARM_DEFOCUS:
                if (ParseDefocus(data, length, subscription) is { } defocus) Deliver(onEvent, defocus);
                break;
            // Las patentes (COMM_ITS_*) van por la suscripción ANPR, no por esta.
        }
    }

    private static void Deliver(Action<DeviceEvent> onEvent, DeviceEvent evt)
    {
        try { onEvent(evt); }
        catch { /* el consumidor registra sus propios errores */ }
    }

    /// <summary>Tipo de alarma de NET_DVR_ALARMINFO_V30/V40 (dwAlarmType) → naturaleza y descripción.</summary>
    private static (VideoEventKind Kind, string Description, bool PerChannel)? ClassifyAlarmType(uint type) => type switch
    {
        0 => (VideoEventKind.AlarmInput, "Entrada de alarma activada", false),
        1 => (VideoEventKind.DeviceFault, "Disco lleno", false),
        2 => (VideoEventKind.VideoLoss, "Pérdida de señal de video", true),
        3 => (VideoEventKind.Motion, "Detección de movimiento", true),
        4 => (VideoEventKind.DeviceFault, "Disco sin formatear", false),
        5 => (VideoEventKind.DeviceFault, "Error de lectura/escritura del disco", false),
        6 => (VideoEventKind.Tamper, "Cámara tapada (sabotaje de video)", true),
        7 => (VideoEventKind.DeviceFault, "Formato de video no coincide", false),
        8 => (VideoEventKind.DeviceFault, "Acceso ilegal (credenciales rechazadas en el equipo)", false),
        9 => (VideoEventKind.VideoException, "Señal de video anómala", true),
        10 => (VideoEventKind.DeviceFault, "Falla de grabación", true),
        11 => (VideoEventKind.VideoException, "Cambio de escena", true),
        12 => (VideoEventKind.DeviceFault, "Falla del arreglo de discos", false),
        13 => (VideoEventKind.VideoException, "Resolución no coincide", true),
        15 => (VideoEventKind.VideoException, "Anomalía de calidad de video (VQD)", true),
        16 => (VideoEventKind.DeviceFault, "Cámara IP desconectada", true),
        17 => (VideoEventKind.DeviceFault, "Conflicto de dirección IP de cámara", true),
        _ => null,
    };

    private static IEnumerable<DeviceEvent> ParseV30(IntPtr data, uint length)
    {
        if (length < Marshal.SizeOf<CHCNetSDK.NET_DVR_ALARMINFO_V30>()) yield break;
        var a = Marshal.PtrToStructure<CHCNetSDK.NET_DVR_ALARMINFO_V30>(data);
        if (ClassifyAlarmType(a.dwAlarmType) is not { } info) yield break;
        var now = DateTime.Now;

        if (!info.PerChannel)
        {
            int? input = a.dwAlarmType == 0 ? (int)a.dwAlarmInputNumber + 1 : null;
            yield return new DeviceEvent(0, info.Kind,
                input is { } n ? $"{info.Description} (entrada {n})" : info.Description, now, AlarmInput: input);
            yield break;
        }

        bool any = false;
        if (a.byChannel is not null)
            for (int i = 0; i < a.byChannel.Length; i++)
            {
                if (a.byChannel[i] == 0) continue;
                any = true;
                yield return new DeviceEvent(i + 1, info.Kind, info.Description, now);
            }
        if (!any) yield return new DeviceEvent(0, info.Kind, info.Description, now);
    }

    private static IEnumerable<DeviceEvent> ParseV40(IntPtr data, uint length)
    {
        if (length < Marshal.SizeOf<CHCNetSDK.NET_DVR_ALARMINFO_V40>()) yield break;
        var a = Marshal.PtrToStructure<CHCNetSDK.NET_DVR_ALARMINFO_V40>(data);
        var header = a.struAlarmFixedHeader;
        if (ClassifyAlarmType(header.dwAlarmType) is not { } info) yield break;
        var at = ToDateTime(header.struAlarmTime) ?? DateTime.Now;

        if (!info.PerChannel)
        {
            int? input = header.dwAlarmType == 0 ? (int)header.uStruAlarm.struIOAlarm.dwAlarmInputNo + 1 : null;
            yield return new DeviceEvent(0, info.Kind,
                input is { } n ? $"{info.Description} (entrada {n})" : info.Description, at, AlarmInput: input);
            yield break;
        }

        // La parte variable trae los números de canal (uint cada uno).
        uint count = Math.Min(header.uStruAlarm.struAlarmChannel.dwAlarmChanNum, 512);
        bool any = false;
        if (a.pAlarmData != IntPtr.Zero)
            for (int i = 0; i < count; i++)
            {
                int channel = Marshal.ReadInt32(a.pAlarmData, i * 4);
                if (channel <= 0) continue;
                any = true;
                yield return new DeviceEvent(channel, info.Kind, info.Description, at);
            }
        if (!any) yield return new DeviceEvent(0, info.Kind, info.Description, at);
    }

    private static DeviceEvent? ParseRule(IntPtr data, uint length, Subscription subscription)
    {
        if (length < Marshal.SizeOf<CHCNetSDK.NET_VCA_RULE_ALARM>()) return null;
        var r = Marshal.PtrToStructure<CHCNetSDK.NET_VCA_RULE_ALARM>(data);

        // wEventTypeEx (extendido) manda; si el firmware no lo llena, se usa
        // la máscara antigua dwEventType.
        ushort ex = r.struRuleInfo.wEventTypeEx;
        uint mask = (uint)r.struRuleInfo.dwEventType;
        var kind = ex switch
        {
            1 => VideoEventKind.LineCrossing,
            2 => VideoEventKind.RegionEntrance,
            3 => VideoEventKind.RegionExit,
            4 => VideoEventKind.Intrusion,
            5 => VideoEventKind.Loitering,
            6 or 13 or 14 => VideoEventKind.ObjectLeftOrTaken,
            7 => VideoEventKind.Parking,
            8 => VideoEventKind.FastMoving,
            9 => VideoEventKind.Crowd,
            0 => mask switch
            {
                0x1 => VideoEventKind.LineCrossing,
                0x2 => VideoEventKind.RegionEntrance,
                0x4 => VideoEventKind.RegionExit,
                0x8 => VideoEventKind.Intrusion,
                0x10 => VideoEventKind.Loitering,
                0x20 => VideoEventKind.ObjectLeftOrTaken,
                0x40 => VideoEventKind.Parking,
                0x80 => VideoEventKind.FastMoving,
                0x100 => VideoEventKind.Crowd,
                _ => VideoEventKind.Other,
            },
            _ => VideoEventKind.Other,
        };
        string label = kind switch
        {
            VideoEventKind.LineCrossing => "Cruce de línea",
            VideoEventKind.RegionEntrance => "Entrada a región",
            VideoEventKind.RegionExit => "Salida de región",
            VideoEventKind.Intrusion => "Intrusión",
            VideoEventKind.Loitering => "Merodeo",
            VideoEventKind.ObjectLeftOrTaken => "Objeto abandonado o retirado",
            VideoEventKind.Parking => "Estacionamiento indebido",
            VideoEventKind.FastMoving => "Movimiento rápido",
            VideoEventKind.Crowd => "Aglomeración de personas",
            _ => $"Regla de comportamiento (tipo {(ex != 0 ? ex : mask)})",
        };
        string? rule = DecodeName(r.struRuleInfo.byRuleName);
        string description = rule is { Length: > 0 } ? $"{label} (regla '{rule}')" : label;
        // byPicTransType 1 = el equipo manda una URL en vez de la foto: no es una imagen.
        byte[]? image = r.dwPicDataLen > 0 && r.byPicTransType == 0 && r.pImage != IntPtr.Zero
            ? CopyBuffer(r.pImage, r.dwPicDataLen) : null;

        return new DeviceEvent(subscription.ResolveChannel(r.struDevInfo), kind, description,
            FromPackedTime(r.dwAbsTime) ?? DateTime.Now, RuleName: rule, Image: image);
    }

    private static DeviceEvent? ParseFace(IntPtr data, uint length, Subscription subscription)
    {
        if (length < Marshal.SizeOf<CHCNetSDK.NET_DVR_FACEDETECT_ALARM>()) return null;
        var f = Marshal.PtrToStructure<CHCNetSDK.NET_DVR_FACEDETECT_ALARM>(data);
        string? rule = DecodeName(f.byRuleName);
        return new DeviceEvent(subscription.ResolveChannel(f.struDevInfo), VideoEventKind.FaceDetection,
            rule is { Length: > 0 } ? $"Detección de rostro (regla '{rule}')" : "Detección de rostro",
            FromPackedTime(f.dwAbsTime) ?? DateTime.Now, RuleName: rule);
    }

    private static DeviceEvent? ParsePdc(IntPtr data, uint length, Subscription subscription)
    {
        if (length < Marshal.SizeOf<CHCNetSDK.NET_DVR_PDC_ALRAM_INFO>()) return null;
        var p = Marshal.PtrToStructure<CHCNetSDK.NET_DVR_PDC_ALRAM_INFO>(data);
        int channel = p.byChannel > 0 ? p.byChannel : subscription.ResolveChannel(p.struDevInfo);
        return new DeviceEvent(channel, VideoEventKind.PeopleCounting, "Conteo de personas", DateTime.Now);
    }

    private static DeviceEvent? ParseAudio(IntPtr data, uint length, Subscription subscription)
    {
        if (length < Marshal.SizeOf<CHCNetSDK.NET_DVR_AUDIOEXCEPTION_ALARM>()) return null;
        var a = Marshal.PtrToStructure<CHCNetSDK.NET_DVR_AUDIOEXCEPTION_ALARM>(data);
        string description = a.byAlarmType switch
        {
            1 => "Pérdida de audio",
            2 => "Subida brusca del audio" + (a.wAudioDecibel > 0 ? $" ({a.wAudioDecibel} dB)" : ""),
            _ => "Anomalía de audio",
        };
        return new DeviceEvent(subscription.ResolveChannel(a.struDevInfo), VideoEventKind.AudioException, description, DateTime.Now);
    }

    private static DeviceEvent? ParseDefocus(IntPtr data, uint length, Subscription subscription)
    {
        if (length < Marshal.SizeOf<CHCNetSDK.NET_DVR_DEFOCUS_ALARM>()) return null;
        var d = Marshal.PtrToStructure<CHCNetSDK.NET_DVR_DEFOCUS_ALARM>(data);
        return new DeviceEvent(subscription.ResolveChannel(d.struDevInfo), VideoEventKind.VideoException, "Cámara desenfocada", DateTime.Now);
    }

    // ------------------------------------------------------------------
    // Utilidades
    // ------------------------------------------------------------------

    /// <summary>Hora empaquetada del SDK (dwAbsTime): año desde 2000 en los 6 bits altos, luego mes, día, hora, minuto y segundo.</summary>
    private static DateTime? FromPackedTime(uint packed)
    {
        if (packed == 0) return null;
        try
        {
            return new DateTime((int)(packed >> 26) + 2000, (int)((packed >> 22) & 15), (int)((packed >> 17) & 31),
                (int)((packed >> 12) & 31), (int)((packed >> 6) & 63), (int)(packed & 63));
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static DateTime? ToDateTime(CHCNetSDK.NET_DVR_TIME_EX t)
    {
        try
        {
            if (t.wYear is < 2000 or > 2100) return null;
            return new DateTime(t.wYear, t.byMonth, t.byDay, t.byHour, t.byMinute, t.bySecond);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static byte[]? CopyBuffer(IntPtr buffer, uint length)
    {
        const uint MaxImageBytes = 8 * 1024 * 1024;
        if (buffer == IntPtr.Zero || length == 0 || length > MaxImageBytes) return null;
        byte[] data = new byte[length];
        Marshal.Copy(buffer, data, 0, (int)length);
        return data;
    }

    /// <summary>Nombre de regla del equipo (UTF-8 o Latin-1), cortado en el primer NUL.</summary>
    private static string? DecodeName(byte[]? raw)
    {
        if (raw is null) return null;
        int len = Array.IndexOf(raw, (byte)0);
        if (len < 0) len = raw.Length;
        if (len == 0) return null;
        var span = raw.AsSpan(0, len);
        try
        {
            return Encoding.GetEncoding("utf-8", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                .GetString(span).Trim();
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(span).Trim();
        }
    }
}
