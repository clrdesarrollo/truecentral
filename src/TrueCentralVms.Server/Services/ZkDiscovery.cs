using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.ZkTeco;

namespace TrueCentralVms.Server.Services;

/// <summary>Equipo ZKTeco que respondió el sondeo de la red local.</summary>
public sealed record ZkDiscoveredDto(string Ip, int Port, string Model, string Serial, string Mac);

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
        var responders = await ProbeAsync(window, directedHost, ct);
        if (responders.Count == 0)
        {
            logger?.LogInformation("ZKTeco: ningún equipo respondió el sondeo en {Window}s.", window.TotalSeconds);
            return [];
        }

        using var limiter = new SemaphoreSlim(IdentifyParallelism);
        var devices = await Task.WhenAll(responders.Select(async ip =>
        {
            if (skipIdentify?.Contains(ip) == true)
                return new ZkDiscoveredDto(ip, ZkProtocol.DefaultPort, "", "", "");
            await limiter.WaitAsync(ct);
            try { return await IdentifyAsync(ip, ct); }
            finally { limiter.Release(); }
        }));

        logger?.LogInformation("ZKTeco: {Count} equipo(s) en {Window}s.", devices.Length, window.TotalSeconds);
        return devices.OrderBy(d => d.Ip, StringComparer.OrdinalIgnoreCase).ToList();
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

        var broadcasts = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.IPv4Mask is not null)
            .Select(a => BroadcastOf(a.Address, a.IPv4Mask!))
            .Distinct()
            .ToList();

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
