using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TrueCentralVms.Server.Services;

/// <summary>Resultado de medir a qué ritmo entrega el equipo una grabación.</summary>
/// <param name="MediaSeconds">Segundos de video que llegaron (según las marcas de tiempo RTP).</param>
/// <param name="WallSeconds">Segundos de reloj que tomó recibirlos.</param>
/// <param name="Factor">MediaSeconds / WallSeconds: 1 = tiempo real, 4 = cuatro veces más rápido.</param>
public sealed record RtspRateResult(
    bool Success, string? Error, double MediaSeconds, double WallSeconds, double Factor,
    long Bytes, int Packets, string? ServerHeader, bool ScaleAccepted, string? PlayResponse,
    string? Challenge = null, string? RequestUrl = null, string? AuthDiagnostic = null);

/// <summary>
/// Cliente RTSP mínimo para medir el ritmo real de entrega de una grabación.
///
/// Existe porque la reproducción remota llega a 1× exacto: el grabador pacea
/// el envío a tiempo real, y por ese camino ninguna velocidad mayor puede
/// funcionar (no hay más video llegando). Para pasar de ahí hay que pedirle al
/// equipo que deje de pacear —cabecera <c>Scale</c> y/o <c>Rate-Control: no</c>
/// del RTSP—, y esto sirve para comprobar si el equipo las honra ANTES de
/// construir el relé completo sobre esa suposición.
///
/// Mide con las marcas de tiempo RTP del video (reloj de 90 kHz), no con los
/// bytes: los bytes dependen del bitrate de la escena, y lo que interesa es
/// cuántos segundos de grabación entran por cada segundo de reloj.
/// </summary>
public static class RtspRateProbe
{
    private const int VideoClockHz = 90000;

    public static async Task<RtspRateResult> MeasureAsync(string rtspUrl, double scale, bool rateControlOff,
        TimeSpan duration, CancellationToken ct)
    {
        var uri = new Uri(rtspUrl);

        // Credenciales y URL limpia se sacan de la CADENA, no del Uri: para un
        // esquema que .NET no conoce (rtsp) UserInfo llega vacío, y con eso se
        // manda una autenticación en blanco que el equipo rechaza con 401.
        int schemeEnd = rtspUrl.IndexOf("://", StringComparison.Ordinal) + 3;
        int at = rtspUrl.IndexOf('@', schemeEnd);
        string user = "", password = "";
        if (at > 0)
        {
            string[] parts = rtspUrl[schemeEnd..at].Split(':', 2);
            user = Uri.UnescapeDataString(parts[0]);
            password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        }
        // El digest se calcula sobre esta cadena EXACTA, así que se arma a mano
        // (UriBuilder normaliza de forma impredecible un esquema desconocido).
        string baseUrl = at > 0
            ? string.Concat(rtspUrl.AsSpan(0, schemeEnd), rtspUrl.AsSpan(at + 1))
            : rtspUrl;

        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(uri.Host, uri.Port <= 0 ? 554 : uri.Port, ct);
        await using var stream = tcp.GetStream();

        var session = new RtspSession(stream, baseUrl, user, password);
        try
        {
            var describe = await session.SendAsync("DESCRIBE", baseUrl, ct, ("Accept", "application/sdp"));
            if (describe.Status != 200)
                return Fail($"El equipo respondió {describe.Status} a DESCRIBE.") with
                {
                    Challenge = HeaderValue(describe.Headers, "WWW-Authenticate"),
                    RequestUrl = baseUrl,
                    AuthDiagnostic = session.Diagnostic,
                };

            string control = FirstVideoControl(describe.Body, baseUrl);
            var setup = await session.SendAsync("SETUP", control, ct,
                ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"));
            if (setup.Status != 200)
                return Fail($"El equipo respondió {setup.Status} a SETUP.");
            session.SessionId = HeaderValue(setup.Headers, "Session")?.Split(';')[0].Trim();

            var extra = new List<(string, string)> { ("Range", "npt=0-") };
            if (scale > 0 && Math.Abs(scale - 1) > 0.001)
                extra.Add(("Scale", scale.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)));
            if (rateControlOff)
                extra.Add(("Rate-Control", "no"));

            var play = await session.SendAsync("PLAY", baseUrl, ct, [.. extra]);
            if (play.Status != 200)
                return Fail($"El equipo respondió {play.Status} a PLAY.");

            // Un equipo que ACEPTA Scale lo devuelve en la respuesta.
            string? echoed = HeaderValue(play.Headers, "Scale");
            bool accepted = echoed is not null &&
                            double.TryParse(echoed, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double back) &&
                            Math.Abs(back - scale) < 0.01;

            var (media, wall, bytes, packets) = await ReadInterleavedAsync(stream, duration, ct);
            return new RtspRateResult(true, null, media, wall, wall > 0 ? media / wall : 0,
                bytes, packets, HeaderValue(describe.Headers, "Server"), accepted,
                Summarize(play.Headers));
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return Fail(ex.Message);
        }
        finally
        {
            try { await session.SendAsync("TEARDOWN", baseUrl, CancellationToken.None); }
            catch (Exception) { /* la sesión se cierra igual al soltar el socket */ }
        }

        static RtspRateResult Fail(string message) => new(false, message, 0, 0, 0, 0, 0, null, false, null);
    }

    /// <summary>
    /// Consume el flujo entrelazado ($ canal + largo + RTP) y mide cuánto
    /// tiempo de VIDEO llegó, mirando la marca de tiempo del primer y del
    /// último paquete del canal 0.
    /// </summary>
    private static async Task<(double Media, double Wall, long Bytes, int Packets)> ReadInterleavedAsync(
        NetworkStream stream, TimeSpan duration, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(duration);

        byte[] header = new byte[4];
        byte[] payload = new byte[65536];
        long bytes = 0;
        int packets = 0;
        uint firstStamp = 0, lastStamp = 0;
        bool haveFirst = false;

        try
        {
            while (!window.IsCancellationRequested)
            {
                await ReadExactAsync(stream, header, 1, window.Token);
                if (header[0] != (byte)'$') continue; // respuesta RTSP intercalada: se ignora
                await ReadExactAsync(stream, header, 3, window.Token);
                int channel = header[0];
                int length = (header[1] << 8) | header[2];
                if (length <= 0 || length > payload.Length) break;

                await ReadExactAsync(stream, payload, length, window.Token);
                bytes += length;

                // Canal 0 = RTP de video. La marca de tiempo va en los bytes 4..7.
                if (channel == 0 && length >= 12)
                {
                    uint stamp = (uint)((payload[4] << 24) | (payload[5] << 16) | (payload[6] << 8) | payload[7]);
                    if (!haveFirst) { firstStamp = stamp; haveFirst = true; }
                    lastStamp = stamp;
                    packets++;
                }
            }
        }
        catch (OperationCanceledException) { /* se cumplió la ventana de medición */ }
        catch (IOException) { /* el equipo cortó: se informa lo alcanzado */ }

        double media = haveFirst ? unchecked(lastStamp - firstStamp) / (double)VideoClockHz : 0;
        return (media, clock.Elapsed.TotalSeconds, bytes, packets);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int chunk = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (chunk <= 0) throw new IOException("El equipo cerró la conexión.");
            read += chunk;
        }
    }

    /// <summary>URL de control de la primera pista de video del SDP.</summary>
    private static string FirstVideoControl(string sdp, string baseUrl)
    {
        bool inVideo = false;
        foreach (string line in sdp.Split('\n'))
        {
            string text = line.Trim();
            if (text.StartsWith("m=", StringComparison.Ordinal))
                inVideo = text.StartsWith("m=video", StringComparison.Ordinal);
            else if (inVideo && text.StartsWith("a=control:", StringComparison.Ordinal))
            {
                string control = text["a=control:".Length..].Trim();
                if (control is "*" or "") return baseUrl;
                return control.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
                    ? control
                    : baseUrl.TrimEnd('/') + "/" + control.TrimStart('/');
            }
        }
        return baseUrl;
    }

    private static string? HeaderValue(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.TryGetValue(name.ToLowerInvariant(), out string? value) ? value : null;

    private static string Summarize(IReadOnlyDictionary<string, string> headers) =>
        string.Join(" · ", headers.Where(h => h.Key is "scale" or "speed" or "range" or "rtp-info" or "rate-control")
            .Select(h => $"{h.Key}={h.Value}"));

    // ------------------------------------------------------------------
    // Diálogo RTSP con autenticación digest
    // ------------------------------------------------------------------

    private sealed class RtspSession(NetworkStream stream, string baseUrl, string user, string password)
    {
        private int _sequence;
        private string? _realm, _nonce, _qop, _opaque;
        private int _nonceCount;
        /// <summary>El equipo pidió Basic (varios DVR Hikvision lo hacen) en vez de Digest.</summary>
        private bool _basic;

        public string? SessionId { get; set; }

        /// <summary>Estado de la autenticación, para diagnosticar un 401 sin exponer la clave.</summary>
        public string Diagnostic =>
            $"usuario='{user}' largoClave={password.Length} basic={_basic} realm='{_realm}' " +
            $"qop='{_qop}' intentos={_attempts}";

        private int _attempts;

        public async Task<RtspResponse> SendAsync(string method, string url, CancellationToken ct,
            params (string Name, string Value)[] headers)
        {
            var response = await SendOnceAsync(method, url, headers, ct);
            if (response.Status != 401 || (_realm is null && !_basic))
                return response;
            // El reto llegó con la primera respuesta: se repite ya autenticado.
            return await SendOnceAsync(method, url, headers, ct);
        }

        private async Task<RtspResponse> SendOnceAsync(string method, string url,
            (string Name, string Value)[] headers, CancellationToken ct)
        {
            var request = new StringBuilder();
            request.Append($"{method} {url} RTSP/1.0\r\n");
            request.Append($"CSeq: {++_sequence}\r\n");
            request.Append("User-Agent: CLR TrueCentral VMS\r\n");
            if (SessionId is not null) request.Append($"Session: {SessionId}\r\n");
            if (_basic)
            {
                // Varios DVR Hikvision solo ofrecen Basic sobre RTSP.
                string pair = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
                request.Append($"Authorization: Basic {pair}\r\n");
            }
            else if (_realm is not null && _nonce is not null)
            {
                request.Append($"Authorization: {DigestFor(method, url)}\r\n");
            }
            foreach (var (name, value) in headers)
                request.Append($"{name}: {value}\r\n");
            request.Append("\r\n");

            byte[] data = Encoding.ASCII.GetBytes(request.ToString());
            await stream.WriteAsync(data, ct);
            return await ReadResponseAsync(ct);
        }

        /// <summary>
        /// Autenticación digest. Los firmwares actuales ofrecen el reto con
        /// <c>qop=auth</c>, que cambia el cálculo (entran el contador y un
        /// nonce del cliente): sin eso el equipo responde 401 una y otra vez.
        /// </summary>
        private string DigestFor(string method, string url)
        {
            string ha1 = Md5($"{user}:{_realm}:{password}");
            string ha2 = Md5($"{method}:{url}");

            var header = new StringBuilder(
                $"Digest username=\"{user}\", realm=\"{_realm}\", nonce=\"{_nonce}\", uri=\"{url}\"");

            string response;
            if (_qop is { Length: > 0 })
            {
                string count = (++_nonceCount).ToString("x8");
                string cnonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
                response = Md5($"{ha1}:{_nonce}:{count}:{cnonce}:auth:{ha2}");
                header.Append($", qop=auth, nc={count}, cnonce=\"{cnonce}\"");
            }
            else
            {
                response = Md5($"{ha1}:{_nonce}:{ha2}");
            }

            header.Append($", response=\"{response}\"");
            if (_opaque is { Length: > 0 })
                header.Append($", opaque=\"{_opaque}\"");
            return header.ToString();
        }

        private static string Md5(string text) =>
            Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));

        private async Task<RtspResponse> ReadResponseAsync(CancellationToken ct)
        {
            var head = new StringBuilder();
            byte[] one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                int read = await stream.ReadAsync(one.AsMemory(0, 1), ct);
                if (read <= 0) throw new IOException("El equipo cerró la conexión durante la respuesta RTSP.");
                head.Append((char)one[0]);
                if (head.Length > 16384) throw new IOException("Respuesta RTSP demasiado larga.");
            }

            string text = head.ToString();
            string[] lines = text.Split("\r\n");
            int status = lines[0].Split(' ') is [_, string code, ..] && int.TryParse(code, out int value) ? value : 0;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1))
            {
                if (line.Length == 0) break;
                int colon = line.IndexOf(':');
                if (colon > 0)
                    headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
            }

            if (status == 401 && headers.TryGetValue("www-authenticate", out string? challenge))
            {
                // Varios DVR Hikvision solo ofrecen Basic sobre RTSP.
                _basic = challenge.TrimStart().StartsWith("Basic", StringComparison.OrdinalIgnoreCase);
                _realm = Between(challenge, "realm=\"", "\"");
                _nonce = Between(challenge, "nonce=\"", "\"");
                _qop = Between(challenge, "qop=\"", "\"") ?? (challenge.Contains("qop=auth", StringComparison.OrdinalIgnoreCase) ? "auth" : null);
                _opaque = Between(challenge, "opaque=\"", "\"");
                _nonceCount = 0;
            }

            string body = "";
            if (headers.TryGetValue("content-length", out string? lengthText) &&
                int.TryParse(lengthText, out int length) && length > 0)
            {
                byte[] buffer = new byte[length];
                await ReadExactAsync(stream, buffer, length, ct);
                body = Encoding.UTF8.GetString(buffer);
            }
            return new RtspResponse(status, headers, body);
        }

        private static string? Between(string text, string start, string end)
        {
            int from = text.IndexOf(start, StringComparison.OrdinalIgnoreCase);
            if (from < 0) return null;
            from += start.Length;
            int to = text.IndexOf(end, from, StringComparison.Ordinal);
            return to < 0 ? null : text[from..to];
        }
    }

    private sealed record RtspResponse(int Status, IReadOnlyDictionary<string, string> Headers, string Body);
}
