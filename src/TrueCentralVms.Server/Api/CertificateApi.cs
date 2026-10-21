using System.Security.Cryptography.X509Certificates;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Https;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Sistema → Certificado HTTPS: qué certificado presenta el servidor, su huella
/// (para comparar con la que muestran el cliente de escritorio, las pantallas y
/// los agentes la primera vez) y cargar uno del cliente o volver al propio.
/// </summary>
public static class CertificateApi
{
    private const long MaxPfxBytes = 64 * 1024;

    public static void MapCertificateApi(this WebApplication app)
    {
        // Datos públicos del certificado: cualquiera con sesión puede comparar la huella.
        app.MapGet("/api/system/certificate", (HttpContext ctx, ServerCertificateManager certificates, WebPorts ports) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var cert = certificates.Current;
            return Results.Ok(new
            {
                custom = certificates.IsCustom,
                subject = cert.Subject,
                issuer = cert.Issuer,
                notBefore = cert.NotBefore,
                notAfter = cert.NotAfter,
                names = ServerCertificateManager.SubjectNames(cert),
                spki = ServerCertificateManager.SpkiFingerprint(cert),
                sha256 = ServerCertificateManager.CertificateFingerprint(cert),
                httpsPort = ports.HttpsPort,
                httpPort = ports.HttpPort,
            });
        });

        // Solo la parte pública (.cer), para instalarla como confiable en los navegadores de la red.
        app.MapGet("/api/system/certificate/download", (HttpContext ctx, ServerCertificateManager certificates) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.File(certificates.Current.Export(X509ContentType.Cert), "application/pkix-cert",
                $"truecentral-{Environment.MachineName.ToLowerInvariant()}.cer");
        });

        app.MapPost("/api/system/certificate", async (HttpContext ctx, ServerCertificateManager certificates,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemCertificate, out _) is { } failure) return failure;
            if (!ctx.Request.HasFormContentType) return Error("Envíe el archivo .pfx en un formulario.");
            var form = await ctx.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0) return Error("Elija el archivo .pfx o .p12 del certificado.");
            if (file.Length > MaxPfxBytes) return Error("El archivo es demasiado grande para ser un certificado.");

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            try
            {
                var cert = certificates.InstallCustom(buffer.ToArray(), form["password"].FirstOrDefault());
                await audit.LogAsync(ctx, "system", "https-certificate-uploaded", targetType: "certificate",
                    targetName: cert.Subject,
                    detail: $"Cargó el certificado HTTPS '{cert.Subject}' emitido por '{cert.Issuer}', vigente hasta el " +
                            $"{cert.NotAfter:dd-MM-yyyy} (nombres: {string.Join(", ", ServerCertificateManager.SubjectNames(cert))}).",
                    data: new { Spki = ServerCertificateManager.SpkiFingerprint(cert), cert.Thumbprint });
                return Results.Ok(new { ok = true });
            }
            catch (InvalidOperationException ex)
            {
                await audit.LogAsync(ctx, "system", "https-certificate-uploaded", targetType: "certificate",
                    targetName: file.FileName, detail: $"Rechazado: {ex.Message}", success: false);
                return Error(ex.Message);
            }
        });

        app.MapDelete("/api/system/certificate", async (HttpContext ctx, ServerCertificateManager certificates,
            AuditService audit) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemCertificate, out _) is { } failure) return failure;
            if (!certificates.IsCustom) return Error("El servidor ya usa su certificado propio.");
            string previous = certificates.Current.Subject;
            certificates.RemoveCustom();
            await audit.LogAsync(ctx, "system", "https-certificate-removed", targetType: "certificate",
                targetName: previous,
                detail: $"Quitó el certificado HTTPS cargado '{previous}': el servidor vuelve a usar el propio " +
                        $"(huella {ServerCertificateManager.SpkiFingerprint(certificates.Current)}).");
            return Results.Ok(new { ok = true });
        });
    }

    private static IResult Error(string message) =>
        Results.Json(new { error = message }, statusCode: StatusCodes.Status400BadRequest);
}
