using System.Buffers.Binary;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.ZkTeco;

public sealed class C3IoDriverFactory : IIoModuleDriverFactory
{
    public string DriverKey => "zkteco-c3-io";
    public string DisplayName => "ZKTeco inBio / C3 / MA: entradas y salidas auxiliares (protocolo C3, puerto 4370)";
    public int DefaultPort => C3Protocol.DefaultPort;
    public IIoModuleDriver Create() => new C3IoDriver();
}

/// <summary>
/// Las entradas y salidas AUXILIARES de una controladora ZKTeco C3 (las que
/// no son de puerta) como módulo de E/S del VMS: así se operan desde la página
/// de módulos, desde las automatizaciones («Operar relé») y disparan con el
/// cambio de una entrada, igual que un ADAM.
///
/// La controladora no tiene una lectura del estado de las auxiliares: se sabe
/// por sus eventos (220/221 entrada abierta/en corto, 12/13 salida activada/
/// desactivada) y por las órdenes propias. Hasta el primer cambio, una entrada
/// figura en 0. La conexión de módulos no lleva clave: sirve para controladoras
/// sin clave de comunicación (como vienen de fábrica).
/// </summary>
public sealed class C3IoDriver : IIoModuleDriver
{
    // Por cantidad de auxiliares: la inBio480 informa 4 entradas y 6 salidas,
    // el MA500 ninguna entrada y 1 salida.
    public IReadOnlyList<IoModuleModel> Models { get; } =
    [
        new("c3-4x6", "inBio 460/480 (4 entradas, 6 salidas auxiliares)", 4, 6, "Controladora de 4 puertas"),
        new("c3-4x4", "C3-400 / inBio 4 puertas (4 entradas, 4 salidas auxiliares)", 4, 4, "Controladora de 4 puertas"),
        new("c3-2x2", "C3-200 / inBio 260 (2 entradas, 2 salidas auxiliares)", 2, 2, "Controladora de 2 puertas"),
        new("c3-1x1", "C3-100 / inBio 160 (1 entrada, 1 salida auxiliar)", 1, 1, "Controladora de 1 puerta"),
        new("c3-0x1", "MA300 / MA500 (1 salida auxiliar)", 0, 1, "Terminal de acceso"),
    ];

    /// <summary>La salida se maneja por orden a la controladora: más rápido que 1 s no tiene sentido.</summary>
    public TimeSpan MinBlinkInterval => TimeSpan.FromSeconds(1);

    public async Task<IoModuleInfo> ProbeAsync(IoConnectionInfo info, string? modelKey, CancellationToken ct = default)
    {
        await using var connection = await C3Connection.OpenAsync(info.Host, info.Port, null, ct);
        var p = await connection.GetParametersAsync(["~DeviceName", "AuxInCount", "AuxOutCount"], ct);
        int inputs = int.TryParse(p.GetValueOrDefault("AuxInCount"), out int i) ? i : 0;
        int outputs = int.TryParse(p.GetValueOrDefault("AuxOutCount"), out int o) ? o : 0;
        // Lo "informado" es la clave del modelo que calza con lo que dice el
        // equipo (así el servidor solo avisa si se eligió a mano otro distinto).
        var detected = Models.FirstOrDefault(m => m.Inputs == inputs && m.Outputs == outputs);
        string reported = detected?.Key ?? $"{p.GetValueOrDefault("~DeviceName") ?? "C3"} ({inputs} entradas, {outputs} salidas)";

        var model = modelKey is not null
            ? Models.FirstOrDefault(m => string.Equals(m.Key, modelKey, StringComparison.OrdinalIgnoreCase))
              ?? throw new DriverException($"Modelo desconocido: '{modelKey}'.")
            : detected
              ?? throw new DriverException(
                  $"La controladora informa {inputs} entradas y {outputs} salidas auxiliares, una combinación que el VMS no conoce: elija el modelo a mano.");
        return new IoModuleInfo(reported, model);
    }

    public IIoModuleSession Open(IoConnectionInfo info, IoModuleModel model, TimeSpan pollInterval) =>
        new C3IoSession(info, model, pollInterval);
}

internal sealed class C3IoSession(IoConnectionInfo info, IoModuleModel model, TimeSpan pollInterval) : IIoModuleSession
{
    private const byte EventAuxOutOn = 12, EventAuxOutOff = 13, EventAuxInOpen = 220, EventAuxInShort = 221;
    private const byte AddressAux = 2;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool[] _inputs = new bool[model.Inputs];
    private readonly bool[] _outputs = new bool[model.Outputs];
    private readonly Dictionary<int, CancellationTokenSource> _effects = [];
    private readonly SemaphoreSlim _wake = new(0, 1);
    private C3Connection? _connection;
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

    private TimeSpan Interval => pollInterval < TimeSpan.FromMilliseconds(500) ? TimeSpan.FromMilliseconds(500) : pollInterval;

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
                _connection = await C3Connection.OpenAsync(info.Host, info.Port, null, ct);
                SetOnline(true, null);
                while (!ct.IsCancellationRequested)
                {
                    await PollAsync(ct);
                    try { await _wake.WaitAsync(Interval, ct); } catch (OperationCanceledException) { return; }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                SetOnline(false, ex is DriverException ? ex.Message : $"Error de comunicación: {ex.Message}");
            }
            finally
            {
                if (_connection is not null) { try { await _connection.DisposeAsync(); } catch { } _connection = null; }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Lee los eventos nuevos. La primera lectura de una sesión repite eventos
    /// recientes: sirven para conocer el estado, pero no se informan como
    /// cambios (dispararían automatizaciones por cosas viejas).
    /// </summary>
    private async Task PollAsync(CancellationToken ct)
    {
        byte[]? data;
        await _gate.WaitAsync(ct);
        try { data = _connection is null ? null : await _connection.ReadRtLogAsync(ct); }
        finally { _gate.Release(); }

        var changes = new List<IoChannelChange>();
        var now = DateTime.UtcNow;
        if (data is not null)
            for (int at = 0; at + C3Protocol.RtLogRecordSize <= data.Length; at += C3Protocol.RtLogRecordSize)
            {
                var r = data.AsSpan(at, C3Protocol.RtLogRecordSize);
                int index = r[9] - 1;
                switch (r[10])
                {
                    case EventAuxInOpen or EventAuxInShort when index >= 0 && index < _inputs.Length:
                        Apply(_inputs, IoChannelKind.Input, index, r[10] == EventAuxInShort, now, changes);
                        break;
                    case EventAuxOutOn or EventAuxOutOff when index >= 0 && index < _outputs.Length:
                        Apply(_outputs, IoChannelKind.Output, index, r[10] == EventAuxOutOn, now, changes);
                        break;
                }
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
        LastState = new IoState(_inputs.ToArray(), _outputs.ToArray(), DateTime.UtcNow);
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

    /// <summary>Orden a una salida auxiliar: segundos 0 = soltar, 255 = dejar activada, 1-254 = pulso.</summary>
    private async Task CommandAsync(int output, byte seconds, CancellationToken ct)
    {
        if (output < 0 || output >= _outputs.Length)
            throw new DriverException($"La controladora tiene salidas auxiliares 0 a {_outputs.Length - 1}.");
        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is null) throw new DriverException("Sin comunicación con la controladora.");
            await _connection.ControlAsync(C3Protocol.ControlOutput, (byte)(output + 1), AddressAux, seconds, ct);
        }
        finally { _gate.Release(); }
        var changes = new List<IoChannelChange>();
        Apply(_outputs, IoChannelKind.Output, output, seconds != 0, DateTime.UtcNow, changes);
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
        await CommandAsync(output, on ? C3Protocol.OutputHoldOpen : (byte)0, ct);
    }

    public async Task<bool> ToggleOutputAsync(int output, CancellationToken ct = default)
    {
        bool next = !(output >= 0 && output < _outputs.Length && _outputs[output]);
        await SetOutputAsync(output, next, ct);
        return next;
    }

    /// <summary>
    /// El pulso lo cronometra la propia controladora (segundos enteros, 1 a
    /// 254): sigue aunque se corte la comunicación. Aquí solo se refleja el
    /// fin en el estado.
    /// </summary>
    public async Task PulseOutputAsync(int output, TimeSpan duration, CancellationToken ct = default)
    {
        var effect = NewEffect(output);
        int seconds = Math.Clamp((int)Math.Ceiling(duration.TotalSeconds), 1, 254);
        await CommandAsync(output, (byte)seconds, ct);
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), effect); }
            catch (OperationCanceledException) { return; }
            var changes = new List<IoChannelChange>();
            Apply(_outputs, IoChannelKind.Output, output, false, DateTime.UtcNow, changes);
            Publish(changes);
        });
    }

    public async Task BlinkOutputAsync(int output, TimeSpan interval, TimeSpan duration, CancellationToken ct = default)
    {
        var effect = NewEffect(output);
        if (interval < TimeSpan.FromSeconds(1)) interval = TimeSpan.FromSeconds(1);
        await CommandAsync(output, C3Protocol.OutputHoldOpen, ct);
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
                    await CommandAsync(output, on ? C3Protocol.OutputHoldOpen : (byte)0, effect);
                }
                if (on) await CommandAsync(output, 0, CancellationToken.None);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OutputEffectFailed?.Invoke(output, ex.Message);
            }
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
