using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TrueCentralVms.Server.Services.Rtsp;

/// <summary>Respuesta RTSP ya separada en estado, cabeceras y cuerpo.</summary>
public sealed record RtspResponse(int Status, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out string? value) ? value : null;
}

/// <summary>Un paquete entrelazado del flujo RTSP ($ canal + largo + datos).</summary>
public readonly record struct InterleavedFrame(int Channel, byte[] Payload, int Length);

/// <summary>
/// Conexión RTSP sobre TCP con autenticación, pensada para hablar tanto con un
/// equipo (como cliente que reproduce) como con el media server (como emisor
/// que publica).
///
/// Existe porque el camino normal —MediaMTX pulsando el equipo— no puede pedir
/// velocidades distintas de 1×: la cabecera <c>Scale</c> del PLAY es lo que
/// hace que el grabador entregue más rápido, y MediaMTX no la envía.
///
/// Dos detalles aprendidos contra equipos reales:
/// • Los DVR Hikvision ofrecen <b>Basic</b> sobre RTSP, no digest.
/// • <see cref="Uri"/> NO puebla <c>UserInfo</c> para el esquema <c>rtsp</c>,
///   así que las credenciales se sacan de la cadena a mano; con Uri viajaban
///   vacías y el equipo respondía 401 sin fin.
/// </summary>
public sealed class RtspConnection : IAsyncDisposable
{
    private readonly TcpClient _tcp = new() { NoDelay = true };
    private readonly string _user, _password;
    private NetworkStream? _stream;
    private int _sequence;
    private string? _realm, _nonce, _qop, _opaque;
    private int _nonceCount;
    private bool _basic;

    /// <summary>URL sin credenciales: es la que viaja en las solicitudes.</summary>
    public string BaseUrl { get; }

    public string? SessionId { get; set; }

    public RtspConnection(string url)
    {
        int schemeEnd = url.IndexOf("://", StringComparison.Ordinal) + 3;
        int at = url.IndexOf('@', schemeEnd);
        if (at > 0)
        {
            string[] parts = url[schemeEnd..at].Split(':', 2);
            _user = Uri.UnescapeDataString(parts[0]);
            _password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            BaseUrl = string.Concat(url.AsSpan(0, schemeEnd), url.AsSpan(at + 1));
        }
        else
        {
            _user = _password = "";
            BaseUrl = url;
        }
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        var uri = new Uri(BaseUrl);
        await _tcp.ConnectAsync(uri.Host, uri.Port <= 0 ? 554 : uri.Port, ct);
        _stream = _tcp.GetStream();
    }

    private NetworkStream Stream => _stream ?? throw new InvalidOperationException("La conexión RTSP no está abierta.");

    /// <summary>
    /// Envía una solicitud y devuelve la respuesta. Si el equipo pide
    /// autenticación, se repite UNA vez ya autenticado (es como funciona RTSP:
    /// el reto llega con la primera respuesta).
    /// </summary>
    public async Task<RtspResponse> SendAsync(string method, string url, CancellationToken ct,
        IEnumerable<(string Name, string Value)>? headers = null, string? body = null, string? contentType = null)
    {
        var list = headers?.ToArray() ?? [];
        var response = await SendOnceAsync(method, url, list, body, contentType, ct);
        if (response.Status != 401 || (!_basic && _nonce is null))
            return response;
        return await SendOnceAsync(method, url, list, body, contentType, ct);
    }

    private async Task<RtspResponse> SendOnceAsync(string method, string url, (string Name, string Value)[] headers,
        string? body, string? contentType, CancellationToken ct)
    {
        var request = new StringBuilder();
        request.Append($"{method} {url} RTSP/1.0\r\n");
        request.Append($"CSeq: {++_sequence}\r\n");
        request.Append("User-Agent: CLR TrueCentral VMS\r\n");
        if (SessionId is not null) request.Append($"Session: {SessionId}\r\n");
        if (Authorization(method, url) is { } auth) request.Append($"Authorization: {auth}\r\n");
        foreach (var (name, value) in headers) request.Append($"{name}: {value}\r\n");
        if (body is not null)
        {
            request.Append($"Content-Type: {contentType ?? "application/sdp"}\r\n");
            request.Append($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n");
        }
        request.Append("\r\n");
        if (body is not null) request.Append(body);

        await Stream.WriteAsync(Encoding.UTF8.GetBytes(request.ToString()), ct);
        return await ReadResponseAsync(ct);
    }

    private string? Authorization(string method, string url)
    {
        if (_basic)
            return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_user}:{_password}"));
        if (_realm is null || _nonce is null)
            return null;

        string ha1 = Md5($"{_user}:{_realm}:{_password}");
        string ha2 = Md5($"{method}:{url}");
        var header = new StringBuilder(
            $"Digest username=\"{_user}\", realm=\"{_realm}\", nonce=\"{_nonce}\", uri=\"{url}\"");

        string response;
        if (_qop is { Length: > 0 })
        {
            // Los firmwares actuales retan con qop=auth, que suma el contador y
            // un nonce del cliente al cálculo.
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
        if (_opaque is { Length: > 0 }) header.Append($", opaque=\"{_opaque}\"");
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
            int read = await Stream.ReadAsync(one.AsMemory(0, 1), ct);
            if (read <= 0) throw new IOException("El otro extremo cerró la conexión RTSP.");
            head.Append((char)one[0]);
            if (head.Length > 32768) throw new IOException("Respuesta RTSP demasiado larga.");
        }

        string[] lines = head.ToString().Split("\r\n");
        int status = lines[0].Split(' ') is [_, string code, ..] && int.TryParse(code, out int value) ? value : 0;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            if (line.Length == 0) break;
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (status == 401 && headers.TryGetValue("WWW-Authenticate", out string? challenge))
        {
            _basic = challenge.TrimStart().StartsWith("Basic", StringComparison.OrdinalIgnoreCase);
            _realm = Between(challenge, "realm=\"", "\"");
            _nonce = Between(challenge, "nonce=\"", "\"");
            _qop = Between(challenge, "qop=\"", "\"")
                   ?? (challenge.Contains("qop=auth", StringComparison.OrdinalIgnoreCase) ? "auth" : null);
            _opaque = Between(challenge, "opaque=\"", "\"");
            _nonceCount = 0;
        }

        string body = "";
        if (headers.TryGetValue("Content-Length", out string? lengthText) &&
            int.TryParse(lengthText, out int length) && length > 0)
        {
            byte[] buffer = new byte[length];
            await ReadExactAsync(buffer, length, ct);
            body = Encoding.UTF8.GetString(buffer);
        }
        return new RtspResponse(status, headers, body);
    }

    // ------------------------------------------------------------------
    // Datos entrelazados
    // ------------------------------------------------------------------

    /// <summary>
    /// Lee el siguiente paquete entrelazado. Las respuestas RTSP que el equipo
    /// intercale (por ejemplo un PLAY nuevo) se descartan aquí mismo: en el
    /// flujo de datos solo interesan los marcos que empiezan con '$'.
    /// </summary>
    public async Task<InterleavedFrame?> ReadFrameAsync(byte[] buffer, CancellationToken ct)
    {
        byte[] header = new byte[3];
        while (true)
        {
            await ReadExactAsync(header, 1, ct);
            if (header[0] != (byte)'$') continue;
            await ReadExactAsync(header, 3, ct);
            int channel = header[0];
            int length = (header[1] << 8) | header[2];
            if (length <= 0 || length > buffer.Length) return null;
            await ReadExactAsync(buffer, length, ct);
            return new InterleavedFrame(channel, buffer, length);
        }
    }

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Escribe un paquete entrelazado (publicación hacia el media server).</summary>
    public async Task WriteFrameAsync(int channel, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            byte[] header = [(byte)'$', (byte)channel, (byte)(payload.Length >> 8), (byte)(payload.Length & 0xFF)];
            await Stream.WriteAsync(header, ct);
            await Stream.WriteAsync(payload, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadExactAsync(byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int chunk = await Stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (chunk <= 0) throw new IOException("El otro extremo cerró la conexión RTSP.");
            read += chunk;
        }
    }

    private static string? Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (from < 0) return null;
        from += start.Length;
        int to = text.IndexOf(end, from, StringComparison.Ordinal);
        return to < 0 ? null : text[from..to];
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _tcp.Dispose();
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
