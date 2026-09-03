using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

public static class AuthApi
{
    public static void MapAuthApi(this WebApplication app)
    {
        app.MapPost("/api/auth/login", async (HttpContext ctx, LoginRequest request, VmsDbContext db,
            TokenService tokens, PasswordGovernance passwords, AuditService audit, ILogger<Program> logger) =>
        {
            // Servidor "desactivado": sin usuarios no hay login; hay que
            // completar la configuración inicial desde la máquina del servidor.
            if (!await db.Users.AnyAsync())
                return Results.Json(
                    new { error = "El sistema no está inicializado. Complete la configuración inicial desde la máquina del servidor.", setupRequired = true },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == request.Username && u.Enabled);
            if (user is null || !PasswordHasher.Verify(request.Password, user.PasswordHash, user.PasswordSalt))
            {
                logger.LogWarning("Auth: login rechazado para '{Username}' desde {Ip}.",
                    request.Username, ctx.Connection.RemoteIpAddress);
                await audit.LogAsAsync(ctx, null, request.Username ?? "", null, "auth", "login-failed",
                    detail: "Usuario o contraseña incorrectos.", success: false);
                return Results.Json(new { error = "Usuario o contraseña incorrectos." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (passwords.IsExpired(user))
            {
                await audit.LogAsAsync(ctx, user.Id, user.Username, user.Role, "auth", "login-blocked",
                    detail: "Login denegado: la contraseña está vencida y debe renovarse.", success: false);
                return Results.Json(
                    new { error = "La contraseña está vencida: debe definir una nueva para continuar.", mustChangePassword = true },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var (token, session) = tokens.Issue(user.Id, user.Username, user.Role);
            logger.LogInformation("Auth: sesión iniciada por {Username} desde {Ip}.",
                user.Username, ctx.Connection.RemoteIpAddress);
            await audit.LogAsAsync(ctx, user.Id, user.Username, user.Role, "auth", "login",
                detail: "Inicio de sesión correcto.",
                data: new { userAgent = ctx.Request.Headers.UserAgent.ToString() });
            return Results.Ok(new LoginResponse(token, user.Username, user.Role, session.ExpiresAt));
        });

        // Cambio de clave autenticado por la clave actual: funciona también con
        // la clave vencida (es el camino de renovación) y sin token previo.
        app.MapPost("/api/auth/change-password", async (HttpContext ctx, ChangePasswordRequest request, VmsDbContext db,
            TokenService tokens, PasswordGovernance passwords, AuditService audit) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == request.Username && u.Enabled);
            if (user is null || !PasswordHasher.Verify(request.CurrentPassword, user.PasswordHash, user.PasswordSalt))
                return Results.Json(new { error = "Usuario o contraseña incorrectos." }, statusCode: StatusCodes.Status401Unauthorized);

            if (await passwords.ValidateNewPasswordAsync(db, user.Id, request.NewPassword) is { } error)
                return Results.Json(new { error }, statusCode: StatusCodes.Status422UnprocessableEntity);

            passwords.SetPassword(user, request.NewPassword);
            await db.SaveChangesAsync();

            // Las sesiones anteriores quedan revocadas; se entrega una nueva.
            tokens.RevokeUser(user.Id);
            var (token, session) = tokens.Issue(user.Id, user.Username, user.Role);
            await audit.LogAsAsync(ctx, user.Id, user.Username, user.Role, "auth", "password-changed",
                detail: "Cambió su propia contraseña.");
            return Results.Ok(new LoginResponse(token, user.Username, user.Role, session.ExpiresAt));
        });

        app.MapPost("/api/auth/logout", async (HttpContext ctx, TokenService tokens, AuditService audit) =>
        {
            // La sesión se lee ANTES de revocar el token, para saber quién cerró.
            if (ApiSecurity.CurrentSession(ctx) is not null)
                await audit.LogAsync(ctx, "auth", "logout", detail: "Cierre de sesión.");
            string? header = ctx.Request.Headers.Authorization.FirstOrDefault();
            if (header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                tokens.Revoke(header["Bearer ".Length..].Trim());
            return Results.Ok();
        });

        app.MapGet("/api/auth/me", (HttpContext ctx) =>
            ApiSecurity.CurrentSession(ctx) is { } s
                ? Results.Ok(new { s.Username, s.Role, s.ExpiresAt })
                : Results.Unauthorized());
    }
}
