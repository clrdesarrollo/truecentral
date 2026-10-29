using System.Xml.Linq;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Hikvision;

public sealed class HikvisionIoDriverFactory : IIoModuleDriverFactory
{
    public string DriverKey => HikvisionIoDriver.DriverKey;
    public string DisplayName => "Hikvision: entradas y salidas de alarma (ISAPI)";
    public int DefaultPort => 80;
    /// <summary>Por ahora lo usa el servidor para las entradas y salidas de los terminales de acceso.</summary>
    public bool Internal => true;
    public IIoModuleDriver Create() => new HikvisionIoDriver();
}

/// <summary>
/// Entradas y salidas de alarma de un equipo Hikvision por ISAPI (las de un
/// terminal de acceso; sirve igual para las de una cámara o un grabador):
/// <list type="bullet">
/// <item><c>GET /ISAPI/System/IO</c>: cuáles tiene.</item>
/// <item><c>GET /ISAPI/System/IO/status</c>: el estado de TODAS en una sola
/// consulta (<c>active</c>/<c>inactive</c>), que es lo que se sondea.</item>
/// <item><c>PUT /ISAPI/System/IO/outputs/{id}/trigger</c> con <c>high</c> o
/// <c>low</c>: activar o soltar una salida.</item>
/// </list>
/// El equipo numera desde 1; el VMS, desde 0 (DI0 = entrada 1). Comparte la
/// sesión Digest del equipo con el resto de los drivers ISAPI.
/// </summary>
public sealed class HikvisionIoDriver : IIoModuleDriver
{
    public const string DriverKey = "hikvision-isapi-io";
    private const int MaxPorts = 16;

    /// <summary>Clave del modelo para una cantidad de entradas y salidas ("isapi-2x1").</summary>
    public static string ModelKeyFor(int inputs, int outputs) => $"isapi-{inputs}x{outputs}";

    /// <summary>Todas las combinaciones hasta 16 y 16: el modelo es lo que el equipo declara.</summary>
    public IReadOnlyList<IoModuleModel> Models { get; } =
        (from i in Enumerable.Range(0, MaxPorts + 1)
         from o in Enumerable.Range(0, MaxPorts + 1)
         where i + o > 0
         select new IoModuleModel(ModelKeyFor(i, o), $"{i} entradas y {o} salidas de alarma", i, o, "Equipo Hikvision (ISAPI)"))
        .ToList();

    /// <summary>Cada orden es una petición HTTP al equipo: más rápido que 1 s no tiene sentido.</summary>
    public TimeSpan MinBlinkInterval => TimeSpan.FromSeconds(1);

    internal static HikvisionIsapiClient Client(IoConnectionInfo info) =>
        new(new AlarmConnectionInfo(info.Host, info.Port, info.UseHttps, info.Username, info.Password), digestOnly: true, deviceNoun: "equipo");

    public async Task<IoModuleInfo> ProbeAsync(IoConnectionInfo info, string? modelKey, CancellationToken ct = default)
    {
        string xml = await Client(info).RequestAsync(HttpMethod.Get, "/ISAPI/System/IO", ct: ct, allowNotFound: false)
                     ?? throw new DriverException("El equipo no informa entradas ni salidas de alarma.");
        int inputs, outputs;
        try
        {
            var all = XDocument.Parse(xml).Descendants().ToList();
            inputs = Math.Min(all.Count(e => e.Name.LocalName == "IOInputPort"), MaxPorts);
            outputs = Math.Min(all.Count(e => e.Name.LocalName == "IOOutputPort"), MaxPorts);
        }
        catch (System.Xml.XmlException) { throw new DriverException("El equipo respondió algo que no es ISAPI."); }
        if (inputs + outputs == 0) throw new DriverException("El equipo no tiene entradas ni salidas de alarma.");
        string reported = ModelKeyFor(inputs, outputs);
        var model = Models.First(m => m.Key == (modelKey ?? reported));
        return new IoModuleInfo(reported, model);
    }

    public IIoModuleSession Open(IoConnectionInfo info, IoModuleModel model, TimeSpan pollInterval) =>
        new HikvisionIoSession(info, model, pollInterval);
}

internal sealed class HikvisionIoSession(IoConnectionInfo info, IoModuleModel model, TimeSpan pollInterval) : IIoModuleSession
{
    /// <summary>Tope de sondeo: los terminales atienden de a una petición y además llevan el control de acceso.</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private readonly HikvisionIsapiClient _client = HikvisionIoDriver.Client(info);
    private readonly bool[] _inputs = new bool[model.Inputs];
    private readonly bool[] _outputs = new bool[model.Outputs];
    private readonly Dictionary<int, CancellationTokenSource> _effects = [];
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _stateLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _primed;

    public IoModuleModel Model { get; } = model;
    public IoState? LastState { get; private set; }
    public bool? IsOnline { get; private set; }

    public event Action<IoState>? StateChanged;
    public event Action<IoChannelChange>? ChannelChanged;
    public event Action<bool, string?>? ConnectionChanged;
    public event Action<int, string>? OutputEffectFailed;

    private TimeSpan Interval => pollInterval < MinInterval ? MinInterval : pollInterval;

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void RefreshNow()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollAsync(ct);
                SetOnline(true, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                SetOnline(false, ex is DriverException ? ex.Message : $"Error de comunicación: {ex.Message}");
                // Sin respuesta: se espera más antes de volver a preguntar (no cargar al equipo caído).
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
                continue;
            }
            try { await _wake.WaitAsync(Interval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Lee el estado de todas las entradas y salidas en una sola consulta.</summary>
    private async Task PollAsync(CancellationToken ct)
    {
        string xml = await _client.RequestAsync(HttpMethod.Get, "/ISAPI/System/IO/status", ct: ct, allowNotFound: false)
                     ?? throw new DriverException("El equipo no entregó el estado de sus entradas y salidas.");
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { throw new DriverException("El equipo entregó un estado de entradas y salidas ilegible."); }

        var changes = new List<IoChannelChange>();
        var now = DateTime.UtcNow;
        lock (_stateLock)
        {
            foreach (var port in doc.Descendants().Where(e => e.Name.LocalName == "IOPortStatus"))
            {
                string? Value(string name) => port.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim();
                if (!int.TryParse(Value("ioPortID"), out int id) || id < 1) continue;
                bool active = string.Equals(Value("ioState"), "active", StringComparison.OrdinalIgnoreCase);
                bool output = string.Equals(Value("ioPortType"), "output", StringComparison.OrdinalIgnoreCase);
                var channels = output ? _outputs : _inputs;
                if (id - 1 < channels.Length)
                    Apply(channels, output ? IoChannelKind.Output : IoChannelKind.Input, id - 1, active, now, changes);
            }
        }
        // La primera lectura da el estado, no cambios (dispararían automatizaciones por algo viejo).
        bool first = !_primed;
        _primed = true;
        Publish(first ? [] : changes, force: first);
    }

    private static void Apply(bool[] channels, IoChannelKind kind, int index, bool value, DateTime at, List<IoChannelChange> changes)
    {
        if (channels[index] == value) return;
        channels[index] = value;
        changes.Add(new IoChannelChange(kind, index, value, at));
    }

    private void Publish(IReadOnlyList<IoChannelChange> changes, bool force = false)
    {
        if (!force && changes.Count == 0) return;
        lock (_stateLock) LastState = new IoState(_inputs.ToArray(), _outputs.ToArray(), DateTime.UtcNow);
        StateChanged?.Invoke(LastState);
        foreach (var change in changes) ChannelChanged?.Invoke(change);
    }

    private void SetOnline(bool online, string? error)
    {
        if (IsOnline == online) return;
        IsOnline = online;
        if (!online) _primed = false;
        ConnectionChanged?.Invoke(online, error);
    }

    /// <summary>Activa (high) o suelta (low) una salida del equipo.</summary>
    private async Task CommandAsync(int output, bool on, CancellationToken ct)
    {
        if (output < 0 || output >= _outputs.Length)
            throw new DriverException(_outputs.Length == 0 ? "El equipo no tiene salidas de alarma."
                : $"El equipo tiene salidas de alarma 0 a {_outputs.Length - 1}.");
        string body = "<IOPortData version=\"2.0\" xmlns=\"http://www.isapi.org/ver20/XMLSchema\">" +
                      $"<outputState>{(on ? "high" : "low")}</outputState></IOPortData>";
        _ = await _client.RequestAsync(HttpMethod.Put, $"/ISAPI/System/IO/outputs/{output + 1}/trigger", body,
            "application/xml", ct, allowNotFound: false);
        var changes = new List<IoChannelChange>();
        lock (_stateLock) Apply(_outputs, IoChannelKind.Output, output, on, DateTime.UtcNow, changes);
        Publish(changes);
        RefreshNow();
    }

    private void StopEffect(int output)
    {
        lock (_effects)
            if (_effects.Remove(output, out var running)) { running.Cancel(); running.Dispose(); }
    }

    private CancellationToken NewEffect(int output)
    {
        StopEffect(output);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? CancellationToken.None);
        lock (_effects) _effects[output] = cts;
        return cts.Token;
    }

    public async Task SetOutputAsync(int output, bool on, CancellationToken ct = default)
    {
        StopEffect(output);
        await CommandAsync(output, on, ct);
    }

    public async Task<bool> ToggleOutputAsync(int output, CancellationToken ct = default)
    {
        bool next;
        lock (_stateLock) next = !(output >= 0 && output < _outputs.Length && _outputs[output]);
        await SetOutputAsync(output, next, ct);
        return next;
    }

    /// <summary>El pulso lo cronometra el VMS: si se corta la comunicación antes de soltar, se avisa.</summary>
    public async Task PulseOutputAsync(int output, TimeSpan duration, CancellationToken ct = default)
    {
        var effect = NewEffect(output);
        await CommandAsync(output, true, ct);
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(duration, effect); }
            catch (OperationCanceledException) { return; }
            try { await CommandAsync(output, false, CancellationToken.None); }
            catch (Exception ex) { OutputEffectFailed?.Invoke(output, ex.Message); }
        });
    }

    public async Task BlinkOutputAsync(int output, TimeSpan interval, TimeSpan duration, CancellationToken ct = default)
    {
        var effect = NewEffect(output);
        if (interval < TimeSpan.FromSeconds(1)) interval = TimeSpan.FromSeconds(1);
        await CommandAsync(output, true, ct);
        _ = Task.Run(async () =>
        {
            var until = duration == Timeout.InfiniteTimeSpan ? DateTime.MaxValue : DateTime.UtcNow + duration;
            bool on = true;
            try
            {
                while (DateTime.UtcNow < until)
                {
                    await Task.Delay(interval, effect);
                    on = !on;
                    await CommandAsync(output, on, effect);
                }
                if (on) await CommandAsync(output, false, CancellationToken.None);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { OutputEffectFailed?.Invoke(output, ex.Message); }
        });
    }

    public async Task SetAllOutputsAsync(bool on, CancellationToken ct = default)
    {
        for (int output = 0; output < _outputs.Length; output++) await SetOutputAsync(output, on, ct);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_effects)
        {
            foreach (var effect in _effects.Values) { effect.Cancel(); effect.Dispose(); }
            _effects.Clear();
        }
        if (_cts is null) return;
        _cts.Cancel();
        try { if (_loop is not null) await _loop; } catch { }
        _cts.Dispose();
        _cts = null;
    }
}
