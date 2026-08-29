using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Reproducción remota: las grabaciones viven en el almacenamiento del propio
/// equipo (DVR/NVR/tarjeta). El servidor consulta los segmentos por el driver
/// y, para reproducir, monta una ruta dinámica en MediaMTX cuyo source es la
/// URL RTSP de reproducción del fabricante con el rango pedido — las
/// credenciales del equipo nunca llegan al cliente y el video sale por el
/// mismo puerto y esquema de tokens del vivo.
/// </summary>
public static class PlaybackApi
{
    public static void MapPlaybackApi(this WebApplication app)
    {
        // ------------------------------------------------------------------
        // Segmentos grabados de un canal para un día (hora LOCAL del equipo).
        // ------------------------------------------------------------------
        app.MapGet("/api/playback/{deviceId:int}/{channelNumber:int}/segments",
            async (HttpContext ctx, int deviceId, int channelNumber, DateTime? date, VmsDbContext db,
                DriverRegistry drivers, CredentialProtector protector, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;

            var channel = await db.Channels.Include(c => c.Device)
                .FirstOrDefaultAsync(c => c.DeviceId == deviceId && c.ChannelNumber == channelNumber, ct);
            if (channel is null) return Results.NotFound();

            var device = channel.Device;
            if (drivers.Find(device.DriverKey)?.Create() is not { } driver)
                return Results.Json(new { error = "El driver del equipo no está disponible." },
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            var day = (date ?? DateTime.Now).Date;
            var conn = new DeviceConnectionInfo(device.Host, device.SdkPort, device.Username,
                protector.Unprotect(device.PasswordCiphertext));
            try
            {
                var segments = await driver.QueryRecordingsAsync(conn, channelNumber, day, day.AddDays(1), ct);
                return Results.Ok(Merge(segments)
                    .Select(s => new RecordingSegmentDto(s.Start, s.End, s.Kind.ToString()))
                    .ToList());
            }
            catch (DriverException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        });

        // ------------------------------------------------------------------
        // Concesión de reproducción: ruta dinámica en MediaMTX + token corto.
        // ------------------------------------------------------------------
        app.MapPost("/api/playback/request", async (HttpContext ctx, PlaybackRequestDto request, VmsDbContext db,
            DriverRegistry drivers, CredentialProtector protector, StreamTokenService streamTokens,
            MediaMtxManager mtx, IConfiguration config, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;

            if (!mtx.IsRunning)
                return Results.Json(new { error = "El servicio de streaming no está disponible en el servidor." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            if (request.EndLocal <= request.StartLocal ||
                request.EndLocal - request.StartLocal > TimeSpan.FromHours(24))
                return Results.Json(new { error = "El rango de reproducción es inválido (máximo 24 horas)." },
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            var channel = await db.Channels.Include(c => c.Device)
                .FirstOrDefaultAsync(c => c.DeviceId == request.DeviceId && c.RtspChannel == request.RtspChannel, ct);
            if (channel is null) return Results.NotFound();
            if (!channel.Enabled)
                return Results.Json(new { error = "El canal está deshabilitado." },
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            var device = channel.Device;
            if (drivers.Find(device.DriverKey)?.Create() is not { } driver)
                return Results.Json(new { error = "El driver del equipo no está disponible." },
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            var conn = new DeviceConnectionInfo(device.Host, device.SdkPort, device.Username,
                protector.Unprotect(device.PasswordCiphertext));
            string? source = driver.BuildPlaybackUrl(conn, device.RtspPort, request.RtspChannel,
                request.StartLocal, request.EndLocal);
            if (source is null)
                return Results.Json(new { error = "Este equipo aún no soporta reproducción remota desde el VMS." },
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            // Nombre plano y único: cada espectador/rango tiene su propia ruta.
            string path = $"pb-{device.Id}-{request.RtspChannel}-" +
                          Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            if (!await mtx.AddPlaybackPathAsync(path, source, ct))
                return Results.Json(new { error = "No se pudo preparar la ruta de reproducción en el media server." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var (token, grant) = streamTokens.Issue(session.UserId, session.Username, path,
                device.Id, device.Name, request.RtspChannel, "playback");
            string host = config["Streaming:PublicHost"] is { Length: > 0 } configured
                ? configured
                : ctx.Request.Host.Host;
            string url = $"rtsp://{host}:{mtx.RtspPort}/{path}?token={token}";
            return Results.Ok(new StreamGrantDto(url, token, grant.ExpiresAt));
        });
    }

    /// <summary>
    /// Une archivos contiguos del mismo tipo: los grabadores parten el día en
    /// trozos (por hora, por evento) y la línea de tiempo quiere tramos limpios.
    /// </summary>
    private static List<RecordingSegment> Merge(IReadOnlyList<RecordingSegment> raw)
    {
        var merged = new List<RecordingSegment>();
        foreach (var segment in raw.OrderBy(s => s.Start))
        {
            if (merged.Count > 0 && merged[^1].Kind == segment.Kind &&
                segment.Start - merged[^1].End <= TimeSpan.FromSeconds(3))
            {
                if (segment.End > merged[^1].End)
                    merged[^1] = merged[^1] with { End = segment.End };
            }
            else
            {
                merged.Add(segment);
            }
        }
        return merged;
    }
}
