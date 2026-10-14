using System.Diagnostics;

namespace TrueCentralVms.IsupHost.Engine;

/// <summary>
/// Dosifica un flujo MPEG-PS grabado para entregarlo a tiempo real.
///
/// El equipo envía una grabación mucho más rápido de lo que dura (20 s de vídeo llegan en menos de 1 s). Un reproductor de red
/// asume que lo que recibe es directo y trataría de seguir ese ritmo (aceleración, saltos). Aquí se lee el reloj del propio flujo
/// (SCR de las cabeceras de pack, 90 kHz) y se calcula cuánto esperar antes de enviar cada bloque, dejando una pequeña ventaja
/// (<see cref="LeadSeconds"/>) para que el reproductor siempre tenga colchón.
/// </summary>
public sealed class PsPacer
{
    public double LeadSeconds { get; init; } = 1.0;

    long _baseScr = -1, _lastScr = -1, _baseTick;

    /// <summary>Milisegundos que hay que esperar antes de enviar este bloque (0 si ya toca).</summary>
    public int WaitMs(byte[] buf, int count)
    {
        long scr = FindScr(buf, count);
        if (scr < 0) return 0;

        // Primera vez, o el reloj saltó (hacia atrás, o más de 30 s hacia delante): se vuelve a tomar como referencia.
        if (_baseScr < 0 || scr < _lastScr - 2 * 90_000 || scr > _lastScr + 30 * 90_000)
        {
            _baseScr = scr;
            _baseTick = Stopwatch.GetTimestamp();
        }
        _lastScr = scr;

        double due = (scr - _baseScr) / 90_000.0 - LeadSeconds;                                   // cuándo debería salir, desde la referencia
        double now = (Stopwatch.GetTimestamp() - _baseTick) / (double)Stopwatch.Frequency;        // cuánto ha pasado en realidad
        return due > now ? (int)((due - now) * 1000) : 0;
    }

    /// <summary>SCR (33 bits, ticks de 90 kHz) del primer pack MPEG-2 completo del bloque, o -1.</summary>
    static long FindScr(byte[] b, int n)
    {
        for (int i = 0; i + 10 <= n; i++)
        {
            if (b[i] != 0x00 || b[i + 1] != 0x00 || b[i + 2] != 0x01 || b[i + 3] != 0xBA) continue;
            if ((b[i + 4] & 0xC0) != 0x40) continue;   // no es una cabecera MPEG-2
            return ((long)((b[i + 4] >> 3) & 0x07) << 30) | ((long)(b[i + 4] & 0x03) << 28) | ((long)b[i + 5] << 20)
                 | ((long)((b[i + 6] >> 3) & 0x1F) << 15) | ((long)(b[i + 6] & 0x03) << 13) | ((long)b[i + 7] << 5) | (long)(b[i + 8] >> 3);
        }
        return -1;
    }
}
