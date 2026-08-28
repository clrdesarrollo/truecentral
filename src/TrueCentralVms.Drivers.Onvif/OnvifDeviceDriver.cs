using System.Net;
using System.Net.Http;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Onvif;

/// <summary>
/// Driver ONVIF genérico para cámaras de otras marcas: valida credenciales con
/// GetDeviceInformation, resuelve las URL RTSP reales con GetStreamUri (los
/// dos primeros perfiles = principal y secundario) y captura por
/// GetSnapshotUri. Limitación v1: un dispositivo ONVIF = una cámara de un
/// canal (los encoders multicanal quedan para una iteración futura).
/// </summary>
public sealed class OnvifDeviceDriver : IDeviceDriver
{
    public async Task<DeviceProbeInfo> ProbeAsync(DeviceConnectionInfo info, CancellationToken ct = default)
    {
        using var client = new OnvifClient(info.Host, info.Port, info.Username, info.Password);
        await client.InitializeAsync(ct);

        var (manufacturer, model, firmware, serial) = await client.GetDeviceInformationAsync(ct);
        string fullModel = string.Join(" ", new[] { manufacturer, model }.Where(s => !string.IsNullOrEmpty(s)));

        var profiles = await client.GetProfilesAsync(ct);
        if (profiles.Count == 0)
            throw new DriverException("El dispositivo ONVIF no expone perfiles de video.");

        string? mainUrl = await client.GetStreamUriAsync(profiles[0].Token, ct);
        string? subUrl = profiles.Count > 1 ? await client.GetStreamUriAsync(profiles[1].Token, ct) : null;
        if (mainUrl is null)
            throw new DriverException("El dispositivo ONVIF no entregó la URL RTSP del stream principal.");

        var channel = new DeviceChannelInfo(
            ChannelNumber: 1,
            RtspChannel: 1,
            Name: model is { Length: > 0 } ? model : "Cámara",
            IsOnline: true,
            MainStreamUrl: mainUrl,
            SubStreamUrl: subUrl ?? mainUrl,
            // La cámara tiene PTZ si su perfil trae configuración PTZ.
            SupportsPtz: profiles[0].HasPtz);

        return new DeviceProbeInfo(
            Model: fullModel.Length > 0 ? fullModel : null,
            SerialNumber: serial,
            FirmwareVersion: firmware,
            SuggestedType: "Camera",
            AnalogChannelCount: 0,
            IpChannelCount: 1,
            Channels: [channel]);
    }

    /// <summary>
    /// Respaldo por si un canal ONVIF no tiene URL resuelta (no debería
    /// ocurrir: el probe siempre las guarda). Convención RTSP más común.
    /// </summary>
    public string BuildRtspUrl(DeviceConnectionInfo info, int rtspPort, int rtspChannel, StreamProfile profile) =>
        $"rtsp://{Uri.EscapeDataString(info.Username)}:{Uri.EscapeDataString(info.Password)}@{info.Host}:{rtspPort}/";

    /// <summary>Token del perfil PTZ por host (evita GetProfiles en cada orden interactiva).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> PtzProfileCache = new();

    /// <summary>Perfil PTZ del equipo (cacheado): el que declara PTZConfiguration, o el primero.</summary>
    private static async Task<string?> ResolvePtzProfileAsync(OnvifClient client, DeviceConnectionInfo info, CancellationToken ct)
    {
        string cacheKey = $"{info.Host}:{info.Port}";
        if (PtzProfileCache.TryGetValue(cacheKey, out string? token))
            return token;
        var profiles = await client.GetProfilesAsync(ct);
        token = (profiles.FirstOrDefault(p => p.HasPtz).Token is { Length: > 0 } withPtz
            ? withPtz
            : profiles.FirstOrDefault().Token) ?? "";
        if (token.Length == 0) return null;
        PtzProfileCache[cacheKey] = token;
        return token;
    }

    public async Task<bool> PtzControlAsync(DeviceConnectionInfo info, int channelNumber, PtzCommand command, int speed, bool stop,
        CancellationToken ct = default)
    {
        // Foco e iris no viajan por el servicio PTZ de ONVIF (van por el
        // servicio de imagen, pendiente): se reporta como no soportado.
        if (command is PtzCommand.FocusNear or PtzCommand.FocusFar or PtzCommand.IrisOpen or PtzCommand.IrisClose)
            return false;

        using var client = new OnvifClient(info.Host, info.Port, info.Username, info.Password);
        await client.InitializeAsync(ct);

        string cacheKey = $"{info.Host}:{info.Port}";
        if (await ResolvePtzProfileAsync(client, info, ct) is not { } token)
            return false;

        try
        {
            if (stop)
            {
                await client.PtzStopAsync(token, ct);
                return true;
            }

            // Velocidad 1..7 → magnitud ONVIF 0..1 (diagonales al 70 % por eje).
            double m = Math.Clamp(speed, 1, 7) / 7.0;
            double d = m * 0.7;
            (double pan, double tilt, double zoom) = command switch
            {
                PtzCommand.PanLeft => (-m, 0d, 0d),
                PtzCommand.PanRight => (m, 0d, 0d),
                PtzCommand.TiltUp => (0d, m, 0d),
                PtzCommand.TiltDown => (0d, -m, 0d),
                PtzCommand.UpLeft => (-d, d, 0d),
                PtzCommand.UpRight => (d, d, 0d),
                PtzCommand.DownLeft => (-d, -d, 0d),
                PtzCommand.DownRight => (d, -d, 0d),
                PtzCommand.ZoomIn => (0d, 0d, m),
                PtzCommand.ZoomOut => (0d, 0d, -m),
                _ => (0d, 0d, 0d),
            };
            await client.ContinuousMoveAsync(token, pan, tilt, zoom, ct);
            return true;
        }
        catch (DriverException)
        {
            PtzProfileCache.TryRemove(cacheKey, out _); // perfil obsoleto: redescubrir la próxima vez
            throw;
        }
    }

    public async Task<bool> PtzPresetAsync(DeviceConnectionInfo info, int channelNumber, PtzPresetAction action, int presetIndex,
        CancellationToken ct = default)
    {
        using var client = new OnvifClient(info.Host, info.Port, info.Username, info.Password);
        await client.InitializeAsync(ct);
        if (await ResolvePtzProfileAsync(client, info, ct) is not { } token)
            return false;

        // Convención de facto en ONVIF: tokens de preset numéricos ("1", "2"...).
        string presetToken = presetIndex.ToString();
        try
        {
            switch (action)
            {
                case PtzPresetAction.Goto:
                    await client.GotoPresetAsync(token, presetToken, ct);
                    break;
                case PtzPresetAction.Set:
                    await client.SetPresetAsync(token, presetToken, $"Preset {presetIndex}", ct);
                    break;
                case PtzPresetAction.Clear:
                    await client.RemovePresetAsync(token, presetToken, ct);
                    break;
                default:
                    return false;
            }
            return true;
        }
        catch (DriverException)
        {
            PtzProfileCache.TryRemove($"{info.Host}:{info.Port}", out _);
            throw;
        }
    }

    public async Task<byte[]?> CaptureSnapshotAsync(DeviceConnectionInfo info, int channelNumber, CancellationToken ct = default)
    {
        try
        {
            using var client = new OnvifClient(info.Host, info.Port, info.Username, info.Password);
            await client.InitializeAsync(ct);
            var tokens = await client.GetProfileTokensAsync(ct);
            if (tokens.Count == 0) return null;
            string? snapshotUrl = await client.GetSnapshotUriAsync(tokens[0], ct);
            if (snapshotUrl is null) return null;

            // La descarga del JPEG usa autenticación digest/básica HTTP.
            using var handler = new HttpClientHandler
            {
                Credentials = new NetworkCredential(info.Username, info.Password),
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var response = await http.GetAsync(snapshotUrl, ct);
            if (!response.IsSuccessStatusCode) return null;
            byte[] data = await response.Content.ReadAsByteArrayAsync(ct);
            return data.Length > 0 ? data : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Fábrica del driver ONVIF (clave estable para la base de datos).</summary>
public sealed class OnvifDeviceDriverFactory : IDeviceDriverFactory
{
    public string DriverKey => "onvif";
    public string DisplayName => "ONVIF (otras marcas)";
    public DriverCapabilities Capabilities => new(
        SupportsSnapshot: true,
        SupportsDiscovery: false,
        DefaultSdkPort: 80,
        DefaultRtspPort: 554);

    public IDeviceDriver Create() => new OnvifDeviceDriver();
}
