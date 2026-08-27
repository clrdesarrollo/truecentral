using TrueCentralVms.Core.Domain;

namespace TrueCentralVms.Server.Auth;

/// <summary>
/// Guardas de autorización compartidas por los módulos de la API (el middleware
/// de token deja la sesión en <c>HttpContext.Items["session"]</c>).
/// </summary>
public static class ApiSecurity
{
    public static SessionInfo? CurrentSession(HttpContext ctx) => ctx.Items["session"] as SessionInfo;

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
    /// Exige que la solicitud provenga de la propia máquina del servidor
    /// (127.0.0.1 / ::1). Se usa para la configuración inicial y para el
    /// callback de autorización de MediaMTX.
    /// </summary>
    public static bool IsLoopback(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip);
}
