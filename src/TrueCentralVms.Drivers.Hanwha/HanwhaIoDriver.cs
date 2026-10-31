using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Hanwha;

public sealed class HanwhaIoDriverFactory : IIoModuleDriverFactory
{
    public string DriverKey => HanwhaIoDriver.DriverKey;
    public string DisplayName => "Hanwha / Wisenet: entradas y salidas de alarma (SUNAPI)";
    public int DefaultPort => 80;
    /// <summary>Lo crea el servidor con la cámara o el grabador (sus partes), no se agrega a mano.</summary>
    public bool Internal => true;
    public IIoModuleDriver Create() => new HanwhaIoDriver();
}

/// <summary>
/// Entradas y salidas de alarma de un equipo Wisenet por SUNAPI: cuáles tiene
/// (<c>io.cgi alarminput/alarmoutput view</c>), su estado en una sola consulta
/// (<c>eventstatus.cgi monitor</c>: "AlarmInput.1=True", "AlarmOutput.1=False")
/// y la orden a una salida (<c>io.cgi alarmoutput control … State=On|Off</c>).
/// El equipo numera desde 1; el VMS, desde 0.
/// </summary>
public sealed class HanwhaIoDriver : IIoModuleDriver
{
    public const string DriverKey = "hanwha-sunapi-io";

    public IReadOnlyList<IoModuleModel> Models { get; } = PollingIoSession.PartModels("Equipo Hanwha / Wisenet (SUNAPI)");

    public TimeSpan MinBlinkInterval => TimeSpan.FromSeconds(1);

    internal static DeviceConnectionInfo Connection(IoConnectionInfo info) => new(info.Host, info.Port, info.Username, info.Password);

    /// <summary>Cuántas entradas y salidas tiene (0 si el modelo no las trae: SUNAPI responde NG).</summary>
    internal static async Task<(int Inputs, int Outputs)> CountAsync(SunapiClient client, CancellationToken ct)
    {
        async Task<int> Count(string submenu, string prefix)
        {
            try
            {
                var values = await client.GetAsync("io.cgi", $"msubmenu={submenu}&action=view", ct);
                return values.Keys.Select(k => k.Split('.'))
                    .Where(p => p.Length >= 2 && p[0].Equals(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(p[1], out _))
                    .Select(p => int.Parse(p[1])).Distinct().Count(n => n >= 1);
            }
            catch (SunapiErrorException) { return 0; }
        }
        return (Math.Min(await Count("alarminput", "AlarmInput"), 16), Math.Min(await Count("alarmoutput", "AlarmOutput"), 16));
    }

    public async Task<IoModuleInfo> ProbeAsync(IoConnectionInfo info, string? modelKey, CancellationToken ct = default)
    {
        using var client = new SunapiClient(Connection(info));
        var (inputs, outputs) = await CountAsync(client, ct);
        if (inputs + outputs == 0) throw new DriverException("El equipo Hanwha no tiene entradas ni salidas de alarma.");
        string reported = $"isapi-{inputs}x{outputs}";
        return new IoModuleInfo(reported, Models.First(m => m.Key == (modelKey ?? reported)));
    }

    public IIoModuleSession Open(IoConnectionInfo info, IoModuleModel model, TimeSpan pollInterval) =>
        new HanwhaIoSession(Connection(info), model, pollInterval);
}

internal sealed class HanwhaIoSession(DeviceConnectionInfo info, IoModuleModel model, TimeSpan pollInterval)
    : PollingIoSession(model, pollInterval)
{
    private readonly SunapiClient _client = new(info, TimeSpan.FromSeconds(5));

    protected override async Task<(bool[]? Inputs, bool[]? Outputs)> ReadAsync(CancellationToken ct)
    {
        var values = await _client.GetAsync("eventstatus.cgi", "msubmenu=eventstatus&action=monitor", ct);
        return (States(values, "AlarmInput", Model.Inputs), States(values, "AlarmOutput", Model.Outputs));
    }

    /// <summary>"AlarmInput.2=True" → posición 1 en true. Null si el equipo no informa ninguna.</summary>
    internal static bool[]? States(Dictionary<string, string> values, string prefix, int count)
    {
        var result = new bool[count];
        bool any = false;
        for (int n = 1; n <= count; n++)
            if (values.TryGetValue($"{prefix}.{n}", out string? v) || values.TryGetValue($"{prefix}.{n}.State", out v))
            {
                any = true;
                result[n - 1] = v.Equals("True", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("On", StringComparison.OrdinalIgnoreCase);
            }
        return any ? result : null;
    }

    protected override async Task WriteAsync(int output, bool on, CancellationToken ct) =>
        await _client.GetAsync("io.cgi", $"msubmenu=alarmoutput&action=control&AlarmOutput={output + 1}&State={(on ? "On" : "Off")}", ct);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _client.Dispose();
    }
}
