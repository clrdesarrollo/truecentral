using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Lo que un equipo de control de acceso Hikvision DECLARA en sus rutas de
/// capacidades oficiales (guía ISAPI "Person-Based Access Control"):
/// <list type="bullet">
/// <item><c>/ISAPI/AccessControl/capabilities</c> (XML_Cap_AccessControl): un
/// <c>isSupport…</c> por función —personas, tarjetas, rostros, borrado completo,
/// estado de puertas, historial, captura de tarjeta…—.</item>
/// <item><c>UserInfo</c>, <c>CardInfo</c> y <c>FDLib</c>: <c>supportFunction</c>
/// (alta, edición, baja, búsqueda y <c>setUp</c>, que crea o edita en un paso),
/// cupos y largos máximos.</item>
/// <item><c>AcsEvent</c>: cuántos eventos entrega por consulta y qué condiciones
/// de búsqueda entiende.</item>
/// <item><c>RemoteControl/door</c>: qué órdenes de puerta acepta.</item>
/// <item><c>FingerPrint/Delete</c> y <c>UserInfoDetail/Delete</c>: modos de
/// borrado.</item>
/// <item><c>DeployInfo</c>: quién está suscrito a sus eventos (otras
/// plataformas ocupan los mismos cupos).</item>
/// </list>
/// Se lee UNA vez (al validar el equipo, o la primera vez que el driver lo
/// necesita) y con eso el driver elige la ruta en vez de probarlas en orden.
/// Lo que el equipo no declara queda en null: ahí el driver sigue con el
/// camino ya probado contra equipos reales, sin inventar un sí ni un no.
/// </summary>
internal sealed class HikvisionAccessProfile
{
    public DateTime ReadAtUtc { get; } = DateTime.UtcNow;

    /// <summary><c>isSupport…</c> de XML_Cap_AccessControl tal como los declara el equipo.</summary>
    public Dictionary<string, bool> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>El equipo contestó XML_Cap_AccessControl.</summary>
    public bool HasAccessCaps { get; private set; }

    /// <summary><c>supportFunction</c> de cada recurso (post, delete, put, get, setUp); null = no contestó.</summary>
    public HashSet<string>? UserFunctions { get; private set; }
    public HashSet<string>? CardFunctions { get; private set; }
    public HashSet<string>? FaceFunctions { get; private set; }

    public int? UserCapacity { get; private set; }
    public int? CardCapacity { get; private set; }
    public int? FaceCapacity { get; private set; }

    /// <summary>Tope del nombre de la persona (bytes) y del nombre en la biblioteca de rostros.</summary>
    public int? NameMaxBytes { get; private set; }
    public int? FaceNameMaxBytes { get; private set; }
    public (int Min, int Max)? PinLength { get; private set; }

    /// <summary><c>maxResults</c> máximo de la búsqueda de eventos.</summary>
    public int? EventMaxResults { get; private set; }
    /// <summary>Campos que el equipo declara en la condición de búsqueda de eventos (picEnable, beginSerialNo…).</summary>
    public HashSet<string>? EventCondFields { get; private set; }

    /// <summary>Órdenes de puerta que acepta (open, close, alwaysOpen, alwaysClose…).</summary>
    public HashSet<string>? DoorCommands { get; private set; }

    public (int Min, int Max)? FingerprintReaders { get; private set; }
    public HashSet<string>? FingerprintDeleteModes { get; private set; }
    public HashSet<string>? UserDetailDeleteModes { get; private set; }

    /// <summary>Suscripciones a eventos abiertas en el equipo (tipo y dirección).</summary>
    public List<(int Type, string Address)>? Arming { get; private set; }

    public List<string> Notes { get; } = [];

    public bool? Flag(string name) => Flags.TryGetValue(name, out bool value) ? value : null;

    /// <summary>Hay lectura suficiente para decidir algo. Sin ella el driver sigue por el camino probado.</summary>
    public bool Usable => HasAccessCaps || UserFunctions is not null;

    /// <summary>Crea o edita la persona en una sola llamada (<c>PUT UserInfo/SetUp</c>).</summary>
    public bool CanSetUpUser => UserFunctions?.Contains("setUp") == true;

    /// <summary>Borra la persona con sus tarjetas, huellas, rostro y permisos (<c>UserInfoDetail/Delete</c>).</summary>
    public bool CanDeleteUserDetail =>
        Flag("isSupportUserInfoDetailDelete") != false && UserDetailDeleteModes?.Contains("byEmployeeNo") == true;

    /// <summary>¿Maneja huellas? null = no lo declara.</summary>
    public bool? Fingerprints => Flag("isSupportFingerPrintCfg") ?? (FingerprintReaders is not null ? true : null);
    public bool? Cards => Flag("isSupportCardInfo") ?? (CardFunctions is not null ? true : null);
    public bool? Faces => Flag("isSupportFDLib") ?? (FaceFunctions is not null ? true : null);

    // ------------------------------------------------------------------
    // Lectura
    // ------------------------------------------------------------------

    internal const string AccessCapsPath = "/ISAPI/AccessControl/capabilities";
    private const string UserCapsPath = "/ISAPI/AccessControl/UserInfo/capabilities?format=json";
    private const string CardCapsPath = "/ISAPI/AccessControl/CardInfo/capabilities?format=json";
    private const string FaceCapsPath = "/ISAPI/Intelligent/FDLib/capabilities?format=json";
    private const string EventCapsPath = "/ISAPI/AccessControl/AcsEvent/capabilities?format=json";
    private const string DoorCapsPath = "/ISAPI/AccessControl/RemoteControl/door/capabilities";
    private const string FingerCapsPath = "/ISAPI/AccessControl/FingerPrintCfg/capabilities?format=json";
    private const string FingerDeleteCapsPath = "/ISAPI/AccessControl/FingerPrint/Delete/capabilities?format=json";
    private const string UserDetailCapsPath = "/ISAPI/AccessControl/UserInfoDetail/Delete/capabilities?format=json";
    private const string DeployInfoPath = "/ISAPI/AccessControl/DeployInfo";

    /// <summary>
    /// Lee todas las capacidades. Son solo consultas (GET) y cada una es
    /// tolerante: una ruta que el firmware no tiene, o que el usuario no puede
    /// leer, deja su parte en null y no corta las demás.
    /// </summary>
    public static async Task<HikvisionAccessProfile> ReadAsync(HikvisionIsapiClient client, CancellationToken ct)
    {
        var p = new HikvisionAccessProfile();

        // La guía la define en XML (sin ?format=json); se acepta JSON por si un
        // firmware la contesta así.
        if (await TryGetAsync(client, AccessCapsPath, ct) is { } accessCaps)
            p.ReadAccessCaps(accessCaps);
        if (!p.HasAccessCaps)
            p.Notes.Add("El equipo no entregó su ficha general de control de acceso " +
                        $"({AccessCapsPath}): lo que no declare en las demás rutas se prueba al usarlo.");

        await p.ReadJsonAsync(client, UserCapsPath, ct, root =>
        {
            var user = HikvisionAlarmPanelDriver.FindObject(root, "UserInfo") ?? root;
            p.UserFunctions = Options(Child(user, "supportFunction")) ?? [];
            p.UserCapacity = FirstInt(user, "maxRecordNum", 3);
            p.NameMaxBytes = Bound(Child(user, "name"), "max");
            if (Bound(Child(user, "password"), "min") is { } min && Bound(Child(user, "password"), "max") is { } max)
                p.PinLength = (min, max);
        });

        await p.ReadJsonAsync(client, CardCapsPath, ct, root =>
        {
            var card = HikvisionAlarmPanelDriver.FindObject(root, "CardInfo") ?? root;
            p.CardFunctions = Options(Child(card, "supportFunction")) ?? [];
            p.CardCapacity = FirstInt(card, "maxRecordNum", 3);
        });

        await p.ReadJsonAsync(client, FaceCapsPath, ct, root =>
        {
            p.FaceFunctions = Options(Find(root, "supportFDFunction", 3)) ?? [];
            p.FaceCapacity = FirstInt(root, "FDRecordDataMaxNum", 3);
            p.FaceNameMaxBytes = FirstInt(root, "FDNameMaxLen", 3);
        });

        await p.ReadJsonAsync(client, EventCapsPath, ct, root =>
        {
            if (Find(root, "AcsEventCond", 3) is not { ValueKind: JsonValueKind.Object } cond) return;
            p.EventCondFields = cond.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            p.EventMaxResults = Bound(Child(cond, "maxResults"), "max");
        });

        if (await TryGetAsync(client, DoorCapsPath, ct) is { } doorCaps && TryXml(doorCaps) is { } doorXml &&
            doorXml.Descendants().FirstOrDefault(e => e.Name.LocalName == "cmd")?.Attribute("opt")?.Value is { } cmds)
            p.DoorCommands = Split(cmds);

        await p.ReadJsonAsync(client, FingerCapsPath, ct, root =>
        {
            var readers = Find(root, "enableCardReader", 4);
            if (Bound(readers, "min") is { } min && Bound(readers, "max") is { } max) p.FingerprintReaders = (min, max);
            else p.FingerprintReaders = (1, 1);   // contesta la ruta pero no dice dónde: al menos uno
        });

        await p.ReadJsonAsync(client, FingerDeleteCapsPath, ct, root =>
            p.FingerprintDeleteModes = Options(Find(root, "mode", 3)) ?? []);

        await p.ReadJsonAsync(client, UserDetailCapsPath, ct, root =>
            p.UserDetailDeleteModes = Options(Find(root, "mode", 3)) ?? []);

        if (p.Flag("isSupportDeployInfo") != false &&
            await TryGetAsync(client, DeployInfoPath, ct) is { } deploy && TryXml(deploy) is { } deployXml)
        {
            p.Arming = deployXml.Descendants().Where(e => e.Name.LocalName == "Content")
                .Select(c => (
                    Type: int.TryParse(c.Elements().FirstOrDefault(e => e.Name.LocalName == "deployType")?.Value, out int t) ? t : -1,
                    Address: c.Elements().FirstOrDefault(e => e.Name.LocalName == "ipAddr")?.Value?.Trim() ?? ""))
                .ToList();
        }

        return p;
    }

    private void ReadAccessCaps(string body)
    {
        string text = body.TrimStart();
        if (text.StartsWith('<'))
        {
            if (TryXml(body) is not { } xml) return;
            HasAccessCaps = true;
            foreach (var e in xml.Descendants())
            {
                // Los isSupport con hijos (condiciones, rangos) no son un sí/no.
                if (!e.Name.LocalName.StartsWith("isSupport", StringComparison.OrdinalIgnoreCase) || e.HasElements) continue;
                if (bool.TryParse(e.Value.Trim(), out bool value)) SetFlag(e.Name.LocalName, value);
            }
        }
        else if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                HasAccessCaps = true;
                CollectJsonFlags(doc.RootElement, 6);
            }
            catch (JsonException) { /* no se entiende: queda sin ficha */ }
        }
    }

    private void CollectJsonFlags(JsonElement element, int depth)
    {
        if (depth < 0 || element.ValueKind != JsonValueKind.Object) return;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.StartsWith("isSupport", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                SetFlag(property.Name, property.Value.ValueKind == JsonValueKind.True);
            else
                CollectJsonFlags(property.Value, depth - 1);
        }
    }

    /// <summary>La ficha repite algunas marcas (isSupportDeployInfo va dos veces): basta un sí.</summary>
    private void SetFlag(string name, bool value) =>
        Flags[name] = value || (Flags.TryGetValue(name, out bool previous) && previous);

    private async Task ReadJsonAsync(HikvisionIsapiClient client, string path, CancellationToken ct, Action<JsonElement> read)
    {
        if (await TryGetAsync(client, path, ct) is not { } json || !json.TrimStart().StartsWith('{')) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            // Un 200 con statusCode de error es "no lo tengo", no una ficha.
            if (HikvisionAlarmPanelDriver.GetInt(doc.RootElement, "statusCode") is { } code && code != 1) return;
            read(doc.RootElement);
        }
        catch (JsonException) { /* respuesta que no se entiende: esa parte queda sin declarar */ }
    }

    /// <summary>GET tolerante: null si la ruta no existe, el usuario no puede leerla o el equipo falla.</summary>
    private static async Task<string?> TryGetAsync(HikvisionIsapiClient client, string path, CancellationToken ct)
    {
        try { return await client.RequestAsync(HttpMethod.Get, path, ct: ct); }
        catch (DriverException) { return null; }
    }

    private static XDocument? TryXml(string body)
    {
        try { return XDocument.Parse(body); }
        catch (System.Xml.XmlException) { return null; }
    }

    // ------------------------------------------------------------------
    // Ayudas de lectura del JSON de capacidades
    // ------------------------------------------------------------------

    private static JsonElement? Child(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return null;
    }

    /// <summary>Primera propiedad con ese nombre a cualquier profundidad (acotada).</summary>
    private static JsonElement? Find(JsonElement element, string name, int depth)
    {
        if (depth < 0 || element.ValueKind != JsonValueKind.Object) return null;
        if (Child(element, name) is { } direct) return direct;
        foreach (var property in element.EnumerateObject())
            if (Find(property.Value, name, depth - 1) is { } nested)
                return nested;
        return null;
    }

    private static int? FirstInt(JsonElement element, string name, int depth) =>
        Find(element, name, depth) is { } value ? AsInt(value) : null;

    /// <summary><c>@min</c>/<c>@max</c> (o <c>min</c>/<c>max</c>) de un campo de capacidades.</summary>
    private static int? Bound(JsonElement? field, string which)
    {
        if (field is not { ValueKind: JsonValueKind.Object } f) return null;
        return (Child(f, "@" + which) ?? Child(f, which)) is { } value ? AsInt(value) : null;
    }

    private static int? AsInt(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt32(out int n) => n,
        JsonValueKind.String when int.TryParse(value.GetString(), out int n) => n,
        _ => null,
    };

    /// <summary>Opciones declaradas: <c>{"@opt": "a,b"}</c>, <c>"a,b"</c> o <c>["a","b"]</c>.</summary>
    private static HashSet<string>? Options(JsonElement? field)
    {
        if (field is not { } f) return null;
        if (f.ValueKind == JsonValueKind.Object)
            return (Child(f, "@opt") ?? Child(f, "opt")) is { } opt ? Options(opt) : null;
        if (f.ValueKind == JsonValueKind.String) return Split(f.GetString() ?? "");
        if (f.ValueKind == JsonValueKind.Array)
            return f.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return null;
    }

    private static HashSet<string> Split(string options) =>
        options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------
    // Ficha para mostrar
    // ------------------------------------------------------------------

    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-CL");

    private static string Count(int value) => value.ToString("N0", Spanish);

    /// <summary>Nombre en español de las marcas que el VMS sabe explicar; las demás se muestran por su nombre ISAPI.</summary>
    private static readonly Dictionary<string, string> FlagLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["isSupportUserInfo"] = "Personas",
        ["isSupportCardInfo"] = "Tarjetas",
        ["isSupportFDLib"] = "Rostros",
        ["isSupportFingerPrintCfg"] = "Huellas",
        ["isSupportFingerPrintDelete"] = "Borrado de huellas",
        ["isSupportUserInfoDetailDelete"] = "Borrado completo de personas",
        ["isSupportCaptureCardInfo"] = "Leer una tarjeta en el lector",
        ["isSupportCaptureFingerPrint"] = "Registrar una huella en el equipo",
        ["isSupportCaptureFace"] = "Tomar la foto del rostro en el equipo",
        ["isSupportCaptureInfraredFace"] = "Tomar el rostro infrarrojo en el equipo",
        ["isSupportRemoteControlDoor"] = "Órdenes remotas de puerta",
        ["isSupportRemoteControlBuzzer"] = "Zumbador remoto",
        ["isSupportAcsWorkStatus"] = "Estado de puertas y cerraduras",
        ["isSupportDoorCfg"] = "Parámetros de las puertas",
        ["isSupportCardReaderCfg"] = "Parámetros de los lectores",
        ["isSupportUserRightPlanTemplate"] = "Horarios de acceso por persona",
        ["isSupportUserRightHolidayGroupCfg"] = "Feriados en los horarios de acceso",
        ["isSupportAcsEvent"] = "Historial de accesos",
        ["isSupportAcsEventTotalNum"] = "Conteo de eventos",
        ["isSupportDeployInfo"] = "Suscripciones a eventos",
        ["isSupportRemoteCheck"] = "Verificación remota de accesos",
        ["isSupportAcsCfg"] = "Parámetros generales de control de acceso",
        ["isSupportWiegandCfg"] = "Wiegand",
        ["isSupportAntiSneakCfg"] = "Antirretorno",
        ["isSupportMultiCardCfg"] = "Autenticación con varias tarjetas",
        ["isSupportMultiDoorInterLockCfg"] = "Esclusa (enclavamiento de puertas)",
        ["isSupportFaceRecognizeMode"] = "Modo de reconocimiento facial",
        ["isSupportMaskDetection"] = "Detección de mascarilla",
        ["isSupportSafetyHelmetDetection"] = "Detección de casco",
        ["isSupportTemperatureMeasureCfg"] = "Medición de temperatura",
        ["isSupportAttendanceStatusModeCfg"] = "Estados de asistencia",
        ["isSupportCardOperations"] = "Operaciones sobre la tarjeta (M1/CPU)",
        ["isSupportM1CardEncryptCfg"] = "Cifrado de tarjetas M1",
        ["isSupportIdentityTerminal"] = "Terminal de reconocimiento facial",
        ["isSupportUserDataImport"] = "Importar personas desde un archivo",
        ["isSupportUserDataExport"] = "Exportar personas a un archivo",
        ["isSupportFactoryReset"] = "Restablecer de fábrica",
        ["isSupportLockTypeCfg"] = "Tipo de cerradura",
        ["isSupportCaptureIDInfo"] = "Leer cédula de identidad",
        ["isSupportBluetooth"] = "Bluetooth",
        ["isSupportNFCCfg"] = "NFC",
        ["isSupportRFCardCfg"] = "Tarjetas de radiofrecuencia",
    };

    private AccessCapabilityState StateOf(bool? value) => value switch
    {
        true => AccessCapabilityState.Supported,
        false => AccessCapabilityState.NotSupported,
        null => AccessCapabilityState.NotDeclared,
    };

    private AccessCapabilityItem FlagItem(string flag, string? detail = null) =>
        new(FlagLabels.GetValueOrDefault(flag, flag), StateOf(Flag(flag)), detail, $"{AccessCapsPath} · {flag}");

    private static string Functions(HashSet<string> functions)
    {
        var names = new List<string>();
        if (functions.Contains("post")) names.Add("alta");
        if (functions.Contains("put")) names.Add("edición");
        if (functions.Contains("delete")) names.Add("baja");
        if (functions.Contains("get")) names.Add("búsqueda");
        if (functions.Contains("setUp")) names.Add("alta o edición en un paso");
        return names.Count == 0 ? "sin funciones declaradas" : string.Join(", ", names);
    }

    internal static string DoorCommandName(string command) => command switch
    {
        "open" => "abrir",
        "close" => "cerrar",
        "alwaysOpen" => "abierta permanente",
        "alwaysClose" => "bloqueada permanente",
        "visitorCallLadder" => "llamar ascensor (visita)",
        "householdCallLadder" => "llamar ascensor (residente)",
        _ => command,
    };

    private static string ArmingName(int type) => type switch
    {
        0 => "plataforma con eventos fuera de línea",
        1 => "suscripción en tiempo real",
        2 => "ISAPI",
        _ => "tipo desconocido",
    };

    public AccessCapabilityProfile ToDisplay()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AccessCapabilityItem Mark(string flag, string? detail = null)
        {
            used.Add(flag);
            return FlagItem(flag, detail);
        }

        // ---- Padrón ----
        var people = new List<AccessCapabilityItem>
        {
            new("Personas", StateOf(Flag("isSupportUserInfo") ?? (UserFunctions is null ? null : true)),
                Join(UserCapacity is { } users ? $"cupo de {Count(users)} personas" : null,
                     UserFunctions is { } uf ? Functions(uf) : null),
                UserCapsPath),
            new("Alta o edición en un solo paso",
                UserFunctions is null ? AccessCapabilityState.NotDeclared
                    : CanSetUpUser ? AccessCapabilityState.Supported : AccessCapabilityState.NotSupported,
                UserFunctions is null ? null
                    : CanSetUpUser ? "El VMS escribe cada persona con una sola llamada (UserInfo/SetUp)."
                    : "El VMS pregunta si la persona existe y después la crea o la edita.",
                $"{UserCapsPath} · supportFunction"),
            new("Borrado completo de la persona (con tarjetas, huellas y rostro)",
                CanDeleteUserDetail ? AccessCapabilityState.Supported
                    : Flag("isSupportUserInfoDetailDelete") == false || UserDetailDeleteModes is not null
                        ? AccessCapabilityState.NotSupported : AccessCapabilityState.NotDeclared,
                CanDeleteUserDetail
                    ? "El VMS borra todo en un paso y espera a que el equipo confirme que terminó."
                    : "El VMS borra tarjetas, huellas y rostro por separado y comprueba que no quede nada.",
                UserDetailCapsPath),
            new("Largo del nombre", NameMaxBytes is null ? AccessCapabilityState.NotDeclared : AccessCapabilityState.Supported,
                NameMaxBytes is { } nameMax ? $"hasta {nameMax} bytes; lo que sobra se acorta" : "el VMS lo acota a 32 bytes",
                $"{UserCapsPath} · name"),
            new("Clave numérica (PIN)", PinLength is null ? AccessCapabilityState.NotDeclared : AccessCapabilityState.Supported,
                PinLength is { } pin ? $"de {pin.Min} a {pin.Max} caracteres" : null, $"{UserCapsPath} · password"),
        };
        used.Add("isSupportUserInfo");
        used.Add("isSupportUserInfoDetailDelete");

        // ---- Credenciales ----
        var credentials = new List<AccessCapabilityItem>
        {
            new("Tarjetas", StateOf(Cards),
                Join(CardCapacity is { } cards ? $"cupo de {Count(cards)} tarjetas" : null,
                     CardFunctions is { } cf ? Functions(cf) : null),
                CardCapsPath),
            Mark("isSupportCaptureCardInfo"),
            new("Huellas", StateOf(Fingerprints),
                FingerprintReaders is { } r
                    ? (r.Min == r.Max ? $"se graban en el lector {r.Min}" : $"se graban en los lectores {r.Min} a {r.Max}")
                    : null,
                FingerCapsPath),
            new("Borrado de huellas", StateOf(Flag("isSupportFingerPrintDelete") ?? (FingerprintDeleteModes is null ? null : FingerprintDeleteModes.Count > 0)),
                FingerprintDeleteModes is { Count: > 0 } modes
                    ? "modos: " + string.Join(", ", modes.Select(m => m switch
                    {
                        "byEmployeeNo" => "por persona",
                        "byCardReader" => "por lector",
                        _ => m,
                    }))
                    : null,
                FingerDeleteCapsPath),
            Mark("isSupportCaptureFingerPrint"),
            new("Rostros", StateOf(Faces),
                Join(FaceCapacity is { } faces ? $"cupo de {Count(faces)} rostros" : null,
                     FaceFunctions is { } ff ? Functions(ff) : null),
                FaceCapsPath),
            Mark("isSupportCaptureFace"),
        };
        used.Add("isSupportCardInfo");
        used.Add("isSupportFingerPrintCfg");
        used.Add("isSupportFingerPrintDelete");
        used.Add("isSupportFDLib");

        // ---- Puertas y horarios ----
        var doors = new List<AccessCapabilityItem>
        {
            new("Órdenes remotas de puerta",
                StateOf(Flag("isSupportRemoteControlDoor") ?? (DoorCommands is null ? null : DoorCommands.Count > 0)),
                DoorCommands is { Count: > 0 } commands ? string.Join(", ", commands.Select(DoorCommandName)) : null,
                DoorCapsPath),
            Mark("isSupportAcsWorkStatus"),
            Mark("isSupportDoorCfg"),
            Mark("isSupportCardReaderCfg"),
            Mark("isSupportUserRightPlanTemplate"),
            Mark("isSupportUserRightHolidayGroupCfg"),
        };
        used.Add("isSupportRemoteControlDoor");

        // ---- Eventos ----
        var events = new List<AccessCapabilityItem>
        {
            new("Historial de accesos", StateOf(Flag("isSupportAcsEvent") ?? (EventCondFields is null ? null : true)),
                EventMaxResults is { } maxEvents ? $"hasta {maxEvents} eventos por consulta" : null, EventCapsPath),
            Mark("isSupportAcsEventTotalNum"),
            Mark("isSupportRemoteCheck"),
        };
        used.Add("isSupportAcsEvent");
        if (Arming is { } arming)
        {
            events.Add(new("Suscripciones a sus eventos abiertas ahora", AccessCapabilityState.Supported,
                arming.Count == 0
                    ? "ninguna"
                    : string.Join("; ", arming.Select(a => $"{ArmingName(a.Type)} desde {(a.Address.Length > 0 ? a.Address : "dirección desconocida")}")) +
                      ". El equipo admite pocas a la vez: si otra plataforma ocupa los cupos, la escucha en vivo del VMS puede fallar.",
                DeployInfoPath));
            used.Add("isSupportDeployInfo");
        }

        var groups = new List<AccessCapabilityGroup>
        {
            new("Padrón de personas", people),
            new("Credenciales", credentials),
            new("Puertas y horarios", doors),
            new("Eventos", events),
        };

        // El resto de lo que el equipo dice que SÍ tiene: no lo usa el VMS, pero
        // muestra qué se le puede pedir más adelante.
        var others = Flags.Where(f => f.Value && !used.Contains(f.Key))
            .Select(f => FlagItem(f.Key))
            .OrderBy(i => i.Label, StringComparer.Create(Spanish, ignoreCase: true))
            .ToList();
        if (others.Count > 0) groups.Add(new("Otras funciones que declara el equipo", others));

        return new AccessCapabilityProfile(ReadAtUtc, groups, Notes);
    }

    private static string? Join(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return present.Count == 0 ? null : string.Join(" · ", present);
    }
}
