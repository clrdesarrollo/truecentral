using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services.Network;

/// <summary>Ajustes de Sistema → Acceso desde Internet (SystemBlobs "wan.settings").</summary>
public sealed class WanSettings
{
    public bool Enabled { get; set; }
    /// <summary>IP pública fija o nombre DNS (DDNS) con que se llega al router del cliente.</summary>
    public string PublicHost { get; set; } = "";
    /// <summary>Redes desde las que un equipo cuenta como LOCAL (CIDR). El resto es Internet.</summary>
    public List<string> LocalNetworks { get; set; } = [];
    /// <summary>Escenarios habilitados (<see cref="WanAccessService.Scenarios"/>).</summary>
    public List<string> Scenarios { get; set; } = [];
    /// <summary>Puerto público por puerto (clave del catálogo); falta = el mismo que el interno.</summary>
    public Dictionary<string, int> PublicPorts { get; set; } = [];
    public DateTime? UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// Acceso desde Internet: dirección pública, redes locales, escenarios y puertos.
///
/// Mismo criterio que Milestone (dirección pública + rangos locales): quien se
/// conecta desde una red local recibe las direcciones internas; el resto, la
/// dirección pública con el puerto que el router reenvía. Apagado (por omisión)
/// todo funciona como antes y nadie cuenta como remoto.
///
/// Además, propio: con el acceso encendido, solo entran desde fuera de las
/// redes locales los usuarios cuyo rol tiene «Entrar desde Internet».
/// </summary>
public sealed class WanAccessService(IServiceScopeFactory scopes, IConfiguration config, ILogger<WanAccessService> logger)
{
    public const string SettingsBlob = "wan.settings";

    public const string ScenarioOperators = "operators";
    public const string ScenarioIsup = "isup";
    public const string ScenarioSia = "sia";
    public const string ScenarioHttpAlarms = "http-alarms";
    public const string ScenarioScreens = "screens";
    public const string ScenarioCerco = "cerco";

    /// <summary>Escenario: clave, nombre y para qué sirve.</summary>
    public sealed record Scenario(string Key, string Name, string Description);

    public static readonly IReadOnlyList<Scenario> Scenarios =
    [
        new(ScenarioOperators, "Operadores remotos", "Panel web y cliente de escritorio desde otra sede o desde la casa, con video en vivo y grabaciones."),
        new(ScenarioScreens, "Pantallas y parlantes en otra sede", "Pantallas de alarma y agentes TrueCentral Speaker que se conectan desde otra red."),
        new(ScenarioIsup, "Equipos por ISUP", "Grabadores, cámaras, paneles y terminales Hikvision que se registran por ISUP desde otra red."),
        new(ScenarioSia, "Paneles de alarma por SIA DC-09", "Paneles que reportan al centro receptor de alarmas propio."),
        new(ScenarioHttpAlarms, "Paneles de alarma por HTTP", "Paneles que envían sus eventos por notificación HTTP."),
        new(ScenarioCerco, "Cercos eléctricos", "Paneles de cerco que se conectan al receptor y bajan su firmware."),
    ];

    /// <summary>Un puerto que hay que reenviar en el router. <paramref name="FixedPublic"/>: el público debe ser el mismo.</summary>
    public sealed record PortInfo(string Key, string Name, string Protocol, int Internal, bool FixedPublic, string[] Scenarios);

    /// <summary>Catálogo de puertos con sus valores reales de esta instalación.</summary>
    public IReadOnlyList<PortInfo> Ports()
    {
        var isup = config.GetSection("Isup");
        int[] registration = isup.GetSection("RegistrationPorts").GetChildren()
            .Select(c => int.TryParse(c.Value, out var p) ? p : 0).Where(p => p > 0).ToArray();
        if (registration.Length == 0) registration = [7660, 7661];
        var list = new List<PortInfo>
        {
            new("https", "Panel web, clientes, pantallas y parlantes (HTTPS)", "TCP", config.GetValue("Web:HttpsPort", 443), false,
                [ScenarioOperators, ScenarioScreens]),
            new("rtsp", "Video hacia el cliente de escritorio (RTSP)", "TCP", config.GetValue("Streaming:RtspPort", 8654), false,
                [ScenarioOperators]),
            // El candidato ICE lleva el puerto del servidor: el router debe reenviar el MISMO número.
            new("webrtc", "Video del panel web (WebRTC)", "UDP y TCP", config.GetValue("Streaming:WebRtcIcePort", 8660), true,
                [ScenarioOperators]),
            new("http", "Equipos sin cifrado (HTTP: avisos de paneles, fotos para terminales ISUP)", "TCP",
                config.GetValue("Web:HttpPort", 80), false, [ScenarioIsup, ScenarioHttpAlarms]),
        };
        for (int i = 0; i < registration.Length; i++)
            list.Add(new($"isup-reg-{i}", $"ISUP: registro de equipos{(registration.Length > 1 ? $" ({i + 1})" : "")}", "TCP y UDP",
                registration[i], false, [ScenarioIsup]));
        // Estos los anuncia el servicio ISUP a los equipos con su número: van fijos.
        list.Add(new("isup-alarm", "ISUP: alarmas", "TCP", isup.GetValue("AlarmPort", 7662), true, [ScenarioIsup]));
        list.Add(new("isup-stream", "ISUP: video en vivo", "TCP", isup.GetValue("StreamPort", 7664), true, [ScenarioIsup]));
        list.Add(new("isup-playback", "ISUP: reproducción", "TCP", isup.GetValue("PlaybackPort", 7665), true, [ScenarioIsup]));
        list.Add(new("isup-voice", "ISUP: voz (parlantes y citofonía)", "TCP", isup.GetValue("VoicePort", 7666), true, [ScenarioIsup]));
        if (isup.GetValue("PictureServerPort", 7667) is > 0 and var pictures)
            list.Add(new("isup-pictures", "ISUP: fotos de los eventos", "TCP", pictures, true, [ScenarioIsup]));
        list.Add(new("sia", "Centro receptor de alarmas (SIA DC-09)", "TCP", config.GetValue("Alarms:Receiver:Port", 5091), false,
            [ScenarioSia]));
        list.Add(new("cerco", "Receptor de cercos eléctricos", "TCP", config.GetValue("Cerco:Receiver:Port", 5092), false,
            [ScenarioCerco]));
        list.Add(new("cerco-fw", "Firmware de los cercos (OTA)", "TCP", config.GetValue("Cerco:Firmware:Port", 5093), true,
            [ScenarioCerco]));
        // Un servicio apagado (puerto 0) no se reenvía.
        return list.Where(p => p.Internal > 0).ToList();
    }

    /// <summary>Redes privadas que se proponen como locales.</summary>
    public static readonly string[] DefaultLocalNetworks = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"];

    private readonly Lock _sync = new();
    private WanSettings? _settings;
    private List<(IPAddress Network, int Prefix)> _networks = [];

    /// <summary>Ajustes vigentes (se leen de la base la primera vez).</summary>
    public WanSettings Current
    {
        get
        {
            lock (_sync)
            {
                if (_settings is not null) return _settings;
                try
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
                    string? data = db.SystemBlobs.AsNoTracking().Where(b => b.Name == SettingsBlob).Select(b => b.Data).FirstOrDefault();
                    Apply(data is null ? Defaults() : JsonSerializer.Deserialize<WanSettings>(data) ?? Defaults());
                }
                catch (Exception ex)
                {
                    // Base todavía sin migrar o ilegible: apagado (como antes), sin cachear para volver a intentar.
                    logger.LogDebug(ex, "Acceso desde Internet: no se pudieron leer los ajustes.");
                    return Defaults();
                }
                return _settings!;
            }
        }
    }

    public bool Enabled => Current.Enabled && Current.PublicHost.Length > 0;
    public bool Has(string scenario) => Enabled && Current.Scenarios.Contains(scenario);
    public string PublicHost => Current.PublicHost;

    /// <summary>Puerto público de un puerto del catálogo (el interno si no se cambió o si debe ser fijo).</summary>
    public int PublicPort(string key)
    {
        var port = Ports().FirstOrDefault(p => p.Key == key);
        if (port is null) return 0;
        return !port.FixedPublic && Current.PublicPorts.TryGetValue(key, out int p) && p is > 0 and <= 65535 ? p : port.Internal;
    }

    /// <summary>¿Esta conexión viene de fuera de las redes locales? (apagado: nunca)</summary>
    public bool IsRemote(IPAddress? address)
    {
        if (!Enabled || address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        List<(IPAddress Network, int Prefix)> networks;
        lock (_sync) networks = _networks;
        return !networks.Any(n => InNetwork(address, n.Network, n.Prefix));
    }

    public async Task SaveAsync(WanSettings settings, CancellationToken ct)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var row = await db.SystemBlobs.FirstOrDefaultAsync(b => b.Name == SettingsBlob, ct);
            if (row is null) db.SystemBlobs.Add(row = new SystemBlob { Name = SettingsBlob });
            row.Data = JsonSerializer.Serialize(settings);
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        lock (_sync) Apply(settings);
    }

    /// <summary>"10.0.0.0/8" → red y prefijo; null si no es válida.</summary>
    public static (IPAddress Network, int Prefix)? ParseCidr(string text)
    {
        string[] parts = text.Trim().Split('/');
        if (!IPAddress.TryParse(parts[0], out var ip)) return null;
        int max = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        int prefix = max;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > max)) return null;
        if (parts.Length > 2) return null;
        return (ip, prefix);
    }

    private WanSettings Defaults() => new()
    {
        LocalNetworks = [.. DefaultLocalNetworks],
        Scenarios = [ScenarioOperators],
    };

    private void Apply(WanSettings settings)
    {
        _settings = settings;
        _networks = settings.LocalNetworks.Select(ParseCidr).Where(n => n is not null).Select(n => n!.Value).ToList();
    }

    private static bool InNetwork(IPAddress ip, IPAddress network, int prefix)
    {
        if (ip.AddressFamily != network.AddressFamily) return false;
        byte[] a = ip.GetAddressBytes(), n = network.GetAddressBytes();
        for (int i = 0; i < a.Length; i++)
        {
            int bits = Math.Clamp(prefix - i * 8, 0, 8);
            int mask = (0xFF << (8 - bits)) & 0xFF;
            if ((a[i] & mask) != (n[i] & mask)) return false;
        }
        return true;
    }
}
