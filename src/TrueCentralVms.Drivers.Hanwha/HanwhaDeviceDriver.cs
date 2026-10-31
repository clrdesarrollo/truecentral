using System.Globalization;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Hanwha;

/// <summary>
/// Driver nativo de Hanwha Vision (Wisenet) por SUNAPI (HTTP abierto, sin
/// SDK). Lee del equipo los perfiles de video de cada canal y elige el de
/// mayor resolución como principal y el más chico como secundario (los
/// números de perfil cambian según modelo y configuración, por eso no hay
/// plantilla fija). SUNAPI numera los canales desde 0; el VMS, desde 1.
/// </summary>
public sealed class HanwhaDeviceDriver : IDeviceDriver
{
    /// <summary>Un perfil de video de un canal (Channel.N.Profile.P.*).</summary>
    internal sealed record VideoProfile(int Channel, int Number, string Encoding, int Width, int Height)
    {
        public int Area => Width * Height;
    }

    public async Task<DeviceProbeInfo> ProbeAsync(DeviceConnectionInfo info, CancellationToken ct = default)
    {
        using var client = new SunapiClient(info);

        var device = await client.GetAsync("system.cgi", "msubmenu=deviceinfo&action=view", ct);
        string? model = device.GetValueOrDefault("Model") is { Length: > 0 } m ? m : null;
        string deviceType = device.GetValueOrDefault("DeviceType") ?? "";
        bool isRecorder = deviceType.Contains("NVR", StringComparison.OrdinalIgnoreCase) ||
                          deviceType.Contains("DVR", StringComparison.OrdinalIgnoreCase);

        var profiles = ParseProfiles(await client.GetAsync("media.cgi", "msubmenu=videoprofile&action=view", ct));

        int rtspPort = isRecorder ? 558 : 554;
        try
        {
            var rtsp = await client.GetAsync("network.cgi", "msubmenu=rtsp&action=view", ct);
            if (int.TryParse(rtsp.GetValueOrDefault("Port") ?? rtsp.GetValueOrDefault("RTSPPort"), out int port) && port > 0)
                rtspPort = port;
        }
        catch (SunapiErrorException) { }

        var channelIds = profiles.Select(p => p.Channel).Distinct().Order().ToList();
        if (channelIds.Count == 0) channelIds.Add(0);
        bool multi = isRecorder || channelIds.Count > 1;
        bool ptzModel = IsPtzModel(model);

        var channels = new List<DeviceChannelInfo>();
        foreach (int ch in channelIds)
        {
            var (main, sub) = PickProfiles(profiles.Where(p => p.Channel == ch));
            string? Url(VideoProfile? profile) => isRecorder
                ? $"rtsp://{info.Host}:{rtspPort}/LiveChannel/{ch}/media.smp"
                : profile is null ? null
                : multi ? $"rtsp://{info.Host}:{rtspPort}/{ch}/profile{profile.Number}/media.smp"
                : $"rtsp://{info.Host}:{rtspPort}/profile{profile.Number}/media.smp";

            channels.Add(new DeviceChannelInfo(
                ChannelNumber: ch + 1,
                RtspChannel: ch + 1,
                Name: !multi ? (model ?? "Cámara") : $"Cámara {ch + 1}",
                IsOnline: true,
                MainStreamUrl: Url(main),
                SubStreamUrl: Url(sub ?? main),
                // SUNAPI no tiene una consulta simple de "¿mueve?": se reconoce por el
                // modelo (XNP-, QNP-, PNP-...). Un domo que no calce se marca a mano.
                SupportsPtz: !isRecorder && ptzModel));
        }

        return new DeviceProbeInfo(
            Model: model,
            SerialNumber: device.GetValueOrDefault("SerialNumber"),
            FirmwareVersion: device.GetValueOrDefault("FirmwareVersion"),
            SuggestedType: isRecorder ? "Nvr" : "Camera",
            AnalogChannelCount: 0,
            IpChannelCount: channels.Count,
            Channels: channels,
            RtspPort: rtspPort);
    }

    /// <summary>Perfiles "Channel.N.Profile.P.EncodingType / Resolution" (texto o JSON aplanado).</summary>
    internal static List<VideoProfile> ParseProfiles(Dictionary<string, string> values)
    {
        var result = new List<VideoProfile>();
        foreach (var (key, encoding) in values)
        {
            string[] parts = key.Split('.');
            if (parts.Length != 5 || !parts[0].Equals("Channel", StringComparison.OrdinalIgnoreCase) ||
                !parts[2].Equals("Profile", StringComparison.OrdinalIgnoreCase) ||
                !parts[4].Equals("EncodingType", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(parts[1], out int ch) || !int.TryParse(parts[3], out int number))
                continue;
            string resolution = values.GetValueOrDefault($"Channel.{ch}.Profile.{number}.Resolution") ?? "";
            string[] wh = resolution.Split('x', 'X');
            int.TryParse(wh.ElementAtOrDefault(0), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w);
            int.TryParse(wh.ElementAtOrDefault(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int h);
            result.Add(new VideoProfile(ch, number, encoding, w, h));
        }
        return result;
    }

    /// <summary>
    /// Principal = H.264/H.265 de mayor resolución; secundario = el más chico
    /// de al menos 320 de ancho. MJPEG no sirve para el media server. A igual
    /// tamaño gana el número de perfil menor (el que el equipo trae de fábrica).
    /// </summary>
    internal static (VideoProfile? Main, VideoProfile? Sub) PickProfiles(IEnumerable<VideoProfile> profiles)
    {
        var usable = profiles.Where(p => p.Encoding.StartsWith("H26", StringComparison.OrdinalIgnoreCase)).ToList();
        if (usable.Count == 0) return (null, null);
        var main = usable.OrderByDescending(p => p.Area).ThenBy(p => p.Number).First();
        var sub = usable.Where(p => p.Width >= 320 && p.Area < main.Area).OrderBy(p => p.Area).ThenBy(p => p.Number).FirstOrDefault();
        return (main, sub);
    }

    /// <summary>Domos PTZ Wisenet: la segunda y tercera letra del modelo son "NP" (XNP-6400, QNP-6320...).</summary>
    internal static bool IsPtzModel(string? model) =>
        model is { Length: > 4 } && model.Length > 3 && model.AsSpan(1, 2).Equals("NP", StringComparison.OrdinalIgnoreCase) && model[3] == '-';

    /// <summary>Respaldo si el sondeo no resolvió perfiles: el perfil 2 de fábrica (H.264) en ambos.</summary>
    public string BuildRtspUrl(DeviceConnectionInfo info, int rtspPort, int rtspChannel, StreamProfile profile) =>
        $"rtsp://{Uri.EscapeDataString(info.Username)}:{Uri.EscapeDataString(info.Password)}@{info.Host}:{rtspPort}" +
        (rtspChannel > 1 ? $"/{rtspChannel - 1}/profile2/media.smp" : "/profile2/media.smp");

    public async Task<byte[]?> CaptureSnapshotAsync(DeviceConnectionInfo info, int channelNumber, CancellationToken ct = default)
    {
        try
        {
            using var client = new SunapiClient(info);
            byte[] data = await client.GetBytesAsync(
                $"stw-cgi/video.cgi?msubmenu=snapshot&action=view&Channel={Math.Max(0, channelNumber - 1)}", ct);
            return data.Length > 0 ? data : null;
        }
        catch (DriverException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // PTZ (ptzcontrol.cgi / ptzconfig.cgi): velocidades normalizadas −100..100.
    // ------------------------------------------------------------------

    public async Task<bool> PtzControlAsync(DeviceConnectionInfo info, int channelNumber, PtzCommand command, int speed, bool stop,
        CancellationToken ct = default)
    {
        if (command is PtzCommand.IrisOpen or PtzCommand.IrisClose) return false;   // SUNAPI no tiene iris continuo
        int ch = Math.Max(0, channelNumber - 1);

        if (command is PtzCommand.FocusNear or PtzCommand.FocusFar)
        {
            string focus = stop ? "Stop" : command == PtzCommand.FocusNear ? "Near" : "Far";
            await CommandAsync(info, "ptzcontrol.cgi", $"msubmenu=continuous&action=control&Channel={ch}&Focus={focus}", ct);
            return true;
        }
        if (stop)
        {
            await CommandAsync(info, "ptzcontrol.cgi", $"msubmenu=stop&action=control&Channel={ch}&OperationType=All", ct);
            return true;
        }

        int v = (int)Math.Round(Math.Clamp(speed, 1, 7) * 100 / 7.0);
        int d = (int)Math.Round(v * 0.7);
        (int pan, int tilt, int zoom) = command switch
        {
            PtzCommand.PanLeft => (-v, 0, 0),
            PtzCommand.PanRight => (v, 0, 0),
            PtzCommand.TiltUp => (0, v, 0),
            PtzCommand.TiltDown => (0, -v, 0),
            PtzCommand.UpLeft => (-d, d, 0),
            PtzCommand.UpRight => (d, d, 0),
            PtzCommand.DownLeft => (-d, -d, 0),
            PtzCommand.DownRight => (d, -d, 0),
            PtzCommand.ZoomIn => (0, 0, v),
            PtzCommand.ZoomOut => (0, 0, -v),
            _ => (0, 0, 0),
        };
        await ContinuousAsync(info, ch, pan, tilt, zoom, ct);
        return true;
    }

    public async Task<bool> PtzMoveAsync(DeviceConnectionInfo info, int channelNumber, double pan, double tilt, double zoom,
        CancellationToken ct = default)
    {
        int ch = Math.Max(0, channelNumber - 1);
        static int Scale(double x) => (int)Math.Round(Math.Clamp(x, -1, 1) * 100);
        if (pan == 0 && tilt == 0 && zoom == 0)
            await CommandAsync(info, "ptzcontrol.cgi", $"msubmenu=stop&action=control&Channel={ch}&OperationType=All", ct);
        else
            await ContinuousAsync(info, ch, Scale(pan), Scale(tilt), Scale(zoom), ct);
        return true;
    }

    private static Task ContinuousAsync(DeviceConnectionInfo info, int ch, int pan, int tilt, int zoom, CancellationToken ct) =>
        CommandAsync(info, "ptzcontrol.cgi",
            $"msubmenu=continuous&action=control&Channel={ch}&NormalizedSpeed=True&Pan={pan}&Tilt={tilt}&Zoom={zoom}", ct);

    public async Task<bool> PtzPresetAsync(DeviceConnectionInfo info, int channelNumber, PtzPresetAction action, int presetIndex,
        CancellationToken ct = default)
    {
        int ch = Math.Max(0, channelNumber - 1);
        switch (action)
        {
            case PtzPresetAction.Goto:
                await CommandAsync(info, "ptzcontrol.cgi", $"msubmenu=preset&action=control&Channel={ch}&Preset={presetIndex}", ct);
                return true;
            case PtzPresetAction.Set:
                // El nombre no admite espacios ni símbolos en varios firmware.
                await CommandAsync(info, "ptzconfig.cgi", $"msubmenu=preset&action=add&Channel={ch}&Preset={presetIndex}&Name=Preset{presetIndex}", ct);
                return true;
            case PtzPresetAction.Clear:
                await CommandAsync(info, "ptzconfig.cgi", $"msubmenu=preset&action=remove&Channel={ch}&Preset={presetIndex}", ct);
                return true;
            default:
                return false;
        }
    }

    private static async Task CommandAsync(DeviceConnectionInfo info, string cgi, string query, CancellationToken ct)
    {
        using var client = new SunapiClient(info, TimeSpan.FromSeconds(5));
        await client.GetAsync(cgi, query, ct);
    }

    // ------------------------------------------------------------------
    // Reproducción (tarjeta SD/NAS de la cámara o disco del grabador).
    // ------------------------------------------------------------------

    public Task<IReadOnlyList<RecordingSegment>> QueryRecordingsAsync(DeviceConnectionInfo info, int channelNumber,
        DateTime localStart, DateTime localEnd, CancellationToken ct = default) =>
        HanwhaRecordings.QuerySegmentsAsync(info, channelNumber, localStart, localEnd, ct);

    public Task<IReadOnlyList<int>> QueryRecordedDaysAsync(DeviceConnectionInfo info, int channelNumber,
        int year, int month, CancellationToken ct = default) =>
        HanwhaRecordings.QueryDaysAsync(info, channelNumber, year, month, ct);

    /// <summary>La URL necesita el tipo de equipo y su desfase horario: ver <see cref="ResolvePlaybackUrlAsync"/>.</summary>
    public string? BuildPlaybackUrl(DeviceConnectionInfo info, int rtspPort, int rtspChannel,
        DateTime localStart, DateTime localEnd) => null;

    public async Task<string?> ResolvePlaybackUrlAsync(DeviceConnectionInfo info, int rtspPort, int rtspChannel,
        DateTime localStart, DateTime localEnd, CancellationToken ct = default) =>
        await HanwhaRecordings.BuildPlaybackUrlAsync(info, rtspPort, rtspChannel, localStart, localEnd, ct);

    // ------------------------------------------------------------------
    // Eventos (eventstatus.cgi monitordiff).
    // ------------------------------------------------------------------

    public bool SupportsEvents => true;

    public async Task<IDeviceEventSubscription> SubscribeEventsAsync(DeviceConnectionInfo info, Action<DeviceEvent> onEvent,
        CancellationToken ct = default) =>
        await HanwhaEventStream.OpenAsync(info, onEvent, ct);
}

/// <summary>Fábrica del driver Hanwha (clave estable para la base de datos).</summary>
public sealed class HanwhaDeviceDriverFactory : IDeviceDriverFactory
{
    public const string Key = "hanwha-sunapi";

    public string DriverKey => Key;
    public string DisplayName => "Hanwha Vision / Wisenet (SUNAPI)";
    public DriverCapabilities Capabilities => new(
        SupportsSnapshot: true,
        SupportsDiscovery: false,
        DefaultSdkPort: 80,
        DefaultRtspPort: 554,
        SupportsEvents: true);

    public IDeviceDriver Create() => new HanwhaDeviceDriver();
}
