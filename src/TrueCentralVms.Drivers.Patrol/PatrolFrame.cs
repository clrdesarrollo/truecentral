namespace TrueCentralVms.Drivers.Patrol;

/// <summary>
/// Trama del bastón JWM (Captor 4, modelo interno 0x7D2 de WmWebUsb.dll):
///
///   D5 D5 | LEN_lo LEN_hi | CMD | DATA... | SUM_lo SUM_hi | 0D 0A
///
/// LEN = bytes de CMD + DATA; SUM = suma de 16 bits de CMD + DATA. La respuesta
/// lleva CMD | 0x80. OJO: LEN no siempre es cierto (la respuesta a 0x39 declara
/// 1 y trae 2), por eso la trama se delimita por el CR LF y la suma se calcula
/// sobre lo realmente recibido, igual que la DLL del fabricante.
/// </summary>
public static class PatrolFrame
{
    public const byte Header = 0xD5;
    public const int Overhead = 8;

    public static byte[] Build(byte cmd, ReadOnlySpan<byte> data = default)
    {
        int len = 1 + data.Length;
        var f = new byte[len + Overhead];
        f[0] = Header;
        f[1] = Header;
        f[2] = (byte)len;
        f[3] = (byte)(len >> 8);
        f[4] = cmd;
        data.CopyTo(f.AsSpan(5));
        ushort sum = Sum(f.AsSpan(4, len));
        f[4 + len] = (byte)sum;
        f[5 + len] = (byte)(sum >> 8);
        f[6 + len] = 0x0D;
        f[7 + len] = 0x0A;
        return f;
    }

    /// <summary>Largo que declara la trama, o -1 si aún no llegan sus 4 primeros bytes.</summary>
    public static int DeclaredLength(ReadOnlySpan<byte> buf) =>
        buf.Length < 4 ? -1 : (buf[2] | buf[3] << 8) + Overhead;

    public static bool IsValid(ReadOnlySpan<byte> f)
    {
        int n = f.Length;
        if (n < Overhead + 1 || f[0] != Header || f[1] != Header || f[n - 2] != 0x0D || f[n - 1] != 0x0A) return false;
        ushort sum = Sum(f[4..(n - 4)]);
        return f[n - 4] == (byte)sum && f[n - 3] == (byte)(sum >> 8);
    }

    /// <summary>Datos de la trama sin el comando, según su largo real.</summary>
    public static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> f) => f[5..(f.Length - 4)];

    private static ushort Sum(ReadOnlySpan<byte> bytes)
    {
        ushort s = 0;
        foreach (var b in bytes) s += b;
        return s;
    }
}
