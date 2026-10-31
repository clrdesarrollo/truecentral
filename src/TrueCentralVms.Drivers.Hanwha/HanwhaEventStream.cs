using System.Globalization;
using System.Text;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Hanwha;

/// <summary>
/// Eventos de un equipo Hanwha por SUNAPI. "monitor" devuelve el estado de
/// todos los eventos de una vez; "monitordiff" deja la conexión abierta y
/// manda solo lo que cambia (según firmware, como flujo continuo o
/// respondiendo y cerrando: en ese caso se vuelve a pedir enseguida). Los
/// firmware que no lo tienen se consultan con "monitor" cada segundo. Se
/// notifica solo el flanco de subida (False → True).
/// </summary>
internal sealed class HanwhaEventStream : IDeviceEventSubscription
{
    private const string Monitor = "stw-cgi/eventstatus.cgi?msubmenu=eventstatus&action=monitor";
    private const string MonitorDiff = "stw-cgi/eventstatus.cgi?msubmenu=eventstatus&action=monitordiff";

    private readonly DeviceConnectionInfo _info;
    private readonly Action<DeviceEvent> _onEvent;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, bool> _states = new(StringComparer.OrdinalIgnoreCase);
    private Task? _loop;
    private volatile bool _alive = true;

    private HanwhaEventStream(DeviceConnectionInfo info, Action<DeviceEvent> onEvent)
    {
        _info = info;
        _onEvent = onEvent;
    }

    public bool IsAlive => _alive;

    public static async Task<HanwhaEventStream> OpenAsync(DeviceConnectionInfo info, Action<DeviceEvent> onEvent, CancellationToken ct)
    {
        var stream = new HanwhaEventStream(info, onEvent);
        // Estado inicial: lo que ya está activo al conectar no se notifica como evento nuevo
        // (y de paso valida credenciales y que el equipo tenga eventstatus).
        using (var client = new SunapiClient(info))
            stream.Apply(SunapiClient.Parse(await client.GetStringAsync(Monitor, ct)), notify: false);
        stream._loop = Task.Run(stream.RunAsync);
        return stream;
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        try
        {
            using var client = new SunapiClient(_info, Timeout.InfiniteTimeSpan);
            bool pushSupported = true;
            int quickEnds = 0;
            while (!ct.IsCancellationRequested)
            {
                if (pushSupported)
                {
                    var started = DateTime.UtcNow;
                    try
                    {
                        await ReadDiffAsync(client, ct);
                    }
                    catch (SunapiErrorException)
                    {
                        pushSupported = false;   // firmware sin monitordiff: consulta periódica
                        continue;
                    }
                    // Un equipo que cierra al instante sin esperar cambios se consulta con pausa, no en bucle apretado.
                    quickEnds = DateTime.UtcNow - started < TimeSpan.FromMilliseconds(300) ? quickEnds + 1 : 0;
                    if (quickEnds >= 3) await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
                else
                {
                    using (var poll = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, poll.Token))
                        Apply(SunapiClient.Parse(await client.GetStringAsync(Monitor, linked.Token)), notify: true);
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
            }
        }
        catch (Exception)
        {
            // corte de red, credenciales cambiadas o cierre: el servicio rehace la suscripción
        }
        finally
        {
            _alive = false;
        }
    }

    /// <summary>
    /// Lee la respuesta de monitordiff a medida que llega: líneas
    /// "Clave=Valor" se aplican al vuelo; un bloque JSON se junta hasta cerrar
    /// sus llaves. Cabeceras y separadores de multipart se ignoran.
    /// </summary>
    private async Task ReadDiffAsync(SunapiClient client, CancellationToken ct)
    {
        using var response = await client.OpenStreamAsync(MonitorDiff, ct);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(body, Encoding.UTF8);

        var json = new StringBuilder();
        int depth = 0;
        bool first = true;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            string trimmed = line.Trim();
            if (first && trimmed.Length > 0)
            {
                first = false;
                if (trimmed.StartsWith("NG", StringComparison.Ordinal))
                    SunapiClient.Parse(trimmed + "\n" + await reader.ReadToEndAsync(ct));   // lanza el error
            }
            if (depth > 0 || trimmed.StartsWith('{'))
            {
                json.AppendLine(line);
                depth += trimmed.Count(c => c == '{') - trimmed.Count(c => c == '}');
                if (depth <= 0)
                {
                    depth = 0;
                    Apply(SunapiClient.Parse(json.ToString()), notify: true);
                    json.Clear();
                }
                continue;
            }
            int eq = trimmed.IndexOf('=');
            if (eq > 0 && !trimmed.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                Apply(new Dictionary<string, string> { [trimmed[..eq]] = trimmed[(eq + 1)..] }, notify: true);
        }
    }

    internal void Apply(Dictionary<string, string> values, bool notify)
    {
        foreach (var (key, value) in values)
        {
            bool? active = value.Equals("True", StringComparison.OrdinalIgnoreCase) || value == "1" ? true
                : value.Equals("False", StringComparison.OrdinalIgnoreCase) || value == "0" ? false
                : null;
            if (active is null) continue;
            bool was = _states.GetValueOrDefault(key);
            _states[key] = active.Value;
            if (!notify || !active.Value || was) continue;
            if (Translate(key) is { } evt) _onEvent(evt);
        }
    }

    /// <summary>
    /// Clave de SUNAPI → evento del VMS; null si no interesa. Ejemplos:
    /// "Channel.0.MotionDetection", "Channel.1.VideoAnalytics.Passing",
    /// "AlarmInput.2", "SystemEvent.SDFail".
    /// </summary>
    internal static DeviceEvent? Translate(string key)
    {
        string[] parts = key.Split('.');
        if (parts.Length >= 2 && parts[0].Equals("AlarmInput", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int input))
            return new DeviceEvent(0, VideoEventKind.AlarmInput, $"Entrada de alarma {input}", DateTime.Now, AlarmInput: input);

        if (parts[0].Equals("SystemEvent", StringComparison.OrdinalIgnoreCase))
            return key.Contains("Fail", StringComparison.OrdinalIgnoreCase) || key.Contains("Error", StringComparison.OrdinalIgnoreCase)
                ? new DeviceEvent(0, VideoEventKind.DeviceFault, $"Falla del equipo ({parts[^1]})", DateTime.Now)
                : null;

        if (parts.Length < 3 || !parts[0].Equals("Channel", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ch))
            return null;

        string rest = string.Join('.', parts[2..]);
        bool Is(string name) => rest.StartsWith(name, StringComparison.OrdinalIgnoreCase);
        bool Has(string name) => rest.Contains(name, StringComparison.OrdinalIgnoreCase);

        (VideoEventKind Kind, string Label)? kind =
            Is("MotionDetection") ? (VideoEventKind.Motion, "Detección de movimiento")
            : Is("VideoAnalytics") && Has("Passing") ? (VideoEventKind.LineCrossing, "Cruce de línea")
            : Is("VideoAnalytics") && Has("Intrusion") ? (VideoEventKind.Intrusion, "Intrusión")
            : Is("VideoAnalytics") && Has("Entering") ? (VideoEventKind.RegionEntrance, "Entrada a la región")
            : Is("VideoAnalytics") && Has("Exiting") ? (VideoEventKind.RegionExit, "Salida de la región")
            : Is("VideoAnalytics") && Has("Loitering") ? (VideoEventKind.Loitering, "Merodeo")
            : Is("VideoAnalytics") && (Has("Appearing") || Has("Disappearing")) ? (VideoEventKind.ObjectLeftOrTaken, "Objeto abandonado o retirado")
            : Is("Tampering") || Is("ShockDetection") ? (VideoEventKind.Tamper, "Sabotaje de la cámara")
            : Is("DefocusDetection") || Is("FogDetection") ? (VideoEventKind.VideoException, "Anomalía de video")
            : Is("AudioDetection") || Is("AudioAnalytics") ? (VideoEventKind.AudioException, "Anomalía de audio")
            : Is("FaceDetection") ? (VideoEventKind.FaceDetection, "Detección de rostro")
            : Is("Videoloss") || Is("VideoLoss") ? (VideoEventKind.VideoLoss, "Pérdida de video")
            : null;
        if (kind is null) return null;

        // "...Passing.Line.2" / "...Intrusion.Area.1" / "MotionDetection.RegionID.3": la regla del equipo.
        int? ruleId = null;
        for (int i = 2; i < parts.Length - 1; i++)
            if (parts[i] is "Line" or "Area" or "Region" or "RegionID" or "ROI" &&
                int.TryParse(parts[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rule))
                ruleId = rule;

        string label = ruleId is null ? kind.Value.Label : $"{kind.Value.Label} (regla {ruleId})";
        return new DeviceEvent(ch + 1, kind.Value.Kind, label, DateTime.Now, RuleId: ruleId);
    }

    public async ValueTask DisposeAsync()
    {
        _alive = false;
        _cts.Cancel();
        if (_loop is not null)
            try { await _loop; } catch (Exception) { }
        _cts.Dispose();
    }
}
