using System.Text.RegularExpressions;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Descubrimiento de equipos en la red local. SADP (Hikvision) por UDP
/// multicast: solo encuentra equipos del mismo segmento L2 que el servidor.
/// Este es el administrador de equipos de VIDEO: los controles de acceso,
/// intercomunicadores, alarmas y switches que responden al sondeo se filtran.
/// </summary>
public static partial class DiscoveryApi
{
    public static void MapDiscoveryApi(this WebApplication app)
    {
        app.MapGet("/api/discovery/sadp", async (HttpContext ctx, ILogger<Program> logger, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var found = await SadpDiscovery.ScanAsync(TimeSpan.FromSeconds(4), logger, ct);

            var video = found
                .Select(d => new { Device = d, Category = Categorize(d.Model) })
                .Where(x => x.Category is not null)
                .Select(x => new
                {
                    x.Device.Ip,
                    x.Device.CommandPort,
                    x.Device.HttpPort,
                    x.Device.Model,
                    x.Device.Serial,
                    x.Device.Mac,
                    x.Device.Activated,
                    Category = x.Category!,
                })
                .ToList();
            return Results.Ok(video);
        });
    }

    /// <summary>
    /// Clasifica por el modelo Hikvision; null = no es un equipo de video (se
    /// oculta en este administrador). Prefijos: DS-K* control de acceso e
    /// intercomunicación, DS-P*/AX alarmas, DS-3* switches, DS-2* cámaras,
    /// *NI-* NVR, H(Q|U|G|T)HI DVR Turbo, DS-64/69 decodificadores.
    /// </summary>
    private static string? Categorize(string model)
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

    [GeneratedRegex(@"^I?DS-(96|95|77|76)\d")]
    private static partial Regex NvrSeries();

    [GeneratedRegex(@"H[QUGT]HI")]
    private static partial Regex DvrTurbo();

    [GeneratedRegex(@"^I?DS-7[123]\d")]
    private static partial Regex DvrSeries();

    [GeneratedRegex(@"^DS-6[49]\d")]
    private static partial Regex Decoder();
}
