using System.Buffers.Binary;

namespace TrueCentralVms.Server.Services.Time;

/// <summary>
/// El paquete NTP (RFC 5905) en lo que hace falta para pedir y dar la hora:
/// 48 bytes, marcas de tiempo de 64 bits (segundos desde 1900 + fracción).
///
/// Las marcas dan la vuelta en 2036 (era 1): al leerlas se elige la era más
/// cercana a la hora de referencia, así que sirven igual después de esa fecha.
/// </summary>
internal static class NtpWire
{
    public const int PacketSize = 48;

    public const int ModeClient = 3;
    public const int ModeServer = 4;

    /// <summary>Indicador de salto "reloj sin sincronizar": quien lo recibe no debe creerle.</summary>
    public const int LeapUnsynchronized = 3;

    // Posiciones de los campos.
    public const int RefIdOffset = 12;
    public const int ReferenceOffset = 16;
    public const int OriginOffset = 24;
    public const int ReceiveOffset = 32;
    public const int TransmitOffset = 40;

    private static readonly DateTime Epoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static int Leap(ReadOnlySpan<byte> p) => p[0] >> 6;
    public static int Version(ReadOnlySpan<byte> p) => (p[0] >> 3) & 0x7;
    public static int Mode(ReadOnlySpan<byte> p) => p[0] & 0x7;
    public static int Stratum(ReadOnlySpan<byte> p) => p[1];

    public static byte Header(int leap, int version, int mode) => (byte)((leap << 6) | (version << 3) | mode);

    public static void WriteTimestamp(Span<byte> destination, DateTime utc)
    {
        long ticks = (utc - Epoch).Ticks;
        ulong seconds = (ulong)(ticks / TimeSpan.TicksPerSecond);
        ulong fraction = (ulong)(ticks % TimeSpan.TicksPerSecond * 0x1_0000_0000L / TimeSpan.TicksPerSecond);
        BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)seconds);          // la vuelta de 2036 la da el cast
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], (uint)fraction);
    }

    /// <summary>Lee una marca; null si viene en cero (campo sin valor).</summary>
    public static DateTime? ReadTimestamp(ReadOnlySpan<byte> source, DateTime near)
    {
        uint seconds = BinaryPrimitives.ReadUInt32BigEndian(source);
        uint fraction = BinaryPrimitives.ReadUInt32BigEndian(source[4..]);
        if (seconds == 0 && fraction == 0) return null;

        long nearSeconds = (near - Epoch).Ticks / TimeSpan.TicksPerSecond;
        long era = nearSeconds >> 32;
        long best = long.MaxValue, bestDistance = long.MaxValue;
        for (long e = era - 1; e <= era + 1; e++)
        {
            if (e < 0) continue;
            long candidate = (e << 32) + seconds;
            long distance = Math.Abs(candidate - nearSeconds);
            if (distance < bestDistance) (best, bestDistance) = (candidate, distance);
        }
        return Epoch.AddTicks(best * TimeSpan.TicksPerSecond + (long)((ulong)fraction * TimeSpan.TicksPerSecond >> 32));
    }

    /// <summary>Duración en el formato corto de NTP (16.16, segundos), para retardo y dispersión de la raíz.</summary>
    public static void WriteShort(Span<byte> destination, TimeSpan value)
    {
        double seconds = Math.Clamp(value.TotalSeconds, 0, 65535);
        BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)(seconds * 65536));
    }
}
