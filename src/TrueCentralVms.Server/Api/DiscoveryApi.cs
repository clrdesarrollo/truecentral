using System.Net;
using System.Text.RegularExpressions;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Descubrimiento de equipos en la red local, al estilo "Online Device":
/// SADP (Hikvision, UDP 37020), DHDiscover (Dahua, UDP 37810) y WS-Discovery
/// (ONVIF, UDP 3702) corren en paralelo y el resultado se unifica por IP —
/// si un equipo responde por su protocolo de fábrica y además por ONVIF, gana
/// la entrada de fábrica (trae serie, puertos y estado de activación).
/// Solo se listan equipos de VIDEO: controles de acceso, intercomunicadores,
/// alarmas y switches se filtran.
///
/// Limitación común: los sondeos son multicast/broadcast y no cruzan routers
/// ni VPN; solo se ve el segmento L2 del servidor. DHDiscover acepta además
/// un sondeo unicast dirigido (?host=IP) para equipos Dahua remotos.
/// </summary>
public static partial class DiscoveryApi
{
    public static void MapDiscoveryApi(this WebApplication app)
    {
        app.MapGet("/api/discovery/scan", async (HttpContext ctx, ILogger<Program> logger, string? host, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;

            IPAddress? directed = null;
            if (!string.IsNullOrWhiteSpace(host) && !IPAddress.TryParse(host.Trim(), out directed))
                return Results.Json(new { error = "El parámetro host debe ser una dirección IP." },
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            var window = TimeSpan.FromSeconds(4);
            var sadpTask = SadpDiscovery.ScanAsync(window, logger, ct);
            var dahuaTask = DahuaDiscovery.ScanAsync(window, logger, directed, ct);
            var onvifTask = WsDiscovery.ScanAsync(window, logger, ct);
            await Task.WhenAll(sadpTask, dahuaTask, onvifTask);

            var rows = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var d in sadpTask.Result)
            {
                if (CategorizeHikvision(d.Model) is not { } category) continue;
                rows[d.Ip] = new
                {
                    d.Ip, Brand = "Hikvision", DriverKey = "hikvision-netsdk",
                    d.CommandPort, d.HttpPort, d.Model, d.Serial, d.Mac, d.Activated, Category = category,
                };
            }

            foreach (var d in dahuaTask.Result)
            {
                if (CategorizeDahua(d.DeviceClass, d.Model) is not { } category) continue;
                rows[d.Ip] = new
                {
                    d.Ip, Brand = "Dahua", DriverKey = "dahua-netsdk",
                    CommandPort = d.SdkPort, d.HttpPort, d.Model, d.Serial, d.Mac,
                    Activated = true, Category = category,
                };
            }

            foreach (var d in onvifTask.Result)
            {
                if (rows.ContainsKey(d.Ip)) continue; // ya visto por su protocolo de fábrica
                rows[d.Ip] = new
                {
                    d.Ip, Brand = "ONVIF", DriverKey = "onvif",
                    CommandPort = d.HttpPort, d.HttpPort,
                    Model = d.Hardware.Length > 0 ? d.Hardware : d.Name,
                    Serial = "", Mac = "", Activated = true, Category = "Cámara",
                };
            }

            return Results.Ok(rows.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Select(r => r.Value));
        });
    }

    /// <summary>
    /// Clasifica por el modelo Hikvision; null = no es un equipo de video (se
    /// oculta en este administrador). Prefijos: DS-K* control de acceso e
    /// intercomunicación, DS-P*/AX alarmas, DS-3* switches, DS-2* cámaras,
    /// *NI-* NVR, H(Q|U|G|T)HI DVR Turbo, DS-64/69 decodificadores.
    /// </summary>
    private static string? CategorizeHikvision(string model)
    {
        string m = (model ?? "").Trim().ToUpperInvariant();
        if (m.Length == 0) return "Otro";

        if (m.StartsWith("DS-K") || m.StartsWith("IDS-K")) return null;                   // control de acceso / intercom
        if (m.StartsWith("DS-P") || m.StartsWith("AX ") || m.StartsWith("AX-")) return null; // alarmas
        if (m.StartsWith("DS-3")) return null;                                            // switches
        if (m.StartsWith("DS-1")) return null;                                            // teclados/accesorios

        if (m.Contains("NVR") || m.Contains("NI-") || NvrSeries().IsMatch(m)) return "NVR";
        if (DvrTurbo().IsMatch(m) || m.Contains("DVR") || DvrSeries().IsMatch(m)) return "DVR";
        if (Decoder().IsMatch(m)) return "Decodificador";
        if (m.StartsWith("DS-2") || m.StartsWith("IDS-2") || m.StartsWith("IPC")) return "Cámara";
        return "Otro";
    }

    /// <summary>
    /// Clasifica por la clase que anuncia DHDiscover (con el modelo como
    /// respaldo); null = no es un equipo de video. Clases Dahua: IPC/SD
    /// cámaras (SD = domo PTZ), NVR/EVS grabadores IP, DVR/HCVR/XVR híbridos,
    /// NVD decodificadores, VT* intercomunicación, AS* control de acceso,
    /// AR* alarmas.
    /// </summary>
    private static string? CategorizeDahua(string deviceClass, string model)
    {
        string c = (deviceClass ?? "").Trim().ToUpperInvariant();
        string m = (model ?? "").Trim().ToUpperInvariant();

        if (c.StartsWith("VT") || m.StartsWith("VT")) return null;   // intercomunicación
        if (c.StartsWith("AS") || m.StartsWith("ASI") || m.StartsWith("ASA")) return null; // control de acceso
        if (c.StartsWith("AR")) return null;                         // alarmas

        if (c is "IPC" or "SD") return "Cámara";
        if (c is "NVR" or "EVS") return "NVR";
        if (c is "DVR" or "HCVR" or "XVR" or "MCVR") return "DVR";
        if (c is "NVD") return "Decodificador";

        if (m.Contains("IPC") || m.Contains("-SD")) return "Cámara";
        if (m.Contains("NVR")) return "NVR";
        if (m.Contains("XVR") || m.Contains("HCVR") || m.Contains("DVR")) return "DVR";
        return "Otro";
    }

    [GeneratedRegex(@"^I?DS-(96|95|77|76)\d")]
    private static partial Regex NvrSeries();

    [GeneratedRegex(@"H[QUGT]HI")]
    private static partial Regex DvrTurbo();

    [GeneratedRegex(@"^I?DS-7[123]\d")]
    private static partial Regex DvrSeries();

    [GeneratedRegex(@"^DS-6[49]\d")]
    private static partial Regex Decoder();
}
