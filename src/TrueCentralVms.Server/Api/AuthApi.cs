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
            LoginThrottle throttle, ILogger<Program> logger) =>
        {
            if (string.IsNullOrEmpty(request.Username) || request.Password is null)
                return Results.Json(new { error = "Indique usuario y contraseña." }, statusCode: StatusCodes.Status400BadRequest);

            // Servidor "desactivado": sin usuarios no hay login; hay que
            // completar la configuración inicial desde la máquina del servidor.
            if (!await db.Users.AnyAsync())
                return Results.Json(
                    new { error = "El sistema no está inicializado. Complete la configuración inicial desde la máquina del servidor.", setupRequired = true },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            if (await ThrottledAsync(ctx, request.Username, throttle, audit, "login-throttled") is { } throttled) return throttled;
            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == request.Username && u.Enabled);
            if (!PasswordMatches(user, request.Password))
            {
                logger.LogWarning("Auth: login rechazado para '{Username}' desde {Ip}.",
                    request.Username, ctx.Connection.RemoteIpAddress);
                await audit.LogAsAsync(ctx, user?.Id, request.Username, user?.Role, "auth", "login-failed",
                    detail: "Usuario o contraseña incorrectos.", success: false);
                await RecordFailureAsync(ctx, request.Username, user, throttle, audit);
                return Results.Json(new { error = "Usuario o contraseña incorrectos." }, statusCode: StatusCodes.Status401Unauthorized);
            }
            throttle.RecordSuccess(request.Username);

            if (passwords.IsExpired(user))
            {
                await audit.LogAsAsync(ctx, user.Id, user.Username, user.Role, "auth", "login-blocked",
                    detail: "Login denegado: la contraseña está vencida y debe renovarse.", success: false);
                return Results.Json(
                    new { error = "La contraseña está vencida: debe definir una nueva para continuar.", mustChangePassword = true },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            // Cupo de clientes de escritorio simultáneos (la cabecera la manda
            // solo el cliente WPF; el panel web no consume puesto).
            string? clientId = ctx.Request.Headers.TryGetValue("X-TCVMS-Client", out var clientHeader)
                ? clientHeader.ToString() : null;
            // Sin licencia vigente (vencida, revocada, prueba terminada, reloj
            // atrasado) el cliente de escritorio no entra: es la consola de
            // operación. El panel web sí, porque desde ahí se regulariza la
            // licencia (Sistema → Licencia).
            if (clientId is not null && !license.IsOperational)
            {
                var snapshot = license.Snapshot;
                string reason = snapshot.Message ?? "El sistema no tiene una licencia vigente.";
                await audit.LogAsAsync(ctx, user.Id, user.Username, user.Role, "license", "license-denied",
                    targetType: "client", targetName: TokenService.SeatOf(clientId),
                    detail: "Ingreso del cliente de escritorio denegado: licencia no vigente. " + reason, success: false);
                // Al operador, un mensaje corto y sin datos de la licencia; el
                // detalle queda en la bitácora y en el panel web.
                bool expired = snapshot.ExpiresUtc is { } expires && expires <= DateTime.UtcNow;
                return Results.Json(new
                {
                    error = expired
                        ? "La licencia del sistema expiró. No es posible iniciar sesión."
                        : "La licencia del sistema no está vigente. No es posible iniciar sesión.",
                    licenseDenied = true,
                }, statusCode: StatusCodes.Status402PaymentRequired);
            }
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
            if (clientId is not null
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
            TokenService tokens, PasswordGovernance passwords, AuditService audit, LoginThrottle throttle) =>
        {
            if (string.IsNullOrEmpty(request.Username) || request.CurrentPassword is null || request.NewPassword is null)
                return Results.Json(new { error = "Indique usuario, contraseña actual y contraseña nueva." }, statusCode: StatusCodes.Status400BadRequest);
            // Prueba una contraseña sin sesión, igual que el ingreso: mismo freno y misma bitácora.
            if (await ThrottledAsync(ctx, request.Username, throttle, audit, "password-change-throttled") is { } throttled) return throttled;
            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == request.Username && u.Enabled);
            if (!PasswordMatches(user, request.CurrentPassword))
            {
                await audit.LogAsAsync(ctx, user?.Id, request.Username, user?.Role, "auth", "password-change-failed",
                    detail: "Cambio de contraseña rechazado: usuario o contraseña actual incorrectos.", success: false);
                await RecordFailureAsync(ctx, request.Username, user, throttle, audit);
                return Results.Json(new { error = "Usuario o contraseña incorrectos." }, statusCode: StatusCodes.Status401Unauthorized);
            }
            throttle.RecordSuccess(request.Username);

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

        app.MapGet("/api/auth/me", async (HttpContext ctx, VmsDbContext db, LicenseService license) =>
        {
            if (ApiSecurity.CurrentSession(ctx) is not { } s) return Results.Unauthorized();
            // Su alcance (roles + límite propio), para mostrarlo: el servidor ya filtra todo lo demás.
            var scope = await ctx.ScopeAsync(s);
            var dto = new UserScopeDto(!scope.Unrestricted, scope.ViewOutside, scope.GrantedLocations, scope.Labels);
            // Sus permisos (unión de sus roles): la interfaz oculta lo demás.
            var roleNames = await db.UserRoles.Where(ur => ur.UserId == s.UserId)
                .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name).OrderBy(n => n).ToListAsync();
            // Y los módulos de la licencia: lo que no incluye se oculta a todos.
            var permissions = new MyPermissionsDto(scope.IsAdmin, scope.EffectivePermissions.ToList(), roleNames,
                license.LicensedModules(), license.IsOperational);
            return Results.Ok(new { s.UserId, s.Username, s.Role, s.ExpiresAt, Scope = dto, Permissions = permissions });
        });

        // Mi cuenta: lo que el usuario conectado puede saber de sí mismo (roles,
        // alcance, contraseña, sesión y últimos ingresos). Es su propia información:
        // no se audita la consulta.
        app.MapGet("/api/auth/account", async (HttpContext ctx, VmsDbContext db, PasswordGovernance passwords, CancellationToken ct) =>
        {
            if (ApiSecurity.CurrentSession(ctx) is not { } s) return Results.Unauthorized();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == s.UserId, ct);
            if (user is null) return Results.Unauthorized();
            var scope = await ctx.ScopeAsync(s);
            var roles = await db.UserRoles.Where(ur => ur.UserId == s.UserId)
                .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name).OrderBy(n => n).ToListAsync(ct);
            var logins = await db.AuditEvents.AsNoTracking()
                .Where(e => e.UserId == s.UserId && e.Category == "auth" && e.Action == "login")
                .OrderByDescending(e => e.Timestamp).Take(2)
                .Select(e => new { at = e.Timestamp, ip = e.ClientIp, origin = e.Origin }).ToListAsync(ct);
            // Intentos fallidos con su nombre desde el ingreso anterior (señal de que alguien probó su clave).
            DateTime since = logins.Count > 1 ? logins[1].at : user.CreatedAt;
            int failed = await db.AuditEvents.AsNoTracking()
                .CountAsync(e => e.Username == user.Username && e.Category == "auth" && e.Action == "login-failed" && e.Timestamp > since, ct);
            return Results.Ok(new
            {
                user.Username,
                isAdmin = scope.IsAdmin,
                user.IsSuperAdmin,
                roles,
                user.CreatedAt,
                restricted = !scope.Unrestricted,
                scope = scope.Labels,
                passwordChangedAt = user.PasswordChangedAt,
                passwordExpiresAt = passwords.ExpiresAt(user),
                sessionExpiresAt = s.ExpiresAt,
                currentLogin = logins.FirstOrDefault(),
                previousLogin = logins.Skip(1).FirstOrDefault(),
                failedSincePrevious = failed,
            });
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

    // Hash de relleno para usuarios que no existen: se calcula igual el PBKDF2, para que el tiempo de respuesta no
    // delate qué nombres de usuario existen.
    private static readonly (string Hash, string Salt) DummyPassword = PasswordHasher.Hash(Guid.NewGuid().ToString("N"));

    private static bool PasswordMatches([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Data.Entities.User? user, string password)
    {
        if (user is not null) return PasswordHasher.Verify(password, user.PasswordHash, user.PasswordSalt);
        PasswordHasher.Verify(password, DummyPassword.Hash, DummyPassword.Salt);
        return false;
    }

    /// <summary>429 si la cuenta o la IP están bloqueadas por intentos fallidos (y lo deja en la bitácora).</summary>
    private static async Task<IResult?> ThrottledAsync(HttpContext ctx, string username, LoginThrottle throttle, AuditService audit,
        string action)
    {
        if (throttle.LockedFor(username, ctx.Connection.RemoteIpAddress) is not { } left) return null;
        await audit.LogAsAsync(ctx, null, username, null, "auth", action,
            detail: $"Intento rechazado sin probar la contraseña: hay un bloqueo vigente por intentos fallidos (quedan {LoginThrottle.Describe(left)}).",
            success: false);
        ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(left.TotalSeconds)).ToString();
        return Results.Json(new { error = $"Demasiados intentos fallidos. Espere {LoginThrottle.Describe(left)} antes de volver a intentarlo." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    private static async Task RecordFailureAsync(HttpContext ctx, string username, Data.Entities.User? user, LoginThrottle throttle,
        AuditService audit)
    {
        if (Random.Shared.Next(50) == 0) throttle.Prune();
        if (throttle.RecordFailure(username, ctx.Connection.RemoteIpAddress) is not { } locked) return;
        string what = locked == "IP"
            ? $"la IP {ctx.Connection.RemoteIpAddress} ({LoginThrottle.MaxIpFailures} intentos fallidos)"
            : locked == "cuenta"
                ? $"la cuenta «{username}» ({LoginThrottle.MaxAccountFailures} intentos fallidos seguidos)"
                : $"la cuenta «{username}» y la IP {ctx.Connection.RemoteIpAddress}";
        await audit.LogAsAsync(ctx, user?.Id, username, user?.Role, "auth", "login-locked",
            detail: $"Se bloqueó {what} por {LoginThrottle.Describe(LoginThrottle.LockTime)}: posible ataque de fuerza bruta.",
            success: false);
    }
}
