using Microsoft.Extensions.FileProviders;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Manual de usuario en HTML (docs\manual\build-manual.ps1 -Web), servido en
/// /manual para que el panel web y el cliente lo abran desde el ícono de ayuda.
/// El instalador lo deja en {app}\manual; en desarrollo se usa el último sitio
/// compilado en dist\ del repositorio. Sin sitio, /manual responde un aviso.
/// </summary>
public static class ManualSite
{
    public const string RequestPath = "/manual";
    private const string DevSitePattern = "CLRTrueCentralVMS-Manual-*-web";

    /// <summary>Carpeta del sitio (la que tiene index.html) o null si no está.</summary>
    public static string? Locate(string contentRoot)
    {
        foreach (string root in new[] { contentRoot, AppContext.BaseDirectory })
        {
            string candidate = Path.Combine(root, "manual");
            if (File.Exists(Path.Combine(candidate, "index.html"))) return candidate;
        }

        // Desarrollo: subir desde el proyecto hasta el repositorio y tomar el sitio más nuevo de dist\.
        for (var dir = new DirectoryInfo(contentRoot); dir is not null; dir = dir.Parent)
        {
            var dist = new DirectoryInfo(Path.Combine(dir.FullName, "dist"));
            if (!dist.Exists) continue;
            var newest = dist.EnumerateDirectories(DevSitePattern)
                .Where(d => File.Exists(Path.Combine(d.FullName, "index.html")))
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is not null) return newest.FullName;
        }
        return null;
    }

    public static void Map(WebApplication app, ILogger logger)
    {
        string? root = Locate(app.Environment.ContentRootPath);
        if (root is not null)
        {
            logger.LogInformation("Manual de usuario servido en {Path} desde {Root}.", RequestPath, root);
            app.UseFileServer(new FileServerOptions
            {
                RequestPath = RequestPath,
                FileProvider = new PhysicalFileProvider(root),
                EnableDefaultFiles = true,
                StaticFileOptions =
                {
                    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
                },
            });
        }
        else
        {
            logger.LogWarning("No se encontró el manual de usuario en HTML: {Path} mostrará un aviso.", RequestPath);
        }

        // La portada se sirve a mano: MapFallbackToFile (la SPA) le gana a
        // DefaultFiles en /manual/. Un capítulo que no existe (edición sin ese
        // módulo, enlace viejo) lleva a la portada; sin sitio, el aviso.
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(RequestPath, out var rest) || !HttpMethods.IsGet(context.Request.Method))
            {
                await next();
                return;
            }
            if (root is not null)
            {
                if (rest.Value == "/")
                {
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.Headers.CacheControl = "no-cache";
                    await context.Response.SendFileAsync(Path.Combine(root, "index.html"));
                    return;
                }
                context.Response.Redirect(context.Request.PathBase + RequestPath + "/");
                return;
            }
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync(MissingNotice);
        });
    }

    private const string MissingNotice = """
        <!doctype html>
        <html lang="es"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Manual de usuario</title>
        <style>body{font-family:Segoe UI,system-ui,sans-serif;background:#f4f6f9;color:#1f2937;display:flex;min-height:100vh;margin:0;align-items:center;justify-content:center}
        main{max-width:520px;margin:16px;padding:28px 32px;background:#fff;border-radius:10px;box-shadow:0 2px 12px rgba(0,0,0,.08)}
        h1{font-size:20px;margin:0 0 12px}p{line-height:1.5;margin:0}</style></head>
        <body><main><h1>El manual no está instalado en este servidor</h1>
        <p>Este servidor no tiene el manual de usuario en HTML. Actualice el sistema con el instalador de la suite, que lo incluye.</p></main></body></html>
        """;
}
