namespace TrueCentralVms.Server.Services.Https;

/// <summary>
/// Puertos por los que atiende el servidor (sección "Web" de la configuración):
/// <list type="bullet">
/// <item><see cref="HttpsPort"/>: TODO lo de las personas y los programas propios
/// (panel, API, hubs, clientes, pantallas, agentes).</item>
/// <item><see cref="HttpPort"/>: solo lo que piden equipos que no saben de TLS
/// (paneles de alarma que empujan eventos, terminales ISUP que bajan fotos); el
/// resto se redirige a HTTPS si es una página y se rechaza si no. 0 = cerrado.</item>
/// <item><see cref="InternalPort"/>: HTTP solo en 127.0.0.1/::1 para el propio
/// equipo (autorización de MediaMTX y la sonda de salud del instalador y del
/// Watchdog). No se abre en el firewall.</item>
/// </list>
/// </summary>
public sealed record WebPorts(int HttpsPort, int HttpPort, int InternalPort)
{
    public static WebPorts From(IConfiguration config) => new(
        config.GetValue("Web:HttpsPort", 443),
        config.GetValue("Web:HttpPort", 80),
        config.GetValue("Web:InternalPort", 5099));

    /// <summary>Rutas que se atienden por <see cref="HttpPort"/> sin cifrar (equipos sin TLS).</summary>
    public static readonly string[] PlainHttpPaths = ["/api/alarms/push", "/api/isup/files"];

    /// <summary>Rutas del puerto interno (loopback).</summary>
    public static readonly string[] InternalPaths = ["/api/streaming/auth", "/api/health"];

    /// <summary>URL https del servidor para <paramref name="host"/> (sin ":443").</summary>
    public string HttpsUrl(string host) =>
        HttpsPort == 443 ? $"https://{HostPart(host)}" : $"https://{HostPart(host)}:{HttpsPort}";

    /// <summary>URL http (equipos sin TLS) para <paramref name="host"/>; null si el puerto está cerrado.</summary>
    public string? HttpUrl(string host) =>
        HttpPort <= 0 ? null : HttpPort == 80 ? $"http://{HostPart(host)}" : $"http://{HostPart(host)}:{HttpPort}";

    /// <summary>Sonda local por el puerto interno.</summary>
    public string InternalUrl => $"http://127.0.0.1:{InternalPort}";

    // Una IPv6 literal va entre corchetes en una URL.
    private static string HostPart(string host) =>
        host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
}
