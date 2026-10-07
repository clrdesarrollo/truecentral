using System.Reflection;
using System.Runtime.InteropServices;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Licensing;
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
        // "Acerca de" del panel web: producto, fabricante, versión y build del
        // servidor, plataforma y un resumen de la licencia. Cualquier usuario
        // con sesión (el operador también lo necesita para reportar a soporte).
        app.MapGet("/api/system/about", async (HttpContext ctx, LicenseService license, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var asm = typeof(SystemApi).Assembly;
            string? Attr<T>(Func<T, string> pick) where T : Attribute => asm.GetCustomAttribute<T>() is { } a ? pick(a) : null;
            // InformationalVersion = "0.5.4+<build Jenkins o commit>": lo que va después del '+' es el build.
            string info = Attr<AssemblyInformationalVersionAttribute>(a => a.InformationalVersion) ?? "";
            int plus = info.IndexOf('+');
            string? build = plus >= 0 ? info[(plus + 1)..] : null;
            if (build is { Length: 40 }) build = build[..10];   // hash de commit (compilación local): abreviado
            DateTime? builtAt = null;
            try { if (!string.IsNullOrEmpty(asm.Location)) builtAt = File.GetLastWriteTimeUtc(asm.Location); } catch { /* sin acceso al archivo */ }
            var status = await license.GetStatusAsync(db, ct);
            var started = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
            return Results.Ok(new
            {
                product = Attr<AssemblyProductAttribute>(a => a.Product) ?? "CLR TrueCentral VMS",
                manufacturer = Attr<AssemblyCompanyAttribute>(a => a.Company) ?? "CLRobotics",
                copyright = Attr<AssemblyCopyrightAttribute>(a => a.Copyright),
                version = asm.GetName().Version?.ToString(3) ?? "0.0.0",
                fileVersion = Attr<AssemblyFileVersionAttribute>(a => a.Version),
                build,
                builtAt,
                runtime = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                hostname = Environment.MachineName,
                startedAt = started,
                license = new
                {
                    status.State, status.Operational, status.Mode, status.Message, status.Warning,
                    status.LicenseKey, status.CustomerName, status.Package, status.ExpiresAt, status.DaysRemaining,
                    status.HardwareId,
                },
            });
        });

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
        // Puestos de cliente de escritorio (el cupo de la licencia): qué equipos
        // los ocupan y liberarlos. Un cliente que se cerró sin cerrar sesión
        // sigue ocupando su puesto hasta que la sesión vence (12 h); desde acá
        // un administrador lo libera sin reiniciar el servidor.
        app.MapGet("/api/system/desktop-seats", (HttpContext ctx, TokenService tokens, LicenseService license) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var seats = tokens.DesktopSeats();
            return Results.Ok(new
            {
                limit = license.Quota(LicenseFeatures.MaxClientSessions),
                inUse = seats.Count,
                seats,
            });
        });

        app.MapPost("/api/system/desktop-seats/release", async (HttpContext ctx, DesktopSeatReleaseRequest request,
            TokenService tokens, AuditService audit) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (string.IsNullOrWhiteSpace(request.Seat))
                return Results.Json(new { error = "Indique el equipo cuyo puesto quiere liberar." }, statusCode: 422);
            var seat = tokens.DesktopSeats().FirstOrDefault(s =>
                string.Equals(s.Seat, request.Seat.Trim(), StringComparison.OrdinalIgnoreCase));
            int closed = tokens.ReleaseSeat(request.Seat);
            await audit.LogAsync(ctx, "auth", "desktop-seat-released", targetType: "client", targetId: request.Seat.Trim(),
                targetName: request.Seat.Trim(),
                detail: seat is null
                    ? $"Liberó el puesto del equipo '{request.Seat.Trim()}' (no tenía sesiones vigentes)."
                    : $"Liberó el puesto del equipo '{seat.Seat}' ({seat.Username}, cliente {seat.Version ?? "?"}, " +
                      $"{(seat.Connected ? "conectado en ese momento" : "sesión colgada: el cliente no estaba conectado")}): " +
                      $"{closed} sesión(es) cerrada(s); ese equipo no puede volver a entrar durante " +
                      $"{(int)TokenService.ReleasedSeatBlock.TotalSeconds} s.",
                data: new { closed, connected = seat?.Connected });
            return Results.Ok(new { closed });
        });

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

/// <summary>Equipo cuyo puesto de cliente de escritorio se libera (el nombre que muestra la lista).</summary>
public sealed record DesktopSeatReleaseRequest(string Seat);
