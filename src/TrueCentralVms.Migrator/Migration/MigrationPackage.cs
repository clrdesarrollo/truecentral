using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrueCentralVms.Migrator.Migration;

/// <summary>Huella recuperada de un terminal, lista para TrueCentral.</summary>
public sealed class PackageFingerprint
{
    /// <summary>Dedo 1..10 (numeración de los equipos).</summary>
    public int Finger { get; set; }
    /// <summary>Plantilla en base64 tal como la entrega el terminal.</summary>
    public string Template { get; set; } = "";
    /// <summary>De qué terminal salió.</summary>
    public string Source { get; set; } = "";
}

/// <summary>Una puerta que la persona abría, según el terminal que HikCentral le escribió.</summary>
public sealed class PackageAccess
{
    /// <summary>Nombre del terminal en el migrador (el de HikCentral).</summary>
    public string Terminal { get; set; } = "";
    /// <summary>IP del terminal: con ella se lo encuentra en TrueCentral.</summary>
    public string Host { get; set; } = "";
    public int DoorNo { get; set; } = 1;
    /// <summary>Plantilla de horario del terminal (1 = la de fábrica, todo el día).</summary>
    public int TemplateNo { get; set; } = 1;
}

public sealed class PackageSegment
{
    /// <summary>0 = domingo … 6 = sábado.</summary>
    public int Day { get; set; }
    public int StartMinutes { get; set; }
    /// <summary>1439 = hasta el final del día.</summary>
    public int EndMinutes { get; set; }
}

/// <summary>Plantilla de horario de un terminal, para recrearla en TrueCentral.</summary>
public sealed class PackageSchedule
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public List<PackageSegment> Segments { get; set; } = [];
    /// <summary>Grupo de feriados que le asoció HikCentral (null = ninguno).</summary>
    public string? HolidayGroup { get; set; }
}

/// <summary>Puerta de un nivel de HikCentral, ubicada en su terminal.</summary>
public sealed class PackageLevelDoor
{
    /// <summary>Nombre de la puerta en HikCentral (puede no coincidir con el del terminal).</summary>
    public string Name { get; set; } = "";
    public string HcpIndexCode { get; set; } = "";
    /// <summary>IP del terminal (de acsDeviceList); null si HikCentral no la dio.</summary>
    public string? DeviceHost { get; set; }
    public string? DeviceName { get; set; }
    /// <summary>
    /// Número de la puerta EN EL EQUIPO. HikCentral numera las puertas a su
    /// manera: se deduce del orden de las puertas del equipo (1 si tiene una sola).
    /// </summary>
    public int DoorNo { get; set; } = 1;
}

/// <summary>Nivel de acceso de HikCentral, para replicarlo en TrueCentral.</summary>
public sealed class PackageLevel
{
    public string HcpId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ScheduleName { get; set; } = "";
    public string ScheduleHcpId { get; set; } = "";
    /// <summary>
    /// Tramos del horario. La OpenAPI no los entrega: salen de la plantilla que
    /// tienen en los terminales las personas del nivel. Null = no se pudo saber.
    /// </summary>
    public List<PackageSegment>? Segments { get; set; }
    /// <summary>De dónde salieron los tramos ("terminal Sala Técnica, plantilla 3"), para el registro.</summary>
    public string? SegmentsSource { get; set; }
    public List<PackageLevelDoor> Doors { get; set; } = [];
    /// <summary>Elementos que no son puertas (pisos de ascensor…): no se migran.</summary>
    public int OtherElements { get; set; }
}

/// <summary>Un terminal leído: identificación y sus plantillas de horario en uso.</summary>
public sealed class PackageTerminal
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? Firmware { get; set; }
    /// <summary>Personas que tenía grabadas (todas, crucen o no con HikCentral).</summary>
    public int Users { get; set; }
    public int Cards { get; set; }
    public List<PackageSchedule> Schedules { get; set; } = [];
}

/// <summary>Una persona del paquete: lo que dijo HikCentral más lo que aportaron los terminales.</summary>
public sealed class PackagePerson
{
    public string HcpPersonId { get; set; } = "";
    public string PersonCode { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    /// <summary>Ruta del departamento ("Padre / Hijo"), como se carga en TrueCentral.</summary>
    public string? Department { get; set; }
    /// <summary>Nombre del departamento solo (la hoja): es el que el casino usa como «empresa».</summary>
    public string? DepartmentName { get; set; }
    /// <summary>Campos personalizados de HikCentral (nombre → valor), p. ej. «Colacion».</summary>
    public Dictionary<string, string> CustomFields { get; set; } = [];
    /// <summary>
    /// Clave de teclado, leída de los terminales (HikCentral no la entrega).
    /// Va en claro en el paquete, como las huellas: el paquete es un dato
    /// sensible y se guarda cifrado.
    /// </summary>
    public string? Pin { get; set; }
    /// <summary>Puertas y horarios que tenía en los terminales (modo «uno por puerta» y para verificar).</summary>
    public List<PackageAccess> Access { get; set; } = [];
    /// <summary>Niveles de HikCentral asignados a la persona (<see cref="PackageLevel.HcpId"/>).</summary>
    public List<string> HcpLevelIds { get; set; } = [];
    /// <summary>De dónde salió la foto ("HikCentral" o el terminal).</summary>
    public string? PhotoSource { get; set; }
    /// <summary>Terminales donde estaba deshabilitada (<c>Valid.enable=false</c>: lo hace el kiosco del casino).</summary>
    public List<string> DisabledOn { get; set; } = [];
    /// <summary>Terminales donde estaba en «solo tarjeta» (visitas del casino).</summary>
    public List<string> CardOnlyOn { get; set; } = [];
    /// <summary>Terminales donde podía entrar al menú (administrador local).</summary>
    public List<string> LocalAdminOn { get; set; } = [];
    /// <summary>
    /// Terminal → tipo de usuario cuando no es «normal» («visitor», «blackList»…).
    /// TrueCentral escribe a todos como «normal», y hay sistemas que leen el tipo
    /// del terminal (el kiosco del casino distingue así a las visitas).
    /// </summary>
    public Dictionary<string, string> UserTypeOn { get; set; } = [];
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Notes { get; set; }
    public DateTime ValidFrom { get; set; }
    public DateTime ValidTo { get; set; }
    /// <summary>Archivo de la foto, relativo a la carpeta del paquete (null si no tiene).</summary>
    public string? PhotoFile { get; set; }
    public string? PhotoContentType { get; set; }
    public List<string> Cards { get; set; } = [];
    /// <summary>Cuántas huellas dice HikCentral que tiene (aunque no entregue la plantilla).</summary>
    public int HcpFingerprintCount { get; set; }
    public List<PackageFingerprint> Fingerprints { get; set; } = [];
    /// <summary>Legajo con el que la conocen los terminales (lo que HikCentral les bajó).</summary>
    public string? TerminalEmployeeNo { get; set; }
    /// <summary>Terminales donde se la encontró.</summary>
    public List<string> FoundOnTerminals { get; set; } = [];

    [JsonIgnore] public string FullName => $"{FirstName} {LastName}".Trim();
}

public sealed class MigrationPackage
{
    public const string FileName = "paquete.json";
    public const string PhotosFolder = "fotos";

    public string Source { get; set; } = "";
    public string SourceVersion { get; set; } = "";
    public DateTime ExportedAt { get; set; }
    public string ExportedBy { get; set; } = "";
    public List<PackagePerson> Persons { get; set; } = [];
    /// <summary>Terminales leídos (vacío en paquetes de versiones anteriores del migrador).</summary>
    public List<PackageTerminal> Terminals { get; set; } = [];
    /// <summary>Niveles de acceso de HikCentral (vacío si la versión de HCP no los entrega por OpenAPI).</summary>
    public List<PackageLevel> Levels { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task SaveAsync(string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, FileName);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, Json, ct);
    }

    public static async Task<MigrationPackage> LoadAsync(string folder, CancellationToken ct)
    {
        string path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"En esa carpeta no hay un {FileName} del migrador.", path);
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<MigrationPackage>(stream, Json, ct)
               ?? throw new InvalidDataException("El paquete está vacío.");
    }
}
