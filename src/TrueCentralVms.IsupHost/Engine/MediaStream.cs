using System.Threading.Channels;

namespace TrueCentralVms.IsupHost.Engine;

/// <summary>
/// Flujo de un canal (video en vivo o reproducción) tal como lo entrega el SDK: bloques MPEG-PS que escriben sus hilos nativos y que
/// lee la petición HTTP que lo sirve. En vivo, si el lector se atrasa se descartan los bloques más viejos (mejor un salto que un
/// retraso creciente); en reproducción no se descarta nada: se frena al hilo del SDK hasta que haya sitio (el equipo manda una
/// grabación mucho más rápido que el tiempo real).
/// </summary>
public sealed class MediaStream
{
    readonly Channel<byte[]> _channel;
    volatile bool _closed;

    public MediaStream(string deviceId, int channel, bool playback)
    {
        DeviceId = deviceId;
        Channel = channel;
        IsPlayback = playback;
        _channel = playback
            ? System.Threading.Channels.Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait })
            : System.Threading.Channels.Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2048) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    }

    public string DeviceId { get; }
    public int Channel { get; }
    public bool IsPlayback { get; }
    public bool IsClosed => _closed;

    // Identificadores del SDK (los completa el motor).
    internal int UserId;
    internal int Session = -1;
    internal int LinkHandle = -1;
    internal bool Legacy;     // equipo ISUP 2.0/4.0: el enlace se cierra también por su handle

    long _bytes;
    public long BytesReceived => Interlocked.Read(ref _bytes);

    /// <summary>Lo llama el hilo del SDK con cada bloque recibido.</summary>
    internal void Push(byte[] chunk)
    {
        if (_closed) return;
        Interlocked.Add(ref _bytes, chunk.Length);
        if (!IsPlayback) { _channel.Writer.TryWrite(chunk); return; }
        // Reproducción: contrapresión hacia el equipo (el hilo del SDK espera; nunca async desde un callback nativo).
        while (!_closed && !_channel.Writer.TryWrite(chunk)) Thread.Sleep(10);
    }

    /// <summary>Siguiente bloque, o null cuando el flujo se cerró.</summary>
    public async ValueTask<byte[]?> ReadAsync(CancellationToken ct)
    {
        try { return await _channel.Reader.ReadAsync(ct); }
        catch (ChannelClosedException) { return null; }
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _channel.Writer.TryComplete();
    }
}
