using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.ZkTeco;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Equipo ZKTeco que respondió el sondeo de la red local. <c>DriverKey</c>
/// distingue los terminales autónomos (<c>zkteco-tcp</c>) de las controladoras
/// inBio/C3 (<c>zkteco-c3</c>), que hablan otro protocolo.
/// </summary>
public sealed record ZkDiscoveredDto(string Ip, int Port, string Model, string Serial, string Mac,
    string DriverKey = "zkteco-tcp", string SubnetMask = "", string Gateway = "", string Firmware = "");

/// <summary>
/// Descubrimiento de equipos ZKTeco. El protocolo del fabricante no tiene un
/// anuncio como SADP o DHDiscover: lo que hacen sus herramientas es mandar el
/// mismo mensaje de conexión del SDK por <b>broadcast UDP al puerto 4370</b> y
/// escuchar quién contesta. Eso da la dirección de cada equipo, no su modelo.
///
/// Para poder mostrar modelo y número de serie —que es lo que hace útil la
/// tabla— a cada equipo que contestó se le abre una sesión TCP corta con la
/// clave de comunicación de fábrica (0) y se le preguntan sus datos. Un equipo
/// con clave propia igual aparece en la lista, pero sin identificación: se
/// agrega escribiendo su clave en el formulario.
///
/// Las controladoras inBio y C3 no contestan ese saludo: tienen su propia
/// búsqueda (<see cref="C3Protocol.BuildDiscovery"/>, UDP 65535) y en la
/// respuesta ya vienen modelo, serie, MAC, máscara y gateway, así que no se les
/// abre sesión. Como los terminales, contestan por unicast vía su gateway: una
/// controladora en otra subred sin ruta hacia el servidor no aparece.
/// </summary>
public static class ZkDiscovery
{
    private static readonly TimeSpan IdentifyTimeout = TimeSpan.FromSeconds(4);
    /// <summary>Equipos que se identifican a la vez (cada uno es una sesión TCP del SDK).</summary>
    private const int IdentifyParallelism = 4;

    /// <param name="skipIdentify">
    /// Direcciones que NO se identifican: las de equipos ya administrados. El
    /// equipo acepta una sola conexión del SDK a la vez, y el servicio del
    /// módulo ya los está sondeando; abrirles otra sesión desde aquí les haría
    /// rechazar una de las dos.
    /// </param>
    public static async Task<List<ZkDiscoveredDto>> ScanAsync(
        TimeSpan window, ILogger? logger = null, IPAddress? directedHost = null,
        IReadOnlySet<string>? skipIdentify = null, CancellationToken ct = default)
    {
        var c3Task = ProbeC3Async(window, directedHost, ct);
        var responders = await ProbeAsync(window, directedHost, ct);
        var controllers = await c3Task;
        if (responders.Count == 0 && controllers.Count == 0)
        {
            logger?.LogInformation("ZKTeco: ningún equipo respondió el sondeo en {Window}s.", window.TotalSeconds);
            return [];
        }
        // Una controladora no contesta el saludo de terminal, pero si algún
        // firmware lo hiciera se queda la identificación de la búsqueda C3.
        responders.RemoveAll(ip => controllers.Any(c => c.Ip == ip));

        using var limiter = new SemaphoreSlim(IdentifyParallelism);
        var devices = await Task.WhenAll(responders.Select(async ip =>
        {
            if (skipIdentify?.Contains(ip) == true)
                return new ZkDiscoveredDto(ip, ZkProtocol.DefaultPort, "", "", "");
            await limiter.WaitAsync(ct);
            try { return await IdentifyAsync(ip, ct); }
            finally { limiter.Release(); }
        }));

        logger?.LogInformation("ZKTeco: {Count} terminal(es) y {Controllers} controladora(s) en {Window}s.",
            devices.Length, controllers.Count, window.TotalSeconds);
        return devices.Concat(controllers).OrderBy(d => d.Ip, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Direcciones que contestaron el saludo del SDK por UDP.</summary>
    private static async Task<List<string>> ProbeAsync(TimeSpan window, IPAddress? directedHost, CancellationToken ct)
    {
        // Mensaje de conexión pelado (por UDP no lleva la cabecera de TCP).
        byte[] probe = ZkProtocol.BuildPacket(ZkProtocol.CmdConnect, sessionId: 0, replyId: 0, data: []);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.EnableBroadcast = true;
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));   // las respuestas llegan al puerto de origen

        var broadcasts = LocalBroadcasts();

        void SendProbes()
        {
            foreach (var address in broadcasts)
                try { socket.SendTo(probe, new IPEndPoint(address, ZkProtocol.DefaultPort)); } catch { }
            try { socket.SendTo(probe, new IPEndPoint(IPAddress.Broadcast, ZkProtocol.DefaultPort)); } catch { }
            if (directedHost is not null)
                try { socket.SendTo(probe, new IPEndPoint(directedHost, ZkProtocol.DefaultPort)); } catch { }
        }

        SendProbes();

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[4096];
        var deadline = DateTime.UtcNow + window;
        bool resent = false;

        while (!ct.IsCancellationRequested)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            if (!resent && remaining < window / 2)
            {
                SendProbes();   // segundo sondeo para equipos lentos
                resent = true;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(remaining);
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(
                    buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeoutCts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }   // datagrama corrupto/ICMP: seguir escuchando

            // Respuesta válida = un mensaje del protocolo contestando el saludo
            // (con clave de comunicación contesta "no autorizado", que también
            // confirma que hay un equipo ZKTeco ahí).
            if (!ZkProtocol.TryReadHeader(buffer.AsSpan(0, result.ReceivedBytes), out ushort command, out _, out _))
                continue;
            if (command is not (ZkProtocol.CmdAckOk or ZkProtocol.CmdAckUnauth)) continue;

            string ip = ((IPEndPoint)result.RemoteEndPoint).Address.ToString();
            if (seen.Add(ip)) found.Add(ip);
        }
        return found;
    }

    /// <summary>
    /// Controladoras inBio/C3 que contestaron su búsqueda. La respuesta sale
    /// del puerto 65535 de la controladora hacia el puerto de origen del sondeo.
    /// </summary>
    private static async Task<List<ZkDiscoveredDto>> ProbeC3Async(TimeSpan window, IPAddress? directedHost, CancellationToken ct)
    {
        byte[] probe = C3Protocol.BuildDiscovery();

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.EnableBroadcast = true;
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        var broadcasts = LocalBroadcasts();
        void SendProbes()
        {
            foreach (var address in broadcasts)
                try { socket.SendTo(probe, new IPEndPoint(address, C3Protocol.DiscoveryPort)); } catch { }
            try { socket.SendTo(probe, new IPEndPoint(IPAddress.Broadcast, C3Protocol.DiscoveryPort)); } catch { }
            if (directedHost is not null)
                try { socket.SendTo(probe, new IPEndPoint(directedHost, C3Protocol.DiscoveryPort)); } catch { }
        }

        SendProbes();

        var found = new Dictionary<string, ZkDiscoveredDto>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[2048];
        var deadline = DateTime.UtcNow + window;
        bool resent = false;

        while (!ct.IsCancellationRequested)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            if (!resent && remaining < window / 2)
            {
                SendProbes();
                resent = true;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(remaining);
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(
                    buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeoutCts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }

            // El propio sondeo vuelve por broadcast: no es respuesta (no es ReplyOk ni trae "MAC=").
            if (!C3Protocol.TryReadFrame(buffer.AsSpan(0, result.ReceivedBytes), out byte command, out var data) ||
                command != C3Protocol.ReplyOk)
                continue;
            var values = C3Protocol.ParseKeyValues(data);
            if (!values.TryGetValue("MAC", out var mac) || mac.Length == 0) continue;

            // La IP es la que la controladora dice tener: si está en otra subred
            // es la que hay que mostrar (y la que hay que cambiar).
            string ip = values.GetValueOrDefault("IP") is { Length: > 0 } announced
                ? announced : ((IPEndPoint)result.RemoteEndPoint).Address.ToString();
            found[mac] = new ZkDiscoveredDto(ip, C3Protocol.DefaultPort,
                values.GetValueOrDefault("Device") ?? "", values.GetValueOrDefault("SN") ?? "",
                mac.Replace(':', '-').ToLowerInvariant(), "zkteco-c3",
                values.GetValueOrDefault("NetMask") ?? "", values.GetValueOrDefault("GATEIPAddress") ?? "",
                values.GetValueOrDefault("Ver") ?? "");
        }
        return found.Values.ToList();
    }

    /// <summary>
    /// Cambia IP, máscara y puerta de enlace de una controladora C3 por su MAC
    /// (lo que hace ZKAccess con "Modificar dirección IP"): una trama
    /// <see cref="C3Protocol.CmdModifyIp"/> por broadcast desde cada interfaz,
    /// así llega aunque la controladora esté en otra subred del mismo cable. No
    /// lleva clave. Validado contra una inBio480. Devuelve true si alguna
    /// respondió que sí; una en otra subred aplica el cambio pero su respuesta
    /// puede no volver (sale por su puerta de enlace), así que el llamador
    /// confirma igual con una búsqueda.
    /// </summary>
    public static async Task<bool> ChangeNetworkAsync(string mac, string ip, string mask, string gateway, CancellationToken ct)
    {
        string normalized = string.Join(':', mac.Replace("-", "").Replace(":", "").ToUpperInvariant().Chunk(2).Select(c => new string(c)));
        string text = $"MAC={normalized},IPAddress={ip},NetMask={mask}" + (gateway.Length > 0 ? $",GATEIPAddress={gateway}" : "");
        byte[] frame = C3Protocol.BuildFrame(C3Protocol.CmdModifyIp, System.Text.Encoding.ASCII.GetBytes(text));

        var sockets = new List<Socket>();
        try
        {
            // Una sesión por interfaz: el broadcast general sale por una sola.
            foreach (var local in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                     n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                         .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                         .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.IPv4Mask is not null))
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
                try
                {
                    socket.Bind(new IPEndPoint(local.Address, 0));
                    socket.SendTo(frame, new IPEndPoint(IPAddress.Broadcast, C3Protocol.DiscoveryPort));
                    socket.SendTo(frame, new IPEndPoint(BroadcastOf(local.Address, local.IPv4Mask!), C3Protocol.DiscoveryPort));
                    sockets.Add(socket);
                }
                catch (SocketException) { socket.Dispose(); }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var replies = sockets.Select(async socket =>
            {
                var buffer = new byte[512];
                while (!timeout.IsCancellationRequested)
                {
                    try
                    {
                        var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None,
                            new IPEndPoint(IPAddress.Any, 0), timeout.Token);
                        // El propio broadcast vuelve: se distingue porque no es una respuesta.
                        if (C3Protocol.TryReadFrame(buffer.AsSpan(0, result.ReceivedBytes), out byte command, out _) &&
                            command == C3Protocol.ReplyOk)
                            return true;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (SocketException) { }
                }
                return false;
            }).ToList();
            return (await Task.WhenAll(replies)).Any(ok => ok);
        }
        finally
        {
            foreach (var socket in sockets) socket.Dispose();
        }
    }

    /// <summary>Broadcast de cada subred IPv4 de las interfaces activas.</summary>
    private static List<IPAddress> LocalBroadcasts() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.IPv4Mask is not null)
        .Select(a => BroadcastOf(a.Address, a.IPv4Mask!))
        .Distinct()
        .ToList();

    /// <summary>Modelo y serie del equipo, si acepta la clave de comunicación de fábrica.</summary>
    private static async Task<ZkDiscoveredDto> IdentifyAsync(string ip, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(IdentifyTimeout);
            await using var connection = await ZkConnection.OpenAsync(ip, ZkProtocol.DefaultPort, commKey: 0, cts.Token);
            string model = await connection.GetParameterAsync("~DeviceName", cts.Token) ?? "";
            string serial = await connection.GetParameterAsync("~SerialNumber", cts.Token) ?? "";
            string mac = await connection.GetParameterAsync("MAC", cts.Token) ?? "";
            return new ZkDiscoveredDto(ip, ZkProtocol.DefaultPort, model, serial, mac);
        }
        catch (DriverException)
        {
            // Con clave propia el equipo rechaza la sesión: se lista igual, sin
            // identificación, para que el administrador lo agregue a mano.
            return new ZkDiscoveredDto(ip, ZkProtocol.DefaultPort, "", "", "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ZkDiscoveredDto(ip, ZkProtocol.DefaultPort, "", "", "");
        }
    }

    private static IPAddress BroadcastOf(IPAddress address, IPAddress mask)
    {
        byte[] ip = address.GetAddressBytes(), m = mask.GetAddressBytes();
        var broadcast = new byte[4];
        for (int i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~m[i]);
        return new IPAddress(broadcast);
    }
}
