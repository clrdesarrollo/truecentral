using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NAudio.Wave;

namespace TrueCentralVms.Client.Services;

/// <summary>
/// Conversación del operador con un frente de citofonía: voz en ambos
/// sentidos por el WebSocket <c>/api/intercoms/{id}/voice</c>. Sube el
/// micrófono (WaveIn 8 kHz / 16 bits / mono, tramas de 40 ms) y reproduce por
/// los parlantes del equipo (WaveOut) el PCM que capta el frente. El micrófono
/// se puede silenciar sin cortar: mientras está silenciado se mandan tramas
/// de silencio, porque el servidor corta la conversación si no le llega audio.
/// </summary>
public sealed class IntercomVoiceClient(ApiClient api) : IAsyncDisposable
{
    private static readonly WaveFormat Format = new(8000, 16, 1);

    private ClientWebSocket? _socket;
    private WaveInEvent? _waveIn;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _playback;
    private Channel<byte[]>? _frames;
    private Task? _sender;
    private Task? _receiver;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _muted;
    private float _volume = 1f;

    /// <summary>Texto para el operador (conversación abierta, motivo del cierre).</summary>
    public event Action<string>? StatusChanged;

    /// <summary>La conversación terminó (por el operador, el servidor o la red).</summary>
    public event Action? Ended;

    /// <summary>
    /// El frente dio por terminada la llamada (al abrir la puerta o por su tope
    /// de conversación) pero la voz sigue abierta (payload: motivo).
    /// </summary>
    public event Action<string>? Detached;

    /// <summary>Nivel 0..100 del micrófono del operador.</summary>
    public event Action<int>? MicLevelChanged;

    /// <summary>Nivel 0..100 de lo que llega del frente.</summary>
    public event Action<int>? RemoteLevelChanged;

    public bool IsActive => _socket is { State: WebSocketState.Open };

    public bool Muted
    {
        get => _muted;
        set => _muted = value;
    }

    /// <summary>Volumen de lo que se escucha del frente, 0..1.</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            try { if (_waveOut is not null) _waveOut.Volume = _volume; } catch { /* dispositivo cerrado */ }
        }
    }

    /// <summary>Abre la conversación. Lanza <see cref="InvalidOperationException"/> con el motivo en español.</summary>
    public async Task StartAsync(int intercomId, string? microphone, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (IsActive) return;
            await CleanupAsync();
            if (api.BaseUrl is null || api.Token is null)
                throw new InvalidOperationException("Sin sesión con el servidor.");

            var baseUri = new Uri(api.BaseUrl);
            var builder = new UriBuilder(baseUri)
            {
                Scheme = baseUri.Scheme == "https" ? "wss" : "ws",
                Path = $"/api/intercoms/{intercomId}/voice",
                Query = $"access_token={Uri.EscapeDataString(api.Token)}",
            };
            var socket = new ClientWebSocket();
            socket.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.ConnectAsync(builder.Uri, connectTimeout.Token);
            }
            catch (WebSocketException ex)
            {
                socket.Dispose();
                // El servidor contesta 409 con el motivo antes de la mejora de protocolo.
                throw new InvalidOperationException(
                    "El servidor no abrió la conversación: otro operador está hablando con este frente, la llamada ya la tomó alguien o el frente no responde. " +
                    $"({ex.Message})");
            }

            _socket = socket;
            _cts = new CancellationTokenSource();
            _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

            // Salida: búfer corto para que la voz del visitante no se atrase.
            _playback = new BufferedWaveProvider(Format)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
            };
            _waveOut = new WaveOutEvent { DesiredLatency = 150, NumberOfBuffers = 3, Volume = _volume };
            _waveOut.Init(_playback);
            _waveOut.Play();

            _sender = Task.Run(() => SendLoopAsync(socket, _frames.Reader, _cts.Token));
            _receiver = Task.Run(() => ReceiveLoopAsync(socket, _cts.Token));

            if (WaveInEvent.DeviceCount > 0)
            {
                _waveIn = CreateCapture(microphone, _frames.Writer);
                _waveIn.StartRecording();
            }
            else
            {
                // Sin micrófono igual se escucha al visitante; se mandan silencios para mantener la sesión.
                StatusChanged?.Invoke("Este equipo no tiene micrófono: solo podrá escuchar.");
                _ = Task.Run(() => SilenceLoopAsync(_frames.Writer, _cts.Token));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static int DeviceIndexOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        var list = SpeakerTalkClient.ListMicrophones();
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        for (int i = 0; i < list.Count; i++)
            if (name.StartsWith(list[i], StringComparison.OrdinalIgnoreCase) || list[i].StartsWith(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private WaveInEvent CreateCapture(string? microphone, ChannelWriter<byte[]> writer)
    {
        var waveIn = new WaveInEvent
        {
            DeviceNumber = DeviceIndexOf(microphone),
            WaveFormat = Format,
            BufferMilliseconds = 40,
            NumberOfBuffers = 4,
        };
        waveIn.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded <= 0) return;
            var frame = new byte[e.BytesRecorded];
            if (!_muted) Buffer.BlockCopy(e.Buffer, 0, frame, 0, e.BytesRecorded);
            MicLevelChanged?.Invoke(_muted ? 0 : Level(frame, frame.Length));
            writer.TryWrite(frame);
        };
        waveIn.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) StatusChanged?.Invoke($"El micrófono dejó de capturar: {e.Exception.Message}");
        };
        return waveIn;
    }

    private static async Task SilenceLoopAsync(ChannelWriter<byte[]> writer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                writer.TryWrite(new byte[640]);
                await Task.Delay(40, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>RMS en dBFS mapeado a 0..100 (−60 dB = 0, 0 dB = 100).</summary>
    private static int Level(byte[] buffer, int count)
    {
        int samples = count / 2;
        if (samples == 0) return 0;
        double sum = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(buffer[2 * i] | (buffer[2 * i + 1] << 8));
            sum += (double)s * s;
        }
        double rms = Math.Sqrt(sum / samples) / short.MaxValue;
        if (rms <= 0.0001) return 0;
        return (int)Math.Clamp((20 * Math.Log10(rms) + 60) / 60 * 100, 0, 100);
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try { await CleanupAsync(); }
        finally { _gate.Release(); }
    }

    private async Task CleanupAsync()
    {
        var waveIn = _waveIn;
        _waveIn = null;
        if (waveIn is not null)
        {
            try { waveIn.StopRecording(); } catch (Exception) { /* ya detenido */ }
            waveIn.Dispose();
            MicLevelChanged?.Invoke(0);
        }
        _frames?.Writer.TryComplete();
        var socket = _socket;
        _socket = null;
        if (socket is not null)
        {
            if (_sender is not null)
            {
                try { await Task.WhenAny(_sender, Task.Delay(1000)); } catch (Exception) { /* se cierra igual */ }
            }
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await socket.SendAsync(Encoding.UTF8.GetBytes("stop"), WebSocketMessageType.Text, true, closeTimeout.Token);
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "fin", closeTimeout.Token);
                }
            }
            catch (Exception) { /* la red ya se cortó */ }
        }
        _cts?.Cancel();
        if (_receiver is not null)
        {
            try { await Task.WhenAny(_receiver, Task.Delay(1000)); } catch (Exception) { /* nada que hacer */ }
        }
        var waveOut = _waveOut;
        _waveOut = null;
        if (waveOut is not null)
        {
            try { waveOut.Stop(); } catch (Exception) { /* ya detenido */ }
            waveOut.Dispose();
            RemoteLevelChanged?.Invoke(0);
        }
        _playback = null;
        socket?.Dispose();
        _cts?.Dispose();
        _cts = null;
        _sender = null;
        _receiver = null;
        _frames = null;
    }

    private static async Task SendLoopAsync(ClientWebSocket socket, ChannelReader<byte[]> frames, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in frames.ReadAllAsync(ct))
            {
                if (socket.State != WebSocketState.Open) break;
                await socket.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
            }
        }
        catch (Exception) { /* cierre o red caída: lo informa el receptor */ }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var message = new List<byte>();
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.AddRange(buffer.AsSpan(0, result.Count));
                if (!result.EndOfMessage) continue;
                var data = message.ToArray();
                message.Clear();
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    HandleMessage(Encoding.UTF8.GetString(data));
                    continue;
                }
                _playback?.AddSamples(data, 0, data.Length);
                RemoteLevelChanged?.Invoke(Level(data, data.Length));
            }
        }
        catch (Exception) { /* cierre normal o red caída */ }
        Ended?.Invoke();
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "ready")
            {
                string name = root.TryGetProperty("intercom", out var n) ? n.GetString() ?? "" : "";
                StatusChanged?.Invoke($"Conversación abierta con {name}.");
            }
            else if (type == "detached")
            {
                Detached?.Invoke(root.TryGetProperty("reason", out var dr) ? dr.GetString() ?? "" : "");
            }
            else if (type == "closed")
            {
                string reason = root.TryGetProperty("reason", out var rs) ? rs.GetString() ?? "" : "";
                StatusChanged?.Invoke($"Conversación terminada: {reason}.");
            }
        }
        catch (JsonException) { /* mensaje ajeno */ }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

/// <summary>
/// Timbre de llamada entrante: patrón de teléfono (dos ráfagas de 440+480 Hz,
/// pausa) sintetizado en memoria y repetido hasta que se llama a <see cref="Stop"/>.
/// Un solo timbre por aplicación aunque suenen varias llamadas.
/// </summary>
public sealed class IntercomRinger
{
    private WaveOutEvent? _out;
    private int _ringing;

    private static byte[] Pattern()
    {
        const int rate = 8000;
        var pcm = new List<byte>();
        void Burst(int ms)
        {
            int n = rate * ms / 1000;
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1, Math.Min(i, n - 1 - i) / 80.0);
                double v = (Math.Sin(2 * Math.PI * 440 * i / rate) + Math.Sin(2 * Math.PI * 480 * i / rate)) * 0.5;
                // Modulación a 20 Hz: el "trino" clásico de campanilla.
                v *= 0.6 + 0.4 * Math.Sign(Math.Sin(2 * Math.PI * 20 * i / rate));
                short s = (short)(v * 12000 * env);
                pcm.Add((byte)(s & 0xFF));
                pcm.Add((byte)((s >> 8) & 0xFF));
            }
        }
        void Silence(int ms) => pcm.AddRange(new byte[rate * ms / 1000 * 2]);
        Burst(400); Silence(200); Burst(400); Silence(1800);
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
        if (Interlocked.Exchange(ref _ringing, 1) == 1) return;
        try
        {
            _out = new WaveOutEvent { DesiredLatency = 200 };
            _out.Init(new LoopProvider(Pattern()));
            _out.Play();
        }
        catch
        {
            _out?.Dispose();
            _out = null;
            System.Media.SystemSounds.Exclamation.Play();
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _ringing, 0) == 0) return;
        var output = _out;
        _out = null;
        try { output?.Stop(); } catch { /* ya detenido */ }
        output?.Dispose();
    }
}
