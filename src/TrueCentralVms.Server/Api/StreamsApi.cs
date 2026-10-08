using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Core.Domain;

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
            if (ApiSecurity.RequireAny(ctx, out var session, Permissions.LiveView, Permissions.WallOperate, Permissions.EventsAttend) is { } failure) return failure;

            if (!mtx.IsRunning)
                return Results.Json(new { error = "El servicio de streaming no está disponible en el servidor." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var grant = await IssueGrantAsync(ctx, session, db, streamTokens, request.DeviceId, request.RtspChannel, request.Profile);
            if (grant.Failure is not null) return grant.Failure;

            // Host visible para los clientes: configurable (NAT/DNS) o el del request.
            string host = config["Streaming:PublicHost"] is { Length: > 0 } configured
                ? configured
                : ctx.Request.Host.Host;
            string url = $"rtsp://{host}:{mtx.RtspPort}/{grant.Path}?token={grant.Token}";
            return Results.Ok(new StreamGrantDto(url, grant.Token, grant.ExpiresAt));
        });

        // ------------------------------------------------------------------
        // Vista en vivo del panel web (WebRTC/WHEP). El navegador manda su
        // oferta SDP y el servidor, tras las MISMAS validaciones de la
        // concesión RTSP (permiso, canal habilitado, alcance), la reenvía a la
        // señalización local de MediaMTX con un token de corta vida. MediaMTX
        // canjea el token contra /api/streaming/auth (auditoría incluida) y el
        // video viaja directo navegador <-> MediaMTX por el puerto ICE.
        // ------------------------------------------------------------------
        app.MapPost("/api/streams/webrtc", async (HttpContext ctx, WebRtcOfferDto request, VmsDbContext db,
            StreamTokenService streamTokens, MediaMtxManager mtx, ILogger<Program> logger, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAny(ctx, out var session, Permissions.LiveView, Permissions.WallOperate, Permissions.EventsAttend) is { } failure) return failure;

            if (!mtx.IsRunning)
                return Results.Json(new { error = "El servicio de streaming no está disponible en el servidor." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            if (!mtx.WebRtcEnabled)
                return Results.Json(new { error = "La vista en vivo del panel web está deshabilitada en este servidor (Streaming:WebRtcIcePort = 0)." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            if (string.IsNullOrWhiteSpace(request.Offer) || request.Offer.Length > 64 * 1024 || !request.Offer.StartsWith("v=0"))
                return Results.Json(new { error = "Oferta WebRTC inválida." }, statusCode: StatusCodes.Status400BadRequest);

            var grant = await IssueGrantAsync(ctx, session, db, streamTokens, request.DeviceId, request.RtspChannel, request.Profile);
            if (grant.Failure is not null) return grant.Failure;

            var remote = ctx.Connection.RemoteIpAddress;
            string? clientIp = remote is null ? null : (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString();
            MediaMtxManager.WhepAnswer answer;
            try
            {
                answer = await mtx.WhepOfferAsync(grant.Path, grant.Token, request.Offer, clientIp, ct);
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
            {
                logger.LogWarning("WebRTC: la señalización de MediaMTX no respondió para '{Path}': {Message}", grant.Path, ex.Message);
                return Results.Json(new { error = "El servidor de video no respondió a tiempo. Reintente en unos segundos." },
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }

            if (answer.Status is 200 or 201 && answer.SessionUrl is { } sessionUrl)
                return Results.Ok(new WebRtcAnswerDto(answer.Body, mtx.RegisterWhepSession(session.UserId, sessionUrl)));

            string reason = WhepErrorText(answer.Body);
            logger.LogInformation("WebRTC: MediaMTX rechazó '{Path}' para {User} ({Status}): {Reason}",
                grant.Path, session.Username, answer.Status, reason);
            // Nunca 401: el panel lo tomaría como sesión vencida y llevaría al ingreso.
            return Results.Json(new { error = TranslateWhepError(answer.Status, reason), detail = reason },
                statusCode: answer.Status is 400 or 404 ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status502BadGateway);
        });

        // Cerrar un cuadro: libera la sesión WebRTC al instante (sin esperar a
        // que MediaMTX note que el navegador se fue).
        app.MapDelete("/api/streams/webrtc/{id}", async (HttpContext ctx, string id, MediaMtxManager mtx, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            if (mtx.TakeWhepSession(id, session.UserId) is not { } sessionUrl) return Results.NotFound();
            await mtx.WhepDeleteAsync(sessionUrl, ct);
            return Results.Ok();
        });

        // ------------------------------------------------------------------
        // Expulsar una sesión: corta la conexión RTSP en MediaMTX y cierra la
        // auditoría. No bloquea al usuario (puede volver a conectarse).
        // ------------------------------------------------------------------
        app.MapPost("/api/streams/{id:int}/kick", async (HttpContext ctx, int id, VmsDbContext db,
            MediaMtxManager mtx, Hubs.ScopedHub hub, AuditService audit, ILogger<Program> logger) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SessionsManage, out var admin) is { } failure) return failure;

            var session = await db.StreamSessions.FirstOrDefaultAsync(s => s.Id == id && s.EndedAt == null);
            if (session is null) return Results.NotFound();

            bool kicked = await mtx.KickSessionAsync(session.MtxSessionId);
            session.EndedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await SessionAccounting.BroadcastActiveSessionsAsync(db, hub);
            logger.LogInformation("Streaming: {Admin} expulsó la sesión de {User} ({Device} canal {Channel}).",
                admin.Username, session.Username, session.DeviceName, session.RtspChannel);
            await audit.LogAsync(ctx, "live", "session-kicked",
                targetType: "channel", targetId: $"{session.DeviceId}/{session.RtspChannel}",
                targetName: $"{session.DeviceName} · canal {session.RtspChannel}",
                detail: $"Expulsó la sesión de video de '{session.Username}' sobre '{session.DeviceName}' " +
                        $"canal {session.RtspChannel} ({session.Profile}).",
                success: kicked,
                data: new { kickedUser = session.Username, session.ClientIp, session.StartedAt });
            return Results.Ok(new { kicked });
        });

        // ------------------------------------------------------------------
        // Sesiones activas (dashboard de administración)
        // ------------------------------------------------------------------
        app.MapGet("/api/streams/active", async (HttpContext ctx, VmsDbContext db) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SessionsManage, out _) is { } failure) return failure;
            var active = await db.StreamSessions
                .Where(s => s.EndedAt == null)
                .OrderBy(s => s.StartedAt)
                .Select(s => new ActiveSessionDto(s.Id, s.Username, s.DeviceName, s.RtspChannel, s.Profile, s.ClientIp, s.StartedAt))
                .ToListAsync();
            return Results.Ok(active);
        });
    }

    private sealed record GrantResult(IResult? Failure, string Path = "", string Token = "", DateTime ExpiresAt = default);

    /// <summary>
    /// Validaciones comunes de una concesión de video en vivo (canal existente
    /// y habilitado, alcance por ubicación) y emisión del token de corta vida
    /// para la ruta de MediaMTX. La comparten el RTSP del cliente de escritorio
    /// y el WebRTC del panel web.
    /// </summary>
    private static async Task<GrantResult> IssueGrantAsync(HttpContext ctx, SessionInfo session, VmsDbContext db,
        StreamTokenService streamTokens, int deviceId, int rtspChannel, StreamProfile requested)
    {
        var channel = await db.Channels.Include(c => c.Device)
            .FirstOrDefaultAsync(c => c.DeviceId == deviceId && c.RtspChannel == rtspChannel);
        if (channel is null) return new GrantResult(Results.NotFound());
        if (!channel.Enabled)
            return new GrantResult(Results.Json(new { error = "El canal está deshabilitado." },
                statusCode: StatusCodes.Status422UnprocessableEntity));
        // Alcance por ubicación: es el único paso antes de MediaMTX (que solo
        // acepta tokens emitidos aquí), así que basta con validarlo aquí.
        if (!(await ctx.ScopeAsync(session)).CanView(channel.LocationId))
            return new GrantResult(await ctx.OutOfScopeAsync(session, "channel", $"{channel.DeviceId}/{channel.RtspChannel}",
                $"{channel.Device.Name} · {channel.Name}", "ver en vivo"));

        string profile = requested == StreamProfile.Main ? "main" : "sub";
        string path = MediaMtxManager.PathName(deviceId, rtspChannel, requested);
        var (token, grant) = streamTokens.Issue(session.UserId, session.Username, path,
            channel.DeviceId, channel.Device.Name, rtspChannel, profile);
        return new GrantResult(null, path, token, grant.ExpiresAt);
    }

    /// <summary>Mensaje de error de MediaMTX (JSON {"error": "..."} o texto).</summary>
    private static string WhepErrorText(string body)
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error) && error.GetString() is { Length: > 0 } text)
                return text;
        }
        catch (System.Text.Json.JsonException) { /* no era JSON */ }
        return body.Trim();
    }

    /// <summary>Rechazo de MediaMTX en palabras del operador.</summary>
    private static string TranslateWhepError(int status, string reason)
    {
        if (reason.Contains("codec", StringComparison.OrdinalIgnoreCase))
            return "Este navegador no puede reproducir el formato de video de la cámara por WebRTC " +
                   "(típicamente H.265 sin decodificación por hardware). Use Chrome actualizado, o configure " +
                   "el stream de la cámara en H.264.";
        if (reason.Contains("no stream is available", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("not ready", StringComparison.OrdinalIgnoreCase))
            return "La cámara no está entregando video (sin señal, equipo desconectado o no respondió a tiempo).";
        if (status is 401 or 403)
            return "El servidor de video rechazó la concesión: vuelva a abrir la cámara.";
        return $"El servidor de video rechazó la conexión ({status}): {reason}";
    }
}
