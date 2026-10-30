using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Https;
using TrueCentralVms.Server.Services.Integration;
using TrueCentralVms.Server.Services.Licensing;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Sistema → Integraciones: las aplicaciones integradas (sistemas externos que
/// leen el padrón por la API de integración) y sus credenciales. El secreto se
/// muestra una sola vez, al crearla o al cambiarlo; después solo se puede
/// reemplazar. Cada aplicación habilitada consume un cupo <c>integration_apps</c>.
/// </summary>
public static class IntegrationAppsApi
{
    private const int MaxAllowedIps = 50;
    private const string KeyAlphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    public static void MapIntegrationAppsApi(this WebApplication app)
    {
        app.MapGet("/api/integrations", async (HttpContext ctx, VmsDbContext db, LicenseService license,
            ServerCertificateManager certificates, WebPorts ports, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemIntegrations, out _) is { } failure) return failure;
            var apps = await db.IntegrationApps.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);
            int? quota = license.IsModuleEnabled(LicenseFeatures.ModuleIntegrationApi)
                ? license.Quota(LicenseFeatures.IntegrationApps) : null;
            return Results.Ok(new IntegrationAppsPageDto(apps.Select(ToDto).ToList(),
                new IntegrationServerInfoDto(ServerCertificateManager.SpkiFingerprint(certificates.Current), ports.HttpsPort),
                IntegrationScopes.All.Select(s => new IntegrationScopeDto(s.Key, s.Label, s.Description)).ToList(),
                quota, apps.Count(a => a.Enabled)));
        });

        app.MapPost("/api/integrations", async (HttpContext ctx, IntegrationAppWriteDto request, VmsDbContext db,
            CredentialProtector protector, LicenseService license, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemIntegrations, out _) is { } failure) return failure;
            if (await ValidateAsync(db, request, null, ct) is { } invalid) return Error(invalid);
            if (request.Enabled && license.Deny(LicenseFeatures.ModuleIntegrationApi, LicenseFeatures.IntegrationApps,
                    await db.IntegrationApps.CountAsync(a => a.Enabled, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "integration-app", request.Name.Trim());

            string secret = NewSecret();
            var entity = new IntegrationApp
            {
                Name = request.Name.Trim(),
                Description = Clean(request.Description),
                AppKey = await NewAppKeyAsync(db, ct),
                SecretCiphertext = protector.Protect(secret),
                Scopes = string.Join(',', request.Scopes.Distinct()),
                AllowedIps = IpsText(request.AllowedIps),
                ExpiresAt = request.ExpiresAt?.ToUniversalTime(),
                Enabled = request.Enabled,
            };
            db.IntegrationApps.Add(entity);
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "integration", "app-created",
                targetType: "integration-app", targetId: entity.Id.ToString(), targetName: entity.Name,
                detail: $"Creó la aplicación integrada «{entity.Name}» (llave {entity.AppKey}): {Describe(entity)}.",
                data: new { entity.AppKey, Scopes = entity.ScopeList, AllowedIps = entity.AllowedIpList, entity.ExpiresAt, entity.Enabled });
            return Results.Ok(new IntegrationAppSecretDto(ToDto(entity), secret));
        });

        app.MapPut("/api/integrations/{id:int}", async (HttpContext ctx, int id, IntegrationAppWriteDto request,
            VmsDbContext db, LicenseService license, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemIntegrations, out _) is { } failure) return failure;
            var entity = await db.IntegrationApps.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (entity is null) return Results.NotFound();
            if (await ValidateAsync(db, request, id, ct) is { } invalid) return Error(invalid);
            if (request.Enabled && !entity.Enabled && license.Deny(LicenseFeatures.ModuleIntegrationApi,
                    LicenseFeatures.IntegrationApps, await db.IntegrationApps.CountAsync(a => a.Enabled, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "integration-app", entity.Name);

            var changes = new List<string>();
            string name = request.Name.Trim(), scopes = string.Join(',', request.Scopes.Distinct());
            string? ips = IpsText(request.AllowedIps), description = Clean(request.Description);
            DateTime? expires = request.ExpiresAt?.ToUniversalTime();
            if (entity.Name != name) changes.Add($"nombre «{entity.Name}» → «{name}»");
            if (entity.Description != description) changes.Add("descripción");
            if (entity.Scopes != scopes) changes.Add($"permisos {ScopesText(entity.ScopeList)} → {ScopesText(scopes.Split(','))}");
            if ((entity.AllowedIps ?? "") != (ips ?? ""))
                changes.Add($"direcciones permitidas → {(ips is null ? "cualquiera" : ips.Replace('\n', ' '))}");
            if (entity.ExpiresAt != expires) changes.Add($"vencimiento → {(expires is { } e ? e.ToLocalTime().ToString("dd-MM-yyyy") : "no vence")}");
            if (entity.Enabled != request.Enabled) changes.Add(request.Enabled ? "habilitada" : "deshabilitada");

            entity.Name = name;
            entity.Description = description;
            entity.Scopes = scopes;
            entity.AllowedIps = ips;
            entity.ExpiresAt = expires;
            entity.Enabled = request.Enabled;
            // Habilitarla a mano (con cupo) la saca del apagado por licencia.
            if (request.Enabled) entity.DisabledByLicense = false;
            entity.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "integration", "app-updated",
                targetType: "integration-app", targetId: entity.Id.ToString(), targetName: entity.Name,
                detail: $"Modificó la aplicación integrada «{entity.Name}»: " +
                        (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios."));
            return Results.Ok(ToDto(entity));
        });

        app.MapPost("/api/integrations/{id:int}/rotate-secret", async (HttpContext ctx, int id, VmsDbContext db,
            CredentialProtector protector, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemIntegrations, out _) is { } failure) return failure;
            var entity = await db.IntegrationApps.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (entity is null) return Results.NotFound();

            string secret = NewSecret();
            entity.SecretCiphertext = protector.Protect(secret);
            entity.SecretIssuedAt = entity.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "integration", "app-secret-rotated",
                targetType: "integration-app", targetId: entity.Id.ToString(), targetName: entity.Name,
                detail: $"Cambió el secreto de la aplicación integrada «{entity.Name}» (llave {entity.AppKey}): el anterior dejó de valer.");
            return Results.Ok(new IntegrationAppSecretDto(ToDto(entity), secret));
        });

        app.MapDelete("/api/integrations/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, AuditService audit,
            CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemIntegrations, out _) is { } failure) return failure;
            var entity = await db.IntegrationApps.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (entity is null) return Results.NotFound();
            db.IntegrationApps.Remove(entity);
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "integration", "app-deleted",
                targetType: "integration-app", targetId: id.ToString(), targetName: entity.Name,
                detail: $"Eliminó la aplicación integrada «{entity.Name}» (llave {entity.AppKey}): ya no puede conectarse.");
            return Results.Ok();
        });
    }

    public static IntegrationAppDto ToDto(IntegrationApp a) => new(
        a.Id, a.Name, a.Description, a.AppKey, a.ScopeList, a.AllowedIpList, a.ExpiresAt, a.Enabled, a.DisabledByLicense,
        a.CreatedAt, a.UpdatedAt, a.SecretIssuedAt, a.LastUsedAt, a.LastUsedIp);

    private static async Task<string?> ValidateAsync(VmsDbContext db, IntegrationAppWriteDto request, int? id,
        CancellationToken ct)
    {
        string name = (request.Name ?? "").Trim();
        if (name.Length is 0 or > 64) return "El nombre es obligatorio (máximo 64 caracteres).";
        string lower = name.ToLower();
        if (await db.IntegrationApps.AnyAsync(a => a.Name.ToLower() == lower && a.Id != id, ct))
            return "Ya existe otra aplicación con ese nombre.";
        if (request.Description is { Length: > 256 }) return "La descripción admite hasta 256 caracteres.";
        if (request.Scopes is not { Count: > 0 }) return "Marque al menos un permiso.";
        if (request.Scopes.FirstOrDefault(s => !IntegrationScopes.IsValid(s)) is { } unknown)
            return $"Permiso desconocido: {unknown}.";
        var ips = CleanIps(request.AllowedIps);
        if (ips.Count > MaxAllowedIps) return $"Se admiten hasta {MaxAllowedIps} direcciones o redes.";
        if (ips.FirstOrDefault(e => !IntegrationAuth.IsValidEntry(e)) is { } bad)
            return $"«{bad}» no es una dirección IP ni una red (por ejemplo 192.168.1.50 o 192.168.1.0/24).";
        if (request.ExpiresAt is { } expires && expires.ToUniversalTime() <= DateTime.UtcNow && id is null)
            return "La fecha de vencimiento ya pasó.";
        return null;
    }

    private static List<string> CleanIps(IReadOnlyList<string>? ips) =>
        (ips ?? []).SelectMany(e => (e ?? "").Split([',', ' ', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries))
            .Select(e => e.Trim()).Where(e => e.Length > 0).Distinct().ToList();

    private static string? IpsText(IReadOnlyList<string>? ips) => CleanIps(ips) is { Count: > 0 } list ? string.Join('\n', list) : null;

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string ScopesText(IEnumerable<string> scopes) =>
        string.Join(" + ", scopes.Select(s => IntegrationScopes.All.FirstOrDefault(x => x.Key == s).Label ?? s));

    private static string Describe(IntegrationApp a) =>
        ScopesText(a.ScopeList) +
        (a.AllowedIpList.Count > 0 ? $"; solo desde {string.Join(", ", a.AllowedIpList)}" : "; desde cualquier dirección") +
        (a.ExpiresAt is { } e ? $"; vence el {e.ToLocalTime():dd-MM-yyyy}" : "") +
        (a.Enabled ? "" : "; deshabilitada");

    /// <summary>Secreto de 256 bits en Base64 URL (43 caracteres): se copia de una vez, sin espacios.</summary>
    private static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Llave de 20 caracteres sin letras que se confundan (l, o, 0, 1).</summary>
    private static async Task<string> NewAppKeyAsync(VmsDbContext db, CancellationToken ct)
    {
        while (true)
        {
            string key = "tc" + RandomNumberGenerator.GetString(KeyAlphabet, 18);
            if (!await db.IntegrationApps.AnyAsync(a => a.AppKey == key, ct)) return key;
        }
    }
}
