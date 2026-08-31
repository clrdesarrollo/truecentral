namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Un reconocimiento de patente recibido de un equipo ANPR. Guarda TODO lo que
/// informó la cámara; las imágenes NO van en la base (una escena pesa cientos
/// de kB y a este ritmo la inflaría): viven en disco y aquí queda su ruta
/// relativa al directorio de imágenes del módulo (ver <c>AnprStore</c>).
/// </summary>
public class PlateEvent
{
    public long Id { get; set; }

    public int DeviceId { get; set; }
    public Device Device { get; set; } = null!;
    /// <summary>Canal del equipo (número interno del SDK) que hizo la lectura.</summary>
    public int ChannelNumber { get; set; }

    public string PlateNumber { get; set; } = "";

    /// <summary>Hora local del equipo (las cámaras ITS fechan con su propio reloj).</summary>
    public DateTime CapturedAt { get; set; }
    /// <summary>Hora UTC en que el servidor recibió el evento (orden de la lista en vivo).</summary>
    public DateTime ReceivedAt { get; set; }

    /// <summary>Confianza global 0..100 (−1 = el equipo no la informa).</summary>
    public int Confidence { get; set; }
    /// <summary>Confianza por carácter, separada por comas ("85,90,78,…"); vacía = no informada.</summary>
    public string? CharConfidences { get; set; }

    public string? PlateColor { get; set; }
    public string? PlateType { get; set; }
    public string? VehicleType { get; set; }
    public string? VehicleColor { get; set; }
    public string? VehicleBrand { get; set; }
    /// <summary>Observaciones del interior del vehículo, ya redactadas por el driver.</summary>
    public string? VehicleAttributes { get; set; }
    public int? SpeedKmh { get; set; }
    public int? VehicleLengthCm { get; set; }
    public string? Direction { get; set; }
    public int? Lane { get; set; }
    public string? DetectionMethod { get; set; }
    public string? Violation { get; set; }

    /// <summary>Recuadro de la placa sobre la imagen de escena, normalizado 0..1.</summary>
    public double PlateX { get; set; }
    public double PlateY { get; set; }
    public double PlateWidth { get; set; }
    public double PlateHeight { get; set; }

    /// <summary>Rutas relativas de las fotos (null = el equipo no la envió).</summary>
    public string? SceneImagePath { get; set; }
    public string? PlateImagePath { get; set; }
}
