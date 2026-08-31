using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
                return Results.Ok(ClipToDay(Merge(segments), day)
                    .Select(s => new RecordingSegmentDto(s.Start, s.End, s.Kind.ToString()))
                    .ToList());
            }
            catch (DriverException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        });

        // ------------------------------------------------------------------
        // Diagnóstico: a qué ritmo entrega el equipo una grabación, y si
        // acepta que se le pida más rápido (Scale / Rate-Control del RTSP).
        // Es la medición que decide si la velocidad de reproducción puede
        // funcionar de verdad o el equipo siempre pacea a tiempo real.
        // ------------------------------------------------------------------
        app.MapGet("/api/playback/{deviceId:int}/{rtspChannel:int}/rate-probe",
            async (HttpContext ctx, int deviceId, int rtspChannel, double? scale, bool? rateControlOff,
                int? seconds, DateTime? start, VmsDbContext db, DriverRegistry drivers,
                CredentialProtector protector, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;

            var channel = await db.Channels.Include(c => c.Device)
                .FirstOrDefaultAsync(c => c.DeviceId == deviceId && c.RtspChannel == rtspChannel, ct);
            if (channel is null) return Results.NotFound();

            var device = channel.Device;
            if (drivers.Find(device.DriverKey)?.Create() is not { } driver)
                return Results.Json(new { error = "El driver del equipo no está disponible." },
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            var from = start ?? DateTime.Now.Date.AddHours(14);
            var conn = new DeviceConnectionInfo(device.Host, device.SdkPort, device.Username,
                protector.Unprotect(device.PasswordCiphertext));
            if (driver.BuildPlaybackUrl(conn, device.RtspPort, rtspChannel, from, from.AddHours(1)) is not { } source)
                return Results.Json(new { error = "Este equipo no soporta reproducción remota." },
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            var result = await RtspRateProbe.MeasureAsync(source, scale ?? 1, rateControlOff ?? false,
                TimeSpan.FromSeconds(Math.Clamp(seconds ?? 10, 3, 30)), ct);
            return Results.Ok(result);
        });

        // ------------------------------------------------------------------
        // Días con grabación de un mes (marcas del calendario).
        // ------------------------------------------------------------------
        app.MapGet("/api/playback/{deviceId:int}/{channelNumber:int}/days",
            async (HttpContext ctx, int deviceId, int channelNumber, int? year, int? month, VmsDbContext db,
                DriverRegistry drivers, CredentialProtector protector, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;

            var now = DateTime.Now;
            int y = year ?? now.Year, m = month ?? now.Month;
            if (y is < 2000 or > 2100 || m is < 1 or > 12)
                return Results.Json(new { error = "Mes inválido." }, statusCode: StatusCodes.Status422UnprocessableEntity);

            var channel = await db.Channels.Include(c => c.Device)
                .FirstOrDefaultAsync(c => c.DeviceId == deviceId && c.ChannelNumber == channelNumber, ct);
            if (channel is null) return Results.NotFound();

            var device = channel.Device;
            if (drivers.Find(device.DriverKey)?.Create() is not { } driver)
                return Results.Ok(Array.Empty<int>());

            var conn = new DeviceConnectionInfo(device.Host, device.SdkPort, device.Username,
                protector.Unprotect(device.PasswordCiphertext));
            try
            {
                // El calendario usa el índice RTSP del canal (es el que arma el track).
                var days = await driver.QueryRecordedDaysAsync(conn, channel.RtspChannel, y, m, ct);
                return Results.Ok(days);
            }
            catch (Exception)
            {
                // Sin marcas el calendario sigue sirviendo para elegir la fecha.
                return Results.Ok(Array.Empty<int>());
            }
        });

        // ------------------------------------------------------------------
        // Concesión de reproducción: ruta dinámica en MediaMTX + token corto.
        // ------------------------------------------------------------------
        app.MapPost("/api/playback/request", async (HttpContext ctx, PlaybackRequestDto request, VmsDbContext db,
            DriverRegistry drivers, CredentialProtector protector, StreamTokenService streamTokens,
            MediaMtxManager mtx, Services.Rtsp.PlaybackRelayManager relays, IConfiguration config,
            CancellationToken ct) =>
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

            // A 1× manda MediaMTX, que pulsa el equipo (camino de siempre).
            // A otra velocidad el servidor toma la sesión RTSP para poder
            // pedirle al equipo que entregue más rápido (cabecera Scale), y
            // publica él mismo en la ruta.
            double speed = request.Speed;
            bool accelerated = speed > 0 && Math.Abs(speed - 1) > 0.01;
            if (accelerated)
            {
                if (!await mtx.AddPublishPathAsync(path, ct))
                    return Results.Json(new { error = "No se pudo preparar la ruta de reproducción en el media server." },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                if (!await relays.StartAsync(path, source, speed, mtx.PublishUrlFor(path), ct))
                    return Results.Json(
                        new { error = $"El equipo no aceptó reproducir a {speed:0.##}×." },
                        statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            else if (!await mtx.AddPlaybackPathAsync(path, source, ct))
            {
                return Results.Json(new { error = "No se pudo preparar la ruta de reproducción en el media server." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var (token, grant) = streamTokens.Issue(session.UserId, session.Username, path,
                device.Id, device.Name, request.RtspChannel, "playback");
            string host = config["Streaming:PublicHost"] is { Length: > 0 } configured
                ? configured
                : ctx.Request.Host.Host;
            string url = $"rtsp://{host}:{mtx.RtspPort}/{path}?token={token}";
            return Results.Ok(new StreamGrantDto(url, token, grant.ExpiresAt, driver.SupportsExactPlaybackSeek));
        });

        // ------------------------------------------------------------------
        // Descarga de un tramo como archivo MP4. El servidor tira del equipo
        // con FFmpeg (video copiado tal cual, sin recodificar) y va enviando
        // el archivo a medida que llega: las credenciales del equipo nunca
        // salen del servidor y el cliente solo recibe un MP4.
        // ------------------------------------------------------------------
        app.MapGet("/api/playback/{deviceId:int}/{rtspChannel:int}/download",
            async (HttpContext ctx, int deviceId, int rtspChannel, DateTime start, DateTime end,
                VmsDbContext db, DriverRegistry drivers, CredentialProtector protector,
                ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var logger = loggers.CreateLogger("Playback");

            if (end <= start || end - start > TimeSpan.FromHours(2))
                return Results.Json(new { error = "El tramo a descargar es inválido (máximo 2 horas)." },
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            var channel = await db.Channels.Include(c => c.Device)
                .FirstOrDefaultAsync(c => c.DeviceId == deviceId && c.RtspChannel == rtspChannel, ct);
            if (channel is null) return Results.NotFound();

            var device = channel.Device;
            if (drivers.Find(device.DriverKey)?.Create() is not { } driver)
                return Results.Json(new { error = "El driver del equipo no está disponible." },
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            var conn = new DeviceConnectionInfo(device.Host, device.SdkPort, device.Username,
                protector.Unprotect(device.PasswordCiphertext));
            if (driver.BuildPlaybackUrl(conn, device.RtspPort, rtspChannel, start, end) is not { } source)
                return Results.Json(new { error = "Este equipo aún no soporta reproducción remota desde el VMS." },
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            if (MediaMtxManager.LocateFfmpeg() is not { } ffmpeg)
                return Results.Json(new { error = "El servidor no tiene FFmpeg disponible para exportar grabaciones." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // Video copiado (sin costo de CPU ni pérdida) y audio a AAC, que es
            // el único que aceptan todos los reproductores dentro de un MP4
            // (los equipos suelen entregar G.711). Se descartan los flujos de
            // datos privados del fabricante, que el contenedor MP4 rechaza.
            foreach (string argument in new[]
                     {
                         // -nostdin: el hijo hereda la consola del servidor y
                         // FFmpeg leería de ahí las teclas de control.
                         "-hide_banner", "-loglevel", "error", "-nostdin",
                         "-rtsp_transport", "tcp",
                         "-i", source,
                         "-t", ((int)(end - start).TotalSeconds).ToString(),
                         "-map", "0:v:0", "-map", "0:a:0?",
                         "-c:v", "copy", "-c:a", "aac",
                         // Sin esto el MP4 hereda los rótulos del RTSP del
                         // equipo (Hikvision anuncia su sesión como "HIK Media
                         // Server Vx.y") y los reproductores los muestran como
                         // título del archivo. Se reemplazan por el origen real
                         // del tramo, que es lo que sirve como evidencia.
                         "-map_metadata", "-1",
                         "-metadata", $"title={device.Name} · {channel.Name} — {start:dd-MM-yyyy HH:mm:ss}",
                         "-metadata", $"comment=Exportado por CLR TrueCentral VMS · {device.Name} ({device.Host}) " +
                                      $"canal {channel.ChannelNumber} · {start:dd-MM-yyyy HH:mm:ss} a {end:HH:mm:ss}",
                         "-movflags", "frag_keyframe+empty_moov+default_base_moof",
                         "-f", "mp4", "pipe:1",
                     })
                psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi);
            if (process is null)
                return Results.Json(new { error = "No se pudo iniciar la exportación en el servidor." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            ChildProcessJob.Attach(process);

            // El error se lee siempre: si nadie vacía la tubería, FFmpeg se
            // queda bloqueado al llenarse el buffer. Sin token de cancelación
            // a propósito: la lectura termina sola al cerrarse la tubería
            // cuando el proceso muere.
            var errorTask = process.StandardError.ReadToEndAsync();
            try
            {
                var output = process.StandardOutput.BaseStream;
                byte[] buffer = new byte[64 * 1024];

                // Primer bloque ANTES de responder: si el equipo no entrega
                // video, el cliente recibe un error legible y no un MP4 vacío.
                // Con tope de espera propio: un equipo que acepta la conexión
                // y no manda nada dejaría la solicitud colgada para siempre.
                int read;
                using (var firstBlock = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    firstBlock.CancelAfter(TimeSpan.FromSeconds(60));
                    try
                    {
                        read = await output.ReadAsync(buffer, firstBlock.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning("La exportación de {Device} canal {Channel} no recibió video en 60 s.",
                            device.Name, rtspChannel);
                        return Results.Json(new { error = "El equipo no entregó video para ese tramo (tiempo de espera agotado)." },
                            statusCode: StatusCodes.Status422UnprocessableEntity);
                    }
                }
                if (read == 0)
                {
                    await process.WaitForExitAsync(ct);
                    string error = await errorTask;
                    logger.LogWarning("Exportación de grabación fallida ({Device} canal {Channel}): {Error}",
                        device.Name, rtspChannel, error.Trim());
                    return Results.Json(new { error = "El equipo no entregó video para ese tramo." },
                        statusCode: StatusCodes.Status422UnprocessableEntity);
                }

                string fileName = $"{Sanitize(device.Name)}_{Sanitize(channel.Name)}_{start:yyyyMMdd_HHmmss}.mp4";
                ctx.Response.ContentType = "video/mp4";
                ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
                await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                await output.CopyToAsync(ctx.Response.Body, ct);
                await process.WaitForExitAsync(ct);

                if (process.ExitCode != 0)
                    logger.LogWarning("La exportación de {Device} canal {Channel} terminó con código {Code}: {Error}",
                        device.Name, rtspChannel, process.ExitCode, (await errorTask).Trim());
                else
                    logger.LogInformation("{User} exportó {Device} canal {Channel} desde {Start:g} ({Minutes} min).",
                        session.Username, device.Name, rtspChannel, start, (int)(end - start).TotalMinutes);
            }
            catch (OperationCanceledException)
            {
                // El usuario canceló la descarga: se corta el tirón del equipo.
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* ya terminó */ }
                }
            }
            return Results.Empty;
        });
    }

    /// <summary>
    /// Une archivos contiguos del mismo tipo: los grabadores parten el día en
    /// trozos (por hora, por evento) y la línea de tiempo quiere tramos limpios.
    /// </summary>
    /// <summary>
    /// Recorta los tramos al día consultado. Los grabadores devuelven el
    /// ARCHIVO completo que cubre el rango, así que uno que empezó a grabar
    /// antes de la medianoche llega con su hora de inicio del día anterior:
    /// sin recortar, la línea de tiempo del cliente recibe un tramo que no
    /// cabe en el día que está dibujando.
    /// </summary>
    private static List<RecordingSegment> ClipToDay(List<RecordingSegment> segments, DateTime day)
    {
        var from = day;
        var to = day.AddDays(1);
        var clipped = new List<RecordingSegment>(segments.Count);
        foreach (var segment in segments)
        {
            var start = segment.Start < from ? from : segment.Start;
            var end = segment.End > to ? to : segment.End;
            if (end > start)
                clipped.Add(segment with { Start = start, End = end });
        }
        return clipped;
    }

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

    /// <summary>
    /// Nombre de archivo seguro para la cabecera Content-Disposition: sin
    /// tildes (la forma simple de la cabecera es ASCII) ni caracteres que
    /// Windows rechace en un nombre de archivo.
    /// </summary>
    private static string Sanitize(string name)
    {
        var clean = new StringBuilder();
        foreach (char c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue; // tilde suelta tras descomponer la letra
            if (char.IsAsciiLetterOrDigit(c))
                clean.Append(c);
            else if (clean.Length > 0 && clean[^1] != '_')
                clean.Append('_');
        }
        string result = clean.ToString().Trim('_');
        if (result.Length > 40) result = result[..40].TrimEnd('_');
        return result.Length > 0 ? result : "grabacion";
    }
}
