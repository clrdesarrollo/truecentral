using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Eventos en vivo del Cuadro vigía. Llegan por el hub (<c>LiveEvent</c>);
/// esta ruta pone al día a un puesto que recién conecta o que se reconectó.
/// Sin bitácora: es lo mismo que el hub ya le entregó (leer no cambia nada).
/// </summary>
public static class LiveEventsApi
{
    public static void MapLiveEventsApi(this WebApplication app)
    {
        app.MapGet("/api/live-events/recent", async (HttpContext ctx, int? take, LiveEventFeed feed) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.LiveView, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            return Results.Ok(feed.Recent(scope, take ?? 100));
        });
    }
}
