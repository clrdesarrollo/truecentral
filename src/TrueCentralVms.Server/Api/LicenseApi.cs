using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Licensing;

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
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var result = await license.ActivateOnlineAsync(request.ActivationCode ?? "", ct);
            await audit.LogAsync(ctx, "license", "license-activated", targetType: "license",
                targetId: Trim(request.ActivationCode), detail: result.Message, success: result.Success);
            return result.Success
                ? Results.Ok(new { message = result.Message, status = await license.GetStatusAsync(db, ct) })
                : Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status422UnprocessableEntity);
        });

        // Solicitud de activación sin internet: el administrador la descarga y
        // la envía a soporte, que devuelve el .lic para importar.
        app.MapPost("/api/system/license/request", async (HttpContext ctx, LicenseActivateRequest request,
            LicenseService license, AuditService audit) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var file = license.BuildActivationRequest(request.ActivationCode ?? "");
            await audit.LogAsync(ctx, "license", "license-request-generated", targetType: "license",
                targetId: Trim(request.ActivationCode),
                detail: $"Generó la solicitud de activación sin conexión ({file.FileName}) para el equipo {license.HardwareIdentifier}.");
            return Results.Ok(file);
        });

        app.MapPost("/api/system/license/import", async (HttpContext ctx, LicenseImportRequest request,
            LicenseService license, AuditService audit, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
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
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var result = await license.RefreshAsync(ct);
            await audit.LogAsync(ctx, "license", "license-refresh-requested", targetType: "license",
                targetId: license.Snapshot.Payload?.LicenseKey, detail: result.Message, success: result.Success);
            return result.Success
                ? Results.Ok(new { message = result.Message, status = await license.GetStatusAsync(db, ct) })
                : Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status422UnprocessableEntity);
        });

        app.MapPost("/api/system/license/deactivate", async (HttpContext ctx, LicenseService license, AuditService audit,
            VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
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
