using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Callback de autorización de MediaMTX (authMethod: http). Por cada intento
/// de conexión, MediaMTX manda un POST con {user, password, token, ip,
/// action, path, protocol, id, query}; 2xx autoriza y cualquier otra cosa
/// rechaza. Solo se aceptan lecturas con token vigente emitido por
/// /api/streams/request; publicar está siempre prohibido (no hay publishers
/// en este producto). El endpoint solo responde a loopback: MediaMTX corre en
/// la misma máquina.
/// </summary>
public static class StreamingAuthApi
{
    public static void MapStreamingAuthApi(this WebApplication app)
    {
        app.MapPost("/api/streaming/auth", async (HttpContext ctx, StreamTokenService streamTokens,
            VmsDbContext db, IHubContext<VmsHub> hub, Services.MediaMtxManager mtx, ILogger<Program> logger) =>
        {
            if (!ApiSecurity.IsLoopback(ctx))
                return Results.NotFound(); // ni siquiera revelar que existe

            using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
            var root = body.RootElement;
            string GetString(string name) =>
                root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

            string action = GetString("action");
            string path = GetString("path");
            string query = GetString("query");
            string ip = GetString("ip");
            string mtxId = GetString("id");

            // El token viaja en el query de la URL RTSP (?token=...).
            string? token = null;
            foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (pair.StartsWith("token=", StringComparison.OrdinalIgnoreCase))
                {
                    token = Uri.UnescapeDataString(pair["token=".Length..]);
                    break;
                }
            }
            token ??= GetString("token") is { Length: > 0 } t ? t : null;

            // Publicar solo puede hacerlo el relé FFmpeg local (rutas con
            // runOnDemand para cámaras de SDP inválido): loopback + secreto
            // por arranque. Nada más publica en este producto.
            if (action == "publish")
            {
                if (ip is "127.0.0.1" or "::1" && token is not null &&
                    CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(token),
                        System.Text.Encoding.UTF8.GetBytes(mtx.PublishSecret)))
                    return Results.Ok();
                logger.LogWarning("MediaMTX: publicación rechazada en '{Path}' desde {Ip}.", path, ip);
                return Results.Unauthorized();
            }

            if (action != "read")
            {
                logger.LogWarning("MediaMTX: acción '{Action}' rechazada (ruta '{Path}', ip {Ip}).", action, path, ip);
                return Results.Unauthorized();
            }

            if (token is null || streamTokens.Validate(token, path) is not { } grant)
            {
                logger.LogWarning("MediaMTX: lectura rechazada en '{Path}' desde {Ip} (token ausente, vencido o de otra ruta).", path, ip);
                return Results.Unauthorized();
            }

            // DESCRIBE y PLAY llegan como callbacks separados: solo el primero
            // por token crea la fila de auditoría (los demás solo autorizan).
            if (!streamTokens.TryMarkSessionRecorded(token))
                return Results.Ok();

            // Auditoría: quién empezó a ver qué. El cierre lo detecta SessionAccounting.
            db.StreamSessions.Add(new StreamSession
            {
                UserId = grant.UserId,
                Username = grant.Username,
                DeviceId = grant.DeviceId,
                DeviceName = grant.DeviceName,
                RtspChannel = grant.RtspChannel,
                Profile = grant.Profile,
                ClientIp = ip,
                Path = path,
                MtxSessionId = mtxId,
            });
            await db.SaveChangesAsync();
            logger.LogInformation("Streaming: {User} comenzó a ver {Device} canal {Channel} ({Profile}) desde {Ip}.",
                grant.Username, grant.DeviceName, grant.RtspChannel, grant.Profile, ip);

            await SessionAccounting.BroadcastActiveSessionsAsync(db, hub);
            return Results.Ok();
        });
    }
}
