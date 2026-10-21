using System.Diagnostics;
using System.Net.WebSockets;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Entrega alternativa del video al panel web para lo que WebRTC no admite
/// (H.265 con B-frames: MediaMTX corta la sesión al primer B-frame). FFmpeg
/// lee la ruta de MediaMTX con un token de corta vida —la misma autorización,
/// alcance y auditoría que cualquier lector— y la reempaqueta SIN recodificar
/// a MP4 fragmentado, que el navegador decodifica con Media Source Extensions
/// (H.265 por hardware en Chrome/Edge actuales). El costo en el servidor es
/// solo de copia: no hay decodificación ni codificación.
/// </summary>
public static class MseRelay
{
    /// <summary>
    /// Bombea el MP4 fragmentado de <paramref name="rtspUrl"/> hacia el
    /// WebSocket hasta que el navegador cierre, el origen termine o se cancele.
    /// Solo video: el audio de las cámaras (G.711) no cabe en MP4 sin recodificar.
    /// </summary>
    public static async Task RunAsync(WebSocket ws, string ffmpeg, string rtspUrl, string label,
        ILogger logger, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-nostdin",
                     "-fflags", "nobuffer", "-rtsp_transport", "tcp", "-i", rtspUrl,
                     "-map", "0:v:0", "-c:v", "copy", "-an",
                     // Fragmentos cortos (~100 ms) para poca latencia; empty_moov
                     // deja el init segment al comienzo, como lo pide MSE.
                     "-f", "mp4", "-movflags", "frag_keyframe+empty_moov+default_base_moof",
                     "-frag_duration", "100000", "-flush_packets", "1", "pipe:1",
                 })
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        string stderr = "";
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Relé MSE: no se pudo iniciar FFmpeg para {Label}: {Message}", label, ex.Message);
            await CloseAsync(ws, WebSocketCloseStatus.InternalServerError, "No se pudo iniciar FFmpeg en el servidor.");
            return;
        }
        var stderrTask = Task.Run(async () =>
        {
            try { stderr = await process.StandardError.ReadToEndAsync(ct); } catch { /* cancelado */ }
        }, CancellationToken.None);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // El navegador no manda nada: leer solo sirve para enterarse de que cerró.
        var readTask = Task.Run(async () =>
        {
            var buffer = new byte[256];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(buffer, linked.Token);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch { /* cerrado o cancelado */ }
            linked.Cancel();
        }, CancellationToken.None);

        long sent = 0;
        try
        {
            var stdout = process.StandardOutput.BaseStream;
            var chunk = new byte[64 * 1024];
            while (!linked.IsCancellationRequested)
            {
                int n = await stdout.ReadAsync(chunk, linked.Token);
                if (n <= 0) break;
                await ws.SendAsync(chunk.AsMemory(0, n), WebSocketMessageType.Binary, true, linked.Token);
                sent += n;
            }
        }
        catch (OperationCanceledException) { /* el navegador cerró */ }
        catch (WebSocketException) { /* conexión perdida */ }
        catch (IOException) { /* FFmpeg terminó */ }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* ya terminó */ }
            linked.Cancel();
            await Task.WhenAny(stderrTask, Task.Delay(1000, CancellationToken.None));
        }

        if (sent == 0)
        {
            string reason = stderr.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
            logger.LogInformation("Relé MSE: {Label} no entregó video. {Reason}", label, reason);
            await CloseAsync(ws, WebSocketCloseStatus.EndpointUnavailable, "El equipo no entregó video.");
        }
        else
        {
            await CloseAsync(ws, WebSocketCloseStatus.NormalClosure, "Fin del video.");
        }
        await readTask;
    }

    private static async Task CloseAsync(WebSocket ws, WebSocketCloseStatus status, string reason)
    {
        if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.CloseAsync(status, reason, timeout.Token);
        }
        catch { /* el navegador ya se fue */ }
    }
}
