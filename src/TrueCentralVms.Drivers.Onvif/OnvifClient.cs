using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Onvif;

/// <summary>
/// Cliente SOAP mínimo para ONVIF (Device + Media) sobre HttpClient, con
/// WS-UsernameToken (PasswordDigest) y corrección de desfase de reloj: el
/// digest incluye la hora, y muchas cámaras rechazan solicitudes si el reloj
/// del cliente difiere del suyo, así que primero se consulta
/// GetSystemDateAndTime (sin autenticación) y se ajusta.
/// </summary>
internal sealed class OnvifClient : IDisposable
{
    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private TimeSpan _clockSkew = TimeSpan.Zero;
    private string? _mediaUrl;

    public OnvifClient(string host, int port, string username, string password)
    {
        _host = host;
        _port = port;
        _username = username;
        _password = password;
    }

    private string DeviceUrl => $"http://{_host}:{_port}/onvif/device_service";

    public void Dispose() => _http.Dispose();

    /// <summary>Sincroniza el reloj (sin autenticación) y valida que el servicio ONVIF responda.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        var response = await CallAsync(DeviceUrl, """
            <tds:GetSystemDateAndTime xmlns:tds="http://www.onvif.org/ver10/device/wsdl"/>
            """, withAuth: false, ct);

        // UTCDateTime → Date(Year,Month,Day) + Time(Hour,Minute,Second)
        var utc = Descend(response, "GetSystemDateAndTimeResponse", "SystemDateAndTime", "UTCDateTime");
        if (utc is not null &&
            int.TryParse(Find(utc, "Year")?.Value, out int y) &&
            int.TryParse(Find(utc, "Month")?.Value, out int mo) &&
            int.TryParse(Find(utc, "Day")?.Value, out int d) &&
            int.TryParse(Find(utc, "Hour")?.Value, out int h) &&
            int.TryParse(Find(utc, "Minute")?.Value, out int mi) &&
            int.TryParse(Find(utc, "Second")?.Value, out int s))
        {
            _clockSkew = new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc) - DateTime.UtcNow;
        }
    }

    public async Task<(string? Manufacturer, string? Model, string? Firmware, string? Serial)> GetDeviceInformationAsync(CancellationToken ct)
    {
        var response = await CallAsync(DeviceUrl, """
            <tds:GetDeviceInformation xmlns:tds="http://www.onvif.org/ver10/device/wsdl"/>
            """, withAuth: true, ct);
        var info = Descend(response, "GetDeviceInformationResponse");
        return (
            Find(info, "Manufacturer")?.Value.Trim(),
            Find(info, "Model")?.Value.Trim(),
            Find(info, "FirmwareVersion")?.Value.Trim(),
            Find(info, "SerialNumber")?.Value.Trim());
    }

    private async Task<string> GetMediaUrlAsync(CancellationToken ct)
    {
        if (_mediaUrl is not null) return _mediaUrl;
        var response = await CallAsync(DeviceUrl, """
            <tds:GetCapabilities xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
              <tds:Category>Media</tds:Category>
            </tds:GetCapabilities>
            """, withAuth: true, ct);
        string? url = Descend(response, "GetCapabilitiesResponse", "Capabilities", "Media", "XAddr")?.Value.Trim();
        // Algunas cámaras anuncian su XAddr con otra IP (NAT): se conserva la
        // ruta pero SIEMPRE se habla con el host configurado.
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            _mediaUrl = $"http://{_host}:{_port}{parsed.PathAndQuery}";
        else
            _mediaUrl = $"http://{_host}:{_port}/onvif/media_service";
        return _mediaUrl;
    }

    /// <summary>
    /// Perfiles de media (típicamente main y sub del canal 1) con su token y
    /// si traen configuración PTZ (la señal de que la cámara tiene PTZ).
    /// </summary>
    public async Task<List<(string Token, bool HasPtz)>> GetProfilesAsync(CancellationToken ct)
    {
        string mediaUrl = await GetMediaUrlAsync(ct);
        var response = await CallAsync(mediaUrl, """
            <trt:GetProfiles xmlns:trt="http://www.onvif.org/ver10/media/wsdl"/>
            """, withAuth: true, ct);
        return Descend(response, "GetProfilesResponse")?
            .Elements().Where(e => e.Name.LocalName == "Profiles")
            .Select(e => (
                Token: e.Attribute("token")?.Value ?? "",
                HasPtz: e.Elements().Any(x => x.Name.LocalName == "PTZConfiguration")))
            .Where(p => p.Token.Length > 0)
            .ToList() ?? [];
    }

    /// <summary>Compatibilidad: solo los tokens.</summary>
    public async Task<List<string>> GetProfileTokensAsync(CancellationToken ct) =>
        (await GetProfilesAsync(ct)).Select(p => p.Token).ToList();

    private string? _ptzUrl;

    private async Task<string> GetPtzUrlAsync(CancellationToken ct)
    {
        if (_ptzUrl is not null) return _ptzUrl;
        var response = await CallAsync(DeviceUrl, """
            <tds:GetCapabilities xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
              <tds:Category>PTZ</tds:Category>
            </tds:GetCapabilities>
            """, withAuth: true, ct);
        string? url = Descend(response, "GetCapabilitiesResponse", "Capabilities", "PTZ", "XAddr")?.Value.Trim();
        _ptzUrl = url is not null && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? $"http://{_host}:{_port}{parsed.PathAndQuery}"
            : $"http://{_host}:{_port}/onvif/ptz_service";
        return _ptzUrl;
    }

    /// <summary>Movimiento continuo (velocidades -1.0 .. 1.0; 0 = sin movimiento en ese eje).</summary>
    public async Task ContinuousMoveAsync(string profileToken, double panSpeed, double tiltSpeed, double zoomSpeed, CancellationToken ct)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string ptzUrl = await GetPtzUrlAsync(ct);
        await CallAsync(ptzUrl, $"""
            <tptz:ContinuousMove xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl">
              <tptz:ProfileToken>{profileToken}</tptz:ProfileToken>
              <tptz:Velocity>
                <tt:PanTilt x="{panSpeed.ToString("0.###", inv)}" y="{tiltSpeed.ToString("0.###", inv)}"
                    xmlns:tt="http://www.onvif.org/ver10/schema"/>
                <tt:Zoom x="{zoomSpeed.ToString("0.###", inv)}" xmlns:tt="http://www.onvif.org/ver10/schema"/>
              </tptz:Velocity>
            </tptz:ContinuousMove>
            """, withAuth: true, ct);
    }

    /// <summary>Detiene todo movimiento PTZ en curso.</summary>
    public async Task PtzStopAsync(string profileToken, CancellationToken ct)
    {
        string ptzUrl = await GetPtzUrlAsync(ct);
        await CallAsync(ptzUrl, $"""
            <tptz:Stop xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl">
              <tptz:ProfileToken>{profileToken}</tptz:ProfileToken>
              <tptz:PanTilt>true</tptz:PanTilt>
              <tptz:Zoom>true</tptz:Zoom>
            </tptz:Stop>
            """, withAuth: true, ct);
    }

    /// <summary>URI RTSP del perfil (sin credenciales; se inyectan al generar la configuración del media server).</summary>
    public async Task<string?> GetStreamUriAsync(string profileToken, CancellationToken ct)
    {
        string mediaUrl = await GetMediaUrlAsync(ct);
        var response = await CallAsync(mediaUrl, $"""
            <trt:GetStreamUri xmlns:trt="http://www.onvif.org/ver10/media/wsdl">
              <trt:StreamSetup>
                <tt:Stream xmlns:tt="http://www.onvif.org/ver10/schema">RTP-Unicast</tt:Stream>
                <tt:Transport xmlns:tt="http://www.onvif.org/ver10/schema">
                  <tt:Protocol>RTSP</tt:Protocol>
                </tt:Transport>
              </trt:StreamSetup>
              <trt:ProfileToken>{profileToken}</trt:ProfileToken>
            </trt:GetStreamUri>
            """, withAuth: true, ct);
        string? uri = Descend(response, "GetStreamUriResponse", "MediaUri", "Uri")?.Value.Trim();
        if (uri is null) return null;
        // Igual que el XAddr: conservar ruta/puerto pero forzar el host configurado.
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return $"rtsp://{_host}:{(parsed.IsDefaultPort ? 554 : parsed.Port)}{parsed.PathAndQuery}";
        return uri;
    }

    public async Task<string?> GetSnapshotUriAsync(string profileToken, CancellationToken ct)
    {
        string mediaUrl = await GetMediaUrlAsync(ct);
        var response = await CallAsync(mediaUrl, $"""
            <trt:GetSnapshotUri xmlns:trt="http://www.onvif.org/ver10/media/wsdl">
              <trt:ProfileToken>{profileToken}</trt:ProfileToken>
            </trt:GetSnapshotUri>
            """, withAuth: true, ct);
        string? uri = Descend(response, "GetSnapshotUriResponse", "MediaUri", "Uri")?.Value.Trim();
        if (uri is null) return null;
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return $"http://{_host}:{(parsed.IsDefaultPort ? 80 : parsed.Port)}{parsed.PathAndQuery}";
        return uri;
    }

    // ------------------------------------------------------------------
    // SOAP
    // ------------------------------------------------------------------

    private async Task<XElement> CallAsync(string serviceUrl, string bodyXml, bool withAuth, CancellationToken ct)
    {
        string envelope = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
              {(withAuth ? SecurityHeader() : "<s:Header/>")}
              <s:Body>{bodyXml}</s:Body>
            </s:Envelope>
            """;

        HttpResponseMessage response;
        try
        {
            using var content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml");
            response = await _http.PostAsync(serviceUrl, content, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DriverException($"El dispositivo ONVIF en {_host}:{_port} no respondió a tiempo.");
        }
        catch (HttpRequestException ex)
        {
            throw new DriverException($"No se pudo conectar con el servicio ONVIF en {_host}:{_port}: {ex.Message}");
        }

        string text = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new DriverException("El dispositivo ONVIF rechazó las credenciales (usuario o contraseña incorrectos).");

        XElement root;
        try
        {
            root = XDocument.Parse(text).Root!;
        }
        catch
        {
            throw new DriverException($"El dispositivo en {_host}:{_port} no respondió como servicio ONVIF (¿puerto correcto?).");
        }

        var body = root.Element(Soap + "Body")
            ?? root.Elements().FirstOrDefault(e => e.Name.LocalName == "Body")
            ?? throw new DriverException("Respuesta ONVIF sin cuerpo SOAP.");

        var fault = body.Elements().FirstOrDefault(e => e.Name.LocalName == "Fault");
        if (fault is not null)
        {
            string reason = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value.Trim()
                ?? fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value.Trim()
                ?? "error SOAP";
            bool authFault = reason.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
                             reason.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase);
            throw new DriverException(authFault
                ? "El dispositivo ONVIF rechazó las credenciales (usuario o contraseña incorrectos)."
                : $"El dispositivo ONVIF respondió con un error: {reason}");
        }
        return body;
    }

    /// <summary>WS-Security UsernameToken con PasswordDigest = Base64(SHA1(nonce + created + password)).</summary>
    private string SecurityHeader()
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(16);
        string created = (DateTime.UtcNow + _clockSkew).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        byte[] digestInput = [.. nonce, .. Encoding.UTF8.GetBytes(created), .. Encoding.UTF8.GetBytes(_password)];
        string digest = Convert.ToBase64String(SHA1.HashData(digestInput));

        return $"""
            <s:Header>
              <wsse:Security s:mustUnderstand="1"
                  xmlns:wsse="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd"
                  xmlns:wsu="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd">
                <wsse:UsernameToken>
                  <wsse:Username>{System.Security.SecurityElement.Escape(_username)}</wsse:Username>
                  <wsse:Password Type="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest">{digest}</wsse:Password>
                  <wsse:Nonce EncodingType="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary">{Convert.ToBase64String(nonce)}</wsse:Nonce>
                  <wsu:Created>{created}</wsu:Created>
                </wsse:UsernameToken>
              </wsse:Security>
            </s:Header>
            """;
    }

    // ------------------------------------------------------------------
    // Navegación XML por nombre local (los prefijos varían entre marcas)
    // ------------------------------------------------------------------

    private static XElement? Find(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static XElement? Descend(XElement? parent, params string[] path)
    {
        var current = parent;
        foreach (string name in path)
        {
            current = current?.Descendants().FirstOrDefault(e => e.Name.LocalName == name);
            if (current is null) return null;
        }
        return current;
    }
}
