using System.Buffers.Binary;
using System.Text;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.ZkTeco;

/// <summary>
/// Una tabla de datos de la controladora, tal como la declara (comando
/// <see cref="C3Protocol.CmdTableConfig"/>): su número y el número y tipo de
/// cada campo (<c>i</c> entero, <c>s</c> texto, <c>B</c> binario). Los números
/// cambian entre modelos y firmware, así que no se fijan en el código: se leen
/// del equipo en cada sesión que los necesita.
/// </summary>
public sealed record C3Table(string Name, byte Index, IReadOnlyDictionary<string, (byte Index, char Type)> Fields)
{
    public byte Field(string name) => Fields.TryGetValue(name, out var f)
        ? f.Index
        : throw new DriverException($"La tabla '{Name}' de la controladora no tiene el campo '{name}'.");

    public bool Has(string name) => Fields.ContainsKey(name);

    /// <summary>
    /// Lee la respuesta del comando de configuración: una tabla por línea,
    /// <c>user=1,UID=i1,CardNo=i2,…</c>.
    /// </summary>
    public static Dictionary<string, C3Table> Parse(ReadOnlySpan<byte> data)
    {
        var tables = new Dictionary<string, C3Table>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in Encoding.ASCII.GetString(data).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim('\0', '\r', ' ').Split(',');
            if (parts.Length == 0 || parts[0].Split('=') is not [var name, var index] || !byte.TryParse(index, out byte tableIndex))
                continue;
            var fields = new Dictionary<string, (byte, char)>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in parts.Skip(1))
                if (field.Split('=') is [var fieldName, { Length: >= 2 } def] && byte.TryParse(def[1..], out byte fieldIndex))
                    fields[fieldName.Trim()] = (fieldIndex, def[0]);
            tables[name.Trim()] = new C3Table(name.Trim(), tableIndex, fields);
        }
        return tables;
    }
}

/// <summary>Una fila leída de una tabla: el valor crudo de cada campo, por número de campo.</summary>
public sealed class C3Row(Dictionary<byte, byte[]> values)
{
    public byte[] Bytes(byte field) => values.TryGetValue(field, out var v) ? v : [];
    public long Int(byte field) => values.TryGetValue(field, out var v) ? C3Protocol.DecodeInt(v) : 0;
    public string Text(byte field) => values.TryGetValue(field, out var v)
        ? C3Protocol.TextEncoding.GetString(v).TrimEnd('\0') : "";
}

public sealed partial class C3Connection
{
    private Dictionary<string, C3Table>? _tables;

    /// <summary>Tabla por nombre ("user", "userauthorize", "timezone", "transaction"…).</summary>
    public async Task<C3Table> TableAsync(string name, CancellationToken ct)
    {
        if (_tables is null)
        {
            var reply = await SendAsync(C3Protocol.CmdTableConfig, [], ct);
            if (!reply.Ok) throw new DriverException($"La controladora no describió sus tablas (código {reply.ErrorCode}).");
            _tables = C3Table.Parse(reply.Data);
        }
        return _tables.TryGetValue(name, out var table)
            ? table
            : throw new DriverException($"La controladora no tiene la tabla '{name}'.");
    }

    public async Task<long> CountAsync(C3Table table, CancellationToken ct)
    {
        var reply = await SendAsync(C3Protocol.CmdCountData, [table.Index, 0], ct);
        if (!reply.Ok || reply.Data.Length < 4)
            throw new DriverException($"La controladora no contó la tabla '{table.Name}' (código {reply.ErrorCode}).");
        return BinaryPrimitives.ReadUInt32LittleEndian(reply.Data);
    }

    /// <summary>
    /// Filas de una tabla. <paramref name="onlyNew"/> pide solo las que la
    /// controladora no entregó antes con esta misma opción (es lo que usa ZKAccess
    /// para el historial): la controladora lleva UN marcador por tabla, compartido
    /// por todo el que lea así.
    /// </summary>
    public async Task<List<C3Row>> ReadAsync(C3Table table, IReadOnlyList<string> fields, CancellationToken ct,
        (string Field, byte[] Value)? filter = null, bool onlyNew = false)
    {
        var request = new List<byte> { table.Index, (byte)fields.Count };
        request.AddRange(fields.Select(table.Field));
        request.Add(onlyNew ? (byte)1 : (byte)0);
        if (filter is { } f)
        {
            request.AddRange([1, table.Field(f.Field), (byte)f.Value.Length]);
            request.AddRange(f.Value);
        }
        else request.Add(0);

        var reply = await SendAsync(C3Protocol.CmdGetData, request.ToArray(), ct);
        byte[] data;
        if (reply.Command == C3Protocol.ReplyLargeData) data = await ReadLargeAsync(reply.Data, ct);
        else if (reply.Ok) data = reply.Data;
        else throw new DriverException($"La controladora no entregó la tabla '{table.Name}' (código {reply.ErrorCode}).");

        // Leer "solo nuevas" no mueve el marcador: hay que confirmarlo, como
        // hace el PullSDK al terminar. La inBio480 responde C9 con código 0
        // y aun así lo mueve (sin esta orden, la próxima lectura repite todo).
        if (onlyNew) await SendAsync(C3Protocol.CmdConfirmNew, [table.Index], ct);
        return ParseRows(data, table);
    }

    /// <summary>
    /// Lectura que no cabe en una trama: el anuncio trae
    /// <c>[0][total:4][total:4][verificación:4][parte:4]</c>; se piden las
    /// partes por desplazamiento y al final se libera el búfer.
    /// </summary>
    private async Task<byte[]> ReadLargeAsync(byte[] announce, CancellationToken ct)
    {
        if (announce.Length < 17) throw new DriverException("La controladora anunció una lectura grande ilegible.");
        int total = BinaryPrimitives.ReadInt32LittleEndian(announce.AsSpan(1));
        int chunk = BinaryPrimitives.ReadInt32LittleEndian(announce.AsSpan(13));
        if (total <= 0 || chunk <= 0 || total > 64 * 1024 * 1024)
            throw new DriverException("La controladora anunció una lectura de tamaño inválido.");

        var buffer = new byte[total];
        try
        {
            for (int offset = 0; offset < total; offset += chunk)
            {
                var offsetBytes = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(offsetBytes, offset);
                var part = await SendAsync(C3Protocol.CmdReadChunk, offsetBytes, ct);
                if (!part.Ok || part.Data.Length < 4)
                    throw new DriverException($"La controladora cortó la lectura en el byte {offset} (código {part.ErrorCode}).");
                int length = Math.Min(part.Data.Length - 4, total - offset);
                part.Data.AsSpan(4, length).CopyTo(buffer.AsSpan(offset));
            }
        }
        finally
        {
            try { await SendAsync(C3Protocol.CmdFreeData, [], ct); } catch (DriverException) { }
        }
        return buffer;
    }

    /// <summary>
    /// <c>[tabla][n][campos…]</c> y luego, por fila, <c>[tamaño][valor]</c> por
    /// campo. Los campos binarios (tipo <c>B</c>: la plantilla de huella) llevan
    /// el tamaño en 2 bytes; los demás, en 1.
    /// </summary>
    private static List<C3Row> ParseRows(byte[] data, C3Table table)
    {
        var binary = table.Fields.Values.Where(f => f.Type == 'B').Select(f => f.Index).ToHashSet();
        var rows = new List<C3Row>();
        if (data.Length < 2) return rows;
        int count = data[1];
        if (data.Length < 2 + count) return rows;
        var fields = data.AsSpan(2, count).ToArray();
        int at = 2 + count;
        while (at < data.Length)
        {
            var values = new Dictionary<byte, byte[]>();
            foreach (byte field in fields)
            {
                if (at >= data.Length) return rows;   // fila incompleta: se descarta
                int size;
                if (binary.Contains(field))
                {
                    if (at + 2 > data.Length) return rows;
                    size = data[at] | (data[at + 1] << 8);
                    at += 2;
                }
                else size = data[at++];
                if (at + size > data.Length) return rows;
                values[field] = data.AsSpan(at, size).ToArray();
                at += size;
            }
            rows.Add(new C3Row(values));
        }
        return rows;
    }

    /// <summary>
    /// Escribe filas (alta, o actualización si ya hay una con la misma clave:
    /// el PIN en usuarios, el número en horarios). Cada fila trae un valor por
    /// campo de <paramref name="fields"/>, en el mismo orden.
    /// </summary>
    public async Task WriteAsync(C3Table table, IReadOnlyList<string> fields, IEnumerable<byte[][]> rows, CancellationToken ct)
    {
        var header = new List<byte> { table.Index, (byte)fields.Count };
        header.AddRange(fields.Select(table.Field));

        // Las filas se agrupan en tramas que no pasen de unos 16 KB.
        var batch = new List<byte>(header);
        async Task FlushAsync()
        {
            if (batch.Count == header.Count) return;
            var reply = await SendAsync(C3Protocol.CmdSetData, batch.ToArray(), ct);
            if (!reply.Ok)
                throw new DriverException($"La controladora rechazó la escritura en '{table.Name}' (código {reply.ErrorCode}).");
            batch = new List<byte>(header);
        }
        foreach (var row in rows)
        {
            if (row.Length != fields.Count) throw new ArgumentException("Cada fila debe traer un valor por campo.");
            var encoded = new List<byte>();
            for (int i = 0; i < row.Length; i++)
            {
                var value = row[i];
                if (table.Fields.TryGetValue(fields[i], out var def) && def.Type == 'B')
                {
                    if (value.Length > ushort.MaxValue) throw new DriverException($"Un valor para '{table.Name}' es demasiado largo.");
                    encoded.Add((byte)value.Length);
                    encoded.Add((byte)(value.Length >> 8));
                }
                else
                {
                    if (value.Length > 255) throw new DriverException($"Un valor para '{table.Name}' es demasiado largo.");
                    encoded.Add((byte)value.Length);
                }
                encoded.AddRange(value);
            }
            if (batch.Count + encoded.Count > 16 * 1024) await FlushAsync();
            batch.AddRange(encoded);
        }
        await FlushAsync();
    }

    /// <summary>Borra las filas cuyo <paramref name="field"/> vale <paramref name="value"/>. Que no haya ninguna no es error.</summary>
    public async Task DeleteAsync(C3Table table, string field, byte[] value, CancellationToken ct)
    {
        var request = new List<byte> { table.Index, 1, table.Field(field), (byte)value.Length };
        request.AddRange(value);
        var reply = await SendAsync(C3Protocol.CmdDeleteData, request.ToArray(), ct);
        if (!reply.Ok)
            throw new DriverException($"La controladora rechazó el borrado en '{table.Name}' (código {reply.ErrorCode}).");
    }
}
