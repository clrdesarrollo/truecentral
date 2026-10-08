using TrueCentralVms.Server.Services.Licensing;
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
            TokenService tokens, PasswordGovernance passwords, AuditService audit, LicenseService license,
            ILogger<Program> logger) =>
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

            // Cupo de clientes de escritorio simultáneos (la cabecera la manda
            // solo el cliente WPF; el panel web no consume puesto). Con la
            // licencia restringida el login sigue: el cliente muestra el aviso.
            string? clientId = ctx.Request.Headers.TryGetValue("X-TCVMS-Client", out var clientHeader)
                ? clientHeader.ToString() : null;
            // Un administrador acaba de liberar el puesto de este equipo: el
            // cliente que seguía abierto no puede recuperarlo renovando solo la
            // sesión (403 = el cliente vuelve al ingreso con este aviso).
            if (tokens.IsSeatReleased(clientId))
            {
                await audit.LogAsAsync(ctx, user.Id, user.Username, user.Role, "auth", "login-blocked",
                    targetType: "client", targetName: TokenService.SeatOf(clientId),
                    detail: "Ingreso denegado: un administrador acaba de liberar el puesto de este equipo.", success: false);
                return Results.Json(new { error = "Un administrador cerró la sesión de este equipo para liberar su puesto. " +
                                                  "Podrá volver a ingresar en un minuto." },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            if (clientId is not null && license.IsOperational
                && license.Deny(null, LicenseFeatures.MaxClientSessions, tokens.CountDesktopSessions(excludingClientId: clientId)) is { } denied)
            {
                await audit.LogAsAsync(ctx, user.Id, user.Username, user.Role, "license", "license-denied",
                    targetType: "client", targetName: clientId, detail: denied, success: false);
                return Results.Json(new { error = denied, licenseDenied = true }, statusCode: StatusCodes.Status402PaymentRequired);
            }

            var (token, session) = tokens.Issue(user.Id, user.Username, user.Role, clientId);
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

        app.MapGet("/api/auth/me", async (HttpContext ctx, VmsDbContext db) =>
        {
            if (ApiSecurity.CurrentSession(ctx) is not { } s) return Results.Unauthorized();
            // Su alcance (roles + límite propio), para mostrarlo: el servidor ya filtra todo lo demás.
            var scope = await ctx.ScopeAsync(s);
            var dto = new UserScopeDto(!scope.Unrestricted, scope.ViewOutside, scope.GrantedLocations, scope.Labels);
            // Sus permisos (unión de sus roles): la interfaz oculta lo demás.
            var roleNames = await db.UserRoles.Where(ur => ur.UserId == s.UserId)
                .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name).OrderBy(n => n).ToListAsync();
            var permissions = new MyPermissionsDto(scope.IsAdmin, scope.EffectivePermissions.ToList(), roleNames);
            return Results.Ok(new { s.UserId, s.Username, s.Role, s.ExpiresAt, Scope = dto, Permissions = permissions });
        });

        // Qué puede operar esta sesión: la interfaz deshabilita el resto (el
        // servidor valida igual cada orden). Cambia con el alcance del usuario y
        // con la ubicación de los recursos: los puestos lo releen con esos avisos.
        app.MapGet("/api/auth/operable", async (HttpContext ctx) =>
        {
            if (ApiSecurity.CurrentSession(ctx) is not { } s) return Results.Unauthorized();
            return Results.Ok((await ctx.ScopeAsync(s)).Operable());
        });

        // Vigencia de la sesión, sin más trabajo: el panel la consulta al volver
        // a la pestaña, al recuperar el foco, cada minuto y a la hora del
        // vencimiento, para avisar en cuanto termina aunque nadie toque nada.
        // 401 = terminó (venció, la cortó un administrador o el servidor se reinició).
        app.MapGet("/api/auth/session", (HttpContext ctx) =>
            ApiSecurity.CurrentSession(ctx) is { } s
                ? Results.Ok(new { s.ExpiresAt })
                : Results.Unauthorized());
    }
}
