using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Configuración propia de un equipo de control de acceso Hikvision: los
/// parámetros de cada puerta (<c>/ISAPI/AccessControl/Door/param/{n}</c>,
/// documento <c>DoorParam</c>) y de cada lector
/// (<c>/ISAPI/AccessControl/CardReaderCfg/{n}</c>, documento
/// <c>CardReaderCfg</c>). Son los mismos que HikCentral muestra en la ficha de
/// la puerta: contacto de puerta, botón de salida, tiempos de apertura, alarma
/// de puerta abierta, códigos de coacción y maestro; y en la del lector:
/// detección de desconexión, intervalo entre tarjetas, intentos fallidos,
/// sabotaje, polaridad de los LED, nivel de huella, umbrales y tiempos del
/// reconocimiento facial, antisuplantación.
///
/// Cómo se trabaja con el equipo:
/// <list type="bullet">
/// <item>Se pide el documento en JSON (<c>?format=json</c>) y, si el firmware
/// no lo da en esa forma (el DS-K1T321MFWX V3.9.20 contesta un JSON casi
/// vacío para la puerta), en XML. Se escribe de vuelta en la MISMA forma en
/// que se leyó.</item>
/// <item>Se devuelve el documento ENTERO con los valores cambiados, en el
/// mismo orden en que vino: los firmware rechazan documentos parciales o con
/// los campos desordenados.</item>
/// <item>La página se arma con lo que el equipo contestó: un parámetro del
/// catálogo que el equipo no trae no se muestra, y lo que el equipo trae y el
/// catálogo no conoce se muestra igual, solo lectura, para no esconder nada.
/// Los rangos y las opciones salen de las rutas <c>capabilities</c> cuando el
/// equipo las declara.</item>
/// </list>
/// </summary>
public sealed class HikvisionAccessSettingsProvider : IAccessDeviceSettingsProvider
{
    public string DriverKey => "hikvision-isapi";

    private const string Noun = "equipo de control de acceso";
    private const int MaxDoors = 16;
    private const int MaxReaders = 8;
    private const string DoorCapsPath = "/ISAPI/AccessControl/Door/param/capabilities?format=json";
    private const string ReaderCapsPath = "/ISAPI/AccessControl/CardReaderCfg/capabilities?format=json";

    private static string DoorPath(int n) => $"/ISAPI/AccessControl/Door/param/{n}";
    private static string ReaderPath(int n) => $"/ISAPI/AccessControl/CardReaderCfg/{n}";

    /// <summary>Mismo transporte y mismo criterio (digest) que <see cref="HikvisionAccessDriver"/>.</summary>
    private static HikvisionIsapiClient Client(AccessConnectionInfo info) =>
        new(new AlarmConnectionInfo(info.Host, info.Port, info.UseHttps, info.Username, info.Password),
            digestOnly: true, deviceNoun: Noun);

    // ==================================================================
    // Lectura
    // ==================================================================

    public async Task<AccessDeviceSettingsDto> ReadAsync(AccessConnectionInfo info, CancellationToken ct = default)
    {
        var client = Client(info);
        var sections = new List<AccessSettingsSectionDto>();
        var notes = new List<string>();

        var doorCaps = await CapsAsync(client, DoorCapsPath, ct);
        int doorCount = Math.Clamp(CountOf(doorCaps, "doorNo", ["doorNum", "doorNumber", "doorNums"]) ?? 1, 1, MaxDoors);
        for (int n = 1; n <= doorCount; n++)
        {
            var doc = await LoadAsync(client, DoorPath(n), "DoorParam", ct);
            if (doc is null) { notes.Add($"El equipo no entregó los parámetros de la puerta {n}."); continue; }
            sections.Add(DoorSection(n, doc, doorCaps));
        }

        // Cuántos lectores: lo que declaran las capacidades; si no lo dicen,
        // se leen hasta que el equipo deje de contestar (un terminal trae el
        // propio y la entrada para uno externo; una controladora, hasta 8).
        var readerCaps = await CapsAsync(client, ReaderCapsPath, ct);
        int? declared = CountOf(readerCaps, "cardReaderNo", ["cardReaderNum", "readerNum", "maxCardReaderNum"]);
        int readerCount = Math.Clamp(declared ?? MaxReaders, 1, MaxReaders);
        for (int n = 1; n <= readerCount; n++)
        {
            var doc = await LoadAsync(client, ReaderPath(n), "CardReaderCfg", ct);
            if (doc is null)
            {
                if (declared is null) break;
                notes.Add($"El equipo no entregó los parámetros del lector {n}.");
                continue;
            }
            sections.Add(ReaderSection(n, doc, readerCaps));
        }

        if (sections.Count == 0)
            throw new DriverException("El equipo no entregó la configuración de sus puertas ni de sus lectores " +
                                      "(firmware sin esas rutas o usuario sin permiso).");
        return new AccessDeviceSettingsDto(sections, notes);
    }

    // ==================================================================
    // Escritura
    // ==================================================================

    public async Task<AccessSettingsSectionDto> ApplyAsync(AccessConnectionInfo info, string sectionKey,
        IReadOnlyDictionary<string, string?> values, CancellationToken ct = default)
    {
        var (kind, number) = ParseSectionKey(sectionKey);
        bool isDoor = kind == "door";
        string path = isDoor ? DoorPath(number) : ReaderPath(number);
        string rootName = isDoor ? "DoorParam" : "CardReaderCfg";
        var catalog = isDoor ? DoorCatalog : ReaderCatalog;
        string what = isDoor ? $"la puerta {number}" : $"el lector {number}";

        var client = Client(info);
        var doc = await LoadAsync(client, path, rootName, ct)
                  ?? throw new DriverException($"El equipo no entregó los parámetros de {what}.");

        int changed = 0;
        foreach (var (key, raw) in values)
        {
            var entry = catalog.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                        ?? throw new DriverException($"El parámetro '{key}' no se edita desde el VMS.");
            if (entry.Type == AccessSettingType.Info)
                throw new DriverException($"'{entry.Label}' es solo de lectura.");
            if (entry.Type != AccessSettingType.Password && !doc.Has(entry.Key))
                throw new DriverException($"El equipo no tiene el parámetro '{entry.Label}'.");

            string? value = raw?.Trim();
            switch (entry.Type)
            {
                case AccessSettingType.Password:
                    if (string.IsNullOrEmpty(value)) continue;   // vacío = no cambiar
                    if (value.Length > 8 || !value.All(char.IsAsciiDigit))
                        throw new DriverException($"'{entry.Label}' debe tener de 1 a 8 dígitos (se teclea en el equipo).");
                    doc.SetString(entry.Key, entry.Base64 ? Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) : value);
                    break;
                case AccessSettingType.Boolean:
                    if (TryParseBool(value) is not { } flag)
                        throw new DriverException($"'{entry.Label}' debe ser sí o no.");
                    doc.SetBool(entry.Key, flag);
                    break;
                case AccessSettingType.Integer:
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                        throw new DriverException($"'{entry.Label}' debe ser un número entero.");
                    doc.SetInt(entry.Key, n);
                    break;
                case AccessSettingType.Choice:
                    if (string.IsNullOrEmpty(value))
                        throw new DriverException($"Elija un valor para '{entry.Label}'.");
                    // Muchas listas son números con significado (nivel de huella 1..18,
                    // nivel de seguridad facial 1..3): se escriben con el tipo que tenía el campo.
                    doc.SetChoice(entry.Key, value);
                    break;
                default:
                    doc.SetString(entry.Key, value ?? "");
                    break;
            }
            changed++;
        }

        if (changed > 0)
        {
            var (body, contentType, suffix) = doc.Serialize();
            string? response = await client.RequestAsync(HttpMethod.Put, path + suffix, body, contentType, ct,
                allowNotFound: false);
            if (FailureOf(response) is { } error)
                throw new DriverException($"El equipo rechazó los cambios de {what}: {error}");
        }

        // Se devuelve lo que el equipo DEJÓ: puede haber redondeado o acotado.
        var fresh = await LoadAsync(client, path, rootName, ct)
                    ?? throw new DriverException($"Los cambios se enviaron, pero el equipo no volvió a entregar los parámetros de {what}.");
        var caps = await CapsAsync(client, isDoor ? DoorCapsPath : ReaderCapsPath, ct);
        return isDoor ? DoorSection(number, fresh, caps) : ReaderSection(number, fresh, caps);
    }

    private static (string Kind, int Number) ParseSectionKey(string key)
    {
        if (key?.Split(':') is [var kind, var text] && (kind is "door" or "reader") &&
            int.TryParse(text, out int number) && number >= 1 && number <= (kind == "door" ? MaxDoors : MaxReaders))
            return (kind, number);
        throw new DriverException($"Bloque de configuración desconocido: '{key}'.");
    }

    /// <summary>ISAPI contesta HTTP 200 con <c>statusCode</c> distinto de 1 cuando rechaza el contenido.</summary>
    private static string? FailureOf(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;
        string trimmed = response.TrimStart();
        if (trimmed.StartsWith('<'))
        {
            try
            {
                var xml = XDocument.Parse(response);
                string? Value(string name) => xml.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim();
                if (int.TryParse(Value("statusCode"), out int code) && code != 1)
                    return Value("subStatusCode") ?? Value("statusString") ?? $"código {code}";
            }
            catch (System.Xml.XmlException) { /* el transporte ya validó el HTTP */ }
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (HikvisionAlarmPanelDriver.GetInt(root, "statusCode") is { } code && code != 1)
                return HikvisionAlarmPanelDriver.GetString(root, "subStatusCode")
                       ?? HikvisionAlarmPanelDriver.GetString(root, "statusString")
                       ?? $"código {code}";
        }
        catch (JsonException) { /* texto: el transporte ya validó el HTTP */ }
        return null;
    }

    // ==================================================================
    // Bloques
    // ==================================================================

    private static AccessSettingsSectionDto DoorSection(int number, ParamDoc doc, JsonObject? caps) =>
        Build("door", number, $"Puerta {number}", doc.Get("doorName"), doc, caps, DoorCatalog, DoorHidden);

    private static AccessSettingsSectionDto ReaderSection(int number, ParamDoc doc, JsonObject? caps)
    {
        // Qué lector es: el propio del terminal dice su modelo y sus funciones
        // (rostro, tarjeta, huella); la entrada para un lector externo sin
        // nada conectado viene sin funciones y sin descripción.
        string? description = doc.Get("cardReaderDescription");
        var functions = doc.GetList("cardReaderFunction").Select(FunctionLabel).ToList();
        string? subtitle;
        if (functions.Count > 0)
            subtitle = string.IsNullOrWhiteSpace(description) ? string.Join(", ", functions) : $"{description} · {string.Join(", ", functions)}";
        else if (!string.IsNullOrWhiteSpace(description))
            subtitle = description;
        else if (doc.Has("cardReaderFunction"))
            subtitle = "Entrada para un lector externo (sin lector conectado)";
        else
            subtitle = null;
        return Build("reader", number, $"Lector {number}", subtitle, doc, caps, ReaderCatalog, ReaderHidden);
    }

    private static string FunctionLabel(string function) => function switch
    {
        "face" => "rostro",
        "card" => "tarjeta",
        "fingerPrint" => "huella",
        "password" => "clave",
        "qrCode" => "código QR",
        _ => function,
    };

    private static AccessSettingsSectionDto Build(string kind, int number, string title, string? subtitle, ParamDoc doc,
        JsonObject? caps, Entry[] catalog, HashSet<string> hidden)
    {
        var settings = new List<AccessSettingDto>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog)
        {
            if (!doc.Has(entry.Key)) continue;
            used.Add(entry.Key);
            var cap = CapOf(caps, entry.Key);
            string? value = entry.Type == AccessSettingType.Password ? null : doc.Get(entry.Key);
            // Un dato de solo lectura con nombres conocidos se muestra traducido ("cardOrFace" → "Tarjeta o rostro").
            if (entry.Type == AccessSettingType.Info && value is not null && entry.Labels?.GetValueOrDefault(value) is { } named)
                value = named;
            IReadOnlyList<AccessSettingOption>? options = null;
            if (entry.Type == AccessSettingType.Choice)
            {
                // Las opciones válidas las dice el equipo; el catálogo solo pone
                // los nombres. Si el valor actual no está entre ellas, se ofrece
                // igual (si no, el formulario lo cambiaría sin querer).
                var values = (cap?.Options is { Count: > 0 } declared ? declared : entry.Labels?.Keys.ToList() ?? []).ToList();
                if (value is not null && !values.Contains(value, StringComparer.OrdinalIgnoreCase)) values.Add(value);
                options = values.Select(v => new AccessSettingOption(v, entry.Labels?.GetValueOrDefault(v) ?? v)).ToList();
            }
            settings.Add(new AccessSettingDto(entry.Key, entry.Label, entry.Type, value,
                entry.Type == AccessSettingType.Integer ? cap?.Min : null,
                entry.Type == AccessSettingType.Integer ? cap?.Max : null,
                entry.Unit, options, entry.Help));
        }
        var others = doc.Scalars()
            .Where(s => !used.Contains(s.Key) && !hidden.Contains(s.Key))
            .Select(s => new AccessSettingDto(s.Key, s.Key, AccessSettingType.Info, s.Value))
            .ToList();
        return new AccessSettingsSectionDto($"{kind}:{number}", kind, number, title, subtitle, settings, others);
    }

    // ==================================================================
    // Catálogo: qué parámetros se editan y cómo se llaman en español
    // ==================================================================

    private sealed record Entry(string Key, string Label, AccessSettingType Type, string? Unit = null, string? Help = null,
        IReadOnlyDictionary<string, string>? Labels = null,
        /// <summary>El equipo espera el valor en Base64 (las claves de la puerta, según la guía ISAPI).</summary>
        bool Base64 = false);

    private static readonly Dictionary<string, string> ContactLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alwaysClose"] = "Normalmente cerrado",
        ["alwaysOpen"] = "Normalmente abierto",
    };

    private static readonly Dictionary<string, string> YesNoLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["true"] = "Sí",
        ["false"] = "No",
    };

    private static readonly Dictionary<string, string> TerminalModeLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["preventCutAndShort"] = "Supervisadas (corte y cortocircuito)",
        ["common"] = "Comunes",
    };

    private static readonly Dictionary<string, string> LeaderCardLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["disable"] = "Desactivado",
        ["alwaysOpen"] = "Queda abierta con la primera tarjeta",
        ["authorize"] = "La primera tarjeta habilita la puerta",
    };

    private static readonly Dictionary<string, string> PolarityLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["anode"] = "Ánodo",
        ["cathode"] = "Cátodo",
    };

    private static readonly Dictionary<string, string> LightLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["indoor"] = "Interior",
        ["outdoor"] = "Exterior",
        ["other"] = "Otro",
    };

    /// <summary>Modos de verificación de ISAPI (<c>defaultVerifyMode</c>), nombrados como los entiende un guardia.</summary>
    private static readonly Dictionary<string, string> VerifyModeLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["card"] = "Tarjeta",
        ["cardAndPw"] = "Tarjeta + clave",
        ["cardOrPw"] = "Tarjeta o clave",
        ["fp"] = "Huella",
        ["fpAndPw"] = "Huella + clave",
        ["fpOrCard"] = "Huella o tarjeta",
        ["fpAndCard"] = "Huella + tarjeta",
        ["fpAndCardAndPw"] = "Huella + tarjeta + clave",
        ["fpOrPw"] = "Huella o clave",
        ["face"] = "Rostro",
        ["faceAndPw"] = "Rostro + clave",
        ["faceAndCard"] = "Rostro + tarjeta",
        ["faceAndFp"] = "Rostro + huella",
        ["faceAndFpAndCard"] = "Rostro + huella + tarjeta",
        ["faceAndPwAndFp"] = "Rostro + clave + huella",
        ["faceOrFpOrCardOrPw"] = "Rostro, huella, tarjeta o clave",
        ["cardOrFace"] = "Tarjeta o rostro",
        ["cardOrFaceOrFp"] = "Tarjeta, rostro o huella",
        ["cardOrfaceOrPw"] = "Tarjeta, rostro o clave",
        ["cardOrFpOrPw"] = "Tarjeta, huella o clave",
        ["fpOrface"] = "Huella o rostro",
        ["faceOrfaceAndCard"] = "Rostro, o rostro + tarjeta",
        ["employeeNoAndPw"] = "N° de persona + clave",
        ["employeeNoAndFp"] = "N° de persona + huella",
        ["employeeNoAndFpAndPw"] = "N° de persona + huella + clave",
        ["employeeNoAndFace"] = "N° de persona + rostro",
        ["sleep"] = "Lector en reposo",
        ["invalid"] = "Sin verificación (lector apagado)",
    };

    private static readonly Entry[] DoorCatalog =
    [
        new("doorName", "Nombre de la puerta en el equipo", AccessSettingType.Text,
            Help: "El VMS la muestra con este nombre mientras nadie la renombre en Recursos."),
        new("magneticType", "Contacto de puerta (sensor magnético)", AccessSettingType.Choice,
            Help: "Cómo está cableado el sensor que informa si la hoja está abierta.", Labels: ContactLabels),
        new("openButtonType", "Botón de salida", AccessSettingType.Choice,
            Help: "Cómo está cableado el pulsador de salida.", Labels: ContactLabels),
        new("openDuration", "Tiempo de apertura", AccessSettingType.Integer, "s",
            "Cuánto queda destrabada la cerradura tras una credencial válida."),
        new("disabledOpenDuration", "Tiempo de apertura ampliado", AccessSettingType.Integer, "s",
            "Para personas con movilidad reducida (HikCentral lo llama «Delay Duration»)."),
        new("magneticAlarmTimeout", "Alarma de puerta abierta demasiado tiempo", AccessSettingType.Integer, "s",
            "Avisa si la hoja sigue abierta pasado este tiempo; 0 = sin alarma."),
        new("enableDoorLock", "Trabar la cerradura apenas se cierra la hoja", AccessSettingType.Boolean,
            Help: "Si no, espera a que termine el tiempo de apertura."),
        new("enableLeaderCard", "Abrir con la primera tarjeta", AccessSettingType.Boolean,
            Help: "La puerta queda abierta cuando pasa una tarjeta autorizada a abrir el día."),
        new("leaderCardMode", "Modo de primera tarjeta", AccessSettingType.Choice,
            Help: "Qué hace la primera tarjeta del día; si está, manda sobre la casilla anterior.", Labels: LeaderCardLabels),
        new("leaderCardOpenDuration", "Duración tras la primera tarjeta", AccessSettingType.Integer, "s"),
        new("openButton", "Botón de salida habilitado", AccessSettingType.Boolean),
        new("lockInputCheck", "Supervisar el estado de la cerradura", AccessSettingType.Boolean),
        new("lockInputType", "Entrada de estado de la cerradura", AccessSettingType.Choice, Labels: ContactLabels),
        new("doorTerminalMode", "Entradas de la puerta", AccessSettingType.Choice,
            Help: "Supervisadas: el equipo detecta un cable cortado o en corto.", Labels: TerminalModeLabels),
        new("ladderControlDelayTime", "Retardo del control de ascensor (visitas)", AccessSettingType.Integer, "min"),
        new("stressPassword", "Código de coacción", AccessSettingType.Password,
            Help: "1 a 8 dígitos. Al teclearlo la puerta abre igual, pero el equipo manda una alarma silenciosa. Vacío = no cambiar.",
            Base64: true),
        new("superPassword", "Contraseña maestra", AccessSettingType.Password,
            Help: "1 a 8 dígitos. Abre la puerta desde el teclado sin credencial. Vacío = no cambiar.", Base64: true),
        new("unlockPassword", "Código de desbloqueo", AccessSettingType.Password,
            Help: "1 a 8 dígitos. Cancela una alarma desde el teclado. Vacío = no cambiar.", Base64: true),
        new("remoteControlPWStatus", "Contraseña de apertura remota configurada", AccessSettingType.Info, Labels: YesNoLabels),
        new("useLocalController", "Usa una controladora local", AccessSettingType.Info, Labels: YesNoLabels),
        new("localControllerID", "Controladora local", AccessSettingType.Info),
        new("localControllerDoorNumber", "Puerta en la controladora local", AccessSettingType.Info),
        new("localControllerStatus", "Estado de la controladora local", AccessSettingType.Info),
    ];

    private static readonly HashSet<string> DoorHidden = new(StringComparer.OrdinalIgnoreCase) { "doorNo" };

    /// <summary>Nivel de huella (<c>fingerPrintCheckLevel</c>): un entero que codifica la tasa de falsa aceptación.</summary>
    private static readonly Dictionary<string, string> FingerLevelLabels = new()
    {
        ["1"] = "1/10", ["2"] = "1/100", ["3"] = "1/1.000", ["4"] = "1/10.000", ["5"] = "1/100.000",
        ["6"] = "1/1.000.000", ["7"] = "1/10.000.000", ["8"] = "1/100.000.000",
        ["9"] = "3/100", ["10"] = "3/1.000", ["11"] = "3/10.000", ["12"] = "3/100.000",
        ["13"] = "3/1.000.000", ["14"] = "3/10.000.000", ["15"] = "3/100.000.000",
        ["16"] = "Automático normal", ["17"] = "Automático seguro", ["18"] = "Automático más seguro",
    };

    private static readonly Dictionary<string, string> FingerQualityLabels = new()
    {
        ["1"] = "Baja", ["2"] = "Media", ["3"] = "Alta", ["4"] = "Máxima",
        ["5"] = "Baja (algoritmo V2)", ["6"] = "Media (algoritmo V2)", ["7"] = "Alta (algoritmo V2)", ["8"] = "Máxima (algoritmo V2)",
    };

    private static readonly Dictionary<string, string> SecurityLevelLabels = new()
    {
        ["1"] = "Normal", ["2"] = "Alto", ["3"] = "Más alto",
    };

    private static readonly Dictionary<string, string> FaceEnableLabels = new()
    {
        ["1"] = "Activado", ["2"] = "Desactivado", ["3"] = "Asistencia: varias caras a la vez",
    };

    private static readonly Dictionary<string, string> LiveLevelLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["low"] = "Bajo", ["middle"] = "Medio", ["high"] = "Alto",
    };

    private static readonly Dictionary<string, string> EnvirLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["indoor"] = "Interior", ["other"] = "Otro (exterior)",
    };

    private static readonly Entry[] ReaderCatalog =
    [
        new("enable", "Lector habilitado", AccessSettingType.Boolean),
        new("cardReaderDescription", "Descripción", AccessSettingType.Info),
        new("defaultVerifyMode", "Modo de verificación de fábrica", AccessSettingType.Info, Labels: VerifyModeLabels),
        new("offlineCheckTime", "Detección de lector desconectado", AccessSettingType.Integer, "s",
            "Cuánto espera sin respuesta del lector antes de darlo por desconectado; 0 = sin detección."),
        new("swipeInterval", "Intervalo mínimo entre autenticaciones", AccessSettingType.Integer, "s",
            "Dos pasadas seguidas (tarjeta, huella o rostro) más juntas que esto se ignoran; 0 = sin límite."),
        new("independSwipeIntervals", "Intervalo mínimo por persona", AccessSettingType.Integer, "s",
            "Como el anterior, pero contado para cada persona por separado."),
        new("pressTimeout", "Borrar lo tecleado tras", AccessSettingType.Integer, "s",
            "Si alguien deja una clave a medias, el teclado se limpia pasado este tiempo."),
        new("enableFailAlarm", "Alarma por intentos fallidos", AccessSettingType.Boolean),
        new("maxReadCardFailNum", "Intentos fallidos antes de la alarma", AccessSettingType.Integer),
        new("enableTamperCheck", "Detección de sabotaje (tamper)", AccessSettingType.Boolean,
            Help: "Avisa si abren o arrancan el lector."),
        new("okLedPolarity", "Polaridad del LED de OK", AccessSettingType.Choice, Labels: PolarityLabels),
        new("errorLedPolarity", "Polaridad del LED de error", AccessSettingType.Choice, Labels: PolarityLabels),
        new("buzzerPolarity", "Polaridad del zumbador", AccessSettingType.Choice, Labels: PolarityLabels),
        new("buzzerTime", "Duración del zumbador", AccessSettingType.Integer, "s", "0 = suena hasta que se atienda."),
        new("enableReverseCardNo", "Invertir el número de tarjeta", AccessSettingType.Boolean,
            Help: "Lee los bytes del número al revés (lectores Wiegand de otra marca)."),
        new("fingerPrintCheckLevel", "Nivel de reconocimiento de huella", AccessSettingType.Choice,
            Help: "Tasa de falsa aceptación: más estricto es más seguro, pero rechaza más dedos.", Labels: FingerLevelLabels),
        new("fingerPrintImageQuality", "Calidad exigida a la huella", AccessSettingType.Choice, Labels: FingerQualityLabels),
        new("fingerPrintContrastTimeOut", "Tiempo de espera de la huella", AccessSettingType.Integer, "s", "255 = sin límite."),
        new("fingerPrintRecogizeInterval", "Intervalo entre huellas", AccessSettingType.Integer, "s", "255 = sin espera."),
        new("fingerPrintMatchFastMode", "Modo rápido de comparación de huella", AccessSettingType.Integer, null, "1 a 5; 255 = automático."),
        new("fingerPrintModuleSensitive", "Sensibilidad del módulo de huella", AccessSettingType.Integer, null, "1 a 8."),
        new("fingerPrintModuleLightCondition", "Iluminación del módulo de huella", AccessSettingType.Choice, Labels: LightLabels),
        new("faceRecogizeEnable", "Reconocimiento facial", AccessSettingType.Choice, Labels: FaceEnableLabels),
        new("envirMode", "Modo de aplicación del reconocimiento facial", AccessSettingType.Choice,
            Help: "Dónde está el terminal (HikCentral: Face Recognition Application Mode).", Labels: EnvirLabels),
        new("faceMatchThresholdN", "Umbral de coincidencia facial 1:N", AccessSettingType.Integer, "%",
            "Parecido mínimo contra todo el padrón (HikCentral: Face 1:N Matching Threshold)."),
        new("faceMatchThreshold1", "Umbral de coincidencia facial 1:1", AccessSettingType.Integer, "%",
            "Parecido mínimo contra la persona de la tarjeta o del número tecleado."),
        new("faceMatchNSecurityLevel", "Nivel de seguridad facial 1:N", AccessSettingType.Choice, Labels: SecurityLevelLabels),
        new("faceMatch1SecurityLevel", "Nivel de seguridad facial 1:1", AccessSettingType.Choice, Labels: SecurityLevelLabels),
        new("maskFaceMatchThresholdN", "Umbral 1:N con mascarilla", AccessSettingType.Integer, "%"),
        new("maskFaceMatchThreshold1", "Umbral 1:1 con mascarilla", AccessSettingType.Integer, "%"),
        new("faceQuality", "Calidad mínima del rostro", AccessSettingType.Integer),
        new("faceRecogizeTimeOut", "Tiempo de espera del reconocimiento facial", AccessSettingType.Integer, "s",
            "Cuánto intenta reconocer una cara antes de darse por vencido; 255 = sin límite."),
        new("faceRecogizeInterval", "Intervalo entre reconocimientos faciales", AccessSettingType.Integer, "s",
            "Después de reconocer a alguien, cuánto espera antes de intentar con la siguiente cara; 255 = sin espera."),
        new("livingBodyDetect", "Antisuplantación facial (detección de vida)", AccessSettingType.Boolean,
            Help: "Rechaza fotos y pantallas puestas frente a la cámara (HikCentral: Face Anti-Spoofing)."),
        new("liveDetLevelSet", "Nivel de antisuplantación", AccessSettingType.Choice,
            Help: "Más alto rechaza más intentos de engaño, y también a más personas reales.", Labels: LiveLevelLabels),
        new("enableLiveDetAntiAttack", "Bloqueo por intentos de suplantación", AccessSettingType.Boolean),
        new("liveDetAntiAttackCntLimit", "Intentos de suplantación antes del bloqueo", AccessSettingType.Integer),
        new("faceImageSensitometry", "Exposición de la cámara facial", AccessSettingType.Info),
        new("fingerPrintCapacity", "Cupo de huellas", AccessSettingType.Info),
        new("fingerPrintNum", "Huellas guardadas", AccessSettingType.Info),
        new("FPAlgorithmVersion", "Algoritmo de huella", AccessSettingType.Info),
        new("cardReaderVersion", "Versión del lector", AccessSettingType.Info),
        new("useLocalController", "Usa una controladora local", AccessSettingType.Info, Labels: YesNoLabels),
        new("localControllerID", "Controladora local", AccessSettingType.Info),
        new("localControllerReaderID", "Lector en la controladora local", AccessSettingType.Info),
        new("cardReaderChannel", "Canal del lector", AccessSettingType.Info),
    ];

    private static readonly HashSet<string> ReaderHidden = new(StringComparer.OrdinalIgnoreCase)
        { "cardReaderNo", "cardReaderFunction" };

    // ==================================================================
    // Capacidades (rangos y opciones)
    // ==================================================================

    private sealed record CapInfo(int? Min, int? Max, IReadOnlyList<string>? Options);

    private static async Task<JsonObject?> CapsAsync(HikvisionIsapiClient client, string path, CancellationToken ct)
    {
        string? json;
        try { json = await client.RequestAsync(HttpMethod.Get, path, ct: ct); }
        catch (DriverException) { return null; }   // sin permiso o función deshabilitada: se sigue sin rangos
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>Cuántos hay: el <c>@max</c> del campo de número, o el primer contador declarado.</summary>
    private static int? CountOf(JsonObject? caps, string rangeKey, string[] countKeys)
    {
        if (caps is null) return null;
        if (CapOf(caps, rangeKey)?.Max is { } max and > 0) return max;
        foreach (string key in countKeys)
            if (FindNode(caps, key, depth: 4) is JsonValue value && ToInt(value) is { } n and > 0) return n;
        return null;
    }

    /// <summary>Rango (<c>@min</c>/<c>@max</c>) y opciones (<c>@opt</c>) que el equipo declara para un campo.</summary>
    private static CapInfo? CapOf(JsonObject? caps, string key)
    {
        if (caps is null || FindNode(caps, key, depth: 4) is not JsonObject field) return null;
        int? min = Property(field, "@min") is JsonValue lo ? ToInt(lo) : null;
        int? max = Property(field, "@max") is JsonValue hi ? ToInt(hi) : null;
        List<string>? options = null;
        if (Property(field, "@opt") is { } opt)
        {
            options = opt switch
            {
                JsonArray array => array.Select(i => i?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList(),
                JsonValue value => (value.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                _ => null,
            };
            if (options is { Count: 0 }) options = null;
        }
        return min is null && max is null && options is null ? null : new CapInfo(min, max, options);
    }

    private static JsonNode? Property(JsonObject obj, string name)
    {
        foreach (var (key, value) in obj)
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    private static JsonNode? FindNode(JsonObject obj, string name, int depth)
    {
        if (Property(obj, name) is { } direct) return direct;
        if (depth <= 0) return null;
        foreach (var (_, value) in obj)
            if (value is JsonObject nested && FindNode(nested, name, depth - 1) is { } found) return found;
        return null;
    }

    private static int? ToInt(JsonValue value)
    {
        if (value.TryGetValue(out int n)) return n;
        if (value.TryGetValue(out long l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        if (value.TryGetValue(out double d) && d is >= int.MinValue and <= int.MaxValue) return (int)d;
        return value.TryGetValue(out string? s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : null;
    }

    private static bool? TryParseBool(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "on" or "yes" or "sí" or "si" => true,
        "false" or "0" or "off" or "no" => false,
        _ => null,
    };

    // ==================================================================
    // El documento del equipo, en JSON o en XML
    // ==================================================================

    /// <summary>
    /// Lee el documento de un bloque: primero en JSON y, si el firmware no lo
    /// da en esa forma (404, o un objeto sin parámetros), en XML. Null si el
    /// equipo no contesta el bloque de ninguna forma.
    /// </summary>
    private static async Task<ParamDoc?> LoadAsync(HikvisionIsapiClient client, string path, string rootName, CancellationToken ct)
    {
        string? json = null;
        try { json = await client.RequestAsync(HttpMethod.Get, path + "?format=json", ct: ct); }
        catch (DriverException) { /* se prueba en XML */ }
        if (json is not null && ParamDoc.FromJson(json, rootName) is { } fromJson && fromJson.Scalars().Count() > 1)
            return fromJson;

        string? xml = null;
        try { xml = await client.RequestAsync(HttpMethod.Get, path, ct: ct); }
        catch (DriverException) { /* sin permiso o sin la ruta */ }
        if (xml is not null && ParamDoc.FromXml(xml, rootName) is { } fromXml && fromXml.Scalars().Any())
            return fromXml;

        // Un JSON con un solo campo es mejor que nada (algún firmware muy acotado).
        return json is not null && ParamDoc.FromJson(json, rootName) is { } thin && thin.Scalars().Any() ? thin : null;
    }

    /// <summary>
    /// El documento de un bloque tal como lo entregó el equipo, en JSON o en
    /// XML, con lo justo para leer sus campos simples, cambiar algunos y
    /// devolverlo ENTERO y en el mismo orden.
    /// </summary>
    private sealed class ParamDoc
    {
        private readonly JsonNode? _jsonRoot;
        private readonly JsonObject? _json;
        private readonly XDocument? _xml;
        private readonly XElement? _element;

        private ParamDoc(JsonNode root, JsonObject obj) { _jsonRoot = root; _json = obj; }
        private ParamDoc(XDocument xml, XElement element) { _xml = xml; _element = element; }

        public static ParamDoc? FromJson(string text, string rootName)
        {
            JsonNode? root;
            try { root = JsonNode.Parse(text); }
            catch (JsonException) { return null; }
            if (root is not JsonObject obj) return null;
            // El documento viene envuelto ({"DoorParam": {...}}); si no, el raíz es el bloque.
            var inner = FindNode(obj, rootName, depth: 1) as JsonObject ?? obj;
            return new ParamDoc(root, inner);
        }

        public static ParamDoc? FromXml(string text, string rootName)
        {
            XDocument doc;
            try { doc = XDocument.Parse(text); }
            catch (System.Xml.XmlException) { return null; }
            var element = doc.Root?.Name.LocalName == rootName
                ? doc.Root
                : doc.Descendants().FirstOrDefault(e => e.Name.LocalName == rootName);
            return element is null ? null : new ParamDoc(doc, element);
        }

        /// <summary>Los campos simples del bloque, en el orden del equipo (listas y objetos anidados, como texto).</summary>
        public IEnumerable<(string Key, string? Value)> Scalars()
        {
            if (_json is not null)
            {
                foreach (var (key, node) in _json)
                {
                    if (node is null) continue;
                    yield return (key, node switch
                    {
                        JsonValue value => ValueText(value),
                        JsonArray array => string.Join(", ", array.Select(i => i is JsonValue v ? ValueText(v) : i?.ToJsonString())),
                        _ => node.ToJsonString(),
                    });
                }
            }
            else if (_element is not null)
            {
                foreach (var child in _element.Elements())
                    if (!child.HasElements) yield return (child.Name.LocalName, child.Value.Trim());
            }
        }

        private static string? ValueText(JsonValue value)
        {
            if (value.TryGetValue(out bool b)) return b ? "true" : "false";
            if (value.TryGetValue(out string? s)) return s;
            return value.ToJsonString();
        }

        public bool Has(string key) => _json is not null ? Property(_json, key) is not null : Child(key) is not null;

        public string? Get(string key)
        {
            if (_json is not null)
                return Property(_json, key) is JsonValue value ? ValueText(value) : null;
            return Child(key)?.Value.Trim();
        }

        /// <summary>Un campo de lista (<c>cardReaderFunction</c>); vacío si no está o no es lista.</summary>
        public IReadOnlyList<string> GetList(string key)
        {
            if (_json is not null)
                return Property(_json, key) is JsonArray array
                    ? array.Select(i => i is JsonValue v ? ValueText(v) : null).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList()
                    : [];
            // En XML la lista viene como texto separado por comas, o como elementos repetidos.
            var child = Child(key);
            if (child is null) return [];
            return child.HasElements
                ? child.Elements().Select(e => e.Value.Trim()).Where(s => s.Length > 0).ToList()
                : child.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        public void SetBool(string key, bool value) => Set(key, JsonValue.Create(value), value ? "true" : "false");
        public void SetInt(string key, int value) => Set(key, JsonValue.Create(value), value.ToString(CultureInfo.InvariantCulture));
        public void SetString(string key, string value) => Set(key, JsonValue.Create(value), value);

        /// <summary>
        /// Una opción de lista, conservando el tipo con que el equipo entregó el
        /// campo: si era un número (nivel 1..18) se escribe un número, si era un
        /// booleano, un booleano, y si no, texto. En XML todo es texto.
        /// </summary>
        public void SetChoice(string key, string value)
        {
            if (_json is not null && Property(_json, key) is JsonValue current)
            {
                if (current.TryGetValue(out bool _) && TryParseBool(value) is { } flag) { SetBool(key, flag); return; }
                if (!current.TryGetValue(out string? _) && current.TryGetValue(out double _) &&
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) { SetInt(key, n); return; }
            }
            SetString(key, value);
        }

        private void Set(string key, JsonNode node, string text)
        {
            if (_json is not null)
            {
                // Se conserva el nombre con la grafía del equipo y su posición.
                string actual = _json.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
                _json[actual] = node;
                return;
            }
            var child = Child(key);
            if (child is not null) child.Value = text;
            else _element!.Add(new XElement(_element.Name.Namespace + key, text));
        }

        private XElement? Child(string key) =>
            _element?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(key, StringComparison.OrdinalIgnoreCase));

        /// <summary>El documento listo para el PUT: cuerpo, tipo y sufijo de la ruta (<c>?format=json</c> o nada).</summary>
        public (string Body, string ContentType, string PathSuffix) Serialize()
        {
            if (_jsonRoot is not null)
                return (_jsonRoot.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
                    "application/json", "?format=json");
            string declaration = _xml!.Declaration?.ToString() ?? "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";
            return (declaration + _xml.Root!.ToString(SaveOptions.DisableFormatting), "application/xml", "");
        }
    }
}
