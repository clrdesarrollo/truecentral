using System.Net;
using System.Xml.Linq;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Hikvision;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Parámetros básicos de un equipo recién puesto en red que el cambio de IP
/// por multicast no lleva (ni SADP en Hikvision ni CLIENT_ModifyDevice en
/// Dahua): DNS y fecha/hora/zona horaria. Se aplican por la API web del
/// equipo, iniciando sesión con sus credenciales, justo después del cambio;
/// por eso se reintenta mientras el equipo reinicia su red.
/// </summary>
public static class DeviceWebSetup
{
    private static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>Configura los DNS. Devuelve null si quedó aplicado, o el motivo por el que no.</summary>
    public static async Task<string?> ApplyDnsAsync(string brand, string host, int httpPort, string username,
        string password, string dns1, string dns2, ILogger logger, CancellationToken ct) =>
        (await RetryAsync(host, logger, ct, async () =>
        {
            await (brand == "Hikvision"
                ? ApplyHikvisionDnsAsync(host, httpPort, username, password, dns1, dns2, ct)
                : ApplyDahuaDnsAsync(host, httpPort, username, password, dns1, dns2, ct));
            return null;
        })).Error;

    /// <summary>
    /// Deja la fecha, la hora y la zona horaria (con su horario de verano) del
    /// equipo iguales a las de este servidor. Error = null si quedó aplicado;
    /// Note = lo que quedó a medias aunque se aplicó (p. ej. sin horario de verano).
    /// </summary>
    public static Task<(string? Error, string? Note)> SyncTimeAsync(string brand, string host, int httpPort,
        string username, string password, ILogger logger, CancellationToken ct) =>
        RetryAsync(host, logger, ct, async () =>
        {
            if (brand == "Hikvision") return await SyncHikvisionTimeAsync(host, httpPort, username, password, logger, ct);
            await SyncDahuaTimeAsync(host, httpPort, username, password, ct);
            return null;
        });

    private static async Task<(string? Error, string? Note)> RetryAsync(string host, ILogger logger,
        CancellationToken ct, Func<Task<string?>> action)
    {
        var deadline = DateTime.UtcNow + RetryWindow;
        string? lastError = null;
        while (true)
        {
            try
            {
                return (null, await action());
            }
            catch (UnauthorizedAccessException ex)
            {
                return (ex.Message, null); // credenciales: reintentar solo bloquearía la cuenta
            }
            catch (DeviceRejectedException ex)
            {
                return (ex.Message, null); // el equipo respondió y dijo que no: reintentar no cambia nada
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                lastError = ex.Message;
                logger.LogDebug(ex, "Configuración web de {Host}: reintento.", host);
            }
            if (DateTime.UtcNow + RetryDelay > deadline) return (lastError, null);
            await Task.Delay(RetryDelay, ct);
        }
    }

    /// <summary>El equipo contestó pero rechazó el valor.</summary>
    private sealed class DeviceRejectedException(string message) : Exception(message);

    // ------------------------------------------------------------------
    // Hikvision (ISAPI)
    // ------------------------------------------------------------------

    private static HikvisionIsapiClient Isapi(string host, int port, string username, string password) =>
        new(new AlarmConnectionInfo(host, port, false, username, password), digestOnly: true, deviceNoun: "equipo");

    /// <summary>GET y PUT de /ISAPI/System/Network/interfaces/{id}/ipAddress con PrimaryDNS/SecondaryDNS.</summary>
    private static async Task ApplyHikvisionDnsAsync(string host, int port, string username, string password,
        string dns1, string dns2, CancellationToken ct)
    {
        var isapi = Isapi(host, port, username, password);
        string id = "1";
        string? list = await RequestHikAsync(isapi, HttpMethod.Get, "/ISAPI/System/Network/interfaces", null, ct);
        if (list is not null)
            id = XDocument.Parse(list).Descendants().FirstOrDefault(e => e.Name.LocalName == "id")?.Value.Trim() ?? "1";

        string path = $"/ISAPI/System/Network/interfaces/{id}/ipAddress";
        string xml = await RequestHikAsync(isapi, HttpMethod.Get, path, null, ct)
            ?? throw new DeviceRejectedException("El equipo no expone su configuración de red por ISAPI.");
        var doc = XDocument.Parse(xml);
        var root = doc.Root!;
        XNamespace ns = root.Name.Namespace;

        void SetDns(string name, string value, string after)
        {
            var node = root.Element(ns + name);
            if (node is null)
            {
                node = new XElement(ns + name, new XElement(ns + "ipAddress"));
                var anchor = root.Element(ns + after);
                if (anchor is not null) anchor.AddAfterSelf(node); else root.Add(node);
            }
            var address = node.Element(ns + "ipAddress");
            if (address is null) node.Add(address = new XElement(ns + "ipAddress"));
            address.Value = value;
        }
        SetDns("PrimaryDNS", dns1, "DefaultGateway");
        SetDns("SecondaryDNS", dns2.Length > 0 ? dns2 : "0.0.0.0", "PrimaryDNS");

        await RequestHikAsync(isapi, HttpMethod.Put, path, doc.ToString(SaveOptions.DisableFormatting), ct);
    }

    /// <summary>
    /// /ISAPI/System/time: hora manual con la hora local del servidor y la zona
    /// en el formato POSIX que usa Hikvision ("CST+4:00:00DST01:00:00,M9.1.6/24:00:00,M4.1.6/24:00:00";
    /// el signo va invertido respecto de UTC, como en POSIX). No todos los
    /// firmwares aceptan lo mismo: los terminales de acceso (DS-K1T) rechazan la
    /// hora 24:00:00 de las transiciones con badXmlContent. Se prueba la forma
    /// completa, luego con 23:59:59 y, como último recurso, solo el desfase sin
    /// horario de verano (devuelto como nota, para avisarlo).
    /// </summary>
    private static async Task<string?> SyncHikvisionTimeAsync(string host, int port, string username, string password,
        ILogger logger, CancellationToken ct)
    {
        var isapi = Isapi(host, port, username, password);
        const string path = "/ISAPI/System/time";
        string xml = await RequestHikAsync(isapi, HttpMethod.Get, path, null, ct)
            ?? throw new DeviceRejectedException("El equipo no expone su configuración de hora por ISAPI.");

        var candidates = DeviceTimeZones.PosixCandidates(TimeZoneInfo.Local);
        string full = candidates[0];
        int dst = full.IndexOf("DST", StringComparison.Ordinal);

        DeviceRejectedException? rejected = null;
        foreach (string zone in candidates)
        {
            var doc = XDocument.Parse(xml);
            var root = doc.Root!;
            XNamespace ns = root.Name.Namespace;
            void Set(string name, string value)
            {
                var node = root.Element(ns + name);
                if (node is null) root.Add(node = new XElement(ns + name));
                node.Value = value;
            }
            Set("timeMode", "manual");
            Set("localTime", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));
            Set("timeZone", zone);
            try
            {
                await RequestHikAsync(isapi, HttpMethod.Put, path, doc.ToString(SaveOptions.DisableFormatting), ct);
            }
            catch (DeviceRejectedException ex)
            {
                logger.LogInformation("{Host} rechazó la zona horaria \"{Zone}\": {Error}", host, zone, ex.Message);
                rejected = ex;
                continue;
            }
            return dst > 0 && zone == full[..dst]
                ? "el equipo no aceptó el horario de verano: quedó con el desfase de invierno; ajústelo en el equipo si corresponde."
                : null;
        }
        throw rejected!;
    }

    private static async Task<string?> RequestHikAsync(HikvisionIsapiClient isapi, HttpMethod method, string path,
        string? body, CancellationToken ct)
    {
        try
        {
            return await isapi.RequestAsync(method, path, body, "application/xml", ct);
        }
        catch (DriverException ex) when (ex.Message.Contains("credencial", StringComparison.OrdinalIgnoreCase) ||
                                         ex.Message.Contains("contraseña", StringComparison.OrdinalIgnoreCase) ||
                                         ex.Message.Contains("bloque", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"El equipo rechazó el usuario o la contraseña ({ex.Message}).");
        }
        catch (DriverException ex) when (method != HttpMethod.Get && ex.Message.Contains("rechazó"))
        {
            throw new DeviceRejectedException(ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Dahua (CGI)
    // ------------------------------------------------------------------

    private static async Task<string> DahuaGetAsync(HttpClient http, string pathAndQuery, CancellationToken ct)
    {
        using var response = await http.GetAsync(pathAndQuery, ct);
        string text = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("El equipo rechazó el usuario o la contraseña.");
        if (response.StatusCode == HttpStatusCode.BadRequest)
            throw new DeviceRejectedException($"El equipo rechazó el valor ({text.Trim()}).");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"El equipo respondió {(int)response.StatusCode}.");
        return text;
    }

    private static HttpClient DahuaHttp(string host, int port, string username, string password) =>
        new(new HttpClientHandler { Credentials = new NetworkCredential(username, password) }, disposeHandler: true)
        {
            BaseAddress = new Uri($"http://{host}:{port}"),
            Timeout = TimeSpan.FromSeconds(10),
        };

    private static void ExpectOk(string result, string what)
    {
        if (!result.Trim().StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            throw new DeviceRejectedException($"El equipo no aceptó {what} ({result.Trim()}).");
    }

    /// <summary>Network.&lt;interfaz por defecto&gt;.DnsServers[0..1].</summary>
    private static async Task ApplyDahuaDnsAsync(string host, int port, string username, string password,
        string dns1, string dns2, CancellationToken ct)
    {
        using var http = DahuaHttp(host, port, username, password);
        string config = await DahuaGetAsync(http, "/cgi-bin/configManager.cgi?action=getConfig&name=Network", ct);
        string iface = config.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("table.Network.DefaultInterface=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1].Trim() ?? "eth0";

        string query = $"action=setConfig&Network.{iface}.DnsServers[0]={Uri.EscapeDataString(dns1)}" +
                       $"&Network.{iface}.DnsServers[1]={Uri.EscapeDataString(dns2.Length > 0 ? dns2 : dns1)}";
        ExpectOk(await DahuaGetAsync(http, $"/cgi-bin/configManager.cgi?{query}", ct), "los DNS");
    }

    /// <summary>
    /// Zona horaria (NTP.TimeZone es un índice de la tabla fija de Dahua),
    /// horario de verano (Locales.DST*), formato de fecha y, al final, la hora
    /// actual (global.cgi setCurrentTime, en hora local del equipo).
    /// </summary>
    private static async Task SyncDahuaTimeAsync(string host, int port, string username, string password,
        CancellationToken ct)
    {
        using var http = DahuaHttp(host, port, username, password);

        var settings = new List<string> { "Locales.TimeFormat=" + Uri.EscapeDataString("dd-MM-yyyy HH:mm:ss") };
        settings.AddRange(TrueCentralVms.Drivers.Dahua.DahuaTime.ZoneSettings(TimeZoneInfo.Local));
        ExpectOk(await DahuaGetAsync(http, "/cgi-bin/configManager.cgi?action=setConfig&" + string.Join('&', settings), ct),
            "la zona horaria");

        string now = TrueCentralVms.Drivers.Dahua.DahuaTime.TimeArgument(DateTime.Now);
        ExpectOk(await DahuaGetAsync(http, $"/cgi-bin/global.cgi?action=setCurrentTime&time={now}", ct), "la hora");
    }
}
