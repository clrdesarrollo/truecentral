using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Dispositivo administrado (cámara, DVR, NVR o XVR). La contraseña se guarda
/// cifrada con AES-256-GCM (ver <see cref="CredentialProtector"/>); modelo,
/// serie y firmware provienen del propio equipo al validar las credenciales.
/// </summary>
public class Device
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DeviceType DeviceType { get; set; }
    /// <summary>Clave del driver (hikvision-netsdk / dahua-netsdk / onvif).</summary>
    public string DriverKey { get; set; } = "";
    public string Host { get; set; } = "";
    /// <summary>Puerto de gestión (SDK Hikvision 8000, Dahua 37777, ONVIF 80).</summary>
    public int SdkPort { get; set; }
    public int RtspPort { get; set; } = 554;
    public string Username { get; set; } = "";
    public byte[] PasswordCiphertext { get; set; } = [];

    // Datos obtenidos del equipo al validar credenciales.
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? FirmwareVersion { get; set; }

    /// <summary>
    /// El equipo es fuente del módulo Reconocimiento de patentes: el servidor
    /// le mantiene abierto el canal de eventos ANPR mientras esté en línea.
    /// </summary>
    public bool AnprEnabled { get; set; }

    public DeviceStatus Status { get; set; } = DeviceStatus.Unknown;
    public DateTime? LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<Channel> Channels { get; set; } = [];
}
