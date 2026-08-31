namespace TrueCentralVms.Core.Contracts;

// DTOs del módulo Reconocimiento de patentes (Aplicaciones → ANPR/LPR).
// Las imágenes NO viajan en el JSON: el cliente las pide aparte por
// /api/anpr/events/{id}/scene.jpg y /plate.jpg (así la lista se refresca
// liviana y las fotos se cargan solo al mirarlas).

/// <summary>
/// Un reconocimiento tal como lo ven el cliente y el panel.
/// <paramref name="CapturedAt"/> es la hora local del equipo que lo capturó;
/// <paramref name="ReceivedAt"/> es la hora UTC en que el servidor lo recibió.
/// </summary>
public sealed record PlateEventDto(
    long Id,
    int DeviceId,
    string DeviceName,
    int ChannelNumber,
    string ChannelName,
    string PlateNumber,
    DateTime CapturedAt,
    DateTime ReceivedAt,
    int Confidence,
    IReadOnlyList<int> CharConfidences,
    string? PlateColor,
    string? PlateType,
    string? VehicleType,
    string? VehicleColor,
    string? VehicleBrand,
    string? VehicleAttributes,
    int? SpeedKmh,
    int? VehicleLengthCm,
    string? Direction,
    int? Lane,
    string? DetectionMethod,
    string? Violation,
    double PlateX, double PlateY, double PlateWidth, double PlateHeight,
    bool HasSceneImage,
    bool HasPlateImage);

/// <summary>
/// Equipo capaz de entregar patentes, con su estado como fuente del módulo.
/// <paramref name="Live"/> indica que el servidor tiene ahora mismo el canal
/// de eventos abierto contra el equipo.
/// </summary>
public sealed record AnprSourceDto(
    int DeviceId,
    string DeviceName,
    string DriverKey,
    string Host,
    DeviceStatus Status,
    bool Enabled,
    bool Live,
    string? LastError,
    DateTime? LastEventAt,
    int EventCount);

/// <summary>Encender o apagar un equipo como fuente de patentes.</summary>
public sealed record AnprSourceWriteDto(bool Enabled);
