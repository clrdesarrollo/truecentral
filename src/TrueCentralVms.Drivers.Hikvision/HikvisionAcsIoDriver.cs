using System.Text.Json;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Hikvision.Interop;

namespace TrueCentralVms.Drivers.Hikvision;

public sealed class HikvisionAcsIoDriverFactory : IIoModuleDriverFactory
{
    public string DriverKey => HikvisionAcsIoDriver.DriverKey;
    public string DisplayName => "Hikvision: entradas y salidas de alarma de un terminal de acceso";
    public int DefaultPort => 80;
    /// <summary>Lo usa el servidor para las entradas y salidas de los terminales de acceso.</summary>
    public bool Internal => true;
    public IIoModuleDriver Create() => new HikvisionAcsIoDriver();
}

/// <summary>
/// Entradas y salidas de alarma de un terminal o controladora de acceso
/// Hikvision que no las expone en <c>/ISAPI/System/IO</c> (el DS-K1T804AMF):
/// <list type="bullet">
/// <item>Estado: <c>/ISAPI/AccessControl/AcsWorkStatus</c> trae
/// <c>alarmInStatus</c> y <c>alarmOutStatus</c>, los números (desde 1) de las
/// entradas en alarma y de las salidas activas. Es la misma consulta que hace
/// el control de acceso para sus puertas.</item>
/// <item>Operar: <c>NET_DVR_SetAlarmOut</c> por el SDK (puerto 8000): ISAPI no
/// tiene una orden para las salidas de alarma de estos equipos.</item>
/// </list>
/// Cuántas tiene lo dice el SDK al iniciar sesión (lo leyó el control de acceso).
/// </summary>
public sealed class HikvisionAcsIoDriver : IIoModuleDriver
{
    public const string DriverKey = "hikvision-acs-io";

    /// <summary>Puerto del SDK de los terminales (el de fábrica).</summary>
    internal const int SdkPort = 8000;

    public IReadOnlyList<IoModuleModel> Models { get; } = new HikvisionIoDriver().Models;

    public TimeSpan MinBlinkInterval => TimeSpan.FromSeconds(1);

    public Task<IoModuleInfo> ProbeAsync(IoConnectionInfo info, string? modelKey, CancellationToken ct = default) => Task.Run(() =>
    {
        var (userId, inputs, outputs) = HikvisionSdkIoDriver.Login(info with { Port = SdkPort });
        CHCNetSDK.NET_DVR_Logout(userId);
        if (inputs + outputs == 0) throw new DriverException("El equipo no tiene entradas ni salidas de alarma.");
        string reported = HikvisionIoDriver.ModelKeyFor(inputs, outputs);
        return new IoModuleInfo(reported, Models.First(m => m.Key == (modelKey ?? reported)));
    }, ct);

    public IIoModuleSession Open(IoConnectionInfo info, IoModuleModel model, TimeSpan pollInterval) =>
        new HikvisionAcsIoSession(info, model, pollInterval);
}

internal sealed class HikvisionAcsIoSession(IoConnectionInfo info, IoModuleModel model, TimeSpan pollInterval) : IIoModuleSession
{
    /// <summary>Los terminales atienden de a una petición y llevan el control de acceso: sin apuro.</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(5);

    private readonly HikvisionIsapiClient _client = HikvisionIoDriver.Client(info);
    private readonly bool[] _inputs = new bool[model.Inputs];
    private readonly bool[] _outputs = new bool[model.Outputs];
    private readonly Dictionary<int, CancellationTokenSource> _effects = [];
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _sdkGate = new(1, 1);
    private readonly object _stateLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _userId = -1;
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
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { return; }
                continue;
            }
            try { await _wake.WaitAsync(Interval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Números (desde 1) de una lista de <c>AcsWorkStatus</c>; vacía si el equipo no la manda.</summary>
    private static HashSet<int> Ports(JsonElement status, string name)
    {
        var set = new HashSet<int>();
        if (status.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int n)) set.Add(n);
        return set;
    }

    private async Task PollAsync(CancellationToken ct)
    {
        string json = await _client.RequestAsync(HttpMethod.Get, "/ISAPI/AccessControl/AcsWorkStatus?format=json", ct: ct, allowNotFound: false)
                      ?? throw new DriverException("El equipo no entregó su estado de trabajo.");
        HashSet<int> inputs, outputs;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var status = doc.RootElement.TryGetProperty("AcsWorkStatus", out var inner) ? inner : doc.RootElement;
            inputs = Ports(status, "alarmInStatus");
            outputs = Ports(status, "alarmOutStatus");
        }
        catch (JsonException) { throw new DriverException("El equipo entregó un estado de trabajo ilegible."); }

        var changes = new List<IoChannelChange>();
        var now = DateTime.UtcNow;
        lock (_stateLock)
        {
            for (int i = 0; i < _inputs.Length; i++) Apply(_inputs, IoChannelKind.Input, i, inputs.Contains(i + 1), now, changes);
            for (int o = 0; o < _outputs.Length; o++) Apply(_outputs, IoChannelKind.Output, o, outputs.Contains(o + 1), now, changes);
        }
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

    private void Logout()
    {
        if (_userId >= 0) { CHCNetSDK.NET_DVR_Logout(_userId); _userId = -1; }
    }

    /// <summary>Activa (1) o suelta (0) una salida por el SDK; la sesión se abre con la primera orden.</summary>
    private async Task CommandAsync(int output, bool on, CancellationToken ct)
    {
        if (output < 0 || output >= _outputs.Length)
            throw new DriverException(_outputs.Length == 0 ? "El equipo no tiene salidas de alarma."
                : $"El equipo tiene salidas de alarma 0 a {_outputs.Length - 1}.");
        await _sdkGate.WaitAsync(ct);
        try
        {
            await Task.Run(() =>
            {
                if (_userId < 0) _userId = HikvisionSdkIoDriver.Login(info with { Port = HikvisionAcsIoDriver.SdkPort }).UserId;
                if (!CHCNetSDK.NET_DVR_SetAlarmOut(_userId, output, on ? 1 : 0))
                {
                    uint code = CHCNetSDK.NET_DVR_GetLastError();
                    Logout();
                    throw new DriverException($"El equipo no aceptó la orden a la salida {output}: {HikvisionException.DescribeForUser(code)} (error {code}).");
                }
            }, ct);
        }
        finally { _sdkGate.Release(); }
        var changes = new List<IoChannelChange>();
        lock (_stateLock) Apply(_outputs, IoChannelKind.Output, output, on, DateTime.UtcNow, changes);
        Publish(changes);
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
        await _sdkGate.WaitAsync();
        try { Logout(); } finally { _sdkGate.Release(); }
    }
}
