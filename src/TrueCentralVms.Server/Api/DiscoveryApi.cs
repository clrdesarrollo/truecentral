using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Drivers.Dahua;
using TrueCentralVms.Drivers.Hikvision;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Descubrimiento de equipos en la red local, al estilo "Online Device":
/// SADP (Hikvision, UDP 37020), DHDiscover (Dahua, UDP 37810) y WS-Discovery
/// (ONVIF, UDP 3702) corren en paralelo y el resultado se unifica por IP —
/// si un equipo responde por su protocolo de fábrica y además por ONVIF, gana
/// la entrada de fábrica (trae serie, puertos y estado de activación).
/// Por omisión solo se listan equipos de VIDEO: controles de acceso,
/// intercomunicadores, alarmas y switches se filtran. Con ?kind= se pide otra
/// familia: <c>decoders</c> (decodificadores de muro) o <c>access</c> (control
/// de acceso, y ahí solo lo que el módulo sabe manejar).
///
/// Limitación común: los sondeos son multicast/broadcast y no cruzan routers
/// ni VPN; solo se ve el segmento L2 del servidor. DHDiscover acepta además
/// un sondeo unicast dirigido (?host=IP) para equipos Dahua remotos.
/// </summary>
public static partial class DiscoveryApi
{
    public static void MapDiscoveryApi(this WebApplication app)
    {
        app.MapGet("/api/discovery/scan", async (HttpContext ctx, ILogger<Program> logger,
            Services.AuditService audit, Data.VmsDbContext db, string? host, string? kind, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out var session) is { } failure) return failure;
            // kind=decoders: solo decodificadores de muro (Hikvision DS-64/69/C10, Dahua NVD);
            // el resto de los equipos de video se omite (lo usa la página Decodificadores).
            bool decodersOnly = string.Equals(kind, "decoders", StringComparison.OrdinalIgnoreCase);
            // kind=access: solo equipos de control de acceso COMPATIBLES (los que
            // el módulo sabe administrar); lo usa la página Control de acceso.
            bool accessOnly = string.Equals(kind, "access", StringComparison.OrdinalIgnoreCase);

            // El panel repite el sondeo solo cada 30 s mientras la página está
            // abierta: se audita una vez por usuario cada 10 min.
            if (audit.ShouldLog($"scan:{session.UserId}", TimeSpan.FromMinutes(10)))
                await audit.LogAsync(ctx, accessOnly ? "access" : "devices", "discovery-scan",
                    detail: accessOnly
                        ? "Sondeó la red en busca de equipos de control de acceso (SADP)."
                        : decodersOnly
                            ? "Sondeó la red en busca de decodificadores de muro (SADP, DHDiscover)."
                            : "Sondeó la red en busca de equipos de video (SADP, DHDiscover, WS-Discovery).");

            IPAddress? directed = null;
            if (!string.IsNullOrWhiteSpace(host) && !IPAddress.TryParse(host.Trim(), out directed))
                return Results.Json(new { error = "El parámetro host debe ser una dirección IP." },
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            var window = TimeSpan.FromSeconds(4);
            var sadpTask = SadpDiscovery.ScanAsync(window, logger, ct);
            var dahuaTask = DahuaDiscovery.ScanAsync(window, logger, directed, ct);
            // Cada familia se pregunta solo donde puede contestar algo útil:
            // WS-Discovery no distingue equipos de control de acceso, y ZKTeco
            // no aparece en ningún otro sondeo (habla su propio protocolo).
            var onvifTask = accessOnly ? Task.FromResult(new List<OnvifDiscoveredDto>()) : WsDiscovery.ScanAsync(window, logger, ct);
            var knownZk = accessOnly
                ? (await db.AccessDevices.AsNoTracking().Where(d => d.DriverKey == "zkteco-tcp")
                    .Select(d => d.Host).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            var zkTask = accessOnly
                ? ZkDiscovery.ScanAsync(window, logger, directed, knownZk, ct)
                : Task.FromResult(new List<ZkDiscoveredDto>());
            await Task.WhenAll(sadpTask, dahuaTask, onvifTask, zkTask);

            var rows = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var d in sadpTask.Result)
            {
                string? category = accessOnly ? CategorizeAccess(d.Model) : CategorizeHikvision(d.Model);
                if (category is null) continue;
                if (decodersOnly && category != "Decodificador") continue;
                rows[d.Ip] = new
                {
                    d.Ip, Brand = "Hikvision",
                    // El control de acceso se administra por ISAPI (puerto HTTP), no por el SDK.
                    DriverKey = accessOnly ? "hikvision-isapi" : "hikvision-netsdk",
                    d.CommandPort, d.HttpPort, d.Model, d.Serial, d.Mac, d.Activated, Category = category,
                };
            }

            foreach (var d in dahuaTask.Result)
            {
                string? category = accessOnly
                    ? CategorizeDahuaAccess(d.DeviceClass, d.Model)
                    : CategorizeDahua(d.DeviceClass, d.Model);
                if (category is null) continue;
                if (decodersOnly && category != "Decodificador") continue;
                rows[d.Ip] = new
                {
                    d.Ip, Brand = "Dahua",
                    // El control de acceso se administra por el CGI HTTP del equipo, no por el SDK.
                    DriverKey = accessOnly ? "dahua-http" : "dahua-netsdk",
                    CommandPort = d.SdkPort, d.HttpPort, d.Model, d.Serial, d.Mac,
                    Activated = true, Category = category,
                };
            }

            foreach (var d in zkTask.Result)
            {
                if (rows.ContainsKey(d.Ip)) continue;   // ya lo anunció su marca
                rows[d.Ip] = new
                {
                    d.Ip, Brand = "ZKTeco", DriverKey = "zkteco-tcp",
                    CommandPort = d.Port, HttpPort = d.Port,
                    // Sin la clave de comunicación de fábrica el equipo no se
                    // identifica: se lista igual, con lo que se sabe de él.
                    Model = d.Model.Length > 0 ? d.Model : "ZKTeco (sin identificar)",
                    d.Serial, d.Mac, Activated = true, Category = "Terminal",
                };
            }

            foreach (var d in onvifTask.Result)
            {
                if (decodersOnly) break;               // WS-Discovery no distingue decodificadores
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
    /// Etiqueta del equipo de control de acceso Dahua; null = no lo es o el
    /// módulo todavía no lo maneja. La clase que anuncia DHDiscover sirve de
    /// respaldo cuando el modelo llega vacío.
    /// </summary>
    private static string? CategorizeDahuaAccess(string deviceClass, string model) =>
        (DahuaAccessDriver.ClassifyModel(model) ?? DahuaAccessDriver.ClassifyModel(deviceClass)) switch
        {
            AccessDeviceKind.Terminal => "Terminal",
            AccessDeviceKind.Controller => "Controladora",
            AccessDeviceKind.Turnstile => "Torniquete",
            _ => null,
        };

    /// <summary>
    /// Etiqueta del equipo de control de acceso; null = no es de control de
    /// acceso o el módulo todavía no lo maneja (intercomunicación y cerraduras
    /// autónomas): el administrador muestra SOLO lo compatible para no ofrecer
    /// altas que fallarían.
    /// </summary>
    private static string? CategorizeAccess(string model) => HikvisionAccessDriver.ClassifyModel(model) switch
    {
        AccessDeviceKind.Terminal => "Terminal",
        AccessDeviceKind.Controller => "Controladora",
        AccessDeviceKind.Turnstile => "Torniquete",
        _ => null,
    };

    /// <summary>
    /// Clasifica por el modelo Hikvision; null = no es un equipo de video (se
    /// oculta en este administrador). Prefijos: DS-K* control de acceso e
    /// intercomunicación, DS-P*/AX alarmas, DS-3* switches, DS-2* cámaras,
    /// *NI-* NVR, H(Q|U|G|T)HI DVR Turbo, DS-64/69 y DS-C10 decodificadores/controladores de muro.
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

    [GeneratedRegex(@"^DS-(6[49]\d|C1\d)")]
    private static partial Regex Decoder();
}
