using System.Globalization;
using System.Xml.Linq;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Hora y mantenimiento de cualquier equipo Hikvision que hable ISAPI
/// (terminales de acceso hoy; cámaras, grabadores, paneles y citofonía usan
/// las mismas rutas):
/// <list type="bullet">
/// <item><c>/ISAPI/System/time</c> — <c>timeMode</c> (manual | NTP),
/// <c>localTime</c> con su desfase ("2026-10-08T00:33:26+08:00") y
/// <c>timeZone</c> en POSIX de Hikvision (ver <see cref="DeviceTimeZones"/>).</item>
/// <item><c>/ISAPI/System/time/ntpServers</c> — servidor NTP e intervalo.</item>
/// <item><c>/ISAPI/System/reboot</c> y <c>/ISAPI/System/factoryReset?mode=basic|full</c>.</item>
/// </list>
/// Todo en XML: es lo que estas rutas aceptan en todos los firmware.
/// </summary>
public static class HikvisionMaintenance
{
    private const string Xml = "application/xml";

    public static async Task<DeviceClock> GetClockAsync(HikvisionIsapiClient client, CancellationToken ct)
    {
        var before = DateTime.UtcNow;
        string xml = await client.RequestAsync(HttpMethod.Get, "/ISAPI/System/time", null, Xml, ct)
                     ?? throw new DriverException("El equipo no informa su hora por ISAPI.");
        // El punto medio de la consulta: el viaje de ida y vuelta no es desfase.
        var readAt = before + (DateTime.UtcNow - before) / 2;

        var root = XDocument.Parse(xml).Root ?? throw new DriverException("El equipo devolvió una hora vacía.");
        string? mode = Value(root, "timeMode");
        string? local = Value(root, "localTime");
        string? zoneText = Value(root, "timeZone");
        var zone = DeviceTimeZones.ParsePosix(zoneText);

        DateTime localTime;
        TimeSpan? utcOffset = null;
        if (local is not null && local.Length > 19 &&
            DateTimeOffset.TryParse(local, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
        {
            localTime = withOffset.DateTime;
            utcOffset = withOffset.Offset;
        }
        else if (local is not null && DateTime.TryParse(local, CultureInfo.InvariantCulture,
                     DateTimeStyles.AllowWhiteSpaces, out var plain))
        {
            localTime = DateTime.SpecifyKind(plain, DateTimeKind.Unspecified);
            // Sin desfase en la hora: si la zona no tiene horario de verano, el
            // desfase es el de la zona y se puede saber la hora absoluta.
            if (zone is { DstDelta: null }) utcOffset = zone.UtcOffset;
        }
        else
        {
            throw new DriverException($"El equipo informó una hora que no se entiende ('{local}').");
        }

        var timeMode = mode?.Equals("NTP", StringComparison.OrdinalIgnoreCase) == true ? DeviceTimeMode.Ntp
            : mode?.Equals("manual", StringComparison.OrdinalIgnoreCase) == true ? DeviceTimeMode.Manual
            : DeviceTimeMode.Unknown;

        string? ntpServer = null;
        int? ntpInterval = null;
        if (timeMode == DeviceTimeMode.Ntp && await ReadNtpAsync(client, ct) is { } ntp)
        {
            // El equipo guarda los dos campos y usa el que dice addressingFormatType:
            // el otro puede traer un valor viejo (time.windows.com de fábrica).
            bool byIp = Value(ntp, "addressingFormatType")?.Equals("ipaddress", StringComparison.OrdinalIgnoreCase) == true;
            ntpServer = byIp ? Value(ntp, "ipAddress") ?? Value(ntp, "hostName") : Value(ntp, "hostName") ?? Value(ntp, "ipAddress");
            ntpInterval = int.TryParse(Value(ntp, "synchronizeInterval"), out int every) ? every : null;
        }

        return new DeviceClock(localTime, utcOffset, zoneText, zone?.UtcOffset, zone is null ? null : zone.DstDelta is not null,
            zone is null ? null : DeviceTimeZones.Describe(zone), timeMode, ntpServer, ntpInterval, readAt);
    }

    /// <summary>
    /// Deja la zona, el modo y la hora. La zona se prueba de la forma más
    /// completa a la más pobre (<see cref="DeviceTimeZones.PosixCandidates"/>):
    /// si el equipo solo aceptó el desfase sin horario de verano, lo devuelve
    /// como nota para avisarlo.
    /// </summary>
    public static async Task<string?> SetClockAsync(HikvisionIsapiClient client, DeviceClockSetting setting,
        CancellationToken ct)
    {
        string xml = await client.RequestAsync(HttpMethod.Get, "/ISAPI/System/time", null, Xml, ct)
                     ?? throw new DriverException("El equipo no expone su configuración de hora por ISAPI.");

        bool ntp = setting.Mode == DeviceTimeMode.Ntp;
        if (ntp) await WriteNtpAsync(client, setting, ct);

        var candidates = DeviceTimeZones.PosixCandidates(setting.Zone);
        DriverException? rejected = null;
        foreach (string zone in candidates)
        {
            var doc = XDocument.Parse(xml);
            var root = doc.Root!;
            Set(root, "timeMode", ntp ? "NTP" : "manual");
            // En NTP la hora la trae el servidor de hora; igual se manda la del
            // VMS para que no quede mal hasta la primera sincronización.
            Set(root, "localTime", setting.TargetLocalTime().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
            Set(root, "timeZone", zone);
            try
            {
                await PutAsync(client, "/ISAPI/System/time", doc.ToString(SaveOptions.DisableFormatting), "la hora", ct);
            }
            catch (DriverException ex) when (IsContentRejection(ex.Message))
            {
                rejected = ex;
                continue;
            }
            return zone == candidates[0] || zone.Contains("DST", StringComparison.Ordinal)
                ? null
                : "el equipo no aceptó el horario de verano: quedó con el desfase de la zona sin el cambio de hora " +
                  "de verano; ajústelo en el equipo si corresponde.";
        }
        throw rejected ?? new DriverException("El equipo no aceptó la zona horaria.");
    }

    public static Task RebootAsync(HikvisionIsapiClient client, CancellationToken ct) =>
        PutAsync(client, "/ISAPI/System/reboot", null, "el reinicio", ct);

    /// <summary>
    /// <c>basic</c> conserva la red y las cuentas de usuario; <c>full</c> deja
    /// el equipo de fábrica y DESACTIVADO.
    /// </summary>
    public static Task ResetAsync(HikvisionIsapiClient client, DeviceResetMode mode, CancellationToken ct) =>
        PutAsync(client, $"/ISAPI/System/factoryReset?mode={(mode == DeviceResetMode.Full ? "full" : "basic")}", null,
            "el restablecimiento", ct);

    // ------------------------------------------------------------------

    private static async Task<XElement?> ReadNtpAsync(HikvisionIsapiClient client, CancellationToken ct)
    {
        try
        {
            string? xml = await client.RequestAsync(HttpMethod.Get, "/ISAPI/System/time/ntpServers", null, Xml, ct);
            return xml is null ? null
                : XDocument.Parse(xml).Root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "NTPServer");
        }
        catch (DriverException) { return null; }
        catch (System.Xml.XmlException) { return null; }
    }

    /// <summary>El primer servidor NTP del equipo, con la dirección y el intervalo pedidos.</summary>
    private static async Task WriteNtpAsync(HikvisionIsapiClient client, DeviceClockSetting setting, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(setting.NtpServer))
            throw new DriverException("Falta el servidor NTP.");
        var current = await ReadNtpAsync(client, ct)
                      ?? throw new DriverException("El equipo no expone su configuración de NTP por ISAPI.");
        var server = BuildNtpServer(current, setting.NtpServer, setting.NtpIntervalMinutes);
        await PutAsync(client, $"/ISAPI/System/time/ntpServers/{Value(server, "id")}",
            server.ToString(SaveOptions.DisableFormatting), "el servidor NTP", ct);
    }

    /// <summary>
    /// Arma el <c>NTPServer</c> a enviar a partir del que el equipo informa.
    /// Se construye de nuevo, EN EL ORDEN DEL ESQUEMA (id, addressingFormatType,
    /// hostName, ipAddress, ipv6Address, portNo, synchronizeInterval): los
    /// terminales validan la secuencia, y editar la respuesta del GET agregando
    /// al final el campo que no traía —un equipo en «ipaddress» no informa
    /// <c>hostName</c>— termina en «Invalid Content · badParameters». Lo que
    /// el equipo ya tenía (la otra dirección, IPv6, el puerto) se conserva; el
    /// puerto es obligatorio, así que si no vino se manda el 123 de NTP.
    /// </summary>
    public static XElement BuildNtpServer(XElement current, string ntpServer, int intervalMinutes)
    {
        string host = ntpServer.Trim();
        bool isIp = System.Net.IPAddress.TryParse(host, out _);
        var ns = current.Name.Namespace;
        var server = new XElement(current.Name, current.Attributes());

        void Add(string name, string? value)
        {
            if (value is not null) server.Add(new XElement(ns + name, value));
        }

        Add("id", Value(current, "id") ?? "1");
        Add("addressingFormatType", isIp ? "ipaddress" : "hostname");
        Add("hostName", isIp ? Value(current, "hostName") : host);
        Add("ipAddress", isIp ? host : Value(current, "ipAddress"));
        Add("ipv6Address", Value(current, "ipv6Address"));
        Add("portNo", Value(current, "portNo") ?? "123");
        Add("synchronizeInterval", Math.Clamp(intervalMinutes, 1, 10080).ToString(CultureInfo.InvariantCulture));
        return server;
    }

    /// <summary>
    /// PUT que además revisa el <c>ResponseStatus</c>: ISAPI contesta 200 con un
    /// <c>statusCode</c> distinto de 1 cuando no aplicó lo pedido (7 = "hay que
    /// reiniciar", que también es aceptarlo).
    /// </summary>
    private static async Task PutAsync(HikvisionIsapiClient client, string path, string? body, string what, CancellationToken ct)
    {
        string? response = await client.RequestAsync(HttpMethod.Put, path, body, Xml, ct, allowNotFound: false);
        if (string.IsNullOrWhiteSpace(response)) return;
        XElement? root;
        try { root = XDocument.Parse(response).Root; }
        catch (System.Xml.XmlException) { return; }
        if (root is null || !int.TryParse(Value(root, "statusCode"), out int code) || code is 1 or 7) return;
        string detail = string.Join(" · ", new[] { Value(root, "statusString"), Value(root, "subStatusCode") }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        throw new DriverException($"El equipo rechazó {what}: {detail}");
    }

    /// <summary>
    /// El equipo entendió el pedido y no le gustó el CONTENIDO: vale la pena
    /// probar la forma siguiente de la zona. Un problema de clave nunca entra
    /// acá —reintentar con credenciales malas gasta intentos hasta bloquear la
    /// IP—, ni uno de red.
    /// </summary>
    private static bool IsContentRejection(string message) =>
        message.Contains("badXml", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("badParameters", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Invalid Content", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("rechazó la solicitud", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("rechazó la hora", StringComparison.OrdinalIgnoreCase);

    private static string? Value(XElement root, string name) =>
        root.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;

    /// <summary>Cambia (o agrega) un hijo directo respetando el espacio de nombres del documento.</summary>
    private static void Set(XElement root, string name, string value)
    {
        var node = root.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        if (node is null) root.Add(node = new XElement(root.Name.Namespace + name));
        node.Value = value;
    }
}
