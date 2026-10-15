using System.Buffers.Binary;
using System.Net.Sockets;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Adam;

/// <summary>
/// Cliente Modbus TCP mínimo, con solo las funciones que usan los módulos
/// ADAM-6000 de E/S digital: leer bobinas, entradas y registros, y escribir
/// bobinas. Mantiene una conexión persistente y atiende las peticiones de a una,
/// en orden de llegada, así que se puede usar desde varios hilos a la vez.
/// Si la conexión se cae (el módulo se reinició o cerró el socket), la
/// siguiente petición reconecta sola.
/// </summary>
/// <remarks>
/// La cancelación del llamador se respeta mientras la petición espera turno o
/// conecta. Una vez enviada al módulo, la petición termina (o vence su
/// <see cref="RequestTimeout"/>): cortarla a la mitad dejaría la orden en duda
/// y la conexión desincronizada.
/// </remarks>
public sealed class ModbusTcpClient : IAsyncDisposable
{
    public const int DefaultPort = 502;

    private const byte FcReadCoils = 0x01;
    private const byte FcReadDiscreteInputs = 0x02;
    private const byte FcReadHoldingRegisters = 0x03;
    private const byte FcWriteSingleCoil = 0x05;
    private const byte FcWriteMultipleCoils = 0x0F;

    /// <summary>Cabecera MBAP: transacción, protocolo, largo y unidad.</summary>
    private const int HeaderSize = 7;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private ushort _transactionId;
    private bool _disposed;

    public ModbusTcpClient(string host, int port = DefaultPort, byte unitId = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        Host = host.Trim();
        Port = port;
        UnitId = unitId;
    }

    public string Host { get; }
    public int Port { get; }

    /// <summary>Unidad Modbus. Los ADAM por TCP aceptan cualquiera; por omisión 1.</summary>
    public byte UnitId { get; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Abre la conexión ahora, en vez de esperar a la primera petición.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Lee bobinas (función 1). <paramref name="address"/> es base 0: la bobina 00017 es la dirección 16.</summary>
    public async Task<bool[]> ReadCoilsAsync(ushort address, ushort count, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, (ushort)2000);
        var response = await RequestAsync(BuildRead(FcReadCoils, address, count), ct);
        return UnpackBits(response, count);
    }

    /// <summary>Lee entradas discretas (función 2). Dirección base 0.</summary>
    public async Task<bool[]> ReadDiscreteInputsAsync(ushort address, ushort count, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, (ushort)2000);
        var response = await RequestAsync(BuildRead(FcReadDiscreteInputs, address, count), ct);
        return UnpackBits(response, count);
    }

    /// <summary>Lee registros de retención (función 3). Dirección base 0: el registro 40211 es la dirección 210.</summary>
    public async Task<ushort[]> ReadHoldingRegistersAsync(ushort address, ushort count, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, (ushort)125);
        var response = await RequestAsync(BuildRead(FcReadHoldingRegisters, address, count), ct);
        if (response.Length < 2 || response[1] != count * 2 || response.Length < 2 + count * 2)
            throw new DriverException("El módulo respondió una lectura de registros incompleta.");

        var values = new ushort[count];
        for (int i = 0; i < count; i++)
            values[i] = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2 + i * 2));
        return values;
    }

    /// <summary>Escribe una bobina (función 5). Dirección base 0.</summary>
    public async Task WriteSingleCoilAsync(ushort address, bool value, CancellationToken ct = default)
    {
        byte[] pdu = [FcWriteSingleCoil, (byte)(address >> 8), (byte)address, value ? (byte)0xFF : (byte)0x00, 0x00];
        var response = await RequestAsync(pdu, ct);
        // El módulo confirma devolviendo la misma petición.
        if (!response.AsSpan().SequenceEqual(pdu))
            throw new DriverException("El módulo no confirmó la escritura de la salida.");
    }

    /// <summary>Escribe varias bobinas seguidas (función 15). Dirección base 0.</summary>
    public async Task WriteMultipleCoilsAsync(ushort address, IReadOnlyList<bool> values, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(values.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(values.Count, 1968);
        int byteCount = (values.Count + 7) / 8;
        var pdu = new byte[6 + byteCount];
        pdu[0] = FcWriteMultipleCoils;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Count);
        pdu[5] = (byte)byteCount;
        for (int i = 0; i < values.Count; i++)
            if (values[i])
                pdu[6 + i / 8] |= (byte)(1 << (i % 8));

        var response = await RequestAsync(pdu, ct);
        // Confirma con dirección y cantidad.
        if (response.Length < 5 || !response.AsSpan(0, 5).SequenceEqual(pdu.AsSpan(0, 5)))
            throw new DriverException("El módulo no confirmó la escritura de las salidas.");
    }

    // ------------------------------------------------------------------
    // Transporte
    // ------------------------------------------------------------------

    private static byte[] BuildRead(byte function, ushort address, ushort count) =>
        [function, (byte)(address >> 8), (byte)address, (byte)(count >> 8), (byte)count];

    /// <summary>Respuesta de lectura de bits: [función, bytes, datos…], bit 0 del primer byte = primera dirección.</summary>
    private static bool[] UnpackBits(byte[] response, int count)
    {
        int byteCount = (count + 7) / 8;
        if (response.Length < 2 + byteCount || response[1] < byteCount)
            throw new DriverException("El módulo respondió una lectura de E/S incompleta.");

        var bits = new bool[count];
        for (int i = 0; i < count; i++)
            bits[i] = (response[2 + i / 8] & (1 << (i % 8))) != 0;
        return bits;
    }

    private async Task<byte[]> RequestAsync(byte[] pdu, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                bool reused = _stream is not null;
                var stream = await EnsureConnectedAsync(ct);
                try
                {
                    return await ExchangeAsync(stream, pdu);
                }
                catch (ModbusException)
                {
                    // El módulo contestó: la conexión sigue sana.
                    throw;
                }
                catch (Exception ex)
                {
                    Close();
                    // Una conexión vieja que el módulo ya había soltado falla en
                    // la primera petición: se reintenta una vez con una nueva.
                    // Todas las funciones que se usan son idempotentes.
                    if (reused && attempt == 0 && !_disposed) continue;
                    throw ex is OperationCanceledException
                        ? new DriverException($"El módulo en {Host}:{Port} no respondió a tiempo.")
                        : new DriverException($"Se cortó la comunicación con el módulo en {Host}:{Port}: {ex.Message}", ex);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<byte[]> ExchangeAsync(NetworkStream stream, byte[] pdu)
    {
        ushort transactionId = unchecked(++_transactionId);
        var frame = new byte[HeaderSize + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, transactionId);
        // Bytes 2-3: protocolo Modbus = 0.
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(pdu.Length + 1));
        frame[6] = UnitId;
        pdu.CopyTo(frame, HeaderSize);

        using var timeout = new CancellationTokenSource(RequestTimeout);
        await stream.WriteAsync(frame, timeout.Token);

        var header = new byte[HeaderSize];
        while (true)
        {
            await stream.ReadExactlyAsync(header, timeout.Token);
            ushort replyId = BinaryPrimitives.ReadUInt16BigEndian(header);
            ushort protocol = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
            int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
            if (protocol != 0 || length < 2 || length > 254)
                throw new IOException("el equipo respondió algo que no es Modbus TCP");

            var body = new byte[length - 1];
            await stream.ReadExactlyAsync(body, timeout.Token);
            // Respuesta de otra transacción: se descarta y se sigue esperando la nuestra.
            if (replyId != transactionId) continue;

            if (body[0] == (pdu[0] | 0x80))
                throw new ModbusException(pdu[0], body.Length > 1 ? body[1] : (byte)0);
            if (body[0] != pdu[0])
                throw new IOException($"respuesta a la función {body[0]} cuando se pidió la {pdu[0]}");
            return body;
        }
    }

    private async Task<NetworkStream> EnsureConnectedAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream is not null) return _stream;

        var client = new TcpClient { NoDelay = true };
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(Host, Port, connectCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new DriverException($"El módulo en {Host}:{Port} no respondió a tiempo.");
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new DriverException($"No se pudo conectar con el módulo en {Host}:{Port}: {ex.Message}", ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _client = client;
        _stream = client.GetStream();
        return _stream;
    }

    private void Close()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        Close();
        return ValueTask.CompletedTask;
    }
}
