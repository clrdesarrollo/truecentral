using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Axis;

/// <summary>
/// Eventos de una cámara Axis por el WebSocket de AXIS OS
/// (/vapix/ws-data-stream). El WebSocket no lleva Digest: primero se pide
/// por HTTP autenticado una sesión de un solo uso (wssession.cgi) y se
/// conecta con ella. Llegan los mismos temas ONVIF/Axis que ven las reglas de
/// la cámara; aquí se traducen a los tipos de evento del VMS y solo se
/// notifica el flanco de subida (el estado pasa a activo).
/// </summary>
internal sealed partial class AxisEventStream : IDeviceEventSubscription
{
    /// <summary>Temas que se piden: video y analíticas (incluidas las ACAP), entradas, audio y fallas.</summary>
    private static readonly string[] TopicFilters =
    [
        "tns1:VideoSource//.",
        "tns1:RuleEngine//.",
        "tnsaxis:CameraApplicationPlatform//.",
        "tns1:Device/tnsaxis:IO//.",
        "tns1:AudioSource//.",
        "tns1:Device/tnsaxis:HardwareFailure//.",
        "tns1:Device/tnsaxis:Status//.",
    ];

    private readonly ClientWebSocket _socket;
    private readonly Action<DeviceEvent> _onEvent;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, bool> _states = new();
    private Task? _loop;
    private volatile bool _alive = true;
    /// <summary>Para tomar la foto del momento (null en las pruebas del traductor).</summary>
    private readonly DeviceConnectionInfo? _info;
    /// <summary>Escenarios y perfiles de la cámara: dan el tipo y el número de regla de cada evento de analítica.</summary>
    private readonly IReadOnlyList<AxisAnalytics.Rule> _rules;

    internal AxisEventStream(ClientWebSocket socket, Action<DeviceEvent> onEvent, DeviceConnectionInfo? info = null,
        IReadOnlyList<AxisAnalytics.Rule>? rules = null)
    {
        _socket = socket;
        _onEvent = onEvent;
        _info = info;
        _rules = rules ?? [];
    }

    public bool IsAlive => _alive && _socket.State == WebSocketState.Open;

    public static async Task<AxisEventStream> OpenAsync(DeviceConnectionInfo info, Action<DeviceEvent> onEvent, CancellationToken ct)
    {
        string session;
        using (var client = new AxisVapixClient(info))
        {
            try
            {
                session = ParseSession(await client.GetStringAsync("axis-cgi/wssession.cgi", ct));
            }
            catch (AxisNotFoundException)
            {
                throw new DriverException(
                    "La cámara Axis no ofrece el canal de eventos por WebSocket: requiere AXIS OS 10.11 o superior.");
            }
        }
        if (session.Length == 0)
            throw new DriverException("La cámara Axis no entregó una sesión para el canal de eventos.");

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var uri = new Uri($"ws://{info.Host}:{info.Port}/vapix/ws-data-stream?wssession={Uri.EscapeDataString(session)}&sources=events");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(uri, timeout.Token);

            var configure = new
            {
                apiVersion = "1.0",
                context = "truecentral",
                method = "events:configure",
                @params = new { eventFilterList = TopicFilters.Select(t => new { topicFilter = t }).ToArray() },
            };
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(configure), WebSocketMessageType.Text, true, timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new DriverException($"No se pudo abrir el canal de eventos de la cámara Axis {info.Host}: {ex.Message}", ex);
        }

        IReadOnlyList<AxisAnalytics.Rule> rules;
        try { rules = await AxisAnalytics.RulesAsync(info, ct); }
        catch (DriverException) { rules = []; }   // sin reglas los eventos llegan igual, con su tipo genérico

        var stream = new AxisEventStream(socket, onEvent, info, rules);
        stream._loop = Task.Run(stream.ReceiveLoopAsync);
        return stream;
    }

    /// <summary>wssession.cgi devuelve la sesión como texto o, en algunos firmware, dentro de un JSON.</summary>
    private static string ParseSession(string body)
    {
        body = body.Trim();
        if (!body.StartsWith('{')) return body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (string name in new[] { "data", "wssession", "session", "sessionId" })
                if (doc.RootElement.TryGetProperty(name, out var v))
                {
                    if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
                    if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("wssession", out var inner)) return inner.GetString() ?? "";
                }
        }
        catch (JsonException) { }
        return "";
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        try
        {
            while (!_cts.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(buffer, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                try
                {
                    Handle(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                }
                catch (Exception)
                {
                    // un mensaje mal formado no corta el canal
                }
                message.SetLength(0);
            }
        }
        catch (Exception)
        {
            // corte de red o cierre: la suscripción queda muerta y el servicio la rehace
        }
        finally
        {
            _alive = false;
        }
    }

    internal void Handle(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("method", out var method) || method.GetString() != "events:notify") return;
        if (!root.TryGetProperty("params", out var p) || !p.TryGetProperty("notification", out var n)) return;

        string topic = n.TryGetProperty("topic", out var t) ? t.GetString() ?? "" : "";
        var message = n.TryGetProperty("message", out var m) ? m : default;
        var source = Dictionary(message, "source");
        var data = Dictionary(message, "data");

        if (Translate(topic, source, data, _rules) is not { } evt) return;

        bool active = IsActive(data);
        string stateKey = topic + "|" + string.Join(",", source.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        bool wasActive = _states.GetValueOrDefault(stateKey);
        _states[stateKey] = active;
        if (!active || wasActive) return;

        DateTime at = n.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out long ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime
            : DateTime.Now;
        evt = evt with { At = at };

        // Axis no adjunta imagen al evento: se toma la del momento (sobre ella el
        // servidor dibuja la regla), sin frenar la lectura de los siguientes.
        if (_info is { } info && evt.ChannelNumber > 0 && evt.Kind != VideoEventKind.DeviceFault)
            _ = Task.Run(async () =>
            {
                byte[]? jpeg = null;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    jpeg = await new AxisDeviceDriver().CaptureSnapshotAsync(info, evt.ChannelNumber, timeout.Token);
                }
                catch (Exception) { }
                _onEvent(evt with { Image = jpeg });
            });
        else
            _onEvent(evt);
    }

    private static Dictionary<string, string> Dictionary(JsonElement message, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty(name, out var obj) || obj.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var prop in obj.EnumerateObject())
            result[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.ToString();
        return result;
    }

    /// <summary>Las reglas de Axis informan su estado en campos de nombre variable: active, state, triggered, IsMotion...</summary>
    internal static bool IsActive(Dictionary<string, string> data)
    {
        foreach (string key in new[] { "active", "state", "triggered", "IsMotion", "IsTamper", "IsInside", "LogicalState", "tampering", "disruption" })
            if (data.TryGetValue(key, out string? v))
                return v is "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    /// <summary>Tema de Axis → evento del VMS (sin hora); null si el tema no interesa.</summary>
    internal static DeviceEvent? Translate(string topic, Dictionary<string, string> source, Dictionary<string, string> data,
        IReadOnlyList<AxisAnalytics.Rule>? rules = null)
    {
        string Has(string s) => topic.Contains(s, StringComparison.OrdinalIgnoreCase) ? s : "";
        int channel = ChannelOf(topic, source);
        string? rule = RuleNameOf(topic);

        DeviceEvent Video(VideoEventKind kind, string label) =>
            new(channel, kind, rule is null ? label : $"{label} ({rule})", DateTime.Now, RuleName: rule);

        // Escenarios de Object Analytics y perfiles de VMD4: el tipo y el nombre salen de la
        // configuración de la cámara ("Device1Scenario3" solo dice el número). ANY repite
        // cualquier escenario/perfil: se descarta para no duplicar.
        var acap = AcapRuleInTopic().Match(topic);
        if (acap.Success)
        {
            if (acap.Groups[3].Value.Equals("ANY", StringComparison.OrdinalIgnoreCase)) return null;
            bool aoa = acap.Groups[2].Value.Equals("Scenario", StringComparison.OrdinalIgnoreCase);
            int id = int.Parse(acap.Groups[3].Value, CultureInfo.InvariantCulture);
            var known = AxisAnalytics.ForTopic(rules ?? [], aoa ? "aoa" : "vmd", channel, id);
            var kind = known?.Kind ?? (aoa ? VideoEventKind.Intrusion : VideoEventKind.Motion);
            string label = kind switch
            {
                VideoEventKind.LineCrossing => "Cruce de línea",
                VideoEventKind.PeopleCounting => "Conteo por línea",
                VideoEventKind.Crowd => "Ocupación del área",
                VideoEventKind.Motion => "Detección de movimiento",
                _ => "Objeto en el área",
            };
            string name = known?.Name ?? (aoa ? $"escenario {id}" : $"perfil {id}");
            return new DeviceEvent(channel, kind, $"{label} ({name})", DateTime.Now, RuleName: name, RuleId: id);
        }

        // Entradas físicas: "IO/Port" (las salidas publican aparte, en Trigger/Relay, que no se pide).
        if (topic.EndsWith("IO/Port", StringComparison.OrdinalIgnoreCase))
        {
            int input = int.TryParse(source.GetValueOrDefault("port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : 1;
            return new DeviceEvent(0, VideoEventKind.AlarmInput, $"Entrada de alarma {input}", DateTime.Now, AlarmInput: input);
        }
        if (Has("IO/") != "") return null;   // puertos virtuales, supervisión de cableado...

        if (Has("CrossLine") != "" || Has("FenceGuard") != "") return Video(VideoEventKind.LineCrossing, "Cruce de línea");
        if (Has("LoiteringGuard") != "") return Video(VideoEventKind.Loitering, "Merodeo");
        if (Has("MotionGuard") != "") return Video(VideoEventKind.Intrusion, "Intrusión");
        if (Has("ObjectAnalytics") != "") return Video(VideoEventKind.Intrusion, "Objeto detectado (AXIS Object Analytics)");
        if (Has("Tampering") != "") return Video(VideoEventKind.Tamper, "Sabotaje de la cámara");
        if (Has("MotionAlarm") != "" || Has("VMD") != "" || Has("MotionDetector") != "" || Has("MotionRegionDetector") != "")
            return Video(VideoEventKind.Motion, "Detección de movimiento");
        if (Has("AudioSource") != "" && (Has("TriggerLevel") != "" || Has("Audio") != ""))
            return Video(VideoEventKind.AudioException, "Nivel de audio");
        if (Has("HardwareFailure") != "" || Has("Storage/Disruption") != "")
            return new DeviceEvent(0, VideoEventKind.DeviceFault, "Falla del equipo", DateTime.Now);
        return null;
    }

    /// <summary>
    /// Canal (1..N) del evento: "Camera2Profile1" o "Device1Scenario3" en el
    /// tema de las ACAP, o el campo de la fuente (channel es 1-based; el
    /// token de fuente de video ONVIF, 0-based). 1 si no se informa.
    /// </summary>
    internal static int ChannelOf(string topic, Dictionary<string, string> source)
    {
        var camera = CameraInTopic().Match(topic);
        if (!camera.Success) camera = DeviceInTopic().Match(topic);
        if (camera.Success) return int.Parse(camera.Groups[1].Value, CultureInfo.InvariantCulture);
        if (int.TryParse(source.GetValueOrDefault("channel"), out int ch) && ch > 0) return ch;
        foreach (string key in new[] { "VideoSourceConfigurationToken", "VideoSource", "VideoSourceToken", "source" })
            if (int.TryParse(source.GetValueOrDefault(key), out int zero) && zero >= 0) return zero + 1;
        return 1;
    }

    /// <summary>"Profile3" / "Scenario2" del tema: la regla de la analítica.</summary>
    private static string? RuleNameOf(string topic)
    {
        var rule = RuleInTopic().Match(topic);
        return rule.Success ? $"{(rule.Groups[1].Value.Equals("Scenario", StringComparison.OrdinalIgnoreCase) ? "escenario" : "perfil")} {rule.Groups[2].Value}" : null;
    }

    [GeneratedRegex(@"Camera(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CameraInTopic();

    [GeneratedRegex(@"(Profile|Scenario)(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RuleInTopic();

    [GeneratedRegex(@"Device(\d+)Scenario", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceInTopic();

    /// <summary>".../ObjectAnalytics/Device1Scenario3" o ".../VMD/Camera1Profile2" (o ANY).</summary>
    [GeneratedRegex(@"(?:ObjectAnalytics/Device|VMD/Camera)(\d+)(Scenario|Profile)(\d+|ANY)", RegexOptions.IgnoreCase)]
    private static partial Regex AcapRuleInTopic();

    public async ValueTask DisposeAsync()
    {
        _alive = false;
        _cts.Cancel();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
            }
        }
        catch (Exception) { }
        if (_loop is not null)
            try { await _loop; } catch (Exception) { }
        _socket.Dispose();
        _cts.Dispose();
    }
}
