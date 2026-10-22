using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TrueCentralVms.IsupHost.Engine;

/// <summary>Direcciones de este equipo (para anunciarlas a los equipos ISUP y para escuchar el servidor de imágenes tras un NAT).</summary>
public static class LocalNetwork
{
    /// <summary>
    /// IPv4 de los adaptadores activos (Ethernet, Wi-Fi, VPN…); las de adaptadores virtuales de VMware/Hyper-V van al final, o se
    /// omiten con <paramref name="includeVirtual"/> en false.
    /// </summary>
    public static List<string> Candidates(bool includeVirtual = true)
    {
        var list = new List<(string Ip, bool Virtual)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var text = nic.Name + " " + nic.Description;
            bool virt = text.Contains("VMware", StringComparison.OrdinalIgnoreCase) || text.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) || text.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase);
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = ua.Address.ToString();
                if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;   // sin DHCP
                list.Add((ip, virt));
            }
        }
        return list.Where(x => includeVirtual || !x.Virtual).OrderBy(x => x.Virtual).Select(x => x.Ip).Distinct().ToList();
    }

    /// <summary>true si la IP es de algún adaptador de este equipo (se puede escuchar en ella).</summary>
    public static bool IsLocalAddress(string ip) => ip is "0.0.0.0" or "127.0.0.1" || Candidates().Contains(ip);

    /// <summary>IP local por la que sale (y entra, tras el NAT del router) el tráfico de internet.</summary>
    public static string InternetFacingIp() => RouteSourceIp("8.8.8.8") ?? Candidates().FirstOrDefault() ?? "127.0.0.1";

    /// <summary>
    /// IP de este equipo en la misma red (subred) que <paramref name="remote"/>, o null si no comparte red con ninguna interfaz
    /// activa. Las direcciones sin DHCP (169.254.x) no cuentan.
    /// </summary>
    public static string? SameSubnetAddress(IPAddress remote)
    {
        if (remote.AddressFamily != AddressFamily.InterNetwork || remote.ToString().StartsWith("169.254.", StringComparison.Ordinal)) return null;
        uint target = ToUInt(remote);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.PrefixLength is <= 0 or > 32) continue;
                if (ua.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)) continue;
                uint mask = ua.PrefixLength == 32 ? uint.MaxValue : ~(uint.MaxValue >> ua.PrefixLength);
                if ((ToUInt(ua.Address) & mask) == (target & mask)) return ua.Address.ToString();
            }
        }
        return null;

        static uint ToUInt(IPAddress ip) { var b = ip.GetAddressBytes(); return (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3]; }
    }

    /// <summary>La IP local que el sistema usaría para llegar a <paramref name="remoteIp"/> (sin enviar nada).</summary>
    public static string? RouteSourceIp(string remoteIp)
    {
        try
        {
            if (!IPAddress.TryParse(remoteIp, out var remote)) return null;
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(remote, 9);   // UDP: solo fija la ruta, no genera tráfico
            return (s.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (SocketException) { return null; }
    }

    /// <summary>Rango privado o CGNAT (100.64/10): la IP que informa un equipo detrás de NAT.</summary>
    public static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127));
    }
}
