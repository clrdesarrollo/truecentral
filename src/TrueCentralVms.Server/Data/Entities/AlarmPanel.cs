using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Panel de alarma (central de intrusión, ej. Hikvision AX PRO). Es una
/// entidad aparte de <see cref="Device"/>: no tiene canales de video ni rutas
/// de streaming; tiene áreas y zonas y un canal de eventos. La contraseña se
/// guarda cifrada con AES-256-GCM (ver <see cref="CredentialProtector"/>).
/// </summary>
public class AlarmPanel : IIsupEquipment
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Clave del driver ("hikvision-isapi").</summary>
    public string DriverKey { get; set; } = "";
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

    // Datos obtenidos del panel al validar credenciales.
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? FirmwareVersion { get; set; }

    /// <summary>El servidor mantiene el sondeo y el canal de eventos abiertos.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Apagado por el cupo (o el módulo) de la licencia, no por decisión del
    /// administrador. Cuando vuelve a haber cupo el servidor lo enciende solo
    /// (LicenseQuotaReconciler); cualquier cambio manual de <see cref="Enabled"/>
    /// limpia la marca (VmsDbContext.SaveChanges).
    /// </summary>
    public bool DisabledByLicense { get; set; }

    /// <summary>Sabotaje del chasis del panel (tapa abierta), informado por el equipo.</summary>
    public bool PanelTamper { get; set; }
    /// <summary>Falla de corriente de red (el panel está en batería).</summary>
    public bool AcLoss { get; set; }

    /// <summary>
    /// Ubicación del panel en el árbol de Recursos (null = por ubicar). La
    /// reciben sus áreas y zonas nuevas; al cambiarla se mueven las que estaban
    /// con el panel (las ubicadas aparte se quedan donde están).
    /// </summary>
    public int? LocationId { get; set; }

    public AlarmPanelStatus Status { get; set; } = AlarmPanelStatus.Unknown;
    public string? LastError { get; set; }
    public DateTime? LastSeenAt { get; set; }
    /// <summary>Última lectura completa de áreas/zonas (UTC).</summary>
    public DateTime? LastStateAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<AlarmArea> Areas { get; set; } = [];
    public List<AlarmZone> Zones { get; set; } = [];

    /// <summary>Dónde entra una zona nueva: con su área si el área está ubicada, si no con el panel.</summary>
    public int? LocationForNewZone(int? areaNumber) =>
        Areas.FirstOrDefault(a => a.Number == areaNumber)?.LocationId ?? LocationId;
}

/// <summary>Área (partición / subsistema) de un panel, con su estado de armado.</summary>
public class AlarmArea : IOrderedResource
{
    public int Id { get; set; }
    public int AlarmPanelId { get; set; }
    public AlarmPanel AlarmPanel { get; set; } = null!;
    /// <summary>Número del área tal como lo usa la API del panel.</summary>
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public AlarmArmState ArmState { get; set; } = AlarmArmState.Unknown;
    public bool InAlarm { get; set; }
    /// <summary>Retardo de salida en curso (s) durante el armado; transitorio, no se persiste.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public int ExitDelaySeconds { get; set; }
    /// <summary>Ubicación en el árbol de Recursos; null = por ubicar.</summary>
    public int? LocationId { get; set; }
    /// <summary>Posición dentro de su ubicación (Recursos); null = sin orden propio.</summary>
    public int? LocationOrder { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Zona (detector) de un panel, con su último estado conocido.</summary>
public class AlarmZone : IOrderedResource
{
    public int Id { get; set; }
    public int AlarmPanelId { get; set; }
    public AlarmPanel AlarmPanel { get; set; } = null!;
    /// <summary>Identificador de la zona tal como lo usa la API del panel (en Hikvision parte en 0).</summary>
    public int Number { get; set; }
    public int? AreaNumber { get; set; }
    public string Name { get; set; } = "";
    public string? ZoneType { get; set; }
    public string? DetectorType { get; set; }
    public AlarmZoneStatus Status { get; set; } = AlarmZoneStatus.Unknown;
    public bool Bypassed { get; set; }
    public bool Armed { get; set; }
    public bool InAlarm { get; set; }
    public bool Tamper { get; set; }
    public bool LowBattery { get; set; }
    public int? Signal { get; set; }
    public string? Model { get; set; }
    /// <summary>Ubicación en el árbol de Recursos; null = por ubicar.</summary>
    public int? LocationId { get; set; }
    /// <summary>Posición dentro de su ubicación (Recursos); null = sin orden propio.</summary>
    public int? LocationOrder { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Evento de un panel (alarma, armado, falla...). Sin FK al panel para que el
/// historial sobreviva a su eliminación (el nombre queda copiado).
/// </summary>
public class AlarmEvent
{
    public long Id { get; set; }
    public int AlarmPanelId { get; set; }
    public string PanelName { get; set; } = "";
    /// <summary>Hora del evento según el panel, en UTC.</summary>
    public DateTime Timestamp { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public AlarmEventKind Kind { get; set; }
    public AlarmSeverity Severity { get; set; }
    /// <summary>Código Contact-ID u otro del fabricante.</summary>
    public string? Code { get; set; }
    public string Description { get; set; } = "";
    public int? AreaNumber { get; set; }
    public string? AreaName { get; set; }
    public int? ZoneNumber { get; set; }
    public string? ZoneName { get; set; }
    public string? Operator { get; set; }
    /// <summary>"panel" | "poll" | "vms".</summary>
    public string Source { get; set; } = "panel";
    /// <summary>Notificación cruda del panel (diagnóstico).</summary>
    public string? RawJson { get; set; }
}
