namespace TrueCentralVms.Core.Contracts;

// DTOs del módulo Paneles de alarma (centrales de intrusión: áreas/particiones,
// zonas, armado/desarmado y eventos). Los enums viajan como texto en JSON.

/// <summary>Estado de armado de un área (partición) del panel.</summary>
public enum AlarmArmState
{
    Unknown,
    Disarmed,
    /// <summary>Armado total (fuera de casa).</summary>
    Away,
    /// <summary>Armado parcial (en casa / perimetral).</summary>
    Stay,
    /// <summary>Armado vacaciones (algunos fabricantes).</summary>
    Vacation,
    /// <summary>Transitorio: retardo de salida en curso ("conteo de activación") antes de quedar armado.</summary>
    Arming,
}

/// <summary>Estado físico de una zona.</summary>
public enum AlarmZoneStatus
{
    Unknown,
    /// <summary>En reposo, comunicando normalmente.</summary>
    Normal,
    /// <summary>Detector activado (abierta / con movimiento).</summary>
    Triggered,
    /// <summary>Falla del detector (batería, supervisión, tamper).</summary>
    Fault,
    /// <summary>Sin comunicación con el panel.</summary>
    Offline,
    /// <summary>La zona no está asociada a ningún área.</summary>
    NotConfigured,
}

/// <summary>Modo de armado solicitado por el operador.</summary>
public enum AlarmArmMode { Away, Stay }

/// <summary>Naturaleza de un evento del panel.</summary>
public enum AlarmEventKind
{
    /// <summary>Alarma (intrusión, pánico, incendio, tamper...).</summary>
    Alarm,
    /// <summary>Restauración de una alarma o falla anterior.</summary>
    Restore,
    Arm,
    Disarm,
    Bypass,
    /// <summary>Falla o problema (batería, corriente, comunicación).</summary>
    Trouble,
    /// <summary>Evento del sistema (reinicio, prueba, mantención).</summary>
    System,
    Info,
    /// <summary>
    /// Detector interrumpido (zona activada) sin que haya alarma: puerta
    /// abierta, movimiento con el área desarmada. Lo detecta el sondeo de
    /// estado, no el canal de eventos del panel (las centrales no reportan
    /// por Contact-ID lo que pasa con el área desarmada).
    /// </summary>
    ZoneTriggered,
}

public enum AlarmSeverity { Info, Warning, Critical }

/// <summary>Estado de conexión del servidor con el panel.</summary>
public enum AlarmPanelStatus { Unknown, Online, Offline, AuthFailed }

public sealed record AlarmAreaDto(
    int Number,
    string Name,
    bool Enabled,
    AlarmArmState ArmState,
    /// <summary>El área tiene una alarma activa (sin restaurar).</summary>
    bool InAlarm,
    int ZoneCount,
    /// <summary>Retardo de salida (s) mientras el área está en "Armando…"; 0 si no aplica.</summary>
    int ExitDelaySeconds = 0);

public sealed record AlarmZoneDto(
    int Number,
    int? AreaNumber,
    string Name,
    string? ZoneType,
    string? DetectorType,
    AlarmZoneStatus Status,
    bool Bypassed,
    bool Armed,
    bool InAlarm,
    bool Tamper,
    bool LowBattery,
    /// <summary>Nivel de señal inalámbrica 0..N que informa el panel (null = cableada / no informado).</summary>
    int? Signal,
    string? Model);

/// <summary>Panel de alarma tal como lo ven el cliente y el panel web. NUNCA incluye la contraseña.</summary>
public sealed record AlarmPanelDto(
    int Id,
    string Name,
    string DriverKey,
    string Host,
    int Port,
    bool UseHttps,
    string Username,
    /// <summary>Identificador del panel dentro de la pasarela (drivers que no hablan directo con el equipo).</summary>
    string? DeviceId,
    string? Model,
    string? SerialNumber,
    string? FirmwareVersion,
    /// <summary>El servidor mantiene la conexión (sondeo + canal de eventos) con este panel.</summary>
    bool Enabled,
    AlarmPanelStatus Status,
    /// <summary>El canal de eventos del panel está abierto ahora mismo.</summary>
    bool Live,
    /// <summary>Sabotaje del chasis (tapa abierta).</summary>
    bool PanelTamper,
    /// <summary>Falla de corriente de red (el panel quedó en batería).</summary>
    bool AcLoss,
    string? LastError,
    DateTime? LastSeenAt,
    /// <summary>Última vez que el servidor leyó el estado completo (UTC).</summary>
    DateTime? LastStateAt,
    DateTime CreatedAt,
    IReadOnlyList<AlarmAreaDto> Areas,
    IReadOnlyList<AlarmZoneDto> Zones)
{
    /// <summary>Alguna área o zona del panel está en alarma ahora.</summary>
    public bool InAlarm => Areas.Any(a => a.InAlarm) || Zones.Any(z => z.InAlarm);
}

/// <summary>Alta/edición. En edición, Password null o vacía = mantener la actual.</summary>
public sealed record AlarmPanelWriteDto(
    string Name,
    string DriverKey,
    string Host,
    int Port,
    bool UseHttps,
    string Username,
    string? Password,
    bool Enabled = true,
    /// <summary>Identificador del panel dentro de la pasarela (uuid, serie, cuenta o ID ISUP); solo drivers con NeedsDeviceId.</summary>
    string? DeviceId = null);

/// <summary>Resultado del botón "Probar conexión" (no persiste nada).</summary>
public sealed record AlarmPanelProbeResultDto(
    bool Success,
    string? Error,
    string? Model,
    string? SerialNumber,
    string? FirmwareVersion,
    IReadOnlyList<AlarmAreaDto> Areas,
    IReadOnlyList<AlarmZoneDto> Zones);

public sealed record AlarmDriverDto(string Key, string DisplayName, int DefaultPort, bool DefaultHttps,
    /// <summary>El driver habla con una pasarela: el mantenedor debe pedir además el identificador del panel en ella.</summary>
    bool NeedsDeviceId = false);

/// <summary>Orden de armado de un área.</summary>
public sealed record AlarmArmRequestDto(AlarmArmMode Mode);

/// <summary>Anular (bypass) o restituir una zona.</summary>
public sealed record AlarmZoneBypassRequestDto(bool Bypassed);

/// <summary>
/// Evento del panel (alarma, armado, falla...). <paramref name="Timestamp"/>
/// es la hora del evento según el panel convertida a UTC;
/// <paramref name="ReceivedAt"/> la hora UTC en que el servidor lo recibió.
/// </summary>
public sealed record AlarmEventDto(
    long Id,
    int PanelId,
    string PanelName,
    DateTime Timestamp,
    DateTime ReceivedAt,
    AlarmEventKind Kind,
    AlarmSeverity Severity,
    /// <summary>Código Contact-ID (ej. 1130) u otro código del fabricante; null si no aplica.</summary>
    string? Code,
    string Description,
    int? AreaNumber,
    string? AreaName,
    int? ZoneNumber,
    string? ZoneName,
    /// <summary>Usuario/llavero que operó el panel, si el evento lo informa.</summary>
    string? Operator,
    /// <summary>"panel" = lo empujó el equipo; "poll" = lo detectó el sondeo de estado; "vms" = orden dada desde el VMS.</summary>
    string Source);
