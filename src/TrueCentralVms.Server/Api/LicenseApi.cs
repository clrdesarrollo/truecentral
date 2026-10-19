using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Licensing;
using TrueCentralVms.Core.Domain;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// API de licenciamiento (Sistema → Licencia). El estado lo ve cualquier
/// usuario con sesión (el cliente muestra los avisos); activar, importar,
/// revalidar y desactivar exigen administrador y quedan en la bitácora.
/// Estas rutas quedan FUERA del modo restringido: son el camino para
/// regularizar la licencia.
/// </summary>
public static class LicenseApi
{
    public static void MapLicenseApi(this WebApplication app)
    {
        app.MapGet("/api/system/license", async (HttpContext ctx, LicenseService license, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.Ok(await license.GetStatusAsync(db, ct));
        });

        app.MapPost("/api/system/license/activate", async (HttpContext ctx, LicenseActivateRequest request,
            LicenseService license, AuditService audit, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemLicense, out _) is { } failure) return failure;
            var result = await license.ActivateOnlineAsync(request.ActivationCode ?? "", ct);
            await audit.LogAsync(ctx, "license", "license-activated", targetType: "license",
                targetId: Trim(request.ActivationCode), detail: result.Message, success: result.Success);
            return result.Success
                ? Results.Ok(new { message = result.Message, status = await license.GetStatusAsync(db, ct) })
                : Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status422UnprocessableEntity);
        });

        // Ingreso único en línea: base y expansiones en una sola lista. Cada
        // código queda en la bitácora con la acción que le tocó.
        app.MapPost("/api/system/license/apply", async (HttpContext ctx, LicenseCodesRequest request,
            LicenseService license, AuditService audit, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemLicense, out _) is { } failure) return failure;
            var codes = (request.Codes ?? []).Append(request.ActivationCode).ToList();
            var result = await license.ApplyCodesAsync(codes, ct);
            if (result.Items.Count == 0)
                await audit.LogAsync(ctx, "license", "license-activated", targetType: "license",
                    detail: result.Message, success: false);
            foreach (var item in result.Items)
            {
                string action = item.Step switch
                {
                    "refresh" => "license-refresh-requested",
                    "addon" => "license-addon-entered",
                    _ => "license-activated",
                };
                await audit.LogAsync(ctx, "license", action, targetType: "license",
                    targetId: Trim(item.Code), detail: item.Message, success: item.Success);
            }
            var status = await license.GetStatusAsync(db, ct);
            return result.Success
                ? Results.Ok(new { message = result.Message, items = result.Items, status })
                : Results.Json(new { error = result.Message, items = result.Items, status },
                    statusCode: StatusCodes.Status422UnprocessableEntity);
        });

        // Solicitud de activación sin internet: el administrador la descarga y
        // la envía a soporte, que devuelve el .lic para importar. Acepta un
        // código (activationCode) o la lista completa (codes).
        app.MapPost("/api/system/license/request", async (HttpContext ctx, LicenseCodesRequest request,
            LicenseService license, AuditService audit) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemLicense, out _) is { } failure) return failure;
            var codes = (request.Codes ?? []).Append(request.ActivationCode).ToList();
            var file = license.BuildActivationRequest(codes);
            string listed = string.Join(", ", codes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim()));
            await audit.LogAsync(ctx, "license", "license-request-generated", targetType: "license",
                targetId: Trim(codes.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))),
                detail: $"Generó la solicitud de activación sin conexión ({file.FileName}) para el equipo {license.HardwareIdentifier}" +
                        (listed.Length > 0 ? $" con los códigos {listed}." : "."));
            return Results.Ok(file);
        });

        app.MapPost("/api/system/license/import", async (HttpContext ctx, LicenseImportRequest request,
            LicenseService license, AuditService audit, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemLicense, out _) is { } failure) return failure;
            var result = await license.ImportAsync(request.LicenseFile ?? "", ct);
            await audit.LogAsync(ctx, "license", "license-imported", targetType: "license",
                targetId: license.Snapshot.Payload?.LicenseKey, detail: result.Message, success: result.Success);
            return result.Success
                ? Results.Ok(new { message = result.Message, status = await license.GetStatusAsync(db, ct) })
                : Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status422UnprocessableEntity);
        });

        app.MapPost("/api/system/license/refresh", async (HttpContext ctx, LicenseService license, AuditService audit,
            VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemLicense, out _) is { } failure) return failure;
            var result = await license.RefreshAsync(ct);
            await audit.LogAsync(ctx, "license", "license-refresh-requested", targetType: "license",
                targetId: license.Snapshot.Payload?.LicenseKey, detail: result.Message, success: result.Success);
            return result.Success
                ? Results.Ok(new { message = result.Message, status = await license.GetStatusAsync(db, ct) })
                : Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status422UnprocessableEntity);
        });

        // Código de una expansión comprada: se confirma contra el servidor de
        // licencias y se revalida para traerla (sin conexión, explica el .lic).
        app.MapPost("/api/system/license/addon", async (HttpContext ctx, LicenseActivateRequest request,
            LicenseService license, AuditService audit, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemLicense, out _) is { } failure) return failure;
            var result = await license.AddAddonAsync(request.ActivationCode ?? "", ct);
            await audit.LogAsync(ctx, "license", "license-addon-entered", targetType: "license",
                targetId: Trim(request.ActivationCode), detail: result.Message, success: result.Success);
            return result.Success
                ? Results.Ok(new { message = result.Message, status = await license.GetStatusAsync(db, ct) })
                : Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status422UnprocessableEntity);
        });

        app.MapPost("/api/system/license/deactivate", async (HttpContext ctx, LicenseService license, AuditService audit,
            VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemLicense, out _) is { } failure) return failure;
            string? key = license.Snapshot.Payload?.LicenseKey;
            var result = await license.DeactivateAsync(ct);
            await audit.LogAsync(ctx, "license", "license-deactivated", targetType: "license",
                targetId: key, detail: result.Message, success: result.Success);
            return result.Success
                ? Results.Ok(new { message = result.Message, status = await license.GetStatusAsync(db, ct) })
                : Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status422UnprocessableEntity);
        });
    }

    private static string? Trim(string? code)
    {
        code = code?.Trim();
        return string.IsNullOrEmpty(code) ? null : (code.Length > 64 ? code[..64] : code);
    }
}
