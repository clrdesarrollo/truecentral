using NAudio.Wave;

namespace TrueCentralVms.Client.Services;

/// <summary>
/// Sirena de alarma de cerco eléctrico: dos tonos alternados (960 / 770 Hz cada
/// 0,45 s) sintetizados en memoria y repetidos hasta <see cref="Stop"/>. Es un
/// sonido distinto del timbre de citofonía y del aviso de las automatizaciones, para
/// que el operador reconozca la alarma de cerco sin mirar la pantalla. Una sola
/// sirena por aplicación aunque haya varios paneles en alarma.
/// </summary>
public sealed class CercoSiren
{
    private WaveOutEvent? _out;
    private int _sounding;

    public bool IsSounding => Volatile.Read(ref _sounding) == 1;

    private static byte[] Pattern()
    {
        const int rate = 8000;
        var pcm = new List<byte>();
        void Tone(double freq, int ms)
        {
            int n = rate * ms / 1000;
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1, Math.Min(i, n - 1 - i) / 40.0);
                // Fundamental + tercer armónico: más penetrante que un seno puro.
                double t = 2 * Math.PI * freq * i / rate;
                double v = Math.Sin(t) * 0.75 + Math.Sin(3 * t) * 0.25;
                short s = (short)(v * 14000 * env);
                pcm.Add((byte)(s & 0xFF));
                pcm.Add((byte)((s >> 8) & 0xFF));
            }
        }
        Tone(960, 450); Tone(770, 450);
        return pcm.ToArray();
    }

    private sealed class LoopProvider(byte[] pattern) : IWaveProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = new(8000, 16, 1);

        public int Read(byte[] buffer, int offset, int count)
        {
            int written = 0;
            while (written < count)
            {
                int take = Math.Min(count - written, pattern.Length - _position);
                Buffer.BlockCopy(pattern, _position, buffer, offset + written, take);
                written += take;
                _position = (_position + take) % pattern.Length;
            }
            return written;
        }
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _sounding, 1) == 1) return;
        try
        {
            _out = new WaveOutEvent { DesiredLatency = 200 };
            _out.Init(new LoopProvider(Pattern()));
            _out.Play();
        }
        catch
        {
            // Sin dispositivo de audio: al menos el sonido del sistema.
            _out?.Dispose();
            _out = null;
            System.Media.SystemSounds.Hand.Play();
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _sounding, 0) == 0) return;
        var output = _out;
        _out = null;
        try { output?.Stop(); } catch { /* ya detenido */ }
        output?.Dispose();
    }
}
