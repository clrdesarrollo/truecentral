using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Driver de paneles de alarma Hikvision por ISAPI. Cubre dos generaciones
/// que hablan el mismo /ISAPI/SecurityCP con pequeñas diferencias:
///  - AX PRO / AX Hybrid PRO (DS-PWxx, DS-PHA64-LP...): login de sesión con
///    doble salt, alertStream mudo, notificación HTTP como vía de eventos.
///  - Paneles híbridos cableados de la línea clásica (DS-PHA64-W4M, firmware
///    V1.3.x, interfaz web antigua /doc/page/config.asp): Digest y sesión
///    con un salt, alertStream funcional que manda un latido "cidEvent"
///    vacío cada 2 s y pega el delimitador multipart al final del cuerpo.
/// No usa HCNetSDK: los paneles exponen todo por HTTP y el canal de eventos
/// es el flujo multipart /ISAPI/Event/notification/alertStream.
///
/// Convenciones del fabricante que este driver traduce:
///  - "subsistema" = área/partición (subSystems); "0xffffffff" = todas
///    (si el panel no lo acepta, se manda la orden por lotes con la lista
///    de áreas habilitadas, que es lo que hace su propia interfaz web).
///  - Los identificadores de zona son los que usa la API del panel
///    (empiezan en 0); se conservan tal cual porque son los que exigen las
///    órdenes de anulación.
///  - "shielded" = zona deshabilitada en el panel (no es bypass): se
///    muestra como no configurada para que no distraiga al operador.
///  - Los eventos llegan como Contact-ID (CIDEvent.code) y se traducen con
///    <see cref="ContactIdCatalog"/>.
/// </summary>
public sealed class HikvisionAlarmPanelDriver : IAlarmPanelDriver
{
    private const string AllAreas = "0xffffffff";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // ------------------------------------------------------------------
    // Sondeo
    // ------------------------------------------------------------------

    public async Task<AlarmPanelInfo> ProbeAsync(AlarmConnectionInfo info, CancellationToken ct = default)
    {
        var client = new HikvisionIsapiClient(info);
        string xml = await client.RequestAsync(HttpMethod.Get, "/ISAPI/System/deviceInfo", ct: ct, allowNotFound: false)
                     ?? throw new DriverException("El equipo no respondió la información de dispositivo.");
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException)
        {
            throw new DriverException("El equipo respondió algo que no es ISAPI (¿es un panel Hikvision?).");
        }
        string? Value(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim();

        // Confirmar que es un panel: un DVR o una cámara también responden
        // deviceInfo, pero no tienen subsistemas de intrusión.
        string? subsystems = await client.RequestAsync(HttpMethod.Get, "/ISAPI/SecurityCP/status/subSystems?format=json", ct: ct);
        if (subsystems is null)
            throw new DriverException(
                $"El equipo ({Value("model") ?? "modelo desconocido"}) responde ISAPI pero no es un panel de alarma " +
                "(no expone /ISAPI/SecurityCP).");

        return new AlarmPanelInfo(Value("model"), Value("serialNumber"), Value("firmwareVersion"), Value("deviceType"));
    }

    // ------------------------------------------------------------------
    // Estado de áreas y zonas
    // ------------------------------------------------------------------

    public async Task<AlarmPanelState> GetStateAsync(AlarmConnectionInfo info, CancellationToken ct = default)
    {
        var client = new HikvisionIsapiClient(info);

        string subsystemsJson = await client.RequestAsync(HttpMethod.Get, "/ISAPI/SecurityCP/status/subSystems?format=json", ct: ct)
                                ?? throw new DriverException("El panel no expone el estado de áreas (/ISAPI/SecurityCP/status/subSystems).");
        string zonesJson = await client.RequestAsync(HttpMethod.Get, "/ISAPI/SecurityCP/status/zones?format=json", ct: ct)
                           ?? "{}";

        // Nombres configurados (opcionales: si el firmware no los expone, se
        // usan los del estado o uno genérico).
        var areaNames = new Dictionary<int, string>();
        var zoneNames = new Dictionary<int, (string? Name, string? ZoneType, string? DetectorType, int? Area)>();
        try
        {
            string? cfg = await client.RequestAsync(HttpMethod.Get, "/ISAPI/SecurityCP/Configuration/subSys?format=json", ct: ct);
            if (cfg is not null) ParseAreaNames(cfg, areaNames);
        }
        catch (DriverException) { /* opcional */ }
        try
        {
            string? cfg = await client.RequestAsync(HttpMethod.Get, "/ISAPI/SecurityCP/Configuration/zones?format=json", ct: ct);
            if (cfg is not null) ParseZoneConfig(cfg, zoneNames);
        }
        catch (DriverException) { /* opcional */ }

        var areas = ParseAreas(subsystemsJson, areaNames);
        var zones = ParseZones(zonesJson, zoneNames);

        // Estado del chasis (tapa/sabotaje, corriente): opcional; si el equipo
        // no lo expone, el panel queda sin esa información.
        AlarmHostState? host = null;
        try
        {
            string? hostJson = await client.RequestAsync(HttpMethod.Get, "/ISAPI/SecurityCP/status/host?format=json", ct: ct);
            if (hostJson is not null) host = ParseHost(hostJson);
        }
        catch (DriverException) { /* opcional */ }

        return new AlarmPanelState(areas, zones, host);
    }

    internal static List<AlarmAreaState> ParseAreas(string json, Dictionary<int, string> names)
    {
        var result = new List<AlarmAreaState>();
        using var doc = JsonDocument.Parse(json);
        foreach (var item in EnumerateList(doc.RootElement, "SubSysList", "SubSys"))
        {
            int id = GetInt(item, "id") ?? result.Count + 1;
            // Los paneles reportan TODAS las particiones posibles (32 en los
            // DS-PHA64), pero solo unas pocas están habilitadas/en uso. Las
            // deshabilitadas se ocultan: no aportan al operador y ensucian la
            // vista. (Si el firmware no informa "enabled", se asume en uso.)
            bool enabled = GetBool(item, "enabled") ?? true;
            if (!enabled) continue;
            string arming = GetString(item, "arming") ?? "";
            var state = arming.ToLowerInvariant() switch
            {
                "disarm" or "disarmed" => AlarmArmState.Disarmed,
                "away" => AlarmArmState.Away,
                "stay" => AlarmArmState.Stay,
                "vacation" => AlarmArmState.Vacation,
                "arming" or "exitdelay" or "entrydelay" => AlarmArmState.Arming,
                _ => AlarmArmState.Unknown,
            };
            string name = names.GetValueOrDefault(id) ?? GetString(item, "name") ?? $"Área {id}";
            int exitDelay = GetInt(item, "delayTime") ?? 0;
            result.Add(new AlarmAreaState(id, name, enabled, state, GetBool(item, "alarm") ?? false, exitDelay));
        }
        return result;
    }

    /// <summary>
    /// Lee AlarmHostStatus.HostStatus (/ISAPI/SecurityCP/status/host): tapa
    /// (tamperEvident), corriente de red (ACConnect) y cantidad de fallas.
    /// </summary>
    internal static AlarmHostState? ParseHost(string json)
    {
        // OJO: el Hik IP Receiver Pro devuelve a veces el status/host con los
        // nombres de zona (ZoneList) en una codificación inválida que rompe el
        // JSON completo. Solo se necesita el objeto HostStatus, que va al
        // principio y sin anidar, así que se aísla y se parsea por separado en
        // vez de todo el documento.
        string host = ExtractObjectAfter(json, "HostStatus") ?? json;
        try
        {
            using var doc = JsonDocument.Parse(host);
            var root = doc.RootElement;
            if (TryGetPropertyIgnoreCase(root, "AlarmHostStatus", out var h)) root = h;
            if (TryGetPropertyIgnoreCase(root, "HostStatus", out var hs)) root = hs;
            bool tamper = GetBool(root, "tamperEvident") ?? false;
            bool acOk = GetBool(root, "ACConnect") ?? GetBool(root, "acConnect") ?? true;
            int faults = GetInt(root, "faultNum") ?? 0;
            return new AlarmHostState(tamper, !acOk, faults);
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Devuelve el objeto JSON <c>{...}</c> que sigue a la clave dada (con su
    /// llave de cierre balanceada, respetando cadenas), o null si no está. Sirve
    /// para leer un bloque bien formado aunque el resto del documento esté
    /// corrupto.
    /// </summary>
    private static string? ExtractObjectAfter(string json, string key)
    {
        int k = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (k < 0) return null;
        int open = json.IndexOf('{', k);
        if (open < 0) return null;
        int depth = 0;
        bool inStr = false, esc = false;
        for (int i = open; i < json.Length; i++)
        {
            char c = json[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
            }
            else if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0)
                return json.Substring(open, i - open + 1);
        }
        return null;
    }

    internal static List<AlarmZoneState> ParseZones(string json,
        Dictionary<int, (string? Name, string? ZoneType, string? DetectorType, int? Area)> config)
    {
        var result = new List<AlarmZoneState>();
        using var doc = JsonDocument.Parse(json);
        foreach (var item in EnumerateList(doc.RootElement, "ZoneList", "Zone"))
        {
            int id = GetInt(item, "id") ?? result.Count;
            config.TryGetValue(id, out var cfg);
            string status = (GetString(item, "status") ?? "").ToLowerInvariant();
            bool alarm = GetBool(item, "alarm") ?? false;
            bool tamper = GetBool(item, "tamperEvident") ?? GetBool(item, "tamper") ?? false;
            var zoneStatus = status switch
            {
                "online" or "normal" => AlarmZoneStatus.Normal,
                "trigger" or "triggered" or "open" => AlarmZoneStatus.Triggered,
                "offline" => AlarmZoneStatus.Offline,
                "breakdown" or "heartbeatabnormal" or "fault" => AlarmZoneStatus.Fault,
                "notrelated" or "notconfigured" => AlarmZoneStatus.NotConfigured,
                _ => AlarmZoneStatus.Unknown,
            };
            if (zoneStatus == AlarmZoneStatus.Normal && (GetBool(item, "magnetOpenStatus") ?? false))
                zoneStatus = AlarmZoneStatus.Triggered;
            // El AX Hybrid PRO separa "status" (comunicación: online) de
            // "sensorStatus" (el detector: normal/trigger...).
            string sensor = (GetString(item, "sensorStatus") ?? "").ToLowerInvariant();
            if (zoneStatus == AlarmZoneStatus.Normal && sensor is "trigger" or "triggered" or "alarm" or "open")
                zoneStatus = AlarmZoneStatus.Triggered;
            if (zoneStatus == AlarmZoneStatus.Normal && tamper)
                zoneStatus = AlarmZoneStatus.Fault;
            // "shielded" en la línea clásica (DS-PHA64-W4M) es una zona
            // deshabilitada en la configuración del panel: su interfaz web la
            // muestra como estado propio, por encima del bypass. No es una
            // anulación del operador, así que no se informa como tal.
            bool bypassed = GetBool(item, "bypassed") ?? false;
            bool shielded = GetBool(item, "shielded") ?? false;
            if (shielded && !bypassed && zoneStatus is AlarmZoneStatus.Normal or AlarmZoneStatus.Unknown)
                zoneStatus = AlarmZoneStatus.NotConfigured;

            int? charge = GetInt(item, "chargeValue");
            bool lowBattery = (GetBool(item, "lowBattery") ?? GetBool(item, "batteryLow") ?? false) || charge is > 0 and <= 20;
            int? area = GetInt(item, "subSystemNo") ?? cfg.Area;
            string name = cfg.Name ?? GetString(item, "name") ?? GetString(item, "zoneName") ?? $"Zona {id + 1}";
            result.Add(new AlarmZoneState(
                id, area, name,
                cfg.ZoneType ?? GetString(item, "zoneType"),
                cfg.DetectorType ?? GetString(item, "detectorType"),
                zoneStatus,
                bypassed,
                GetBool(item, "armed") ?? false,
                alarm, tamper, lowBattery,
                GetInt(item, "signal"),
                GetString(item, "model")));
        }
        return result;
    }

    private static void ParseAreaNames(string json, Dictionary<int, string> names)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var item in EnumerateList(doc.RootElement, "SubSysList", "SubSys")
                     .Concat(EnumerateList(doc.RootElement, "List", "SubSys")))
        {
            if (GetInt(item, "id") is { } id && (GetString(item, "name") ?? GetString(item, "subSysName")) is { Length: > 0 } name)
                names[id] = name;
        }
    }

    private static void ParseZoneConfig(string json,
        Dictionary<int, (string? Name, string? ZoneType, string? DetectorType, int? Area)> config)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var item in EnumerateList(doc.RootElement, "List", "Zone").Concat(EnumerateList(doc.RootElement, "ZoneList", "Zone")))
        {
            if (GetInt(item, "id") is not { } id) continue;
            config[id] = (
                GetString(item, "zoneName") ?? GetString(item, "name"),
                GetString(item, "zoneType"),
                GetString(item, "detectorType"),
                GetInt(item, "subSystemNo"));
        }
    }

    /// <summary>Recorre listas ISAPI del tipo {"XList":[{"X":{...}}, ...]} tolerando variantes.</summary>
    internal static IEnumerable<JsonElement> EnumerateList(JsonElement root, string listName, string itemName)
    {
        if (root.ValueKind != JsonValueKind.Object) yield break;
        if (!TryGetPropertyIgnoreCase(root, listName, out var list) || list.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var wrapper in list.EnumerateArray())
        {
            if (wrapper.ValueKind != JsonValueKind.Object) continue;
            if (TryGetPropertyIgnoreCase(wrapper, itemName, out var item) && item.ValueKind == JsonValueKind.Object)
                yield return item;
            else
                yield return wrapper; // lista sin envoltorio
        }
    }

    internal static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value)) return true;
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    internal static string? GetString(JsonElement obj, string name)
    {
        if (!TryGetPropertyIgnoreCase(obj, name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    internal static int? GetInt(JsonElement obj, string name)
    {
        if (!TryGetPropertyIgnoreCase(obj, name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out n)) return n;
        return null;
    }

    internal static bool? GetBool(JsonElement obj, string name)
    {
        if (!TryGetPropertyIgnoreCase(obj, name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out bool b) ? b : null,
            JsonValueKind.Number => v.TryGetInt32(out int n) ? n != 0 : null,
            _ => null,
        };
    }

    // ------------------------------------------------------------------
    // Órdenes
    // ------------------------------------------------------------------

    public Task ArmAsync(AlarmConnectionInfo info, int areaNumber, AlarmArmMode mode, CancellationToken ct = default)
    {
        string ways = mode == AlarmArmMode.Stay ? "stay" : "away";
        return AreaCommandAsync(info, areaNumber, "arm", $"?ways={ways}&format=json", "armar el área",
            id => $"{{\"SubSys\":{{\"id\":{id},\"armType\":\"{ways}\"}}}}", ct);
    }

    public Task DisarmAsync(AlarmConnectionInfo info, int areaNumber, CancellationToken ct = default) =>
        AreaCommandAsync(info, areaNumber, "disarm", "?format=json", "desarmar el área",
            id => $"{{\"SubSys\":{{\"id\":{id}}}}}", ct);

    public Task ClearAlarmAsync(AlarmConnectionInfo info, int areaNumber, CancellationToken ct = default) =>
        AreaCommandAsync(info, areaNumber, "clearAlarm", "?format=json", "borrar la alarma",
            id => $"{{\"SubSys\":{{\"id\":{id}}}}}", ct);

    /// <summary>
    /// Orden sobre un área: PUT /ISAPI/SecurityCP/control/{verbo}/{id}
    /// (formato leído del propio JS de la interfaz web del DS-PHA64-W4M y
    /// documentado igual para los AX PRO). Para "todas las áreas" se prueba
    /// primero el comodín 0xffffffff; si el panel lo rechaza, se manda la
    /// orden por lotes ({"SubSysList":[{"SubSys":{...}}]}) con las áreas
    /// habilitadas, que es lo que hace la web de la línea clásica.
    /// </summary>
    private static async Task<string?> AreaCommandAsync(AlarmConnectionInfo info, int areaNumber, string verb, string query,
        string action, Func<int, string> batchItem, CancellationToken ct)
    {
        var client = new HikvisionIsapiClient(info);
        if (areaNumber > 0)
        {
            string? body = await client.RequestAsync(HttpMethod.Put,
                $"/ISAPI/SecurityCP/control/{verb}/{areaNumber.ToString(CultureInfo.InvariantCulture)}{query}",
                ct: ct, allowNotFound: false);
            EnsureOk(body, action);
            return body;
        }

        try
        {
            string? body = await client.RequestAsync(HttpMethod.Put, $"/ISAPI/SecurityCP/control/{verb}/{AllAreas}{query}",
                ct: ct, allowNotFound: false);
            EnsureOk(body, action);
            return body;
        }
        catch (DriverException ex) when (IsUnsupported(ex))
        {
            // El comodín no existe en este firmware: lote con las áreas habilitadas.
            var ids = await EnabledAreaIdsAsync(client, ct);
            if (ids.Count == 0)
                throw new DriverException($"El panel no pudo {action}: no hay áreas habilitadas.");
            string json = "{\"SubSysList\":[" + string.Join(",", ids.Select(batchItem)) + "]}";
            string? body = await client.RequestAsync(HttpMethod.Put, $"/ISAPI/SecurityCP/control/{verb}?format=json", json,
                ct: ct, allowNotFound: false);
            EnsureOk(body, action);
            return body;
        }
    }

    private static async Task<List<int>> EnabledAreaIdsAsync(HikvisionIsapiClient client, CancellationToken ct)
    {
        string json = await client.RequestAsync(HttpMethod.Get, "/ISAPI/SecurityCP/status/subSystems?format=json", ct: ct) ?? "{}";
        var ids = new List<int>();
        using var doc = JsonDocument.Parse(json);
        foreach (var item in EnumerateList(doc.RootElement, "SubSysList", "SubSys"))
        {
            if (GetInt(item, "id") is { } id && (GetBool(item, "enabled") ?? true))
                ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// Anulación de zona. La forma verificada con hardware (DS-PHA64-W4M) es
    /// la de un solo identificador en la ruta, sin cuerpo; si el firmware no
    /// la tiene, se prueban las dos variantes por lote que documenta el
    /// fabricante ({"List":[{"id":n}]} en la línea clásica y
    /// {"BypassCtrl":{"zoneIds":[n]}} en los AX PRO).
    /// </summary>
    public async Task SetZoneBypassAsync(AlarmConnectionInfo info, int zoneNumber, bool bypassed, CancellationToken ct = default)
    {
        var client = new HikvisionIsapiClient(info);
        string verb = bypassed ? "bypass" : "bypassRecover";
        string action = bypassed ? "anular la zona" : "restituir la zona";
        string id = zoneNumber.ToString(CultureInfo.InvariantCulture);
        DriverException last;
        try
        {
            string? body = await client.RequestAsync(HttpMethod.Put, $"/ISAPI/SecurityCP/control/{verb}/{id}?format=json",
                ct: ct, allowNotFound: false);
            EnsureOk(body, action);
            return;
        }
        catch (DriverException ex) when (IsUnsupported(ex)) { last = ex; }

        foreach (string json in new[] { $"{{\"List\":[{{\"id\":{id}}}]}}", $"{{\"BypassCtrl\":{{\"zoneIds\":[{id}]}}}}" })
        {
            try
            {
                string? body = await client.RequestAsync(HttpMethod.Put, $"/ISAPI/SecurityCP/control/{verb}?format=json", json,
                    ct: ct, allowNotFound: false);
                EnsureOk(body, action);
                return;
            }
            catch (DriverException ex) when (IsUnsupported(ex)) { last = ex; }
        }
        throw last;
    }

    /// <summary>
    /// true si el panel no reconoció la forma de la orden (404, 400
    /// "invalidOperation"/"badParameters"...) y vale la pena probar otra
    /// variante. Un rechazo de fondo (zona abierta, sin permiso, credenciales,
    /// red) no se reintenta: repetirlo no cambia nada y ensucia el registro.
    /// </summary>
    private static bool IsUnsupported(DriverException ex) =>
        ex.Message.Contains("no soporta esta función", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("rechazó la solicitud", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("invalidOperation", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("badParameters", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("badXmlFormat", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("badJsonFormat", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("notSupport", StringComparison.OrdinalIgnoreCase);

    /// <summary>ISAPI responde 200 incluso cuando la orden falló: el veredicto viene en statusCode (1 = OK).</summary>
    internal static void EnsureOk(string? body, string action)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        string trimmed = body.TrimStart();
        string? code = null, text = null, sub = null;
        try
        {
            if (trimmed.StartsWith('<'))
            {
                var doc = XDocument.Parse(body);
                code = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "statusCode")?.Value?.Trim();
                text = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "statusString")?.Value?.Trim();
                sub = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "subStatusCode")?.Value?.Trim();
            }
            else if (trimmed.StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(body);
                code = GetString(doc.RootElement, "statusCode");
                text = GetString(doc.RootElement, "statusString");
                sub = GetString(doc.RootElement, "subStatusCode");
            }
        }
        catch { /* cuerpo no estructurado: se asume OK por el 200 */ }

        if (code is null || code == "1" || string.Equals(text, "OK", StringComparison.OrdinalIgnoreCase)) return;
        string reason = string.Join(" · ", new[] { text, sub }.Where(s => !string.IsNullOrWhiteSpace(s))!);
        throw new DriverException($"El panel no pudo {action}" + (reason.Length > 0 ? $": {reason}" : ".") +
                                  (sub is "notArmed" or "zoneOpen" or "armFailed" ? " (revise zonas abiertas)" : ""));
    }

    // ------------------------------------------------------------------
    // Canal de eventos (alertStream)
    // ------------------------------------------------------------------

    public async Task<IAlarmSubscription> SubscribeEventsAsync(AlarmConnectionInfo info, Action<AlarmPanelEvent> onEvent,
        Action? onActivity = null, CancellationToken ct = default)
    {
        var client = new HikvisionIsapiClient(info);
        var response = await client.OpenStreamAsync("/ISAPI/Event/notification/alertStream", ct);
        string? boundary = response.Content.Headers.ContentType?.Parameters
            .FirstOrDefault(p => string.Equals(p.Name, "boundary", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"');
        var stream = await response.Content.ReadAsStreamAsync(ct);
        return new AlertStreamSubscription(response, stream, boundary ?? "boundary", onEvent, onActivity);
    }

    /// <summary>
    /// Notificación empujada por el panel al servidor ("HTTP Host
    /// Notification": XML EventNotificationAlert con CIDEvent). Es el mismo
    /// formato de las partes del alertStream, así que se traduce igual.
    /// En el AX Hybrid PRO (DS-PHA64-LP V1.1.2) el alertStream se abre pero
    /// no empuja los eventos del panel: la notificación HTTP es la única vía
    /// de tiempo real, y hay que habilitarla en el equipo.
    /// </summary>
    public AlarmPanelEvent? ParsePushedEvent(string contentType, byte[] body) => ParseEvent(contentType, body);

    /// <summary>
    /// Lector del flujo multipart de alertas. Cada parte trae cabeceras
    /// (Content-Type, Content-Length) y un cuerpo XML o JSON con
    /// EventNotificationAlert. Se lee en un hilo propio hasta que el panel
    /// cierra o falla; entonces IsAlive pasa a false y el servidor rehace la
    /// suscripción.
    /// </summary>
    internal sealed class AlertStreamSubscription : IAlarmSubscription
    {
        private readonly HttpResponseMessage _response;
        private readonly Stream _stream;
        private readonly string _boundary;
        private readonly Action<AlarmPanelEvent> _onEvent;
        private readonly Action? _onActivity;
        private readonly Func<string, byte[], AlarmPanelEvent?> _parse;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _reader;
        private volatile bool _alive = true;

        /// <param name="parse">Traduce una parte (content-type, cuerpo) a evento; null = latido u otra cosa.</param>
        public AlertStreamSubscription(HttpResponseMessage response, Stream stream, string boundary,
            Action<AlarmPanelEvent> onEvent, Action? onActivity, Func<string, byte[], AlarmPanelEvent?>? parse = null)
        {
            _response = response;
            _stream = stream;
            _boundary = boundary;
            _onEvent = onEvent;
            _onActivity = onActivity;
            _parse = parse ?? ParseEvent;
            _reader = Task.Run(ReadLoopAsync);
        }

        public bool IsAlive => _alive;

        private async Task ReadLoopAsync()
        {
            try
            {
                var reader = new MultipartReader(_stream, _boundary);
                while (!_cts.IsCancellationRequested)
                {
                    var part = await reader.ReadPartAsync(_cts.Token);
                    if (part is null) break; // fin del flujo
                    _onActivity?.Invoke();
                    if (_parse(part.Value.ContentType, part.Value.Body) is { } evt)
                        _onEvent(evt);
                }
            }
            catch (OperationCanceledException) { /* cierre */ }
            catch (Exception) { /* el panel cortó o la red falló: se rehace desde el servidor */ }
            finally
            {
                _alive = false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _alive = false;
            _cts.Cancel();
            try { _response.Dispose(); } catch { }
            try { await _reader.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            _cts.Dispose();
        }
    }

    /// <summary>Lector multipart mínimo: partes delimitadas por "--boundary", con cabeceras y Content-Length.</summary>
    internal sealed class MultipartReader(Stream stream, string boundary)
    {
        private readonly byte[] _buffer = new byte[16 * 1024];
        private int _start, _end;
        private readonly string _delimiter = "--" + boundary;

        public async Task<(string ContentType, byte[] Body)?> ReadPartAsync(CancellationToken ct)
        {
            // Saltar hasta la línea delimitadora. La línea clásica (DS-PHA64-W4M)
            // pega el delimitador al final del cuerpo anterior sin salto de
            // línea ("{...}--boundary"), y la primera parte del flujo llega sin
            // delimitador ni cabeceras: se acepta el delimitador en cualquier
            // posición de la línea y se descarta lo que había antes.
            while (true)
            {
                string? line = await ReadLineAsync(ct);
                if (line is null) return null;
                line = line.Trim();
                int at = line.IndexOf(_delimiter, StringComparison.Ordinal);
                if (at < 0) continue;
                line = line[at..];
                if (line.EndsWith("--", StringComparison.Ordinal) && line.Length == _delimiter.Length + 2)
                    return null; // cierre del multipart
                break;
            }

            string contentType = "";
            int contentLength = -1;
            while (true)
            {
                string? header = await ReadLineAsync(ct);
                if (header is null) return null;
                if (header.Length == 0) break;
                int colon = header.IndexOf(':');
                if (colon <= 0) continue;
                string name = header[..colon].Trim();
                string value = header[(colon + 1)..].Trim();
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) contentType = value;
                else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int n)) contentLength = n;
            }

            if (contentLength >= 0)
            {
                var body = new byte[contentLength];
                int read = 0;
                while (read < contentLength)
                {
                    int n = await ReadIntoAsync(body, read, contentLength - read, ct);
                    if (n <= 0) return null;
                    read += n;
                }
                return (contentType, body);
            }

            // Sin Content-Length: acumular hasta el próximo delimitador.
            var sb = new StringBuilder();
            while (true)
            {
                string? line = await ReadLineAsync(ct);
                if (line is null) break;
                if (line.StartsWith(_delimiter, StringComparison.Ordinal))
                {
                    // Devolver el delimitador al buffer para la próxima parte.
                    var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
                    Unread(bytes);
                    break;
                }
                sb.Append(line).Append('\n');
            }
            return (contentType, Encoding.UTF8.GetBytes(sb.ToString()));
        }

        private void Unread(byte[] bytes)
        {
            // Solo ocurre al final de una parte sin Content-Length: el buffer
            // ya se consumió hasta aquí, así que hay espacio para reponer.
            if (_start >= bytes.Length)
            {
                _start -= bytes.Length;
                Array.Copy(bytes, 0, _buffer, _start, bytes.Length);
            }
            else
            {
                int remaining = _end - _start;
                Array.Copy(_buffer, _start, _buffer, bytes.Length, remaining);
                Array.Copy(bytes, 0, _buffer, 0, bytes.Length);
                _start = 0;
                _end = bytes.Length + remaining;
            }
        }

        private async Task<int> ReadIntoAsync(byte[] target, int offset, int count, CancellationToken ct)
        {
            if (_start < _end)
            {
                int n = Math.Min(count, _end - _start);
                Array.Copy(_buffer, _start, target, offset, n);
                _start += n;
                return n;
            }
            return await stream.ReadAsync(target.AsMemory(offset, count), ct);
        }

        private async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var line = new List<byte>(128);
            while (true)
            {
                if (_start >= _end)
                {
                    _start = 0;
                    _end = await stream.ReadAsync(_buffer, ct);
                    if (_end <= 0) return line.Count == 0 ? null : Encoding.UTF8.GetString(line.ToArray());
                }
                byte b = _buffer[_start++];
                if (b == (byte)'\n')
                {
                    if (line.Count > 0 && line[^1] == (byte)'\r') line.RemoveAt(line.Count - 1);
                    return Encoding.UTF8.GetString(line.ToArray());
                }
                line.Add(b);
                if (line.Count > 64 * 1024) return Encoding.UTF8.GetString(line.ToArray()); // línea absurda: cortar
            }
        }
    }

    // ------------------------------------------------------------------
    // Traducción de una notificación a AlarmPanelEvent
    // ------------------------------------------------------------------

    /// <summary>
    /// Traduce una parte del alertStream. Devuelve null para latidos y
    /// notificaciones que no son eventos del panel (el servidor solo usa la
    /// actividad para saber que el equipo sigue vivo).
    /// </summary>
    internal static AlarmPanelEvent? ParseEvent(string contentType, byte[] body)
    {
        if (body.Length == 0) return null;
        string text = Encoding.UTF8.GetString(body).Trim();
        if (text.Length == 0) return null;

        string? eventType = null, eventState = null, dateTime = null, description = null;
        string? code = null, cidType = null, cidName = null, cidDescription = null, cidTrigger = null, user = null;
        int? zone = null, area = null;

        try
        {
            if (text.StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                eventType = GetString(root, "eventType");
                eventState = GetString(root, "eventState");
                dateTime = GetString(root, "dateTime");
                description = GetString(root, "eventDescription");
                if (FindObject(root, "CIDEvent") is { } cid)
                {
                    code = GetString(cid, "code");
                    cidType = GetString(cid, "type");
                    cidName = GetString(cid, "name");
                    cidDescription = GetString(cid, "description") ?? GetString(cid, "eventDescription");
                    cidTrigger = GetString(cid, "trigger");
                    user = GetString(cid, "userName") ?? GetString(cid, "user") ?? GetString(cid, "operatorName")
                           ?? GetString(cid, "keypadName") ?? GetString(cid, "remoteControlName") ?? GetString(cid, "cardName");
                    zone = GetInt(cid, "zone") ?? GetInt(cid, "zoneId") ?? GetInt(cid, "zoneNo");
                    area = GetInt(cid, "subSystem") ?? GetInt(cid, "subSystemNo") ?? GetInt(cid, "partition");
                }
            }
            else if (text.StartsWith('<'))
            {
                var doc = XDocument.Parse(text);
                string? V(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim();
                eventType = V("eventType");
                eventState = V("eventState");
                dateTime = V("dateTime");
                description = V("eventDescription");
                if (doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "CIDEvent") is { } cid)
                {
                    string? C(string name) => cid.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim();
                    code = C("code");
                    cidType = C("type");
                    cidName = C("name");
                    cidDescription = C("description");
                    cidTrigger = C("trigger");
                    user = C("userName") ?? C("user") ?? C("operatorName");
                    zone = int.TryParse(C("zone") ?? C("zoneNo"), out int z) ? z : null;
                    area = int.TryParse(C("subSystem") ?? C("subSystemNo"), out int a) ? a : null;
                }
            }
            else
            {
                return null;
            }
        }
        catch (Exception)
        {
            return null; // parte corrupta o formato desconocido
        }

        // Latidos y notificaciones de video no son eventos de alarma. La
        // línea clásica (DS-PHA64-W4M) late con un "cidEvent" inactivo SIN
        // objeto CIDEvent cada 2 s: sin código no hay evento que registrar.
        if (code is null && (eventType is null ||
                             eventType.Equals("heartBeat", StringComparison.OrdinalIgnoreCase) ||
                             eventType.Equals("cidEvent", StringComparison.OrdinalIgnoreCase) ||
                             eventType.Equals("videoloss", StringComparison.OrdinalIgnoreCase)))
            return null;

        var timestamp = DateTime.UtcNow;
        if (dateTime is not null && DateTimeOffset.TryParse(NormalizeOffset(dateTime), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
            timestamp = parsed.UtcDateTime;

        AlarmEventKind kind;
        AlarmSeverity severity;
        string label;
        if (HikvisionEventCodes.Translate(code) is { } hik)
        {
            (kind, severity, label) = hik;
        }
        else if (ContactIdCatalog.Translate(code) is { } cid2)
        {
            (kind, severity, label) = cid2;
        }
        else
        {
            (kind, severity) = (cidType ?? eventType ?? "").ToLowerInvariant() switch
            {
                "alarm" or "zonealarm" or "panic" or "fire" or "tamper" or "duress" => (AlarmEventKind.Alarm, AlarmSeverity.Critical),
                "arm" or "armed" or "away" or "stay" => (AlarmEventKind.Arm, AlarmSeverity.Info),
                "disarm" or "disarmed" => (AlarmEventKind.Disarm, AlarmSeverity.Info),
                "bypass" or "unbypass" or "bypassrecover" => (AlarmEventKind.Bypass, AlarmSeverity.Warning),
                "trouble" or "fault" or "exception" => (AlarmEventKind.Trouble, AlarmSeverity.Warning),
                "restore" or "recover" => (AlarmEventKind.Restore, AlarmSeverity.Info),
                _ => (AlarmEventKind.Info, AlarmSeverity.Info),
            };
            label = cidDescription ?? description ?? cidType ?? eventType ?? "Evento del panel";
            if (code is not null) label += $" (código {code})";
        }

        // Un evento "inactive" es la vuelta a la normalidad de uno anterior.
        if (kind == AlarmEventKind.Alarm && string.Equals(eventState, "inactive", StringComparison.OrdinalIgnoreCase))
        {
            kind = AlarmEventKind.Restore;
            severity = AlarmSeverity.Info;
            label += " (fin)";
        }

        var detail = new StringBuilder(label);
        if (!string.IsNullOrWhiteSpace(cidName) && !label.Contains(cidName, StringComparison.OrdinalIgnoreCase))
            detail.Append(" · ").Append(cidName);
        if (!string.IsNullOrWhiteSpace(cidTrigger) && !cidTrigger.Equals("zone", StringComparison.OrdinalIgnoreCase))
            detail.Append(" · origen: ").Append(cidTrigger);

        return new AlarmPanelEvent(timestamp, kind, severity, code, detail.ToString(), area, zone, user, text);
    }

    /// <summary>
    /// La línea clásica fecha con desfase de un dígito ("2026-09-02T12:50:00-4:00"),
    /// que .NET no acepta: se rellena a "-04:00".
    /// </summary>
    public static string NormalizeOffset(string dateTime)
    {
        int t = dateTime.IndexOf('T');
        if (t < 0) return dateTime;
        int sign = dateTime.LastIndexOfAny(['+', '-']);
        if (sign <= t) return dateTime;
        string offset = dateTime[(sign + 1)..];
        int colon = offset.IndexOf(':');
        if (colon == 1 && char.IsDigit(offset[0]))
            return dateTime[..(sign + 1)] + "0" + offset;
        return dateTime;
    }

    internal static JsonElement? FindObject(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in root.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Object)
                return p.Value;
        }
        // Un nivel más abajo (algunos firmware envuelven en EventNotificationAlert).
        foreach (var p in root.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Object && FindObject(p.Value, name) is { } nested)
                return nested;
        }
        return null;
    }
}

public sealed class HikvisionAlarmPanelDriverFactory : IAlarmPanelDriverFactory
{
    public string DriverKey => "hikvision-isapi";
    public string DisplayName => "Hikvision AX PRO / AX Hybrid / DS-PHA (ISAPI)";
    public int DefaultPort => 80;
    public bool DefaultHttps => false;
    public IAlarmPanelDriver Create() => new HikvisionAlarmPanelDriver();
}
