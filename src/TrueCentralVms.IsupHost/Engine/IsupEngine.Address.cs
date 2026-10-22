using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;

namespace TrueCentralVms.IsupHost.Engine;

// ====================== dirección que se anuncia a cada equipo ======================
// Al registrarse, el equipo pregunta a qué servidor registrarse (DAS, ISUP 5.0) y recibe a dónde mandar sus alarmas; después, a cada
// video, reproducción o voz que se le pide, a dónde conectarse. Un servidor con dos redes (cable y Wi-Fi, una red de cámaras, una VPN)
// tiene una IP en cada una, y no todas se alcanzan desde todas partes: en producción, desde 192.168.10.x se llega a 192.168.1.78 por el
// router, pero desde 192.168.1.x no se llega a 192.168.10.39. Anunciar una sola IP para todos (la de salida a internet) dejó paneles
// sin poder registrarse. La regla:
//   1. Un equipo que está en una de las redes de este servidor (se conectó desde su propia IP, sin NAT de por medio) recibe la IP del
//      servidor en SU MISMA red, aunque haya una dirección configurada: es la ruta directa.
//   2. Si no, la dirección configurada (Isup:PublicAddress, o la del acceso desde internet con equipos ISUP), la misma para todos.
//   3. Sin dirección configurada, la IP de este servidor por la que llegó, o la de la interfaz por la que se le responde.

public sealed partial class IsupEngine
{
    /// <summary>Dirección fija para los equipos de fuera de las redes del servidor (Isup:PublicAddress o la del acceso desde internet).</summary>
    string _configuredAddress = "";

    /// <summary>ID ISUP → dirección que se le anunció en su último registro.</summary>
    readonly ConcurrentDictionary<string, string> _announced = new(StringComparer.Ordinal);

    bool PerDeviceAddress => _configuredAddress.Length == 0;

    /// <summary>La dirección con que el equipo llega a este servidor: la que se le anunció, o la general si todavía no se le anunció.</summary>
    string AddressFor(string deviceId) => _announced.TryGetValue(deviceId, out var a) ? a : _publicIp;

    string? AnnouncedTo(string deviceId) =>
        _announced.TryGetValue(deviceId, out var a) ? a : PerDeviceAddress ? null : _configuredAddress;

    /// <summary>
    /// Elige y recuerda la dirección que se le anuncia a un equipo que se está registrando. <paramref name="deviceIp"/> es la que el
    /// equipo informa en su registro: la suya, que detrás de un NAT es una privada distinta de la que ve el servidor.
    /// </summary>
    string Announce(string deviceId, string deviceIp)
    {
        string address = ChooseAddress(deviceIp, out string reason);
        if (deviceId.Length > 0 && (!_announced.TryGetValue(deviceId, out var previous) || previous != address))
        {
            _announced[deviceId] = address;
            if (address != _publicIp || previous is not null)
                Emit($"Equipo '{deviceId}'{(deviceIp.Length > 0 ? $" (IP {deviceIp})" : "")}: se le anuncia {address}, {reason}.");
        }
        return address;
    }

    string ChooseAddress(string deviceIp, out string reason)
    {
        IPAddress? device = IPAddress.TryParse(deviceIp, out var parsed) ? Plain(parsed) : null;
        if (device is not null && (IPAddress.IsLoopback(device) || device.Equals(IPAddress.Any))) device = null;

        // Se conectó desde su propia IP (sin NAT): se sabe dónde está y que llega directo.
        string? dialed = device is null ? null : DialedAddress(device);
        if (dialed is not null)
        {
            if (LocalNetwork.SameSubnetAddress(device!) is { } segment) { reason = "la IP de este servidor en su misma red"; return segment; }
            if (PerDeviceAddress) { reason = "la IP de este servidor por la que llegó"; return dialed; }
        }
        if (!PerDeviceAddress) { reason = "la dirección configurada"; return _configuredAddress; }
        if (device is not null && LocalNetwork.RouteSourceIp(device.ToString()) is { } routed && routed != "0.0.0.0" && !routed.StartsWith("127.", StringComparison.Ordinal))
        {
            reason = "la IP de este servidor por la que se le responde";
            return routed;
        }
        reason = "la IP de salida a internet de este servidor";
        return LocalNetwork.InternetFacingIp();
    }

    /// <summary>
    /// IP de este servidor a la que el equipo abrió su conexión de registro (directa o por el reenvío del 7661) DESDE SU PROPIA IP. null si
    /// no hay ninguna: registro por UDP, o un equipo detrás de NAT, que llega con la IP de su router y no con la que informa.
    /// </summary>
    string? DialedAddress(IPAddress device)
    {
        try
        {
            var ports = _options.RegistrationPorts;
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
                .Where(c => c.State == TcpState.Established && ports.Contains(c.LocalEndPoint.Port) && Plain(c.RemoteEndPoint.Address).Equals(device))
                .Select(c => Plain(c.LocalEndPoint.Address))
                .FirstOrDefault(a => !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any))?.ToString();
        }
        catch (NetworkInformationException) { return null; }
    }

    static IPAddress Plain(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}
