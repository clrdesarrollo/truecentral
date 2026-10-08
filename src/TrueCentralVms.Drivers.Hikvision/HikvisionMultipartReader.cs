using System.Text;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Separa en partes el flujo <c>multipart</c> de <c>alertStream</c> a medida que
/// llegan los bytes.
///
/// Cada parte empieza con <c>--separador</c>, trae sus cabeceras y después el
/// cuerpo. Si la cabecera <c>Content-Length</c> viene (la guía ISAPI la muestra
/// en cada parte), la parte se entrega apenas llegó ese largo: no hace falta
/// esperar el separador de la SIGUIENTE, que en un equipo tranquilo puede ser
/// el próximo latido, segundos después. Sin largo —o con uno que no cuadra— se
/// corta en el separador siguiente, como siempre.
///
/// Trabaja con bytes, no con texto: el largo se cuenta en bytes, una tilde
/// partida entre dos lecturas no se rompe (el cuerpo se decodifica entero) y
/// las fotos binarias no se pasan por un decodificador de texto.
/// </summary>
internal sealed class HikvisionMultipartReader(string boundary)
{
    private readonly byte[] _delimiter = Encoding.ASCII.GetBytes("--" + boundary);
    private static readonly byte[] HeaderEnd = "\r\n\r\n"u8.ToArray();

    private byte[] _buffer = new byte[16 * 1024];
    private int _count;

    /// <summary>Bytes retenidos esperando completar una parte.</summary>
    public int Buffered => _count;

    public void Append(ReadOnlySpan<byte> data)
    {
        if (_count + data.Length > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _count + data.Length));
        data.CopyTo(_buffer.AsSpan(_count));
        _count += data.Length;
    }

    /// <summary>Las partes que ya están completas, en orden, con su <c>Content-Type</c> (null si no lo trae).</summary>
    public List<(string? ContentType, byte[] Body)> TakeParts()
    {
        var parts = new List<(string?, byte[])>();
        while (true)
        {
            var data = _buffer.AsSpan(0, _count);
            int start = data.IndexOf(_delimiter);
            if (start < 0)
            {
                // Lo que hay antes del primer separador es preámbulo o restos: se
                // descarta, menos la cola por si el separador quedó partido.
                Discard(Math.Max(0, _count - (_delimiter.Length - 1)));
                break;
            }
            if (start > 0)
            {
                Discard(start);
                continue;
            }

            int afterDelimiter = _delimiter.Length;
            if (_count < afterDelimiter + 2) break;
            // "--separador--" cierra el multipart: no trae parte.
            if (data[afterDelimiter] == (byte)'-' && data[afterDelimiter + 1] == (byte)'-')
            {
                Discard(afterDelimiter + 2);
                continue;
            }

            int next = IndexOf(data, _delimiter, afterDelimiter);
            int headerEnd = IndexOf(data, HeaderEnd, afterDelimiter);
            if (headerEnd < 0 || (next >= 0 && headerEnd > next))
            {
                // Parte sin cabeceras: el cuerpo llega hasta el separador siguiente.
                if (next < 0) break;
                parts.Add((null, Trim(data[afterDelimiter..next]).ToArray()));
                Discard(next);
                continue;
            }

            var (contentType, length) = Headers(data[afterDelimiter..headerEnd]);
            int bodyStart = headerEnd + HeaderEnd.Length;

            if (length is int declared && declared >= 0)
            {
                if (_count >= bodyStart + declared)
                {
                    var body = data.Slice(bodyStart, declared);
                    // Un largo que se queda corto deja un JSON sin cerrar: ahí no
                    // se le cree y se espera el separador.
                    if (!LooksCutJson(contentType, body))
                    {
                        parts.Add((contentType, body.ToArray()));
                        Discard(bodyStart + declared);
                        continue;
                    }
                }
                else if (next < 0)
                {
                    break;   // falta cuerpo: se espera
                }
                // Si el separador llegó antes que el largo prometido, el largo
                // miente: manda el separador.
            }

            if (next < 0) break;
            parts.Add((contentType, Trim(data[bodyStart..next]).ToArray()));
            Discard(next);
        }
        return parts;
    }

    private void Discard(int bytes)
    {
        if (bytes <= 0) return;
        bytes = Math.Min(bytes, _count);
        _buffer.AsSpan(bytes, _count - bytes).CopyTo(_buffer);
        _count -= bytes;
    }

    private static int IndexOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> value, int from)
    {
        if (from >= data.Length) return -1;
        int at = data[from..].IndexOf(value);
        return at < 0 ? -1 : at + from;
    }

    /// <summary>El cuerpo sin los saltos de línea que lo separan del separador.</summary>
    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> body)
    {
        int end = body.Length;
        while (end > 0 && body[end - 1] is (byte)'\r' or (byte)'\n') end--;
        int begin = 0;
        while (begin < end && body[begin] is (byte)'\r' or (byte)'\n') begin++;
        return body[begin..end];
    }

    private static (string? ContentType, int? Length) Headers(ReadOnlySpan<byte> block)
    {
        string? contentType = null;
        int? length = null;
        foreach (string line in Encoding.ASCII.GetString(block).Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) contentType = value;
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int n))
                length = n;
        }
        return (contentType, length);
    }

    private static bool LooksCutJson(string? contentType, ReadOnlySpan<byte> body)
    {
        var trimmed = body.Trim(" \t\r\n"u8);
        bool json = contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true ||
                    (trimmed.Length > 0 && trimmed[0] == (byte)'{');
        return json && (trimmed.Length == 0 || trimmed[^1] != (byte)'}');
    }
}
