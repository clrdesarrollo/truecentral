using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace TrueCentralVms.Server.Services.Workflows;

/// <summary>
/// A qué direcciones pueden conectarse las acciones de las automatizaciones que
/// apuntan a un destino escrito a mano (HTTP, FTP, parlante por URL, correo).
///
/// Las de la red local SÍ (un módulo de relés, una central de monitoreo, un
/// parlante en la caseta son el caso de uso). Las del PROPIO servidor no: desde
/// loopback se llega a servicios internos que confían en estar solos en la
/// máquina (API de control de MediaMTX, servicio ISUP, puerto interno, el
/// complemento de enrolamiento), y una automatización los convertiría en una
/// puerta (SSRF). Tampoco link-local (169.254.169.254 = metadatos de la nube,
/// con credenciales), multicast, broadcast ni 0.0.0.0.
///
/// La dirección se revisa DESPUÉS de resolver el nombre y en cada conexión
/// (también las de una redirección): un nombre que resuelve a 127.0.0.1, o que
/// cambia de IP entre la validación y la llamada (DNS rebinding), no pasa.
/// <c>Workflows:AllowLoopbackTargets</c> = true lo desactiva para loopback (solo
/// si un servicio legítimo del cliente corre en el mismo equipo).
/// </summary>
public static class OutboundAddressPolicy
{
    /// <summary>Workflows:AllowLoopbackTargets (se fija al arrancar).</summary>
    public static bool AllowLoopback { get; set; }

    /// <summary>Por qué no se permite conectarse a <paramref name="address"/>; null = permitida.</summary>
    public static string? Reason(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return AllowLoopback ? null : "es el propio servidor (loopback)";
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return "es una dirección no especificada";
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            if (b[0] == 0) return "es una dirección no especificada";
            if (b[0] == 169 && b[1] == 254) return "es link-local (incluye los metadatos de la nube, 169.254.169.254)";
            if (b[0] >= 224) return "es multicast o broadcast";
            return null;
        }
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return "es link-local";
        if (address.IsIPv6Multicast) return "es multicast";
        return null;
    }

    /// <summary>
    /// Resuelve <paramref name="host"/> y devuelve el motivo si alguna de sus direcciones no se permite (para FTP y
    /// correo, que conectan por su cuenta). null = se puede conectar.
    /// </summary>
    public static async Task<string?> CheckHostAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException)
        {
            return null; // no resuelve: la conexión fallará sola con su propio mensaje
        }
        foreach (var address in addresses)
            if (Reason(address) is { } reason)
                return $"{host} ({address}) {reason}: no se permite como destino de una automatización.";
        return null;
    }

    /// <summary>
    /// Manejador HTTP que revisa la dirección de CADA conexión (también las de las redirecciones) después de
    /// resolver el nombre, y se conecta exactamente a la IP revisada.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(bool acceptAnyCertificate, bool allowRedirect)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = allowRedirect,
            MaxAutomaticRedirections = 5,
            ConnectCallback = ConnectAsync,
        };
        if (acceptAnyCertificate)
            handler.SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true };
        return handler;
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        string host = context.DnsEndPoint.Host;
        IPAddress[] addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, ct);
        string? refused = null;
        foreach (var address in addresses)
        {
            if (Reason(address) is { } reason)
            {
                refused ??= $"{host} ({address}) {reason}: no se permite como destino de una automatización.";
                continue;
            }
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                if (address == addresses[^1]) throw;
            }
        }
        throw new HttpRequestException(refused ?? $"No se pudo resolver {host}.");
    }
}
