using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.AlarmScreen.Services;

/// <summary>
/// Busca el servidor por UDP broadcast. Funciona dentro de la subred; si la caseta y el
/// servidor están en VLANs distintas no hay respuesta y hay que escribir la URL a mano —
/// por eso la pantalla de configuración siempre ofrece esa opción.
/// </summary>
public static class DiscoveryClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Servidores que respondieron dentro del tiempo indicado, sin repetir.</summary>
    public static async Task<List<AlarmScreenDiscoveryResponse>> FindAsync(TimeSpan timeout, CancellationToken ct)
    {
        var found = new Dictionary<string, AlarmScreenDiscoveryResponse>();
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var probe = Encoding.UTF8.GetBytes(AlarmScreenProtocol.DiscoveryProbe);
        foreach (var target in BroadcastTargets())
        {
            try { await udp.SendAsync(probe, new IPEndPoint(target, AlarmScreenProtocol.DiscoveryPort), ct); }
            catch { /* interfaz sin ruta: se prueba la siguiente */ }
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                var received = await udp.ReceiveAsync(deadline.Token);
                try
                {
                    var response = JsonSerializer.Deserialize<AlarmScreenDiscoveryResponse>(received.Buffer, Json);
                    if (response is { Url.Length: > 0 })
                        found[response.ServerId.Length > 0 ? response.ServerId : response.Url] = response;
                }
                catch (JsonException) { /* respuesta ajena en el mismo puerto */ }
            }
        }
        catch (OperationCanceledException) { /* se agotó el tiempo: normal */ }

        return found.Values.ToList();
    }

    /// <summary>
    /// Broadcast global más el de cada interfaz. Windows suele ignorar 255.255.255.255 cuando
    /// hay varias tarjetas, así que conviene mandar también al broadcast de cada subred.
    /// </summary>
    private static IEnumerable<IPAddress> BroadcastTargets()
    {
        yield return IPAddress.Broadcast;

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (addr.IPv4Mask is null) continue;

                var ip = addr.Address.GetAddressBytes();
                var mask = addr.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (int i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(broadcast);
            }
        }
    }
}
