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
    /// <summary>Grupo lógico (sector) para elegir varios parlantes de una vez; libre.</summary>
    public string? GroupName { get; set; }

    /// <summary>Ubicación en el árbol de Recursos; null = por ubicar.</summary>
    public int? LocationId { get; set; }

    // Datos obtenidos del parlante al validar credenciales.
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? FirmwareVersion { get; set; }
    public bool SupportsLibrary { get; set; }
    public bool SupportsTts { get; set; }
    public bool SupportsLiveAudio { get; set; }

    /// <summary>El servidor lo sondea y lo ofrece a operadores y automatizaciones.</summary>
    public bool Enabled { get; set; } = true;

    public SpeakerStatus Status { get; set; } = SpeakerStatus.Unknown;
    public string? LastError { get; set; }
    public DateTime? LastSeenAt { get; set; }
    /// <summary>Último volumen de salida leído del equipo (0..100).</summary>
    public int? Volume { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
