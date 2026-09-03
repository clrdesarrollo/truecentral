using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Parlante IP (altavoz de red, ej. Hikvision DS-QAZ). Entidad aparte de
/// <see cref="Device"/>: no tiene canales de video ni rutas de streaming; tiene
/// una biblioteca de audios propia y un canal de audio en vivo. La contraseña
/// se guarda cifrada con AES-256-GCM (ver <see cref="CredentialProtector"/>).
/// </summary>
public class Speaker
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
    /// <summary>Grupo lógico (sector) para elegir varios parlantes de una vez; libre.</summary>
    public string? GroupName { get; set; }

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
