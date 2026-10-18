using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.SpeakerAgent.Audio;

/// <summary>
/// Reproducción del agente. El servidor abre flujos en vivo (voz del operador o sonidos del servidor, G.711 a
/// 8 kHz al ritmo real) y escribe tramas; aquí se decodifican a PCM, se acumulan en un búfer de fluctuación y
/// un hilo propio las entrega al dispositivo de 20 en 20 ms con el volumen aplicado.
/// <list type="bullet">
/// <item>Al empezar a sonar se espera <see cref="PrebufferMilliseconds"/> de audio para absorber el jitter de la red.</item>
/// <item>Si la red se atrasa se rellena con silencio (el dispositivo no se queda sin datos ni "chasquea").</item>
/// <item>El búfer tiene tope (<see cref="MaxBufferedMilliseconds"/>): si llega audio de golpe se descarta lo más viejo
/// para que la voz no quede atrasada.</item>
/// <item>Un flujo sin audio por <see cref="StreamIdleTimeout"/> se cierra solo y se avisa (<see cref="StreamClosed"/>).</item>
/// <item>El dispositivo se suelta tras unos segundos sin sonar (otro programa puede usarlo).</item>
/// </list>
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int SampleRate = 8000;
    private const int FrameSamples = SampleRate / 50;   // 20 ms
    private const int PrebufferMilliseconds = 120;
    private const int MaxBufferedMilliseconds = 1000;
    private static readonly TimeSpan StreamIdleTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReleaseDeviceAfter = TimeSpan.FromSeconds(3);

    private readonly AgentStore _store;
    private readonly ILogger<AudioEngine> _log;
    private readonly object _gate = new();
    private readonly SampleRing _ring = new(SampleRate * 2);
    private readonly Dictionary<int, LiveStream> _streams = [];
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private int _nextStream;
    private volatile bool _disposed;
    private volatile int _volume;
    private volatile bool _reopen;
    private string? _toneLabel;
    private string? _device;

    private sealed class LiveStream(string codec)
    {
        public string Codec { get; } = codec;
        public DateTime LastAudio { get; set; } = DateTime.UtcNow;
    }

    public AudioEngine(AgentStore store, ILogger<AudioEngine> log)
    {
        _store = store;
        _log = log;
        var state = store.Snapshot;
        _volume = Math.Clamp(state.Volume, 0, 100);
        _device = state.OutputDevice;
        store.Changed += () =>
        {
            string? device = store.Snapshot.OutputDevice;
            if (device != _device) { _device = device; _reopen = true; }
        };
        _thread = new Thread(Run) { IsBackground = true, Name = "Reproducción", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>El agente cerró un flujo por su cuenta (sin audio, dispositivo con error).</summary>
    public event Action<int>? StreamClosed;

    /// <summary>El volumen cambió desde la página local (hay que avisarle al servidor).</summary>
    public event Action<int>? VolumeChangedLocally;

    public int Volume => _volume;

    /// <summary>Nombre del dispositivo abierto, o el configurado si no está sonando.</summary>
    public string OutputDevice { get; private set; } = "—";

    /// <summary>Último error del dispositivo de salida (null = sin problemas).</summary>
    public string? OutputError { get; private set; }

    public bool IsPlaying
    {
        get { lock (_gate) return _streams.Count > 0 || _ring.Count > 0; }
    }

    public string? CurrentName
    {
        get
        {
            lock (_gate)
                return _streams.Count > 0 ? "Audio en vivo" : _ring.Count > 0 ? _toneLabel ?? "Audio" : null;
        }
    }

    public void SetVolume(int volume, bool fromServer)
    {
        volume = Math.Clamp(volume, 0, 100);
        if (volume == _volume) return;
        _volume = volume;
        _store.Update(s => s.Volume = volume);
        if (!fromServer) VolumeChangedLocally?.Invoke(volume);
    }

    public int OpenStream(string codec)
    {
        if (codec is not (SpeakerAgentProtocol.Codecs.ALaw or SpeakerAgentProtocol.Codecs.MuLaw or SpeakerAgentProtocol.Codecs.Pcm16))
            throw new ArgumentException($"Códec no soportado: '{codec}'.");
        lock (_gate)
        {
            _nextStream = _nextStream % ushort.MaxValue + 1;
            _streams[_nextStream] = new LiveStream(codec);
            _toneLabel = null;
            _signal.Set();
            return _nextStream;
        }
    }

    public void CloseStream(int stream)
    {
        lock (_gate) _streams.Remove(stream);
        _signal.Set();
    }

    public void Write(int stream, ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(stream, out var live)) return;
            live.LastAudio = DateTime.UtcNow;
            switch (live.Codec)
            {
                case SpeakerAgentProtocol.Codecs.ALaw:
                    foreach (byte b in payload) _ring.Push(G711.ALawToLinear(b));
                    break;
                case SpeakerAgentProtocol.Codecs.MuLaw:
                    foreach (byte b in payload) _ring.Push(G711.MuLawToLinear(b));
                    break;
                default:
                    for (int i = 0; i + 1 < payload.Length; i += 2) _ring.Push((short)(payload[i] | payload[i + 1] << 8));
                    break;
            }
            _ring.TrimTo(SampleRate * MaxBufferedMilliseconds / 1000);
        }
        _signal.Set();
    }

    /// <summary>Corta todo: flujos y lo que quede en el búfer.</summary>
    public void StopAll()
    {
        int[] closed;
        lock (_gate)
        {
            closed = [.. _streams.Keys];
            _streams.Clear();
            _ring.Clear();
        }
        foreach (int id in closed) StreamClosed?.Invoke(id);
        _signal.Set();
    }

    /// <summary>Tono de prueba: pitidos alternados de 800/1200 Hz a −6 dBFS.</summary>
    public void PlayTone(int seconds)
    {
        seconds = Math.Clamp(seconds, 1, 10);
        lock (_gate)
        {
            if (_streams.Count > 0) throw new InvalidOperationException("El parlante está transmitiendo audio en vivo.");
            _ring.Clear();
            _toneLabel = "Tono de prueba";
            int total = seconds * SampleRate;
            for (int i = 0; i < total; i++)
            {
                int beep = i / (SampleRate / 4);               // cuartos de segundo
                double freq = beep % 2 == 0 ? 800 : 1200;
                double envelope = Math.Min(1, Math.Min(i % (SampleRate / 4), SampleRate / 4 - i % (SampleRate / 4)) / 80.0);
                _ring.Push((short)(Math.Sin(2 * Math.PI * freq * i / SampleRate) * 16384 * envelope));
            }
        }
        _signal.Set();
    }

    private void Run()
    {
        IAudioOutput? output = null;
        var frame = new short[FrameSamples];
        var idleSince = DateTime.UtcNow;
        bool prebuffering = true;
        var prebufferStart = DateTime.MinValue;
        while (!_disposed)
        {
            try
            {
                bool active;
                List<int>? expired = null;
                lock (_gate)
                {
                    foreach (var (id, live) in _streams)
                        if (DateTime.UtcNow - live.LastAudio > StreamIdleTimeout) (expired ??= []).Add(id);
                    if (expired is not null) foreach (int id in expired) _streams.Remove(id);
                    active = _streams.Count > 0 || _ring.Count > 0;
                }
                if (expired is not null)
                    foreach (int id in expired)
                    {
                        _log.LogInformation("Flujo {Stream} cerrado: sin audio por {Seconds} s.", id, StreamIdleTimeout.TotalSeconds);
                        StreamClosed?.Invoke(id);
                    }

                if (_reopen && output is not null)
                {
                    output.Dispose();
                    output = null;
                    _reopen = false;
                }
                if (!active)
                {
                    if (output is not null && DateTime.UtcNow - idleSince > ReleaseDeviceAfter)
                    {
                        output.Dispose();
                        output = null;
                    }
                    prebuffering = true;
                    prebufferStart = DateTime.MinValue;
                    _signal.WaitOne(500);
                    continue;
                }
                idleSince = DateTime.UtcNow;

                if (output is null)
                {
                    try
                    {
                        output = AudioOutputs.Open(_device);
                        OutputDevice = output.DeviceName;
                        OutputError = null;
                    }
                    catch (Exception ex)
                    {
                        OutputError = ex.Message;
                        _log.LogWarning("No se pudo abrir la salida de audio: {Error}", ex.Message);
                        lock (_gate) _ring.Clear();
                        Thread.Sleep(2000);
                        continue;
                    }
                }

                bool waitForMore = false;
                lock (_gate)
                {
                    // Colchón inicial contra el jitter (no aplica al tono, que ya está entero en el búfer).
                    if (prebuffering && _streams.Count > 0)
                    {
                        if (prebufferStart == DateTime.MinValue) prebufferStart = DateTime.UtcNow;
                        bool enough = _ring.Count >= SampleRate * PrebufferMilliseconds / 1000;
                        bool waitedTooLong = DateTime.UtcNow - prebufferStart > TimeSpan.FromMilliseconds(PrebufferMilliseconds * 3);
                        waitForMore = !enough && !waitedTooLong;
                    }
                    if (!waitForMore)
                    {
                        prebuffering = false;
                        int got = _ring.Pop(frame);
                        if (got < frame.Length) Array.Clear(frame, got, frame.Length - got);
                    }
                }
                if (waitForMore)
                {
                    _signal.WaitOne(10);
                    continue;
                }

                ApplyVolume(frame, _volume);
                output.Write(frame);
            }
            catch (Exception ex) when (!_disposed)
            {
                OutputError = ex.Message;
                _log.LogWarning("Error de la salida de audio: {Error}", ex.Message);
                try { output?.Dispose(); } catch (Exception) { }
                output = null;
                Thread.Sleep(1000);
            }
        }
        output?.Dispose();
    }

    /// <summary>Curva cuadrática: 50 % ≈ −12 dB, que se percibe como "la mitad".</summary>
    private static void ApplyVolume(Span<short> samples, int volume)
    {
        if (volume >= 100) return;
        double gain = volume / 100.0 * (volume / 100.0);
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(samples[i] * gain);
    }

    public void Dispose()
    {
        _disposed = true;
        _signal.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>Cola circular de muestras con tope (sin asignaciones por trama).</summary>
    private sealed class SampleRing(int capacity)
    {
        private readonly short[] _data = new short[capacity];
        private int _head;

        public int Count { get; private set; }

        public void Push(short sample)
        {
            if (Count == _data.Length) { _head = (_head + 1) % _data.Length; Count--; }
            _data[(_head + Count) % _data.Length] = sample;
            Count++;
        }

        public int Pop(Span<short> destination)
        {
            int n = Math.Min(destination.Length, Count);
            for (int i = 0; i < n; i++) destination[i] = _data[(_head + i) % _data.Length];
            _head = (_head + n) % _data.Length;
            Count -= n;
            return n;
        }

        /// <summary>Descarta lo más viejo hasta dejar <paramref name="max"/> muestras.</summary>
        public void TrimTo(int max)
        {
            if (Count <= max) return;
            int drop = Count - max;
            _head = (_head + drop) % _data.Length;
            Count = max;
        }

        public void Clear() { _head = 0; Count = 0; }
    }
}
