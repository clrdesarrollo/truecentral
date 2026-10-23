namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Geocerca del módulo Flota: polígono, círculo o corredor (línea con ancho). El servidor la evalúa con cada posición nueva de los
/// vehículos a los que aplica y genera los eventos de entrada, salida y exceso de velocidad dentro (ver FleetEventService).
/// </summary>
public class Geofence
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>"polygon" | "circle" | "corridor".</summary>
    public string Kind { get; set; } = "polygon";
    /// <summary>Puntos [[longitud, latitud], …] en JSON: los vértices, el centro o la línea del corredor.</summary>
    public string PointsJson { get; set; } = "[]";
    /// <summary>Radio del círculo o ancho total del corredor, en metros.</summary>
    public double RadiusM { get; set; }
    public string Color { get; set; } = "#3b82f6";
    public bool AlertOnEnter { get; set; } = true;
    public bool AlertOnExit { get; set; } = true;
    public int? SpeedLimitKmh { get; set; }
    /// <summary>Vehículos a los que aplica, en JSON ([] = toda la flota).</summary>
    public string VehicleIdsJson { get; set; } = "[]";
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
