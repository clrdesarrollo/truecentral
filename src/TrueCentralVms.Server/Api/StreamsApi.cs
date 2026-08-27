using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

public static class StreamsApi
{
    public static void MapStreamsApi(this WebApplication app)
    {
        // ------------------------------------------------------------------
        // Concesión de streaming: valida al usuario y el canal, emite un token
        // de corta vida y devuelve la URL RTSP del media server embebido.
        // ------------------------------------------------------------------
        app.MapPost("/api/streams/request", async (HttpContext ctx, StreamRequestDto request, VmsDbContext db,
            StreamTokenService streamTokens, MediaMtxManager mtx, IConfiguration config) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;

            if (!mtx.IsRunning)
                return Results.Json(new { error = "El servicio de streaming no está disponible en el servidor." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var channel = await db.Channels.Include(c => c.Device)
                .FirstOrDefaultAsync(c => c.DeviceId == request.DeviceId && c.RtspChannel == request.RtspChannel);
            if (channel is null) return Results.NotFound();
            if (!channel.Enabled)
                return Results.Json(new { error = "El canal está deshabilitado." },
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            string profile = request.Profile == StreamProfile.Main ? "main" : "sub";
            string path = MediaMtxManager.PathName(request.DeviceId, request.RtspChannel, request.Profile);
            var (token, grant) = streamTokens.Issue(session.UserId, session.Username, path,
                channel.DeviceId, channel.Device.Name, request.RtspChannel, profile);

            // Host visible para los clientes: configurable (NAT/DNS) o el del request.
            string host = config["Streaming:PublicHost"] is { Length: > 0 } configured
                ? configured
                : ctx.Request.Host.Host;
            string url = $"rtsp://{host}:{mtx.RtspPort}/{path}?token={token}";
            return Results.Ok(new StreamGrantDto(url, token, grant.ExpiresAt));
        });

        // ------------------------------------------------------------------
        // Sesiones activas (dashboard de administración)
        // ------------------------------------------------------------------
        app.MapGet("/api/streams/active", async (HttpContext ctx, VmsDbContext db) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var active = await db.StreamSessions
                .Where(s => s.EndedAt == null)
                .OrderBy(s => s.StartedAt)
                .Select(s => new ActiveSessionDto(s.Id, s.Username, s.DeviceName, s.RtspChannel, s.Profile, s.ClientIp, s.StartedAt))
                .ToListAsync();
            return Results.Ok(active);
        });
    }
}
