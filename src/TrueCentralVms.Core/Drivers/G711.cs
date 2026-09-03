namespace TrueCentralVms.Core.Drivers;

/// <summary>
/// Codificador G.711 (µ-law y A-law) para audio PCM de 16 bits a 8 kHz. Es el
/// formato que hablan los canales de audio en vivo de los parlantes y las
/// cámaras: la voz del operador llega en PCM y se convierte aquí antes de
/// enviarla. Algoritmos de referencia del ITU-T (mismos que usan NAudio y
/// FFmpeg), sin dependencias.
/// </summary>
public static class G711
{
    /// <summary>Bytes por segundo de un flujo G.711 a 8 kHz.</summary>
    public const int BytesPerSecond = 8000;

    public static byte LinearToMuLaw(short pcm)
    {
        int sample = pcm;
        int sign = (sample & 0x8000) >> 8;
        if (sign != 0) sample = -sample;
        if (sample > 32635) sample = 32635;
        sample += 0x84;
        int exponent = 7;
        for (int mask = 0x4000; (sample & mask) == 0 && exponent > 0; exponent--, mask >>= 1) { }
        int mantissa = (sample >> (exponent + 3)) & 0x0F;
        byte mulaw = (byte)(sign | (exponent << 4) | mantissa);
        return (byte)~mulaw;
    }

    public static byte LinearToALaw(short pcm)
    {
        int sample = pcm;
        int sign = (sample & 0x8000) >> 8;
        if (sign != 0) sample = -sample;
        if (sample > 32635) sample = 32635;
        int exponent = 7;
        for (int mask = 0x4000; (sample & mask) == 0 && exponent > 0; exponent--, mask >>= 1) { }
        int mantissa = (sample >> (exponent == 0 ? 4 : exponent + 3)) & 0x0F;
        byte alaw = (byte)(sign | (exponent << 4) | mantissa);
        return (byte)(alaw ^ 0xD5);
    }

    /// <summary>
    /// Convierte PCM 16 bits little-endian mono a G.711. Devuelve la cantidad
    /// de bytes escritos (una muestra = un byte). Un byte impar al final se
    /// ignora.
    /// </summary>
    public static int Encode(ReadOnlySpan<byte> pcm16, Span<byte> destination, bool aLaw)
    {
        int samples = Math.Min(pcm16.Length / 2, destination.Length);
        for (int i = 0; i < samples; i++)
        {
            short sample = (short)(pcm16[2 * i] | (pcm16[2 * i + 1] << 8));
            destination[i] = aLaw ? LinearToALaw(sample) : LinearToMuLaw(sample);
        }
        return samples;
    }

    public static byte[] Encode(ReadOnlySpan<byte> pcm16, bool aLaw)
    {
        var result = new byte[pcm16.Length / 2];
        Encode(pcm16, result, aLaw);
        return result;
    }

    /// <summary>Reduce el volumen o lo amplifica (ganancia lineal) sobre PCM 16 bits, con recorte.</summary>
    public static void ApplyGain(Span<byte> pcm16, float gain)
    {
        if (Math.Abs(gain - 1f) < 0.001f) return;
        for (int i = 0; i + 1 < pcm16.Length; i += 2)
        {
            int sample = (short)(pcm16[i] | (pcm16[i + 1] << 8));
            sample = (int)Math.Clamp(sample * gain, short.MinValue, short.MaxValue);
            pcm16[i] = (byte)(sample & 0xFF);
            pcm16[i + 1] = (byte)((sample >> 8) & 0xFF);
        }
    }
}
