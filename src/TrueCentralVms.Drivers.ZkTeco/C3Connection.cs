using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.ZkTeco;

/// <summary>
/// Sesión TCP con una controladora inBio/C3 (<see cref="C3Protocol"/>).
///
/// Al conectar, la controladora entrega un número de sesión; desde ahí cada
/// trama lleva, delante de sus datos, la sesión y un número de petición que
/// sube de a uno, y la controladora los repite delante de los datos de su
/// respuesta. Los firmware viejos no conocen las sesiones: si la conexión con
/// sesión se rechaza se reintenta sin ella, como hace el PullSDK.
/// </summary>
public sealed partial class C3Connection : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Sesión y petición con que se pide la conexión, antes de que la controladora fije la suya.</summary>
    private const ushort InitialSession = 0xFEFE;

    /// <summary>Error de la controladora: clave de comunicación incorrecta.</summary>
    private const int ErrorBadPassword = -14;
    /// <summary>Error de la controladora: comando que este firmware no tiene.</summary>
    public const int ErrorUnsupported = -13;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly string _host;
    private ushort? _session;
    private ushort _request = InitialSession;
    private bool _closed;

    private C3Connection(TcpClient client, string host)
    {
        _client = client;
        _stream = client.GetStream();
        _host = host;
    }

    /// <summary>
    /// Abre la sesión. <paramref name="password"/> es la clave de comunicación
    /// de la controladora (ComPwd); vacía si no tiene, que es como vienen de fábrica.
    /// </summary>
    public static async Task<C3Connection> OpenAsync(string host, int port, string? password, CancellationToken ct)
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
            throw new DriverException($"La controladora en {host}:{port} no respondió a tiempo.");
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new DriverException($"No se pudo conectar con la controladora en {host}:{port}: {ex.Message}", ex);
        }

        var connection = new C3Connection(client, host);
        try
        {
            byte[] secret = string.IsNullOrEmpty(password) ? [] : Encoding.ASCII.GetBytes(password);
            connection._session = InitialSession;
            var reply = await connection.ExchangeAsync(C3Protocol.CmdConnect, secret, ct, expectSession: false);
            if (reply.Ok && reply.Data.Length >= 2)
            {
                connection._session = BinaryPrimitives.ReadUInt16LittleEndian(reply.Data);
                return connection;
            }
            if (reply.ErrorCode == ErrorBadPassword) throw BadPassword();

            // Firmware sin sesiones.
            connection._session = null;
            reply = await connection.ExchangeAsync(C3Protocol.CmdConnectSessionLess, secret, ct, expectSession: false);
            if (reply.Ok) return connection;
            if (reply.ErrorCode == ErrorBadPassword) throw BadPassword();
            throw new DriverException(
                $"La controladora rechazó la conexión (código {reply.ErrorCode}). ¿Hay otra aplicación conectada a ella?");
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static DriverException BadPassword() => new(
        "La controladora rechazó la clave de comunicación. Es la que tiene configurada en ZKAccess " +
        "(Equipo → Modificar clave de comunicación); de fábrica no tiene.");

    /// <summary>
    /// Parámetros de la controladora por nombre ("~SerialNumber", "LockCount",
    /// "Door1Drivertime"…). Los que la controladora no conoce no vienen.
    /// </summary>
    public async Task<Dictionary<string, string>> GetParametersAsync(IEnumerable<string> names, CancellationToken ct)
    {
        var reply = await ExchangeAsync(C3Protocol.CmdGetParam, Encoding.ASCII.GetBytes(string.Join(',', names)), ct);
        if (!reply.Ok)
            throw new DriverException($"La controladora no entregó sus parámetros (código {reply.ErrorCode}).");
        return C3Protocol.ParseKeyValues(reply.Data);
    }

    /// <summary>Orden de control (<see cref="C3Protocol.CmdControl"/>): puertas, salidas, alarmas, reinicio.</summary>
    public async Task ControlAsync(byte operation, byte param1, byte param2, byte param3, CancellationToken ct)
    {
        var reply = await ExchangeAsync(C3Protocol.CmdControl, [operation, param1, param2, param3, 0], ct);
        if (!reply.Ok)
            throw new DriverException($"La controladora rechazó la orden (código {reply.ErrorCode}).");
    }

    /// <summary>Escribe parámetros (<c>nombre=valor</c>), todos en una orden.</summary>
    public async Task SetParametersAsync(IEnumerable<string> assignments, CancellationToken ct)
    {
        var reply = await ExchangeAsync(C3Protocol.CmdSetTime, Encoding.ASCII.GetBytes(string.Join(',', assignments)), ct);
        if (!reply.Ok)
            throw new DriverException($"La controladora rechazó los parámetros (código {reply.ErrorCode}).");
    }

    /// <summary>Fija la hora local de la controladora.</summary>
    public async Task SetTimeAsync(DateTime local, CancellationToken ct)
    {
        var reply = await ExchangeAsync(C3Protocol.CmdSetTime,
            Encoding.ASCII.GetBytes($"DateTime={C3Protocol.EncodeTime(local)}"), ct);
        if (!reply.Ok)
            throw new DriverException($"La controladora rechazó la hora (código {reply.ErrorCode}).");
    }

    /// <summary>
    /// Lo que pasó desde la última lectura de ESTA sesión, en registros de 16
    /// bytes (<see cref="C3Protocol.RtLogRecordSize"/>). Si la controladora
    /// contesta algo que no es múltiplo de 16, su firmware solo conoce el
    /// formato de texto: se devuelve null y el llamador pasa a
    /// <see cref="ReadRtLogTextAsync"/>.
    /// </summary>
    public async Task<byte[]?> ReadRtLogAsync(CancellationToken ct)
    {
        var reply = await ExchangeAsync(C3Protocol.CmdRtLog, [], ct);
        if (!reply.Ok)
            throw new DriverException($"La controladora no entregó sus eventos (código {reply.ErrorCode}).");
        return reply.Data.Length % C3Protocol.RtLogRecordSize == 0 ? reply.Data : null;
    }

    /// <summary>Eventos en el formato de texto de los firmware nuevos (clave=valor).</summary>
    public async Task<Dictionary<string, string>> ReadRtLogTextAsync(CancellationToken ct)
    {
        var reply = await ExchangeAsync(C3Protocol.CmdRtLogText, [], ct);
        if (!reply.Ok)
            throw new DriverException($"La controladora no entregó sus eventos (código {reply.ErrorCode}).");
        return C3Protocol.ParseKeyValues(reply.Data);
    }

    /// <summary>Comando cualquiera; devuelve la respuesta tal cual (para los comandos de tablas y el diagnóstico).</summary>
    public async Task<C3Reply> SendAsync(byte command, byte[] data, CancellationToken ct) =>
        await ExchangeAsync(command, data, ct);

    /// <summary>Respuesta de la controladora, ya sin sesión ni número de petición.</summary>
    public readonly record struct C3Reply(byte Command, byte[] Data)
    {
        public bool Ok => Command == C3Protocol.ReplyOk;
        /// <summary>Código de error (negativo) que trae una respuesta <see cref="C3Protocol.ReplyError"/>.</summary>
        public int ErrorCode => !Ok && Data.Length > 0 ? (sbyte)Data[^1] : 0;
    }

    private async Task<C3Reply> ExchangeAsync(byte command, byte[] data, CancellationToken ct, bool expectSession = true)
    {
        if (_closed) throw new DriverException("La sesión con la controladora ya está cerrada.");

        byte[] payload = data;
        if (_session is ushort session)
        {
            payload = new byte[4 + data.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(payload, session);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), _request);
            data.CopyTo(payload, 4);
        }
        _request++;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CommandTimeout);
        try
        {
            await _stream.WriteAsync(C3Protocol.BuildFrame(command, payload), cts.Token);

            var header = new byte[5];
            await _stream.ReadExactlyAsync(header, cts.Token);
            if (header[0] != C3Protocol.Start)
                throw new DriverException("La controladora respondió algo que no es el protocolo C3.");
            int length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(3));
            var frame = new byte[5 + length + 3];
            header.CopyTo(frame, 0);
            await _stream.ReadExactlyAsync(frame.AsMemory(5), cts.Token);

            if (!C3Protocol.TryReadFrame(frame, out byte replyCommand, out var replyData))
                throw new DriverException("La respuesta de la controladora llegó dañada (CRC).");

            // Con sesión, la controladora repite sesión y petición delante de
            // los datos (cuando hay datos más allá de eso).
            var body = expectSession && _session is not null && replyData.Length > 2 ? replyData[4..] : replyData;
            return new C3Reply(replyCommand, body.ToArray());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _closed = true;
            throw new DriverException($"La controladora en {_host} dejó de responder.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException)
        {
            _closed = true;
            throw new DriverException($"Se cortó la conexión con la controladora en {_host}.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_closed && _session is not null)
        {
            // Cerrar la sesión con su comando libera el cupo de conexiones de la
            // controladora en el acto, sin esperar a que lo note por inactividad.
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ExchangeAsync(C3Protocol.CmdDisconnect, [], cts.Token);
            }
            catch { /* ya no importa */ }
        }
        _closed = true;
        _client.Dispose();
    }
}
