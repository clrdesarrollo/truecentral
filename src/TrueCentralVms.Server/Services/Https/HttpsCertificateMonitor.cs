namespace TrueCentralVms.Server.Services.Https;

/// <summary>
/// Mantiene al día el certificado propio: cada 5 minutos revisa si el equipo
/// tiene un nombre o una IP que el certificado no cubre (cambio de red, IP nueva
/// por DHCP) o si está por vencer, y lo vuelve a emitir con la misma llave. Deja
/// constancia en la bitácora y avisa del certificado cargado que esté por vencer.
/// </summary>
public sealed class HttpsCertificateMonitor(
    ServerCertificateManager certificates,
    AuditService audit,
    ILogger<HttpsCertificateMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private DateTime _lastExpiryWarning = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (certificates.StartupWarning is { } warning)
        {
            logger.LogWarning("HTTPS: {Warning}", warning);
            await audit.LogSystemAsync("system", "https-certificate-warning", detail: warning);
        }

        string? lastExcluded = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var added = certificates.RefreshSelfSigned();
                // Nombres que la CA propia no puede firmar (IP pública, nombre agregado después de crearla).
                string excluded = string.Join(", ", certificates.ExcludedNames);
                if (excluded.Length > 0 && excluded != lastExcluded)
                {
                    string detail = $"El certificado propio no incluye {excluded}: la CA del servidor solo firma sus nombres de origen " +
                                    "y direcciones privadas. Para entrar por esos nombres cargue un certificado de su organización.";
                    logger.LogWarning("HTTPS: {Detail}", detail);
                    await audit.LogSystemAsync("system", "https-certificate-warning", detail: detail);
                }
                lastExcluded = excluded;
                if (added.Count > 0)
                {
                    string detail = $"Certificado HTTPS propio vuelto a emitir con la misma llave (los clientes no deben volver a confiar): {string.Join(", ", added)}.";
                    logger.LogInformation("HTTPS: {Detail}", detail);
                    await audit.LogSystemAsync("system", "https-certificate-renewed", detail: detail);
                }

                var current = certificates.Current;
                if (certificates.IsCustom && current.NotAfter < DateTime.Now.AddDays(30)
                    && DateTime.UtcNow - _lastExpiryWarning > TimeSpan.FromDays(1))
                {
                    _lastExpiryWarning = DateTime.UtcNow;
                    string detail = $"El certificado HTTPS cargado vence el {current.NotAfter:dd-MM-yyyy}: cargue uno nuevo en Sistema → Certificado HTTPS.";
                    logger.LogWarning("HTTPS: {Detail}", detail);
                    await audit.LogSystemAsync("system", "https-certificate-warning", detail: detail);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "HTTPS: no se pudo revisar el certificado.");
            }

            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}
