using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Parlante IP (altavoz de red, ej. Hikvision DS-QAZ). Entidad aparte de
/// <see cref="Device"/>: no tiene canales de video ni rutas de streaming; tiene
/// una biblioteca de audios propia y un canal de audio en vivo. La contraseña
/// se guarda cifrada con AES-256-GCM (ver <see cref="CredentialProtector"/>).
/// </summary>
public class Speaker : ILocatable, IIsupEquipment
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Clave del driver ("hikvision-isapi").</summary>
    public string DriverKey { get; set; } = "hikvision-isapi";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 80;
    public bool UseHttps { get; set; }
    public string Username { get; set; } = "";
    public byte[] PasswordCiphertext { get; set; } = [];
    /// <summary>
    /// ID ISUP del equipo cuando se conecta por ISUP (driver hikvision-isup): el equipo se registra contra el servidor y no hace falta
    /// alcanzarlo por IP (detrás de NAT, 4G o MPLS). Null = conexión directa por IP.
    /// </summary>
    public string? IsupDeviceId { get; set; }
    /// <summary>Clave ISUP del equipo (la «clave de cifrado» de su menú de plataforma), cifrada como la contraseña.</summary>
    public byte[]? IsupKeyCiphertext { get; set; }
    /// <summary>
    /// Agente «TrueCentral Speaker» (driver truecentral-agent): id del equipo (32 hex) que se enroló con el código. Null = aún
    /// sin enrolar. El agente abre él la conexión al servidor; <see cref="Host"/> solo guarda, como dato, desde dónde se conectó.
    /// </summary>
    public string? AgentId { get; set; }
    /// <summary>SHA-256 del token que se le entregó al agente al enrolarse (el token nunca se guarda).</summary>
    public string? AgentTokenHash { get; set; }
    /// <summary>SHA-256 del código de enrolamiento vigente (de un solo uso).</summary>
    public string? EnrollmentCodeHash { get; set; }
    public DateTime? EnrollmentExpiresAt { get; set; }
    /// <summary>Grupos de parlantes a los que pertenece (puede estar en varios).</summary>
    public List<SpeakerGroupMember> GroupMemberships { get; set; } = [];

    /// <summary>Ubicación en el árbol de Recursos; null = por ubicar.</summary>
    public int? LocationId { get; set; }

    /// <summary>
    /// Altavoz de un terminal de acceso (facial): el VMS lo crea y lo mantiene a
    /// partir del terminal (dirección, credenciales, nombre y ubicación son los
    /// suyos) y se borra con él. No se edita en Parlantes IP y no ocupa parlante
    /// de la licencia.
    /// </summary>
    public int? AccessDeviceId { get; set; }
    public AccessDevice? AccessDevice { get; set; }

    // Datos obtenidos del parlante al validar credenciales.
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? FirmwareVersion { get; set; }
    public bool SupportsLibrary { get; set; }
    public bool SupportsTts { get; set; }
    public bool SupportsLiveAudio { get; set; }

    /// <summary>El servidor lo sondea y lo ofrece a operadores y automatizaciones.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Apagado por el cupo (o el módulo) de la licencia, no por decisión del
    /// administrador. Cuando vuelve a haber cupo el servidor lo enciende solo
    /// (LicenseQuotaReconciler); cualquier cambio manual de <see cref="Enabled"/>
    /// limpia la marca (VmsDbContext.SaveChanges).
    /// </summary>
    public bool DisabledByLicense { get; set; }

    public SpeakerStatus Status { get; set; } = SpeakerStatus.Unknown;
    public string? LastError { get; set; }
    public DateTime? LastSeenAt { get; set; }
    /// <summary>Último volumen de salida leído del equipo (0..100).</summary>
    public int? Volume { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Grupo de parlantes ("Perímetro", "Bodega", "Todos"): para hablar o hacer
/// sonar varios de una vez desde el Centro de mando, un tablero o una
/// automatización. Un parlante puede estar en varios grupos.
/// </summary>
public class SpeakerGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<SpeakerGroupMember> Members { get; set; } = [];
}

/// <summary>Parlante de un grupo.</summary>
public class SpeakerGroupMember
{
    public int SpeakerGroupId { get; set; }
    public SpeakerGroup? Group { get; set; }
    public int SpeakerId { get; set; }
    public Speaker? Speaker { get; set; }
}
