using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Cómo debe estar el reloj de los equipos y qué hace el VMS si no lo está:
/// una sola fila (Id = 1), como el servidor de correo. Sin fila rige
/// <see cref="Default"/>: zona del servidor, hora del servidor, revisar cada
/// 15 minutos y corregir solo a partir de un minuto de desfase.
/// </summary>
public class DeviceClockPolicy
{
    public int Id { get; set; } = 1;

    /// <summary>
    /// Zona horaria que deben tener los equipos (id de Windows,
    /// "Pacific SA Standard Time"). Null = la del servidor, que es lo normal:
    /// el VMS y sus equipos viven en el mismo lugar.
    /// </summary>
    public string? TimeZoneId { get; set; }

    /// <summary>Hora fijada por el VMS (Manual) o que la tomen de un servidor NTP.</summary>
    public DeviceTimeMode Mode { get; set; } = DeviceTimeMode.Manual;
    public string? NtpServer { get; set; }
    public int NtpIntervalMinutes { get; set; } = 60;

    /// <summary>Ponerlos en hora solo cuando se desfasan o tienen otra zona.</summary>
    public bool AutoCorrect { get; set; } = true;

    /// <summary>Desfase a partir del cual un equipo se considera fuera de hora.</summary>
    public int ThresholdSeconds { get; set; } = 60;

    /// <summary>Cada cuánto se revisa el reloj de los equipos.</summary>
    public int CheckMinutes { get; set; } = 15;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static DeviceClockPolicy Default() => new();

    /// <summary>La zona que deben tener los equipos: la configurada o, si no hay o ya no existe, la del servidor.</summary>
    public TimeZoneInfo Zone()
    {
        if (string.IsNullOrWhiteSpace(TimeZoneId)) return TimeZoneInfo.Local;
        try { return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Local; }
    }
}
