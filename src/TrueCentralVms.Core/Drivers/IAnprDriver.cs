namespace TrueCentralVms.Core.Drivers;

/// <summary>
/// Un reconocimiento de patente entregado por el equipo (ANPR/LPR). Trae TODO
/// lo que la cámara informa del evento: la patente y su confianza, los datos
/// del vehículo, el carril y la dirección, y las imágenes de la escena y del
/// primer plano de la placa.
///
/// <see cref="CapturedAt"/> viene en HORA LOCAL DEL EQUIPO (las cámaras ITS
/// fechan con su propio reloj, igual que las grabaciones); el servidor agrega
/// su propia marca de recepción al persistirlo.
///
/// El rectángulo de la placa (<see cref="PlateX"/>…) viene normalizado 0..1
/// sobre la imagen de escena, para dibujarlo encima sin conocer su tamaño.
/// </summary>
public sealed record PlateRecognition(
    int ChannelNumber,
    string PlateNumber,
    DateTime CapturedAt,
    /// <summary>Confianza global de la lectura, 0..100 (−1 = el equipo no la informa).</summary>
    int Confidence,
    /// <summary>Confianza por carácter, en el orden de la patente (vacío = no informada).</summary>
    IReadOnlyList<int> CharConfidences,
    string? PlateColor,
    string? PlateType,
    string? VehicleType,
    string? VehicleColor,
    string? VehicleBrand,
    /// <summary>Lo que la cámara observó dentro del vehículo (cinturón, teléfono,
    /// carga peligrosa…), ya redactado; null = el equipo no informa nada.</summary>
    string? VehicleAttributes,
    /// <summary>Velocidad en km/h informada por el equipo (null = sin radar/lazo).</summary>
    int? SpeedKmh,
    /// <summary>Largo del vehículo en centímetros (null = no informado).</summary>
    int? VehicleLengthCm,
    string? Direction,
    /// <summary>Carril de circulación (null = no informado).</summary>
    int? Lane,
    /// <summary>Cómo se disparó la captura (lazo magnético, video, radar…).</summary>
    string? DetectionMethod,
    /// <summary>Infracción informada por el equipo (null = ninguna).</summary>
    string? Violation,
    double PlateX, double PlateY, double PlateWidth, double PlateHeight,
    byte[]? SceneImage,
    byte[]? PlateImage);

/// <summary>
/// Suscripción viva a los reconocimientos de un equipo. Liberarla cierra el
/// canal de alarma y la sesión que la sostiene.
/// </summary>
public interface IPlateSubscription : IAsyncDisposable
{
    /// <summary>false cuando la suscripción se cayó y hay que rehacerla.</summary>
    bool IsAlive { get; }
}
