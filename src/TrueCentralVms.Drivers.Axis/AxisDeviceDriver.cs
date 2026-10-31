using System.Globalization;
using System.Text.Json;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Axis;

/// <summary>
/// Driver nativo de Axis por VAPIX (HTTP abierto, sin SDK). Además de lo que
/// ya da ONVIF suma lo propio de la marca: canales de multisensores y
/// encoders, el stream secundario a una resolución que el equipo admite, PTZ
/// con foco e iris y los eventos de AXIS OS (incluidas las analíticas ACAP)
/// por WebSocket. Las cámaras numeran sus fuentes de video desde 1
/// ("camera=1"); el VMS usa ese mismo número como canal.
/// </summary>
public sealed class AxisDeviceDriver : IDeviceDriver
{
    public async Task<DeviceProbeInfo> ProbeAsync(DeviceConnectionInfo info, CancellationToken ct = default)
    {
        using var client = new AxisVapixClient(info);

        var (model, serial, firmware, productType) = await ReadIdentityAsync(client, ct);

        var sources = await client.GetParamsAsync("ImageSource", ct);
        var properties = await client.GetParamsAsync("Properties", ct);
        var ptzParams = await client.GetParamsAsync("PTZ", ct);
        var rtsp = await client.GetParamsAsync("Network.RTSP", ct);

        // Fuentes I0..In: una por sensor (multisensor) o por entrada analógica (encoder).
        var indexes = sources.Keys
            .Select(k => k.Split('.'))
            .Where(p => p.Length >= 2 && p[1].Length > 1 && p[1][0] is 'I' or 'i' && int.TryParse(p[1][1..], out _))
            .Select(p => int.Parse(p[1][1..], CultureInfo.InvariantCulture))
            .Distinct().Order().ToList();
        if (indexes.Count == 0) indexes.Add(0);

        bool hasPtz = properties.GetValueOrDefault("Properties.PTZ.PTZ")?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true;
        int? rtspPort = int.TryParse(rtsp.GetValueOrDefault("Network.RTSP.Port"), out int p) && p > 0 ? p : null;
        string? subResolution = PickSubResolution(properties.GetValueOrDefault("Properties.Image.Resolution"));

        var channels = new List<DeviceChannelInfo>();
        foreach (int index in indexes)
        {
            int camera = index + 1;
            string? name = sources.GetValueOrDefault($"ImageSource.I{index}.Name");
            // Con PTZ, la marca por fuente (firmware nuevo) dice cuál de los sensores mueve; sin ella, todos.
            string? perSource = ptzParams.GetValueOrDefault($"PTZ.ImageSource.I{index}.PTZEnabled");
            bool channelPtz = hasPtz && (perSource is null || perSource.Equals("true", StringComparison.OrdinalIgnoreCase));

            channels.Add(new DeviceChannelInfo(
                ChannelNumber: camera,
                RtspChannel: camera,
                Name: name is { Length: > 0 } ? name : indexes.Count == 1 ? (model ?? "Cámara") : $"Cámara {camera}",
                IsOnline: true,
                // El principal sale de la plantilla (respeta el puerto RTSP del equipo
                // en el VMS); el secundario fija una resolución chica que la cámara admite.
                MainStreamUrl: null,
                SubStreamUrl: subResolution is null ? null
                    : $"rtsp://{info.Host}:{rtspPort ?? 554}/axis-media/media.amp?camera={camera}&resolution={subResolution}",
                SupportsPtz: channelPtz));
        }

        bool isEncoder = productType?.Contains("encoder", StringComparison.OrdinalIgnoreCase) == true;
        return new DeviceProbeInfo(
            Model: model,
            SerialNumber: serial,
            FirmwareVersion: firmware,
            SuggestedType: "Camera",
            AnalogChannelCount: isEncoder ? channels.Count : 0,
            IpChannelCount: isEncoder ? 0 : channels.Count,
            Channels: channels,
            RtspPort: rtspPort);
    }

    /// <summary>Modelo, serie, firmware y tipo de producto: basicdeviceinfo (AXIS OS 8.40+) y, si no está, param.cgi.</summary>
    private static async Task<(string? Model, string? Serial, string? Firmware, string? ProductType)> ReadIdentityAsync(
        AxisVapixClient client, CancellationToken ct)
    {
        try
        {
            string json = await client.PostJsonAsync("axis-cgi/basicdeviceinfo.cgi",
                """{"apiVersion":"1.0","context":"truecentral","method":"getAllProperties"}""", ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("propertyList", out var list))
            {
                string? Prop(string name) =>
                    list.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
                string? model = Prop("ProdFullName") ?? JoinModel(Prop("Brand"), Prop("ProdNbr"));
                return (model, Prop("SerialNumber"), Prop("Version"), Prop("ProdType"));
            }
        }
        catch (AxisNotFoundException) { }
        catch (JsonException) { }

        var brand = await client.GetParamsAsync("Brand", ct);
        var system = await client.GetParamsAsync("Properties", ct);
        return (brand.GetValueOrDefault("Brand.ProdFullName") ?? JoinModel(brand.GetValueOrDefault("Brand.Brand"), brand.GetValueOrDefault("Brand.ProdNbr")),
            system.GetValueOrDefault("Properties.System.SerialNumber"),
            system.GetValueOrDefault("Properties.Firmware.Version"),
            brand.GetValueOrDefault("Brand.ProdType"));
    }

    private static string? JoinModel(string? brand, string? number)
    {
        string joined = string.Join(" ", new[] { brand, number }.Where(s => !string.IsNullOrEmpty(s)));
        return joined.Length > 0 ? joined : null;
    }

    /// <summary>
    /// Resolución del stream secundario: la más cercana a 640 de ancho (y no
    /// menor a 480) con la proporción de la resolución máxima, de la lista que
    /// declara el equipo. Axis rechaza una resolución que no esté en su lista,
    /// por eso no se pide una fija. null = sin lista: el secundario repite el principal.
    /// </summary>
    internal static string? PickSubResolution(string? list)
    {
        if (string.IsNullOrWhiteSpace(list)) return null;
        var sizes = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Split('x'))
            .Where(p => p.Length == 2 && int.TryParse(p[0], out _) && int.TryParse(p[1], out _))
            .Select(p => (W: int.Parse(p[0], CultureInfo.InvariantCulture), H: int.Parse(p[1], CultureInfo.InvariantCulture)))
            .Where(s => s.W > 0 && s.H > 0)
            .ToList();
        if (sizes.Count == 0) return null;
        var max = sizes.MaxBy(s => s.W * s.H);
        double aspect = (double)max.W / max.H;
        var candidates = sizes.Where(s => s.W >= 480 && Math.Abs((double)s.W / s.H - aspect) < 0.03 && s.W < max.W).ToList();
        if (candidates.Count == 0) return null;
        var pick = candidates.MinBy(s => Math.Abs(s.W - 640));
        return $"{pick.W}x{pick.H}";
    }

    /// <summary>Respaldo (y principal): la fuente completa a la resolución por omisión del equipo.</summary>
    public string BuildRtspUrl(DeviceConnectionInfo info, int rtspPort, int rtspChannel, StreamProfile profile) =>
        $"rtsp://{Uri.EscapeDataString(info.Username)}:{Uri.EscapeDataString(info.Password)}@{info.Host}:{rtspPort}" +
        $"/axis-media/media.amp?camera={Math.Max(1, rtspChannel)}";

    public async Task<byte[]?> CaptureSnapshotAsync(DeviceConnectionInfo info, int channelNumber, CancellationToken ct = default)
    {
        try
        {
            using var client = new AxisVapixClient(info);
            byte[] data = await client.GetBytesAsync($"axis-cgi/jpg/image.cgi?camera={Math.Max(1, channelNumber)}", ct);
            return data.Length > 0 ? data : null;
        }
        catch (DriverException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // PTZ (ptz.cgi): velocidades −100..100 por eje.
    // ------------------------------------------------------------------

    public async Task<bool> PtzControlAsync(DeviceConnectionInfo info, int channelNumber, PtzCommand command, int speed, bool stop,
        CancellationToken ct = default)
    {
        int v = stop ? 0 : (int)Math.Round(Math.Clamp(speed, 1, 7) * 100 / 7.0);
        int d = (int)Math.Round(v * 0.7);
        string query = command switch
        {
            PtzCommand.PanLeft => $"continuouspantiltmove={-v},0",
            PtzCommand.PanRight => $"continuouspantiltmove={v},0",
            PtzCommand.TiltUp => $"continuouspantiltmove=0,{v}",
            PtzCommand.TiltDown => $"continuouspantiltmove=0,{-v}",
            PtzCommand.UpLeft => $"continuouspantiltmove={-d},{d}",
            PtzCommand.UpRight => $"continuouspantiltmove={d},{d}",
            PtzCommand.DownLeft => $"continuouspantiltmove={-d},{-d}",
            PtzCommand.DownRight => $"continuouspantiltmove={d},{-d}",
            PtzCommand.ZoomIn => $"continuouszoommove={v}",
            PtzCommand.ZoomOut => $"continuouszoommove={-v}",
            PtzCommand.FocusNear => $"continuousfocusmove={-v}",
            PtzCommand.FocusFar => $"continuousfocusmove={v}",
            PtzCommand.IrisOpen => $"continuousirismove={v}",
            PtzCommand.IrisClose => $"continuousirismove={-v}",
            _ => "",
        };
        if (query.Length == 0) return false;
        return await PtzAsync(info, channelNumber, query, ct);
    }

    public Task<bool> PtzMoveAsync(DeviceConnectionInfo info, int channelNumber, double pan, double tilt, double zoom,
        CancellationToken ct = default)
    {
        static int Scale(double x) => (int)Math.Round(Math.Clamp(x, -1, 1) * 100);
        return PtzAsync(info, channelNumber,
            $"continuouspantiltmove={Scale(pan)},{Scale(tilt)}&continuouszoommove={Scale(zoom)}", ct);
    }

    public Task<bool> PtzPresetAsync(DeviceConnectionInfo info, int channelNumber, PtzPresetAction action, int presetIndex,
        CancellationToken ct = default)
    {
        string verb = action switch
        {
            PtzPresetAction.Goto => "gotoserverpresetno",
            PtzPresetAction.Set => "setserverpresetno",
            PtzPresetAction.Clear => "removeserverpresetno",
            _ => "",
        };
        return verb.Length == 0 ? Task.FromResult(false) : PtzAsync(info, channelNumber, $"{verb}={presetIndex}", ct);
    }

    /// <summary>
    /// ptz.cgi contesta 204 si aceptó la orden; si no, 200 con "Error: ..."
    /// en el cuerpo (preset inexistente, sensor sin PTZ...).
    /// </summary>
    private static async Task<bool> PtzAsync(DeviceConnectionInfo info, int channelNumber, string query, CancellationToken ct)
    {
        using var client = new AxisVapixClient(info, TimeSpan.FromSeconds(5));
        string body = await client.GetStringAsync($"axis-cgi/com/ptz.cgi?camera={Math.Max(1, channelNumber)}&{query}", ct);
        if (body.TrimStart().StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            throw new DriverException($"La cámara Axis rechazó la orden PTZ: {body.Trim()}");
        return true;
    }

    // ------------------------------------------------------------------
    // Eventos (AXIS OS 10.11+: WebSocket de eventos).
    // ------------------------------------------------------------------

    public bool SupportsEvents => true;

    public async Task<IDeviceEventSubscription> SubscribeEventsAsync(DeviceConnectionInfo info, Action<DeviceEvent> onEvent,
        CancellationToken ct = default) =>
        await AxisEventStream.OpenAsync(info, onEvent, ct);
}

/// <summary>Fábrica del driver Axis (clave estable para la base de datos).</summary>
public sealed class AxisDeviceDriverFactory : IDeviceDriverFactory
{
    public const string Key = "axis-vapix";

    public string DriverKey => Key;
    public string DisplayName => "Axis (VAPIX)";
    public DriverCapabilities Capabilities => new(
        SupportsSnapshot: true,
        SupportsDiscovery: false,
        DefaultSdkPort: 80,
        DefaultRtspPort: 554,
        SupportsEvents: true);

    public IDeviceDriver Create() => new AxisDeviceDriver();
}
