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
    int Faces)
{
    /// <summary>
    /// Clave de teclado (<c>password</c> de la ficha). La OpenAPI de HikCentral
    /// no la entrega, pero <c>UserInfo/Search</c> la devuelve en claro: es la
    /// única forma de migrarla sin volver a pedírsela a cada persona. Null si
    /// no tiene o el firmware no la informa.
    /// </summary>
    public string? Pin { get; init; }

    /// <summary>Puertas que abre y con qué plantilla de horario (<c>RightPlan</c>).</summary>
    public IReadOnlyList<TerminalRight> Rights { get; init; } = [];

    /// <summary><c>Valid.enable</c>: false = deshabilitada en el equipo (el kiosco del casino lo hace).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary><c>userVerifyMode</c> propio de la persona ("card" = solo tarjeta), o null si sigue al lector.</summary>
    public string? VerifyMode { get; init; }

    /// <summary><c>localUIRight</c>: puede entrar al menú del terminal (administrador local).</summary>
    public bool LocalAdmin { get; init; }
}

/// <summary>Permiso de una persona en el terminal: puerta y plantilla de horario (1 = 24 h de fábrica).</summary>
public sealed record TerminalRight(int DoorNo, int PlanTemplateNo);

/// <summary>Tramo de un horario leído del terminal: día 0 (domingo) a 6 y minutos desde medianoche.</summary>
public sealed record TerminalSegment(int Day, int StartMinutes, int EndMinutes);

/// <summary>
/// Plantilla de horario del terminal (<c>UserRightPlanTemplate</c>) con su
/// semana (<c>UserRightWeekPlanCfg</c>). <see cref="HolidayGroup"/> no vacío =
/// HikCentral le asoció feriados.
/// </summary>
public sealed record TerminalSchedule(int Number, string Name, IReadOnlyList<TerminalSegment> Segments, string? HolidayGroup);

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
                    bool enabled = !(u.TryGetProperty("Valid", out var v) && v.ValueKind == JsonValueKind.Object &&
                                     v.TryGetProperty("enable", out var en) && en.ValueKind == JsonValueKind.False);
                    users.Add(new TerminalUser(
                        Str(u, "employeeNo") ?? "",
                        Str(u, "name") ?? "",
                        Str(u, "userType") ?? "",
                        from, to,
                        Int(u, "numOfCard") ?? 0,
                        Int(u, "numOfFP"),
                        Int(u, "numOfFace") ?? 0)
                    {
                        Pin = Blank(Str(u, "password")),
                        Rights = ReadRights(u),
                        Enabled = enabled,
                        VerifyMode = Blank(Str(u, "userVerifyMode")),
                        LocalAdmin = u.TryGetProperty("localUIRight", out var ui) && ui.ValueKind == JsonValueKind.True,
                    });
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

    private static readonly string[] IsapiWeekDays =
        ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    /// <summary>Fin de día en la convención del VMS (su horario 24/7 termina en 23:59).</summary>
    private const int EndOfDay = 24 * 60 - 1;

    /// <summary>
    /// Plantilla de horario <paramref name="number"/> con los tramos de su
    /// semana: es lo que referencia cada persona por puerta en <c>RightPlan</c>.
    /// Son dos objetos encadenados (<c>UserRightPlanTemplate</c> →
    /// <c>UserRightWeekPlanCfg</c>), igual que cuando el VMS los escribe. Si el
    /// equipo no tiene la ruta, la 1 es la de fábrica (todo el día, todos los
    /// días) y cualquier otra se devuelve null.
    /// </summary>
    public async Task<TerminalSchedule?> ReadScheduleAsync(int number, CancellationToken ct)
    {
        string? json = await _client.RequestAsync(HttpMethod.Get, $"/ISAPI/AccessControl/UserRightPlanTemplate/{number}?format=json", ct: ct);
        if (json is null) return number == 1 ? AllDay(number, "Todo el día") : null;
        string name = $"Plantilla {number}";
        int weekPlan = number;
        string? holidays = null;
        using (var doc = JsonDocument.Parse(json))
        {
            var template = Find(doc.RootElement, "UserRightPlanTemplate") ?? doc.RootElement;
            name = Blank(Str(template, "templateName")) ?? name;
            weekPlan = Int(template, "weekPlanNo") ?? number;
            holidays = Blank(Str(template, "holidayGroupNo"));
            if (holidays is "0") holidays = null;
        }

        string? weekJson = await _client.RequestAsync(HttpMethod.Get, $"/ISAPI/AccessControl/UserRightWeekPlanCfg/{weekPlan}?format=json", ct: ct);
        if (weekJson is null) return number == 1 ? AllDay(number, name) : null;
        var segments = new List<TerminalSegment>();
        using (var doc = JsonDocument.Parse(weekJson))
        {
            var week = Find(doc.RootElement, "UserRightWeekPlanCfg") ?? doc.RootElement;
            if (week.TryGetProperty("WeekPlanCfg", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var s in list.EnumerateArray())
                {
                    if (s.TryGetProperty("enable", out var en) && en.ValueKind == JsonValueKind.False) continue;
                    int day = Array.FindIndex(IsapiWeekDays, d => d.Equals(Str(s, "week"), StringComparison.OrdinalIgnoreCase));
                    if (day < 0 || !s.TryGetProperty("TimeSegment", out var seg)) continue;
                    int? start = Minutes(Str(seg, "beginTime")), end = Minutes(Str(seg, "endTime"));
                    if (start is null || end is null || end <= start) continue;
                    segments.Add(new TerminalSegment(day, start.Value, Math.Min(end.Value, EndOfDay)));
                }
        }
        return new TerminalSchedule(number, name, segments.OrderBy(s => s.Day).ThenBy(s => s.StartMinutes).ToList(), holidays);
    }

    private static TerminalSchedule AllDay(int number, string name) =>
        new(number, name, Enumerable.Range(0, 7).Select(d => new TerminalSegment(d, 0, EndOfDay)).ToList(), null);

    /// <summary>"08:30:00" → 510; el fin de día ("23:59:59" o "24:00:00") → 1439.</summary>
    private static int? Minutes(string? hhmmss)
    {
        if (string.IsNullOrWhiteSpace(hhmmss)) return null;
        var parts = hhmmss.Trim().Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int h) || !int.TryParse(parts[1], out int m)) return null;
        int s = parts.Length > 2 && int.TryParse(parts[2], out int sec) ? sec : 0;
        int total = Math.Clamp(h * 60 + m + (s >= 59 ? 1 : 0), 0, 24 * 60);
        return total >= EndOfDay ? EndOfDay : total;
    }

    /// <summary>
    /// <c>RightPlan</c> de la ficha: por cada puerta, sus plantillas (pueden
    /// venir varias separadas por coma). Los firmware viejos que solo traen
    /// <c>doorRight</c> ("1,2") no dicen el horario: se asume la plantilla 1,
    /// que es la de fábrica.
    /// </summary>
    private static List<TerminalRight> ReadRights(JsonElement user)
    {
        var rights = new List<TerminalRight>();
        if (user.TryGetProperty("RightPlan", out var plans) && plans.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in plans.EnumerateArray())
            {
                if (Int(p, "doorNo") is not { } door) continue;
                foreach (string t in (Str(p, "planTemplateNo") ?? "1").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (int.TryParse(t, out int template) && template > 0) rights.Add(new TerminalRight(door, template));
            }
            return rights.Distinct().ToList();
        }
        foreach (string d in (Str(user, "doorRight") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(d, out int door) && door > 0) rights.Add(new TerminalRight(door, 1));
        return rights;
    }

    /// <summary>
    /// La foto con la que el terminal modeló el rostro de la persona
    /// (<c>FDLib/FDSearch</c> → <c>faceURL</c>). Suele ser la original que se
    /// enroló, más grande que la miniatura de 135×189 que entrega la OpenAPI de
    /// HikCentral; con la miniatura ampliada el equipo vuelve a modelar el
    /// rostro con menos detalle. Null si no tiene o el equipo no la entrega.
    /// </summary>
    public async Task<(byte[] Bytes, string ContentType)?> ReadFaceAsync(string employeeNo, CancellationToken ct)
    {
        string body = JsonSerializer.Serialize(new
        {
            searchResultPosition = 0,
            maxResults = 1,
            faceLibType = "blackFD",
            FDID = "1",
            FPID = employeeNo,
            searchID = SearchId(),
        });
        string? json;
        try { json = await _client.RequestAsync(HttpMethod.Post, "/ISAPI/Intelligent/FDLib/FDSearch?format=json", body, ct: ct); }
        catch (DriverException) { return null; }
        if (json is null) return null;

        string? url = null;
        using (var doc = JsonDocument.Parse(json))
            if (doc.RootElement.TryGetProperty("MatchList", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var m in list.EnumerateArray())
                {
                    url = Blank(Str(m, "faceURL"));
                    if (url is not null) break;
                }
        // La dirección la arma el equipo con su propio host: se usa solo la ruta,
        // por la misma conexión (puerto y Digest que ya funcionan).
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await _client.OpenStreamAsync(uri.PathAndQuery, limit.Token);
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(limit.Token);
            if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8) return (bytes, "image/jpeg");
            if (bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50) return (bytes, "image/png");
            return null;   // el equipo contestó otra cosa (una página de error, p. ej.)
        }
        catch (DriverException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (HttpRequestException) { return null; }
    }

    // ------------------------------------------------------------------

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

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
