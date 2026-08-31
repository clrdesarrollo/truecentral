using System.Text;
using System.Text.RegularExpressions;

namespace TrueCentralVms.Server.Services.Rtsp;

/// <summary>
/// Relé de reproducción a velocidad distinta de 1×.
///
/// Por qué existe: el grabador pacea la entrega a tiempo real salvo que se le
/// pida otra cosa en el PLAY (cabecera <c>Scale</c>), y MediaMTX —que es quien
/// pulsa el equipo en el camino normal— no la envía. Medido contra un DVR
/// real: sin pedir nada llega 0,98×; con <c>Scale: 4.000</c>, 3,93×.
///
/// Entonces, SOLO para velocidades distintas de 1×, el servidor toma la
/// sesión: abre él mismo el RTSP contra el equipo con el Scale pedido y
/// reenvía los paquetes hacia MediaMTX publicando en la ruta de la concesión.
/// El camino de 1× queda intacto (MediaMTX pulsa el equipo como siempre): así
/// la reproducción normal no depende de este relé.
///
/// Los paquetes se reenvían tal cual, sin decodificar ni recodificar: es un
/// puente de transporte, no un transcodificador.
/// </summary>
public sealed class RtspScaleRelay : IAsyncDisposable
{
    private readonly RtspConnection _source;
    private readonly RtspConnection _sink;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ILogger _logger;
    private readonly string _pathName;
    private Task? _pump;

    private RtspScaleRelay(RtspConnection source, RtspConnection sink, string pathName, ILogger logger)
    {
        _source = source;
        _sink = sink;
        _pathName = pathName;
        _logger = logger;
    }

    /// <summary>Cuántos flujos (video, audio) quedaron enlazados.</summary>
    public int MediaCount { get; private set; }

    /// <summary>
    /// Abre la reproducción en el equipo con el <paramref name="scale"/> pedido
    /// y la publica en <paramref name="publishUrl"/> (la ruta de MediaMTX de
    /// esta concesión).
    /// </summary>
    public static async Task<RtspScaleRelay> StartAsync(string deviceUrl, double scale, string publishUrl,
        string pathName, ILogger logger, CancellationToken ct)
    {
        var source = new RtspConnection(deviceUrl);
        var sink = new RtspConnection(publishUrl);
        var relay = new RtspScaleRelay(source, sink, pathName, logger);
        try
        {
            await relay.OpenAsync(scale, ct);
            return relay;
        }
        catch
        {
            await relay.DisposeAsync();
            throw;
        }
    }

    private async Task OpenAsync(double scale, CancellationToken ct)
    {
        // ---- Equipo: describir y preparar cada flujo ----
        await _source.ConnectAsync(ct);
        var describe = await _source.SendAsync("DESCRIBE", _source.BaseUrl, ct,
            [("Accept", "application/sdp")]);
        if (describe.Status != 200)
            throw new InvalidOperationException($"El equipo respondió {describe.Status} a DESCRIBE.");

        var medias = ParseMedias(describe.Body, _source.BaseUrl);
        if (medias.Count == 0)
            throw new InvalidOperationException("El equipo no ofreció ningún flujo de video.");
        MediaCount = medias.Count;

        for (int i = 0; i < medias.Count; i++)
        {
            var setup = await _source.SendAsync("SETUP", medias[i].Control, ct,
                [("Transport", $"RTP/AVP/TCP;unicast;interleaved={2 * i}-{2 * i + 1}")]);
            if (setup.Status != 200)
                throw new InvalidOperationException($"El equipo respondió {setup.Status} a SETUP.");
            _source.SessionId ??= setup.Header("Session")?.Split(';')[0].Trim();
        }

        // ---- Media server: anunciar los MISMOS flujos y quedar publicando ----
        await _sink.ConnectAsync(ct);
        string sdp = RewriteForPublish(describe.Body, medias.Count);
        var announce = await _sink.SendAsync("ANNOUNCE", _sink.BaseUrl, ct, body: sdp);
        if (announce.Status != 200)
            throw new InvalidOperationException($"El media server respondió {announce.Status} a ANNOUNCE.");

        for (int i = 0; i < medias.Count; i++)
        {
            var setup = await _sink.SendAsync("SETUP", $"{_sink.BaseUrl}/streamid={i}", ct,
                [("Transport", $"RTP/AVP/TCP;unicast;interleaved={2 * i}-{2 * i + 1};mode=record")]);
            if (setup.Status != 200)
                throw new InvalidOperationException($"El media server respondió {setup.Status} a SETUP.");
            _sink.SessionId ??= setup.Header("Session")?.Split(';')[0].Trim();
        }

        var record = await _sink.SendAsync("RECORD", _sink.BaseUrl, ct, [("Range", "npt=0-")]);
        if (record.Status != 200)
            throw new InvalidOperationException($"El media server respondió {record.Status} a RECORD.");

        // ---- Recién ahora se le pide al equipo que empiece, y a qué ritmo ----
        var play = await _source.SendAsync("PLAY", _source.BaseUrl, ct,
        [
            ("Range", "npt=0-"),
            ("Scale", scale.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)),
        ]);
        if (play.Status != 200)
            throw new InvalidOperationException($"El equipo respondió {play.Status} a PLAY.");

        _pump = Task.Run(() => PumpAsync(_stopping.Token), CancellationToken.None);
        _logger.LogInformation("Relé de reproducción a {Scale}× activo en la ruta '{Path}' ({Medias} flujo(s)).",
            scale, _pathName, medias.Count);
    }

    /// <summary>Reenvía los paquetes del equipo al media server hasta que se corte.</summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        byte[] buffer = new byte[65536];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await _source.ReadFrameAsync(buffer, ct);
                if (frame is not { } packet) break;
                await _sink.WriteFrameAsync(packet.Channel, buffer.AsMemory(0, packet.Length), ct);
            }
        }
        catch (OperationCanceledException) { /* cierre pedido */ }
        catch (Exception ex)
        {
            // Fin de la grabación o corte de red: el cliente lo ve como fin del tramo.
            _logger.LogDebug("El relé de la ruta '{Path}' terminó: {Error}", _pathName, ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // SDP
    // ------------------------------------------------------------------

    private sealed record MediaLine(string Control);

    /// <summary>Flujos del SDP con su URL de control (la que pide el SETUP).</summary>
    private static List<MediaLine> ParseMedias(string sdp, string baseUrl)
    {
        var medias = new List<MediaLine>();
        string? pending = null;
        bool inMedia = false;

        foreach (string raw in sdp.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                if (inMedia) medias.Add(new MediaLine(Resolve(pending, baseUrl, medias.Count)));
                inMedia = true;
                pending = null;
            }
            else if (inMedia && line.StartsWith("a=control:", StringComparison.Ordinal))
            {
                pending = line["a=control:".Length..].Trim();
            }
        }
        if (inMedia) medias.Add(new MediaLine(Resolve(pending, baseUrl, medias.Count)));
        return medias;

        static string Resolve(string? control, string baseUrl, int index)
        {
            if (string.IsNullOrEmpty(control) || control == "*")
                return $"{baseUrl.TrimEnd('/')}/trackID={index}";
            return control.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
                ? control
                : baseUrl.TrimEnd('/') + "/" + control.TrimStart('/');
        }
    }

    /// <summary>
    /// Deja el SDP del equipo listo para anunciarlo al media server: la
    /// conexión pasa a ser genérica y cada flujo se identifica con el
    /// <c>streamid</c> que espera el servidor al publicar.
    /// </summary>
    private static string RewriteForPublish(string sdp, int mediaCount)
    {
        var output = new StringBuilder();
        int index = -1;
        foreach (string raw in sdp.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("a=control:", StringComparison.Ordinal))
                continue; // se reemplaza más abajo, por flujo
            if (line.StartsWith("c=", StringComparison.Ordinal))
            {
                output.Append("c=IN IP4 0.0.0.0\r\n");
                continue;
            }
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                index++;
                // El puerto va en 0: el transporte real se acuerda en el SETUP.
                output.Append(Regex.Replace(line, @"^(m=\w+) \d+", "$1 0")).Append("\r\n");
                output.Append($"a=control:streamid={index}\r\n");
                continue;
            }
            if (line.Length > 0) output.Append(line).Append("\r\n");
        }
        return output.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_pump is not null)
        {
            try { await _pump.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception) { /* el bombeo se corta al cerrar los sockets */ }
        }
        try { await _source.SendAsync("TEARDOWN", _source.BaseUrl, CancellationToken.None); }
        catch (Exception) { /* la sesión se cierra igual al soltar el socket */ }
        try { await _sink.SendAsync("TEARDOWN", _sink.BaseUrl, CancellationToken.None); }
        catch (Exception) { /* ídem */ }
        await _source.DisposeAsync();
        await _sink.DisposeAsync();
        _stopping.Dispose();
    }
}
