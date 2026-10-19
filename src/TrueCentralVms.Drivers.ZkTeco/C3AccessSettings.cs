using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.ZkTeco;

/// <summary>
/// Configuración propia de las controladoras inBio/C3 y los terminales MA:
/// los parámetros de cada puerta y los de la controladora (antipassback,
/// interbloqueo, horario de verano…), los mismos que ZKAccess muestra en
/// "Configuración de puerta" y en las ventanas de antipassback e interbloqueo.
/// Se leen y escriben con los comandos de parámetros (04 y 03).
///
/// Las listas de antipassback e interbloqueo son las de ZKAccess 3.5
/// (Languages\emnetman_*.xml, nodos AntiType e InterlockType): dependen de
/// cuántas puertas tiene el equipo.
/// </summary>
public sealed class C3AccessSettingsProvider : IAccessDeviceSettingsProvider
{
    public string DriverKey => "zkteco-c3";

    private const int MaxDoors = 4;

    private static readonly AccessSettingOption[] SensorTypes =
    [
        new("0", "Sin sensor"),
        new("1", "Normalmente abierto"),
        new("2", "Normalmente cerrado"),
    ];

    /// <summary>Modos de verificación del PullSDK (PullSDKVerifyType de ZKAccess).</summary>
    private static readonly AccessSettingOption[] VerifyTypes =
    [
        new("0", "Cualquiera"),
        new("1", "Solo huella"),
        new("2", "Número de usuario"),
        new("3", "Solo clave"),
        new("4", "Solo tarjeta"),
        new("5", "Huella o clave"),
        new("6", "Tarjeta o huella"),
        new("7", "Tarjeta o clave"),
        new("8", "Huella y número de usuario"),
        new("9", "Huella y clave"),
        new("10", "Tarjeta y huella"),
        new("11", "Tarjeta y clave"),
        new("12", "Tarjeta, huella y clave"),
        new("13", "Huella, número de usuario y clave"),
        new("14", "Huella y tarjeta o número de usuario"),
        new("15", "Solo rostro"),
        new("16", "Rostro y huella"),
        new("17", "Rostro y clave"),
        new("18", "Rostro y tarjeta"),
        new("19", "Rostro, huella y tarjeta"),
        new("20", "Rostro, huella y clave"),
    ];

    private static AccessSettingOption[] AntiPassbackOptions(int doors) => doors switch
    {
        1 =>
        [
            new("0", "Sin antipassback"),
            new("1", "Entre los lectores de la puerta 1 (entrada/salida)"),
        ],
        2 =>
        [
            new("0", "Sin antipassback"),
            new("1", "Entre los lectores de la puerta 1"),
            new("2", "Entre los lectores de la puerta 2"),
            new("3", "Entre los lectores de la puerta 1 y entre los de la puerta 2"),
            new("4", "Entre la puerta 1 y la puerta 2"),
        ],
        _ =>
        [
            new("0", "Sin antipassback"),
            new("1", "Puerta 1 ↔ puerta 2"),
            new("2", "Puerta 3 ↔ puerta 4"),
            new("3", "Puerta 1 ↔ 2 y puerta 3 ↔ 4"),
            new("4", "Puertas 1/2 ↔ puertas 3/4"),
            new("5", "Puerta 1 ↔ puertas 2/3"),
            new("6", "Puerta 1 ↔ puertas 2/3/4"),
            new("16", "Entre los lectores de la puerta 1"),
            new("32", "Entre los lectores de la puerta 2"),
            new("64", "Entre los lectores de la puerta 3"),
            new("128", "Entre los lectores de la puerta 4"),
            new("48", "Lectores de la puerta 1 y de la puerta 2"),
            new("80", "Lectores de la puerta 1 y de la puerta 3"),
            new("144", "Lectores de la puerta 1 y de la puerta 4"),
            new("96", "Lectores de la puerta 2 y de la puerta 3"),
            new("160", "Lectores de la puerta 2 y de la puerta 4"),
            new("192", "Lectores de la puerta 3 y de la puerta 4"),
            new("112", "Lectores de las puertas 1, 2 y 3"),
            new("176", "Lectores de las puertas 1, 2 y 4"),
            new("208", "Lectores de las puertas 1, 3 y 4"),
            new("224", "Lectores de las puertas 2, 3 y 4"),
            new("240", "Lectores de las cuatro puertas"),
        ],
    };

    private static AccessSettingOption[]? InterlockOptions(int doors) => doors switch
    {
        < 2 => null,   // con una puerta no hay con qué trabarse
        2 or 3 =>
        [
            new("0", "Sin interbloqueo"),
            new("1", "Entre las puertas 1 y 2"),
        ],
        _ =>
        [
            new("0", "Sin interbloqueo"),
            new("1", "Entre las puertas 1 y 2"),
            new("2", "Entre las puertas 3 y 4"),
            new("3", "Entre las puertas 1, 2 y 3"),
            new("4", "Entre 1 y 2, y entre 3 y 4"),
            new("5", "Entre las cuatro puertas"),
        ],
    };

    private static string[] DoorParams(int n) =>
    [
        $"Door{n}Drivertime", $"Door{n}Detectortime", $"Door{n}SensorType", $"Door{n}VerifyType",
        $"Door{n}Intertime", $"Door{n}CloseAndLock", $"Door{n}FirstCardOpenDoor", $"Door{n}MultiCardOpenDoor",
        $"Door{n}ValidTZ", $"Door{n}KeepOpenTimeZone", $"Door{n}ForcePassWord", $"Door{n}SupperPassWord",
    ];

    private static readonly string[] DeviceParams =
        ["AntiPassback", "InterLock", "DaylightSavingTimeOn", "WatchDog", "Door4ToDoor2", "InBIOTowWay"];

    private static Task<C3Connection> OpenAsync(AccessConnectionInfo info, CancellationToken ct)
    {
        string password = (info.Password ?? "").Trim();
        return C3Connection.OpenAsync(info.Host, info.Port, password is "" or "0" ? null : password, ct);
    }

    public async Task<AccessDeviceSettingsDto> ReadAsync(AccessConnectionInfo info, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(info, ct);
        return new AccessDeviceSettingsDto(await ReadSectionsAsync(connection, ct), []);
    }

    private static async Task<List<AccessSettingsSectionDto>> ReadSectionsAsync(C3Connection connection, CancellationToken ct)
    {
        var counts = await connection.GetParametersAsync(["LockCount"], ct);
        int doors = int.TryParse(counts.GetValueOrDefault("LockCount"), out int n) && n > 0 ? Math.Min(n, MaxDoors) : 1;

        var sections = new List<AccessSettingsSectionDto>();
        for (int door = 1; door <= doors; door++)
        {
            // Se piden por puerta: el comando admite hasta unos 30 nombres.
            var p = await connection.GetParametersAsync(DoorParams(door), ct);
            sections.Add(DoorSection(door, p));
        }
        var d = await connection.GetParametersAsync(DeviceParams, ct);
        sections.Add(DeviceSection(doors, d));
        return sections;
    }

    private static AccessSettingsSectionDto DoorSection(int n, Dictionary<string, string> p)
    {
        var settings = new List<AccessSettingDto>();
        void Int(string key, string label, int min, int max, string? unit, string help)
        {
            if (p.TryGetValue(key, out var v)) settings.Add(new(key, label, AccessSettingType.Integer, v, min, max, unit, Help: help));
        }
        void Bool(string key, string label, string help)
        {
            if (p.TryGetValue(key, out var v)) settings.Add(new(key, label, AccessSettingType.Boolean, v.Trim() == "1" ? "true" : "false", Help: help));
        }
        void Choice(string key, string label, AccessSettingOption[] options, string help)
        {
            if (!p.TryGetValue(key, out var v)) return;
            // Un valor que la lista no conoce se muestra igual, sin perderlo al guardar otra cosa.
            var list = options.Any(o => o.Value == v.Trim()) ? options : [.. options, new(v.Trim(), $"Otro ({v.Trim()})")];
            settings.Add(new(key, label, AccessSettingType.Choice, v.Trim(), Options: list, Help: help));
        }

        Int($"Door{n}Drivertime", "Tiempo de apertura de la cerradura", 0, 255, "s",
            "Cuánto queda energizada la cerradura en una pasada válida. 0 = siempre cerrada, 255 = siempre abierta.");
        Int($"Door{n}Detectortime", "Alarma de puerta abierta", 0, 255, "s",
            "Si la hoja queda abierta más que esto, la controladora da la alarma (requiere sensor). 0 = sin alarma.");
        Choice($"Door{n}SensorType", "Sensor de puerta", SensorTypes,
            "Tipo de contacto magnético conectado. Sin sensor no hay estado de la hoja ni alarma de puerta forzada.");
        Choice($"Door{n}VerifyType", "Modo de verificación", VerifyTypes,
            "Con qué tiene que identificarse la persona en los lectores de esta puerta.");
        Int($"Door{n}Intertime", "Intervalo mínimo entre pasadas", 0, 255, "s",
            "Una misma credencial no abre dos veces seguidas antes de este tiempo. 0 = sin límite.");
        Bool($"Door{n}CloseAndLock", "Trabar al cerrar",
            "Corta la cerradura en cuanto el sensor detecta la hoja cerrada, sin esperar el tiempo de apertura.");
        Bool($"Door{n}FirstCardOpenDoor", "Primera tarjeta deja abierta",
            "La primera persona habilitada del día deja la puerta abierta en su horario (se define quién en el equipo).");
        Bool($"Door{n}MultiCardOpenDoor", "Apertura multitarjeta",
            "La puerta abre solo cuando pasan varias personas de los grupos configurados en el equipo.");
        Int($"Door{n}ValidTZ", "Horario de la puerta", 0, 9999, null,
            "Número de horario del equipo en que la puerta acepta pasadas (1 = el de 24 h de fábrica).");
        Int($"Door{n}KeepOpenTimeZone", "Horario de puerta abierta", 0, 9999, null,
            "Número de horario del equipo en que la puerta queda libre. 0 = nunca.");
        if (p.ContainsKey($"Door{n}ForcePassWord"))
            settings.Add(new($"Door{n}ForcePassWord", "Clave de coacción", AccessSettingType.Password, null,
                Help: "Abre la puerta y avisa en silencio que la persona está bajo amenaza. Vacío = no cambiar."));
        if (p.ContainsKey($"Door{n}SupperPassWord"))
            settings.Add(new($"Door{n}SupperPassWord", "Clave de emergencia", AccessSettingType.Password, null,
                Help: "Abre la puerta sin credencial. Vacío = no cambiar."));

        return new AccessSettingsSectionDto($"door:{n}", "door", n, $"Puerta {n}", null, settings, []);
    }

    private static AccessSettingsSectionDto DeviceSection(int doors, Dictionary<string, string> p)
    {
        var settings = new List<AccessSettingDto>();
        if (p.TryGetValue("AntiPassback", out var apb))
        {
            var options = AntiPassbackOptions(doors);
            if (options.All(o => o.Value != apb.Trim())) options = [.. options, new(apb.Trim(), $"Otro ({apb.Trim()})")];
            settings.Add(new("AntiPassback", "Antipassback", AccessSettingType.Choice, apb.Trim(), Options: options,
                Help: "Impide volver a entrar sin haber salido (o al revés). Lo aplica la propia controladora entre sus puertas o lectores."));
        }
        if (p.TryGetValue("InterLock", out var il) && InterlockOptions(doors) is { } interlock)
        {
            if (interlock.All(o => o.Value != il.Trim())) interlock = [.. interlock, new(il.Trim(), $"Otro ({il.Trim()})")];
            settings.Add(new("InterLock", "Interbloqueo", AccessSettingType.Choice, il.Trim(), Options: interlock,
                Help: "Mientras una puerta del grupo está abierta, las otras no abren (esclusas). Requiere sensor de puerta."));
        }
        if (p.TryGetValue("DaylightSavingTimeOn", out var dst))
            settings.Add(new("DaylightSavingTimeOn", "Horario de verano automático", AccessSettingType.Boolean,
                dst.Trim() == "1" ? "true" : "false",
                Help: "Que la controladora cambie sola la hora. Si se apaga, la supervisión de hora del VMS la corrige."));
        if (p.TryGetValue("Door4ToDoor2", out var d42) && doors >= 4)
            settings.Add(new("Door4ToDoor2", "Usar como 2 puertas de doble lector", AccessSettingType.Boolean,
                d42.Trim() == "1" ? "true" : "false",
                Help: "Convierte la controladora de 4 puertas en 2 puertas con lector de entrada y de salida. Requiere revalidar el equipo."));
        return new AccessSettingsSectionDto("device", "device", 0, "Controladora", null, settings, []);
    }

    public async Task<AccessSettingsSectionDto> ApplyAsync(AccessConnectionInfo info, string sectionKey,
        IReadOnlyDictionary<string, string?> values, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(info, ct);
        var current = await ReadSectionsAsync(connection, ct);
        var section = current.FirstOrDefault(s => s.Key == sectionKey)
            ?? throw new DriverException($"La controladora no tiene el bloque '{sectionKey}'.");

        var assignments = new List<string>();
        foreach (var (key, raw) in values)
        {
            var setting = section.Settings.FirstOrDefault(s => s.Key == key)
                ?? throw new DriverException($"El parámetro '{key}' no es de este bloque.");
            string value = (raw ?? "").Trim();
            switch (setting.Type)
            {
                case AccessSettingType.Password when value.Length == 0:
                    continue;   // vacío = no cambiar
                case AccessSettingType.Password:
                    if (value.Length > 8 || !value.All(char.IsDigit))
                        throw new DriverException($"{setting.Label}: hasta 8 dígitos.");
                    break;
                case AccessSettingType.Boolean:
                    value = value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "1" : "0";
                    break;
                case AccessSettingType.Integer:
                    if (!int.TryParse(value, out int number) || number < (setting.Min ?? int.MinValue) || number > (setting.Max ?? int.MaxValue))
                        throw new DriverException($"{setting.Label}: debe ser un número entre {setting.Min} y {setting.Max}.");
                    value = number.ToString();
                    break;
                case AccessSettingType.Choice:
                    if (setting.Options?.Any(o => o.Value == value) != true)
                        throw new DriverException($"{setting.Label}: opción no válida.");
                    break;
                default:
                    continue;
            }
            // Las comas separan parámetros en la orden: un valor no puede llevarlas.
            if (value.Contains(',')) throw new DriverException($"{setting.Label}: valor no válido.");
            assignments.Add($"{key}={value}");
        }

        if (assignments.Count > 0) await connection.SetParametersAsync(assignments, ct);
        var fresh = await ReadSectionsAsync(connection, ct);
        return fresh.First(s => s.Key == sectionKey);
    }
}
