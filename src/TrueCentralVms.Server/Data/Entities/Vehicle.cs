namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Vehículo de la flota (módulo Flota): su grabador móvil (MDVR) es un <see cref="Device"/> normal conectado por ISUP, marcado
/// <see cref="Device.IsMobile"/>, de modo que el vivo, la reproducción y el alcance por canal funcionan igual que en un equipo fijo.
/// La separación con los equipos de video la hacen la interfaz (árbol propio) y la licencia: cada vehículo habilitado consume un
/// cupo <c>fleet_vehicles</c> y sus canales no consumen <c>video_channels</c>. La ubicación (alcance) es la de su grabador.
/// </summary>
public class Vehicle
{
    public int Id { get; set; }
    /// <summary>Código interno con que la flota identifica al vehículo (p. ej. «UUBB-0387»); único.</summary>
    public string Code { get; set; } = "";
    /// <summary>Patente (opcional).</summary>
    public string? Plate { get; set; }
    /// <summary>Tipo libre (camioneta, bus, furgón…).</summary>
    public string? Kind { get; set; }
    public string? Notes { get; set; }

    /// <summary>Grabador móvil del vehículo (uno por vehículo); al borrar el vehículo se borra su grabador.</summary>
    public int DeviceId { get; set; }
    public Device Device { get; set; } = null!;

    /// <summary>Un vehículo deshabilitado no consume cupo y sus canales quedan deshabilitados.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Apagado por el cupo (o el módulo) de la licencia; ver <see cref="AlarmPanel.DisabledByLicense"/>.</summary>
    public bool DisabledByLicense { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
