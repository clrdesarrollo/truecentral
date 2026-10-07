namespace TrueCentralVms.Core.Drivers;

// Hora y mantenimiento de los equipos: leer y fijar su reloj (zona horaria,
// horario de verano, NTP), reiniciarlos y restablecerlos. Es común a todas las
// familias de equipos —hoy lo implementa control de acceso; video, paneles y
// citofonía lo van a reutilizar tal cual— y por eso vive aparte de cada
// interfaz de driver.

/// <summary>De dónde saca la hora el equipo.</summary>
public enum DeviceTimeMode
{
    Unknown,
    /// <summary>Hora fijada a mano (o por el VMS): corre con su propio reloj.</summary>
    Manual,
    /// <summary>Se sincroniza solo contra un servidor NTP.</summary>
    Ntp,
}

/// <summary>Qué deja en pie un restablecimiento.</summary>
public enum DeviceResetMode
{
    /// <summary>
    /// Vuelve la configuración a la de fábrica pero conserva la red y las
    /// cuentas de usuario: el equipo sigue respondiendo en la misma dirección y
    /// con la misma clave, así que el VMS no lo pierde.
    /// </summary>
    KeepNetwork,
    /// <summary>
    /// De fábrica completo: borra también la red y las cuentas. El equipo queda
    /// desactivado y fuera del VMS hasta que se lo vuelva a activar.
    /// </summary>
    Full,
}

/// <summary>Restablecimientos que sabe hacer un driver.</summary>
[Flags]
public enum DeviceResetModes
{
    None = 0,
    KeepNetwork = 1,
    Full = 2,
}

/// <summary>
/// Lo que el equipo dice de su reloj, tal como lo informa.
/// </summary>
/// <param name="LocalTime">La hora que muestra el equipo, en SU zona (sin desfase).</param>
/// <param name="UtcOffset">
/// Desfase de su zona en ese momento (horario de verano incluido), si lo
/// informa. Con él se sabe la hora absoluta del equipo; sin él (ZKTeco) solo
/// se puede comparar contra la hora local esperada.
/// </param>
/// <param name="TimeZone">La zona en el formato del equipo ("CST-8:00:00", índice Dahua…).</param>
/// <param name="ZoneOffset">Desfase BASE de su zona (sin horario de verano), si se conoce.</param>
/// <param name="DaylightSaving">Tiene horario de verano configurado (null = no lo informa).</param>
/// <param name="ZoneDescription">La zona explicada en palabras ("UTC−4 con horario de verano…").</param>
/// <param name="ReadAtUtc">
/// Cuándo se leyó, en la hora del SERVIDOR: el punto medio de la consulta, para
/// que el viaje de ida y vuelta no cuente como desfase.
/// </param>
public sealed record DeviceClock(
    DateTime LocalTime,
    TimeSpan? UtcOffset,
    string? TimeZone,
    TimeSpan? ZoneOffset,
    bool? DaylightSaving,
    string? ZoneDescription,
    DeviceTimeMode Mode,
    string? NtpServer,
    int? NtpIntervalMinutes,
    DateTime ReadAtUtc);

/// <summary>Cómo dejar el reloj de un equipo.</summary>
/// <param name="Zone">Zona horaria a dejarle, con su horario de verano.</param>
/// <param name="Mode">Hora fijada (<see cref="DeviceTimeMode.Manual"/>) o NTP.</param>
/// <param name="LocalTime">
/// Hora a fijar en modo manual, expresada en <paramref name="Zone"/>. Null = la
/// hora actual del servidor llevada a esa zona, que es lo normal: "ponerlo en
/// hora".
/// </param>
/// <param name="NtpServer">Servidor NTP (IP o nombre), solo en modo NTP.</param>
/// <param name="NtpIntervalMinutes">Cada cuánto se sincroniza, solo en modo NTP.</param>
public sealed record DeviceClockSetting(
    TimeZoneInfo Zone,
    DeviceTimeMode Mode,
    DateTime? LocalTime = null,
    string? NtpServer = null,
    int NtpIntervalMinutes = 60)
{
    /// <summary>La hora que hay que escribirle al equipo, en su zona.</summary>
    public DateTime TargetLocalTime() =>
        LocalTime ?? TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);
}
