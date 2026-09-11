using System.Text.Json;
using System.Xml.Linq;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Hikvision;

namespace TrueCentralVms.Migrator.Terminals;

public sealed record TerminalInfo(string Model, string SerialNumber, string Firmware);

/// <summary>Persona tal como la tiene grabada el terminal.</summary>
public sealed record TerminalUser(
    string EmployeeNo,
    string Name,
    string UserType,
    DateTime? ValidFrom,
    DateTime? ValidTo,
    int Cards,
    /// <summary>Cuántas huellas declara la ficha; null si el firmware no informa el campo (DS-K1T804AMF V1.4): hay que preguntar igual.</summary>
    int? Fingerprints,
    int Faces);

/// <summary>Huella leída del terminal: el dedo (1..10) y la plantilla en base64 tal como la entrega ISAPI.</summary>
public sealed record TerminalFingerprint(int Finger, string Template);

/// <summary>
/// Lee el padrón de un terminal Hikvision por ISAPI: personas, tarjetas y
/// plantillas de huella. Es lo que HikCentral le bajó al equipo, y es la
/// única forma de recuperar las plantillas: la OpenAPI de HCP 3.1 no las
/// devuelve. Reutiliza el cliente ISAPI del servidor (Digest, mensajes en
/// castellano) y respeta los hallazgos que costaron contra hardware: el
/// <c>searchID</c> es una sesión de búsqueda y va NUEVO en cada consulta.
/// </summary>
public sealed class TerminalReader
{
    private readonly HikvisionIsapiClient _client;

    public TerminalReader(string host, int port, string username, string password, bool useHttps = false)
    {
        Host = host.Trim();
        _client = new HikvisionIsapiClient(new AlarmConnectionInfo(Host, port, useHttps, username, password),
            digestOnly: true, deviceNoun: "terminal");
    }

    public string Host { get; }

    /// <summary>Identifica el equipo. Lanza <see cref="DriverException"/> si no responde o no es Hikvision.</summary>
    public async Task<TerminalInfo> ProbeAsync(CancellationToken ct)
    {
        string xml = await _client.RequestAsync(HttpMethod.Get, "/ISAPI/System/deviceInfo", ct: ct, allowNotFound: false)
                     ?? throw new DriverException("El equipo no respondió la información de dispositivo.");
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException)
        {
            throw new DriverException("El equipo respondió algo que no es ISAPI (¿es un equipo Hikvision?).");
        }
        string Value(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim() ?? "";
        return new TerminalInfo(Value("model"), Value("serialNumber"), Value("firmwareVersion"));
    }

    /// <summary>Todas las personas del equipo, de a 30 (tope habitual de los firmware).</summary>
    public async Task<List<TerminalUser>> ReadUsersAsync(IProgress<int>? progress, CancellationToken ct)
    {
        var users = new List<TerminalUser>();
        const int pageSize = 30;
        int position = 0;
        while (true)
        {
            string body = JsonSerializer.Serialize(new
            {
                UserInfoSearchCond = new { searchID = SearchId(), searchResultPosition = position, maxResults = pageSize },
            });
            string json = await _client.RequestAsync(HttpMethod.Post, "/ISAPI/AccessControl/UserInfo/Search?format=json", body, ct: ct)
                          ?? throw new DriverException("El equipo no tiene la búsqueda de personas (UserInfo/Search).");
            using var doc = JsonDocument.Parse(json);
            var search = Find(doc.RootElement, "UserInfoSearch") ?? doc.RootElement;
            string status = Str(search, "responseStatusStrg") ?? "";
            int count = 0;
            if (search.TryGetProperty("UserInfo", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var u in list.EnumerateArray())
                {
                    count++;
                    DateTime? from = null, to = null;
                    if (u.TryGetProperty("Valid", out var valid) && valid.ValueKind == JsonValueKind.Object)
                    {
                        from = LocalDate(Str(valid, "beginTime"));
                        to = LocalDate(Str(valid, "endTime"));
                    }
                    users.Add(new TerminalUser(
                        Str(u, "employeeNo") ?? "",
                        Str(u, "name") ?? "",
                        Str(u, "userType") ?? "",
                        from, to,
                        Int(u, "numOfCard") ?? 0,
                        Int(u, "numOfFP"),
                        Int(u, "numOfFace") ?? 0));
                }
            progress?.Report(users.Count);
            // "MORE" = quedan más; "OK" = esta fue la última tanda; "NO MATCH" = nada.
            if (count == 0 || !status.Equals("MORE", StringComparison.OrdinalIgnoreCase)) break;
            position += count;
        }
        return users;
    }

    /// <summary>Todas las tarjetas del equipo como pares (legajo, número).</summary>
    public async Task<List<(string EmployeeNo, string CardNo)>> ReadCardsAsync(CancellationToken ct)
    {
        var cards = new List<(string, string)>();
        const int pageSize = 30;
        int position = 0;
        while (true)
        {
            string body = JsonSerializer.Serialize(new
            {
                CardInfoSearchCond = new { searchID = SearchId(), searchResultPosition = position, maxResults = pageSize },
            });
            string? json;
            try { json = await _client.RequestAsync(HttpMethod.Post, "/ISAPI/AccessControl/CardInfo/Search?format=json", body, ct: ct); }
            catch (DriverException) { break; }    // firmware sin la ruta: se queda con lo que dijo HikCentral
            if (json is null) break;
            using var doc = JsonDocument.Parse(json);
            var search = Find(doc.RootElement, "CardInfoSearch") ?? doc.RootElement;
            string status = Str(search, "responseStatusStrg") ?? "";
            int count = 0;
            if (search.TryGetProperty("CardInfo", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var c in list.EnumerateArray())
                {
                    count++;
                    string? no = Str(c, "cardNo");
                    string? emp = Str(c, "employeeNo");
                    if (!string.IsNullOrWhiteSpace(no) && !string.IsNullOrWhiteSpace(emp)) cards.Add((emp, no.Replace(" ", "")));
                }
            if (count == 0 || !status.Equals("MORE", StringComparison.OrdinalIgnoreCase)) break;
            position += count;
        }
        return cards;
    }

    /// <summary>
    /// Plantillas de huella de una persona. Verificado con un DS-K1T321MFWX
    /// (V3.9.20): si no se pide un dedo concreto el equipo contesta SOLO el
    /// primero aunque tenga varios, así que se pregunta dedo por dedo (1..10)
    /// hasta juntar los <paramref name="expected"/> que declara la ficha. Sin
    /// lector concreto (pedir uno acota la respuesta y hay equipos que
    /// contestan 400) y con <c>searchID</c> nuevo en cada consulta: repetir
    /// uno usado devuelve "NoFP" a quien sí tiene. Devuelve null si el
    /// equipo no expone la ruta.
    /// </summary>
    public async Task<List<TerminalFingerprint>?> ReadFingerprintsAsync(string employeeNo, int? expected, CancellationToken ct)
    {
        var result = new List<TerminalFingerprint>();
        for (int finger = 1; finger <= 10; finger++)
        {
            if (expected is { } n && result.Count >= n) break;
            string body = JsonSerializer.Serialize(new
            {
                FingerPrintCond = new { searchID = SearchId(), searchResultPosition = 0, maxResults = 10, employeeNo, fingerPrintID = finger },
            });
            string? json;
            try { json = await _client.RequestAsync(HttpMethod.Post, "/ISAPI/AccessControl/FingerPrintUpload?format=json", body, ct: ct); }
            catch (DriverException ex) when (ex.Message.Contains("notSupport", StringComparison.OrdinalIgnoreCase)) { return null; }
            if (json is null) return null;

            using var doc = JsonDocument.Parse(json);
            var info = Find(doc.RootElement, "FingerPrintInfo");
            if (info is null) continue;
            string status = Str(info.Value, "status") ?? "";
            if (status.Equals("NoFP", StringComparison.OrdinalIgnoreCase)) continue;
            if (!status.Equals("OK", StringComparison.OrdinalIgnoreCase))
                throw new DriverException($"El equipo respondió '{status}' al pedir la huella {finger} de {employeeNo}.");

            if (info.Value.TryGetProperty("FingerPrintList", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var f in list.EnumerateArray())
                {
                    int? id = Int(f, "fingerPrintID");
                    string? data = Str(f, "fingerData");
                    // Firmware viejo (DS-K1T804AMF V1.4): ignora el dedo pedido y
                    // contesta siempre el primero, con su propio fingerPrintID=1. Se
                    // deduplica por número Y por contenido: dos dedos distintos nunca
                    // tienen la misma plantilla.
                    if (id is null || string.IsNullOrWhiteSpace(data)) continue;
                    string template = data.Trim();
                    if (result.Any(r => r.Finger == id || r.Template == template)) continue;
                    result.Add(new TerminalFingerprint(id.Value, template));
                }
        }
        return result;
    }

    // ------------------------------------------------------------------

    /// <summary>16 caracteres hexadecimales: lo que aceptan los firmware probados (hay tope de largo).</summary>
    private static string SearchId() => Guid.NewGuid().ToString("N")[..16];

    private static DateTime? LocalDate(string? value) =>
        DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.AssumeLocal, out var d) ? d.ToUniversalTime() : null;

    private static JsonElement? Find(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty(name, out var direct)) return direct;
        foreach (var prop in root.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.Object && Find(prop.Value, name) is { } nested) return nested;
        return null;
    }

    private static string? Str(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
            _ => null,
        };
    }

    private static int? Int(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
        return int.TryParse(v.ToString(), out n) ? n : null;
    }
}
