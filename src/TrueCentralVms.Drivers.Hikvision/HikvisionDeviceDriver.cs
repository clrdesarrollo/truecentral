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
        DefaultRtspPort: 554);

    public IDeviceDriver Create() => new HikvisionDeviceDriver();
}
