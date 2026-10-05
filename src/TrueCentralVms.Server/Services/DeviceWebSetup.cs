using System.Net;
using System.Xml.Linq;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Hikvision;
using TransitionTime = System.TimeZoneInfo.TransitionTime;

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
    public static Task<string?> ApplyDnsAsync(string brand, string host, int httpPort, string username,
        string password, string dns1, string dns2, ILogger logger, CancellationToken ct) =>
        RetryAsync(host, logger, ct, () => brand == "Hikvision"
            ? ApplyHikvisionDnsAsync(host, httpPort, username, password, dns1, dns2, ct)
            : ApplyDahuaDnsAsync(host, httpPort, username, password, dns1, dns2, ct));

    /// <summary>
    /// Deja la fecha, la hora y la zona horaria (con su horario de verano) del
    /// equipo iguales a las de este servidor. Devuelve null si quedó aplicado.
    /// </summary>
    public static Task<string?> SyncTimeAsync(string brand, string host, int httpPort, string username,
        string password, ILogger logger, CancellationToken ct) =>
        RetryAsync(host, logger, ct, () => brand == "Hikvision"
            ? SyncHikvisionTimeAsync(host, httpPort, username, password, ct)
            : SyncDahuaTimeAsync(host, httpPort, username, password, ct));

    private static async Task<string?> RetryAsync(string host, ILogger logger, CancellationToken ct, Func<Task> action)
    {
        var deadline = DateTime.UtcNow + RetryWindow;
        string? lastError = null;
        while (true)
        {
            try
            {
                await action();
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                return ex.Message; // credenciales: reintentar solo bloquearía la cuenta
            }
            catch (DeviceRejectedException ex)
            {
                return ex.Message; // el equipo respondió y dijo que no: reintentar no cambia nada
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                lastError = ex.Message;
                logger.LogDebug(ex, "Configuración web de {Host}: reintento.", host);
            }
            if (DateTime.UtcNow + RetryDelay > deadline) return lastError;
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
    /// el signo va invertido respecto de UTC, como en POSIX).
    /// </summary>
    private static async Task SyncHikvisionTimeAsync(string host, int port, string username, string password,
        CancellationToken ct)
    {
        var isapi = Isapi(host, port, username, password);
        const string path = "/ISAPI/System/time";
        string xml = await RequestHikAsync(isapi, HttpMethod.Get, path, null, ct)
            ?? throw new DeviceRejectedException("El equipo no expone su configuración de hora por ISAPI.");
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
        Set("timeZone", PosixTimeZone(TimeZoneInfo.Local));

        await RequestHikAsync(isapi, HttpMethod.Put, path, doc.ToString(SaveOptions.DisableFormatting), ct);
    }

    /// <summary>Zona horaria en formato POSIX con horario de verano, a partir de la zona de Windows.</summary>
    internal static string PosixTimeZone(TimeZoneInfo tz)
    {
        static string Offset(TimeSpan utcOffset)
        {
            // POSIX invierte el signo: UTC-4 se escribe "+4".
            var o = -utcOffset;
            return $"{(o < TimeSpan.Zero ? "-" : "+")}{Math.Abs(o.Hours)}:{Math.Abs(o.Minutes):00}:00";
        }
        string text = "CST" + Offset(tz.BaseUtcOffset);

        var rule = CurrentRule(tz);
        if (rule is null || rule.DaylightTransitionStart.IsFixedDateRule || rule.DaylightTransitionEnd.IsFixedDateRule)
            return text;
        static string Transition(TransitionTime t)
        {
            // TimeOfDay 23:59:59.999 (las reglas "a medianoche") se escribe 24:00:00.
            var time = t.TimeOfDay.TimeOfDay;
            string at = time >= new TimeSpan(23, 59, 59) ? "24:00:00" : $"{time.Hours:00}:{time.Minutes:00}:{time.Seconds:00}";
            return $"M{t.Month}.{t.Week}.{(int)t.DayOfWeek}/{at}";
        }
        var delta = rule.DaylightDelta;
        return $"{text}DST{delta.Hours:00}:{delta.Minutes:00}:00," +
               $"{Transition(rule.DaylightTransitionStart)},{Transition(rule.DaylightTransitionEnd)}";
    }

    private static TimeZoneInfo.AdjustmentRule? CurrentRule(TimeZoneInfo tz)
    {
        if (!tz.SupportsDaylightSavingTime) return null;
        var today = DateTime.Today;
        return tz.GetAdjustmentRules().FirstOrDefault(r => r.DateStart <= today && today <= r.DateEnd);
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
        var tz = TimeZoneInfo.Local;

        var settings = new List<string> { "Locales.TimeFormat=" + Uri.EscapeDataString("dd-MM-yyyy HH:mm:ss") };
        if (DahuaTimeZoneIndex(tz.BaseUtcOffset) is int index)
            settings.Add($"NTP.TimeZone={index}");
        var rule = CurrentRule(tz);
        if (rule is not null && !rule.DaylightTransitionStart.IsFixedDateRule && !rule.DaylightTransitionEnd.IsFixedDateRule)
        {
            settings.Add("Locales.DSTEnable=true");
            void Add(string prefix, TransitionTime t)
            {
                var time = t.TimeOfDay.TimeOfDay;
                bool midnight = time >= new TimeSpan(23, 59, 59);
                settings.Add($"Locales.{prefix}.Month={t.Month}");
                // Semana: 1..4 y -1 = la última del mes (en .NET es la 5).
                settings.Add($"Locales.{prefix}.Week={(t.Week == 5 ? -1 : t.Week)}");
                settings.Add($"Locales.{prefix}.Day={(int)t.DayOfWeek}");
                settings.Add($"Locales.{prefix}.Hour={(midnight ? 24 : time.Hours)}");
                settings.Add($"Locales.{prefix}.Minute={(midnight ? 0 : time.Minutes)}");
            }
            Add("DSTStart", rule.DaylightTransitionStart);
            Add("DSTEnd", rule.DaylightTransitionEnd);
        }
        else
        {
            settings.Add("Locales.DSTEnable=false");
        }
        ExpectOk(await DahuaGetAsync(http, "/cgi-bin/configManager.cgi?action=setConfig&" + string.Join('&', settings), ct),
            "la zona horaria");

        string now = Uri.EscapeDataString(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        ExpectOk(await DahuaGetAsync(http, $"/cgi-bin/global.cgi?action=setCurrentTime&time={now}", ct), "la hora");
    }

    /// <summary>Índice de NTP.TimeZone en la tabla de Dahua (API HTTP): GMT+00:00 = 0 … GMT-12:00 = 32.</summary>
    private static int? DahuaTimeZoneIndex(TimeSpan offset)
    {
        string[] table =
        [
            "+00:00", "+01:00", "+02:00", "+03:00", "+03:30", "+04:00", "+04:30", "+05:00", "+05:30", "+05:45",
            "+06:00", "+06:30", "+07:00", "+08:00", "+09:00", "+09:30", "+10:00", "+11:00", "+12:00", "+13:00",
            "-01:00", "-02:00", "-03:00", "-03:30", "-04:00", "-05:00", "-06:00", "-07:00", "-08:00", "-09:00",
            "-10:00", "-11:00", "-12:00",
        ];
        string key = $"{(offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";
        int i = Array.IndexOf(table, key);
        return i < 0 ? null : i;
    }
}
