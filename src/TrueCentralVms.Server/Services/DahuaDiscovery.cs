using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TrueCentralVms.Server.Services;

/// <summary>Equipo Dahua anunciado por DHDiscover.</summary>
public sealed record DahuaDiscoveredDto(
    string Ip, int SdkPort, int HttpPort, string Model, string DeviceClass,
    string Serial, string Mac, string Version);

/// <summary>
/// Descubrimiento de equipos Dahua por su protocolo privado "DHDiscover" (el
/// mismo del ConfigTool): datagrama DHIP —cabecera binaria de 32 bytes + JSON—
/// al puerto 37810 por multicast 239.255.255.251 y broadcast. Los equipos
/// responden unicast al puerto de origen con client.notifyDevInfo (clase,
/// modelo, IP, puertos, serie, MAC, versión). Validado contra una
/// IPC-HDW2449T real; a diferencia de SADP, también responde a sondeos
/// unicast dirigidos, útil para equipos al otro lado de un router/VPN.
/// Equipos muy antiguos usan un protocolo binario anterior que no se
/// implementa aquí.
/// </summary>
public static class DahuaDiscovery
{
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("239.255.255.251");
    private const int DiscoveryPort = 37810;

    public static async Task<List<DahuaDiscoveredDto>> ScanAsync(
        TimeSpan window, ILogger? logger = null, IPAddress? directedHost = null, CancellationToken ct = default)
    {
        byte[] probe = BuildProbe();

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.EnableBroadcast = true;
        // Las respuestas llegan unicast al puerto de origen: basta un efímero
        // (y así no se pelea el 37810 con un ConfigTool abierto).
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        var localAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address)
            .ToList();

        void SendProbes()
        {
            foreach (var address in localAddresses)
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                        address.GetAddressBytes());
                    socket.SendTo(probe, new IPEndPoint(MulticastGroup, DiscoveryPort));
                }
                catch { }
            }
            try { socket.SendTo(probe, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort)); } catch { }
            if (directedHost is not null)
                try { socket.SendTo(probe, new IPEndPoint(directedHost, DiscoveryPort)); } catch { }
        }

        SendProbes();

        var found = new Dictionary<string, DahuaDiscoveredDto>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[64 * 1024];
        var deadline = DateTime.UtcNow + window;
        bool resent = false;

        while (!ct.IsCancellationRequested)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            if (!resent && remaining < window / 2)
            {
                SendProbes(); // segundo sondeo para equipos lentos
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
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue; // datagrama corrupto/ICMP; seguir escuchando
            }

            var device = TryParseNotify(buffer.AsSpan(0, result.ReceivedBytes));
            if (device is not null)
                found[device.Mac.Length > 0 ? device.Mac : device.Ip] = device;
        }

        logger?.LogInformation("DHDiscover: {Count} equipo(s) Dahua en {Window}s", found.Count, window.TotalSeconds);
        return found.Values.OrderBy(d => d.Ip, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Cabecera DHIP: 0x20, "DHIP" en el offset 4 y el largo del JSON en los offsets 16 y 24 (LE).</summary>
    private static byte[] BuildProbe()
    {
        byte[] json = Encoding.UTF8.GetBytes("""{"method":"DHDiscover.search","params":{"mac":"","uni":1}}""");
        byte[] packet = new byte[32 + json.Length];
        packet[0] = 0x20;
        Encoding.ASCII.GetBytes("DHIP").CopyTo(packet, 4);
        BitConverter.TryWriteBytes(packet.AsSpan(16, 4), (uint)json.Length);
        BitConverter.TryWriteBytes(packet.AsSpan(24, 4), (uint)json.Length);
        json.CopyTo(packet, 32);
        return packet;
    }

    private static DahuaDiscoveredDto? TryParseNotify(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length <= 32) return null; // solo cabecera (o eco de nuestro sondeo)
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(datagram[32..]).TrimEnd('\0', '\n', ' '));
            var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var method) ||
                !string.Equals(method.GetString(), "client.notifyDevInfo", StringComparison.OrdinalIgnoreCase))
                return null;

            var info = root.GetProperty("params").GetProperty("deviceInfo");
            string? ip = info.TryGetProperty("IPv4Address", out var v4) &&
                         v4.TryGetProperty("IPAddress", out var addr) ? addr.GetString() : null;
            if (string.IsNullOrWhiteSpace(ip)) return null;

            string GetString(string name) =>
                info.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString()!.Trim() : "";
            int GetInt(string name, int fallback) =>
                info.TryGetProperty(name, out var e) && e.TryGetInt32(out int value) && value > 0
                    ? value : fallback;

            string model = GetString("DeviceType");
            if (model.Length == 0) model = GetString("MachineName");

            return new DahuaDiscoveredDto(
                Ip: ip,
                SdkPort: GetInt("Port", 37777),
                HttpPort: GetInt("HttpPort", 80),
                Model: model,
                DeviceClass: GetString("DeviceClass"),
                Serial: GetString("SerialNo"),
                // La MAC viaja en la raíz de la respuesta, no en deviceInfo.
                Mac: root.TryGetProperty("mac", out var mac) ? mac.GetString() ?? "" : "",
                Version: GetString("Version"));
        }
        catch
        {
            return null; // datagrama que no es DHIP/JSON
        }
    }
}
