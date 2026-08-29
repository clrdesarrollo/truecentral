using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace TrueCentralVms.Server.Services;

/// <summary>Transmisor de video ONVIF anunciado por WS-Discovery.</summary>
public sealed record OnvifDiscoveredDto(string Ip, int HttpPort, string Name, string Hardware);

/// <summary>
/// Descubrimiento estándar ONVIF por WS-Discovery: Probe SOAP por multicast
/// 239.255.255.250:3702 pidiendo NetworkVideoTransmitter; cualquier marca
/// ONVIF responde con ProbeMatch (XAddrs con la URL del servicio, scopes con
/// nombre y hardware). Complementa a los protocolos de fábrica: encuentra lo
/// que SADP/DHDiscover no cubren. Es multicast puro — la mayoría de los
/// equipos ignora sondeos unicast — así que solo ve el segmento local.
/// </summary>
public static class WsDiscovery
{
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("239.255.255.250");
    private const int DiscoveryPort = 3702;

    public static async Task<List<OnvifDiscoveredDto>> ScanAsync(
        TimeSpan window, ILogger? logger = null, CancellationToken ct = default)
    {
        byte[] probe = BuildProbe();

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        // Efímero: las respuestas llegan unicast al puerto de origen, y el
        // 3702 suele estar tomado por el propio Windows (Function Discovery).
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
        }

        SendProbes();

        var found = new Dictionary<string, OnvifDiscoveredDto>(StringComparer.OrdinalIgnoreCase);
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
                continue;
            }

            foreach (var device in ParseProbeMatches(buffer.AsSpan(0, result.ReceivedBytes)))
                found[device.Ip] = device;
        }

        logger?.LogInformation("WS-Discovery: {Count} equipo(s) ONVIF en {Window}s", found.Count, window.TotalSeconds);
        return found.Values.OrderBy(d => d.Ip, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static byte[] BuildProbe()
    {
        string uuid = Guid.NewGuid().ToString();
        return Encoding.UTF8.GetBytes($"""
            <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope" xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery" xmlns:dn="http://www.onvif.org/ver10/network/wsdl"><e:Header><w:MessageID>uuid:{uuid}</w:MessageID><w:To e:mustUnderstand="true">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To><w:Action e:mustUnderstand="true">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action></e:Header><e:Body><d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe></e:Body></e:Envelope>
            """);
    }

    private static List<OnvifDiscoveredDto> ParseProbeMatches(ReadOnlySpan<byte> datagram)
    {
        var devices = new List<OnvifDiscoveredDto>();
        try
        {
            var root = XDocument.Parse(Encoding.UTF8.GetString(datagram)).Root;
            if (root is null) return devices;

            // Los prefijos varían por marca: navegar por nombre local.
            foreach (var match in root.Descendants().Where(e => e.Name.LocalName == "ProbeMatch"))
            {
                string xaddrs = match.Descendants().FirstOrDefault(e => e.Name.LocalName == "XAddrs")?.Value ?? "";
                string scopes = match.Descendants().FirstOrDefault(e => e.Name.LocalName == "Scopes")?.Value ?? "";

                // XAddrs: URLs separadas por espacio; tomar la primera con host IPv4.
                (string Ip, int Port)? endpoint = null;
                foreach (string part in xaddrs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Uri.TryCreate(part, UriKind.Absolute, out var uri) &&
                        IPAddress.TryParse(uri.Host, out var ip) &&
                        ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        endpoint = (uri.Host, uri.IsDefaultPort ? 80 : uri.Port);
                        break;
                    }
                }
                if (endpoint is null) continue;

                string ScopeValue(string prefix)
                {
                    foreach (string scope in scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        if (scope.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            return Uri.UnescapeDataString(scope[prefix.Length..]).Trim();
                    return "";
                }

                devices.Add(new OnvifDiscoveredDto(
                    Ip: endpoint.Value.Ip,
                    HttpPort: endpoint.Value.Port,
                    Name: ScopeValue("onvif://www.onvif.org/name/"),
                    Hardware: ScopeValue("onvif://www.onvif.org/hardware/")));
            }
        }
        catch
        {
            // datagrama que no es un ProbeMatch ONVIF
        }
        return devices;
    }
}
