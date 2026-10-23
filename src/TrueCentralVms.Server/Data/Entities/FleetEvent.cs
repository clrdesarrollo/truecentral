using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Evento de un vehículo de la flota (ver <see cref="FleetEventKinds"/>): alarma de su grabador, geocerca o conexión. Guarda la
/// posición conocida al momento para verlo en el mapa y en el recorrido. Los informativos nacen atendidos; el resto espera que un
/// operador lo acuse. Se borran con la retención de las posiciones.
/// </summary>
public class FleetEvent
{
    public long Id { get; set; }
    public int VehicleId { get; set; }
    public Vehicle Vehicle { get; set; } = null!;
    /// <summary>Hora del evento (UTC).</summary>
    public DateTime At { get; set; }
    public string Kind { get; set; } = "";
    public AlarmSeverity Severity { get; set; }
    public string Description { get; set; } = "";
    /// <summary>Canal del grabador que lo informó (cámaras, entradas de alarma), si corresponde.</summary>
    public int? Channel { get; set; }
    public int? GeofenceId { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public float? SpeedKmh { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public int? AcknowledgedByUserId { get; set; }
    public string? AcknowledgedBy { get; set; }
    public string? AckNote { get; set; }
}
