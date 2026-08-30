using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Dahua.Interop;

namespace TrueCentralVms.Drivers.Dahua;

/// <summary>
/// Driver de gestión Dahua por NetSDK: login de alta seguridad (valida
/// credenciales y entrega serie + canales), modelo por CLIENT_GetDeviceType,
/// firmware por CLIENT_GetSoftwareVersion y nombres de canales por
/// CLIENT_QueryChannelName. El video viaja por RTSP
/// (/cam/realmonitor?channel=N&amp;subtype=S) hacia MediaMTX; el snapshot usa
/// el CGI HTTP del equipo (autenticación digest).
/// </summary>
public sealed class DahuaDeviceDriver : IDeviceDriver
{
    public Task<DeviceProbeInfo> ProbeAsync(DeviceConnectionInfo info, CancellationToken ct = default) => Task.Run(() =>
    {
        DahuaSdk.EnsureInitialized();

        var (loginId, deviceInfo) = Login(info);
        try
        {
            string? serial = DecodeText(deviceInfo.sSerialNumber);
            int channelCount = Math.Max(deviceInfo.nChanNum, 0);

            string? model = TryGetModel(loginId);
            string? firmware = TryGetFirmware(loginId);
            string[] names = TryGetChannelNames(loginId, channelCount);

            var channels = new List<DeviceChannelInfo>(channelCount);
            for (int i = 0; i < channelCount; i++)
            {
                string name = i < names.Length && names[i].Length > 0 ? names[i] : $"Canal {i + 1}";
                // Estado real por canal requiere hardware para validar: en el
                // v1 los canales Dahua entran visibles y el administrador
                // oculta los que no use desde el panel.
                channels.Add(new DeviceChannelInfo(i + 1, i + 1, name, true));
            }

            string suggested = ClassifyDevice(model, channelCount);
            (int analog, int ip) = suggested == "Nvr" ? (0, channelCount) : (channelCount, 0);

            return new DeviceProbeInfo(model, serial, firmware, suggested, analog, ip, channels);
        }
        finally
        {
            NetSdk.CLIENT_Logout(loginId);
        }
    }, ct);

    public string BuildRtspUrl(DeviceConnectionInfo info, int rtspPort, int rtspChannel, StreamProfile profile)
    {
        int subtype = profile == StreamProfile.Main ? 0 : 1;
        return $"rtsp://{Uri.EscapeDataString(info.Username)}:{Uri.EscapeDataString(info.Password)}" +
               $"@{info.Host}:{rtspPort}/cam/realmonitor?channel={rtspChannel}&subtype={subtype}";
    }

    public async Task<byte[]?> CaptureSnapshotAsync(DeviceConnectionInfo info, int channelNumber, CancellationToken ct = default)
    {
        // CGI HTTP del equipo con autenticación digest (el SDK exige un
        // callback global para capturas; el CGI es más simple y suficiente).
        using var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(info.Username, info.Password),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            var response = await http.GetAsync(
                $"http://{info.Host}/cgi-bin/snapshot.cgi?channel={channelNumber}", ct);
            if (!response.IsSuccessStatusCode)
                return null;
            byte[] data = await response.Content.ReadAsByteArrayAsync(ct);
            return data.Length > 0 ? data : null;
        }
        catch
        {
            return null; // sin imagen: el panel muestra el marcador vacío
        }
    }

    // ------------------------------------------------------------------
    // Reproducción remota: las grabaciones viven en el disco del equipo
    // (HDD del NVR/XVR o tarjeta SD de la cámara) y se consultan por SDK.
    // ------------------------------------------------------------------

    public Task<IReadOnlyList<RecordingSegment>> QueryRecordingsAsync(DeviceConnectionInfo info, int channelNumber,
        DateTime localStart, DateTime localEnd, CancellationToken ct = default) => Task.Run<IReadOnlyList<RecordingSegment>>(() =>
    {
        DahuaSdk.EnsureInitialized();

        // Sesión cacheada: la línea de tiempo se consulta de forma interactiva.
        long loginId = DahuaSessionCache.GetOrLogin(info);
        var segments = TryFindSegments(loginId, channelNumber, localStart, localEnd, out uint error);
        if (segments is null && error != NetSdk.ErrorNoRecordFound)
        {
            // Sesión vencida (reinicio del equipo, timeout): UNA vez con login limpio.
            DahuaSessionCache.Invalidate(info);
            loginId = DahuaSessionCache.GetOrLogin(info);
            segments = TryFindSegments(loginId, channelNumber, localStart, localEnd, out error);
        }
        if (segments is null)
        {
            // "Sin resultados" no es un fallo: ese día el equipo no grabó.
            if (error == NetSdk.ErrorNoRecordFound) return [];
            throw new DriverException(
                $"No se pudieron consultar las grabaciones del equipo (error 0x{error:X8} del NetSDK).");
        }
        return segments;
    }, ct);

    /// <summary>
    /// RTSP de reproducción de Dahua. El rango viaja en la propia URL con el
    /// formato del fabricante (YYYY_MM_DD_HH_MM_SS) y en HORA LOCAL del
    /// equipo, la misma en la que responde la búsqueda de grabaciones.
    /// </summary>
    public string? BuildPlaybackUrl(DeviceConnectionInfo info, int rtspPort, int rtspChannel,
        DateTime localStart, DateTime localEnd) =>
        $"rtsp://{Uri.EscapeDataString(info.Username)}:{Uri.EscapeDataString(info.Password)}" +
        $"@{info.Host}:{rtspPort}/cam/playback?channel={rtspChannel}" +
        $"&starttime={FormatPlaybackTime(localStart)}&endtime={FormatPlaybackTime(localEnd)}";

    private static string FormatPlaybackTime(DateTime t) => t.ToString("yyyy_MM_dd_HH_mm_ss");

    /// <summary>
    /// Recorre la búsqueda del SDK. Devuelve null si el equipo rechazó la
    /// consulta (el llamador decide si reintentar con sesión nueva) y deja el
    /// código del NetSDK en <paramref name="error"/>.
    /// </summary>
    private static List<RecordingSegment>? TryFindSegments(long loginId, int channelNumber,
        DateTime start, DateTime end, out uint error)
    {
        error = 0;
        var from = ToSdkTime(start);
        var to = ToSdkTime(end);
        // Los canales del NetSDK son base 0: el canal 1 del equipo es el índice 0.
        long handle = NetSdk.CLIENT_FindFile(loginId, channelNumber - 1, NetSdk.RecordTypeAll,
            IntPtr.Zero, ref from, ref to, bTime: false, waittime: 5000);
        if (handle == 0)
        {
            error = NetSdk.CLIENT_GetLastError();
            return null;
        }

        try
        {
            var segments = new List<RecordingSegment>();
            // Tope de cortesía: un día de grabación continua son cientos de
            // archivos, nunca decenas de miles (equipo con datos corruptos).
            while (segments.Count < 20000)
            {
                var data = new NetSdk.NET_RECORDFILE_INFO { filename = "" };
                if (NetSdk.CLIENT_FindNextFile(handle, ref data) != 1)
                    break; // 0 = no hay más archivos, negativo = error
                var fileStart = FromSdkTime(data.starttime);
                var fileEnd = FromSdkTime(data.endtime);
                if (fileStart == DateTime.MinValue || fileEnd <= fileStart) continue;
                // No se filtra por bRecType: si el equipo graba también el
                // stream secundario, sus tramos coinciden con los del
                // principal y el servidor los fusiona al armar la línea.
                segments.Add(new RecordingSegment(fileStart, fileEnd, MapKind(data.nRecordFileType)));
            }
            return segments;
        }
        finally
        {
            NetSdk.CLIENT_FindClose(handle);
        }
    }

    private static NetSdk.NET_TIME ToSdkTime(DateTime t) => new()
    {
        dwYear = t.Year,
        dwMonth = t.Month,
        dwDay = t.Day,
        dwHour = t.Hour,
        dwMinute = t.Minute,
        dwSecond = t.Second,
    };

    private static DateTime FromSdkTime(NetSdk.NET_TIME t)
    {
        try
        {
            return new DateTime(t.dwYear, t.dwMonth, t.dwDay, t.dwHour, t.dwMinute, Math.Min(59, t.dwSecond));
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue; // basura del equipo: el llamador la filtra
        }
    }

    /// <summary>nRecordFileType del NetSDK → categoría para colorear la línea de tiempo.</summary>
    private static RecordingKind MapKind(byte recordFileType) => recordFileType switch
    {
        0 => RecordingKind.Continuous,
        1 => RecordingKind.Alarm,
        2 => RecordingKind.Motion,
        _ => RecordingKind.Other,
    };

    internal static (long LoginId, NetSdk.NET_DEVICEINFO_Ex Info) Login(DeviceConnectionInfo info)
    {
        var input = new NetSdk.NET_IN_LOGIN_WITH_HIGHLEVEL_SECURITY
        {
            szIP = info.Host,
            nPort = info.Port,
            szUserName = info.Username,
            szPassword = info.Password,
            emSpecCap = 0,          // TCP
            byReserved = new byte[4],
            pCapParam = IntPtr.Zero,
            emTLSCap = 0,
            szLocalIP = "",
            nClientType = 3,        // Windows
        };
        input.dwSize = (uint)Marshal.SizeOf<NetSdk.NET_IN_LOGIN_WITH_HIGHLEVEL_SECURITY>();

        var output = new NetSdk.NET_OUT_LOGIN_WITH_HIGHLEVEL_SECURITY
        {
            dwSize = (uint)Marshal.SizeOf<NetSdk.NET_OUT_LOGIN_WITH_HIGHLEVEL_SECURITY>(),
        };

        long loginId = NetSdk.CLIENT_LoginWithHighLevelSecurity(ref input, ref output);
        if (loginId == 0)
            throw new DriverException(
                $"No se pudo iniciar sesión en {info.Host}:{info.Port}: " +
                $"{DahuaSdk.DescribeLoginError(output.nError)} (error {output.nError}).");
        return (loginId, output.stuDeviceInfo);
    }

    private static string? TryGetModel(long loginId)
    {
        try
        {
            var input = new NetSdk.NET_IN_GET_DEVICETYPE_INFO
            {
                dwSize = (uint)Marshal.SizeOf<NetSdk.NET_IN_GET_DEVICETYPE_INFO>(),
            };
            var output = new NetSdk.NET_OUT_GET_DEVICETYPE_INFO
            {
                dwSize = (uint)Marshal.SizeOf<NetSdk.NET_OUT_GET_DEVICETYPE_INFO>(),
            };
            if (!NetSdk.CLIENT_GetDeviceType(loginId, ref input, ref output, 3000))
                return null;
            string model = output.szTypeEx is { Length: > 0 } ex ? ex : output.szType ?? "";
            return model.Trim() is { Length: > 0 } trimmed ? trimmed : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetFirmware(long loginId)
    {
        try
        {
            var input = new NetSdk.NET_IN_GET_SOFTWAREVERSION_INFO
            {
                dwSize = (uint)Marshal.SizeOf<NetSdk.NET_IN_GET_SOFTWAREVERSION_INFO>(),
            };
            var output = new NetSdk.NET_OUT_GET_SOFTWAREVERSION_INFO
            {
                dwSize = (uint)Marshal.SizeOf<NetSdk.NET_OUT_GET_SOFTWAREVERSION_INFO>(),
            };
            if (!NetSdk.CLIENT_GetSoftwareVersion(loginId, ref input, ref output, 3000))
                return null;
            string version = (output.szVersion ?? "").Trim();
            if (version.Length == 0)
                return null;
            var date = output.stuBuildDate;
            return date.dwYear > 0
                ? $"{version} build {date.dwYear:D4}-{date.dwMonth:D2}-{date.dwDay:D2}"
                : version;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Nombres desde el buffer de ranuras de 32 bytes; arreglo vacío si el comando no está soportado.</summary>
    private static string[] TryGetChannelNames(long loginId, int channelCount)
    {
        if (channelCount <= 0) return [];
        const int slot = 32;
        int size = slot * Math.Max(channelCount, 64);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            int returned = 0;
            if (!NetSdk.CLIENT_QueryChannelName(loginId, buffer, size, ref returned, 3000))
                return [];

            var names = new string[channelCount];
            var raw = new byte[slot];
            for (int i = 0; i < channelCount; i++)
            {
                Marshal.Copy(buffer + i * slot, raw, 0, slot);
                names[i] = DecodeText(raw) ?? "";
            }
            return names;
        }
        catch
        {
            return [];
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Clasifica por el modelo reportado (terminología Dahua: XVR = híbrido pentahíbrido).</summary>
    private static string ClassifyDevice(string? model, int channelCount)
    {
        string m = (model ?? "").ToUpperInvariant();
        if (m.Contains("NVR")) return "Nvr";
        if (m.Contains("XVR") || m.Contains("HCVR") || m.Contains("MHVR")) return "Xvr";
        if (m.Contains("IPC") || m.Contains("IP CAMERA")) return "Camera";
        if (m.Contains("DVR")) return "Dvr";
        return channelCount <= 1 ? "Camera" : "Xvr";
    }

    private static string? DecodeText(byte[]? raw)
    {
        if (raw is null) return null;
        int len = Array.IndexOf(raw, (byte)0);
        if (len < 0) len = raw.Length;
        if (len == 0) return null;

        var span = raw.AsSpan(0, len);
        try
        {
            string utf8 = Encoding.GetEncoding("utf-8", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                .GetString(span);
            return utf8.Trim() is { Length: > 0 } t ? t : null;
        }
        catch (DecoderFallbackException)
        {
            string latin = Encoding.Latin1.GetString(span).Trim();
            return latin.Length > 0 ? latin : null;
        }
    }
}

/// <summary>Fábrica del driver Dahua (clave estable para la base de datos).</summary>
public sealed class DahuaDeviceDriverFactory : IDeviceDriverFactory
{
    public string DriverKey => "dahua-netsdk";
    public string DisplayName => "Dahua (SDK nativo)";
    public DriverCapabilities Capabilities => new(
        SupportsSnapshot: true,
        SupportsDiscovery: false,
        DefaultSdkPort: 37777,
        DefaultRtspPort: 554);

    public IDeviceDriver Create() => new DahuaDeviceDriver();
}
