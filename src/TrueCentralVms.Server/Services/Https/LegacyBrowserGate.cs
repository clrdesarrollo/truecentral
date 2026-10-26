namespace TrueCentralVms.Server.Services.Https;

/// <summary>
/// Internet Explorer (11 y anteriores, también el «modo IE» de Edge) no corre el
/// panel: no tiene fetch, promesas ni JavaScript moderno y quedaría en blanco o a
/// medio cargar. A las páginas que pide se les responde un aviso para que use
/// Microsoft Edge u otro navegador actual. Solo se miran las navegaciones (piden
/// HTML): la API, el hub y los equipos no se tocan.
/// </summary>
public static class LegacyBrowserGate
{
    public static bool IsLegacyPageRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) return false;
        string userAgent = request.Headers.UserAgent.ToString();
        if (!userAgent.Contains("MSIE ", StringComparison.Ordinal) && !userAgent.Contains("Trident/", StringComparison.Ordinal))
            return false;
        return request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task WriteNoticeAsync(HttpResponse response)
    {
        // 200 y no un error: con un código de error IE muestra su propia página
        // «amigable» en lugar del aviso.
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-UA-Compatible"] = "IE=edge";
        await response.WriteAsync(Notice);
    }

    // HTML y CSS al alcance de IE (sin flex, sin variables ni JavaScript).
    private const string Notice = """
        <!DOCTYPE html>
        <html lang="es">
        <head>
        <meta charset="utf-8">
        <meta http-equiv="X-UA-Compatible" content="IE=edge">
        <title>Navegador no compatible - CLR TrueCentral VMS</title>
        <style>
        body { margin: 0; background: #f3f5f8; font-family: "Segoe UI", Arial, sans-serif; color: #1f2933; }
        .card { width: 520px; margin: 80px auto; background: #fff; border: 1px solid #d9dee5; padding: 32px 36px; }
        h1 { font-size: 22px; margin: 16px 0 12px; font-weight: 600; }
        p { font-size: 15px; line-height: 1.5; margin: 0 0 12px; }
        .muted { color: #5f6b7a; font-size: 13px; }
        a { color: #0b63c5; }
        </style>
        </head>
        <body>
        <div class="card">
        <img src="/logo.png" alt="" height="48">
        <h1>Internet Explorer no es compatible</h1>
        <p>El panel de CLR TrueCentral VMS no funciona en Internet Explorer 11 ni en versiones anteriores.</p>
        <p>Ábralo con <strong>Microsoft Edge</strong> (incluido en Windows 10 y 11) o con otro navegador actual, como Google Chrome o Mozilla Firefox.</p>
        <p class="muted">Si ya está en Edge y ve este aviso, la página está abierta en «modo Internet Explorer»: desactívelo para este sitio.</p>
        </div>
        </body>
        </html>
        """;
}
