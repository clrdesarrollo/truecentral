using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Dispositivo administrado (cámara, DVR, NVR o XVR). La contraseña se guarda
/// cifrada con AES-256-GCM (ver <see cref="CredentialProtector"/>); modelo,
/// serie y firmware provienen del propio equipo al validar las credenciales.
/// </summary>
public class Device : IIsupEquipment
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
    /// <summary>
    /// ID ISUP del equipo cuando se conecta por ISUP (driver hikvision-isup): el equipo se registra contra el servidor y no hace falta
    /// alcanzarlo por IP (detrás de NAT, 4G o MPLS). Null = conexión directa por IP.
    /// </summary>
    public string? IsupDeviceId { get; set; }
    /// <summary>Clave ISUP del equipo (la «clave de cifrado» de su menú de plataforma), cifrada como la contraseña.</summary>
    public byte[]? IsupKeyCiphertext { get; set; }
    /// <summary>Los equipos de video no se deshabilitan enteros (se deshabilitan sus canales).</summary>
    bool IIsupEquipment.Enabled => true;

    // Datos obtenidos del equipo al validar credenciales.
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? FirmwareVersion { get; set; }

    /// <summary>
    /// El equipo es fuente del módulo Reconocimiento de patentes: el servidor
    /// le mantiene abierto el canal de eventos ANPR mientras esté en línea.
    /// </summary>
    public bool AnprEnabled { get; set; }
    /// <summary>
    /// Apagado por el cupo (o el módulo) de la licencia, no por decisión del
    /// administrador. Cuando vuelve a haber cupo el servidor lo enciende solo
    /// (LicenseQuotaReconciler); cualquier cambio manual de <see cref="AnprEnabled"/>
    /// limpia la marca (VmsDbContext.SaveChanges).
    /// </summary>
    public bool AnprDisabledByLicense { get; set; }

    /// <summary>
    /// Ubicación del equipo en el árbol de Recursos (null = por ubicar). La
    /// reciben sus canales nuevos; al cambiarla se mueven los canales que
    /// estaban con el equipo (los ubicados aparte se quedan donde están).
    /// </summary>
    public int? LocationId { get; set; }

    public DeviceStatus Status { get; set; } = DeviceStatus.Unknown;
    public DateTime? LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<Channel> Channels { get; set; } = [];
}
