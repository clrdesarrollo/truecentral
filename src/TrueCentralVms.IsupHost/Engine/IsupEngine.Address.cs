using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;

namespace TrueCentralVms.IsupHost.Engine;

// ====================== dirección que se anuncia a cada equipo ======================
// Al registrarse, el equipo pregunta a qué servidor registrarse (DAS, ISUP 5.0) y recibe a dónde mandar sus alarmas; después, a cada
// video, reproducción o voz que se le pide, a dónde conectarse. Con Isup:PublicAddress (o Acceso desde Internet con equipos ISUP) se
// anuncia esa dirección a todos. Sin ella se anunciaba la IP de salida a internet, y en un servidor con dos redes (cable y Wi-Fi, una
// red de cámaras sin salida a internet, una VPN) esa puede ser una que el equipo no alcanza: preguntaba, se le contestaba esa IP y
// nunca terminaba de registrarse. Ahora a cada equipo se le anuncia la IP de este servidor por la que llegó.

public sealed partial class IsupEngine
{
    /// <summary>Dirección fija para todos (Isup:PublicAddress o la del acceso desde internet); vacía = una por equipo.</summary>
    string _configuredAddress = "";

    /// <summary>ID ISUP → dirección que se le anunció en su último registro.</summary>
    readonly ConcurrentDictionary<string, string> _announced = new(StringComparer.Ordinal);

    bool PerDeviceAddress => _configuredAddress.Length == 0;

    /// <summary>La dirección con que el equipo llega a este servidor: la que se le anunció, o la general si todavía no se le anunció.</summary>
    string AddressFor(string deviceId) =>
        !PerDeviceAddress ? _configuredAddress : _announced.TryGetValue(deviceId, out var a) ? a : _publicIp;

    string? AnnouncedTo(string deviceId) =>
        !PerDeviceAddress ? _configuredAddress : _announced.TryGetValue(deviceId, out var a) ? a : null;

    /// <summary>
    /// Elige y recuerda la dirección que se le anuncia a un equipo que se está registrando. <paramref name="deviceIp"/> es la que el
    /// equipo informa en su registro: la suya, que detrás de un NAT es una privada distinta de la que ve el servidor.
    /// </summary>
    string Announce(string deviceId, string deviceIp)
    {
        if (!PerDeviceAddress) return _configuredAddress;
        string address = LocalAddressTowards(deviceIp) ?? LocalNetwork.InternetFacingIp();
        if (deviceId.Length > 0 && (!_announced.TryGetValue(deviceId, out var previous) || previous != address))
        {
            _announced[deviceId] = address;
            if (address != _publicIp || previous is not null)
                Emit($"Equipo '{deviceId}'{(deviceIp.Length > 0 ? $" (IP {deviceIp})" : "")}: se le anuncia {address}, la IP de este servidor por la que llega.");
        }
        return address;
    }

    /// <summary>
    /// IP de este servidor por la que llega <paramref name="deviceIp"/>: la de la conexión que el equipo abrió a un puerto de registro
    /// (directa o por el reenvío del 7661), o si no hay ninguna (registro por UDP, o un equipo detrás de NAT, que llega con otra IP), la
    /// de la interfaz por la que el sistema le respondería. null si no se puede saber.
    /// </summary>
    string? LocalAddressTowards(string deviceIp)
    {
        if (!IPAddress.TryParse(deviceIp, out var device)) return null;
        device = Plain(device);
        if (IPAddress.IsLoopback(device) || device.Equals(IPAddress.Any)) return null;
        try
        {
            var ports = _options.RegistrationPorts;
            var dialed = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
                .Where(c => c.State == TcpState.Established && ports.Contains(c.LocalEndPoint.Port) && Plain(c.RemoteEndPoint.Address).Equals(device))
                .Select(c => Plain(c.LocalEndPoint.Address))
                .FirstOrDefault(a => !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any));
            if (dialed is not null) return dialed.ToString();
        }
        catch (NetworkInformationException) { }
        var routed = LocalNetwork.RouteSourceIp(device.ToString());
        return routed is null || routed == "0.0.0.0" || routed.StartsWith("127.", StringComparison.Ordinal) ? null : routed;
    }

    static IPAddress Plain(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}
