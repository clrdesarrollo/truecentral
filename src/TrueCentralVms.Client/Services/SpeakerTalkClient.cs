using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NAudio.Wave;

namespace TrueCentralVms.Client.Services;

/// <summary>
/// Voz en vivo del operador hacia los parlantes IP. Captura el micrófono con
/// WaveIn a 8 kHz / 16 bits / mono (el formato que el servidor convierte a
/// G.711 sin remuestrear) y lo empuja por un WebSocket propio del servidor
/// (<c>/api/speakers/talk</c>), que abre el canal de audio de cada parlante y
/// escribe en todos a la vez. El servidor manda dos mensajes de texto: "ready"
/// (qué parlantes quedaron dentro) y "closed" (motivo del cierre).
///
/// También ofrece un modo <b>prueba</b> (<see cref="StartMonitorAsync"/>): captura
/// el micrófono elegido sin enviar nada, solo para que el vúmetro muestre el
/// nivel y el operador confirme que ese es el micrófono correcto.
/// </summary>
public sealed class SpeakerTalkClient(ApiClient api) : IAsyncDisposable
{
    private ClientWebSocket? _socket;
    private WaveInEvent? _waveIn;
    private Channel<byte[]>? _frames;
    private Task? _sender;
    private Task? _receiver;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Texto para mostrar al operador (parlantes dentro, motivo de cierre).</summary>
    public event Action<string>? StatusChanged;

    /// <summary>El servidor cerró la sesión (por su cuenta o tras <see cref="StopAsync"/>).</summary>
    public event Action? Ended;

    /// <summary>Nivel del micrófono 0..100 (RMS con escala logarítmica), por cada trama capturada.</summary>
    public event Action<int>? LevelChanged;

    public bool IsActive => _socket is { State: WebSocketState.Open };

    public bool IsMonitoring => _waveIn is not null && _socket is null;

    /// <summary>Micrófonos disponibles (nombre de producto que informa Windows).</summary>
    public static IReadOnlyList<string> ListMicrophones()
    {
        var names = new List<string>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            try { names.Add(WaveInEvent.GetCapabilities(i).ProductName); }
            catch (Exception) { names.Add($"Micrófono {i + 1}"); }
        }
        return names;
    }

    /// <summary>Índice WaveIn del micrófono por nombre; -1 = el predeterminado de Windows.</summary>
    private static int DeviceIndexOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        var list = ListMicrophones();
        // Windows recorta los nombres a 31 caracteres en WaveIn: comparar por prefijo también.
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        for (int i = 0; i < list.Count; i++)
            if (name.StartsWith(list[i], StringComparison.OrdinalIgnoreCase) || list[i].StartsWith(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// Tono de apertura de canal, al estilo del "canal listo" de las radios:
    /// dos pitidos cortos (1000 y 1500 Hz) en PCM 16 bits / 8 kHz, en tramas
    /// de 40 ms como las del micrófono. Confirma al operador y a quien
    /// escucha que el canal está abierto antes de que empiece la voz.
    /// </summary>
    public static IEnumerable<byte[]> PreToneFrames()
    {
        var pcm = new List<byte>();
        void Tone(double hz, int ms, double amplitude)
        {
            int samples = 8000 * ms / 1000;
            for (int i = 0; i < samples; i++)
            {
                // Rampas de 5 ms para que no haga "clic" al entrar y salir.
                double env = Math.Min(1, Math.Min(i, samples - 1 - i) / 40.0);
                short v = (short)(Math.Sin(2 * Math.PI * hz * i / 8000.0) * amplitude * env);
                pcm.Add((byte)(v & 0xFF));
                pcm.Add((byte)((v >> 8) & 0xFF));
            }
        }
        void Silence(int ms) { pcm.AddRange(new byte[8000 * ms / 1000 * 2]); }
        Silence(60);
        Tone(1000, 110, 9000);
        Silence(40);
        Tone(1500, 110, 9000);
        Silence(120);
        for (int offset = 0; offset < pcm.Count; offset += 640)
            yield return pcm.GetRange(offset, Math.Min(640, pcm.Count - offset)).ToArray();
    }

    public async Task StartAsync(IReadOnlyList<int> speakerIds, string? microphone, bool preTone, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (IsActive) return;
            await CleanupAsync();
            if (WaveInEvent.DeviceCount == 0)
                throw new InvalidOperationException("Este equipo no tiene micrófono disponible.");
            if (api.BaseUrl is null || api.Token is null)
                throw new InvalidOperationException("Sin sesión con el servidor.");

            var baseUri = new Uri(api.BaseUrl);
            var builder = new UriBuilder(baseUri)
            {
                Scheme = baseUri.Scheme == "https" ? "wss" : "ws",
                Path = "/api/speakers/talk",
                Query = $"ids={string.Join(',', speakerIds)}&access_token={Uri.EscapeDataString(api.Token)}",
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
                // El servidor contesta 409 con el motivo antes de la mejora de
                // protocolo (parlante ocupado, sin conexión...); el cliente de
                // WebSocket solo ve que la mejora falló.
                throw new InvalidOperationException(
                    "El servidor no abrió la voz en vivo: el parlante está ocupado, sin conexión o no acepta audio en vivo. " +
                    $"({ex.Message})");
            }

            _socket = socket;
            _cts = new CancellationTokenSource();
            _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.DropOldest,   // mejor perder 40 ms que acumular retraso
                SingleReader = true,
            });
            _sender = Task.Run(() => SendLoopAsync(socket, _frames.Reader, _cts.Token));
            _receiver = Task.Run(() => ReceiveLoopAsync(socket, _cts.Token));
            // El tono entra a la cola antes que el micrófono: sale primero y al ritmo real.
            if (preTone)
                foreach (var frame in PreToneFrames()) _frames.Writer.TryWrite(frame);
            _waveIn = CreateCapture(microphone, _frames.Writer);
            _waveIn.StartRecording();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Solo captura (vúmetro), sin abrir nada contra el servidor.</summary>
    public async Task StartMonitorAsync(string? microphone)
    {
        await _gate.WaitAsync();
        try
        {
            if (IsActive) return;   // hablando: el vúmetro ya se alimenta de esa captura
            await CleanupAsync();
            if (WaveInEvent.DeviceCount == 0)
                throw new InvalidOperationException("Este equipo no tiene micrófono disponible.");
            _waveIn = CreateCapture(microphone, null);
            _waveIn.StartRecording();
        }
        finally
        {
            _gate.Release();
        }
    }

    private WaveInEvent CreateCapture(string? microphone, ChannelWriter<byte[]>? writer)
    {
        var waveIn = new WaveInEvent
        {
            DeviceNumber = DeviceIndexOf(microphone),
            WaveFormat = new WaveFormat(8000, 16, 1),
            BufferMilliseconds = 40,
            NumberOfBuffers = 4,
        };
        waveIn.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded <= 0) return;
            LevelChanged?.Invoke(Level(e.Buffer, e.BytesRecorded));
            if (writer is null) return;
            var frame = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, frame, 0, e.BytesRecorded);
            writer.TryWrite(frame);
        };
        waveIn.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) StatusChanged?.Invoke($"El micrófono dejó de capturar: {e.Exception.Message}");
        };
        return waveIn;
    }

    /// <summary>RMS de la trama en dBFS, mapeado a 0..100 (−60 dB = 0, 0 dB = 100).</summary>
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
        double db = 20 * Math.Log10(rms);
        return (int)Math.Clamp((db + 60) / 60 * 100, 0, 100);
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
            LevelChanged?.Invoke(0);
        }
        _frames?.Writer.TryComplete();
        var socket = _socket;
        _socket = null;
        if (socket is not null)
        {
            if (_sender is not null)
            {
                try { await Task.WhenAny(_sender, Task.Delay(1500)); } catch (Exception) { /* se cierra igual */ }
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
        var buffer = new byte[8 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) continue;
                HandleMessage(Encoding.UTF8.GetString(buffer, 0, result.Count));
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
                var names = root.TryGetProperty("speakers", out var speakers) && speakers.ValueKind == JsonValueKind.Array
                    ? speakers.EnumerateArray().Select(s => s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").ToList()
                    : [];
                var rejected = root.TryGetProperty("rejected", out var r) && r.ValueKind == JsonValueKind.Array
                    ? r.EnumerateArray().Select(x =>
                        $"{(x.TryGetProperty("speakerName", out var sn) ? sn.GetString() : "?")}: {(x.TryGetProperty("message", out var m) ? m.GetString() : "")}").ToList()
                    : [];
                string text = $"Hablando por {string.Join(", ", names)}.";
                if (rejected.Count > 0) text += " No aceptaron → " + string.Join("; ", rejected);
                StatusChanged?.Invoke(text);
            }
            else if (type == "closed")
            {
                string reason = root.TryGetProperty("reason", out var rs) ? rs.GetString() ?? "" : "";
                double seconds = root.TryGetProperty("seconds", out var sec) && sec.ValueKind == JsonValueKind.Number ? sec.GetDouble() : 0;
                StatusChanged?.Invoke($"Voz terminada ({seconds:0.#} s): {reason}.");
            }
        }
        catch (JsonException) { /* mensaje ajeno */ }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }
}
