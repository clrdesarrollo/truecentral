using System.Globalization;
using System.Text;
using System.Xml.Linq;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Core.Isup;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Cámaras, DVR, NVR y cámaras LPR Hikvision conectadas por ISUP: el equipo se registra contra el servidor y todo pasa por esa
/// conexión. La gestión es ISAPI por el túnel (datos, canales, foto, grabaciones, reglas de analítica), el PTZ va con las órdenes
/// propias de ISUP y el video lo entrega el servicio ISUP como MPEG-PS en una URL local que MediaMTX publica con FFmpeg.
///
/// El número de canal es el de ISAPI (en un DVR las cámaras IP siguen a las analógicas; en el de pruebas de isuptest, desde la 33),
/// que es el mismo que usa ISUP para pedir el video: por eso aquí <see cref="DeviceChannelInfo.ChannelNumber"/> y
/// <see cref="DeviceChannelInfo.RtspChannel"/> coinciden.
/// </summary>
public sealed class HikvisionIsupDeviceDriver : IDeviceDriver
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Slow = TimeSpan.FromSeconds(30);

    private static string IdOf(DeviceConnectionInfo info) =>
        info.IsupId is { Length: > 0 } id ? id : throw new DriverException("El equipo no tiene ID ISUP.");

    /// <summary>El transporte, con el equipo conectado; si no, un error que dice qué pasa.</summary>
    private static IIsupGateway Online(string id)
    {
        var gateway = IsupGateway.Require();
        if (!gateway.IsOnline(id))
            throw new DriverException($"El equipo ISUP «{id}» no está conectado al servidor en este momento.");
        return gateway;
    }

    private static async Task<byte[]?> IsapiAsync(string id, string method, string path, string? body, TimeSpan timeout, CancellationToken ct)
    {
        var response = await Online(id).IsapiAsync(id, method, path, body is null ? null : Encoding.UTF8.GetBytes(body), timeout, ct);
        return response.Ok ? response.Body : null;
    }

    /// <summary>Respuesta XML de una petición ISAPI; null si el equipo no la soporta o contestó un error (ResponseStatus).</summary>
    private static async Task<XElement?> XmlAsync(string id, string path, CancellationToken ct, string method = "GET", string? body = null,
        TimeSpan? timeout = null)
    {
        var bytes = await IsapiAsync(id, method, path, body, timeout ?? Quick, ct);
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            var root = XDocument.Parse(HikvisionIsapiClient.DecodeBody(bytes)).Root;
            return root is null || root.Name.LocalName == "ResponseStatus" ? null : root;
        }
        catch (System.Xml.XmlException) { return null; }
    }

    private static string? Text(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;

    private static IEnumerable<XElement> All(XElement? root, string name) =>
        root?.DescendantsAndSelf().Where(e => e.Name.LocalName == name) ?? [];

    // ------------------------------------------------------------------
    // Datos del equipo y canales
    // ------------------------------------------------------------------

    public async Task<DeviceProbeInfo> ProbeAsync(DeviceConnectionInfo info, CancellationToken ct = default)
    {
        string id = IdOf(info);
        // ISUP 2.0/4.0 no tiene ISAPI: datos y canales por la configuración del SDK.
        if (Online(id).GetState(id) is { Protocol: { } protocol } && !protocol.StartsWith('5'))
            return await ProbeLegacyAsync(id, ct);
        var device = await XmlAsync(id, "/ISAPI/System/deviceInfo", ct);
        // Sin ISAPI (un 2.0/4.0 cuyo protocolo aún no se conocía, o un firmware que no lo pasa por el túnel): por el SDK.
        if (device is null)
        {
            try { return await ProbeLegacyAsync(id, ct); }
            catch (DriverException ex)
            {
                throw new DriverException($"El equipo ISUP «{id}» no respondió a la consulta de sus datos (ISAPI deviceInfo ni SDK: {ex.Message}).");
            }
        }
        string? firmware = Text(device, "firmwareVersion");
        if (firmware is not null && Text(device, "firmwareReleasedDate") is { } built) firmware += " " + built;

        var channels = new List<DeviceChannelInfo>();
        int analog = 0, ip = 0;

        // Entradas propias: las analógicas de un DVR, o el sensor de una cámara.
        foreach (var input in All(await XmlAsync(id, "/ISAPI/System/Video/inputs/channels", ct), "VideoInputChannel"))
        {
            if (!int.TryParse(Text(input, "id"), out int n) || channels.Any(c => c.ChannelNumber == n)) continue;
            // Un DVR informa "NO VIDEO" en las entradas sin cámara; las cámaras ni siquiera traen resolución.
            string res = Text(input, "resDesc") ?? "";
            bool signal = !res.Contains("NO VIDEO", StringComparison.OrdinalIgnoreCase) &&
                          !string.Equals(Text(input, "videoInputEnabled"), "false", StringComparison.OrdinalIgnoreCase);
            channels.Add(new DeviceChannelInfo(n, n, Text(input, "name") ?? $"Canal {n}", signal));
            analog++;
        }

        // Cámaras IP de un NVR (o de un DVR híbrido), con su estado en línea.
        var online = new Dictionary<int, bool>();
        foreach (var status in All(await XmlAsync(id, "/ISAPI/ContentMgmt/InputProxy/channels/status", ct), "InputProxyChannelStatus"))
            if (int.TryParse(Text(status, "id"), out int n))
                online[n] = string.Equals(Text(status, "online"), "true", StringComparison.OrdinalIgnoreCase);
        foreach (var proxy in All(await XmlAsync(id, "/ISAPI/ContentMgmt/InputProxy/channels", ct), "InputProxyChannel"))
        {
            if (!int.TryParse(Text(proxy, "id"), out int n) || channels.Any(c => c.ChannelNumber == n)) continue;
            // Sin estado legible se asume en línea: es una cámara configurada.
            channels.Add(new DeviceChannelInfo(n, n, Text(proxy, "name") ?? $"Cámara IP {n}", !online.TryGetValue(n, out var up) || up));
            ip++;
        }

        // Equipos que no informan sus entradas (algunas cámaras LPR): sus canales de streaming (101, 201…) dicen cuántas tienen.
        if (channels.Count == 0)
            foreach (var stream in All(await XmlAsync(id, "/ISAPI/Streaming/channels", ct), "StreamingChannel"))
                if (int.TryParse(Text(stream, "id"), out int sid) && sid / 100 is int n and > 0 && channels.All(c => c.ChannelNumber != n))
                {
                    channels.Add(new DeviceChannelInfo(n, n, Text(stream, "channelName") ?? $"Canal {n}", true));
                    analog++;
                }
        if (channels.Count == 0)
        {
            channels.Add(new DeviceChannelInfo(1, 1, "Canal 1", true));
            analog = 1;
        }

        // PTZ: la cámara lo informa en sus capacidades PTZ. En las entradas analógicas de un DVR todas traen el PTZ por RS-485 o
        // coaxial configurado, sea domo o no (lo mismo que con el SDK): ahí lo marca el administrador.
        bool dvrAnalog = analog > 1;
        for (int i = 0; i < channels.Count && i < 32; i++)
        {
            var ch = channels[i];
            if (!ch.IsOnline || (dvrAnalog && i < analog)) continue;
            var caps = await XmlAsync(id, $"/ISAPI/PTZCtrl/channels/{ch.ChannelNumber}/capabilities", ct, timeout: TimeSpan.FromSeconds(6));
            if (All(caps, "ContinuousPanTiltSpace").Any() || All(caps, "ContinuousZoomSpace").Any())
                channels[i] = ch with { SupportsPtz = true };
        }

        string suggested = analog <= 1 && ip == 0 ? "Camera" : analog == 0 ? "Nvr" : "Dvr";
        return new DeviceProbeInfo(Text(device, "model"), Text(device, "serialNumber"), firmware, suggested, analog, ip,
            channels.OrderBy(c => c.ChannelNumber).ToList());
    }

    /// <summary>
    /// Equipo ISUP 2.0/4.0 (grabadores móviles, por ejemplo): sin ISAPI, el SDK solo informa cuántos canales tiene. Las entradas
    /// analógicas parten en su canal inicial y las cámaras IP en el 33, como en los grabadores Hikvision (el DVR de pruebas de
    /// isuptest); el equipo no informa nombres ni qué entradas tienen señal.
    /// </summary>
    private static async Task<DeviceProbeInfo> ProbeLegacyAsync(string id, CancellationToken ct)
    {
        var d = await IsupGateway.Require().GetDeviceInfoAsync(id, ct);
        int analog = Math.Max(0, d.AnalogChannels);
        int ip = Math.Max(0, d.TotalChannels - analog);
        int start = d.StartChannel > 0 ? d.StartChannel : 1;
        var channels = new List<DeviceChannelInfo>();
        for (int i = 0; i < analog; i++) channels.Add(new DeviceChannelInfo(start + i, start + i, $"Canal {i + 1}", true));
        int ipStart = Math.Max(33, start + analog);
        for (int i = 0; i < ip; i++) channels.Add(new DeviceChannelInfo(ipStart + i, ipStart + i, $"Cámara IP {i + 1}", true));
        if (channels.Count == 0) channels.Add(new DeviceChannelInfo(1, 1, "Canal 1", true));
        string kind = d.DeviceType switch { 30 => "Cámara IP", 40 => "Domo IP", 3 => "DVS", 1 => "DVR", _ => $"Equipo tipo {d.DeviceType}" };
        string suggested = d.DeviceType is 30 or 40 || (analog <= 1 && ip == 0) ? "Camera" : analog == 0 ? "Nvr" : "Dvr";
        return new DeviceProbeInfo($"{kind} (ISUP 2.0/4.0)", d.Serial, d.Firmware, suggested, analog, ip, channels);
    }

    /// <summary>
    /// La "URL RTSP" de un canal ISUP es la URL local del servicio ISUP que entrega su video en MPEG-PS: MediaMTX la reconoce
    /// (<see cref="IsupGateway.IsMediaSource"/>) y la publica con FFmpeg.
    /// </summary>
    public string BuildRtspUrl(DeviceConnectionInfo info, int rtspPort, int rtspChannel, StreamProfile profile) =>
        IsupGateway.Require().LiveSourceUrl(IdOf(info), rtspChannel, profile == StreamProfile.Main ? 0 : 1);

    /// <summary>
    /// Foto ISAPI del canal; los equipos que no la entregan por ISAPI (terminales de acceso, verificado con el DS-K1T323MBWX:
    /// <c>notSupport</c>) dan un cuadro de su video en vivo.
    /// </summary>
    public async Task<byte[]?> CaptureSnapshotAsync(DeviceConnectionInfo info, int channelNumber, CancellationToken ct = default)
    {
        string id = IdOf(info);
        if (IsupGateway.Current is not { } gateway || !gateway.IsOnline(id)) return null;
        var jpeg = await IsapiAsync(id, "GET", $"/ISAPI/Streaming/channels/{channelNumber * 100 + 1}/picture", null, Slow, ct);
        return jpeg is { Length: > 3 } && jpeg[0] == 0xFF && jpeg[1] == 0xD8 ? jpeg
            : await gateway.GrabFrameAsync(id, channelNumber, 0, ct);
    }

    // ------------------------------------------------------------------
    // PTZ (órdenes propias de ISUP, como hace isuptest)
    // ------------------------------------------------------------------

    public async Task<bool> PtzControlAsync(DeviceConnectionInfo info, int channelNumber, PtzCommand command, int speed, bool stop,
        CancellationToken ct = default)
    {
        IsupPtzCommand? isupCommand = command switch
        {
            PtzCommand.TiltUp => IsupPtzCommand.Up,
            PtzCommand.TiltDown => IsupPtzCommand.Down,
            PtzCommand.PanLeft => IsupPtzCommand.Left,
            PtzCommand.PanRight => IsupPtzCommand.Right,
            PtzCommand.UpLeft => IsupPtzCommand.UpLeft,
            PtzCommand.UpRight => IsupPtzCommand.UpRight,
            PtzCommand.DownLeft => IsupPtzCommand.DownLeft,
            PtzCommand.DownRight => IsupPtzCommand.DownRight,
            PtzCommand.ZoomIn => IsupPtzCommand.ZoomIn,
            PtzCommand.ZoomOut => IsupPtzCommand.ZoomOut,
            PtzCommand.FocusNear => IsupPtzCommand.FocusNear,
            PtzCommand.FocusFar => IsupPtzCommand.FocusFar,
            PtzCommand.IrisOpen => IsupPtzCommand.IrisOpen,
            PtzCommand.IrisClose => IsupPtzCommand.IrisClose,
            _ => null,
        };
        if (isupCommand is not { } cmd) return false;
        string id = IdOf(info);
        await Online(id).PtzAsync(id, channelNumber, cmd, start: !stop, Math.Clamp(speed, 1, 7), ct);
        return true;
    }

    public async Task<bool> PtzPresetAsync(DeviceConnectionInfo info, int channelNumber, PtzPresetAction action, int presetIndex,
        CancellationToken ct = default)
    {
        var isupAction = action switch
        {
            PtzPresetAction.Set => IsupPresetAction.Set,
            PtzPresetAction.Clear => IsupPresetAction.Clear,
            _ => IsupPresetAction.Goto,
        };
        string id = IdOf(info);
        await Online(id).PresetAsync(id, channelNumber, isupAction, presetIndex, ct);
        return true;
    }

    // ------------------------------------------------------------------
    // Grabaciones: búsqueda ISAPI por el túnel; el video, del servicio ISUP
    // ------------------------------------------------------------------

    /// <summary>Las búsquedas de ISAPI viajan con "Z", pero el equipo las interpreta (y responde) en su hora local.</summary>
    private static string IsapiTime(DateTime local) => local.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Hora de una respuesta ISAPI tal como la marca el reloj del equipo (con "Z" o con su desfase, da igual).</summary>
    private static DateTime? DeviceTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.DateTime : null;

    private static void RequireIsapiRecordings(string id)
    {
        if (IsupGateway.Require().GetState(id) is { Protocol: { } protocol } && !protocol.StartsWith('5'))
            throw new DriverException("El equipo se conecta por ISUP 2.0/4.0: buscar y reproducir sus grabaciones requiere ISUP 5.0.");
    }

    public async Task<IReadOnlyList<RecordingSegment>> QueryRecordingsAsync(DeviceConnectionInfo info, int channelNumber,
        DateTime localStart, DateTime localEnd, CancellationToken ct = default)
    {
        string id = IdOf(info);
        Online(id);
        RequireIsapiRecordings(id);
        var result = new List<RecordingSegment>();
        string searchId = Guid.NewGuid().ToString().ToUpperInvariant();
        const int page = 50;
        int position = 0;
        for (int guard = 0; guard < 40; guard++)
        {
            string body = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <CMSearchDescription>
                <searchID>{searchId}</searchID>
                <trackList><trackID>{channelNumber * 100 + 1}</trackID></trackList>
                <timeSpanList><timeSpan><startTime>{IsapiTime(localStart)}</startTime><endTime>{IsapiTime(localEnd)}</endTime></timeSpan></timeSpanList>
                <maxResults>{page}</maxResults>
                <searchResultPostion>{position}</searchResultPostion>
                <metadataList><metadataDescriptor>//recordType.meta.std-cgi.com</metadataDescriptor></metadataList>
                </CMSearchDescription>
                """;
            var root = await XmlAsync(id, "/ISAPI/ContentMgmt/search", ct, "POST", body, Slow);
            if (root is null)
            {
                if (guard == 0)
                    throw new DriverException("El equipo no respondió la búsqueda de grabaciones (ISAPI ContentMgmt/search).");
                break;
            }
            int count = 0;
            foreach (var item in All(root, "searchMatchItem"))
            {
                count++;
                var span = item.Elements().FirstOrDefault(e => e.Name.LocalName == "timeSpan");
                if (DeviceTime(Text(span, "startTime")) is not { } from || DeviceTime(Text(span, "endTime")) is not { } to || to <= from)
                    continue;
                string meta = All(item, "metadataDescriptor").Select(e => e.Value).FirstOrDefault() ?? "";
                result.Add(new RecordingSegment(from, to, KindOf(meta)));
            }
            // El equipo avisa con "MORE" que quedan resultados.
            if (count == 0 || !string.Equals(Text(root, "responseStatusStrg"), "MORE", StringComparison.OrdinalIgnoreCase)) break;
            position += count;
        }
        return result.OrderBy(s => s.Start).ToList();
    }

    /// <summary>"recordType.meta.hikvision.com/timing" → continua; motion, alarm, manual… → su categoría de la línea de tiempo.</summary>
    private static RecordingKind KindOf(string metadata)
    {
        string type = metadata[(metadata.LastIndexOf('/') + 1)..].ToLowerInvariant();
        return type switch
        {
            "timing" or "continuous" or "allday" => RecordingKind.Continuous,
            "motion" or "vmd" => RecordingKind.Motion,
            "manual" => RecordingKind.Manual,
            "" => RecordingKind.Other,
            _ when type.Contains("alarm") || type.Contains("smart") || type.Contains("vca") || type.Contains("detection") ||
                   type.Contains("event") => RecordingKind.Alarm,
            _ => RecordingKind.Other,
        };
    }

    public async Task<IReadOnlyList<int>> QueryRecordedDaysAsync(DeviceConnectionInfo info, int channelNumber, int year, int month,
        CancellationToken ct = default)
    {
        string id = IdOf(info);
        try
        {
            Online(id);
            RequireIsapiRecordings(id);
            // Una consulta por mes (lo que usa el propio calendario del equipo).
            string body = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><trackDailyParam><year>{year}</year><monthOfYear>{month}</monthOfYear></trackDailyParam>";
            var root = await XmlAsync(id, $"/ISAPI/ContentMgmt/record/tracks/{channelNumber * 100 + 1}/dailyDistribution", ct, "POST", body, Slow);
            if (root is not null)
                return All(root, "day")
                    .Where(d => string.Equals(Text(d, "record"), "true", StringComparison.OrdinalIgnoreCase))
                    .Select(d => int.TryParse(Text(d, "dayOfMonth"), out int n) ? n : 0)
                    .Where(n => n is >= 1 and <= 31).Distinct().Order().ToList();

            // Sin calendario: una búsqueda del mes completo y los días que tocan sus tramos.
            var from = new DateTime(year, month, 1);
            var to = from.AddMonths(1).AddSeconds(-1);
            var days = new SortedSet<int>();
            foreach (var segment in await QueryRecordingsAsync(info, channelNumber, from, to, ct))
                for (var day = segment.Start.Date; day <= segment.End.Date && day <= to; day = day.AddDays(1))
                    if (day.Year == year && day.Month == month) days.Add(day.Day);
            return [.. days];
        }
        catch (DriverException)
        {
            return [];
        }
    }

    public string? BuildPlaybackUrl(DeviceConnectionInfo info, int rtspPort, int rtspChannel, DateTime localStart, DateTime localEnd) =>
        IsupGateway.Require().PlaybackSourceUrl(IdOf(info), rtspChannel, localStart, localEnd);

    public Task<string?> ResolvePlaybackUrlAsync(DeviceConnectionInfo info, int rtspPort, int rtspChannel,
        DateTime localStart, DateTime localEnd, CancellationToken ct = default)
    {
        string id = IdOf(info);
        Online(id);
        RequireIsapiRecordings(id);
        return Task.FromResult(BuildPlaybackUrl(info, rtspPort, rtspChannel, localStart, localEnd));
    }

    // ------------------------------------------------------------------
    // Patentes y eventos: llegan por el canal de alarmas de ISUP
    // ------------------------------------------------------------------

    public bool SupportsAnpr => true;

    public Task<IPlateSubscription> SubscribePlatesAsync(DeviceConnectionInfo info, Action<PlateRecognition> onPlate,
        CancellationToken ct = default)
    {
        string id = IdOf(info);
        var subscription = IsupGateway.Require().SubscribeAlarms(id, alarm =>
        {
            if (IsupVideoAlarms.ToPlate(alarm) is { } plate) onPlate(plate);
        });
        return Task.FromResult<IPlateSubscription>(new IsupAlarmSubscription(subscription));
    }

    public bool SupportsEvents => true;

    public Task<IDeviceEventSubscription> SubscribeEventsAsync(DeviceConnectionInfo info, Action<DeviceEvent> onEvent,
        CancellationToken ct = default)
    {
        string id = IdOf(info);
        var subscription = IsupGateway.Require().SubscribeAlarms(id, alarm =>
        {
            if (IsupVideoAlarms.ToEvent(alarm) is { } e) onEvent(e);
        });
        return Task.FromResult<IDeviceEventSubscription>(new IsupAlarmSubscription(subscription));
    }

    public Task<VideoEventOverlay?> GetEventOverlayAsync(DeviceConnectionInfo info, int rtspChannel,
        Core.Contracts.VideoEventKind kind, int? ruleId, CancellationToken ct = default) =>
        HikvisionRuleGeometry.GetAsync(info, rtspChannel, kind, ruleId, ct);

    public Task<IReadOnlyList<AnalyticsRuleInfo>?> GetAnalyticsRulesAsync(DeviceConnectionInfo info, int rtspChannel,
        Core.Contracts.VideoEventKind kind, CancellationToken ct = default) =>
        HikvisionRuleGeometry.ListAsync(info, rtspChannel, kind, ct);

    /// <summary>
    /// Suscripción a las alarmas ISUP de un equipo. No se "cae" cuando el equipo se desconecta: las alarmas vuelven solas al
    /// reconectarse, así que sigue viva hasta que se libera.
    /// </summary>
    private sealed class IsupAlarmSubscription(IDisposable inner) : IPlateSubscription, IDeviceEventSubscription
    {
        private int _disposed;
        public bool IsAlive => Volatile.Read(ref _disposed) == 0;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Fábrica del driver de video por ISUP.</summary>
public sealed class HikvisionIsupDeviceDriverFactory : IDeviceDriverFactory
{
    public string DriverKey => "hikvision-isup";
    public string DisplayName => "Hikvision por ISUP (el equipo se conecta al servidor)";
    public DriverCapabilities Capabilities => new(
        SupportsSnapshot: true,
        SupportsDiscovery: false,
        DefaultSdkPort: 0,
        DefaultRtspPort: 0,
        SupportsAnpr: true,
        SupportsEvents: true);
    public bool UsesIsup => true;

    public IDeviceDriver Create() => new HikvisionIsupDeviceDriver();
}
