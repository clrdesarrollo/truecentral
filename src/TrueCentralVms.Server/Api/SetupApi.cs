using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Configuración inicial. El servidor se distribuye "desactivado": sin ningún
/// usuario en la base no hay login posible, y el único flujo habilitado es
/// crear el primer administrador — exclusivamente desde la propia máquina del
/// servidor (loopback).
/// </summary>
public static class SetupApi
{
    /// <summary>Serializa la creación del primer usuario ante solicitudes concurrentes.</summary>
    private static readonly SemaphoreSlim SetupLock = new(1, 1);

    public static void MapSetupApi(this WebApplication app, string serverVersion)
    {
        // Pública: el panel y el cliente la usan para decidir si muestran el
        // asistente de configuración inicial o el login normal.
        app.MapGet("/api/setup/status", async (HttpContext ctx, VmsDbContext db) =>
            Results.Ok(new SetupStatusDto(
                SetupRequired: !await db.Users.AnyAsync(),
                IsLocalRequest: ApiSecurity.IsLoopback(ctx),
                ServerVersion: serverVersion)));

        app.MapPost("/api/setup/admin", async (HttpContext ctx, SetupAdminRequest request, VmsDbContext db,
            PasswordGovernance passwords, TokenService tokens, Services.AuditService audit, ILogger<Program> logger) =>
        {
            if (!ApiSecurity.IsLoopback(ctx))
                return Results.Json(
                    new { error = "La configuración inicial solo puede realizarse desde la máquina del servidor (localhost)." },
                    statusCode: StatusCodes.Status403Forbidden);

            await SetupLock.WaitAsync();
            try
            {
                if (await db.Users.AnyAsync())
                    return Results.Json(new { error = "El sistema ya fue inicializado." }, statusCode: StatusCodes.Status409Conflict);

                string username = request.Username?.Trim() ?? "";
                if (username.Length is < 3 or > 64)
                    return Results.Json(new { error = "El nombre de usuario debe tener entre 3 y 64 caracteres." },
                        statusCode: StatusCodes.Status422UnprocessableEntity);

                if (await passwords.ValidateNewPasswordAsync(db, null, request.Password) is { } error)
                    return Results.Json(new { error }, statusCode: StatusCodes.Status422UnprocessableEntity);

                var user = new User { Username = username, Role = Roles.Admin };
                passwords.SetPassword(user, request.Password);
                db.Users.Add(user);
                await db.SaveChangesAsync();

                logger.LogInformation("Configuración inicial completada: administrador '{Username}' creado desde {Ip}.",
                    username, ctx.Connection.RemoteIpAddress);
                await audit.LogAsAsync(ctx, user.Id, user.Username, user.Role, "auth", "setup-admin",
                    targetType: "user", targetId: user.Id.ToString(), targetName: user.Username,
                    detail: "Configuración inicial: primer administrador creado desde la máquina del servidor.");

                // Sesión inmediata: el asistente queda logueado sin pedir login.
                var (token, session) = tokens.Issue(user.Id, user.Username, user.Role);
                return Results.Ok(new LoginResponse(token, user.Username, user.Role, session.ExpiresAt));
            }
            finally
            {
                SetupLock.Release();
            }
        });
    }
}
