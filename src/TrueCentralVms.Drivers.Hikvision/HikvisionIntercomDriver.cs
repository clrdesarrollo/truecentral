using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Core.Isup;
using TrueCentralVms.Drivers.Hikvision.Interop;

namespace TrueCentralVms.Drivers.Hikvision;

public sealed class HikvisionIntercomDriverFactory : IIntercomDriverFactory
{
    public string DriverKey => "hikvision-intercom";
    public string DisplayName => "Hikvision (frente de videoportero DS-KB / DS-KD / DS-KV)";
    public int DefaultPort => 8000;
    public int DefaultHttpPort => 80;
    public IIntercomDriver Create() => new HikvisionIntercomDriver();
}

/// <summary>
/// El mismo frente cuando se conecta al servidor por ISUP (detrás de NAT o por 4G): identificación, botones, puerta y órdenes
/// de llamada por el túnel ISAPI; las llamadas entrantes, por el canal de alarmas de ISUP; la voz por el servidor de voz de ISUP
/// y el video por el servicio ISUP.
/// </summary>
public sealed class HikvisionIsupIntercomDriverFactory : IIntercomDriverFactory
{
    public string DriverKey => "hikvision-isup";
    public string DisplayName => "Hikvision por ISUP (el frente se conecta al servidor)";
    public int DefaultPort => 0;
    public int DefaultHttpPort => 0;
    public bool UsesIsup => true;
    public IIntercomDriver Create() => new HikvisionIntercomDriver();
}

/// <summary>
/// Frentes de videoportero Hikvision. Verificado el 2026-09-22 contra un
/// DS-KB8113-IME1 (firmware V2.2.51). Dos transportes, porque el equipo reparte
/// así sus funciones:
/// <list type="bullet">
/// <item><b>SDK (puerto 8000)</b>: el enlace largo de señalización de llamadas
/// (<c>NET_DVR_VIDEO_CALL_SIGNAL_PROCESS</c>), por el que el frente avisa que
/// alguien tocó el timbre y recibe contestar/rechazar/colgar —lo mismo que
/// hace iVMS-4200 como centro de gestión—, y la voz en modo reenvío
/// (<c>StartVoiceCom_MR_V30</c>): G.711µ en tramas de 160 bytes (20 ms) en
/// ambos sentidos, sin tocar la tarjeta de sonido del servidor.</item>
/// <item><b>ISAPI (HTTP, Digest)</b>: identificación, el botón que llama a la
/// central (<c>/ISAPI/VideoIntercom/keyCfg</c>), el estado de la línea
/// (<c>callStatus</c>, que también sirve de sondeo) y la apertura de puerta
/// (<c>/ISAPI/AccessControl/RemoteControl/door/{n}</c>).</item>
/// </list>
/// GOTCHA verificado: el enlace de señalización NO convive con un
/// <c>alertStream</c> ISAPI abierto contra el mismo equipo (el SDK responde
/// error 23, "no soportado"); por eso este driver no usa alertStream.
/// </summary>
public sealed class HikvisionIntercomDriver : IIntercomDriver
{

    private static HikvisionIsapiClient Isapi(IntercomConnectionInfo info) =>
        new(new AlarmConnectionInfo(info.Host, info.HttpPort, false, info.Username, info.Password, info.IsupId),
            digestOnly: true, deviceNoun: "citófono");

    /// <summary>Olvida las conexiones HTTP cacheadas (al editar o borrar el frente).</summary>
    public static void Forget(IntercomConnectionInfo info) =>
        HikvisionIsapiClient.Forget(new AlarmConnectionInfo(info.Host, info.HttpPort, false, info.Username, info.Password, info.IsupId));

    private static bool IsIsup(IntercomConnectionInfo info, out string isupId)
    {
        isupId = info.IsupId ?? "";
        return isupId.Length > 0;
    }

    private static XElement? Child(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static string? Value(XElement parent, string name) => Child(parent, name)?.Value.Trim() is { Length: > 0 } v ? v : null;

    /// <summary>Login SDK propio (las sesiones de llamada y voz duran lo que dure la llamada; no usan la caché de PTZ, que se poda por ocio).</summary>
    private static int Login(IntercomConnectionInfo info)
    {
        HikvisionSdk.EnsureInitialized();
        var deviceInfo = new CHCNetSDK.NET_DVR_DEVICEINFO_V30();
        int userId = CHCNetSDK.NET_DVR_Login_V30(info.Host, info.Port, info.Username, info.Password, ref deviceInfo);
        if (userId < 0)
        {
            uint code = CHCNetSDK.NET_DVR_GetLastError();
            throw new DriverException(
                $"No se pudo iniciar sesión en el citófono {info.Host}:{info.Port}: {HikvisionException.DescribeForUser(code)} (error {code}).");
        }
        return userId;
    }

    private static string? CodecOf(int userId) => HikvisionSdkVoice.CodecOf(userId);

    // ------------------------------------------------------------------
    // Identificación y configuración
    // ------------------------------------------------------------------

    public async Task<IntercomInfo> ProbeAsync(IntercomConnectionInfo info, CancellationToken ct = default)
    {
        var client = Isapi(info);
        string deviceXml = await client.RequestAsync(HttpMethod.Get, "/ISAPI/System/deviceInfo", ct: ct, allowNotFound: false)
            ?? throw new DriverException("El citófono no entregó su identificación (/ISAPI/System/deviceInfo).");
        var device = XDocument.Parse(deviceXml).Root!;
        string? model = Value(device, "model");

        if (await client.RequestAsync(HttpMethod.Get, "/ISAPI/VideoIntercom/capabilities", ct: ct) is null)
            throw new DriverException(
                $"El equipo ({model ?? "modelo desconocido"}) responde ISAPI pero no es un videoportero (no expone /ISAPI/VideoIntercom).");

        bool callCenter = (await ReadKeysAsync(client, ct)).Any(k => string.Equals(Value(k, "enableCallCenter"), "true", StringComparison.OrdinalIgnoreCase));

        int doors = 1;
        try
        {
            if (await client.RequestAsync(HttpMethod.Get, "/ISAPI/AccessControl/RemoteControl/door/capabilities", ct: ct) is { } doorXml
                && XDocument.Parse(doorXml).Root is { } doorRoot
                && Child(doorRoot, "doorNo")?.Attribute("max")?.Value is { } max && int.TryParse(max, out int n) && n > 0)
                doors = n;
        }
        catch (Exception ex) when (ex is DriverException or System.Xml.XmlException) { /* 1 puerta por defecto */ }

        // Por IP el SDK es el que transporta llamadas y voz: validarlo también. Por ISUP no hay SDK: el códec lo dice ISAPI.
        string? codec = IsIsup(info, out _)
            ? await IsapiCodecAsync(client, ct)
            : await Task.Run(() =>
            {
                int userId = Login(info);
                try { return CodecOf(userId); }
                finally { CHCNetSDK.NET_DVR_Logout(userId); }
            }, ct);

        return new IntercomInfo(model, Value(device, "serialNumber"), Value(device, "firmwareVersion"),
            Value(device, "deviceName"), doors, callCenter, codec, await ReadKeyFrameSecondsAsync(client, ct));
    }

    /// <summary>Códec del canal de audio bidireccional ("G.711ulaw" → "ulaw"); null si no lo informa o no es G.711.</summary>
    private static async Task<string?> IsapiCodecAsync(HikvisionIsapiClient client, CancellationToken ct)
    {
        try
        {
            if (await client.RequestAsync(HttpMethod.Get, "/ISAPI/System/TwoWayAudio/channels", ct: ct) is not { } xml) return null;
            string? type = XDocument.Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "audioCompressionType")?.Value;
            return type is null ? null
                : type.Contains("ulaw", StringComparison.OrdinalIgnoreCase) ? "ulaw"
                : type.Contains("alaw", StringComparison.OrdinalIgnoreCase) ? "alaw"
                : null;
        }
        catch (Exception ex) when (ex is DriverException or System.Xml.XmlException) { return null; }
    }

    private const string MainStreamPath = "/ISAPI/Streaming/channels/101";

    /// <summary>Cuadros por segundo del stream (maxFrameRate viene ×100: 3000 = 30 fps).</summary>
    private static double FramesPerSecond(XElement video) =>
        Value(video, "maxFrameRate") is { } raw && int.TryParse(raw, out int x100) && x100 > 0 ? x100 / 100.0 : 25;

    /// <summary>Segundos entre I-frames del stream principal; null si el equipo no lo informa.</summary>
    private static async Task<double?> ReadKeyFrameSecondsAsync(HikvisionIsapiClient client, CancellationToken ct)
    {
        try
        {
            if (await client.RequestAsync(HttpMethod.Get, MainStreamPath, ct: ct) is not { } xml) return null;
            if (Child(XDocument.Parse(xml).Root!, "Video") is not { } video) return null;
            if (Value(video, "GovLength") is { } gov && int.TryParse(gov, out int frames) && frames > 0)
                return Math.Round(frames / FramesPerSecond(video), 1);
            return Value(video, "keyFrameInterval") is { } ms && int.TryParse(ms, out int millis) ? millis / 1000.0 : null;
        }
        catch (Exception ex) when (ex is DriverException or System.Xml.XmlException) { return null; }
    }

    /// <summary>
    /// GovLength = cuadros por segundo (un I-frame por segundo). Verificado el
    /// 2026-09-22: el DS-KB8113 viene de fábrica con GovLength 400 a 30 fps
    /// (un cuadro completo cada 13 s) y el video de la llamada tardaba eso en
    /// aparecer. Se reescribe el mismo XML que entrega el equipo cambiando solo
    /// ese valor, porque el PUT de <c>Streaming/channels</c> exige el documento completo.
    /// </summary>
    public async Task OptimizeVideoAsync(IntercomConnectionInfo info, CancellationToken ct = default)
    {
        var client = Isapi(info);
        string xml = await client.RequestAsync(HttpMethod.Get, MainStreamPath, ct: ct, allowNotFound: false)
            ?? throw new DriverException("El citófono no entregó la configuración de su video.");
        var doc = XDocument.Parse(xml);
        var video = Child(doc.Root!, "Video") ?? throw new DriverException("El citófono no informa la configuración de su video.");
        var gov = Child(video, "GovLength") ?? throw new DriverException("El citófono no permite ajustar el intervalo de cuadros completos.");
        int target = Math.Max(1, (int)Math.Round(FramesPerSecond(video)));
        if (int.TryParse(gov.Value, out int current) && current <= target * 2) return;
        gov.Value = target.ToString();
        _ = await client.RequestAsync(HttpMethod.Put, MainStreamPath, doc.ToString(SaveOptions.DisableFormatting),
                "application/xml", ct, allowNotFound: false)
            ?? throw new DriverException("El citófono no aceptó el ajuste de video.");
    }

    /// <summary>Por ISUP, la URL local del servicio ISUP con el video del frente (MediaMTX la publica con FFmpeg).</summary>
    public string BuildRtspUrl(IntercomConnectionInfo info, StreamProfile profile) =>
        IsIsup(info, out string isupId)
            ? IsupGateway.Require().LiveSourceUrl(isupId, 1, profile == StreamProfile.Main ? 0 : 1)
            : $"rtsp://{Uri.EscapeDataString(info.Username)}:{Uri.EscapeDataString(info.Password)}" +
              $"@{info.Host}:{info.RtspPort}/Streaming/Channels/{(profile == StreamProfile.Main ? 101 : 102)}";

    public async Task<byte[]?> CaptureSnapshotAsync(IntercomConnectionInfo info, CancellationToken ct = default)
    {
        byte[]? jpeg = null;
        try
        {
            using var response = await Isapi(info).OpenStreamAsync(MainStreamPath + "/picture", ct);
            jpeg = await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (DriverException) { }
        catch (HttpRequestException) { }
        // Un equipo sin foto puede contestar 200 con un XML de error. Por ISUP queda el respaldo de un cuadro del video en vivo.
        if (jpeg is [0xFF, 0xD8, ..]) return jpeg;
        return IsIsup(info, out string isupId) && IsupGateway.Current is { } gateway
            ? await gateway.GrabFrameAsync(isupId, 1, 0, ct)
            : null;
    }

    private static async Task<List<XElement>> ReadKeysAsync(HikvisionIsapiClient client, CancellationToken ct)
    {
        string? xml = await client.RequestAsync(HttpMethod.Get, "/ISAPI/VideoIntercom/keyCfg", ct: ct);
        if (xml is null) return [];
        var root = XDocument.Parse(xml).Root!;
        return root.Name.LocalName == "KeyCfg" ? [root] : root.Elements().Where(e => e.Name.LocalName == "KeyCfg").ToList();
    }

    public async Task EnableCallCenterAsync(IntercomConnectionInfo info, CancellationToken ct = default)
    {
        var client = Isapi(info);
        var keys = await ReadKeysAsync(client, ct);
        if (keys.Count == 0)
            throw new DriverException("El citófono no permite configurar sus botones por ISAPI (/ISAPI/VideoIntercom/keyCfg).");
        foreach (var key in keys)
        {
            var flag = Child(key, "enableCallCenter");
            if (flag is null || string.Equals(flag.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)) continue;
            flag.Value = "true";
            string id = Value(key, "id") ?? "1";
            _ = await client.RequestAsync(HttpMethod.Put, $"/ISAPI/VideoIntercom/keyCfg/{id}",
                    key.ToString(SaveOptions.DisableFormatting), "application/xml", ct, allowNotFound: false)
                ?? throw new DriverException($"El citófono no aceptó configurar el botón {id}.");
        }
    }

    public async Task<IntercomLineState> GetLineStateAsync(IntercomConnectionInfo info, CancellationToken ct = default)
    {
        string? json = await Isapi(info).RequestAsync(HttpMethod.Get, "/ISAPI/VideoIntercom/callStatus?format=json", ct: ct, allowNotFound: false);
        string? status = null;
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            if (doc.RootElement.TryGetProperty("CallStatus", out var cs) && cs.TryGetProperty("status", out var s))
                status = s.GetString();
        }
        catch (JsonException) { }
        return status?.ToLowerInvariant() switch
        {
            null or "" or "idle" => IntercomLineState.Idle,
            var v when v.Contains("ring") => IntercomLineState.Ringing,
            _ => IntercomLineState.InCall,
        };
    }

    public async Task SendCommandAsync(IntercomConnectionInfo info, IntercomCommand command, CancellationToken ct = default)
    {
        string cmd = command switch
        {
            IntercomCommand.Answer => "answer",
            IntercomCommand.Reject => "reject",
            _ => "hangUp",
        };
        string body = JsonSerializer.Serialize(new { CallSignal = new { cmdType = cmd } });
        _ = await Isapi(info).RequestAsync(HttpMethod.Put, "/ISAPI/VideoIntercom/callSignal?format=json", body, ct: ct, allowNotFound: false)
            ?? throw new DriverException("El citófono no respondió a la orden de llamada.");
    }

    public async Task OpenDoorAsync(IntercomConnectionInfo info, int door, CancellationToken ct = default)
    {
        const string body = "<RemoteControlDoor><cmd>open</cmd></RemoteControlDoor>";
        _ = await Isapi(info).RequestAsync(HttpMethod.Put, $"/ISAPI/AccessControl/RemoteControl/door/{door}",
                body, "application/xml", ct, allowNotFound: false)
            ?? throw new DriverException("El citófono no respondió a la orden de apertura.");
    }

    // ------------------------------------------------------------------
    // Señalización de llamadas
    // ------------------------------------------------------------------

    public Task<IIntercomCallLink> OpenCallLinkAsync(IntercomConnectionInfo info, Action<IntercomCallSignal> onSignal,
        CancellationToken ct = default)
    {
        if (IsIsup(info, out string isupId))
            return Task.FromResult<IIntercomCallLink>(new IsupCallLink(info, isupId, onSignal));
        return OpenSdkCallLinkAsync(info, onSignal, ct);
    }

    /// <summary>
    /// Enlace de llamadas de un frente ISUP: no hay enlace SDK que abrir; las llamadas llegan como eventos por el canal de
    /// alarmas de ISUP (mientras el frente esté conectado) y las órdenes salen por ISAPI (callSignal). Vive hasta liberarlo.
    /// </summary>
    private sealed class IsupCallLink : IIntercomCallLink
    {
        private readonly IntercomConnectionInfo _info;
        private readonly IDisposable _subscription;
        private int _disposed;

        public IsupCallLink(IntercomConnectionInfo info, string isupId, Action<IntercomCallSignal> onSignal)
        {
            _info = info;
            _subscription = IsupGateway.Require().SubscribeAlarms(isupId, alarm =>
            {
                if (IsupIntercomSignals.ToSignal(alarm) is { } signal) onSignal(signal);
            });
        }

        public bool IsAlive => Volatile.Read(ref _disposed) == 0;

        public Task SendAsync(IntercomCommand command, CancellationToken ct = default) =>
            new HikvisionIntercomDriver().SendCommandAsync(_info, command, ct);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _subscription.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private Task<IIntercomCallLink> OpenSdkCallLinkAsync(IntercomConnectionInfo info, Action<IntercomCallSignal> onSignal,
        CancellationToken ct) => Task.Run<IIntercomCallLink>(() =>
    {
        int userId = Login(info);
        var link = new CallLink(userId, info.Host, onSignal);
        try
        {
            link.Start();
            return link;
        }
        catch
        {
            CHCNetSDK.NET_DVR_Logout(userId);
            throw;
        }
    }, ct);

    private sealed class CallLink(int userId, string host, Action<IntercomCallSignal> onSignal) : IIntercomCallLink
    {
        private readonly object _sync = new();
        private int _handle = -1;
        private int _disposed;
        private volatile bool _broken;
        // Referencia fuerte: el SDK guarda el puntero a la función, no el delegado.
        private IntercomInterop.RemoteConfigCallback? _callback;

        public bool IsAlive => Volatile.Read(ref _disposed) == 0 && !_broken && _handle >= 0;

        public void Start()
        {
            _callback = OnData;
            var cond = new IntercomInterop.NET_DVR_VIDEO_CALL_COND
            {
                dwSize = (uint)Marshal.SizeOf<IntercomInterop.NET_DVR_VIDEO_CALL_COND>(),
                byRes = new byte[128],
            };
            int handle = IntercomInterop.NET_DVR_StartRemoteConfig(userId, IntercomInterop.NET_DVR_VIDEO_CALL_SIGNAL_PROCESS,
                ref cond, cond.dwSize, _callback, IntPtr.Zero);
            if (handle < 0)
            {
                uint code = CHCNetSDK.NET_DVR_GetLastError();
                throw new DriverException(code == 23
                    ? $"El citófono {host} no aceptó el enlace de llamadas (error 23): otro cliente puede tenerlo tomado o el equipo no lo soporta."
                    : $"El citófono {host} no aceptó el enlace de llamadas: {HikvisionException.DescribeForUser(code)} (error {code}).");
            }
            _handle = handle;
        }

        private void OnData(uint type, IntPtr buffer, uint length, IntPtr user)
        {
            try
            {
                if (type == IntercomInterop.NET_SDK_CALLBACK_TYPE_STATUS)
                {
                    // FAILED (1002) o EXCEPTION (1003): el enlace murió. Verificado
                    // el 2026-09-22 con el DS-KB8113: sin llamadas, el frente cierra
                    // la conexión del enlace a los ~3,5 min y el SDK avisa UNA vez
                    // 1002; desde ahí el frente ya no ve a la central y llama solo
                    // al monitor interior. Hay que reabrirlo de inmediato.
                    if (length >= 4 && Marshal.ReadInt32(buffer) is IntercomInterop.NET_SDK_CALLBACK_STATUS_FAILED
                            or IntercomInterop.NET_SDK_CALLBACK_STATUS_EXCEPTION)
                        _broken = true;
                    return;
                }
                if (type != IntercomInterop.NET_SDK_CALLBACK_TYPE_DATA || length < 8) return;
                var param = Marshal.PtrToStructure<IntercomInterop.NET_DVR_VIDEO_CALL_PARAM>(buffer);
                IntercomSignalKind? kind = param.dwCmdType switch
                {
                    IntercomInterop.CallRequest => IntercomSignalKind.Ringing,
                    IntercomInterop.CallCancel => IntercomSignalKind.Cancelled,
                    IntercomInterop.CallAnswer => IntercomSignalKind.Answered,
                    IntercomInterop.CallReject => IntercomSignalKind.Rejected,
                    IntercomInterop.CallBellTimeout => IntercomSignalKind.RingTimeout,
                    IntercomInterop.CallHangUp => IntercomSignalKind.HungUp,
                    IntercomInterop.CallDeviceBusy or IntercomInterop.CallClientBusy => IntercomSignalKind.Busy,
                    _ => null,
                };
                if (kind is null) return;
                onSignal(new IntercomCallSignal(kind.Value, OriginOf(param), DateTime.UtcNow));
            }
            catch
            {
                // Jamás propagar al hilo nativo del SDK.
            }
        }

        private static string? OriginOf(IntercomInterop.NET_DVR_VIDEO_CALL_PARAM p)
        {
            var parts = new List<string>();
            if (p.wBuildingNumber > 0) parts.Add($"edificio {p.wBuildingNumber}");
            if (p.wUnitNumber > 0) parts.Add($"torre {p.wUnitNumber}");
            if (p.wFloorNumber > 0) parts.Add($"piso {p.wFloorNumber}");
            if (p.wRoomNumber > 0) parts.Add($"depto {p.wRoomNumber}");
            if (p.wDevIndex > 0) parts.Add($"frente {p.wDevIndex}");
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }

        public Task SendAsync(IntercomCommand command, CancellationToken ct = default) => Task.Run(() =>
        {
            uint cmd = command switch
            {
                IntercomCommand.Answer => IntercomInterop.CallAnswer,
                IntercomCommand.Reject => IntercomInterop.CallReject,
                _ => IntercomInterop.CallHangUp,
            };
            lock (_sync)
            {
                if (!IsAlive) throw new DriverException($"El enlace de llamadas con el citófono {host} está cortado.");
                var param = IntercomInterop.NET_DVR_VIDEO_CALL_PARAM.Command(cmd);
                if (!IntercomInterop.NET_DVR_SendRemoteConfig(_handle, 0, ref param, param.dwSize))
                {
                    uint code = CHCNetSDK.NET_DVR_GetLastError();
                    throw new DriverException(
                        $"El citófono {host} rechazó la orden: {HikvisionException.DescribeForUser(code)} (error {code}).");
                }
            }
        }, ct);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            return new ValueTask(Task.Run(() =>
            {
                lock (_sync)
                {
                    if (_handle >= 0) IntercomInterop.NET_DVR_StopRemoteConfig(_handle);
                    _handle = -1;
                    CHCNetSDK.NET_DVR_Logout(userId);
                }
                GC.KeepAlive(_callback);
            }));
        }
    }

    // ------------------------------------------------------------------
    // Voz
    // ------------------------------------------------------------------

    public async Task<IIntercomVoiceSession> OpenVoiceAsync(IntercomConnectionInfo info, Action<ReadOnlyMemory<byte>> onAudio,
        CancellationToken ct = default) =>
        IsIsup(info, out string isupId)
            ? await IsupVoiceSession.OpenAsync(isupId, onAudio, broadcast: false, "citófono", ct)
            : await OpenSdkVoiceAsync(info, onAudio, ct);

    private static Task<IIntercomVoiceSession> OpenSdkVoiceAsync(IntercomConnectionInfo info, Action<ReadOnlyMemory<byte>> onAudio,
        CancellationToken ct) => Task.Run<IIntercomVoiceSession>(() =>
    {
        int userId = Login(info);
        try
        {
            string codec = CodecOf(userId)
                ?? throw new DriverException("El citófono usa un códec de voz no soportado (se admite G.711µ y G.711A).");
            var session = new HikvisionSdkVoiceSession(userId, codec, onAudio);
            session.Start(info.Host, "citófono");
            return session;
        }
        catch
        {
            CHCNetSDK.NET_DVR_Logout(userId);
            throw;
        }
    }, ct);

}
