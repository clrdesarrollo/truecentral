using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Adam;

public sealed class AdamIoDriverFactory : IIoModuleDriverFactory
{
    public string DriverKey => "adam-6000";
    public string DisplayName => "Advantech ADAM-6000 (Modbus TCP, puerto 502)";
    public int DefaultPort => ModbusTcpClient.DefaultPort;
    public IIoModuleDriver Create() => new AdamIoDriver();
}

/// <summary>
/// Módulos Advantech ADAM-6000 de E/S digital (6050, 6051, 6052, 6055, 6060 y
/// 6066) por Modbus TCP propio, sin las DLL de Advantech: la misma idea que el
/// driver ZKTeco. El equipo no tiene usuario ni contraseña por Modbus y admite
/// pocas conexiones a la vez: el servidor mantiene una sola por módulo, por la
/// que sondea y da las órdenes.
/// </summary>
public sealed class AdamIoDriver : IIoModuleDriver
{
    public IReadOnlyList<IoModuleModel> Models => AdamModels.All;

    public TimeSpan MinBlinkInterval => AdamModule.MinBlinkInterval;

    public async Task<IoModuleInfo> ProbeAsync(IoConnectionInfo info, string? modelKey, CancellationToken ct = default)
    {
        await using var module = await AdamModule.ConnectAsync(info.Host, info.Port, modelKey, ct);
        // Una lectura completa: confirma que el mapa del modelo calza con el equipo.
        await module.ReadStateAsync(ct);
        return new IoModuleInfo(module.ReportedModel, module.Model);
    }

    public IIoModuleSession Open(IoConnectionInfo info, IoModuleModel model, TimeSpan pollInterval) =>
        new AdamSession(new AdamModule(info.Host, info.Port, model), pollInterval);
}

/// <summary>Conexión viva: el módulo y su monitor, detrás del contrato de Core.</summary>
internal sealed class AdamSession : IIoModuleSession
{
    private readonly AdamModule _module;
    private readonly AdamMonitor _monitor;

    public AdamSession(AdamModule module, TimeSpan pollInterval)
    {
        _module = module;
        _monitor = new AdamMonitor(module, pollInterval);
        _monitor.StateChanged += state => StateChanged?.Invoke(state);
        _monitor.ChannelChanged += change => ChannelChanged?.Invoke(change);
        _monitor.ConnectionChanged += (online, error) => ConnectionChanged?.Invoke(online, error);
        _module.EffectFailed += (output, error) => OutputEffectFailed?.Invoke(output, error);
    }

    public IoModuleModel Model => _module.Model;
    public IoState? LastState => _monitor.LastState;
    public bool? IsOnline => _monitor.IsOnline;

    public event Action<IoState>? StateChanged;
    public event Action<IoChannelChange>? ChannelChanged;
    public event Action<bool, string?>? ConnectionChanged;
    public event Action<int, string>? OutputEffectFailed;

    public void Start() => _monitor.Start();
    public void RefreshNow() => _monitor.RefreshNow();

    public Task SetOutputAsync(int output, bool on, CancellationToken ct = default) => _module.SetOutputAsync(output, on, ct);
    public Task<bool> ToggleOutputAsync(int output, CancellationToken ct = default) => _module.ToggleOutputAsync(output, ct);
    public Task PulseOutputAsync(int output, TimeSpan duration, CancellationToken ct = default) =>
        _module.PulseOutputAsync(output, duration, ct);
    public Task BlinkOutputAsync(int output, TimeSpan interval, TimeSpan duration, CancellationToken ct = default) =>
        _module.BlinkOutputAsync(output, interval, duration, ct);
    public Task SetAllOutputsAsync(bool on, CancellationToken ct = default) => _module.SetAllOutputsAsync(on, ct);

    public async ValueTask DisposeAsync()
    {
        // Primero el sondeo (que no lea un módulo cerrado), después el módulo,
        // que suelta los pulsos y parpadeos en curso.
        await _monitor.DisposeAsync();
        await _module.DisposeAsync();
    }
}
