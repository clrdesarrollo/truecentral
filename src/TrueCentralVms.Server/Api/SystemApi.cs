using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Supervisor;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// API de sistema: uso de recursos y supervisor de servicios (watchdog). El
/// estado lo puede ver cualquier usuario con sesión (los operadores también
/// monitorean); iniciar, detener, reiniciar y cambiar el auto-reinicio exige
/// administrador y queda en la bitácora con el actor real.
/// </summary>
public static class SystemApi
{
    public static void MapSystemApi(this WebApplication app)
    {
        // Uso de recursos del servidor (indicadores del cliente y del panel).
        // Cualquier usuario autenticado: los operadores también monitorean.
        app.MapGet("/api/system/metrics", (HttpContext ctx, SystemMetrics metrics) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.Ok(metrics.Read());
        });

        // ------------------------------------------------------------------
        // Supervisor de servicios
        // ------------------------------------------------------------------
        app.MapGet("/api/system/services", (HttpContext ctx, ServiceSupervisor supervisor) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.Ok(supervisor.Snapshot());
        });

        app.MapPost("/api/system/services/{id}/start", (string id, HttpContext ctx, ServiceSupervisor supervisor, AuditService audit) =>
            CommandAsync(ctx, audit, "service-started", id, () => supervisor.StartAsync(id, CancellationToken.None)));

        app.MapPost("/api/system/services/{id}/stop", (string id, HttpContext ctx, ServiceSupervisor supervisor, AuditService audit) =>
            CommandAsync(ctx, audit, "service-stopped", id, () => supervisor.StopAsync(id, CancellationToken.None)));

        app.MapPost("/api/system/services/{id}/restart", (string id, HttpContext ctx, ServiceSupervisor supervisor, AuditService audit) =>
            CommandAsync(ctx, audit, "service-restarted", id, () => supervisor.RestartAsync(id, CancellationToken.None)));

        app.MapPut("/api/system/services/{id}/auto-restart", (string id, AutoRestartRequest request, HttpContext ctx,
            ServiceSupervisor supervisor, AuditService audit) =>
            CommandAsync(ctx, audit, "service-autorestart-changed", id,
                () => Task.FromResult(supervisor.SetAutoRestart(id, request.Enabled)),
                data: new { enabled = request.Enabled }));

        // Reinicio completo del proceso del servidor (solo como servicio de
        // Windows). Se audita ANTES de pedirlo: después ya no hay quien escriba.
        app.MapPost("/api/system/restart", async (HttpContext ctx, ServiceSupervisor supervisor, AuditService audit) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            await audit.LogAsync(ctx, "system", "server-restart-requested",
                targetType: "server", targetId: ServiceSupervisor.WindowsServiceName,
                detail: "Reinicio completo del servidor solicitado desde el panel.");
            var result = supervisor.RestartServer();
            if (!result.Ok)
            {
                await audit.LogAsync(ctx, "system", "server-restart-requested",
                    targetType: "server", targetId: ServiceSupervisor.WindowsServiceName,
                    detail: result.Message, success: false);
                return Results.Json(new { error = result.Message }, statusCode: result.StatusCode);
            }
            return Results.Ok(new { message = result.Message });
        });
    }

    /// <summary>
    /// Ejecuta una orden sobre un servicio y la audita (éxito o rechazo) con
    /// el actor del request. Las órdenes NO se cancelan si el navegador corta
    /// la conexión: un reinicio a medias es peor que uno completo.
    /// </summary>
    private static async Task<IResult> CommandAsync(HttpContext ctx, AuditService audit, string action, string id,
        Func<Task<ServiceSupervisor.CommandResult>> command, object? data = null)
    {
        if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
        var result = await command();
        if (result.StatusCode != StatusCodes.Status404NotFound)
        {
            await audit.LogAsync(ctx, "system", action,
                targetType: "service", targetId: id, targetName: result.Service?.Name ?? id,
                detail: result.Message, success: result.Ok, data: data);
        }
        return result.Ok
            ? Results.Ok(new { message = result.Message, service = result.Service })
            : Results.Json(new { error = result.Message, service = result.Service }, statusCode: result.StatusCode);
    }
}
