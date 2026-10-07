using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrueCentralVms.Core.Contracts;

// ---------------------------------------------------------------------------
// Catálogo de licenciamiento (fuente única)
// ---------------------------------------------------------------------------
// Cada característica licenciable se define UNA sola vez, aquí, junto a su
// constante de LicenseFeatures; también los packages comerciales estándar.
// El producto "truecentral" del servidor de licencias no se arma a mano:
// importa este catálogo como JSON (TrueCentralVms.Server.exe
// --export-license-catalog <archivo>, que Jenkins genera en cada build) y
// bloquea en su web todo lo que viene de aquí. Así las claves que el VMS lee
// del .lic y las que el servidor firma no pueden divergir.
//
// Reglas (las hace cumplir el servidor de licencias al importar):
//   - Subir Version con CUALQUIER cambio de este archivo: se rechaza un
//     catálogo con el mismo número y otro contenido, o uno más antiguo.
//   - Nunca se borra: una característica o package que sale del catálogo
//     queda "retirado" (no se ofrece en licencias nuevas). Las licencias ya
//     emitidas guardan sus valores y no cambian con el catálogo.
//   - El tipo de una clave existente no se puede cambiar: usar otra clave.
//   - El JSON lleva la clave pública que compila el VMS: el servidor de
//     licencias no importa el catálogo si firmaría con otra (o si tendría que
//     generar un par nuevo), porque el VMS rechazaría sus licencias.

/// <summary>Tipo de valor de una característica licenciable.</summary>
public enum LicenseFeatureType
{
    /// <summary>Módulo habilitado o no (true/false).</summary>
    Boolean,
    /// <summary>Cupo (entero no negativo). Las expansiones lo suman.</summary>
    Integer,
}

/// <summary>
/// Característica licenciable. <paramref name="Default"/> es lo que trae una
/// licencia base sin package; <paramref name="Category"/> agrupa en el
/// certificado PDF (las de una misma categoría van seguidas).
/// </summary>
public sealed record LicenseFeatureDefinition(
    string Key, string Name, string Category, LicenseFeatureType Type, object Default, string Unit);

/// <summary>
/// Package comercial. Uno base es un preset completo (lo que no declara toma
/// el valor por defecto); una expansión (<paramref name="Addon"/>) SUMA sus
/// valores a la licencia base, así que solo declara lo que aporta.
/// </summary>
public sealed record LicensePackageDefinition(
    string Code, string Name, bool Addon, IReadOnlyList<KeyValuePair<string, object>> Values);

/// <summary>Catálogo de licenciamiento del producto TrueCentral (ver el encabezado del archivo).</summary>
public static class LicenseCatalog
{
    /// <summary>Versión del catálogo: subirla con cualquier cambio de este archivo.</summary>
    public const int Version = 1;

    /// <summary>Formato del JSON exportado (lo valida el servidor de licencias).</summary>
    public const int SchemaVersion = 1;

    // Identidad del producto: el servidor de licencias la actualiza en cada importación.
    public const string ProductName = "CLRobotics TrueCentral VMS";
    public const string ProductDescription =
        "Plataforma de video, alarmas, control de acceso, muro de video, parlantes IP y citofonía";

    // Valores INICIALES: solo se usan al crear el producto en el servidor de
    // licencias; después se ajustan en su web (política y texto comercial).
    public const int HeartbeatIntervalDays = 7;
    public const int GracePeriodDays = 30;
    public const string DocumentNotes =
        "Activación:\n" +
        "1. Instale CLRobotics TrueCentral VMS en el servidor de destino.\n" +
        "2. En la administración web, Sistema → Licencia, ingrese el código de activación " +
        "de este certificado (activación en línea).\n" +
        "3. Si el servidor no tiene salida a internet, genere ahí la \"solicitud de activación\" " +
        "(.req), envíela a soporte@clrobotics.cl y luego importe el archivo .lic que recibirá.\n\n" +
        "Las expansiones (canales, decodificadores, paneles, parlantes, frentes de citofonía) " +
        "se suman automáticamente a esta licencia: no requieren activación aparte.";

    /// <summary>Características, en el orden del certificado y del backoffice.</summary>
    public static readonly IReadOnlyList<LicenseFeatureDefinition> Features =
    [
        Int(LicenseFeatures.MaxUsers, "Usuarios del sistema", "Base", 5, "Usuarios"),
        Int(LicenseFeatures.MaxClientSessions, "Clientes conectados simultáneos", "Base", 2, "Clientes"),

        Bool(LicenseFeatures.ModuleVideo, "Módulo de video", "Video", true),
        Int(LicenseFeatures.VideoChannels, "Canales de video", "Video", 8, "Canales"),
        Bool(LicenseFeatures.ModulePlayback, "Reproducción remota de grabaciones", "Video", true),
        Bool(LicenseFeatures.ModuleAnpr, "Reconocimiento de patentes (ANPR)", "Video", false),
        Int(LicenseFeatures.AnprChannels, "Fuentes ANPR", "Video", 0, "Fuentes"),

        Bool(LicenseFeatures.ModuleAlarms, "Módulo de paneles de alarma", "Alarmas", false),
        Int(LicenseFeatures.AlarmPanels, "Paneles de alarma", "Alarmas", 0, "Paneles"),

        Bool(LicenseFeatures.ModuleAccess, "Módulo de control de acceso", "Control de acceso", false),
        Int(LicenseFeatures.AccessDoors, "Puertas / lectores", "Control de acceso", 0, "Puertas"),

        Bool(LicenseFeatures.ModuleVideowall, "Módulo de muro de video", "Muro de video", false),
        Int(LicenseFeatures.Videowalls, "Muros de video", "Muro de video", 0, "Muros"),
        Int(LicenseFeatures.VideowallDecoders, "Decodificadores", "Muro de video", 0, "Decodificadores"),

        Bool(LicenseFeatures.ModuleSpeakers, "Módulo de parlantes IP", "Audio", false),
        Int(LicenseFeatures.SpeakerChannels, "Parlantes IP", "Audio", 0, "Parlantes"),

        Bool(LicenseFeatures.ModuleIntercom, "Módulo de citofonía", "Citofonía", false),
        Int(LicenseFeatures.IntercomDevices, "Frentes de citofonía", "Citofonía", 0, "Frentes"),

        Bool(LicenseFeatures.ModuleAutomation, "Módulo de automatizaciones", "Automatización", false),
        Int(LicenseFeatures.AutomationRules, "Automatizaciones", "Automatización", 0, "Reglas"),
    ];

    /// <summary>Packages estándar. Los especiales para un cliente se crean en la web del servidor de licencias.</summary>
    public static readonly IReadOnlyList<LicensePackageDefinition> Packages =
    [
        // Igual a los valores por defecto: existe para que el certificado diga "Base"
        Base("base", "Base"),
        Base("professional", "Professional",
            (LicenseFeatures.MaxUsers, 20), (LicenseFeatures.MaxClientSessions, 5),
            (LicenseFeatures.VideoChannels, 64),
            (LicenseFeatures.ModuleAnpr, true), (LicenseFeatures.AnprChannels, 2),
            (LicenseFeatures.ModuleAlarms, true), (LicenseFeatures.AlarmPanels, 4),
            (LicenseFeatures.ModuleVideowall, true), (LicenseFeatures.Videowalls, 1), (LicenseFeatures.VideowallDecoders, 4),
            (LicenseFeatures.ModuleSpeakers, true), (LicenseFeatures.SpeakerChannels, 8),
            (LicenseFeatures.ModuleIntercom, true), (LicenseFeatures.IntercomDevices, 4),
            (LicenseFeatures.ModuleAutomation, true), (LicenseFeatures.AutomationRules, 50)),
        Base("enterprise", "Enterprise",
            (LicenseFeatures.MaxUsers, 200), (LicenseFeatures.MaxClientSessions, 50),
            (LicenseFeatures.VideoChannels, 512),
            (LicenseFeatures.ModuleAnpr, true), (LicenseFeatures.AnprChannels, 16),
            (LicenseFeatures.ModuleAlarms, true), (LicenseFeatures.AlarmPanels, 32),
            (LicenseFeatures.ModuleAccess, true), (LicenseFeatures.AccessDoors, 64),
            (LicenseFeatures.ModuleVideowall, true), (LicenseFeatures.Videowalls, 8), (LicenseFeatures.VideowallDecoders, 32),
            (LicenseFeatures.ModuleSpeakers, true), (LicenseFeatures.SpeakerChannels, 64),
            (LicenseFeatures.ModuleIntercom, true), (LicenseFeatures.IntercomDevices, 64),
            (LicenseFeatures.ModuleAutomation, true), (LicenseFeatures.AutomationRules, 500)),

        // Expansiones: lo que SUMAN a la licencia base. Una que habilita un
        // módulo trae el booleano en true para servir también sobre una base
        // que no lo tenía.
        Addon("addon_video_8", "Expansión 8 canales de video", (LicenseFeatures.VideoChannels, 8)),
        Addon("addon_video_32", "Expansión 32 canales de video", (LicenseFeatures.VideoChannels, 32)),
        Addon("addon_anpr_1", "Expansión 1 fuente ANPR",
            (LicenseFeatures.ModuleAnpr, true), (LicenseFeatures.AnprChannels, 1)),
        Addon("addon_alarm_panel_1", "Expansión 1 panel de alarma",
            (LicenseFeatures.ModuleAlarms, true), (LicenseFeatures.AlarmPanels, 1)),
        Addon("addon_access_doors_4", "Expansión 4 puertas de control de acceso",
            (LicenseFeatures.ModuleAccess, true), (LicenseFeatures.AccessDoors, 4)),
        Addon("addon_videowall_1", "Expansión 1 muro de video (incluye 4 decodificadores)",
            (LicenseFeatures.ModuleVideowall, true), (LicenseFeatures.Videowalls, 1), (LicenseFeatures.VideowallDecoders, 4)),
        Addon("addon_decoder_1", "Expansión 1 decodificador",
            (LicenseFeatures.ModuleVideowall, true), (LicenseFeatures.VideowallDecoders, 1)),
        Addon("addon_speakers_4", "Expansión 4 parlantes IP",
            (LicenseFeatures.ModuleSpeakers, true), (LicenseFeatures.SpeakerChannels, 4)),
        Addon("addon_intercom_4", "Expansión 4 frentes de citofonía",
            (LicenseFeatures.ModuleIntercom, true), (LicenseFeatures.IntercomDevices, 4)),
        Addon("addon_automation_25", "Expansión 25 automatizaciones",
            (LicenseFeatures.ModuleAutomation, true), (LicenseFeatures.AutomationRules, 25)),
        Addon("addon_clients_5", "Expansión 5 clientes simultáneos", (LicenseFeatures.MaxClientSessions, 5)),
    ];

    // Mismas restricciones que el servidor de licencias (SlugField de 50, nombres de 120...)
    private static readonly Regex CodePattern = new("^[a-z][a-z0-9_]{0,49}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Errores de consistencia del catálogo (vacío = correcto): toda constante
    /// de <see cref="LicenseFeatures"/> definida una vez, tipos y valores
    /// coherentes, packages que solo usan claves del catálogo y el catálogo de
    /// presentación de módulos apuntando a claves del tipo correcto.
    /// </summary>
    public static IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var constants = typeof(LicenseFeatures)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name != nameof(LicenseFeatures.ProductCode))
            .ToDictionary(f => (string)f.GetRawConstantValue()!, f => f.Name, StringComparer.Ordinal);

        var byKey = new Dictionary<string, LicenseFeatureDefinition>(StringComparer.Ordinal);
        var categories = new HashSet<string>(StringComparer.Ordinal);
        string? previousCategory = null;
        foreach (var feature in Features)
        {
            if (!byKey.TryAdd(feature.Key, feature))
                errors.Add($"La característica '{feature.Key}' está definida dos veces.");
            if (!CodePattern.IsMatch(feature.Key))
                errors.Add($"La clave '{feature.Key}' no es válida (minúsculas, dígitos y _; hasta 50).");
            if (!constants.ContainsKey(feature.Key))
                errors.Add($"'{feature.Key}' no tiene su constante en LicenseFeatures.");
            if (string.IsNullOrWhiteSpace(feature.Name) || feature.Name.Length > 120)
                errors.Add($"El nombre de '{feature.Key}' está vacío o pasa de 120 caracteres.");
            if (feature.Category.Length > 80 || feature.Unit.Length > 40)
                errors.Add($"La categoría o la unidad de '{feature.Key}' es demasiado larga.");
            if (!IsValidValue(feature.Type, feature.Default))
                errors.Add($"El valor por defecto de '{feature.Key}' no es {TypeLabel(feature.Type)}.");
            if (feature.Category != previousCategory)
            {
                if (!categories.Add(feature.Category))
                    errors.Add($"La categoría '{feature.Category}' no está contigua (el certificado la partiría en dos).");
                previousCategory = feature.Category;
            }
        }
        foreach (var (key, field) in constants)
            if (!byKey.ContainsKey(key))
                errors.Add($"LicenseFeatures.{field} ('{key}') no está definida en LicenseCatalog.Features.");

        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in Packages)
        {
            if (!codes.Add(package.Code))
                errors.Add($"El package '{package.Code}' está definido dos veces.");
            if (!CodePattern.IsMatch(package.Code))
                errors.Add($"El código de package '{package.Code}' no es válido.");
            if (string.IsNullOrWhiteSpace(package.Name) || package.Name.Length > 120)
                errors.Add($"El nombre del package '{package.Code}' está vacío o pasa de 120 caracteres.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, value) in package.Values)
            {
                if (!keys.Add(key))
                    errors.Add($"El package '{package.Code}' declara '{key}' dos veces.");
                if (!byKey.TryGetValue(key, out var feature))
                    errors.Add($"El package '{package.Code}' usa '{key}', que no está en el catálogo.");
                else if (!IsValidValue(feature.Type, value))
                    errors.Add($"El package '{package.Code}' da a '{key}' un valor que no es {TypeLabel(feature.Type)}.");
            }
        }

        foreach (var module in LicenseFeatures.Modules.Concat(LicenseFeatures.BaseQuotas))
        {
            if (module.ModuleKey is { } moduleKey
                && (!byKey.TryGetValue(moduleKey, out var m) || m.Type != LicenseFeatureType.Boolean))
                errors.Add($"'{module.Name}': el módulo '{moduleKey}' debe ser una característica booleana del catálogo.");
            if (module.QuotaKey is { } quotaKey
                && (!byKey.TryGetValue(quotaKey, out var q) || q.Type != LicenseFeatureType.Integer))
                errors.Add($"'{module.Name}': el cupo '{quotaKey}' debe ser una característica entera del catálogo.");
        }
        return errors;
    }

    /// <summary>True si <paramref name="value"/> sirve para una característica del tipo indicado.</summary>
    public static bool IsValidValue(LicenseFeatureType type, object value) => type switch
    {
        LicenseFeatureType.Boolean => value is bool,
        LicenseFeatureType.Integer => value is int n && n >= 0,
        _ => false,
    };

    /// <summary>
    /// JSON que importa el servidor de licencias (formato <see cref="SchemaVersion"/>).
    /// <paramref name="generatedBy"/> es informativo; <paramref name="publicKey"/> es la
    /// clave pública que compila el VMS, para que el servidor verifique que firma con
    /// ella. Ninguno de los dos cuenta para detectar cambios del catálogo.
    /// </summary>
    public static string ToJson(string? generatedBy = null, string? publicKey = null)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema_version", SchemaVersion);
            w.WriteNumber("catalog_version", Version);

            w.WriteStartObject("product");
            w.WriteString("code", LicenseFeatures.ProductCode);
            w.WriteString("name", ProductName);
            w.WriteString("description", ProductDescription);
            w.WriteString("document_notes", DocumentNotes);
            w.WriteNumber("heartbeat_interval_days", HeartbeatIntervalDays);
            w.WriteNumber("grace_period_days", GracePeriodDays);
            if (!string.IsNullOrWhiteSpace(publicKey))
                w.WriteString("public_key", publicKey);
            w.WriteEndObject();

            w.WriteStartArray("features");
            foreach (var feature in Features)
            {
                w.WriteStartObject();
                w.WriteString("key", feature.Key);
                w.WriteString("name", feature.Name);
                w.WriteString("category", feature.Category);
                w.WriteString("type", feature.Type == LicenseFeatureType.Boolean ? "BOOLEAN" : "INTEGER");
                w.WriteString("unit", feature.Unit);
                w.WritePropertyName("default");
                WriteValue(w, feature.Default);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("packages");
            foreach (var package in Packages)
            {
                w.WriteStartObject();
                w.WriteString("code", package.Code);
                w.WriteString("name", package.Name);
                w.WriteString("kind", package.Addon ? "ADDON" : "BASE");
                w.WriteStartObject("values");
                foreach (var (key, value) in package.Values)
                {
                    w.WritePropertyName(key);
                    WriteValue(w, value);
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            if (generatedBy is not null)
                w.WriteString("generated_by", generatedBy);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter w, object value)
    {
        switch (value)
        {
            case bool b: w.WriteBooleanValue(b); break;
            case int n: w.WriteNumberValue(n); break;
            default: w.WriteStringValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)); break;
        }
    }

    private static string TypeLabel(LicenseFeatureType type) =>
        type == LicenseFeatureType.Boolean ? "booleano" : "un entero no negativo";

    private static LicenseFeatureDefinition Bool(string key, string name, string category, bool @default) =>
        new(key, name, category, LicenseFeatureType.Boolean, @default, "");

    private static LicenseFeatureDefinition Int(string key, string name, string category, int @default, string unit) =>
        new(key, name, category, LicenseFeatureType.Integer, @default, unit);

    private static LicensePackageDefinition Base(string code, string name, params (string Key, object Value)[] values) =>
        new(code, name, false, ToValues(values));

    private static LicensePackageDefinition Addon(string code, string name, params (string Key, object Value)[] values) =>
        new(code, name, true, ToValues(values));

    // Lista y no diccionario: conserva el orden escrito arriba y deja que
    // Validate detecte una clave repetida en vez de pisarla en silencio.
    private static IReadOnlyList<KeyValuePair<string, object>> ToValues((string Key, object Value)[] values) =>
        values.Select(v => new KeyValuePair<string, object>(v.Key, v.Value)).ToArray();
}
