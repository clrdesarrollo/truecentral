using System.Globalization;
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

    private readonly HttpClient _http;
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
        // Firmwares recientes (Dahua 3.1xx entre otros) exigen digest a nivel
        // HTTP en el endpoint ONVIF y responden 401 ignorando el
        // WS-UsernameToken del sobre; el handler contesta el desafío con las
        // mismas credenciales y reenvía el cuerpo completo.
        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(username, password),
            PreAuthenticate = true,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private string DeviceUrl => $"http://{_host}:{_port}/onvif/device_service";

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Desfase del reloj del equipo respecto de UTC (su hora local menos UTC).
    /// Las grabaciones ONVIF se informan en UTC y la línea de tiempo del VMS
    /// trabaja en la hora local del grabador, como el resto de los drivers.
    /// </summary>
    public TimeSpan DeviceUtcOffset { get; private set; } = TimeSpan.Zero;

    /// <summary>Sincroniza el reloj (sin autenticación) y valida que el servicio ONVIF responda.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        var response = await CallAsync(DeviceUrl, """
            <tds:GetSystemDateAndTime xmlns:tds="http://www.onvif.org/ver10/device/wsdl"/>
            """, withAuth: false, ct);

        var systemTime = Descend(response, "GetSystemDateAndTimeResponse", "SystemDateAndTime");
        if (ReadDateTime(Descend(systemTime, "UTCDateTime")) is { } deviceUtc)
        {
            _clockSkew = DateTime.SpecifyKind(deviceUtc, DateTimeKind.Utc) - DateTime.UtcNow;
            if (ReadDateTime(Descend(systemTime, "LocalDateTime")) is { } deviceLocal)
                DeviceUtcOffset = TimeSpan.FromMinutes(Math.Round((deviceLocal - deviceUtc).TotalMinutes));
        }
    }

    /// <summary>Hora ONVIF: Date(Year,Month,Day) + Time(Hour,Minute,Second).</summary>
    private static DateTime? ReadDateTime(XElement? node)
    {
        if (node is null) return null;
        if (!int.TryParse(Descend(node, "Year")?.Value, out int year) ||
            !int.TryParse(Descend(node, "Month")?.Value, out int month) ||
            !int.TryParse(Descend(node, "Day")?.Value, out int day) ||
            !int.TryParse(Descend(node, "Hour")?.Value, out int hour) ||
            !int.TryParse(Descend(node, "Minute")?.Value, out int minute) ||
            !int.TryParse(Descend(node, "Second")?.Value, out int second))
            return null;
        try
        {
            return new DateTime(year, month, day, hour, minute, Math.Min(59, second));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // hora imposible: se trabaja sin corrección
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

    /// <summary>Mueve la cámara a un preset (los tokens numéricos son la convención de facto).</summary>
    public async Task GotoPresetAsync(string profileToken, string presetToken, CancellationToken ct)
    {
        string ptzUrl = await GetPtzUrlAsync(ct);
        await CallAsync(ptzUrl, $"""
            <tptz:GotoPreset xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl">
              <tptz:ProfileToken>{profileToken}</tptz:ProfileToken>
              <tptz:PresetToken>{presetToken}</tptz:PresetToken>
            </tptz:GotoPreset>
            """, withAuth: true, ct);
    }

    /// <summary>Guarda la posición actual como preset con el token indicado.</summary>
    public async Task SetPresetAsync(string profileToken, string presetToken, string presetName, CancellationToken ct)
    {
        string ptzUrl = await GetPtzUrlAsync(ct);
        await CallAsync(ptzUrl, $"""
            <tptz:SetPreset xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl">
              <tptz:ProfileToken>{profileToken}</tptz:ProfileToken>
              <tptz:PresetName>{presetName}</tptz:PresetName>
              <tptz:PresetToken>{presetToken}</tptz:PresetToken>
            </tptz:SetPreset>
            """, withAuth: true, ct);
    }

    /// <summary>Elimina un preset.</summary>
    public async Task RemovePresetAsync(string profileToken, string presetToken, CancellationToken ct)
    {
        string ptzUrl = await GetPtzUrlAsync(ct);
        await CallAsync(ptzUrl, $"""
            <tptz:RemovePreset xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl">
              <tptz:ProfileToken>{profileToken}</tptz:ProfileToken>
              <tptz:PresetToken>{presetToken}</tptz:PresetToken>
            </tptz:RemovePreset>
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
    // Perfil G (grabación en el equipo): servicios Recording / Search / Replay
    // ------------------------------------------------------------------

    private Dictionary<string, string>? _services;

    /// <summary>
    /// XAddr de los servicios del equipo por espacio de nombres. GetServices
    /// es el único lugar donde se anuncian Recording/Search/Replay
    /// (GetCapabilities solo cubre los servicios clásicos). Como con el resto
    /// de las URL ONVIF se conserva la ruta pero se fuerza el host configurado.
    /// </summary>
    private async Task<string?> GetServiceUrlAsync(string wsdlNamespace, CancellationToken ct)
    {
        if (_services is null)
        {
            var response = await CallAsync(DeviceUrl, """
                <tds:GetServices xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
                  <tds:IncludeCapability>false</tds:IncludeCapability>
                </tds:GetServices>
                """, withAuth: true, ct);
            _services = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var service in Descend(response, "GetServicesResponse")?
                         .Elements().Where(e => e.Name.LocalName == "Service") ?? [])
            {
                string? ns = Find(service, "Namespace")?.Value.Trim();
                string? xaddr = Find(service, "XAddr")?.Value.Trim();
                if (string.IsNullOrEmpty(ns) || string.IsNullOrEmpty(xaddr)) continue;
                _services[ns] = Uri.TryCreate(xaddr, UriKind.Absolute, out var parsed)
                    ? $"http://{_host}:{_port}{parsed.PathAndQuery}"
                    : xaddr;
            }
        }
        return _services.TryGetValue(wsdlNamespace, out string? url) ? url : null;
    }

    public Task<string?> GetSearchServiceAsync(CancellationToken ct) =>
        GetServiceUrlAsync("http://www.onvif.org/ver10/search/wsdl", ct);

    public Task<string?> GetReplayServiceAsync(CancellationToken ct) =>
        GetServiceUrlAsync("http://www.onvif.org/ver10/replay/wsdl", ct);

    /// <summary>
    /// Tokens de las grabaciones del equipo. Primero por el servicio de
    /// grabación (una sola llamada); si no está, por la búsqueda del Perfil G
    /// (FindRecordings abre la sesión de búsqueda y GetRecordingSearchResults
    /// entrega el resultado).
    /// </summary>
    public async Task<List<string>> GetRecordingTokensAsync(CancellationToken ct)
    {
        if (await GetServiceUrlAsync("http://www.onvif.org/ver10/recording/wsdl", ct) is { } recordingUrl)
        {
            try
            {
                var response = await CallAsync(recordingUrl, """
                    <trc:GetRecordings xmlns:trc="http://www.onvif.org/ver10/recording/wsdl"/>
                    """, withAuth: true, ct);
                var tokens = TokensIn(response);
                if (tokens.Count > 0) return tokens;
            }
            catch (DriverException)
            {
                // Servicio anunciado pero no operativo: se intenta por la búsqueda.
            }
        }

        if (await GetSearchServiceAsync(ct) is not { } searchUrl)
            return [];

        var started = await CallAsync(searchUrl, """
            <tse:FindRecordings xmlns:tse="http://www.onvif.org/ver10/search/wsdl">
              <tse:Scope/>
            </tse:FindRecordings>
            """, withAuth: true, ct);
        string? searchToken = Descend(started, "FindRecordingsResponse", "SearchToken")?.Value.Trim();
        if (string.IsNullOrEmpty(searchToken)) return [];

        var results = await CallAsync(searchUrl, $"""
            <tse:GetRecordingSearchResults xmlns:tse="http://www.onvif.org/ver10/search/wsdl">
              <tse:SearchToken>{System.Security.SecurityElement.Escape(searchToken)}</tse:SearchToken>
              <tse:MinResults>1</tse:MinResults>
              <tse:MaxResults>100</tse:MaxResults>
              <tse:WaitTime>PT5S</tse:WaitTime>
            </tse:GetRecordingSearchResults>
            """, withAuth: true, ct);
        return TokensIn(results);
    }

    private static List<string> TokensIn(XElement response) =>
        response.Descendants()
            .Where(e => e.Name.LocalName == "RecordingToken")
            .Select(e => e.Value.Trim())
            .Where(t => t.Length > 0)
            .Distinct()
            .ToList();

    /// <summary>
    /// Rango grabado de una grabación (UTC). El Perfil G solo obliga a
    /// informar el extremo más antiguo y el más nuevo: es el detalle máximo
    /// que entrega el estándar sin recorrer eventos de grabación.
    /// </summary>
    public async Task<(DateTime FromUtc, DateTime UntilUtc)?> GetRecordingRangeAsync(string recordingToken, CancellationToken ct)
    {
        if (await GetSearchServiceAsync(ct) is not { } searchUrl) return null;
        var response = await CallAsync(searchUrl, $"""
            <tse:GetRecordingInformation xmlns:tse="http://www.onvif.org/ver10/search/wsdl">
              <tse:RecordingToken>{System.Security.SecurityElement.Escape(recordingToken)}</tse:RecordingToken>
            </tse:GetRecordingInformation>
            """, withAuth: true, ct);
        var info = Descend(response, "GetRecordingInformationResponse", "RecordingInformation");
        if (info is null) return null;
        if (ParseUtc(Find(info, "EarliestRecording")?.Value) is not { } from) return null;
        var until = ParseUtc(Find(info, "LatestRecording")?.Value) ?? DateTime.UtcNow;
        return until > from ? (from, until) : null;
    }

    /// <summary>URI RTSP de reproducción de una grabación (sin credenciales).</summary>
    public async Task<string?> GetReplayUriAsync(string recordingToken, CancellationToken ct)
    {
        if (await GetReplayServiceAsync(ct) is not { } replayUrl) return null;
        var response = await CallAsync(replayUrl, $"""
            <trp:GetReplayUri xmlns:trp="http://www.onvif.org/ver10/replay/wsdl">
              <trp:StreamSetup>
                <tt:Stream xmlns:tt="http://www.onvif.org/ver10/schema">RTP-Unicast</tt:Stream>
                <tt:Transport xmlns:tt="http://www.onvif.org/ver10/schema">
                  <tt:Protocol>RTSP</tt:Protocol>
                </tt:Transport>
              </trp:StreamSetup>
              <trp:RecordingToken>{System.Security.SecurityElement.Escape(recordingToken)}</trp:RecordingToken>
            </trp:GetReplayUri>
            """, withAuth: true, ct);
        string? uri = Descend(response, "GetReplayUriResponse", "Uri")?.Value.Trim();
        if (string.IsNullOrEmpty(uri)) return null;
        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            ? $"rtsp://{_host}:{(parsed.IsDefaultPort ? 554 : parsed.Port)}{parsed.PathAndQuery}"
            : uri;
    }

    private static DateTime? ParseUtc(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;

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
            throw new DriverException(
                "El dispositivo ONVIF rechazó las credenciales. Ojo: en algunas marcas (ej. Dahua) " +
                "la cuenta ONVIF es independiente de la cuenta web/SDK y se administra aparte " +
                "(Cuentas → Usuario ONVIF en el equipo).");

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
                ? "El dispositivo ONVIF rechazó las credenciales. Ojo: en algunas marcas (ej. Dahua) " +
                  "la cuenta ONVIF es independiente de la cuenta web/SDK y se administra aparte " +
                  "(Cuentas → Usuario ONVIF en el equipo)."
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
