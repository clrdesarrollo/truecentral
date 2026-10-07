using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Core.Contracts;

// Hora y mantenimiento de los equipos (página "Hora y mantenimiento" y el
// apartado del mismo nombre en la ficha de cada equipo). Los equipos se
// nombran por familia + id ("access", 12) para que video, paneles y
// citofonía se sumen sin cambiar la forma de la API.

/// <summary>El reloj de un equipo tal como se lo leyó.</summary>
/// <param name="LocalTime">La hora que muestra el equipo AHORA (lo leído más lo que pasó desde entonces), "yyyy-MM-ddTHH:mm:ss".</param>
/// <param name="UtcOffsetMinutes">Desfase de su zona en este momento, si lo informa.</param>
/// <param name="TimeZone">La zona en el formato del equipo.</param>
/// <param name="ZoneDescription">La zona en palabras.</param>
public sealed record DeviceClockDto(
    string LocalTime,
    int? UtcOffsetMinutes,
    string? TimeZone,
    string? ZoneDescription,
    bool? DaylightSaving,
    DeviceTimeMode Mode,
    string? NtpServer,
    int? NtpIntervalMinutes,
    DateTime ReadAt);

/// <summary>Un equipo en la página de Hora y mantenimiento.</summary>
/// <param name="Kind">Familia: "access" (control de acceso).</param>
/// <param name="Health">
/// "ok", "drift" (hora desfasada), "zone" (otra zona horaria), "error" (no se
/// pudo leer), "offline", "unknown" (todavía sin leer) o "unsupported".
/// </param>
/// <param name="DriftSeconds">Desfase del reloj: positivo = adelantado.</param>
/// <param name="ZoneProblem">Qué tiene de distinto la zona, en palabras.</param>
public sealed record MaintainedDeviceDto(
    string Kind,
    int Id,
    string Name,
    string? Model,
    string Host,
    string? Location,
    string Status,
    bool SupportsClock,
    bool SupportsNtp,
    bool SupportsReboot,
    IReadOnlyList<DeviceResetMode> Resets,
    bool AutoCorrect,
    string Health,
    DeviceClockDto? Clock,
    double? DriftSeconds,
    string? ZoneProblem,
    string? Error,
    DateTime? CheckedAt,
    DateTime? LastCorrectionAt,
    bool? LastCorrectionOk,
    string? LastCorrectionDetail);

public sealed record DeviceClockPolicyDto(
    /// <summary>Null = la zona del servidor.</summary>
    string? TimeZoneId,
    string TimeZoneName,
    string ZoneDescription,
    DeviceTimeMode Mode,
    string? NtpServer,
    int NtpIntervalMinutes,
    bool AutoCorrect,
    int ThresholdSeconds,
    int CheckMinutes);

public sealed record DeviceClockOverviewDto(
    DateTime ServerUtc,
    /// <summary>Hora local del servidor, "yyyy-MM-ddTHH:mm:ss".</summary>
    string ServerLocalTime,
    string ServerTimeZoneId,
    string ServerTimeZoneName,
    string ServerZoneDescription,
    DeviceClockPolicyDto Policy,
    IReadOnlyList<MaintainedDeviceDto> Devices);

public sealed record TimeZoneOptionDto(string Id, string Name, string Description);

/// <summary>Cómo dejar el reloj de un equipo.</summary>
/// <param name="TimeZoneId">Null = la de la política.</param>
/// <param name="LocalTime">Solo en modo manual: "yyyy-MM-ddTHH:mm:ss" en esa zona. Null = la hora del servidor.</param>
public sealed record DeviceClockWriteDto(
    string? TimeZoneId,
    DeviceTimeMode Mode,
    string? LocalTime,
    string? NtpServer,
    int? NtpIntervalMinutes);

public sealed record DeviceRefDto(string Kind, int Id);

/// <summary>Equipos sobre los que actuar; vacío o null = todos los que se ven.</summary>
public sealed record DeviceSelectionDto(IReadOnlyList<DeviceRefDto>? Devices);

/// <summary>Resultado por equipo de una acción en bloque.</summary>
public sealed record DeviceActionResultDto(string Kind, int Id, string Name, bool Ok, string? Message);

/// <summary>Restablecer: el modo y el nombre del equipo escrito a mano como confirmación.</summary>
public sealed record DeviceResetRequestDto(DeviceResetMode Mode, string Confirm);

public sealed record DeviceAutoCorrectDto(bool Enabled);

public sealed record DeviceClockPolicyWriteDto(
    string? TimeZoneId,
    DeviceTimeMode Mode,
    string? NtpServer,
    int NtpIntervalMinutes,
    bool AutoCorrect,
    int ThresholdSeconds,
    int CheckMinutes);
