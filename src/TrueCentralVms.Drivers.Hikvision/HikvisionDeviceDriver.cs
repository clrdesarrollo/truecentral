using System.Runtime.InteropServices;
using System.Text;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Hikvision.Interop;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Driver de gestión Hikvision por HCNetSDK: valida credenciales con un login
/// corto, obtiene modelo/serie/firmware (NET_DVR_GET_DEVICECFG_V40), enumera
/// canales analógicos e IP con su estado real (NET_DVR_GET_IPPARACFG_V40) y
/// captura snapshots JPEG. El video en vivo NO pasa por aquí: viaja por RTSP
/// hacia MediaMTX con la URL que construye <see cref="BuildRtspUrl"/>.
/// </summary>
public sealed class HikvisionDeviceDriver : IDeviceDriver
{
    public Task<DeviceProbeInfo> ProbeAsync(DeviceConnectionInfo info, CancellationToken ct = default) => Task.Run(() =>
    {
        HikvisionSdk.EnsureInitialized();

        var deviceInfo = new CHCNetSDK.NET_DVR_DEVICEINFO_V30();
        int userId = CHCNetSDK.NET_DVR_Login_V30(info.Host, info.Port, info.Username, info.Password, ref deviceInfo);
        if (userId < 0)
        {
            uint code = CHCNetSDK.NET_DVR_GetLastError();
            throw new DriverException(
                $"No se pudo iniciar sesión en {info.Host}:{info.Port}: {HikvisionException.DescribeForUser(code)} (error {code}).");
        }

        try
        {
            int analogCount = deviceInfo.byChanNum;
            int analogStart = deviceInfo.byStartChan;
            int digitalCount = deviceInfo.byIPChanNum + (deviceInfo.byHighDChanNum << 8);
            int digitalStart = deviceInfo.byStartDChan;

            string? serial = DecodeText(deviceInfo.sSerialNumber);

            // Modelo y firmware reales del equipo. Si el firmware no soporta el
            // comando se tolera: el resto de la información sigue siendo válida.
            (string? model, string? firmware) = TryGetDeviceCfg(userId);

            var ipCfg = TryGetIpParaCfg(userId);

            var channels = new List<DeviceChannelInfo>();
            for (int i = 0; i < analogCount; i++)
            {
                bool enabled = ipCfg is not { } cfgA || cfgA.byAnalogChanEnable is null ||
                               i >= cfgA.byAnalogChanEnable.Length || cfgA.byAnalogChanEnable[i] != 0;
                var (name, online) = ReadChannelName(userId, analogStart + i, $"Canal {i + 1}");
                bool available = enabled && online;
                // En las URL RTSP de Hikvision los canales analógicos usan su índice 1..N.
                channels.Add(new DeviceChannelInfo(analogStart + i, i + 1, name, available,
                    SupportsPtz: available && DetectPtz(userId, analogStart + i)));
            }
            for (int i = 0; i < digitalCount; i++)
            {
                // Para canales IP el primer byte del union es byEnable de
                // NET_DVR_IPCHANINFO: "si el canal está en línea".
                bool online = true;
                if (ipCfg is { } cfgD && cfgD.struStreamMode is not null && i < cfgD.struStreamMode.Length)
                {
                    var mode = cfgD.struStreamMode[i];
                    if (mode.byGetStreamType == 0 && mode.uGetStream.byUnion is { Length: > 0 } union)
                        online = union[0] != 0;
                }
                var (name, chanOnline) = ReadChannelName(userId, digitalStart + i, $"Cámara IP {i + 1}");
                bool available = online && chanOnline;
                // Los canales IP continúan la numeración RTSP después de los analógicos.
                channels.Add(new DeviceChannelInfo(digitalStart + i, analogCount + i + 1, name, available,
                    SupportsPtz: available && DetectPtz(userId, digitalStart + i)));
            }

            string suggested = analogCount <= 1 && digitalCount == 0 ? "Camera"
                : analogCount == 0 ? "Nvr"
                : "Dvr"; // analógico o híbrido

            // Puerto RTSP real del equipo: evita que un puerto no estándar
            // (p. ej. 1554) deje las rutas de streaming apuntando al 554.
            int? rtspPort = TryGetRtspPort(userId);

            return new DeviceProbeInfo(model, serial, firmware, suggested, analogCount, digitalCount, channels, rtspPort);
        }
        finally
        {
            CHCNetSDK.NET_DVR_Logout(userId);
        }
    }, ct);

    public string BuildRtspUrl(DeviceConnectionInfo info, int rtspPort, int rtspChannel, StreamProfile profile)
    {
        int path = rtspChannel * 100 + (profile == StreamProfile.Main ? 1 : 2);
        return $"rtsp://{Uri.EscapeDataString(info.Username)}:{Uri.EscapeDataString(info.Password)}" +
               $"@{info.Host}:{rtspPort}/Streaming/Channels/{path}";
    }

    public Task<byte[]?> CaptureSnapshotAsync(DeviceConnectionInfo info, int channelNumber, CancellationToken ct = default) => Task.Run(() =>
    {
        HikvisionSdk.EnsureInitialized();

        var deviceInfo = new CHCNetSDK.NET_DVR_DEVICEINFO_V30();
        int userId = CHCNetSDK.NET_DVR_Login_V30(info.Host, info.Port, info.Username, info.Password, ref deviceInfo);
        if (userId < 0) return (byte[]?)null;

        try
        {
            var jpegPara = new CHCNetSDK.NET_DVR_JPEGPARA
            {
                wPicQuality = 1,   // 0-mejor, 1-buena, 2-normal
                wPicSize = 0xff,   // 0xff = resolución actual del canal
            };
            byte[] buffer = new byte[2 * 1024 * 1024];
            uint returned = 0;
            if (!CHCNetSDK.NET_DVR_CaptureJPEGPicture_NEW(userId, channelNumber, ref jpegPara, buffer, (uint)buffer.Length, ref returned)
                || returned == 0)
                return null;

            byte[] jpeg = new byte[returned];
            Array.Copy(buffer, jpeg, returned);
            return jpeg;
        }
        finally
        {
            CHCNetSDK.NET_DVR_Logout(userId);
        }
    }, ct);

    /// <summary>Modelo (byDevTypeName) y versión de firmware desde NET_DVR_GET_DEVICECFG_V40; (null, null) si no está soportado.</summary>
    private static (string? Model, string? Firmware) TryGetDeviceCfg(int userId)
    {
        int size = Marshal.SizeOf<CHCNetSDK.NET_DVR_DEVICECFG_V40>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, 0, size);

            uint returned = 0;
            if (!CHCNetSDK.NET_DVR_GetDVRConfig(userId, CHCNetSDK.NET_DVR_GET_DEVICECFG_V40, -1,
                    buffer, (uint)size, ref returned))
                return (null, null);

            var cfg = Marshal.PtrToStructure<CHCNetSDK.NET_DVR_DEVICECFG_V40>(buffer);
            string? model = DecodeText(cfg.byDevTypeName);

            // dwSoftwareVersion: bits 24-31 mayor, 16-23 menor, 0-15 revisión.
            // dwSoftwareBuildDate: 0xYYYYMMDD (se muestra como los dígitos hex).
            uint v = cfg.dwSoftwareVersion;
            string? firmware = v == 0 ? null
                : $"V{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{v & 0xFFFF}" +
                  (cfg.dwSoftwareBuildDate == 0 ? "" : $" build {cfg.dwSoftwareBuildDate:X8}");
            return (model, firmware);
        }
        catch
        {
            return (null, null);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public Task<bool> PtzControlAsync(DeviceConnectionInfo info, int channelNumber, PtzCommand command, int speed, bool stop,
        CancellationToken ct = default) => Task.Run(() =>
    {
        int sdkCommand = command switch
        {
            PtzCommand.ZoomIn => CHCNetSDK.ZOOM_IN,
            PtzCommand.ZoomOut => CHCNetSDK.ZOOM_OUT,
            PtzCommand.TiltUp => CHCNetSDK.TILT_UP,
            PtzCommand.TiltDown => CHCNetSDK.TILT_DOWN,
            PtzCommand.PanLeft => CHCNetSDK.PAN_LEFT,
            PtzCommand.PanRight => CHCNetSDK.PAN_RIGHT,
            PtzCommand.UpLeft => CHCNetSDK.UP_LEFT,
            PtzCommand.UpRight => CHCNetSDK.UP_LEFT + 1,     // UP_RIGHT = 26
            PtzCommand.DownLeft => CHCNetSDK.UP_LEFT + 2,    // DOWN_LEFT = 27
            PtzCommand.DownRight => CHCNetSDK.DOWN_RIGHT,
            PtzCommand.FocusNear => CHCNetSDK.FOCUS_NEAR,
            PtzCommand.FocusFar => CHCNetSDK.FOCUS_FAR,
            PtzCommand.IrisOpen => CHCNetSDK.IRIS_OPEN,
            PtzCommand.IrisClose => CHCNetSDK.IRIS_CLOSE,
            _ => -1,
        };
        if (sdkCommand < 0) return false;
        int clampedSpeed = Math.Clamp(speed, 1, 7);

        // Sesión cacheada: el PTZ es interactivo y no tolera un login por orden.
        int userId = HikvisionSessionCache.GetOrLogin(info);
        if (CHCNetSDK.NET_DVR_PTZControlWithSpeed_Other(userId, channelNumber, sdkCommand, stop ? 1 : 0, clampedSpeed))
            return true;

        // Sesión vencida (reinicio del equipo, timeout): reintentar UNA vez con login limpio.
        HikvisionSessionCache.Invalidate(info);
        userId = HikvisionSessionCache.GetOrLogin(info);
        return CHCNetSDK.NET_DVR_PTZControlWithSpeed_Other(userId, channelNumber, sdkCommand, stop ? 1 : 0, clampedSpeed);
    }, ct);

    public Task<bool> PtzPresetAsync(DeviceConnectionInfo info, int channelNumber, PtzPresetAction action, int presetIndex,
        CancellationToken ct = default) => Task.Run(() =>
    {
        uint presetCommand = action switch
        {
            PtzPresetAction.Goto => (uint)CHCNetSDK.GOTO_PRESET,
            PtzPresetAction.Set => CHCNetSDK.SET_PRESET,
            PtzPresetAction.Clear => CHCNetSDK.CLE_PRESET,
            _ => 0,
        };
        if (presetCommand == 0) return false;

        int userId = HikvisionSessionCache.GetOrLogin(info);
        if (CHCNetSDK.NET_DVR_PTZPreset_Other(userId, channelNumber, presetCommand, (uint)presetIndex))
            return true;

        // Sesión vencida: reintentar UNA vez con login limpio.
        HikvisionSessionCache.Invalidate(info);
        userId = HikvisionSessionCache.GetOrLogin(info);
        return CHCNetSDK.NET_DVR_PTZPreset_Other(userId, channelNumber, presetCommand, (uint)presetIndex);
    }, ct);

    // ------------------------------------------------------------------
    // Reproducción remota: las grabaciones viven en el disco del equipo.
    // Los segmentos se consultan por SDK y el video viaja por la URL RTSP
    // de tracks (igual que el vivo, pero contra el almacenamiento).
    // ------------------------------------------------------------------

    public Task<IReadOnlyList<RecordingSegment>> QueryRecordingsAsync(DeviceConnectionInfo info, int channelNumber,
        DateTime localStart, DateTime localEnd, CancellationToken ct = default) => Task.Run<IReadOnlyList<RecordingSegment>>(() =>
    {
        HikvisionSdk.EnsureInitialized();

        // Sesión cacheada: la línea de tiempo se consulta de forma interactiva.
        int userId = HikvisionSessionCache.GetOrLogin(info);
        var segments = TryFindSegments(userId, channelNumber, localStart, localEnd);
        if (segments is null)
        {
            // Sesión vencida (reinicio del equipo, timeout): UNA vez con login limpio.
            HikvisionSessionCache.Invalidate(info);
            userId = HikvisionSessionCache.GetOrLogin(info);
            segments = TryFindSegments(userId, channelNumber, localStart, localEnd);
        }
        if (segments is null)
        {
            uint code = CHCNetSDK.NET_DVR_GetLastError();
            throw new DriverException(
                $"No se pudieron consultar las grabaciones: {HikvisionException.DescribeForUser(code)} (error {code}).");
        }
        return segments;
    }, ct);

    public string? BuildPlaybackUrl(DeviceConnectionInfo info, int rtspPort, int rtspChannel,
        DateTime localStart, DateTime localEnd)
    {
        // Grabación del stream principal: track = canal*100 + 1. El sufijo Z es
        // parte del formato de la URL, pero el equipo la interpreta en SU hora
        // local (la misma en la que responde la búsqueda de segmentos).
        int track = rtspChannel * 100 + 1;
        return $"rtsp://{Uri.EscapeDataString(info.Username)}:{Uri.EscapeDataString(info.Password)}" +
               $"@{info.Host}:{rtspPort}/Streaming/tracks/{track}" +
               $"?starttime={localStart:yyyyMMdd}T{localStart:HHmmss}Z&endtime={localEnd:yyyyMMdd}T{localEnd:HHmmss}Z";
    }

    /// <summary>
    /// Días del mes con grabación (marcas del calendario de Reproducción).
    ///
    /// Se resuelve con UNA búsqueda de archivos sobre el mes completo y se
    /// quedan los días distintos. Descartadas dos alternativas: preguntar día
    /// por día son 31 viajes contra el equipo (inaceptable sobre un enlace
    /// remoto), y el comando mensual del SDK
    /// (NET_DVR_GET_MONTHLY_RECORD_DISTRIBUTION) está declarado pero su
    /// cabecera no publica las estructuras. La vía ISAPI tampoco sirve como
    /// única opción: en equipos remotos tras NAT el puerto HTTP no suele estar
    /// publicado, solo el del SDK.
    /// </summary>
    public Task<IReadOnlyList<int>> QueryRecordedDaysAsync(DeviceConnectionInfo info, int channelNumber,
        int year, int month, CancellationToken ct = default) => Task.Run<IReadOnlyList<int>>(() =>
    {
        HikvisionSdk.EnsureInitialized();

        var from = new DateTime(year, month, 1);
        var to = from.AddMonths(1).AddSeconds(-1);

        int userId = HikvisionSessionCache.GetOrLogin(info);
        var segments = TryFindSegments(userId, channelNumber, from, to);
        if (segments is null)
        {
            HikvisionSessionCache.Invalidate(info);
            userId = HikvisionSessionCache.GetOrLogin(info);
            segments = TryFindSegments(userId, channelNumber, from, to);
        }
        if (segments is null)
            return [];

        // Un archivo puede cruzar la medianoche: cuentan todos los días que toca.
        var days = new SortedSet<int>();
        foreach (var segment in segments)
        {
            var day = segment.Start.Date;
            while (day <= segment.End.Date && day <= to)
            {
                if (day.Year == year && day.Month == month)
                    days.Add(day.Day);
                day = day.AddDays(1);
            }
        }
        return [.. days];
    }, ct);

    private static List<RecordingSegment>? TryFindSegments(int userId, int channel, DateTime start, DateTime end)
    {
        var cond = new CHCNetSDK.NET_DVR_FILECOND_V40
        {
            lChannel = channel,
            dwFileType = 0xff, // todos los tipos de grabación
            dwIsLocked = 0xff, // bloqueadas y normales
            sCardNumber = new byte[CHCNetSDK.CARDNUM_LEN_OUT],
            byWorkingDeviceGUID = new byte[CHCNetSDK.GUID_LEN],
            uSpecialFindInfo = new CHCNetSDK.NET_DVR_SPECIAL_FINDINFO_UNION { byLenth = new byte[8] },
            byRes2 = new byte[32],
            struStartTime = ToSdkTime(start),
            struStopTime = ToSdkTime(end),
        };
        int handle = CHCNetSDK.NET_DVR_FindFile_V40(userId, ref cond);
        if (handle < 0) return null; // sesión vencida u otro error: decide el llamador

        try
        {
            var segments = new List<RecordingSegment>();
            while (true)
            {
                var data = new CHCNetSDK.NET_DVR_FINDDATA_V40
                {
                    sFileName = "",
                    sCardNum = "",
                    byRes1 = new byte[128],
                };
                int result = CHCNetSDK.NET_DVR_FindNextFile_V40(handle, ref data);
                if (result == CHCNetSDK.NET_DVR_ISFINDING) { Thread.Sleep(30); continue; }
                if (result != CHCNetSDK.NET_DVR_FILE_SUCCESS) break; // sin (más) archivos o excepción
                var from = FromSdkTime(data.struStartTime);
                var to = FromSdkTime(data.struStopTime);
                if (from == DateTime.MinValue || to <= from) continue;
                segments.Add(new RecordingSegment(from, to, MapKind(data.byFileType)));
            }
            return segments;
        }
        finally
        {
            CHCNetSDK.NET_DVR_FindClose_V30(handle);
        }
    }

    private static CHCNetSDK.NET_DVR_TIME ToSdkTime(DateTime t) => new()
    {
        dwYear = (uint)t.Year,
        dwMonth = (uint)t.Month,
        dwDay = (uint)t.Day,
        dwHour = (uint)t.Hour,
        dwMinute = (uint)t.Minute,
        dwSecond = (uint)t.Second,
    };

    private static DateTime FromSdkTime(CHCNetSDK.NET_DVR_TIME t)
    {
        try
        {
            return new DateTime((int)t.dwYear, (int)t.dwMonth, (int)t.dwDay,
                (int)t.dwHour, (int)t.dwMinute, Math.Min(59, (int)t.dwSecond));
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue; // basura del equipo: el llamador la filtra
        }
    }

    /// <summary>byFileType del SDK → categoría para colorear la línea de tiempo.</summary>
    private static RecordingKind MapKind(byte fileType) => fileType switch
    {
        0 => RecordingKind.Continuous,
        1 or 3 or 4 => RecordingKind.Motion,
        2 or 7 or 8 or 9 or 10 or 11 or 12 => RecordingKind.Alarm,
        6 => RecordingKind.Manual,
        _ => RecordingKind.Other,
    };

    /// <summary>
    /// El canal tiene PTZ si el equipo responde a la consulta de posición PTZ
    /// (NET_DVR_GET_PTZPOS): las cámaras fijas devuelven "no soportado".
    /// </summary>
    private static bool DetectPtz(int userId, int channel)
    {
        int size = Marshal.SizeOf<CHCNetSDK.NET_DVR_PTZPOS>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            uint returned = 0;
            return CHCNetSDK.NET_DVR_GetDVRConfig(userId, CHCNetSDK.NET_DVR_GET_PTZPOS, channel,
                buffer, (uint)size, ref returned);
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Puerto RTSP configurado en el equipo (NET_DVR_GetRtspConfig); null si el firmware no soporta la consulta.</summary>
    private static int? TryGetRtspPort(int userId)
    {
        try
        {
            var cfg = new CHCNetSDK.NET_DVR_RTSPCFG();
            cfg.dwSize = (uint)Marshal.SizeOf(cfg);
            if (!CHCNetSDK.NET_DVR_GetRtspConfig(userId, 0, ref cfg, cfg.dwSize))
                return null;
            return cfg.wPort > 0 ? cfg.wPort : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Lee NET_DVR_GET_IPPARACFG_V40; null si el equipo no lo soporta (ej. DVR analógico antiguo).</summary>
    private static CHCNetSDK.NET_DVR_IPPARACFG_V40? TryGetIpParaCfg(int userId)
    {
        int size = Marshal.SizeOf<CHCNetSDK.NET_DVR_IPPARACFG_V40>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, 0, size);

            uint returned = 0;
            if (!CHCNetSDK.NET_DVR_GetDVRConfig(userId, CHCNetSDK.NET_DVR_GET_IPPARACFG_V40, 0,
                    buffer, (uint)size, ref returned))
                return null;
            return Marshal.PtrToStructure<CHCNetSDK.NET_DVR_IPPARACFG_V40>(buffer);
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Lee el nombre de un canal; si el canal no responde se marca offline con nombre genérico.</summary>
    private static (string Name, bool Online) ReadChannelName(int userId, int channel, string fallbackName)
    {
        var cfg = new CHCNetSDK.NET_DVR_PICCFG_V30();
        int size = Marshal.SizeOf(cfg);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(cfg, buffer, false);
            uint returned = 0;
            if (!CHCNetSDK.NET_DVR_GetDVRConfig(userId, CHCNetSDK.NET_DVR_GET_PICCFG_V30, channel, buffer, (uint)size, ref returned))
                return (fallbackName, false);

            cfg = Marshal.PtrToStructure<CHCNetSDK.NET_DVR_PICCFG_V30>(buffer);
            string name = DecodeName(cfg.sChanName) is { Length: > 0 } n ? n : fallbackName;
            return (name, true);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? DecodeText(byte[]? raw)
    {
        if (raw is null) return null;
        int len = Array.IndexOf(raw, (byte)0);
        if (len < 0) len = raw.Length;
        string text = Encoding.ASCII.GetString(raw, 0, len).Trim();
        return text.Length == 0 ? null : text;
    }

    private static string DecodeName(byte[]? raw)
    {
        if (raw is null) return "";
        int len = Array.IndexOf(raw, (byte)0);
        if (len < 0) len = raw.Length;
        if (len == 0) return "";

        var span = raw.AsSpan(0, len);
        try
        {
            // Los equipos con firmware reciente usan UTF-8; si aparecen
            // caracteres de reemplazo probamos Latin-1 (nombres en español).
            string utf8 = Encoding.GetEncoding("utf-8", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                .GetString(span);
            return utf8.Trim();
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(span).Trim();
        }
    }

    // ------------------------------------------------------------------
    // Reconocimiento de patentes (ANPR)
    // ------------------------------------------------------------------

    public bool SupportsAnpr => true;

    /// <summary>
    /// Abre el canal de alarma ITS del equipo. El login y el
    /// NET_DVR_SetupAlarmChan_V41 son bloqueantes: van a un hilo del pool para
    /// no frenar el reconciliador de fuentes del servidor.
    /// </summary>
    public Task<IPlateSubscription> SubscribePlatesAsync(DeviceConnectionInfo info, Action<PlateRecognition> onPlate,
        CancellationToken ct = default) =>
        Task.Run(() => HikvisionAnpr.Subscribe(info, onPlate), ct);

    // ------------------------------------------------------------------
    // Eventos de analítica y alarma (movimiento, cruce de línea, intrusión...)
    // ------------------------------------------------------------------

    public bool SupportsEvents => true;

    public Task<IDeviceEventSubscription> SubscribeEventsAsync(DeviceConnectionInfo info, Action<DeviceEvent> onEvent,
        CancellationToken ct = default) =>
        Task.Run(() => HikvisionEvents.Subscribe(info, onEvent), ct);
}

/// <summary>Fábrica del driver Hikvision (clave estable para la base de datos).</summary>
public sealed class HikvisionDeviceDriverFactory : IDeviceDriverFactory
{
    public string DriverKey => "hikvision-netsdk";
    public string DisplayName => "Hikvision (SDK nativo)";
    public DriverCapabilities Capabilities => new(
        SupportsSnapshot: true,
        SupportsDiscovery: false, // SADP se habilita en el hito M4
        DefaultSdkPort: 8000,
        DefaultRtspPort: 554,
        SupportsAnpr: true,
        SupportsEvents: true);

    public IDeviceDriver Create() => new HikvisionDeviceDriver();
}
