namespace TrueCentralVms.Drivers.Patrol;

/// <summary>Una lectura del bastón: el tag leído y la hora local del bastón.</summary>
/// <param name="TagId">10 caracteres: hex de 5 bytes (registro de 11 bytes) o decimal de 4 bytes (registro de 10).</param>
public sealed record PatrolTagRead(string TagId, DateTime ReadAt);

/// <summary>
/// Decodifica los bloques de registros (respuesta 0x93) tal como lo hace
/// WmWebUsb.dll. Vive aparte del puerto serie porque el servidor también lo
/// usa: el panel web lee el bastón por Web Serial y sube los bloques CRUDOS,
/// así la lógica de registros existe en un solo lugar.
/// </summary>
public static class PatrolBlockDecoder
{
    /// <summary>Respuesta del bastón con un bloque de registros.</summary>
    public const byte BlockResponse = 0x93;

    /// <summary>
    /// Lecturas de un bloque. Si (largo − 11) es múltiplo de 11 los registros
    /// son de 11 bytes (tag de 5 bytes en hex, FUN_10024857 de la DLL); si no,
    /// de 10 (tag de 4 bytes big-endian en decimal, FUN_10018681). Cada registro
    /// lleva la hora en BCD: ss mm hh DD MM YY. Se descartan los vacíos (FF) y
    /// los de fecha imposible, igual que la DLL.
    /// </summary>
    public static IEnumerable<PatrolTagRead> Decode(byte[] block)
    {
        if (block.Length < 18 || block[4] != BlockResponse) yield break;
        int total = block.Length;
        int declared = block[2] | block[3] << 8;
        int size = (total - 11) % 11 == 0 || (declared - 3) % 11 == 0 ? 11 : 10;
        int tagLen = size - 6;
        int count = (total - 11) / size;

        for (int i = 0; i < count; i++)
        {
            int o = 7 + i * size;
            var r = block.AsSpan(o, size);
            if (r[..7].IndexOfAnyExcept((byte)0xFF) < 0) continue;

            var t = r.Slice(tagLen, 6);
            int ss = Bcd(t[0]), mm = Bcd(t[1]), hh = Bcd(t[2]), dd = Bcd(t[3]), mo = Bcd(t[4]), yy = Bcd(t[5]);
            if (mo is < 1 or > 12 || dd is < 1 or > 31 || hh > 23 || mm > 59 || ss > 59) continue;
            if (dd > DateTime.DaysInMonth(2000 + yy, mo)) continue;

            string tag = size == 11
                ? Convert.ToHexString(r[..5])
                : ((uint)(r[0] << 24 | r[1] << 16 | r[2] << 8 | r[3])).ToString("D10");

            yield return new PatrolTagRead(tag, new DateTime(2000 + yy, mo, dd, hh, mm, ss, DateTimeKind.Unspecified));
        }
    }

    /// <summary>Número de bloque que trae la respuesta 0x93 (bytes 5 y 6).</summary>
    public static int BlockIndex(byte[] block) => block.Length > 6 ? block[5] | block[6] << 8 : -1;

    internal static int Bcd(byte b) => (b >> 4) * 10 + (b & 0x0F);
    internal static byte ToBcd(int v) => (byte)((v / 10) << 4 | v % 10);
}
