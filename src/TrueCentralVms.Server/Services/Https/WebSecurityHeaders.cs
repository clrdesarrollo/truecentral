namespace TrueCentralVms.Server.Services.Https;

/// <summary>
/// Cabeceras de seguridad de todas las respuestas del servidor (el panel, su API
/// y el manual):
/// <list type="bullet">
/// <item><c>Content-Security-Policy</c>: el panel solo corre su propio código.
/// Un texto que se cuele en una página (el nombre de una cámara, de una
/// persona) no puede ejecutar nada ni mandar datos a otro sitio.</item>
/// <item><c>Referrer-Policy: no-referrer</c>: ninguna dirección del panel sale
/// hacia otro sitio en un enlace.</item>
/// <item><c>frame-ancestors</c> y <c>X-Frame-Options</c>: otro sitio no puede
/// meter el panel en un marco para hacerle clic encima al operador.</item>
/// <item><c>X-Content-Type-Options: nosniff</c>: el navegador no adivina tipos
/// (un archivo subido no se ejecuta como script).</item>
/// </list>
/// "Web:ContentSecurityPolicy" en la configuración: vacío o "enforce" (por
/// omisión), "report-only" (solo avisa en la consola del navegador: para
/// diagnosticar una página que dejó de andar) u "off".
/// </summary>
public static class WebSecurityHeaders
{
    /// <summary>
    /// Lo que el panel usa de verdad, y nada más:
    /// <list type="bullet">
    /// <item>estilos <c>'unsafe-inline'</c>: las páginas arman su HTML con atributos style;</item>
    /// <item>imágenes <c>data:</c> (vista previa de un rostro elegido) y <c>blob:</c> (fotos y capturas armadas en el navegador);</item>
    /// <item>media <c>blob:</c>: el video H.265 por MSE;</item>
    /// <item>workers <c>blob:</c>: MapLibre (mapa de Flota) arma así su worker si no puede cargarlo directo;</item>
    /// <item>conexiones a <c>http://127.0.0.1:*</c>: el complemento de enrolamiento de huellas del puesto.</item>
    /// </list>
    /// Las teselas de Flota, el video WebRTC y los WebSocket van al mismo servidor ('self').
    /// </summary>
    private const string PanelPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; media-src 'self' blob:; font-src 'self' data:; " +
        "connect-src 'self' http://127.0.0.1:*; worker-src 'self' blob:; " +
        "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'self'";

    /// <summary>El manual (sitio estático generado, sin datos de nadie) trae un script en línea por página.</summary>
    private const string ManualPolicy =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; font-src 'self' data:; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'self'";

    /// <summary>Middleware que deja las cabeceras en cada respuesta.</summary>
    public static Func<HttpContext, Func<Task>, Task> Middleware(IConfiguration config)
    {
        string mode = (config["Web:ContentSecurityPolicy"] ?? "").Trim().ToLowerInvariant();
        string? cspHeader = mode switch
        {
            "off" => null,
            "report-only" => "Content-Security-Policy-Report-Only",
            _ => "Content-Security-Policy",
        };
        return (context, next) =>
        {
            var headers = context.Response.Headers;
            if (cspHeader is not null)
                headers[cspHeader] = context.Request.Path.StartsWithSegments(ManualSite.RequestPath) ? ManualPolicy : PanelPolicy;
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Frame-Options"] = "SAMEORIGIN";
            headers["X-Content-Type-Options"] = "nosniff";
            return next();
        };
    }
}
