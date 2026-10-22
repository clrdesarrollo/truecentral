using TrueCentralVms.Core.Isup;

namespace TrueCentralVms.Core.Contracts;

/// <summary>Un equipo del VMS que usa un ID ISUP: tipo ("video", "access", "alarm", "speaker", "intercom"), su id y su nombre.</summary>
public sealed record IsupEquipmentRefDto(string Kind, int Id, string Name);

/// <summary>Un ID ISUP con su estado ante el servidor y los equipos del VMS que lo usan.</summary>
public sealed record IsupDeviceDto(
    string DeviceId,
    bool Online,
    string? Address,
    string? Protocol,
    DateTimeOffset? OnlineSince,
    bool Asleep,
    string? Firmware,
    bool AlarmLinkUp,
    IReadOnlyList<IsupEquipmentRefDto> Equipment,
    /// <summary>Dirección de este servidor que se le anunció al registrarse (alarmas, video, voz).</summary>
    string? AnnouncedAddress = null);

/// <summary>
/// Página Sistema → Conexión ISUP: si el servicio corre, qué puertos abrió, qué dirección anuncia a los equipos, el estado de cada
/// equipo dado de alta y los intentos rechazados (equipos que llaman pero no están en el VMS o traen otra clave).
/// </summary>
public sealed record IsupStatusDto(
    bool Enabled,
    bool Running,
    string? Error,
    string? PublicAddress,
    IReadOnlyList<IsupListenerState> Listeners,
    IReadOnlyList<IsupDeviceDto> Devices,
    IReadOnlyList<IsupRejection> Rejections,
    /// <summary>¿Se aceptan equipos ISUP 2.0/4.0 (sin clave)? Apagado por omisión; se enciende con confirmación del riesgo.</summary>
    bool AllowLegacy = false,
    /// <summary>
    /// true si no hay dirección configurada (a cada equipo, la IP de este servidor por la que llegó). Con o sin ella, los equipos de
    /// las redes del servidor reciben la IP de su misma red.
    /// </summary>
    bool PerDeviceAddress = false,
    /// <summary>IPv4 de este servidor (sin adaptadores virtuales): con más de una, cada equipo apunta a la de su red.</summary>
    IReadOnlyList<string>? LocalAddresses = null);

/// <summary>Encender o apagar la compatibilidad ISUP 2.0/4.0. Encender exige <paramref name="Confirm"/> = "HABILITAR".</summary>
public sealed record IsupLegacyRequestDto(bool Enabled, string? Confirm = null);
