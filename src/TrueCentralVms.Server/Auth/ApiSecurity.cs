using TrueCentralVms.Core.Domain;

namespace TrueCentralVms.Server.Auth;

/// <summary>
/// Guardas de autorización compartidas por los módulos de la API (el middleware
/// de token deja la sesión en <c>HttpContext.Items["session"]</c> y su token
/// en <c>HttpContext.Items["token"]</c>).
/// </summary>
public static class ApiSecurity
{
    public static SessionInfo? CurrentSession(HttpContext ctx) => ctx.Items["session"] as SessionInfo;

    /// <summary>El token de esa sesión (el hub lo registra para cortar sus conexiones al revocarlo).</summary>
    public static string? CurrentToken(HttpContext ctx) => ctx.Items["token"] as string;

    /// <summary>Devuelve null si hay sesión válida; si no, el resultado de error a retornar.</summary>
    public static IResult? RequireUser(HttpContext ctx, out SessionInfo session)
    {
        session = null!;
        if (CurrentSession(ctx) is not { } s)
            return Results.Unauthorized();
        session = s;
        return null;
    }

    /// <summary>Igual que <see cref="RequireUser"/> pero exigiendo rol administrador.</summary>
    public static IResult? RequireAdmin(HttpContext ctx, out SessionInfo session)
    {
        var failure = RequireUser(ctx, out session);
        if (failure is not null) return failure;
        return session.Role == Roles.Admin
            ? null
            : Results.Json(new { error = "Requiere rol administrador." }, statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Exige sesión y que alguno de sus roles otorgue <paramref name="permission"/>
    /// (el administrador los tiene todos). El 403 queda en la bitácora.
    /// </summary>
    public static IResult? Require(HttpContext ctx, string permission, out SessionInfo session)
    {
        var failure = RequireUser(ctx, out session);
        if (failure is not null) return failure;
        return Has(ctx, permission) ? null : new PermissionDenied(session, [permission]);
    }

    /// <summary>Como <see cref="Require"/>, pero basta con uno de los permisos (endpoints que usan varios módulos).</summary>
    public static IResult? RequireAny(HttpContext ctx, out SessionInfo session, params string[] permissions)
    {
        var failure = RequireUser(ctx, out session);
        if (failure is not null) return failure;
        foreach (var permission in permissions)
            if (Has(ctx, permission)) return null;
        return new PermissionDenied(session, permissions);
    }

    /// <summary>¿La sesión de esta solicitud tiene el permiso? (el middleware deja su alcance en Items["scope"]).</summary>
    public static bool Has(HttpContext ctx, string permission) =>
        CurrentSession(ctx) is { } s && ctx.Items["scope"] is UserScope scope && scope.UserId == s.UserId
        && scope.Has(permission);

    /// <summary>
    /// 403 "su rol no le permite…", registrado en la bitácora (una vez cada
    /// 5 min por usuario y permiso: la interfaz no ofrece lo que no puede
    /// hacer, así que un intento suele ser una pantalla desactualizada o una
    /// solicitud armada a mano).
    /// </summary>
    private sealed class PermissionDenied(SessionInfo session, string[] permissions) : IResult
    {
        public async Task ExecuteAsync(HttpContext ctx)
        {
            string labels = string.Join(" o ", permissions.Select(Permissions.LabelOf).Select(l => $"\"{l}\""));
            var audit = ctx.RequestServices.GetRequiredService<Services.AuditService>();
            if (audit.ShouldLog($"permission-denied:{session.UserId}:{string.Join(',', permissions)}", TimeSpan.FromMinutes(5)))
                await audit.LogAsync(ctx, "auth", "permission-denied",
                    detail: $"Intentó {ctx.Request.Method} {ctx.Request.Path} sin el permiso {labels}.",
                    data: new { permissions }, success: false);
            await Results.Json(new { error = $"Su rol no le permite hacer esto (requiere {labels}).", permissions },
                statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(ctx);
        }
    }

    /// <summary>
    /// Exige que la solicitud provenga de la propia máquina del servidor
    /// (127.0.0.1 / ::1). Se usa para la configuración inicial y para el
    /// callback de autorización de MediaMTX.
    /// </summary>
    public static bool IsLoopback(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip);
}
