using System.Net;
using TrueCentralVms.Server.Services.Workflows;

namespace TrueCentralVms.Server.Services.Fleet;

/// <summary>
/// Teselas del mapa base en línea, intermediadas por el servidor: el navegador (y el cliente, que muestra el mismo panel) pide
/// <c>/api/fleet/map/tiles/{z}/{x}/{y}</c> con su sesión y el servidor las trae del proveedor con la plantilla guardada. Así la
/// clave del proveedor no sale del servidor, todos ven el mismo mapa y nadie puede usar la cuenta pagada por su lado; los
/// operadores tampoco necesitan salida a internet (solo el servidor).
///
/// La plantilla la escribe un administrador, pero se conecta con <see cref="OutboundAddressPolicy"/> igual que las
/// automatizaciones: no a loopback ni link-local (SSRF). Solo se devuelven imágenes: una página de error del proveedor no se
/// sirve bajo el origen del VMS. Sin caché en disco (las condiciones de los proveedores la limitan): el navegador guarda cada
/// tesela un día.
/// </summary>
public sealed class FleetTileProxy(FleetPositionService positions, ILogger<FleetTileProxy> logger) : IDisposable
{
    private const int MaxTileBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan TemplateTtl = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http = CreateClient();
    private volatile Tuple<string?, DateTime>? _template;   // (plantilla o null si el modo no es "raster", leída a las)
    private DateTime _lastFailureLogUtc = DateTime.MinValue;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient(OutboundAddressPolicy.CreateHandler(acceptAnyCertificate: false, allowRedirect: true))
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TrueCentralVMS-FleetMap/1.0");
        return http;
    }

    /// <summary>Los ajustes cambiaron: la próxima tesela vuelve a leer la plantilla.</summary>
    public void Invalidate() => _template = null;

    public sealed record Tile(byte[] Data, string ContentType);

    /// <summary>La tesela, o null con el código HTTP a devolver (404 sin mapa en línea, 502 si el proveedor falla).</summary>
    public async Task<(Tile? Tile, int Status)> GetAsync(int z, int x, int y, CancellationToken ct)
    {
        if (z is < 0 or > 22 || x < 0 || y < 0 || x >= 1 << z || y >= 1 << z) return (null, StatusCodes.Status400BadRequest);
        string? template = await TemplateAsync(ct);
        if (template is null) return (null, StatusCodes.Status404NotFound);
        string url = template.Replace("{z}", z.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString())
            .Replace("{s}", "abc"[(x + y) % 3].ToString()).Replace("{r}", "");
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return (null, StatusCodes.Status404NotFound);
            string type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!response.IsSuccessStatusCode || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                LogFailure($"el proveedor respondió {(int)response.StatusCode} ({(type.Length > 0 ? type : "sin tipo")})");
                return (null, StatusCodes.Status502BadGateway);
            }
            if (response.Content.Headers.ContentLength > MaxTileBytes) return (null, StatusCodes.Status502BadGateway);
            byte[] data = await response.Content.ReadAsByteArrayAsync(ct);
            return data.Length > MaxTileBytes ? (null, StatusCodes.Status502BadGateway) : (new Tile(data, type), StatusCodes.Status200OK);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogFailure(ex.Message);
            return (null, StatusCodes.Status502BadGateway);
        }
    }

    private async Task<string?> TemplateAsync(CancellationToken ct)
    {
        if (_template is { } cached && DateTime.UtcNow - cached.Item2 < TemplateTtl) return cached.Item1;
        var settings = await positions.LoadSettingsAsync(ct);
        string? template = settings.Mode == "raster" ? settings.RasterUrl : null;
        _template = Tuple.Create(template, DateTime.UtcNow);
        return template;
    }

    // Un mapa abierto pide decenas de teselas por segundo: si el proveedor falla, una línea por minuto basta (sin la URL: lleva la clave).
    private void LogFailure(string reason)
    {
        if (DateTime.UtcNow - _lastFailureLogUtc < TimeSpan.FromMinutes(1)) return;
        _lastFailureLogUtc = DateTime.UtcNow;
        logger.LogWarning("Mapa de flota: no se pudo traer una tesela del proveedor: {Reason}", reason);
    }

    public void Dispose() => _http.Dispose();
}
