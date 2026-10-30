using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Services.Licensing;

namespace TrueCentralVms.Server.Services.Integration;

/// <summary>
/// Autenticación de la API de integración (<c>/api/integration/v1</c>): cada
/// petición viene firmada con la llave y el secreto de una aplicación integrada
/// (ver <see cref="IntegrationSignature"/>). No hay sesión ni token: una
/// petición capturada no se puede repetir (nonce de un solo uso y hora con ±5
/// minutos) ni modificar (la firma cubre método, ruta, consulta y cuerpo).
///
/// Orden de las revisiones: primero la firma, después el estado de la
/// aplicación. Así quien no tiene el secreto no averigua si una llave existe,
/// está deshabilitada o venció.
/// </summary>
public sealed class IntegrationAuth(CredentialProtector protector, LicenseService license, ILogger<IntegrationAuth> logger)
{
    private const int MaxBodyBytes = 64 * 1024;

    // Nonces ya usados (por aplicación) hasta que su hora sale de la ventana.
    private readonly ConcurrentDictionary<string, DateTime> _nonces = new();
    private DateTime _nextPurge = DateTime.MinValue;

    /// <summary>
    /// Verifica la petición. Devuelve la aplicación, o la respuesta de rechazo
    /// (ya auditada). <paramref name="scope"/> null = solo autenticar.
    /// </summary>
    public async Task<(IntegrationApp? App, IResult? Failure)> AuthenticateAsync(HttpContext ctx, VmsDbContext db,
        string? scope, CancellationToken ct)
    {
        var headers = ctx.Request.Headers;
        string appKey = headers[IntegrationSignature.AppKeyHeader].ToString().Trim();
        string timestamp = headers[IntegrationSignature.TimestampHeader].ToString().Trim();
        string nonce = headers[IntegrationSignature.NonceHeader].ToString().Trim();
        string signature = headers[IntegrationSignature.SignatureHeader].ToString().Trim();
        if (appKey.Length == 0 || timestamp.Length == 0 || nonce.Length == 0 || signature.Length == 0)
            return (null, await RejectAsync(ctx, null, appKey, "headers", StatusCodes.Status401Unauthorized,
                "Faltan las cabeceras de la firma (X-TC-AppKey, X-TC-Timestamp, X-TC-Nonce, X-TC-Signature)."));

        var app = appKey.Length <= 32 ? await db.IntegrationApps.FirstOrDefaultAsync(a => a.AppKey == appKey, ct) : null;
        if (app is null)
            return (null, await RejectAsync(ctx, null, appKey, "unknown-key", StatusCodes.Status401Unauthorized,
                "Llave de aplicación desconocida."));

        if (!long.TryParse(timestamp, out long seconds))
            return (null, await RejectAsync(ctx, app, appKey, "clock", StatusCodes.Status401Unauthorized,
                "X-TC-Timestamp tiene que ser la hora en segundos Unix."));
        var sent = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(seconds, 0, 253402300799)).UtcDateTime;
        var now = DateTime.UtcNow;
        if ((now - sent).Duration() > IntegrationSignature.MaxClockSkew)
            return (null, await RejectAsync(ctx, app, appKey, "clock", StatusCodes.Status401Unauthorized,
                $"La hora de la petición difiere en más de 5 minutos de la del servidor ({now:yyyy-MM-dd HH:mm:ss} UTC): corrija el reloj del sistema externo."));
        if (nonce.Length is < 8 or > 64)
            return (null, await RejectAsync(ctx, app, appKey, "nonce", StatusCodes.Status401Unauthorized,
                "X-TC-Nonce tiene que tener entre 8 y 64 caracteres."));

        byte[] body = await ReadBodyAsync(ctx, ct);
        // La ruta y la consulta tal como llegaron: lo que el sistema externo firmó.
        string target = ctx.Features.Get<IHttpRequestFeature>()?.RawTarget is { Length: > 0 } raw
            ? raw : ctx.Request.Path + ctx.Request.QueryString;
        string expected = IntegrationSignature.Sign(protector.Unprotect(app.SecretCiphertext),
            IntegrationSignature.TextToSign(ctx.Request.Method, target, timestamp, nonce, body));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature)))
            return (null, await RejectAsync(ctx, app, appKey, "signature", StatusCodes.Status401Unauthorized,
                "La firma no coincide (revise el secreto y el texto firmado: método, ruta con la consulta, hora, nonce y SHA-256 del cuerpo)."));

        PurgeNonces(now);
        if (!_nonces.TryAdd($"{app.Id}:{nonce}", sent + IntegrationSignature.MaxClockSkew + TimeSpan.FromMinutes(1)))
            return (null, await RejectAsync(ctx, app, appKey, "nonce", StatusCodes.Status401Unauthorized,
                "Esta petición ya se recibió (nonce repetido): cada petición lleva un nonce nuevo."));

        if (app.ExpiresAt is { } expires && expires <= now)
            return (null, await RejectAsync(ctx, app, appKey, "expired", StatusCodes.Status403Forbidden,
                $"La aplicación «{app.Name}» venció el {expires.ToLocalTime():dd-MM-yyyy}."));
        if (app.DisabledByLicense || license.Deny(LicenseFeatures.ModuleIntegrationApi, null, 0, 0) is not null)
            return (null, await RejectAsync(ctx, app, appKey, "license", StatusCodes.Status402PaymentRequired,
                license.Deny(LicenseFeatures.ModuleIntegrationApi, null, 0, 0) ??
                $"La aplicación «{app.Name}» está deshabilitada: la licencia no alcanza para todas las aplicaciones integradas."));
        if (!app.Enabled)
            return (null, await RejectAsync(ctx, app, appKey, "disabled", StatusCodes.Status403Forbidden,
                $"La aplicación «{app.Name}» está deshabilitada."));

        var ip = ClientIp(ctx);
        if (app.AllowedIpList.Count > 0 && (ip is null || !app.AllowedIpList.Any(n => Matches(n, ip))))
            return (null, await RejectAsync(ctx, app, appKey, "ip", StatusCodes.Status403Forbidden,
                $"La aplicación «{app.Name}» no tiene permitido conectarse desde {ip}."));
        if (scope is not null && !app.ScopeList.Contains(scope))
            return (null, await RejectAsync(ctx, app, appKey, "scope", StatusCodes.Status403Forbidden,
                $"La aplicación «{app.Name}» no tiene el permiso «{IntegrationScopes.All.First(s => s.Key == scope).Label}»."));

        // Último uso: se guarda a lo más una vez por minuto (el kiosco pagina el padrón y pide fotos de a una).
        string ipText = ip?.ToString() ?? "";
        if (app.LastUsedAt is not { } last || now - last > TimeSpan.FromMinutes(1) || app.LastUsedIp != ipText)
        {
            app.LastUsedAt = now;
            app.LastUsedIp = ipText;
            await db.SaveChangesAsync(ct);
        }
        var audit = ctx.RequestServices.GetRequiredService<AuditService>();
        if (audit.ShouldLog($"integration-used:{app.Id}:{now.ToLocalTime():yyyyMMdd}", TimeSpan.FromHours(25)))
            await audit.LogAsAsync(ctx, null, ActorOf(app), null, "integration", "app-used",
                targetType: "integration-app", targetId: app.Id.ToString(), targetName: app.Name,
                detail: $"La aplicación integrada «{app.Name}» se conectó desde {ipText} (primera petición del día: {ctx.Request.Path}).",
                clientIp: ipText);
        return (app, null);
    }

    /// <summary>Nombre con que una aplicación aparece como actor en la bitácora.</summary>
    public static string ActorOf(IntegrationApp app) => $"app:{app.Name}";

    /// <summary>¿La dirección cae en la entrada de la lista (una IP o una red CIDR)?</summary>
    public static bool Matches(string entry, IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (entry.Contains('/'))
            return IPNetwork.TryParse(entry, out var network) && network.Contains(ip);
        return IPAddress.TryParse(entry, out var single) && (single.IsIPv4MappedToIPv6 ? single.MapToIPv4() : single).Equals(ip);
    }

    /// <summary>¿Es una entrada válida de la lista de direcciones permitidas?</summary>
    public static bool IsValidEntry(string entry) =>
        entry.Contains('/') ? IPNetwork.TryParse(entry, out _) : IPAddress.TryParse(entry, out _);

    private static IPAddress? ClientIp(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        return ip is not null && ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext ctx, CancellationToken ct)
    {
        if (ctx.Request.ContentLength is 0 || (ctx.Request.ContentLength is null && !ctx.Request.Body.CanSeek &&
                                              HttpMethods.IsGet(ctx.Request.Method)))
            return [];
        ctx.Request.EnableBuffering(MaxBodyBytes);
        using var buffer = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(buffer, ct);
        ctx.Request.Body.Position = 0;
        return buffer.ToArray();
    }

    private void PurgeNonces(DateTime now)
    {
        if (now < _nextPurge) return;
        _nextPurge = now.AddMinutes(1);
        foreach (var (key, until) in _nonces)
            if (until < now) _nonces.TryRemove(key, out _);
    }

    /// <summary>Rechazo auditado (a lo más una fila cada 10 minutos por llave, motivo y dirección).</summary>
    private async Task<IResult> RejectAsync(HttpContext ctx, IntegrationApp? app, string appKey, string code, int status,
        string message)
    {
        string ip = ClientIp(ctx)?.ToString() ?? "";
        string key = appKey.Length > 32 ? appKey[..32] : appKey;
        var audit = ctx.RequestServices.GetRequiredService<AuditService>();
        if (audit.ShouldLog($"integration-rejected:{key}:{code}:{ip}", TimeSpan.FromMinutes(10)))
        {
            logger.LogWarning("API de integración: rechazada una petición a {Path} desde {Ip} ({Code}): {Message}",
                ctx.Request.Path, ip, code, message);
            await audit.LogAsAsync(ctx, null, app is null ? $"app:{key}" : ActorOf(app), null, "integration", "request-rejected",
                targetType: "integration-app", targetId: app?.Id.ToString(), targetName: app?.Name ?? key,
                detail: $"Rechazada una petición a {ctx.Request.Path} desde {ip}: {message}",
                success: false, data: new { Code = code, AppKey = key }, clientIp: ip);
        }
        return Results.Json(new { error = message, code }, statusCode: status);
    }
}
