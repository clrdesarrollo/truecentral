using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Backup;

namespace TrueCentralVms.Server.Api;

/// <summary>Ajustes de los respaldos (sin secretos: solo si están definidos).</summary>
public sealed record BackupSettingsDto(bool Enabled, string DailyTime, int KeepDaily, int KeepWeekly, int KeepMonthly, int KeepPreUpdate,
    bool IncludeAnpr, bool HasPassword, DateTime? PasswordSetUtc, string? SecondaryPath, string? SecondaryUser, bool HasSecondaryPassword);

/// <summary>Cambio de ajustes. SecondaryPassword: null = no cambiar, "" = quitar.</summary>
public sealed record BackupSettingsWriteDto(bool Enabled, string DailyTime, int KeepDaily, int KeepWeekly, int KeepMonthly, int KeepPreUpdate,
    bool IncludeAnpr, string? SecondaryPath, string? SecondaryUser, string? SecondaryPassword);

/// <summary>Definir o cambiar la contraseña de respaldo: exige la contraseña de la cuenta de quien la cambia.</summary>
public sealed record BackupPasswordDto(string Password, string Confirm, string AccountPassword);

/// <summary>Confirmación con la contraseña de la cuenta (descargar un respaldo es llevarse toda la información).</summary>
public sealed record BackupAccountConfirmDto(string AccountPassword);

/// <summary>
/// Sistema → Respaldos. Todo exige el permiso Respaldos y queda en la bitácora. Lo que entrega información completa
/// (descargar) o cambia cómo se cifra (contraseña) pide además la contraseña de la cuenta. No hay restaurar ni borrar
/// desde la web: restaurar es por consola con el servicio detenido, y los viejos los borra solo la retención.
/// </summary>
public static class BackupApi
{
    private const int MinPasswordLength = 12;

    public static void MapBackupApi(this WebApplication app)
    {
        app.MapGet("/api/system/backups", async (HttpContext ctx, BackupService backups, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemBackup, out _) is { } failure) return failure;
            var s = await backups.LoadSettingsAsync(ct);
            var (healthy, health) = backups.Health(current: false);
            return Results.Ok(new
            {
                settings = ToDto(s),
                state = backups.State,
                busy = backups.Busy,
                progress = backups.Progress(),
                healthy,
                health,
                directory = backups.Directory,
                files = await Task.Run(backups.List, ct),
            });
        });

        app.MapPut("/api/system/backups/settings", async (HttpContext ctx, BackupSettingsWriteDto request, BackupService backups,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemBackup, out _) is { } failure) return failure;
            var current = await backups.LoadSettingsAsync(ct);
            if (!TimeOnly.TryParseExact(request.DailyTime?.Trim(), "HH:mm", out _))
                return Error("La hora del respaldo diario debe tener el formato HH:mm (por ejemplo 03:30).");
            if (request.KeepDaily is < 1 or > 60 || request.KeepWeekly is < 0 or > 52 || request.KeepMonthly is < 0 or > 120
                || request.KeepPreUpdate is < 1 or > 50)
                return Error("Retención fuera de rango: diarios 1-60, semanales 0-52, mensuales 0-120, antes de actualizar 1-50.");
            string? path = string.IsNullOrWhiteSpace(request.SecondaryPath) ? null : request.SecondaryPath.Trim();
            if (path is not null && !(Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)))
                return Error(@"El segundo destino debe ser una ruta completa (D:\Respaldos) o de red (\\servidor\recurso\carpeta).");
            if (path is not null && Path.GetFullPath(path).StartsWith(Path.GetFullPath(backups.Directory), StringComparison.OrdinalIgnoreCase))
                return Error("El segundo destino no puede estar dentro de la carpeta principal de respaldos.");
            string? user = path is null || string.IsNullOrWhiteSpace(request.SecondaryUser) ? null : request.SecondaryUser.Trim();
            // La clave guardada del recurso de red no se entrega a otro destino (misma regla que los equipos).
            string? secondaryCipher = request.SecondaryPassword switch
            {
                null => user is null ? null : current.SecondaryPasswordCipher,
                "" => null,
                var p => backups.Protect(p),
            };
            if (request.SecondaryPassword is null && secondaryCipher is not null
                && !string.Equals(current.SecondaryPath, path, StringComparison.OrdinalIgnoreCase))
                return Error("Cambió el segundo destino: escriba de nuevo la contraseña del recurso de red.");

            var next = current with
            {
                Enabled = request.Enabled,
                DailyTime = request.DailyTime!.Trim(),
                KeepDaily = request.KeepDaily,
                KeepWeekly = request.KeepWeekly,
                KeepMonthly = request.KeepMonthly,
                KeepPreUpdate = request.KeepPreUpdate,
                IncludeAnpr = request.IncludeAnpr,
                SecondaryPath = path,
                SecondaryUser = user,
                SecondaryPasswordCipher = secondaryCipher,
            };
            await backups.SaveSettingsAsync(next, ct);
            await audit.LogAsync(ctx, "backup", "backup-settings-updated", targetType: "backup",
                detail: $"Respaldos {(next.Enabled ? "activados" : "DESACTIVADOS")}, diario a las {next.DailyTime}, se conservan " +
                        $"{next.KeepDaily} diarios / {next.KeepWeekly} semanales / {next.KeepMonthly} mensuales / {next.KeepPreUpdate} previos a actualizar; " +
                        $"fotos de patentes: {(next.IncludeAnpr ? "sí" : "no")}; segundo destino: {next.SecondaryPath ?? "ninguno"}" +
                        $"{(next.SecondaryUser is null ? "" : $" (usuario {next.SecondaryUser})")}.");
            return Results.Ok(ToDto(next));
        });

        app.MapPut("/api/system/backups/password", async (HttpContext ctx, BackupPasswordDto request, BackupService backups,
            VmsDbContext db, LoginThrottle throttle, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemBackup, out var session) is { } failure) return failure;
            if (await ConfirmAccountAsync(ctx, session, request.AccountPassword, db, throttle, audit, "cambiar la contraseña de respaldo", ct) is { } denied)
                return denied;
            if (request.Password is null || request.Password.Length < MinPasswordLength)
                return Error($"La contraseña de respaldo debe tener al menos {MinPasswordLength} caracteres (mejor una frase).");
            if (request.Password != request.Confirm) return Error("La confirmación no coincide.");
            var current = await backups.LoadSettingsAsync(ct);
            bool hadPassword = current.PasswordCipher is not null;
            await backups.SaveSettingsAsync(current with { PasswordCipher = backups.Protect(request.Password), PasswordSetUtc = DateTime.UtcNow }, ct);
            await audit.LogAsync(ctx, "backup", "backup-password-changed", targetType: "backup",
                detail: hadPassword
                    ? "Cambió la contraseña de respaldo. Los respaldos anteriores siguen abriéndose con la contraseña anterior."
                    : "Definió la contraseña de respaldo: desde ahora los respaldos se pueden restaurar en cualquier equipo con ella.");
            return Results.NoContent();
        });

        // Lanza el respaldo y vuelve enseguida (202): uno de 1,7 GB tarda minutos. El avance se sigue con /progress y el
        // resultado (creado o fallido) queda en la bitácora a nombre de quien lo pidió, aunque haya cerrado el panel.
        app.MapPost("/api/system/backups/run", (HttpContext ctx, BackupService backups) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemBackup, out var session) is { } failure) return failure;
            var actor = new BackupActor(session.UserId, session.Username, session.Role,
                ctx.Connection.RemoteIpAddress?.ToString() ?? "", AuditService.OriginOf(ctx));
            return backups.StartManual(actor)
                ? Results.Json(backups.Progress(), statusCode: StatusCodes.Status202Accepted)
                : Error("Ya hay un respaldo o una prueba de restauración en curso.", StatusCodes.Status409Conflict);
        });

        // Avance del respaldo en curso: solo memoria (ni la base ni el disco), para consultarlo cada pocos segundos.
        app.MapGet("/api/system/backups/progress", (HttpContext ctx, BackupService backups) =>
            ApiSecurity.Require(ctx, Permissions.SystemBackup, out _) ?? Results.Ok(backups.Progress()));

        app.MapPost("/api/system/backups/{name}/verify", async (HttpContext ctx, string name, BackupService backups,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemBackup, out _) is { } failure) return failure;
            if (backups.PathOf(name) is null) return Results.NotFound(new { error = "No existe ese respaldo." });
            try
            {
                string result = await backups.VerifyAsync(name, CancellationToken.None);
                await audit.LogAsync(ctx, "backup", "backup-verified", targetType: "backup", targetName: name, detail: result);
                return Results.Ok(new { ok = true, message = result });
            }
            catch (Exception ex)
            {
                await audit.LogAsync(ctx, "backup", "backup-verified", targetType: "backup", targetName: name,
                    detail: $"La prueba del respaldo FALLÓ: {ex.Message}", success: false);
                return Results.Ok(new { ok = false, message = ex.Message });
            }
        });

        app.MapPost("/api/system/backups/{name}/download", async (HttpContext ctx, string name, BackupAccountConfirmDto request,
            BackupService backups, VmsDbContext db, LoginThrottle throttle, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemBackup, out var session) is { } failure) return failure;
            if (backups.PathOf(name) is not { } path) return Results.NotFound(new { error = "No existe ese respaldo." });
            if (await ConfirmAccountAsync(ctx, session, request.AccountPassword, db, throttle, audit, $"descargar el respaldo {name}", ct) is { } denied)
                return denied;
            await audit.LogAsync(ctx, "backup", "backup-downloaded", targetType: "backup", targetName: name,
                detail: $"Descargó el respaldo {name} ({BackupService.Size(new FileInfo(path).Length)}).");
            return Results.File(path, "application/octet-stream", name, enableRangeProcessing: false);
        });

        app.MapPost("/api/system/backups/secondary/test", async (HttpContext ctx, BackupService backups, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemBackup, out _) is { } failure) return failure;
            var settings = await backups.LoadSettingsAsync(ct);
            string? error = await Task.Run(() => backups.TestSecondary(settings), ct);
            return Results.Ok(new { ok = error is null, message = error ?? $"Se pudo escribir en {settings.SecondaryPath}." });
        });
    }

    private static BackupSettingsDto ToDto(BackupSettings s) => new(s.Enabled, s.DailyTime, s.KeepDaily, s.KeepWeekly, s.KeepMonthly,
        s.KeepPreUpdate, s.IncludeAnpr, s.PasswordCipher is not null, s.PasswordSetUtc, s.SecondaryPath, s.SecondaryUser,
        s.SecondaryPasswordCipher is not null);

    /// <summary>Vuelve a pedir la contraseña de la cuenta (con el mismo freno de intentos que el ingreso).</summary>
    private static async Task<IResult?> ConfirmAccountAsync(HttpContext ctx, SessionInfo session, string? password, VmsDbContext db,
        LoginThrottle throttle, AuditService audit, string what, CancellationToken ct)
    {
        if (throttle.LockedFor(session.Username, ctx.Connection.RemoteIpAddress) is { } left)
            return Error($"Demasiados intentos fallidos. Espere {LoginThrottle.Describe(left)}.", StatusCodes.Status429TooManyRequests);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == session.UserId, ct);
        if (user is not null && password is not null && PasswordHasher.Verify(password, user.PasswordHash, user.PasswordSalt))
            return null;
        throttle.RecordFailure(session.Username, ctx.Connection.RemoteIpAddress);
        await audit.LogAsync(ctx, "backup", "backup-downloaded", targetType: "backup",
            detail: $"Rechazado: contraseña de la cuenta incorrecta al intentar {what}.", success: false);
        return Error("La contraseña de su cuenta no es correcta.", StatusCodes.Status403Forbidden);
    }

    private static IResult Error(string message, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new { error = message }, statusCode: status);
}
