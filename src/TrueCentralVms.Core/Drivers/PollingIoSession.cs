namespace TrueCentralVms.Core.Drivers;

/// <summary>
/// Sesión de E/S de un equipo que se consulta por HTTP: lee el estado de
/// todas las entradas y salidas cada tanto y activa o suelta una salida por
/// orden. Los pulsos y parpadeos los cronometra el VMS. La primera lectura
/// solo da el estado (no cambios: dispararían automatizaciones por algo
/// viejo). Cada marca implementa <see cref="ReadAsync"/> y <see cref="WriteAsync"/>.
/// Numeración del VMS: desde 0 (la entrada 1 del equipo es la 0).
/// </summary>
public abstract class PollingIoSession : IIoModuleSession
{
    /// <summary>Tope de sondeo: cada lectura es una petición al equipo, que además atiende video.</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _pollInterval;
    private readonly bool[] _inputs;
    private readonly bool[] _outputs;
    private readonly Dictionary<int, CancellationTokenSource> _effects = [];
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _stateLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _primed;

    protected PollingIoSession(IoModuleModel model, TimeSpan pollInterval)
    {
        Model = model;
        _pollInterval = pollInterval;
        _inputs = new bool[model.Inputs];
        _outputs = new bool[model.Outputs];
    }

    public IoModuleModel Model { get; }
    public IoState? LastState { get; private set; }
    public bool? IsOnline { get; private set; }

    public event Action<IoState>? StateChanged;
    public event Action<IoChannelChange>? ChannelChanged;
    public event Action<bool, string?>? ConnectionChanged;
    public event Action<int, string>? OutputEffectFailed;

    /// <summary>Estado actual: entradas y salidas (null = el equipo no informa ese lado; se deja como está).</summary>
    protected abstract Task<(bool[]? Inputs, bool[]? Outputs)> ReadAsync(CancellationToken ct);

    /// <summary>Activa o suelta la salida <paramref name="output"/> (desde 0).</summary>
    protected abstract Task WriteAsync(int output, bool on, CancellationToken ct);

    private TimeSpan Interval => _pollInterval < MinInterval ? MinInterval : _pollInterval;

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
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
                continue;
            }
            try { await _wake.WaitAsync(Interval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var (inputs, outputs) = await ReadAsync(ct);
        var changes = new List<IoChannelChange>();
        var now = DateTime.UtcNow;
        lock (_stateLock)
        {
            for (int i = 0; inputs is not null && i < Math.Min(inputs.Length, _inputs.Length); i++)
                Apply(_inputs, IoChannelKind.Input, i, inputs[i], now, changes);
            for (int i = 0; outputs is not null && i < Math.Min(outputs.Length, _outputs.Length); i++)
                Apply(_outputs, IoChannelKind.Output, i, outputs[i], now, changes);
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

    private async Task CommandAsync(int output, bool on, CancellationToken ct)
    {
        if (output < 0 || output >= _outputs.Length)
            throw new DriverException(_outputs.Length == 0 ? "El equipo no tiene salidas de alarma."
                : $"El equipo tiene salidas de alarma 0 a {_outputs.Length - 1}.");
        await WriteAsync(output, on, ct);
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

    public virtual async ValueTask DisposeAsync()
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

    /// <summary>
    /// Modelos "N entradas y M salidas" (hasta 16 y 16) con la misma clave que
    /// el servidor da a las partes de un equipo ("isapi-2x1").
    /// </summary>
    public static IReadOnlyList<IoModuleModel> PartModels(string description) =>
        (from i in Enumerable.Range(0, 17)
         from o in Enumerable.Range(0, 17)
         where i + o > 0
         select new IoModuleModel($"isapi-{i}x{o}", $"{i} entradas y {o} salidas de alarma", i, o, description))
        .ToList();
}
