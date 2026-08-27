using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace TrueCentralVms.Server.Services;

/// <summary>Equipo Hikvision anunciado por SADP en el segmento de red local.</summary>
public sealed record DiscoveredDeviceDto(
    string Ip, int CommandPort, int HttpPort, string Model, string Serial, string Mac,
    bool Activated, bool Dhcp, string? SubnetMask, string? Gateway);

/// <summary>
/// Descubrimiento de equipos Hikvision por SADP: sondeo UDP multicast/broadcast
/// al puerto 37020 — el mismo mecanismo del "Online Device" de HikCentral y de
/// la herramienta SADP oficial. Los equipos responden con un XML ProbeMatch
/// (IP, modelo, serie, puertos, estado de activación).
///
/// Limitación del protocolo: solo encuentra equipos en el MISMO segmento de
/// red (L2) que este servidor; el sondeo no cruza routers.
/// </summary>
public static class SadpDiscovery
{
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("239.255.255.250");
    private const int SadpPort = 37020;

    public static async Task<List<DiscoveredDeviceDto>> ScanAsync(
        TimeSpan window, ILogger? logger = null, CancellationToken ct = default)
    {
        string uuid = Guid.NewGuid().ToString().ToUpperInvariant();
        byte[] probe = Encoding.UTF8.GetBytes(
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?><Probe><Uuid>{uuid}</Uuid><Types>inquiry</Types></Probe>");

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.EnableBroadcast = true;
        try
        {
            // Los equipos responden al puerto de origen; algunos firmwares solo
            // contestan si el sondeo sale del 37020 (como la herramienta SADP).
            socket.Bind(new IPEndPoint(IPAddress.Any, SadpPort));
        }
        catch (SocketException)
        {
            // 37020 ocupado (SADP/iVMS abierto): puerto efímero; las respuestas
            // unicast igual llegan al puerto de origen del sondeo.
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        }

        var localAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address)
            .ToList();

        foreach (var address in localAddresses)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(MulticastGroup, address));
            }
            catch { /* interfaz sin soporte multicast */ }
        }

        void SendProbes()
        {
            foreach (var address in localAddresses)
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                        address.GetAddressBytes());
                    socket.SendTo(probe, new IPEndPoint(MulticastGroup, SadpPort));
                }
                catch { }
            }
            try { socket.SendTo(probe, new IPEndPoint(IPAddress.Broadcast, SadpPort)); } catch { }
        }

        SendProbes();

        var found = new Dictionary<string, DiscoveredDeviceDto>(StringComparer.OrdinalIgnoreCase);
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

            var device = TryParseProbeMatch(buffer.AsSpan(0, result.ReceivedBytes));
            if (device is not null)
                found[device.Mac.Length > 0 ? device.Mac : device.Ip] = device;
        }

        logger?.LogInformation("SADP: {Count} equipo(s) en {Window}s", found.Count, window.TotalSeconds);
        return found.Values.OrderBy(d => d.Ip, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static DiscoveredDeviceDto? TryParseProbeMatch(ReadOnlySpan<byte> datagram)
    {
        try
        {
            string xml = Encoding.UTF8.GetString(datagram);
            var root = XDocument.Parse(xml).Root;
            if (root is null || !string.Equals(root.Name.LocalName, "ProbeMatch", StringComparison.OrdinalIgnoreCase))
                return null; // ignora nuestros propios Probe y otros anuncios

            string Get(string name) => root.Elements()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
                ?.Value.Trim() ?? "";

            string ip = Get("IPv4Address");
            if (ip.Length == 0) return null;

            string model = Get("DeviceDescription");
            string serial = Get("DeviceSN");
            if (model.Length == 0 && serial.Length > 0) model = serial;

            return new DiscoveredDeviceDto(
                Ip: ip,
                CommandPort: int.TryParse(Get("CommandPort"), out int cmd) && cmd > 0 ? cmd : 8000,
                HttpPort: int.TryParse(Get("HttpPort"), out int http) && http > 0 ? http : 80,
                Model: model,
                Serial: serial,
                Mac: Get("MAC"),
                // Firmwares viejos no informan Activated: se asume activado.
                Activated: !string.Equals(Get("Activated"), "false", StringComparison.OrdinalIgnoreCase),
                Dhcp: string.Equals(Get("DHCP"), "true", StringComparison.OrdinalIgnoreCase),
                SubnetMask: NullIfEmpty(Get("IPv4SubnetMask")),
                Gateway: NullIfEmpty(Get("IPv4Gateway")));
        }
        catch
        {
            return null; // datagrama que no es XML SADP
        }
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
