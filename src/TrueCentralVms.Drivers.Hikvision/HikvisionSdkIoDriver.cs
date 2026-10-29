using System.Runtime.InteropServices;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Hikvision.Interop;

namespace TrueCentralVms.Drivers.Hikvision;

public sealed class HikvisionSdkIoDriverFactory : IIoModuleDriverFactory
{
    public string DriverKey => HikvisionSdkIoDriver.DriverKey;
    public string DisplayName => "Hikvision: entradas y salidas de alarma (SDK, puerto 8000)";
    public int DefaultPort => 8000;
    /// <summary>Lo usa el servidor para las entradas y salidas de grabadores y equipos sin ISAPI.</summary>
    public bool Internal => true;
    public IIoModuleDriver Create() => new HikvisionSdkIoDriver();
}

/// <summary>
/// Entradas y salidas de alarma de un equipo Hikvision por el SDK (puerto 8000),
/// para los que no las exponen por ISAPI (los DVR Turbo HD):
/// <list type="bullet">
/// <item>Cuántas: <c>byAlarmInPortNum</c>/<c>byAlarmOutPortNum</c> del inicio de sesión.</item>
/// <item>Estado: <c>NET_DVR_GetDVRWorkState_V30</c> (<c>byAlarmInStatic</c>, 1 = en alarma)
/// y <c>NET_DVR_GetAlarmOut_V30</c> (1 = salida activa).</item>
/// <item>Operar: <c>NET_DVR_SetAlarmOut</c> (puerto desde 0; 1 activa, 0 suelta).</item>
/// </list>
/// Las estructuras de estado del SDK son grandes y sus <c>Init()</c> no calzan
/// con sus tamaños declarados: se leen como bytes crudos en el desplazamiento
/// que calcula <see cref="Marshal.OffsetOf{T}(string)"/>.
/// </summary>
public sealed class HikvisionSdkIoDriver : IIoModuleDriver
{
    public const string DriverKey = "hikvision-sdk-io";

    public IReadOnlyList<IoModuleModel> Models { get; } = new HikvisionIoDriver().Models;

    public TimeSpan MinBlinkInterval => TimeSpan.FromSeconds(1);

    /// <summary>Inicia sesión y devuelve cuántas entradas y salidas de alarma tiene el equipo.</summary>
    internal static (int UserId, int Inputs, int Outputs) Login(IoConnectionInfo info)
    {
        HikvisionSdk.EnsureInitialized();
        var deviceInfo = new CHCNetSDK.NET_DVR_DEVICEINFO_V30();
        int userId = CHCNetSDK.NET_DVR_Login_V30(info.Host, info.Port, info.Username, info.Password, ref deviceInfo);
        if (userId < 0)
        {
            uint code = CHCNetSDK.NET_DVR_GetLastError();
            throw new DriverException($"No se pudo iniciar sesión en el equipo {info.Host}:{info.Port}: {HikvisionException.DescribeForUser(code)} (error {code}).");
        }
        return (userId, Math.Min(deviceInfo.byAlarmInPortNum, (byte)16), Math.Min(deviceInfo.byAlarmOutPortNum, (byte)16));
    }

    /// <summary>¿Cuántas entradas y salidas tiene por el SDK? Null si no inicia sesión o no tiene ninguna.</summary>
    internal static Task<DeviceIoInfo?> ReadIoAsync(IoConnectionInfo info, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            var (userId, inputs, outputs) = Login(info);
            CHCNetSDK.NET_DVR_Logout(userId);
            return inputs + outputs > 0 ? new DeviceIoInfo(inputs, outputs) : null;
        }
        catch (DriverException) { return null; }
    }, ct);

    public Task<IoModuleInfo> ProbeAsync(IoConnectionInfo info, string? modelKey, CancellationToken ct = default) => Task.Run(() =>
    {
        var (userId, inputs, outputs) = Login(info);
        CHCNetSDK.NET_DVR_Logout(userId);
        if (inputs + outputs == 0) throw new DriverException("El equipo no tiene entradas ni salidas de alarma.");
        string reported = HikvisionIoDriver.ModelKeyFor(inputs, outputs);
        return new IoModuleInfo(reported, Models.First(m => m.Key == (modelKey ?? reported)));
    }, ct);

    public IIoModuleSession Open(IoConnectionInfo info, IoModuleModel model, TimeSpan pollInterval) =>
        new HikvisionSdkIoSession(info, model, pollInterval);
}

internal sealed class HikvisionSdkIoSession(IoConnectionInfo info, IoModuleModel model, TimeSpan pollInterval) : IIoModuleSession
{
    /// <summary>El estado de trabajo de un grabador no es una consulta liviana: no más seguido que esto.</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);

    private static readonly int WorkStateSize = Marshal.SizeOf<CHCNetSDK.NET_DVR_WORKSTATE_V30>();
    private static readonly int AlarmInOffset = (int)Marshal.OffsetOf<CHCNetSDK.NET_DVR_WORKSTATE_V30>(nameof(CHCNetSDK.NET_DVR_WORKSTATE_V30.byAlarmInStatic));
    private static readonly int AlarmOutStatusSize = Marshal.SizeOf<CHCNetSDK.NET_DVR_ALARMOUTSTATUS_V30>();

    private readonly bool[] _inputs = new bool[model.Inputs];
    private readonly bool[] _outputs = new bool[model.Outputs];
    private readonly Dictionary<int, CancellationTokenSource> _effects = [];
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _gate = new(1, 1);
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
                await _gate.WaitAsync(ct);
                try { Poll(); }
                finally { _gate.Release(); }
                SetOnline(true, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Logout();
                SetOnline(false, ex is DriverException ? ex.Message : $"Error de comunicación: {ex.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
                continue;
            }
            try { await _wake.WaitAsync(Interval, ct); } catch (OperationCanceledException) { return; }
        }
        Logout();
    }

    private int Session()
    {
        if (_userId < 0) _userId = HikvisionSdkIoDriver.Login(info).UserId;
        return _userId;
    }

    private void Logout()
    {
        if (_userId >= 0) { CHCNetSDK.NET_DVR_Logout(_userId); _userId = -1; }
    }

    private static DriverException Failed(string what)
    {
        uint code = CHCNetSDK.NET_DVR_GetLastError();
        return new DriverException($"{what}: {HikvisionException.DescribeForUser(code)} (error {code}).");
    }

    /// <summary>Lee el estado de las entradas (estado de trabajo) y de las salidas.</summary>
    private void Poll()
    {
        int userId = Session();
        byte[] inputs = [], outputs = [];
        if (_inputs.Length > 0)
        {
            var buffer = Marshal.AllocHGlobal(WorkStateSize);
            try
            {
                Marshal.Copy(new byte[WorkStateSize], 0, buffer, WorkStateSize);
                if (!CHCNetSDK.NET_DVR_GetDVRWorkState_V30(userId, buffer)) throw Failed("El equipo no entregó el estado de sus entradas");
                inputs = new byte[_inputs.Length];
                Marshal.Copy(buffer + AlarmInOffset, inputs, 0, inputs.Length);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        if (_outputs.Length > 0)
        {
            var buffer = Marshal.AllocHGlobal(AlarmOutStatusSize);
            try
            {
                if (!CHCNetSDK.NET_DVR_GetAlarmOut_V30(userId, buffer)) throw Failed("El equipo no entregó el estado de sus salidas");
                outputs = new byte[_outputs.Length];
                Marshal.Copy(buffer, outputs, 0, outputs.Length);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        var changes = new List<IoChannelChange>();
        var now = DateTime.UtcNow;
        lock (_stateLock)
        {
            for (int i = 0; i < inputs.Length; i++) Apply(_inputs, IoChannelKind.Input, i, inputs[i] == 1, now, changes);
            for (int o = 0; o < outputs.Length; o++) Apply(_outputs, IoChannelKind.Output, o, outputs[o] == 1, now, changes);
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

    /// <summary>Activa (1) o suelta (0) una salida de alarma del equipo.</summary>
    private async Task CommandAsync(int output, bool on, CancellationToken ct)
    {
        if (output < 0 || output >= _outputs.Length)
            throw new DriverException(_outputs.Length == 0 ? "El equipo no tiene salidas de alarma."
                : $"El equipo tiene salidas de alarma 0 a {_outputs.Length - 1}.");
        await _gate.WaitAsync(ct);
        try
        {
            await Task.Run(() =>
            {
                if (!CHCNetSDK.NET_DVR_SetAlarmOut(Session(), output, on ? 1 : 0))
                {
                    var error = Failed($"El equipo no aceptó la orden a la salida {output}");
                    Logout();   // la próxima orden o lectura vuelve a iniciar sesión
                    throw error;
                }
            }, ct);
        }
        finally { _gate.Release(); }
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
        await _gate.WaitAsync();
        try { Logout(); } finally { _gate.Release(); }
    }
}
