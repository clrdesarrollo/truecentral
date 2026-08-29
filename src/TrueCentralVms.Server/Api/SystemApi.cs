using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

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
    }
}
