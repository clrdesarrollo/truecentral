using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.ZkTeco;

/// <summary>Respuesta del equipo a un comando.</summary>
public readonly record struct ZkReply(ushort Command, ushort SessionId, byte[] Data)
{
    public bool Ok => Command is ZkProtocol.CmdAckOk or ZkProtocol.CmdAckData;
}

/// <summary>
/// Sesión con un equipo ZKTeco por TCP 4370. Los equipos aceptan UNA conexión
/// del SDK a la vez, así que la sesión se abre para lo que se necesita y se
/// cierra enseguida (nada de conexiones cacheadas como en ISAPI).
/// </summary>
public sealed class ZkConnection : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(8);
    /// <summary>Tope de un mensaje de respuesta (los del mantenedor son de bytes, no de KB).</summary>
    private const int MaxReplyBytes = 64 * 1024;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private ushort _sessionId;
    private ushort _replyId = ushort.MaxValue - 1;
    private bool _closed;

    private ZkConnection(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    /// <summary>
    /// Abre la sesión y se autentica si el equipo lo pide. <paramref name="commKey"/>
    /// es la clave de comunicación del equipo (0 = sin clave).
    /// </summary>
    public static async Task<ZkConnection> OpenAsync(string host, int port, int commKey, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(host, port, connectCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new DriverException($"El equipo en {host}:{port} no respondió a tiempo.");
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new DriverException($"No se pudo conectar con el equipo en {host}:{port}: {ex.Message}", ex);
        }

        var connection = new ZkConnection(client);
        try
        {
            var reply = await connection.SendAsync(ZkProtocol.CmdConnect, null, ct);
            // El número de sesión lo fija el equipo en su respuesta y va en
            // todos los mensajes siguientes.
            connection._sessionId = reply.SessionId;
            if (reply.Command == ZkProtocol.CmdAckUnauth)
            {
                var auth = await connection.SendAsync(ZkProtocol.CmdAuth,
                    ZkProtocol.MakeCommKey(commKey, connection._sessionId), ct);
                if (!auth.Ok)
                    throw new DriverException(
                        "El equipo rechazó la clave de comunicación (Comm Key). Revísela en el menú del equipo " +
                        "(Comunicación → Seguridad); de fábrica es 0.");
            }
            else if (!reply.Ok)
            {
                throw new DriverException("El equipo rechazó la conexión del SDK (¿hay otra aplicación conectada?).");
            }
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Valor de un parámetro del equipo ("~DeviceName", "~SerialNumber"…); null si no lo tiene.</summary>
    public async Task<string?> GetParameterAsync(string name, CancellationToken ct)
    {
        var reply = await SendAsync(ZkProtocol.CmdOptionsRead, Encoding.ASCII.GetBytes(name), ct);
        if (!reply.Ok || reply.Data.Length == 0) return null;
        string text = Encoding.ASCII.GetString(reply.Data).Split('\0')[0];
        int separator = text.IndexOf('=');
        string value = (separator >= 0 ? text[(separator + 1)..] : text).Trim();
        return value.Length > 0 ? value : null;
    }

    /// <summary>Versión de firmware que informa el equipo.</summary>
    public async Task<string?> GetFirmwareAsync(CancellationToken ct)
    {
        var reply = await SendAsync(ZkProtocol.CmdGetVersion, null, ct);
        if (!reply.Ok || reply.Data.Length == 0) return null;
        string value = Encoding.ASCII.GetString(reply.Data).Split('\0')[0].Trim();
        return value.Length > 0 ? value : null;
    }

    /// <summary>
    /// Ocupación y cupos del equipo. El equipo responde una tabla de enteros
    /// de 32 bits en posiciones fijas (usuarios en la 4, huellas en la 6,
    /// registros en la 8, tarjetas en la 12, y los cupos entre la 14 y la 16).
    /// </summary>
    public async Task<ZkSizes?> GetSizesAsync(CancellationToken ct)
    {
        var reply = await SendAsync(ZkProtocol.CmdGetFreeSizes, null, ct);
        if (!reply.Ok || reply.Data.Length < 80) return null;
        int Field(int index) => BinaryPrimitives.ReadInt32LittleEndian(reply.Data.AsSpan(index * 4));
        return new ZkSizes(
            Users: Field(4), Fingers: Field(6), Records: Field(8), Cards: Field(12),
            FingerCapacity: Field(14), UserCapacity: Field(15), RecordCapacity: Field(16));
    }

    // ------------------------------------------------------------------
    // Transporte
    // ------------------------------------------------------------------

    /// <summary>
    /// Manda un comando y devuelve su respuesta. Público porque los comandos de
    /// operación (abrir puerta, leer el historial) los arma el driver, no esta
    /// clase, que solo pone el transporte.
    /// </summary>
    public Task<ZkReply> CommandAsync(ushort command, byte[]? data, CancellationToken ct) =>
        SendAsync(command, data, ct);

    /// <summary>
    /// Lee un bloque grande del equipo (historial de marcas, padrón). El
    /// protocolo no lo manda de una: contesta <c>CmdPrepareData</c> con cuántos
    /// bytes vienen y después los va mandando en mensajes <c>CmdData</c> hasta
    /// completarlos, cerrando con un acuse. Algunos firmware cortan camino y
    /// responden los datos de inmediato en un solo mensaje.
    /// </summary>
    public async Task<byte[]> ReadBulkAsync(ushort command, byte[]? data, CancellationToken ct)
    {
        var reply = await SendAsync(command, data, ct);
        if (!reply.Ok && reply.Command != ZkProtocol.CmdPrepareData)
            throw new DriverException("El equipo rechazó la lectura de datos.");

        // Respuesta corta: el equipo mandó todo junto.
        if (reply.Command != ZkProtocol.CmdPrepareData) return reply.Data;
        if (reply.Data.Length < 4) return [];

        int expected = BinaryPrimitives.ReadInt32LittleEndian(reply.Data);
        if (expected <= 0) return [];
        if (expected > MaxBulkBytes)
            throw new DriverException($"El equipo anunció {expected} bytes de datos, más de lo que se puede leer de una vez.");

        var buffer = new byte[expected];
        int written = 0;
        while (written < expected)
        {
            var chunk = await ReadReplyAsync(ct);
            if (chunk.Command == ZkProtocol.CmdData)
            {
                int take = Math.Min(chunk.Data.Length, expected - written);
                chunk.Data.AsSpan(0, take).CopyTo(buffer.AsSpan(written));
                written += take;
                continue;
            }
            // El acuse final llega apenas termina el último bloque.
            if (chunk.Ok) break;
            throw new DriverException("Se cortó la lectura de datos del equipo.");
        }
        return buffer.AsSpan(0, written).ToArray();
    }

    /// <summary>Tope de una lectura larga: el historial completo de un terminal cabe de sobra.</summary>
    private const int MaxBulkBytes = 8 * 1024 * 1024;

    private async Task<ZkReply> SendAsync(ushort command, byte[]? data, CancellationToken ct)
    {
        _replyId = unchecked((ushort)(_replyId + 1));
        byte[] packet = ZkProtocol.BuildPacket(command, _sessionId, _replyId, data ?? []);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CommandTimeout);
        try
        {
            await _stream.WriteAsync(ZkProtocol.WrapTcp(packet), cts.Token);
            return await ReadReplyAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DriverException("El equipo no respondió a tiempo al comando del SDK.");
        }
        catch (IOException ex)
        {
            throw new DriverException($"Se cortó la comunicación con el equipo: {ex.Message}", ex);
        }
    }

    private async Task<ZkReply> ReadReplyAsync(CancellationToken ct)
    {
        var header = new byte[ZkProtocol.HeaderSize];
        await _stream.ReadExactlyAsync(header, ct);
        if (!header.AsSpan(0, 4).SequenceEqual(ZkProtocol.TcpPrefix))
            throw new DriverException("El equipo respondió algo que no es el protocolo de ZKTeco (¿es un equipo ZKTeco?).");

        int size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (size < ZkProtocol.HeaderSize || size > MaxReplyBytes)
            throw new DriverException($"El equipo anunció una respuesta de tamaño inválido ({size} bytes).");

        var payload = new byte[size];
        await _stream.ReadExactlyAsync(payload, ct);
        if (!ZkProtocol.TryReadHeader(payload, out ushort command, out ushort sessionId, out _))
            throw new DriverException("El equipo respondió un mensaje incompleto.");
        return new ZkReply(command, sessionId, payload[ZkProtocol.HeaderSize..]);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_closed)
        {
            _closed = true;
            // Cortesía con el equipo: cerrar la sesión libera el único cupo de
            // conexión del SDK. Si ya se cayó, no hay nada que reportar.
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                byte[] packet = ZkProtocol.BuildPacket(ZkProtocol.CmdExit, _sessionId,
                    unchecked((ushort)(_replyId + 1)), []);
                await _stream.WriteAsync(ZkProtocol.WrapTcp(packet), cts.Token);
            }
            catch { /* la sesión se cierra igual al soltar el socket */ }
        }
        _stream.Dispose();
        _client.Dispose();
    }
}

/// <summary>Ocupación y cupos que informa el equipo.</summary>
public readonly record struct ZkSizes(
    int Users, int Fingers, int Records, int Cards,
    int FingerCapacity, int UserCapacity, int RecordCapacity);
