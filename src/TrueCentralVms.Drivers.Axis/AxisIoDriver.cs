using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Axis;

public sealed class AxisIoDriverFactory : IIoModuleDriverFactory
{
    public string DriverKey => AxisIoDriver.DriverKey;
    public string DisplayName => "Axis: entradas y salidas (VAPIX)";
    public int DefaultPort => 80;
    /// <summary>Lo crea el servidor con la cámara (sus partes), no se agrega a mano.</summary>
    public bool Internal => true;
    public IIoModuleDriver Create() => new AxisIoDriver();
}

/// <summary>
/// Entradas y salidas de una cámara, encoder o equipo de E/S Axis por VAPIX.
/// Con AXIS OS los puertos son configurables (param IOPort.I0..In, cada uno
/// entrada o salida, numerados 1..N en port.cgi); los firmware viejos tienen
/// entradas y salidas fijas con su propia numeración (input.cgi/output.cgi).
/// Estado: <c>port.cgi?checkactive=1,2</c> → "port1=active". Orden:
/// <c>port.cgi?action=2:/</c> (activar) o <c>2:\</c> (soltar).
/// </summary>
public sealed class AxisIoDriver : IIoModuleDriver
{
    public const string DriverKey = "axis-vapix-io";

    public IReadOnlyList<IoModuleModel> Models { get; } = PollingIoSession.PartModels("Equipo Axis (VAPIX)");

    public TimeSpan MinBlinkInterval => TimeSpan.FromSeconds(1);

    /// <summary>Cómo se direccionan los puertos del equipo.</summary>
    internal sealed record PortMap(IReadOnlyList<int> InputPorts, IReadOnlyList<int> OutputPorts, bool Unified);

    internal static async Task<PortMap> ReadMapAsync(AxisVapixClient client, CancellationToken ct)
    {
        var ports = await client.GetParamsAsync("IOPort", ct);
        var inputs = new List<int>();
        var outputs = new List<int>();
        foreach (var (key, value) in ports)
        {
            string[] parts = key.Split('.');
            if (parts.Length != 3 || !parts[2].Equals("Direction", StringComparison.OrdinalIgnoreCase) ||
                parts[1].Length < 2 || !int.TryParse(parts[1][1..], out int index)) continue;
            (value.Equals("output", StringComparison.OrdinalIgnoreCase) ? outputs : inputs).Add(index + 1);
        }
        if (inputs.Count + outputs.Count > 0)
            return new PortMap(inputs.Order().Take(16).ToList(), outputs.Order().Take(16).ToList(), Unified: true);

        var legacyIn = await client.GetParamsAsync("Input", ct);
        var legacyOut = await client.GetParamsAsync("Output", ct);
        int nIn = int.TryParse(legacyIn.GetValueOrDefault("Input.NbrOfInputs"), out int a) ? a : 0;
        int nOut = int.TryParse(legacyOut.GetValueOrDefault("Output.NbrOfOutputs"), out int b) ? b : 0;
        return new PortMap(Enumerable.Range(1, Math.Min(nIn, 16)).ToList(), Enumerable.Range(1, Math.Min(nOut, 16)).ToList(), Unified: false);
    }

    internal static DeviceConnectionInfo Connection(IoConnectionInfo info) => new(info.Host, info.Port, info.Username, info.Password);

    public async Task<IoModuleInfo> ProbeAsync(IoConnectionInfo info, string? modelKey, CancellationToken ct = default)
    {
        using var client = new AxisVapixClient(Connection(info));
        var map = await ReadMapAsync(client, ct);
        if (map.InputPorts.Count + map.OutputPorts.Count == 0) throw new DriverException("El equipo Axis no tiene entradas ni salidas.");
        string reported = $"isapi-{map.InputPorts.Count}x{map.OutputPorts.Count}";
        return new IoModuleInfo(reported, Models.First(m => m.Key == (modelKey ?? reported)));
    }

    public IIoModuleSession Open(IoConnectionInfo info, IoModuleModel model, TimeSpan pollInterval) =>
        new AxisIoSession(Connection(info), model, pollInterval);
}

internal sealed class AxisIoSession(DeviceConnectionInfo info, IoModuleModel model, TimeSpan pollInterval)
    : PollingIoSession(model, pollInterval)
{
    private readonly AxisVapixClient _client = new(info, TimeSpan.FromSeconds(5));
    private AxisIoDriver.PortMap? _map;

    private async Task<AxisIoDriver.PortMap> MapAsync(CancellationToken ct) => _map ??= await AxisIoDriver.ReadMapAsync(_client, ct);

    protected override async Task<(bool[]? Inputs, bool[]? Outputs)> ReadAsync(CancellationToken ct)
    {
        var map = await MapAsync(ct);
        if (map.Unified)
        {
            var all = map.InputPorts.Concat(map.OutputPorts).ToList();
            var states = await CheckAsync($"axis-cgi/io/port.cgi?checkactive={string.Join(',', all)}", "port", ct);
            return (map.InputPorts.Select(p => states.GetValueOrDefault(p)).ToArray(),
                    map.OutputPorts.Select(p => states.GetValueOrDefault(p)).ToArray());
        }
        bool[]? inputs = null, outputs = null;
        if (map.InputPorts.Count > 0)
        {
            var states = await CheckAsync($"axis-cgi/io/input.cgi?checkactive={string.Join(',', map.InputPorts)}", "input", ct);
            inputs = map.InputPorts.Select(p => states.GetValueOrDefault(p)).ToArray();
        }
        if (map.OutputPorts.Count > 0)
        {
            var states = await CheckAsync($"axis-cgi/io/output.cgi?checkactive={string.Join(',', map.OutputPorts)}", "output", ct);
            outputs = map.OutputPorts.Select(p => states.GetValueOrDefault(p)).ToArray();
        }
        return (inputs, outputs);
    }

    /// <summary>"port1=active\nport2=inactive" → {1: true, 2: false}.</summary>
    private async Task<Dictionary<int, bool>> CheckAsync(string path, string prefix, CancellationToken ct)
    {
        string body = await _client.GetStringAsync(path, ct);
        if (body.TrimStart().StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            throw new DriverException($"La cámara Axis no entregó el estado de sus puertos: {body.Trim()}");
        return ParseCheck(body, prefix);
    }

    internal static Dictionary<int, bool> ParseCheck(string body, string prefix)
    {
        var result = new Dictionary<int, bool>();
        foreach (string raw in body.Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= prefix.Length || !line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(line[prefix.Length..eq], out int port)) continue;
            result[port] = line[(eq + 1)..].Trim().Equals("active", StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    protected override async Task WriteAsync(int output, bool on, CancellationToken ct)
    {
        var map = await MapAsync(ct);
        int port = map.OutputPorts[output];
        // "/" activa, "\" suelta (va codificada en la URL).
        string action = $"{port}:{(on ? "/" : "\\")}";
        string cgi = map.Unified ? "port.cgi" : "output.cgi";
        string body = await _client.GetStringAsync($"axis-cgi/io/{cgi}?action={Uri.EscapeDataString(action)}", ct);
        if (body.TrimStart().StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            throw new DriverException($"La cámara Axis rechazó la orden a la salida {output + 1}: {body.Trim()}");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _client.Dispose();
    }
}
