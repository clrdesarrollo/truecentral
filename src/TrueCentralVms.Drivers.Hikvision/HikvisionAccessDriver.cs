using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using EventText = (TrueCentralVms.Core.Contracts.AccessEventKind Kind,
    TrueCentralVms.Core.Contracts.AccessCredentialKind Credential, string Text);

namespace TrueCentralVms.Drivers.Hikvision;

public sealed class HikvisionAccessDriverFactory : IAccessControlDriverFactory
{
    public string DriverKey => "hikvision-isapi";
    public string DisplayName => "Hikvision DS-K (control de acceso por ISAPI)";
    public int DefaultPort => 80;
    public bool DefaultHttps => false;
    public AccessAuthMode AuthMode => AccessAuthMode.UserPassword;
    public string? Hint => "Use un usuario local del equipo (normalmente admin), el mismo de su página web.";
    public bool SupportsPersonSync => true;
    public IAccessControlDriver Create() => new HikvisionAccessDriver();
}

/// <summary>
/// Control de acceso Hikvision (familias DS-K1T terminales, DS-K2
/// controladoras y DS-K3 torniquetes) por ISAPI, sobre el mismo transporte
/// que los paneles de alarma (<see cref="HikvisionIsapiClient"/>: Digest o
/// login de sesión SHA-256 según lo que ofrezca el equipo, con detección de
/// bloqueo por intentos fallidos).
///
/// Rutas que usa el administrador de dispositivos:
/// <list type="bullet">
/// <item><c>/ISAPI/System/deviceInfo</c> (XML) — identificación: modelo,
/// serie, firmware y MAC.</item>
/// <item><c>/ISAPI/AccessControl/Door/param/capabilities</c> — cuántas
/// puertas administra el equipo; <c>/ISAPI/AccessControl/Door/param/{n}</c>
/// entrega el nombre configurado de cada una.</item>
/// <item><c>…/RemoteControl/door/capabilities</c>, <c>…/AcsEvent/capabilities</c>,
/// <c>…/CardInfo/capabilities</c>, <c>…/FingerPrintCfg/capabilities</c> y
/// <c>/ISAPI/Intelligent/FDLib/capabilities</c> — qué sabe hacer, para que la
/// interfaz no ofrezca funciones que el equipo no tiene.</item>
/// </list>
/// Los firmware varían bastante en qué rutas exponen, así que TODAS las
/// capacidades se consultan de forma tolerante (un 404 significa "no lo
/// soporta", no un error): lo único obligatorio es responder deviceInfo y
/// alguna ruta de <c>/ISAPI/AccessControl</c>, que es lo que distingue a un
/// equipo de control de acceso de una cámara o un DVR.
/// </summary>
public sealed class HikvisionAccessDriver : IAccessControlDriver
{
    /// <summary>Cómo se nombra el equipo en los mensajes de error del transporte.</summary>
    private const string Noun = "equipo de control de acceso";

    /// <summary>Tope de puertas que se leen del equipo (la controladora más grande de la familia administra 4).</summary>
    private const int MaxDoors = 16;

    private static AlarmConnectionInfo Transport(AccessConnectionInfo info) =>
        // El transporte ISAPI se comparte con los paneles de alarma; su registro
        // de conexión es el mismo dato (host, puerto, credenciales).
        new(info.Host, info.Port, info.UseHttps, info.Username, info.Password);

    /// <summary>
    /// Cliente ISAPI para esta familia, forzado a DIGEST.
    ///
    /// Los paneles de alarma prefieren el login de sesión porque rechazan el
    /// digest, pero en los terminales DS-K es al revés: el digest anda de una y
    /// su login de sesión es extraordinariamente quisquilloso — verificado
    /// contra un DS-K1T804AMF, devuelve 401 con la clave correcta si la
    /// petición lleva CUALQUIER cabecera Accept o User-Agent, si el
    /// Content-Type trae charset, si la URL trae ?timeStamp=, o si el reto no
    /// es el de la última consulta a capabilities.
    ///
    /// Con digest se evitan las cuatro trampas de una vez. Y si algún firmware
    /// rechazara el digest, el transporte cae solo al login de sesión ante el
    /// primer 401, así que no se pierde nada.
    /// </summary>
    private static HikvisionIsapiClient Client(AccessConnectionInfo info) =>
        new(Transport(info), digestOnly: true, deviceNoun: Noun);

    /// <summary>Olvida la conexión cacheada (credenciales cambiadas o equipo eliminado).</summary>
    public static void Forget(AccessConnectionInfo info) => HikvisionIsapiClient.Forget(Transport(info));

    public void ForgetCachedSession(AccessConnectionInfo info) => Forget(info);

    // Esta familia sí acepta el padrón del VMS y sí informa el estado de sus puertas.
    public bool SupportsPersonSync => true;
    public bool SupportsFingerprintSync => true;
    public bool SupportsFaceSync => true;
    public bool SupportsCardCapture => true;
    public bool SupportsDoorStatus => true;
    public bool SupportsEventStream => true;

    /// <summary>
    /// Clasifica un modelo Hikvision como equipo de control de acceso;
    /// null = no lo es (o no lo soporta este driver). Prefijos de la familia
    /// DS-K: 1T/1A terminales de acceso y asistencia, 2600/2700/2800
    /// controladoras, 3B/3G/3T torniquetes y barreras, 5 tótems faciales. La
    /// intercomunicación (DS-KH/KV/KD/KB) queda fuera: habla otro subsistema.
    /// La usa también el descubrimiento SADP para listar solo lo compatible.
    /// </summary>
    public static AccessDeviceKind? ClassifyModel(string? model)
    {
        string m = (model ?? "").Trim().ToUpperInvariant().Replace(" ", "");
        if (m.Length == 0) return null;
        if (m.StartsWith("IDS-")) m = m[1..];           // iDS-K1T… es la misma familia
        if (!m.StartsWith("DS-K")) return null;

        string rest = m[4..];
        if (rest.StartsWith('H') || rest.StartsWith('V') || rest.StartsWith('D') || rest.StartsWith('B'))
            return null;                                // videoportero / intercomunicación
        if (rest.StartsWith('1')) return AccessDeviceKind.Terminal;
        if (rest.StartsWith('5')) return AccessDeviceKind.Terminal;
        if (rest.StartsWith('2')) return AccessDeviceKind.Controller;
        if (rest.StartsWith('3')) return AccessDeviceKind.Turnstile;
        return null;                                    // DS-K4 (cerraduras, sin red) y lo que no se conoce
    }

    // ------------------------------------------------------------------
    // Identificación
    // ------------------------------------------------------------------

    public async Task<AccessDeviceInfo> ProbeAsync(AccessConnectionInfo info, CancellationToken ct = default)
    {
        var client = Client(info);

        string xml = await client.RequestAsync(HttpMethod.Get, "/ISAPI/System/deviceInfo", ct: ct, allowNotFound: false)
                     ?? throw new DriverException("El equipo no respondió la información de dispositivo.");
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException)
        {
            throw new DriverException("El equipo respondió algo que no es ISAPI (¿es un equipo Hikvision?).");
        }
        string? Value(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim();

        string? model = Value("model");

        // Confirmar que es control de acceso: una cámara o un DVR también
        // responden deviceInfo, pero no tienen el subsistema de puertas.
        string? doorCaps = await client.RequestAsync(HttpMethod.Get, "/ISAPI/AccessControl/Door/param/capabilities?format=json", ct: ct);
        string? acsCaps = doorCaps is null
            ? await client.RequestAsync(HttpMethod.Get, "/ISAPI/AccessControl/capabilities?format=json", ct: ct)
            : null;
        bool remoteControl = await AnswersAsync(client, "/ISAPI/AccessControl/RemoteControl/door/capabilities?format=json", ct);
        if (doorCaps is null && acsCaps is null && !remoteControl)
            throw new DriverException(
                $"El equipo ({model ?? "modelo desconocido"}) responde ISAPI pero no es un equipo de control de acceso " +
                "(no expone /ISAPI/AccessControl).");

        int doorCount = DoorCountOf(doorCaps) ?? DoorCountOf(acsCaps) ?? 1;
        var doors = await ReadDoorsAsync(client, doorCount, ct);

        var capabilities = new AccessCapabilities(
            DoorCount: doors.Count,
            SupportsRemoteControl: remoteControl,
            SupportsEvents: await AnswersAsync(client, "/ISAPI/AccessControl/AcsEvent/capabilities?format=json", ct),
            SupportsCards: await AnswersAsync(client, "/ISAPI/AccessControl/CardInfo/capabilities?format=json", ct),
            SupportsFingerprint: await AnswersAsync(client, "/ISAPI/AccessControl/FingerPrintCfg/capabilities?format=json", ct),
            SupportsFace: await AnswersAsync(client, "/ISAPI/Intelligent/FDLib/capabilities?format=json", ct),
            UserCapacity: await CapacityAsync(client, "/ISAPI/AccessControl/UserInfo/capabilities?format=json", ct),
            CardCapacity: await CapacityAsync(client, "/ISAPI/AccessControl/CardInfo/capabilities?format=json", ct));

        var kind = ClassifyModel(model) ?? KindOfDoorCount(doors.Count);
        return new AccessDeviceInfo(model, Value("serialNumber"), Value("firmwareVersion"), Value("deviceType"),
            Value("macAddress"), kind, capabilities, doors);
    }

    public async Task PingAsync(AccessConnectionInfo info, CancellationToken ct = default)
    {
        var client = Client(info);
        _ = await client.RequestAsync(HttpMethod.Get, "/ISAPI/System/deviceInfo", ct: ct, allowNotFound: false)
            ?? throw new DriverException("El equipo no respondió la información de dispositivo.");
    }

    // ------------------------------------------------------------------
    // Lectura de capacidades (tolerante: 404 = no soportado)
    // ------------------------------------------------------------------

    /// <summary>true si el equipo contesta esa ruta (la función existe).</summary>
    private static async Task<bool> AnswersAsync(HikvisionIsapiClient client, string path, CancellationToken ct)
    {
        try { return await client.RequestAsync(HttpMethod.Get, path, ct: ct) is not null; }
        catch (DriverException) { return false; }   // 403 del usuario sin permiso, o función deshabilitada
    }

    /// <summary>Cupo declarado en una ruta de capacidades (<c>maxRecordNum</c> / <c>maxNum</c>).</summary>
    private static async Task<int?> CapacityAsync(HikvisionIsapiClient client, string path, CancellationToken ct)
    {
        string? json;
        try { json = await client.RequestAsync(HttpMethod.Get, path, ct: ct); }
        catch (DriverException) { return null; }
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FirstInt(doc.RootElement, ["maxRecordNum", "maxNum", "totalNum", "maxCardNum", "maxUserNum"], depth: 3);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Cuántas puertas administra el equipo, según su ruta de capacidades. El
    /// número viene como el máximo de <c>doorNo</c> (los firmware lo escriben
    /// como <c>@max</c>, <c>max</c> o un rango "1-4") o como <c>doorNum</c>.
    /// </summary>
    private static int? DoorCountOf(string? json)
    {
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (FirstInt(doc.RootElement, ["doorNum", "doorNumber", "doorNums"], depth: 4) is { } declared and > 0)
                return Math.Min(declared, MaxDoors);
            if (FindDoorNo(doc.RootElement, depth: 4) is { } fromRange and > 0)
                return Math.Min(fromRange, MaxDoors);
        }
        catch (JsonException) { /* respuesta que no es JSON: se asume 1 puerta */ }
        return null;
    }

    /// <summary>Máximo del campo <c>doorNo</c> en un árbol de capacidades ("@max", "max" o "1-4").</summary>
    private static int? FindDoorNo(JsonElement element, int depth)
    {
        if (depth < 0 || element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals("doorNo", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    if (HikvisionAlarmPanelDriver.GetInt(property.Value, "@max") is { } max) return max;
                    if (HikvisionAlarmPanelDriver.GetInt(property.Value, "max") is { } plain) return plain;
                    // Rango en texto: "1-4".
                    string? range = HikvisionAlarmPanelDriver.GetString(property.Value, "@range")
                                    ?? HikvisionAlarmPanelDriver.GetString(property.Value, "range");
                    if (range?.Split('-') is [_, var end] && int.TryParse(end.Trim(), out int top)) return top;
                }
            }
            if (property.Value.ValueKind == JsonValueKind.Object && FindDoorNo(property.Value, depth - 1) is { } nested)
                return nested;
        }
        return null;
    }

    /// <summary>
    /// <c>@max</c> del campo indicado, cuando el equipo lo declara como rango
    /// (<c>"enableCardReader": { "@min": 1, "@max": 1 }</c>).
    /// </summary>
    private static int? MaxOf(JsonElement element, string[] names, int depth)
    {
        if (depth < 0 || element.ValueKind != JsonValueKind.Object) return null;
        foreach (string name in names)
            if (element.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Object &&
                HikvisionAlarmPanelDriver.GetInt(field, "@max") is { } max)
                return max;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            if (MaxOf(property.Value, names, depth - 1) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>Primer entero con alguno de esos nombres en el árbol (búsqueda acotada en profundidad).</summary>
    private static int? FirstInt(JsonElement element, string[] names, int depth)
    {
        if (depth < 0 || element.ValueKind != JsonValueKind.Object) return null;
        foreach (string name in names)
            if (HikvisionAlarmPanelDriver.GetInt(element, name) is { } value)
                return value;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            if (FirstInt(property.Value, names, depth - 1) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>
    /// Nombre configurado de cada puerta, primero por JSON y si no por XML
    /// (el DS-K1T321MFWX V3.9.20 no entrega <c>doorName</c> en la forma JSON).
    /// Sin nombre del equipo queda "Puerta n": el administrador siempre
    /// muestra las puertas que el equipo dijo administrar.
    /// </summary>
    private static async Task<List<AccessDoorInfo>> ReadDoorsAsync(HikvisionIsapiClient client, int doorCount, CancellationToken ct)
    {
        var doors = new List<AccessDoorInfo>();
        for (int number = 1; number <= Math.Clamp(doorCount, 1, MaxDoors); number++)
        {
            string path = $"/ISAPI/AccessControl/Door/param/{number}";
            string? name = await DoorNameAsync(client, path + "?format=json", json: true, ct)
                           ?? await DoorNameAsync(client, path, json: false, ct);
            doors.Add(new AccessDoorInfo(number, string.IsNullOrWhiteSpace(name) ? $"Puerta {number}" : name!));
        }
        return doors;
    }

    /// <summary>Lee <c>doorName</c> de la configuración de una puerta; null si el equipo no lo entrega.</summary>
    private static async Task<string?> DoorNameAsync(HikvisionIsapiClient client, string path, bool json, CancellationToken ct)
    {
        string? body;
        try { body = await client.RequestAsync(HttpMethod.Get, path, ct: ct); }
        catch (DriverException) { return null; }   // firmware sin esa ruta o sin permiso
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            if (json)
            {
                using var doc = JsonDocument.Parse(body);
                var param = HikvisionAlarmPanelDriver.FindObject(doc.RootElement, "DoorParam") ?? doc.RootElement;
                return HikvisionAlarmPanelDriver.GetString(param, "doorName")?.Trim() is { Length: > 0 } value ? value : null;
            }
            return XDocument.Parse(body).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "doorName")?.Value?.Trim() is { Length: > 0 } xmlValue
                ? xmlValue
                : null;
        }
        catch (JsonException) { return null; }
        catch (System.Xml.XmlException) { return null; }
    }

    /// <summary>Respaldo cuando el modelo no dice nada: un equipo de varias puertas es una controladora.</summary>
    private static AccessDeviceKind KindOfDoorCount(int doorCount) =>
        doorCount > 1 ? AccessDeviceKind.Controller : AccessDeviceKind.Unknown;

    // ==================================================================
    // Órdenes sobre puertas
    // ==================================================================

    /// <summary>
    /// Abre, cierra o deja fija una puerta con
    /// <c>PUT /ISAPI/AccessControl/RemoteControl/door/{n}</c>. El cuerpo va en
    /// XML porque es la forma que aceptan TODOS los firmware de la familia
    /// (la variante JSON aparece recién en los más nuevos).
    /// </summary>
    public async Task ControlDoorAsync(AccessConnectionInfo info, int doorNumber, AccessDoorCommand command,
        CancellationToken ct = default)
    {
        string cmd = command switch
        {
            AccessDoorCommand.Open => "open",
            AccessDoorCommand.Close => "close",
            AccessDoorCommand.RemainOpen => "alwaysOpen",
            AccessDoorCommand.RemainLocked => "alwaysClose",
            _ => throw new DriverException($"Orden de puerta desconocida: {command}."),
        };
        var client = Client(info);
        string body = $"<RemoteControlDoor><cmd>{cmd}</cmd></RemoteControlDoor>";
        _ = await client.RequestAsync(HttpMethod.Put, $"/ISAPI/AccessControl/RemoteControl/door/{doorNumber}",
                body, "application/xml", ct, allowNotFound: false)
            ?? throw new DriverException("El equipo no respondió a la orden de puerta.");
    }

    /// <summary>
    /// Modo de cada puerta según <c>/ISAPI/AccessControl/AcsWorkStatus</c>, que
    /// entrega arreglos paralelos indexados por número de puerta:
    /// <c>doorStatus</c> (1 dormida, 2 mantenida abierta, 3 bloqueada, 4 normal)
    /// y <c>magneticStatus</c> (0 cerrada, 1 abierta), que es el sensor de la
    /// hoja. Un equipo que no expone la ruta devuelve la lista vacía y el
    /// servidor se queda con el modo que él mismo dejó anotado.
    /// </summary>
    public async Task<IReadOnlyList<AccessDoorStatus>> ReadDoorStatusAsync(AccessConnectionInfo info, int doorCount,
        CancellationToken ct = default)
    {
        var client = Client(info);
        string? json;
        try { json = await client.RequestAsync(HttpMethod.Get, "/ISAPI/AccessControl/AcsWorkStatus?format=json", ct: ct); }
        catch (DriverException) { return []; }
        if (json is null) return [];

        int[]? doorStatus, magnetic;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var status = HikvisionAlarmPanelDriver.FindObject(doc.RootElement, "AcsWorkStatus") ?? doc.RootElement;
            doorStatus = IntArray(status, "doorStatus");
            magnetic = IntArray(status, "magneticStatus");
        }
        catch (JsonException) { return []; }
        if (doorStatus is null && magnetic is null) return [];

        var result = new List<AccessDoorStatus>();
        for (int number = 1; number <= doorCount; number++)
        {
            int index = number - 1;
            var mode = doorStatus is not null && index < doorStatus.Length
                ? doorStatus[index] switch
                {
                    2 => AccessDoorMode.RemainOpen,
                    3 => AccessDoorMode.RemainLocked,
                    1 or 4 => AccessDoorMode.Normal,
                    _ => AccessDoorMode.Unknown,
                }
                : AccessDoorMode.Unknown;
            bool? open = magnetic is not null && index < magnetic.Length ? magnetic[index] != 0 : null;
            result.Add(new AccessDoorStatus(number, mode, open));
        }
        return result;
    }

    /// <summary>Arreglo de enteros de una propiedad; null si no está o no es un arreglo.</summary>
    private static int[]? IntArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            return null;
        return array.EnumerateArray()
            .Select(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : -1)
            .ToArray();
    }

    // ==================================================================
    // Historial de accesos
    // ==================================================================

    /// <summary>Cuántos renglones se piden por página al equipo (los firmware antiguos se atragantan con más).</summary>
    /// <summary>
    /// Cuántos eventos se piden por página. Diez, no treinta: el DS-K1T804AMF
    /// V1.4.0 declara <c>maxResults @max=10</c> en las capacidades de
    /// <c>AcsEvent</c> y rechaza con <c>badParameters</c> cualquier número
    /// mayor. Los firmware nuevos aceptan 30, pero pedir de a 10 en todos sale
    /// más barato que preguntarle a cada equipo cuánto aguanta.
    /// </summary>
    private const int EventPageSize = 10;

    /// <summary>
    /// Historial posterior a <paramref name="sinceUtc"/> con
    /// <c>POST /ISAPI/AccessControl/AcsEvent</c>, paginando mientras el equipo
    /// conteste <c>responseStatusStrg = "MORE"</c>. El equipo trabaja en SU
    /// hora local, así que el rango se le manda con desfase horario explícito
    /// y lo que devuelve se convierte a UTC.
    /// </summary>
    public async Task<IReadOnlyList<AccessEventRecord>> FetchEventsAsync(AccessConnectionInfo info, DateTime sinceUtc,
        int max, CancellationToken ct = default)
    {
        var client = Client(info);
        // El mismo id en todas las páginas (así se sigue UNA búsqueda), pero de
        // 32 caracteres: ver SearchId.
        string searchId = SearchId();
        var events = new List<AccessEventRecord>();

        for (int position = 0; events.Count < max; position += EventPageSize)
        {
            string body = JsonSerializer.Serialize(new
            {
                AcsEventCond = new
                {
                    searchID = searchId,
                    searchResultPosition = position,
                    maxResults = Math.Min(EventPageSize, max - events.Count),
                    major = 0,
                    minor = 0,
                    startTime = IsapiTime(sinceUtc),
                    endTime = IsapiTime(DateTime.UtcNow.AddMinutes(1)),
                },
            });
            string? json = await client.RequestAsync(HttpMethod.Post, "/ISAPI/AccessControl/AcsEvent?format=json",
                body, ct: ct, allowNotFound: false);
            if (string.IsNullOrWhiteSpace(json)) break;

            // Todo lo que se saque del JSON tiene que salir ANTES de que se
            // libere el documento: un JsonElement no sobrevive a su
            // JsonDocument, y leerlo después tira "Cannot access a disposed
            // object". Por eso el estado y el conteo se copian acá adentro.
            string status;
            int matches;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var search = HikvisionAlarmPanelDriver.FindObject(doc.RootElement, "AcsEvent") ?? doc.RootElement;
                foreach (var item in InfoList(search)) events.Add(ParseEvent(item));
                status = HikvisionAlarmPanelDriver.GetString(search, "responseStatusStrg") ?? "OK";
                matches = HikvisionAlarmPanelDriver.GetInt(search, "numOfMatches") ?? 0;
            }
            catch (JsonException)
            {
                throw new DriverException("El equipo respondió el historial de accesos en un formato que no se entiende.");
            }

            if (!status.Equals("MORE", StringComparison.OrdinalIgnoreCase) || matches == 0) break;
        }

        // El equipo entrega del más nuevo al más viejo en algunos firmware: el
        // servidor los quiere en orden cronológico para guardarlos de corrido.
        return events.Where(e => e.Timestamp > sinceUtc).OrderBy(e => e.Timestamp).ToList();
    }

    /// <summary>La lista de eventos de la respuesta, sin importar cómo la nombre el firmware.</summary>
    private static IEnumerable<JsonElement> InfoList(JsonElement search)
    {
        foreach (string name in (string[])["InfoList", "AcsEventInfo", "infoList"])
            if (search.ValueKind == JsonValueKind.Object &&
                search.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
                return list.EnumerateArray();
        return [];
    }

    /// <summary>Rango de tiempo como lo espera ISAPI: hora local del servidor con su desfase ("2026-09-09T08:00:00-03:00").</summary>
    private static string IsapiTime(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToLocalTime().ToString("yyyy-MM-ddTHH:mm:sszzz");

    private static AccessEventRecord ParseEvent(JsonElement item)
    {
        // La búsqueda del historial los llama major/minor; el flujo en vivo,
        // majorEventType/subEventType. Es el mismo código de evento.
        int major = HikvisionAlarmPanelDriver.GetInt(item, "major")
                    ?? HikvisionAlarmPanelDriver.GetInt(item, "majorEventType") ?? 0;
        int minor = HikvisionAlarmPanelDriver.GetInt(item, "minor")
                    ?? HikvisionAlarmPanelDriver.GetInt(item, "subEventType") ?? 0;
        string? employeeNo = HikvisionAlarmPanelDriver.GetString(item, "employeeNoString")
                             ?? HikvisionAlarmPanelDriver.GetString(item, "employeeNo");
        string? cardNo = HikvisionAlarmPanelDriver.GetString(item, "cardNo");
        var (kind, credential, text) = Classify(major, minor);

        // El modo de verificación que informa el equipo es más fiable que
        // deducir la credencial del código del evento, y existe en todos los
        // firmware con lector biométrico.
        var verified = CredentialOf(HikvisionAlarmPanelDriver.GetString(item, "currentVerifyMode"));
        if (verified != AccessCredentialKind.Unknown) credential = verified;
        else if (credential == AccessCredentialKind.Unknown && !string.IsNullOrWhiteSpace(cardNo))
            credential = AccessCredentialKind.Card;

        return new AccessEventRecord(
            Timestamp: ParseTime(HikvisionAlarmPanelDriver.GetString(item, "time")),
            DoorNumber: HikvisionAlarmPanelDriver.GetInt(item, "doorNo") ?? HikvisionAlarmPanelDriver.GetInt(item, "doorNum"),
            Kind: kind,
            Credential: credential,
            Description: text,
            EmployeeNo: string.IsNullOrWhiteSpace(employeeNo) ? null : employeeNo.Trim(),
            PersonName: HikvisionAlarmPanelDriver.GetString(item, "name")?.Trim() is { Length: > 0 } name ? name : null,
            CardNumber: string.IsNullOrWhiteSpace(cardNo) || cardNo.Trim('0').Length == 0 ? null : cardNo.Trim(),
            MajorType: major,
            MinorType: minor,
            RawJson: item.GetRawText());
    }

    /// <summary>Hora del equipo ("2026-09-09T10:15:00-03:00") a UTC; sin desfase se asume la hora del servidor.</summary>
    private static DateTime ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DateTime.UtcNow;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset))
            return offset.UtcDateTime;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
        return DateTime.UtcNow;
    }

    private static AccessCredentialKind CredentialOf(string? verifyMode)
    {
        string mode = (verifyMode ?? "").ToLowerInvariant();
        if (mode.Length == 0) return AccessCredentialKind.Unknown;
        // Los modos combinados ("cardOrFace", "cardAndPw") se resuelven por el
        // primer factor que nombran, que es el que la persona usó.
        if (mode.StartsWith("face")) return AccessCredentialKind.Face;
        if (mode.StartsWith("fp") || mode.StartsWith("finger")) return AccessCredentialKind.Fingerprint;
        if (mode.StartsWith("card")) return AccessCredentialKind.Card;
        if (mode.StartsWith("pw") || mode.Contains("password")) return AccessCredentialKind.Pin;
        if (mode.Contains("qr") || mode.Contains("code")) return AccessCredentialKind.Qr;
        return AccessCredentialKind.Unknown;
    }

    /// <summary>
    /// Traduce los códigos del fabricante a algo que un guardia pueda leer.
    ///
    /// Las cuatro tablas son las constantes <c>MAJOR_</c>/<c>MINOR_</c> del
    /// CHCNetSDK de CONTROL DE ACCESO —que no es el <c>Interop/CHCNetSDK.cs</c>
    /// de esta misma carpeta: ese es el del SDK de cámaras y grabadores, y no
    /// trae este bloque—. Los códigos se escriben EN HEXADECIMAL igual que ahí,
    /// para poder cotejarlos uno a uno sin convertir nada de cabeza (el SDK dice
    /// <c>MINOR_DOOR_BUTTON_PRESS = 0x17</c>, no 23; deducirlos en decimal fue
    /// justamente lo que corrió la tabla anterior seis códigos).
    ///
    /// Aunque las constantes vengan del SDK, los códigos son los mismos que
    /// manda ISAPI, tanto en la búsqueda del historial como en el flujo de
    /// eventos en vivo.
    ///
    /// Lo que NO esté en las tablas no se adivina: se guarda como "Otro" con su
    /// major/minor y su JSON crudo a la vista. Inventar una traducción sería
    /// peor que no traducir: mostraría un rechazo como si fuera un acceso
    /// concedido.
    /// </summary>
    private static EventText Classify(int major, int minor)
    {
        var tabla = major switch
        {
            1 => AlarmMinors,
            2 => ExceptionMinors,
            3 => OperationMinors,
            5 => EventMinors,
            _ => null,
        };
        if (tabla is not null && tabla.TryGetValue(minor, out var known)) return known;

        return major switch
        {
            1 => (AccessEventKind.Alarm, AccessCredentialKind.Unknown, $"Alarma del equipo (código {major}/{minor})"),
            2 => (AccessEventKind.Alarm, AccessCredentialKind.Unknown, $"Excepción del equipo (código {major}/{minor})"),
            3 => (AccessEventKind.Other, AccessCredentialKind.Unknown, $"Operación en el equipo (código {major}/{minor})"),
            _ => (AccessEventKind.Other, AccessCredentialKind.Unknown, $"Evento del equipo (código {major}/{minor})"),
        };
    }

    /// <summary>MAJOR_EVENT (0x5): lo que pasa EN la puerta. Es la tabla que más se ve.</summary>
    private static readonly Dictionary<int, EventText> EventMinors = new()
    {
        [0x01] = (AccessEventKind.Granted, AccessCredentialKind.Card, "Acceso concedido con tarjeta"),                                     // MINOR_LEGAL_CARD_PASS
        [0x02] = (AccessEventKind.Granted, AccessCredentialKind.Card, "Acceso concedido con tarjeta y clave"),                             // MINOR_CARD_AND_PSW_PASS
        [0x03] = (AccessEventKind.Denied, AccessCredentialKind.Card, "Tarjeta y clave: clave incorrecta"),                                 // MINOR_CARD_AND_PSW_FAIL
        [0x04] = (AccessEventKind.Denied, AccessCredentialKind.Card, "Tarjeta y clave: no alcanzó a marcar la clave"),                     // MINOR_CARD_AND_PSW_TIMEOUT
        [0x05] = (AccessEventKind.Denied, AccessCredentialKind.Card, "Tarjeta y clave: se venció el plazo de la verificación"),            // MINOR_CARD_AND_PSW_OVER_TIME
        [0x06] = (AccessEventKind.Denied, AccessCredentialKind.Card, "La tarjeta no tiene permiso en esta puerta"),                        // MINOR_CARD_NO_RIGHT
        [0x07] = (AccessEventKind.Denied, AccessCredentialKind.Card, "Fuera del horario permitido"),                                       // MINOR_CARD_INVALID_PERIOD
        [0x08] = (AccessEventKind.Denied, AccessCredentialKind.Card, "Tarjeta vencida"),                                                   // MINOR_CARD_OUT_OF_DATE
        [0x09] = (AccessEventKind.Denied, AccessCredentialKind.Card, "Tarjeta desconocida"),                                               // MINOR_INVALID_CARD
        [0x0a] = (AccessEventKind.Denied, AccessCredentialKind.Unknown, "Rechazo por antirretorno (anti-passback)"),                       // MINOR_ANTI_SNEAK_FAIL
        [0x0b] = (AccessEventKind.Denied, AccessCredentialKind.Unknown, "La otra puerta del esclusamiento está abierta"),                  // MINOR_INTERLOCK_DOOR_NOT_CLOSE
        [0x0c] = (AccessEventKind.Denied, AccessCredentialKind.Unknown, "No pertenece a ningún grupo de verificación múltiple"),           // MINOR_NOT_BELONG_MULTI_GROUP
        [0x0d] = (AccessEventKind.Denied, AccessCredentialKind.Unknown, "Fuera del horario de verificación múltiple"),                     // MINOR_INVALID_MULTI_VERIFY_PERIOD
        [0x0e] = (AccessEventKind.Denied, AccessCredentialKind.Unknown, "Verificación múltiple: falta la credencial de supervisor"),       // MINOR_MULTI_VERIFY_SUPER_RIGHT_FAIL
        [0x0f] = (AccessEventKind.Denied, AccessCredentialKind.Unknown, "Verificación múltiple: falta la autorización remota"),            // MINOR_MULTI_VERIFY_REMOTE_RIGHT_FAIL
        [0x10] = (AccessEventKind.Granted, AccessCredentialKind.Unknown, "Acceso concedido por verificación múltiple"),                    // MINOR_MULTI_VERIFY_SUCCESS
        [0x11] = (AccessEventKind.Other, AccessCredentialKind.Card, "Comenzó la apertura con tarjeta de jefatura"),                        // MINOR_LEADER_CARD_OPEN_BEGIN
        [0x12] = (AccessEventKind.Other, AccessCredentialKind.Card, "Terminó la apertura con tarjeta de jefatura"),                        // MINOR_LEADER_CARD_OPEN_END
        [0x13] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "La puerta quedó abierta en forma permanente"),                     // MINOR_ALWAYS_OPEN_BEGIN
        [0x14] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Terminó el modo abierta permanente"),                              // MINOR_ALWAYS_OPEN_END
        [0x15] = (AccessEventKind.DoorOpen, AccessCredentialKind.Unknown, "Cerradura abierta"),                                            // MINOR_LOCK_OPEN
        [0x16] = (AccessEventKind.DoorClose, AccessCredentialKind.Unknown, "Cerradura cerrada"),                                           // MINOR_LOCK_CLOSE
        [0x17] = (AccessEventKind.DoorOpen, AccessCredentialKind.ExitButton, "Botón de salida presionado"),                                // MINOR_DOOR_BUTTON_PRESS
        [0x18] = (AccessEventKind.Other, AccessCredentialKind.ExitButton, "Botón de salida liberado"),                                     // MINOR_DOOR_BUTTON_RELEASE
        [0x19] = (AccessEventKind.DoorOpen, AccessCredentialKind.Unknown, "Puerta abierta"),                                               // MINOR_DOOR_OPEN_NORMAL
        [0x1a] = (AccessEventKind.DoorClose, AccessCredentialKind.Unknown, "Puerta cerrada"),                                              // MINOR_DOOR_CLOSE_NORMAL
        [0x1b] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Puerta forzada"),                                                  // MINOR_DOOR_OPEN_ABNORMAL
        [0x1c] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Puerta mantenida abierta demasiado tiempo"),                       // MINOR_DOOR_OPEN_TIMEOUT
        [0x1d] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se activo la salida de alarma"),                                   // MINOR_ALARMOUT_ON
        [0x1e] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se desactivo la salida de alarma"),                                // MINOR_ALARMOUT_OFF
        [0x1f] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "La puerta quedó bloqueada en forma permanente"),                   // MINOR_ALWAYS_CLOSE_BEGIN
        [0x20] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Terminó el modo bloqueada permanente"),                            // MINOR_ALWAYS_CLOSE_END
        [0x21] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Verificación múltiple: espera la apertura remota"),                // MINOR_MULTI_VERIFY_NEED_REMOTE_OPEN
        [0x22] = (AccessEventKind.Granted, AccessCredentialKind.Pin, "Verificación múltiple: clave de supervisor aceptada"),               // MINOR_MULTI_VERIFY_SUPERPASSWD_VERIFY_SUCCESS
        [0x23] = (AccessEventKind.Denied, AccessCredentialKind.Unknown, "Verificación múltiple: la misma persona se verificó dos veces"),  // MINOR_MULTI_VERIFY_REPEAT_VERIFY
        [0x24] = (AccessEventKind.Denied, AccessCredentialKind.Unknown, "Verificación múltiple: se venció el tiempo"),                     // MINOR_MULTI_VERIFY_TIMEOUT
        [0x25] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Sonó el timbre"),                                                  // MINOR_DOORBELL_RINGING
        [0x26] = (AccessEventKind.Granted, AccessCredentialKind.Fingerprint, "Acceso concedido con huella"),                               // MINOR_FINGERPRINT_COMPARE_PASS
        [0x27] = (AccessEventKind.Denied, AccessCredentialKind.Fingerprint, "Huella no reconocida"),                                       // MINOR_FINGERPRINT_COMPARE_FAIL
        [0x28] = (AccessEventKind.Granted, AccessCredentialKind.Fingerprint, "Acceso concedido con tarjeta y huella"),                     // MINOR_CARD_FINGERPRINT_VERIFY_PASS
        [0x29] = (AccessEventKind.Denied, AccessCredentialKind.Fingerprint, "Tarjeta y huella: no coinciden"),                             // MINOR_CARD_FINGERPRINT_VERIFY_FAIL
        [0x2a] = (AccessEventKind.Denied, AccessCredentialKind.Fingerprint, "Tarjeta y huella: se venció el tiempo"),                      // MINOR_CARD_FINGERPRINT_VERIFY_TIMEOUT
        [0x2b] = (AccessEventKind.Granted, AccessCredentialKind.Fingerprint, "Acceso concedido con tarjeta, huella y clave"),              // MINOR_CARD_FINGERPRINT_PASSWD_VERIFY_PASS
        [0x2c] = (AccessEventKind.Denied, AccessCredentialKind.Fingerprint, "Tarjeta, huella y clave: no coinciden"),                      // MINOR_CARD_FINGERPRINT_PASSWD_VERIFY_FAIL
        [0x2d] = (AccessEventKind.Denied, AccessCredentialKind.Fingerprint, "Tarjeta, huella y clave: se venció el tiempo"),               // MINOR_CARD_FINGERPRINT_PASSWD_VERIFY_TIMEOUT
        [0x2e] = (AccessEventKind.Granted, AccessCredentialKind.Fingerprint, "Acceso concedido con huella y clave"),                       // MINOR_FINGERPRINT_PASSWD_VERIFY_PASS
        [0x2f] = (AccessEventKind.Denied, AccessCredentialKind.Fingerprint, "Huella y clave: no coinciden"),                               // MINOR_FINGERPRINT_PASSWD_VERIFY_FAIL
        [0x30] = (AccessEventKind.Denied, AccessCredentialKind.Fingerprint, "Huella y clave: se venció el tiempo"),                        // MINOR_FINGERPRINT_PASSWD_VERIFY_TIMEOUT
        [0x31] = (AccessEventKind.Denied, AccessCredentialKind.Fingerprint, "La huella no está registrada en el equipo"),                  // MINOR_FINGERPRINT_INEXISTENCE
        [0x32] = (AccessEventKind.Other, AccessCredentialKind.Card, "La tarjeta se mandó a validar a la plataforma"),                      // MINOR_CARD_PLATFORM_VERIFY
        [0x33] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Llamada a la central"),                                            // MINOR_CALL_CENTER
        [0x34] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Incendio: la puerta quedó abierta permanente"),                    // MINOR_FIRE_RELAY_TURN_ON_DOOR_ALWAYS_OPEN
        [0x35] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Incendio: la puerta volvió a su modo normal"),                     // MINOR_FIRE_RELAY_RECOVER_DOOR_RECOVER_NORMAL
        [0x36] = (AccessEventKind.Granted, AccessCredentialKind.Face, "Acceso concedido con rostro y huella"),                             // MINOR_FACE_AND_FP_VERIFY_PASS
        [0x37] = (AccessEventKind.Denied, AccessCredentialKind.Face, "Rostro y huella: no coinciden"),                                     // MINOR_FACE_AND_FP_VERIFY_FAIL
        [0x38] = (AccessEventKind.Denied, AccessCredentialKind.Face, "Rostro y huella: se venció el tiempo"),                              // MINOR_FACE_AND_FP_VERIFY_TIMEOUT
        [0x39] = (AccessEventKind.Granted, AccessCredentialKind.Face, "Acceso concedido con rostro y clave"),                              // MINOR_FACE_AND_PW_VERIFY_PASS
        [0x3a] = (AccessEventKind.Denied, AccessCredentialKind.Face, "Rostro y clave: no coinciden"),                                      // MINOR_FACE_AND_PW_VERIFY_FAIL
        [0x3b] = (AccessEventKind.Denied, AccessCredentialKind.Face, "Rostro y clave: se venció el tiempo"),                               // MINOR_FACE_AND_PW_VERIFY_TIMEOUT
        [0x3c] = (AccessEventKind.Granted, AccessCredentialKind.Face, "Acceso concedido con rostro y tarjeta"),                            // MINOR_FACE_AND_CARD_VERIFY_PASS
        [0x3d] = (AccessEventKind.Denied, AccessCredentialKind.Face, "Rostro y tarjeta: no coinciden"),                                    // MINOR_FACE_AND_CARD_VERIFY_FAIL
        [0x3e] = (AccessEventKind.Denied, AccessCredentialKind.Face, "Rostro y tarjeta: se venció el tiempo"),                             // MINOR_FACE_AND_CARD_VERIFY_TIMEOUT
        [0x3f] = (AccessEventKind.Granted, AccessCredentialKind.Face, "Acceso concedido con rostro, clave y huella"),                      // MINOR_FACE_AND_PW_AND_FP_VERIFY_PASS
        [0x40] = (AccessEventKind.Denied, AccessCredentialKind.Face, "Rostro, clave y huella: no coinciden"),                              // MINOR_FACE_AND_PW_AND_FP_VERIFY_FAIL
        [0x41] = (AccessEventKind.Denied, AccessCredentialKind.Face, "Rostro, clave y huella: se venció el tiempo"),                       // MINOR_FACE_AND_PW_AND_FP_VERIFY_TIMEOUT
        [0x42] = (AccessEventKind.Granted, AccessCredentialKind.Face, "Acceso concedido con rostro, tarjeta y huella"),                    // MINOR_FACE_CARD_AND_FP_VERIFY_PASS
        [0x43] = (AccessEventKind.Denied, AccessCredentialKind.Face, "Rostro, tarjeta y huella: no coinciden"),                            // MINOR_FACE_CARD_AND_FP_VERIFY_FAIL
        [0x44] = (AccessEventKind.Denied, AccessCredentialKind.Face, "Rostro, tarjeta y huella: se venció el tiempo"),                     // MINOR_FACE_CARD_AND_FP_VERIFY_TIMEOUT
        [0x45] = (AccessEventKind.Granted, AccessCredentialKind.Fingerprint, "Acceso concedido con número de empleado y huella"),          // MINOR_EMPLOYEENO_AND_FP_VERIFY_PASS
        [0x46] = (AccessEventKind.Denied, AccessCredentialKind.Fingerprint, "Número de empleado y huella: no coinciden"),                  // MINOR_EMPLOYEENO_AND_FP_VERIFY_FAIL
        [0x47] = (AccessEventKind.Denied, AccessCredentialKind.Fingerprint, "Número de empleado y huella: se venció el tiempo"),           // MINOR_EMPLOYEENO_AND_FP_VERIFY_TIMEOUT
        [0x48] = (AccessEventKind.Granted, AccessCredentialKind.Fingerprint, "Acceso concedido con número de empleado, huella y clave"),   // MINOR_EMPLOYEENO_AND_FP_AND_PW_VERIFY_PASS
        [0x49] = (AccessEventKind.Denied, AccessCredentialKind.Fingerprint, "Número de empleado, huella y clave: no coinciden"),           // MINOR_EMPLOYEENO_AND_FP_AND_PW_VERIFY_FAIL
        [0x4a] = (AccessEventKind.Denied, AccessCredentialKind.Fingerprint, "Número de empleado, huella y clave: se venció el tiempo"),    // MINOR_EMPLOYEENO_AND_FP_AND_PW_VERIFY_TIMEOUT
        [0x4b] = (AccessEventKind.Granted, AccessCredentialKind.Face, "Acceso concedido con rostro"),                                      // MINOR_FACE_VERIFY_PASS
        [0x4c] = (AccessEventKind.Denied, AccessCredentialKind.Face, "Rostro no reconocido"),                                              // MINOR_FACE_VERIFY_FAIL
        [0x4d] = (AccessEventKind.Granted, AccessCredentialKind.Face, "Acceso concedido con número de empleado y rostro"),                 // MINOR_EMPLOYEENO_AND_FACE_VERIFY_PASS
        [0x4e] = (AccessEventKind.Denied, AccessCredentialKind.Face, "Número de empleado y rostro: no coinciden"),                         // MINOR_EMPLOYEENO_AND_FACE_VERIFY_FAIL
        [0x4f] = (AccessEventKind.Denied, AccessCredentialKind.Face, "Número de empleado y rostro: se venció el tiempo"),                  // MINOR_EMPLOYEENO_AND_FACE_VERIFY_TIMEOUT
        [0x50] = (AccessEventKind.Denied, AccessCredentialKind.Face, "No se pudo reconocer el rostro"),                                    // MINOR_FACE_RECOGNIZE_FAIL
        [0x51] = (AccessEventKind.Other, AccessCredentialKind.Card, "Comenzó la autorización por primera tarjeta"),                        // MINOR_FIRSTCARD_AUTHORIZE_BEGIN
        [0x52] = (AccessEventKind.Other, AccessCredentialKind.Card, "Terminó la autorización por primera tarjeta"),                        // MINOR_FIRSTCARD_AUTHORIZE_END
        [0x53] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Cortocircuito en la entrada de la cerradura"),                     // MINOR_DOORLOCK_INPUT_SHORT_CIRCUIT
        [0x54] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Circuito abierto en la entrada de la cerradura"),                  // MINOR_DOORLOCK_INPUT_BROKEN_CIRCUIT
        [0x55] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Falla en la entrada de la cerradura"),                             // MINOR_DOORLOCK_INPUT_EXCEPTION
        [0x56] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Cortocircuito en el sensor de puerta"),                            // MINOR_DOORCONTACT_INPUT_SHORT_CIRCUIT
        [0x57] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Circuito abierto en el sensor de puerta"),                         // MINOR_DOORCONTACT_INPUT_BROKEN_CIRCUIT
        [0x58] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Falla en el sensor de puerta"),                                    // MINOR_DOORCONTACT_INPUT_EXCEPTION
        [0x59] = (AccessEventKind.Alarm, AccessCredentialKind.ExitButton, "Cortocircuito en el botón de salida"),                          // MINOR_OPENBUTTON_INPUT_SHORT_CIRCUIT
        [0x5a] = (AccessEventKind.Alarm, AccessCredentialKind.ExitButton, "Circuito abierto en el botón de salida"),                       // MINOR_OPENBUTTON_INPUT_BROKEN_CIRCUIT
        [0x5b] = (AccessEventKind.Alarm, AccessCredentialKind.ExitButton, "Falla en el botón de salida"),                                  // MINOR_OPENBUTTON_INPUT_EXCEPTION
        [0x5c] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Falla al abrir la cerradura"),                                     // MINOR_DOORLOCK_OPEN_EXCEPTION
        [0x5d] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "La cerradura no llegó a abrirse a tiempo"),                        // MINOR_DOORLOCK_OPEN_TIMEOUT
        [0x5e] = (AccessEventKind.Denied, AccessCredentialKind.Card, "Falta la primera tarjeta que habilita la puerta"),                   // MINOR_FIRSTCARD_OPEN_WITHOUT_AUTHORIZE
        [0x5f] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se abrió el relé de llamada de ascensor"),                         // MINOR_CALL_LADDER_RELAY_BREAK
        [0x60] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se cerró el relé de llamada de ascensor"),                         // MINOR_CALL_LADDER_RELAY_CLOSE
        [0x61] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se abrió el relé de piso automático"),                             // MINOR_AUTO_KEY_RELAY_BREAK
        [0x62] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se cerró el relé de piso automático"),                             // MINOR_AUTO_KEY_RELAY_CLOSE
        [0x63] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se abrió el relé de la botónera del ascensor"),                    // MINOR_KEY_CONTROL_RELAY_BREAK
        [0x64] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se cerró el relé de la botónera del ascensor"),                    // MINOR_KEY_CONTROL_RELAY_CLOSE
        [0x65] = (AccessEventKind.Granted, AccessCredentialKind.Pin, "Acceso concedido con número de empleado y clave"),                   // MINOR_EMPLOYEENO_AND_PW_PASS
        [0x66] = (AccessEventKind.Denied, AccessCredentialKind.Pin, "Número de empleado y clave: clave incorrecta"),                       // MINOR_EMPLOYEENO_AND_PW_FAIL
        [0x67] = (AccessEventKind.Denied, AccessCredentialKind.Pin, "Número de empleado y clave: se venció el tiempo"),                    // MINOR_EMPLOYEENO_AND_PW_TIMEOUT
        [0x68] = (AccessEventKind.Denied, AccessCredentialKind.Unknown, "No se detectó a una persona real"),                               // MINOR_HUMAN_DETECT_FAIL
        [0x69] = (AccessEventKind.Granted, AccessCredentialKind.Card, "La cédula coincide con la persona"),                                // MINOR_PEOPLE_AND_ID_CARD_COMPARE_PASS
        [0x70] = (AccessEventKind.Denied, AccessCredentialKind.Card, "La cédula no coincide con la persona"),                              // MINOR_PEOPLE_AND_ID_CARD_COMPARE_FAIL
        [0x71] = (AccessEventKind.Denied, AccessCredentialKind.Card, "Documento en lista negra"),                                          // MINOR_CERTIFICATE_BLACK_LIST
        [0x72] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Mensaje válido"),                                                  // MINOR_LEGAL_MESSAGE
        [0x73] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Mensaje inválido"),                                                // MINOR_ILLEGAL_MESSAGE
        [0x74] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Detección de dirección MAC"),                                      // MINOR_MAC_DETECT
        [0x75] = (AccessEventKind.Denied, AccessCredentialKind.Unknown, "No se pudo abrir: la puerta está en reposo"),                     // MINOR_DOOR_OPEN_OR_DORMANT_FAIL
        [0x76] = (AccessEventKind.Denied, AccessCredentialKind.Unknown, "El plan de autenticación está en reposo"),                        // MINOR_AUTH_PLAN_DORMANT_FAIL
        [0x77] = (AccessEventKind.Denied, AccessCredentialKind.Card, "Falló la verificación del cifrado de la tarjeta"),                   // MINOR_CARD_ENCRYPT_VERIFY_FAIL
        [0x78] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "El antirretorno no respondió"),                                    // MINOR_SUBMARINEBACK_REPLY_FAIL
        [0x82] = (AccessEventKind.Denied, AccessCredentialKind.Unknown, "Falló la apertura con la puerta en reposo"),                      // MINOR_DOOR_OPEN_OR_DORMANT_OPEN_FAIL
        [0x83] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Latido del equipo"),                                               // MINOR_HEART_BEAT
        [0x84] = (AccessEventKind.Denied, AccessCredentialKind.Unknown, "Falló la apertura por enlace con la puerta en reposo"),           // MINOR_DOOR_OPEN_OR_DORMANT_LINKAGE_OPEN_FAIL
        [0x85] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Pasaron dos personas con una sola credencial"),                    // MINOR_TRAILING
        [0x86] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Paso en sentido contrario"),                                       // MINOR_REVERSE_ACCESS
        [0x87] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Paso forzado"),                                                    // MINOR_FORCE_ACCESS
        [0x88] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Saltaron por encima del molinete"),                                // MINOR_CLIMBING_OVER_GATE
        [0x89] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Se demoró demasiado en pasar"),                                    // MINOR_PASSING_TIMEOUT
        [0x8a] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Alarma de intrusión"),                                             // MINOR_INTRUSION_ALARM
        [0x8b] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Pasó por el molinete libre sin autenticarse"),                     // MINOR_FREE_GATE_PASS_NOT_AUTH
        [0x8c] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Se trabó el brazo del molinete"),                                  // MINOR_DROP_ARM_BLOCK
        [0x8d] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se destrabó el brazo del molinete"),                               // MINOR_DROP_ARM_BLOCK_RESUME
        [0x8e] = (AccessEventKind.Other, AccessCredentialKind.Face, "No se pudo generar el modelo del rostro"),                            // MINOR_LOCAL_FACE_MODELING_FAIL
        [0x8f] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se detectó permanencia en la zona"),                               // MINOR_STAY_EVENT
        [0x97] = (AccessEventKind.Denied, AccessCredentialKind.Pin, "Clave incorrecta"),                                                   // MINOR_PASSWORD_MISMATCH
        [0x98] = (AccessEventKind.Denied, AccessCredentialKind.Unknown, "El número de empleado no existe en el equipo"),                   // MINOR_EMPLOYEE_NO_NOT_EXIST
        [0x99] = (AccessEventKind.Granted, AccessCredentialKind.Unknown, "Acceso concedido con verificación combinada"),                   // MINOR_COMBINED_VERIFY_PASS
        [0x9a] = (AccessEventKind.Denied, AccessCredentialKind.Unknown, "Verificación combinada: se venció el tiempo"),                    // MINOR_COMBINED_VERIFY_TIMEOUT
        [0x9b] = (AccessEventKind.Denied, AccessCredentialKind.Unknown, "La credencial usada no es la que exige la puerta"),               // MINOR_VERIFY_MODE_MISMATCH
    };

    /// <summary>MAJOR_ALARM (0x1): sabotaje, coacción, incendio y zonas de alarma.</summary>
    private static readonly Dictionary<int, EventText> AlarmMinors = new()
    {
        [0x400] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Cortocircuito en la zona de alarma"),                    // MINOR_ALARMIN_SHORT_CIRCUIT
        [0x401] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Circuito abierto en la zona de alarma"),                 // MINOR_ALARMIN_BROKEN_CIRCUIT
        [0x402] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Falla en la zona de alarma"),                            // MINOR_ALARMIN_EXCEPTION
        [0x403] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "La zona de alarma volvió a la normalidad"),              // MINOR_ALARMIN_RESUME
        [0x404] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Sabotaje: abrieron la carcasa del equipo"),              // MINOR_HOST_DESMANTLE_ALARM
        [0x405] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se cerró la carcasa del equipo"),                        // MINOR_HOST_DESMANTLE_RESUME
        [0x406] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Sabotaje: abrieron el lector"),                          // MINOR_CARD_READER_DESMANTLE_ALARM
        [0x407] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se cerró la carcasa del lector"),                        // MINOR_CARD_READER_DESMANTLE_RESUME
        [0x408] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Sabotaje: se activó el sensor de la caja"),              // MINOR_CASE_SENSOR_ALARM
        [0x409] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se normalizó el sensor de la caja"),                     // MINOR_CASE_SENSOR_RESUME
        [0x40a] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Coacción: se marcó la clave bajo amenaza"),              // MINOR_STRESS_ALARM
        [0x40b] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "La memoria de eventos sin conexion está por llenarse"),  // MINOR_OFFLINE_ECENT_NEARLY_FULL
        [0x40c] = (AccessEventKind.Alarm, AccessCredentialKind.Card, "Demasiados intentos fallidos con la tarjeta"),              // MINOR_CARD_MAX_AUTHENTICATE_FAIL
        [0x40d] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "La tarjeta SD está llena"),                              // MINOR_SD_CARD_FULL
        [0x40e] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se tomó una foto por enlace"),                           // MINOR_LINKAGE_CAPTURE_PIC
        [0x40f] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Sabotaje: abrieron el módulo de seguridad"),             // MINOR_SECURITY_MODULE_DESMANTLE_ALARM
        [0x410] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se cerró el módulo de seguridad"),                       // MINOR_SECURITY_MODULE_DESMANTLE_RESUME
        [0x411] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Comenzó una transacción POS"),                           // MINOR_POS_START_ALARM
        [0x412] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Terminó la transacción POS"),                            // MINOR_POS_END_ALARM
        [0x413] = (AccessEventKind.Other, AccessCredentialKind.Face, "La imagen del rostro es de mala calidad"),                  // MINOR_FACE_IMAGE_QUALITY_LOW
        [0x414] = (AccessEventKind.Other, AccessCredentialKind.Fingerprint, "La huella capturada es de mala calidad"),            // MINOR_FINGE_RPRINT_QUALITY_LOW
        [0x415] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Cortocircuito en la entrada de incendio"),               // MINOR_FIRE_IMPORT_SHORT_CIRCUIT
        [0x416] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Circuito abierto en la entrada de incendio"),            // MINOR_FIRE_IMPORT_BROKEN_CIRCUIT
        [0x417] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se normalizó la entrada de incendio"),                   // MINOR_FIRE_IMPORT_RESUME
        [0x418] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Se accionó el botón de incendio"),                       // MINOR_FIRE_BUTTON_TRIGGER
        [0x419] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se restableció el botón de incendio"),                   // MINOR_FIRE_BUTTON_RESUME
        [0x41a] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se accionó el botón de mantenimiento"),                  // MINOR_MAINTENANCE_BUTTON_TRIGGER
        [0x41b] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se restableció el botón de mantenimiento"),              // MINOR_MAINTENANCE_BUTTON_RESUME
        [0x41c] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Se accionó el botón de emergencia"),                     // MINOR_EMERGENCY_BUTTON_TRIGGER
        [0x41d] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se restableció el botón de emergencia"),                 // MINOR_EMERGENCY_BUTTON_RESUME
        [0x41e] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Alarma del controlador distribuido"),                    // MINOR_DISTRACT_CONTROLLER_ALARM
        [0x41f] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se normalizó el controlador distribuido"),               // MINOR_DISTRACT_CONTROLLER_RESUME
        [0x422] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Sabotaje: abrieron el controlador del molinete"),        // MINOR_CHANNEL_CONTROLLER_DESMANTLE_ALARM
        [0x423] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se cerró el controlador del molinete"),                  // MINOR_CHANNEL_CONTROLLER_DESMANTLE_RESUME
        [0x424] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Incendio en el controlador del molinete"),               // MINOR_CHANNEL_CONTROLLER_FIRE_IMPORT_ALARM
        [0x425] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se normalizó la entrada de incendio del molinete"),      // MINOR_CHANNEL_CONTROLLER_FIRE_IMPORT_RESUME
        [0x440] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "La impresora se quedó sin papel"),                       // MINOR_PRINTER_OUT_OF_PAPER
        [0x442] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "La memoria de eventos está por llenarse"),               // MINOR_LEGAL_EVENT_NEARLY_FULL
    };

    /// <summary>MAJOR_EXCEPTION (0x2): el equipo o alguno de sus periféricos fallando.</summary>
    private static readonly Dictionary<int, EventText> ExceptionMinors = new()
    {
        [0x27]  = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Se cortó la red"),                                    // MINOR_NET_BROKEN
        [0x3a]  = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Falla en el bus RS-485"),                             // MINOR_RS485_DEVICE_ABNORMAL
        [0x3b]  = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se restableció el bus RS-485"),                       // MINOR_RS485_DEVICE_REVERT
        [0x400] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "El equipo se encendió"),                              // MINOR_DEV_POWER_ON
        [0x401] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "El equipo se apagó"),                                 // MINOR_DEV_POWER_OFF
        [0x402] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "El equipo se reinició solo (watchdog)"),              // MINOR_WATCH_DOG_RESET
        [0x403] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Batería baja"),                                       // MINOR_LOW_BATTERY
        [0x404] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "La batería se normalizó"),                            // MINOR_BATTERY_RESUME
        [0x405] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Se cortó la energía de red"),                         // MINOR_AC_OFF
        [0x406] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Volvio la energía de red"),                           // MINOR_AC_RESUME
        [0x407] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Volvio la red"),                                      // MINOR_NET_RESUME
        [0x408] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Falla en la memoria interna"),                        // MINOR_FLASH_ABNORMAL
        [0x409] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "El lector se desconectó"),                            // MINOR_CARD_READER_OFFLINE
        [0x40a] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "El lector volvió a conectarse"),                      // MINOR_CARD_READER_RESUME
        [0x40b] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Se apagó la luz indicadora"),                         // MINOR_INDICATOR_LIGHT_OFF
        [0x40c] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se restableció la luz indicadora"),                   // MINOR_INDICATOR_LIGHT_RESUME
        [0x40d] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "El controlador del molinete se desconectó"),          // MINOR_CHANNEL_CONTROLLER_OFF
        [0x40e] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "El controlador del molinete volvió a conectarse"),    // MINOR_CHANNEL_CONTROLLER_RESUME
        [0x40f] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "El módulo de seguridad se desconectó"),               // MINOR_SECURITY_MODULE_OFF
        [0x410] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "El módulo de seguridad volvió a conectarse"),         // MINOR_SECURITY_MODULE_RESUME
        [0x411] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Carga de la batería baja"),                           // MINOR_BATTERY_ELECTRIC_LOW
        [0x412] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "La carga de la batería se normalizó"),                // MINOR_BATTERY_ELECTRIC_RESUME
        [0x413] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Se cortó la red del controlador local"),              // MINOR_LOCAL_CONTROL_NET_BROKEN
        [0x414] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Volvio la red del controlador local"),                // MINOR_LOCAL_CONTROL_NET_RSUME
        [0x415] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Se cortó el anillo RS-485 principal"),                // MINOR_MASTER_RS485_LOOPNODE_BROKEN
        [0x416] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se restableció el anillo RS-485 principal"),          // MINOR_MASTER_RS485_LOOPNODE_RESUME
        [0x417] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "El controlador local se desconectó"),                 // MINOR_LOCAL_CONTROL_OFFLINE
        [0x418] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "El controlador local volvió a conectarse"),           // MINOR_LOCAL_CONTROL_RESUME
        [0x419] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Se cortó el anillo RS-485 secundario"),               // MINOR_LOCAL_DOWNSIDE_RS485_LOOPNODE_BROKEN
        [0x41a] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se restableció el anillo RS-485 secundario"),         // MINOR_LOCAL_DOWNSIDE_RS485_LOOPNODE_RESUME
        [0x41b] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "El controlador distribuido se conectó"),              // MINOR_DISTRACT_CONTROLLER_ONLINE
        [0x41c] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "El controlador distribuido se desconectó"),           // MINOR_DISTRACT_CONTROLLER_OFFLINE
        [0x41d] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "El lector de cédulas no está conectado"),             // MINOR_ID_CARD_READER_NOT_CONNECT
        [0x41e] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "El lector de cédulas volvió a conectarse"),           // MINOR_ID_CARD_READER_RESUME
        [0x41f] = (AccessEventKind.Alarm, AccessCredentialKind.Fingerprint, "El módulo de huellas no está conectado"),         // MINOR_FINGER_PRINT_MODULE_NOT_CONNECT
        [0x420] = (AccessEventKind.Other, AccessCredentialKind.Fingerprint, "El módulo de huellas volvió a conectarse"),       // MINOR_FINGER_PRINT_MODULE_RESUME
        [0x421] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "La cámara no está conectada"),                        // MINOR_CAMERA_NOT_CONNECT
        [0x422] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "La cámara volvió a conectarse"),                      // MINOR_CAMERA_RESUME
        [0x423] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "El puerto serie no está conectado"),                  // MINOR_COM_NOT_CONNECT
        [0x424] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "El puerto serie volvió a conectarse"),                // MINOR_COM_RESUME
        [0x425] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "El equipo no está autorizado"),                       // MINOR_DEVICE_NOT_AUTHORIZE
        [0x426] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "El lector de cédulas con foto se conectó"),           // MINOR_PEOPLE_AND_ID_CARD_DEVICE_ONLINE
        [0x427] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "El lector de cédulas con foto se desconectó"),        // MINOR_PEOPLE_AND_ID_CARD_DEVICE_OFFLINE
        [0x428] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Se bloqueó el ingreso local por intentos fallidos"),  // MINOR_LOCAL_LOGIN_LOCK
        [0x429] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se desbloqueó el ingreso local"),                     // MINOR_LOCAL_LOGIN_UNLOCK
        [0x42a] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Se cortó la comunicación del antirretorno"),          // MINOR_SUBMARINEBACK_COMM_BREAK
        [0x42b] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se restableció la comunicación del antirretorno"),    // MINOR_SUBMARINEBACK_COMM_RESUME
        [0x42c] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Falla en el sensor del motor"),                       // MINOR_MOTOR_SENSOR_EXCEPTION
        [0x42d] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Falla en el bus CAN"),                                // MINOR_CAN_BUS_EXCEPTION
        [0x42e] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se restableció el bus CAN"),                          // MINOR_CAN_BUS_RESUME
        [0x42f] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Temperatura excesiva en el molinete"),                // MINOR_GATE_TEMPERATURE_OVERRUN
        [0x430] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Falla en el emisor infrarrojo"),                      // MINOR_IR_EMITTER_EXCEPTION
        [0x431] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se restableció el emisor infrarrojo"),                // MINOR_IR_EMITTER_RESUME
        [0x432] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Falla en la placa de luces"),                         // MINOR_LAMP_BOARD_COMM_EXCEPTION
        [0x433] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se restableció la placa de luces"),                   // MINOR_LAMP_BOARD_COMM_RESUME
        [0x434] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "Falla en el adaptador infrarrojo"),                   // MINOR_IR_ADAPTOR_COMM_EXCEPTION
        [0x435] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se restableció el adaptador infrarrojo"),             // MINOR_IR_ADAPTOR_COMM_RESUME
        [0x436] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "La impresora se conectó"),                            // MINOR_PRINTER_ONLINE
        [0x437] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "La impresora se desconectó"),                         // MINOR_PRINTER_OFFLINE
        [0x438] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "El módulo 4G se conectó"),                            // MINOR_4G_MOUDLE_ONLINE
        [0x439] = (AccessEventKind.Alarm, AccessCredentialKind.Unknown, "El módulo 4G se desconectó"),                         // MINOR_4G_MOUDLE_OFFLINE
    };

    /// <summary>MAJOR_OPERATION (0x3): lo que alguien le ORDENÓ al equipo.</summary>
    private static readonly Dictionary<int, EventText> OperationMinors = new()
    {
        [0x5a]  = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Actualización local del firmware"),                         // MINOR_LOCAL_UPGRADE
        [0x70]  = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Ingreso remoto al equipo"),                                 // MINOR_REMOTE_LOGIN
        [0x71]  = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Salida remota del equipo"),                                 // MINOR_REMOTE_LOGOUT
        [0x79]  = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Armado remoto"),                                            // MINOR_REMOTE_ARM
        [0x7a]  = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Desarmado remoto"),                                         // MINOR_REMOTE_DISARM
        [0x7b]  = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Reinicio remoto"),                                          // MINOR_REMOTE_REBOOT
        [0x7e]  = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Actualización remota del firmware"),                        // MINOR_REMOTE_UPGRADE
        [0x86]  = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se exportó la configuración"),                              // MINOR_REMOTE_CFGFILE_OUTPUT
        [0x87]  = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se importó la configuración"),                              // MINOR_REMOTE_CFGFILE_INTPUT
        [0xd6]  = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Salida de alarma activada a mano"),                         // MINOR_REMOTE_ALARMOUT_OPEN_MAN
        [0xd7]  = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Salida de alarma desactivada a mano"),                      // MINOR_REMOTE_ALARMOUT_CLOSE_MAN
        [0x400] = (AccessEventKind.DoorOpen, AccessCredentialKind.Remote, "Apertura remota"),                                        // MINOR_REMOTE_OPEN_DOOR
        [0x401] = (AccessEventKind.DoorClose, AccessCredentialKind.Remote, "Cierre remoto"),                                         // MINOR_REMOTE_CLOSE_DOOR
        [0x402] = (AccessEventKind.Other, AccessCredentialKind.Remote, "Puesta en abierta permanente en forma remota"),              // MINOR_REMOTE_ALWAYS_OPEN
        [0x403] = (AccessEventKind.Other, AccessCredentialKind.Remote, "Puesta en bloqueada permanente en forma remota"),            // MINOR_REMOTE_ALWAYS_CLOSE
        [0x404] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Puesta en hora en forma remota"),                           // MINOR_REMOTE_CHECK_TIME
        [0x405] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Puesta en hora por NTP"),                                   // MINOR_NTP_CHECK_TIME
        [0x406] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se borraron las tarjetas en forma remota"),                 // MINOR_REMOTE_CLEAR_CARD
        [0x407] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se restauró la configuración de fábrica en forma remota"),  // MINOR_REMOTE_RESTORE_CFG
        [0x408] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se armó una zona de alarma"),                               // MINOR_ALARMIN_ARM
        [0x409] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se desarmó una zona de alarma"),                            // MINOR_ALARMIN_DISARM
        [0x40a] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se restauró la configuración de fábrica en el equipo"),     // MINOR_LOCAL_RESTORE_CFG
        [0x40b] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se pidió una foto en forma remota"),                        // MINOR_REMOTE_CAPTURE_PIC
        [0x40c] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se cambió la configuración de reporte por red"),            // MINOR_MOD_NET_REPORT_CFG
        [0x40d] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se cambió la configuración de reporte por GPRS"),           // MINOR_MOD_GPRS_REPORT_PARAM
        [0x40e] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se cambió el grupo de reporte"),                            // MINOR_MOD_REPORT_GROUP_PARAM
        [0x40f] = (AccessEventKind.DoorOpen, AccessCredentialKind.Pin, "Puerta abierta con la clave de desbloqueó"),                 // MINOR_UNLOCK_PASSWORD_OPEN_DOOR
        [0x410] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Renumeración automatica"),                                  // MINOR_AUTO_RENUMBER
        [0x411] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Completado automático de la numeración"),                   // MINOR_AUTO_COMPLEMENT_NUMBER
        [0x412] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se importó un archivo de configuración"),                   // MINOR_NORMAL_CFGFILE_INPUT
        [0x413] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se exportó un archivo de configuración"),                   // MINOR_NORMAL_CFGFILE_OUTTPUT
        [0x414] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se importaron los permisos de tarjetas"),                   // MINOR_CARD_RIGHT_INPUT
        [0x415] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Se exportaron los permisos de tarjetas"),                   // MINOR_CARD_RIGHT_OUTTPUT
        [0x416] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Actualización por USB"),                                    // MINOR_LOCAL_USB_UPGRADE
        [0x417] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Una visita llamó al ascensor"),                             // MINOR_REMOTE_VISITOR_CALL_LADDER
        [0x418] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Un residente llamó al ascensor"),                           // MINOR_REMOTE_HOUSEHOLD_CALL_LADDER
        [0x419] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Armado efectivo en forma remota"),                          // MINOR_REMOTE_ACTUAL_GUARD
        [0x41a] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Desarmado efectivo en forma remota"),                       // MINOR_REMOTE_ACTUAL_UNGUARD
        [0x41b] = (AccessEventKind.Other, AccessCredentialKind.Unknown, "Falló una orden del control remoto sin código"),            // MINOR_REMOTE_CONTROL_NOT_CODE_OPER_FAILED
        [0x41c] = (AccessEventKind.DoorClose, AccessCredentialKind.Remote, "Cierre por control remoto"),                             // MINOR_REMOTE_CONTROL_CLOSE_DOOR
        [0x41d] = (AccessEventKind.DoorOpen, AccessCredentialKind.Remote, "Apertura por control remoto"),                            // MINOR_REMOTE_CONTROL_OPEN_DOOR
        [0x41e] = (AccessEventKind.Other, AccessCredentialKind.Remote, "Abierta permanente por control remoto"),                     // MINOR_REMOTE_CONTROL_ALWAYS_OPEN_DOOR
    };
    // ==================================================================
    // Eventos en vivo
    // ==================================================================

    /// <summary>
    /// Escucha <c>/ISAPI/Event/notification/alertStream</c>: el equipo deja la
    /// respuesta abierta y va empujando cada evento como una parte MIME con un
    /// JSON adentro. No hace falta configurar nada EN el equipo (a diferencia
    /// de httpHosts, que exige darle una URL nuestra y solo admite dos), y la
    /// conexión la abre el servidor, así que funciona igual detrás de NAT.
    ///
    /// Verificado contra un DS-K1T321MFWX: <c>multipart/mixed;
    /// boundary=MIME_boundary</c>, y cada parte trae <c>eventType</c> y, para lo
    /// que interesa acá, un objeto <c>AccessControllerEvent</c>.
    /// </summary>
    public async IAsyncEnumerable<AccessEventRecord> StreamEventsAsync(AccessConnectionInfo info,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var client = Client(info);
        using var response = await client.OpenStreamAsync("/ISAPI/Event/notification/alertStream", ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);

        string boundary = BoundaryOf(response.Content.Headers.ContentType?.Parameters) ?? "MIME_boundary";
        string separator = "--" + boundary;

        var buffer = new byte[8192];
        var pending = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read <= 0) yield break;      // el equipo cerró: que reconecte el llamador
            pending.Append(Encoding.UTF8.GetString(buffer, 0, read));

            while (true)
            {
                string texto = pending.ToString();
                int at = texto.IndexOf(separator, StringComparison.Ordinal);
                if (at < 0) break;
                string parte = texto[..at];
                pending.Remove(0, at + separator.Length);
                if (ParseStreamPart(parte) is { } evento) yield return evento;
            }

            // Un flujo sin separadores que crece sin límite es un equipo que
            // no habla lo que dijo: se corta en vez de comerse la memoria.
            if (pending.Length > MaxStreamBuffer) yield break;
        }
    }

    /// <summary>Tope del buffer del flujo (un evento con foto no llega a tanto).</summary>
    private const int MaxStreamBuffer = 512 * 1024;

    private static string? BoundaryOf(ICollection<System.Net.Http.Headers.NameValueHeaderValue>? parameters) =>
        parameters?.FirstOrDefault(p => p.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase))
            ?.Value?.Trim('"');

    /// <summary>
    /// Una parte del multipart a evento, o null si no es un evento de control
    /// de acceso (el equipo también empuja latidos y eventos de otros
    /// subsistemas por el mismo flujo).
    /// </summary>
    private static AccessEventRecord? ParseStreamPart(string parte)
    {
        int cuerpo = parte.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string json = (cuerpo >= 0 ? parte[(cuerpo + 4)..] : parte).Trim();
        if (json.Length < 2 || json[0] != '{') return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (HikvisionAlarmPanelDriver.FindObject(root, "AccessControllerEvent") is not { } acs) return null;

            var record = ParseEvent(acs);
            // La hora del evento va en la envoltura, no en el objeto interno.
            var cuando = ParseTime(HikvisionAlarmPanelDriver.GetString(root, "dateTime"));
            return record with { Timestamp = cuando, RawJson = json };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ==================================================================
    // Padrón: horarios, personas y credenciales
    // ==================================================================

    /// <summary>Tramos por día que admite un horario semanal del equipo.</summary>
    private const int SegmentsPerDay = 8;

    private static readonly string[] IsapiWeekDays =
        ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    /// <summary>
    /// Sube un horario a su ranura del equipo. Son DOS objetos encadenados:
    /// el <c>UserRightWeekPlanCfg</c> (los tramos de cada día) y la
    /// <c>UserRightPlanTemplate</c> que lo envuelve, que es lo que las
    /// personas referencian por número. El VMS usa el mismo número para los
    /// dos, así que la ranura 3 es siempre "el horario 3" mire donde se mire.
    ///
    /// Se escriben SIEMPRE los siete días con sus ocho tramos (los que no se
    /// usan, deshabilitados): así el equipo queda exactamente igual a lo
    /// configurado y no arrastra restos de un horario anterior.
    /// </summary>
    public async Task ApplyWeekPlanAsync(AccessConnectionInfo info, AccessWeekPlan plan, CancellationToken ct = default)
    {
        var client = Client(info);

        var days = new List<object>();
        for (int day = 0; day < 7; day++)
        {
            var segments = plan.Segments.Where(s => s.Day == day)
                .OrderBy(s => s.StartMinutes).Take(SegmentsPerDay).ToList();
            for (int slot = 0; slot < SegmentsPerDay; slot++)
            {
                var segment = slot < segments.Count ? segments[slot] : null;
                days.Add(new
                {
                    week = IsapiWeekDays[day],
                    id = slot + 1,
                    enable = segment is not null,
                    TimeSegment = new
                    {
                        beginTime = AccessTimeSegment.Hhmmss(segment?.StartMinutes ?? 0),
                        // El equipo cuenta el fin de día como 23:59:59.
                        endTime = segment is null ? "00:00:00" : EndTime(segment.EndMinutes),
                    },
                });
            }
        }

        string weekBody = JsonSerializer.Serialize(new
        {
            UserRightWeekPlanCfg = new { enable = true, WeekPlanCfg = days },
        });
        _ = await WriteAsync(client, HttpMethod.Put,
            $"/ISAPI/AccessControl/UserRightWeekPlanCfg/{plan.Number}?format=json", weekBody,
            "el horario semanal", ct);

        string templateBody = JsonSerializer.Serialize(new
        {
            UserRightPlanTemplate = new
            {
                enable = true,
                templateName = PlanLabel(plan.Name, MaxPlanNameBytes),
                weekPlanNo = plan.Number,
                holidayGroupNo = "",
            },
        });
        _ = await WriteAsync(client, HttpMethod.Put,
            $"/ISAPI/AccessControl/UserRightPlanTemplate/{plan.Number}?format=json", templateBody,
            "la plantilla de horario", ct);
    }

    /// <summary>1439 (el último minuto del día) se escribe 23:59:59, que es como el equipo dice "hasta el final".</summary>
    private static string EndTime(int minutes) =>
        minutes >= 24 * 60 - 1 ? "23:59:59" : AccessTimeSegment.Hhmmss(minutes);

    /// <summary>
    /// Recorta a lo que entra en <paramref name="maxBytes"/> BYTES de UTF-8, no
    /// en caracteres. Los equipos declaran sus topes en bytes: "Todo el día,
    /// todos los días (24/7)" recortado a 32 caracteres son 34 bytes, y el
    /// equipo lo rechaza con <c>beyondARGSRangeLimit</c>. Se corta en borde de
    /// carácter para no partir un acento por la mitad.
    /// </summary>
    private static string Truncate(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
        int chars = value.Length;
        while (chars > 0 && Encoding.UTF8.GetByteCount(value[..chars]) > maxBytes) chars--;
        return value[..chars];
    }

    /// <summary>
    /// Nombre de horario tal como lo quieren los equipos: sin acentos ni
    /// símbolos raros y dentro del tope de bytes. No lo lee nadie —es una
    /// etiqueta interna de la tabla de plantillas del equipo— y hay firmware
    /// que rechaza cualquier cosa fuera de ASCII, así que se transcribe.
    /// </summary>
    private static string PlanLabel(string value, int maxBytes)
    {
        var ascii = new StringBuilder(value.Length);
        foreach (char c in value.Normalize(NormalizationForm.FormD))
        {
            // Los acentos quedan como marcas aparte al normalizar: se descartan
            // y sobrevive la letra base (í → i).
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c) && c < 128) ascii.Append(c);
            else if (c is ' ' or '-' or '_' or '.' or '+') ascii.Append(c);
            else if (c is ',' or '/' or '(' or ')') ascii.Append(' ');
        }
        string clean = string.Join(' ', ascii.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return Truncate(clean.Length > 0 ? clean : "Horario", maxBytes);
    }

    /// <summary>Tope del nombre de plantilla de horario en los equipos (bytes).</summary>
    private const int MaxPlanNameBytes = 32;

    /// <summary>Tope del nombre de una persona en los equipos (bytes).</summary>
    private const int MaxPersonNameBytes = 32;

    /// <summary>
    /// Deja la persona escrita en el equipo. El orden importa: primero la
    /// persona (con su vigencia y sus permisos por puerta), después sus
    /// tarjetas, porque una tarjeta sin su persona el equipo la rechaza.
    /// </summary>
    public async Task ApplyPersonAsync(AccessConnectionInfo info, AccessPersonPlan plan, CancellationToken ct = default)
    {
        var client = Client(info);

        var user = new Dictionary<string, object?>
        {
            ["employeeNo"] = plan.EmployeeNo,
            // El nombre de la persona SÍ se muestra en el terminal: se conservan
            // los acentos y solo se acota al tope de bytes del equipo.
            ["name"] = Truncate(plan.Name, MaxPersonNameBytes),
            ["userType"] = "normal",
            ["Valid"] = new
            {
                enable = true,
                beginTime = LocalStamp(plan.ValidFrom),
                endTime = LocalStamp(plan.ValidTo),
                timeType = "local",
            },
            // Los firmware viejos solo entienden doorRight (la lista de
            // puertas); los nuevos, RightPlan (puerta + horario). Se mandan los
            // dos: cada uno usa el que conoce y descarta el otro.
            ["doorRight"] = string.Join(",", plan.Doors.Select(d => d.DoorNumber)),
            ["RightPlan"] = plan.Doors
                .Select(d => new { doorNo = d.DoorNumber, planTemplateNo = d.Plan.Number.ToString() })
                .ToList(),
            ["localUIRight"] = false,
            ["maxOpenDoorTime"] = 0,
        };
        if (!string.IsNullOrWhiteSpace(plan.PinCode)) user["password"] = plan.PinCode;

        string body = JsonSerializer.Serialize(new Dictionary<string, object?> { ["UserInfo"] = user });

        // Crear una persona que ya está da error; modificar una que no está,
        // también. Se pregunta primero, y si el equipo no sabe buscar se
        // intenta crear y, si no, modificar.
        bool? exists = await PersonExistsAsync(client, plan.EmployeeNo, ct);
        if (exists == true)
            await WriteUserAsync(client, HttpMethod.Put, "/ISAPI/AccessControl/UserInfo/Modify?format=json", body, ct);
        else if (exists == false)
            await WriteUserAsync(client, HttpMethod.Post, "/ISAPI/AccessControl/UserInfo/Record?format=json", body, ct);
        else
        {
            try { await WriteUserAsync(client, HttpMethod.Post, "/ISAPI/AccessControl/UserInfo/Record?format=json", body, ct); }
            catch (DriverException) { await WriteUserAsync(client, HttpMethod.Put, "/ISAPI/AccessControl/UserInfo/Modify?format=json", body, ct); }
        }

        // Las credenciales van cada una por su cuenta y NINGUNA corta a las
        // demás. Antes, una foto que el equipo no sabía modelar dejaba a la
        // persona entera como fallida aunque su tarjeta, su clave y sus huellas
        // ya estuvieran escritas: el operador veía "con problemas" y no sabía
        // que la persona sí podía entrar. Se escribe todo lo que se pueda y al
        // final se cuenta lo que falló, que igual deja el equipo en rojo pero
        // diciendo QUÉ falta.
        var problemas = new List<string>();
        foreach (var (nombre, escribir) in new (string, Func<Task>)[]
                 {
                     ("las tarjetas", () => ApplyCardsAsync(client, plan, ct)),
                     ("las huellas", () => ApplyFingerprintsAsync(client, plan, ct)),
                     ("el rostro", () => ApplyFaceAsync(client, plan, ct)),
                 })
        {
            try { await escribir(); }
            catch (DriverException ex) { problemas.Add($"{nombre}: {ex.Message}"); }
        }

        if (problemas.Count == 0) return;
        throw new DriverException(
            $"La persona quedó escrita en el equipo, pero {(problemas.Count == 1 ? "faltó" : "faltaron")} " +
            $"{string.Join(" · ", problemas)} " +
            "El resto de sus credenciales sí quedó, así que puede entrar con las que sí entraron.");
    }

    // ==================================================================
    // Leer una tarjeta en el lector
    // ==================================================================

    /// <summary>
    /// Pone el equipo a esperar una tarjeta y devuelve su número.
    ///
    /// <c>GET /ISAPI/AccessControl/CaptureCardInfo</c> NO contesta enseguida: el
    /// equipo se queda unos diez segundos esperando que alguien pase una, y
    /// recién ahí responde <c>{"CardInfo":{"cardNo":"3558822549"}}</c>. Si nadie
    /// la pasa contesta <c>deviceError</c>, que acá NO es un error sino "todavía
    /// nada": se devuelve null y el llamador vuelve a preguntar.
    ///
    /// El equipo atiende UNA captura a la vez y contesta <c>deviceBusy</c> a la
    /// segunda, así que tampoco eso es un fallo que valga la pena mostrarle a
    /// nadie: se espera y se reintenta.
    ///
    /// Sobre <c>cardReaderNo</c>: se manda cuando el llamador lo pide, pero
    /// medido contra el DS-K1T321MFWX el equipo contesta igual para el lector 1,
    /// el 2 y para uno que no existe, así que parece escuchar en todos. Por eso
    /// el panel ofrece elegir el EQUIPO y no el lector: prometer lo segundo
    /// sería prometer algo que el equipo no cumple.
    /// </summary>
    public async Task<string?> CaptureCardAsync(AccessConnectionInfo info, int? cardReaderNo = null,
        CancellationToken ct = default)
    {
        string path = "/ISAPI/AccessControl/CaptureCardInfo?format=json"
                      + (cardReaderNo is { } reader ? $"&cardReaderNo={reader}" : "");

        // allowNotFound: false a propósito. Estos equipos contestan **404** para
        // decir "no pasó nadie" (`deviceError`) y también para "ya hay otra
        // captura" (`deviceBusy`): el 404 acá NO significa que la ruta no
        // exista. Dejándolo en su valor normal, el cliente devolvía null y el
        // panel mostraba "este equipo no sabe leer una tarjeta" con el lector
        // esperando la tarjeta en la cara del operador.
        string? response;
        try
        {
            response = await Client(info).RequestAsync(HttpMethod.Get, path, ct: ct, allowNotFound: false);
        }
        catch (DriverException ex) when (IsWaitingForCard(ex.Message))
        {
            return null;   // nadie la pasó todavía, o el equipo estaba ocupado
        }
        if (response is null) return null;

        if (FailureOf(response) is { } error)
        {
            if (IsWaitingForCard(error)) return null;
            throw new DriverException($"El equipo no pudo leer la tarjeta: {error}");
        }

        try
        {
            using var doc = JsonDocument.Parse(response);
            string? number = HikvisionAlarmPanelDriver.FindObject(doc.RootElement, "CardInfo") is { } card
                ? HikvisionAlarmPanelDriver.GetString(card, "cardNo")
                : HikvisionAlarmPanelDriver.GetString(doc.RootElement, "cardNo");
            return string.IsNullOrWhiteSpace(number) ? null : number.Trim();
        }
        catch (JsonException)
        {
            throw new DriverException("El equipo contestó la lectura de la tarjeta en un formato que no se entiende.");
        }
    }

    /// <summary>
    /// "Todavía no pasó nadie": el equipo dice <c>deviceError</c> cuando se le
    /// acaba la espera sin tarjeta, y <c>deviceBusy</c> cuando ya hay otra
    /// captura corriendo. Ninguna de las dos es un fallo que mostrar.
    /// </summary>
    private static bool IsWaitingForCard(string message) =>
        message.Contains("deviceError", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Device Error", StringComparison.OrdinalIgnoreCase) ||
        IsBusy(message);

    // ==================================================================
    // Rostro
    // ==================================================================

    /// <summary>Biblioteca de rostros del terminal: la de acceso es la "blackFD" con FDID 1.</summary>
    private const string FaceLibType = "blackFD";
    private const string FaceLibId = "1";

    /// <summary>
    /// Deja en el equipo el rostro que la persona tiene en el VMS: si no tiene,
    /// borra el que hubiera.
    ///
    /// El rostro NO es una plantilla como la huella: se le manda la FOTO y el
    /// modelo lo arma el propio terminal. Por eso puede aceptar el archivo y
    /// rechazar la cara —<c>SubpicAnalysisModelingError</c> cuando no encuentra
    /// ninguna, o cuando la encuentra pero no le sirve— y por eso ese error se
    /// traduce a algo que el operador pueda accionar: el problema está en la
    /// foto, no en la conexión.
    /// </summary>
    private static async Task ApplyFaceAsync(HikvisionIsapiClient client, AccessPersonPlan plan,
        CancellationToken ct)
    {
        // Siempre se borra primero: si la persona ya no tiene rostro en el VMS,
        // el que quedó en el equipo le seguiría abriendo la puerta.
        await DeleteFaceAsync(client, plan.EmployeeNo, ct);
        if (plan.Face is not { } face) return;

        // Y se espera a que el borrado esté HECHO, por lo mismo que con las
        // huellas: el equipo lo aplica en diferido, y escribir encima antes de
        // que termine devuelve "deviceUserAlreadyExistFace".
        for (int attempt = 0; attempt < VerifyAttempts; attempt++)
        {
            if (await HasFaceAsync(client, plan.EmployeeNo, ct) is not { } left) break;
            if (!left) break;
            await Task.Delay(TimeSpan.FromMilliseconds(VerifyDelayMs), ct);
        }

        string meta = JsonSerializer.Serialize(new
        {
            faceLibType = FaceLibType,
            FDID = FaceLibId,
            FPID = plan.EmployeeNo,
            name = PlanLabel(plan.Name, 48),   // FDNameMaxLen que declara el equipo
        });

        string extension = face.ContentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
        string? response;
        try
        {
            response = await client.RequestMultipartAsync(
                "/ISAPI/Intelligent/FDLib/FaceDataRecord?format=json",
                "FaceDataRecord", meta,
                "img", $"{plan.EmployeeNo}.{extension}", face.Image, face.ContentType, ct);
        }
        catch (DriverException ex) when (IsFaceQuality(ex.Message))
        {
            throw new DriverException(FaceQualityMessage(plan.Name));
        }

        if (response is null)
            throw new DriverException("Este equipo no acepta rostros desde el VMS: no tiene la biblioteca de caras.");
        if (FailureOf(response) is { } error)
            throw new DriverException(
                IsFaceQuality(error) ? FaceQualityMessage(plan.Name)
                : error.Contains("AlreadyExistFace", StringComparison.OrdinalIgnoreCase)
                    ? $"El equipo todavía tiene el rostro anterior de {plan.Name} y no acepta el nuevo. " +
                      "Vuelva a enviarla en un momento."
                    : $"El equipo rechazó la foto de {plan.Name}: {error}");

        // Y se comprueba, por lo mismo que las huellas: un "OK" no prueba que
        // haya quedado guardado. El equipo aplica en diferido, así que se
        // reintenta antes de dar el rostro por perdido.
        for (int attempt = 0; attempt < VerifyAttempts; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(VerifyDelayMs), ct);
            if (await HasFaceAsync(client, plan.EmployeeNo, ct) is not { } stored) return;   // no sabe contestar
            if (stored) return;
        }
        throw new DriverException(
            $"El equipo aceptó la foto de {plan.Name} pero no la guardó: dijo que sí y quedó sin rostro.");
    }

    /// <summary>
    /// El equipo no pudo sacar una cara utilizable de la foto. Es el rechazo
    /// más común y no tiene nada que ver con la red, así que se dice qué hacer.
    /// </summary>
    private static bool IsFaceQuality(string message) =>
        message.Contains("SubpicAnalysisModelingError", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("faceQuality", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("lowPictureQuality", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("noFace", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("saveFacePic", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lo primero que hay que mirar es el TAMAÑO: probado contra el
    /// DS-K1T321MFWX, una foto de 135×189 px se rechaza y esa misma imagen al
    /// doble se acepta. Después vienen la pose y la luz.
    /// </summary>
    private static string FaceQualityMessage(string name) =>
        $"El equipo no pudo reconocer una cara en la foto de {name}. " +
        "Suele ser porque la foto es chica: use una de al menos 300 px de lado. " +
        "Además tiene que ser de frente, con la cara despejada y bien iluminada, " +
        "sin lentes oscuros ni gorro, y ocupando buena parte de la imagen.";

    /// <summary>
    /// Borra el rostro de la persona en el equipo. Que no tuviera ninguno no es
    /// un fallo: el objetivo era que no quede ninguno.
    /// </summary>
    private static async Task DeleteFaceAsync(HikvisionIsapiClient client, string employeeNo, CancellationToken ct)
    {
        string body = JsonSerializer.Serialize(new
        {
            FPID = new[] { new { value = employeeNo } },
        });

        try
        {
            string? response = await client.RequestAsync(HttpMethod.Put,
                $"/ISAPI/Intelligent/FDLib/FDSearch/Delete?format=json&FDID={FaceLibId}&faceLibType={FaceLibType}",
                body, ct: ct);
            if (response is null || FailureOf(response) is not { } error) return;
            if (IsMissingFace(error)) return;
            throw new DriverException($"No se pudo borrar el rostro anterior en el equipo: {error}");
        }
        catch (DriverException ex) when (IsMissingFace(ex.Message))
        {
            // No tenía rostro: es el estado que se buscaba.
        }
    }

    /// <summary>
    /// No hay nada que borrar. Incluye el "esta ruta no existe": un equipo sin
    /// biblioteca de caras no puede estar guardando un rostro, así que pedirle
    /// que lo borre no es un fallo. Sin esto, cada persona quedaba fallida en
    /// los terminales sin cámara.
    /// </summary>
    private static bool IsMissingFace(string error) =>
        UnknownRoute(error) ||
        error.Contains("notExist", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("NO MATCH", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("noRecord", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("invalidFPID", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ¿Está el rostro de la persona en el equipo? null si no sabe contestar.
    /// Sirve para comprobar lo escrito, igual que con las huellas.
    /// </summary>
    private static async Task<bool?> HasFaceAsync(HikvisionIsapiClient client, string employeeNo,
        CancellationToken ct)
    {
        string body = JsonSerializer.Serialize(new
        {
            searchResultPosition = 0,
            maxResults = 1,
            faceLibType = FaceLibType,
            FDID = FaceLibId,
            FPID = employeeNo,
            searchID = SearchId(),
        });

        string? response;
        try { response = await client.RequestAsync(HttpMethod.Post, "/ISAPI/Intelligent/FDLib/FDSearch?format=json", body, ct: ct); }
        catch (DriverException) { return null; }
        if (response is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(response);
            return (HikvisionAlarmPanelDriver.GetInt(doc.RootElement, "numOfMatches") ?? 0) > 0;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Deja en el equipo exactamente las huellas que tiene la persona en el
    /// VMS: borra las suyas y baja las actuales, mismo criterio que con las
    /// tarjetas (más barato que leer y comparar, y no deja viva una huella que
    /// se le quitó).
    ///
    /// <c>enableCardReader</c> dice en qué lectores del equipo queda grabada.
    /// Se le mandan todos los que el equipo declare tener; si no lo dice, uno
    /// por puerta, que es la configuración de un terminal.
    /// </summary>
    private static async Task ApplyFingerprintsAsync(HikvisionIsapiClient client, AccessPersonPlan plan,
        CancellationToken ct)
    {
        // Los lectores se averiguan primero porque el borrado también los
        // necesita: hay firmware que no borra si no se le dice de dónde.
        int[] readers = await CardReadersAsync(client, plan.Doors.Count, ct);

        // Primero se borran las que tenga. Sin huellas nuevas igual hay que
        // hacerlo: puede ser justamente que se le quitaron todas.
        await DeleteFingerprintsAsync(client, plan.EmployeeNo, readers, ct);
        if (plan.Fingerprints.Count == 0) return;

        // Y se espera a que el borrado esté HECHO antes de escribir. El equipo
        // lo aplica en diferido: contesta OK enseguida y sigue borrando por
        // dentro un par de segundos. La primera huella escrita en esa ventana
        // se la lleva puesta el borrado —quedaba siempre sin el primer dedo— y
        // el equipo no avisa nada: dice OK a las dos escrituras.
        await WaitForDeleteAsync(client, plan.EmployeeNo, ct);

        for (int i = 0; i < plan.Fingerprints.Count; i++)
        {
            // El terminal queda masticando cada huella un instante y contesta
            // "deviceBusy · leaderFP" a la que venga pegada (medido: la segunda
            // escritura seguida SIEMPRE cae ocupada). Darle ese respiro sale
            // más barato que quemar los reintentos de WriteAsync, que están
            // para el equipo ocupado de verdad, no para el ruido que hacemos
            // nosotros mismos.
            if (i > 0) await Task.Delay(TimeSpan.FromMilliseconds(FingerprintSettleMs), ct);
            await DownloadFingerprintAsync(client, plan.EmployeeNo, readers, plan.Fingerprints[i], ct);
        }

        await VerifyFingerprintsAsync(client, plan, ct);
    }

    /// <summary>
    /// Espera a que el equipo termine de borrar: pregunta hasta que conteste
    /// que la persona no tiene ninguna huella. Si no sabe contestar, se le da
    /// el mismo respiro que entre dos escrituras y se sigue.
    /// </summary>
    private static async Task WaitForDeleteAsync(HikvisionIsapiClient client, string employeeNo,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt < VerifyAttempts; attempt++)
        {
            if (await ReadFingerprintsAsync(client, employeeNo, finger: null, ct) is not { } left)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(FingerprintSettleMs), ct);
                return;
            }
            if (left.Count == 0) return;
            await Task.Delay(TimeSpan.FromMilliseconds(VerifyDelayMs), ct);
        }
    }

    /// <summary>
    /// Respiro entre dos huellas seguidas. El equipo aplica cada escritura EN
    /// DIFERIDO —tarda un par de segundos en tenerla adentro— y mientras tanto
    /// contesta "ocupado" a la siguiente.
    /// </summary>
    private const int FingerprintSettleMs = 4000;

    /// <summary>Lecturas de comprobación antes de dar una huella por perdida.</summary>
    private const int VerifyAttempts = 4;

    /// <summary>Espera antes de cada lectura de comprobación.</summary>
    private const int VerifyDelayMs = 1500;

    /// <summary>
    /// Comprueba que las huellas hayan quedado DE VERDAD en el equipo.
    ///
    /// No sobra: se vio al equipo aceptar una escritura con <c>statusCode 1
    /// (OK)</c> y quedarse igual, y el VMS marcaba a la persona como
    /// sincronizada con la mitad de sus huellas adentro. Una huella que el
    /// padrón cree escrita y el equipo no tiene es una puerta que no se abre
    /// cuando tiene que abrirse, y nadie se entera hasta que alguien se queda
    /// afuera.
    ///
    /// Se pregunta DEDO POR DEDO, no de una vez: la consulta sin filtro devuelve
    /// un solo dedo aunque la persona tenga varios, y creerle esa lista fue lo
    /// que hizo que esta comprobación acusara pérdidas que no existían.
    ///
    /// El equipo además aplica lo que se le manda EN DIFERIDO: durante un par de
    /// segundos sigue contestando el estado anterior (medido contra el
    /// DS-K1T321MFWX: recién a los 3 s aparece la huella nueva). Por eso se
    /// reintenta antes de dar nada por perdido.
    ///
    /// Si el equipo no sabe contestar no se inventa un veredicto: se deja pasar.
    /// Y si su filtro por dedo no funciona —el DS-K1T804AMF V1.4.0 lo ignora y
    /// contesta que tiene cualquier dedo que se le nombre— lo que sale es un
    /// "está todo", nunca un falso faltante: la comprobación puede quedarse
    /// corta, pero no acusa en falso.
    /// </summary>
    private static async Task VerifyFingerprintsAsync(HikvisionIsapiClient client, AccessPersonPlan plan,
        CancellationToken ct)
    {
        var pending = plan.Fingerprints.Select(f => f.Number).ToList();

        for (int attempt = 0; attempt < VerifyAttempts && pending.Count > 0; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(VerifyDelayMs), ct);
            foreach (int number in pending.ToList())
            {
                if (await ReadFingerprintsAsync(client, plan.EmployeeNo, number, ct) is not { } answer) return;
                if (answer.Count > 0) pending.Remove(number);
            }
        }

        if (pending.Count == 0) return;

        string names = string.Join(", ", pending.OrderBy(n => n).Select(FingerName));
        throw new DriverException(
            $"El equipo aceptó la escritura pero no guardó {(pending.Count == 1 ? "la huella" : "las huellas")} " +
            $"de {names}: dijo que sí y quedó sin ella{(pending.Count == 1 ? "" : "s")}. " +
            "Vuelva a enviar la persona; si se repite, el equipo puede estar sin espacio de huellas.");
    }

    /// <summary>
    /// Cómo se llama el objeto raíz del cuerpo de la bajada de huella. El
    /// DS-K1T321MFWX quiere <c>FingerPrintCfg</c> —el mismo nombre de su ruta de
    /// capacidades— y contesta <c>MessageParametersLack: FingerPrintCfg</c> si
    /// se manda con el nombre de la ruta; otros firmware documentan
    /// <c>FingerPrintDownload</c>. Se intenta el primero y, si el equipo pide el
    /// otro, se reintenta con ese.
    /// </summary>
    private static readonly string[] FingerprintBodyRoots = ["FingerPrintCfg", "FingerPrintDownload"];

    /// <summary>
    /// Baja UNA huella al equipo. Va por POST: con PUT el equipo contesta
    /// <c>methodNotAllowed</c> (verificado contra un DS-K1T321MFWX).
    /// </summary>
    private static async Task DownloadFingerprintAsync(HikvisionIsapiClient client, string employeeNo,
        int[] readers, AccessFingerprintData finger, CancellationToken ct)
    {
        string? lastError = null;
        foreach (string root in FingerprintBodyRoots)
        {
            var payload = new Dictionary<string, object?>
            {
                [root] = new
                {
                    employeeNo,
                    enableCardReader = readers,
                    fingerPrintID = finger.Number,
                    fingerType = "normalFP",
                    fingerData = finger.Template,
                },
            };
            try
            {
                await WriteAsync(client, HttpMethod.Post, "/ISAPI/AccessControl/FingerPrintDownload?format=json",
                    JsonSerializer.Serialize(payload), $"grabar la huella de {FingerName(finger.Number)}", ct);
                return;
            }
            catch (DriverException ex)
            {
                lastError = ex.Message;
                // Solo tiene sentido probar el otro nombre de objeto raíz si se
                // quejó de que FALTAN los parámetros: es lo que responde cuando
                // no lo reconoce.
                if (!ex.Message.Contains("ParametersLack", StringComparison.OrdinalIgnoreCase)) throw;
            }
        }
        throw new DriverException(lastError ?? $"No se pudo grabar la huella de {FingerName(finger.Number)}.");
    }

    /// <summary>
    /// Escritura de configuración con reintento cuando el equipo contesta
    /// "ocupado".
    ///
    /// El terminal atiende UNA operación de configuración a la vez, y el VMS le
    /// habla desde tres lados: el sondeo de estado (cada 60 s), la lectura del
    /// historial (cada 15 s) y la escritura del padrón. Que en medio de eso
    /// conteste <c>deviceBusy</c> es normal y transitorio —no es un error de
    /// contenido— así que se espera un momento y se vuelve a intentar. El
    /// <c>errorMsg</c> que acompaña al "ocupado" nombra un campo cualquiera del
    /// cuerpo (<c>leaderFP</c>, <c>fingerPrintID</c>…) y despista: no es que ese
    /// campo esté mal.
    /// </summary>
    private static async Task<string> WriteAsync(HikvisionIsapiClient client, HttpMethod method, string path,
        string body, string what, CancellationToken ct)
    {
        string? lastError = null;
        for (int attempt = 0; attempt < BusyRetries; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(BusyDelayMs * attempt), ct);

            string? response;
            try
            {
                response = await client.RequestAsync(method, path, body, ct: ct, allowNotFound: false);
            }
            catch (DriverException ex) when (IsBusy(ex.Message) && attempt < BusyRetries - 1)
            {
                lastError = ex.Message;
                continue;
            }
            if (response is null) throw new DriverException($"El equipo no respondió al {what}.");

            if (FailureOf(response) is not { } error) return response;
            lastError = error;
            if (!IsBusy(error)) throw new DriverException($"El equipo rechazó {what}: {error}");
        }
        throw new DriverException(
            $"El equipo estuvo ocupado y no aceptó {what} después de {BusyRetries} intentos. " +
            $"Vuelva a intentarlo en un momento. Detalle del equipo: {lastError}");
    }

    /// <summary>Intentos ante un "equipo ocupado" antes de darse por vencido.</summary>
    private const int BusyRetries = 4;

    /// <summary>Espera base entre reintentos; se multiplica por el número de intento.</summary>
    private const int BusyDelayMs = 900;

    /// <summary>El equipo dijo que está ocupado (transitorio), no que el contenido esté mal.</summary>
    private static bool IsBusy(string message) =>
        message.Contains("deviceBusy", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Device Busy", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rutas de borrado de huellas, en orden de preferencia. Los firmware de la
    /// familia no coinciden: el DS-K1T321MFWX expone
    /// <c>FingerPrint/Delete</c> (con barra) y responde <c>notSupport</c> a
    /// <c>FingerPrintDelete</c>, que es la que documentan otros modelos.
    /// </summary>
    private static readonly string[] FingerprintDeletePaths =
    [
        "/ISAPI/AccessControl/FingerPrint/Delete?format=json",
        "/ISAPI/AccessControl/FingerPrintDelete?format=json",
    ];

    /// <summary>
    /// Borra TODAS las huellas de una persona en el equipo.
    ///
    /// Que esto falle NO se puede pasar por alto: si el equipo se queda con una
    /// huella que en el VMS ya no está, esa huella sigue abriendo la puerta. Por
    /// eso, si ninguna de las rutas conocidas funciona, se avisa en vez de
    /// seguir de largo. Que la persona no tuviera huellas sí es correcto.
    /// </summary>
    private static async Task DeleteFingerprintsAsync(HikvisionIsapiClient client, string employeeNo,
        int[] readers, CancellationToken ct)
    {
        // Dos formas del mismo pedido. La DETALLADA nombra los lectores y los
        // diez dedos: es la que exige el DS-K1T804AMF V1.4.0 —su esquema los
        // declara y sin ellos contesta un `badParameters` que no dice cuál
        // falta— y la que hace que el borrado funcione de verdad ahí. La
        // ESCUETA es la que venía de antes y la que aceptan los firmware
        // nuevos, que ignoran esos campos. Se prueba la detallada primero
        // porque es la más específica.
        string[] bodies =
        [
            DeleteBody(employeeNo, readers, AccessFingers.Count),
            DeleteBody(employeeNo, readers: null, fingers: 0),
        ];

        // El error que se informa es el de la ruta que CONTESTÓ algo con
        // sentido, no el de la última que se probó: un equipo que no conoce la
        // segunda ruta responde "notSupport", y quedarse con eso tapa el motivo
        // real (pasó con el DS-K1T804AMF V1.4.0, que sí tiene la primera).
        string? realError = null, lastError = null;
        foreach (string path in FingerprintDeletePaths)
        foreach (string body in bodies)
        {
            // Mismo criterio que al escribir: si el equipo está ocupado se
            // espera y se reintenta ESA ruta, en vez de descartarla y pasar a
            // la siguiente (que tampoco lo va a atender).
            for (int attempt = 0; attempt < BusyRetries; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromMilliseconds(BusyDelayMs * attempt), ct);
                string? error;
                try
                {
                    string? response = await client.RequestAsync(HttpMethod.Put, path, body, ct: ct);
                    if (response is null) { lastError = "el equipo no conoce esa ruta"; break; }
                    error = FailureOf(response);
                    if (error is null) return;
                    // "No hay nada que borrar" es el resultado esperado en
                    // una persona sin huellas.
                    if (IsHarmlessDeleteError(error)) return;
                }
                catch (DriverException ex)
                {
                    error = ex.Message;
                }

                lastError = error;
                if (!UnknownRoute(error)) realError ??= error;
                if (!IsBusy(error)) break;
            }
        }

        // Antes de dar por perdida la revocación, se le pregunta al equipo qué
        // huellas tiene la persona. Si contesta que NINGUNA, el borrado falló
        // porque no había nada que borrar —el V1.4.0 contesta "badParameters" a
        // eso— y seguir es seguro: no queda ninguna huella vieja abriendo la
        // puerta. No se da por sentado: se exige que el equipo lo afirme.
        if (await ReadFingerprintsAsync(client, employeeNo, finger: null, ct) is { Count: 0 }) return;

        throw new DriverException(
            "No se pudieron borrar las huellas anteriores de la persona en el equipo, así que no se " +
            "escribieron las nuevas: una huella vieja podría seguir abriendo la puerta. " +
            $"Último detalle del equipo: {realError ?? lastError ?? "sin respuesta"}.");
    }

    /// <summary>
    /// Identificador para una búsqueda ISAPI: 16 caracteres al azar.
    ///
    /// El largo no es cosmético. Cada equipo declara su propio tope y los
    /// rechazos son mudos (<c>badParameters</c>, sin decir qué campo): el
    /// DS-K1T804AMF V1.4.0 acepta 32 en las huellas pero solo 20 en el
    /// historial, y el DS-K1T321MFWX V3.9.20 acepta 64. <c>Guid.ToString()</c>
    /// da 36 y no entra en ninguno de los dos topes del equipo viejo. Con 16
    /// entra en todos, y siguen siendo 64 bits de azar: de sobra para que dos
    /// búsquedas no se pisen.
    ///
    /// Tampoco se puede reciclar un valor fijo entre búsquedas distintas: el
    /// equipo lo trata como una SESIÓN, y al repetirlo contesta como si la
    /// búsqueda ya se hubiera agotado ("no hay nada") aunque sí haya.
    /// </summary>
    private static string SearchId() => Guid.NewGuid().ToString("N")[..16];

    /// <summary>
    /// Cuerpo del borrado de huellas. Con <paramref name="readers"/> en null
    /// sale la forma escueta (solo el legajo); con lectores sale la detallada,
    /// que además enumera los dedos 1..<paramref name="fingers"/>.
    /// </summary>
    private static string DeleteBody(string employeeNo, int[]? readers, int fingers)
    {
        var detail = new Dictionary<string, object?> { ["employeeNo"] = employeeNo };
        if (readers is { Length: > 0 })
        {
            detail["enableCardReader"] = readers;
            detail["fingerPrintID"] = Enumerable.Range(1, fingers).ToArray();
        }
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["FingerPrintDelete"] = new Dictionary<string, object?>
            {
                ["mode"] = "byEmployeeNo",
                ["EmployeeNoDetail"] = detail,
            },
        });
    }

    /// <summary>Respuesta de "esa ruta no existe acá", que no explica nada del problema.</summary>
    private static bool UnknownRoute(string error) =>
        error.Contains("notSupport", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("invalidID", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("no conoce esa ruta", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Qué contesta el equipo cuando se le pregunta por las huellas de una
    /// persona: la lista de dedos, vacía si afirma que no tiene ninguna, o
    /// <c>null</c> si no sabe contestar (y entonces no se puede afirmar nada).
    ///
    /// **La lista NO es exhaustiva**: los equipos probados devuelven UN dedo por
    /// consulta aunque la persona tenga varios, y paginar con
    /// <c>searchResultPosition</c> repite el mismo. Sirve para saber si tiene
    /// ALGUNA huella, no cuáles. Para preguntar por un dedo puntual está
    /// <paramref name="finger"/>.
    ///
    /// Se pregunta SIN <c>cardReaderNo</c> a propósito: pedirle un lector
    /// concreto acota la respuesta, y los dos equipos probados contestan 400 al
    /// lector 2 aunque declaren tenerlo. Sin acotar, contestan por la persona,
    /// que es lo que hay que saber para poder decir "no hay nada que revocar".
    ///
    /// El <c>searchID</c> va NUEVO en cada consulta, y no es un detalle: el
    /// equipo lo trata como una sesión de búsqueda, y si se le repite uno ya
    /// usado contesta <c>NoFP</c> —"no tiene huellas"— a una persona que sí las
    /// tiene. Acá esa respuesta decide si se tolera un borrado fallido, así que
    /// reciclarlo dejaría pasar por bueno justo lo que hay que atajar.
    /// </summary>
    private static async Task<IReadOnlyList<int>?> ReadFingerprintsAsync(HikvisionIsapiClient client,
        string employeeNo, int? finger, CancellationToken ct)
    {
        var cond = new Dictionary<string, object?>
        {
            ["searchID"] = SearchId(),
            ["searchResultPosition"] = 0,
            ["maxResults"] = AccessFingers.Count,
            ["employeeNo"] = employeeNo,
        };
        if (finger is { } number) cond["fingerPrintID"] = number;
        string body = JsonSerializer.Serialize(new Dictionary<string, object?> { ["FingerPrintCond"] = cond });

        string? response;
        try
        {
            response = await client.RequestAsync(HttpMethod.Post,
                "/ISAPI/AccessControl/FingerPrintUpload?format=json", body, ct: ct);
        }
        catch (DriverException) { return null; }
        if (response is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(response);
            if (HikvisionAlarmPanelDriver.FindObject(doc.RootElement, "FingerPrintInfo") is not { } info)
                return null;

            string status = HikvisionAlarmPanelDriver.GetString(info, "status") ?? "";
            if (status.Equals("NoFP", StringComparison.OrdinalIgnoreCase)) return [];
            if (!status.Equals("OK", StringComparison.OrdinalIgnoreCase)) return null;
            if (!info.TryGetProperty("FingerPrintList", out var list) || list.ValueKind != JsonValueKind.Array)
                return null;

            var fingers = new List<int>();
            foreach (var item in list.EnumerateArray())
                if (HikvisionAlarmPanelDriver.GetInt(item, "fingerPrintID") is { } id) fingers.Add(id);
            return fingers;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Un borrado que no borró nada porque no había nada: no es un fallo.</summary>
    private static bool IsHarmlessDeleteError(string error) =>
        error.Contains("notExist", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("No Match", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("noRecord", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("emptyData", StringComparison.OrdinalIgnoreCase);

    /// <summary>Nombre del dedo para los mensajes de error (el mismo orden que usa el panel).</summary>
    private static string FingerName(int number) => number switch
    {
        1 => "pulgar derecho", 2 => "índice derecho", 3 => "medio derecho",
        4 => "anular derecho", 5 => "meñique derecho", 6 => "pulgar izquierdo",
        7 => "índice izquierdo", 8 => "medio izquierdo", 9 => "anular izquierdo",
        10 => "meñique izquierdo", _ => $"dedo {number}",
    };

    /// <summary>
    /// Números de lector de tarjeta del equipo. Se leen de sus capacidades; si
    /// no las expone, se asume uno por puerta (lo normal en un terminal) y como
    /// mínimo el 1, para no mandar una lista vacía que el equipo rechazaría.
    /// </summary>
    private static async Task<int[]> CardReadersAsync(HikvisionIsapiClient client, int doorCount, CancellationToken ct)
    {
        int count = 0;
        // El esquema de huellas del propio equipo trae enableCardReader con su
        // máximo: es exactamente lo que hay que mandar y lo dice él mismo.
        foreach (var (path, names) in new (string, string[])[]
                 {
                     ("/ISAPI/AccessControl/FingerPrintCfg/capabilities?format=json", ["enableCardReader"]),
                     ("/ISAPI/AccessControl/CardReaderCfg/capabilities?format=json", ["cardReaderNum", "readerNum", "maxCardReaderNum"]),
                 })
        {
            try
            {
                if (await client.RequestAsync(HttpMethod.Get, path, ct: ct) is not { } json) continue;
                using var doc = JsonDocument.Parse(json);
                count = MaxOf(doc.RootElement, names, depth: 4) ?? FirstInt(doc.RootElement, names, depth: 4) ?? 0;
                if (count > 0) break;
            }
            catch (DriverException) { /* firmware sin esa ruta */ }
            catch (JsonException) { /* respuesta que no se entiende */ }
        }

        if (count <= 0) count = Math.Max(doorCount, 1);
        return Enumerable.Range(1, Math.Clamp(count, 1, MaxCardReaders)).ToArray();
    }

    /// <summary>Tope de lectores que se le declaran al equipo (la controladora más grande tiene 8).</summary>
    private const int MaxCardReaders = 8;

    /// <summary>
    /// Escribe la persona y revisa la respuesta: ISAPI contesta HTTP 200 con
    /// <c>statusCode</c> distinto de 1 cuando rechaza el contenido, así que un
    /// 200 no basta para dar por buena la escritura.
    /// </summary>
    private static async Task WriteUserAsync(HikvisionIsapiClient client, HttpMethod method, string path, string body,
        CancellationToken ct)
    {
        _ = await WriteAsync(client, method, path, body, "escribir la persona", ct);
    }

    /// <summary>
    /// Mensaje de error de una respuesta ISAPI, o null si salió bien. Las
    /// respuestas traen <c>statusCode</c> (1 = correcto) y a veces una lista
    /// por elemento con el motivo del rechazo.
    /// </summary>
    private static string? FailureOf(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;
        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (HikvisionAlarmPanelDriver.GetInt(root, "statusCode") is { } code && code != 1)
                return HikvisionAlarmPanelDriver.GetString(root, "statusString")
                       ?? HikvisionAlarmPanelDriver.GetString(root, "subStatusCode")
                       ?? $"código {code}";
            return null;
        }
        catch (JsonException) { return null; }   // XML o texto: el transporte ya validó el HTTP
    }

    /// <summary>¿La persona ya está en el equipo? null = el equipo no sabe buscar (firmware sin la ruta).</summary>
    private static async Task<bool?> PersonExistsAsync(HikvisionIsapiClient client, string employeeNo, CancellationToken ct)
    {
        string body = JsonSerializer.Serialize(new
        {
            UserInfoSearchCond = new
            {
                searchID = SearchId(),
                searchResultPosition = 0,
                maxResults = 1,
                EmployeeNoList = new[] { new { employeeNo } },
            },
        });
        string? json;
        try { json = await client.RequestAsync(HttpMethod.Post, "/ISAPI/AccessControl/UserInfo/Search?format=json", body, ct: ct); }
        catch (DriverException) { return null; }
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var search = HikvisionAlarmPanelDriver.FindObject(doc.RootElement, "UserInfoSearch") ?? doc.RootElement;
            string status = HikvisionAlarmPanelDriver.GetString(search, "responseStatusStrg") ?? "";
            if (status.Equals("NO MATCH", StringComparison.OrdinalIgnoreCase)) return false;
            return (HikvisionAlarmPanelDriver.GetInt(search, "numOfMatches") ?? 0) > 0;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Deja en el equipo exactamente las tarjetas que tiene la persona en el
    /// VMS: borra las suyas y vuelve a escribirlas. Es más barato que leerlas
    /// y comparar, y evita que quede viva una tarjeta que se le quitó.
    /// </summary>
    private static async Task ApplyCardsAsync(HikvisionIsapiClient client, AccessPersonPlan plan, CancellationToken ct)
    {
        string deleteBody = JsonSerializer.Serialize(new
        {
            CardInfoDelCond = new { EmployeeNoList = new[] { new { employeeNo = plan.EmployeeNo } } },
        });
        try { _ = await client.RequestAsync(HttpMethod.Put, "/ISAPI/AccessControl/CardInfo/Delete?format=json", deleteBody, ct: ct); }
        catch (DriverException) { /* no tenía tarjetas, o el firmware no expone la ruta */ }

        foreach (string card in plan.Cards)
        {
            string body = JsonSerializer.Serialize(new
            {
                CardInfo = new { employeeNo = plan.EmployeeNo, cardNo = card, cardType = "normalCard" },
            });
            _ = await WriteAsync(client, HttpMethod.Post, "/ISAPI/AccessControl/CardInfo/Record?format=json",
                body, $"grabar la tarjeta {card}", ct);
        }
    }

    /// <summary>Vigencia como la escribe ISAPI: hora local del equipo, sin desfase.</summary>
    private static string LocalStamp(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss");

    /// <summary>
    /// Borra la persona del equipo. Se borran también sus tarjetas: hay
    /// firmware que las deja huérfanas y siguen abriendo la puerta.
    /// </summary>
    public async Task RemovePersonAsync(AccessConnectionInfo info, string employeeNo, CancellationToken ct = default)
    {
        var client = Client(info);
        var list = new { EmployeeNoList = new[] { new { employeeNo } } };

        try
        {
            _ = await client.RequestAsync(HttpMethod.Put, "/ISAPI/AccessControl/CardInfo/Delete?format=json",
                JsonSerializer.Serialize(new { CardInfoDelCond = list }), ct: ct);
        }
        catch (DriverException) { /* si no tenía tarjetas, tampoco hay que borrarlas */ }

        // Las huellas se borran aparte: hay firmware que las deja vivas al
        // borrar la persona, y una huella huérfana sigue abriendo la puerta.
        // Acá sí se tolera el fallo: enseguida se borra la persona entera, que
        // es lo que de verdad le quita el acceso.
        try { await DeleteFingerprintsAsync(client, employeeNo, await CardReadersAsync(client, 1, ct), ct); }
        catch (DriverException) { /* no tenía huellas, o el firmware no sabe borrarlas */ }

        // El rostro va por su propia biblioteca: borrar la persona no lo saca.
        try { await DeleteFaceAsync(client, employeeNo, ct); }
        catch (DriverException) { /* no tenía rostro, o el equipo no tiene cámara */ }

        string? response = await client.RequestAsync(HttpMethod.Put, "/ISAPI/AccessControl/UserInfo/Delete?format=json",
            JsonSerializer.Serialize(new { UserInfoDelCond = list }), ct: ct, allowNotFound: false);
        // Que la persona no estuviera no es un error: el objetivo era que no esté.
        if (response is not null && FailureOf(response) is { } error &&
            !error.Contains("notExist", StringComparison.OrdinalIgnoreCase) &&
            !error.Contains("No Match", StringComparison.OrdinalIgnoreCase))
            throw new DriverException($"El equipo rechazó el borrado de la persona: {error}");
    }
}
