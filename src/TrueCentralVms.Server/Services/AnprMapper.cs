using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services;

/// <summary>Conversión de reconocimientos persistidos al DTO que ven el cliente y el panel.</summary>
public static class AnprMapper
{
    public static PlateEventDto ToDto(PlateEvent e, string deviceName, string channelName) => new(
        e.Id, e.DeviceId, deviceName, e.ChannelNumber, channelName,
        e.PlateNumber, e.CapturedAt, e.ReceivedAt,
        e.Confidence, ParseConfidences(e.CharConfidences),
        e.PlateColor, e.PlateType, e.VehicleType, e.VehicleColor, e.VehicleBrand, e.VehicleAttributes,
        e.SpeedKmh, e.VehicleLengthCm, e.Direction, e.Lane, e.DetectionMethod, e.Violation,
        e.PlateX, e.PlateY, e.PlateWidth, e.PlateHeight,
        e.SceneImagePath is not null, e.PlateImagePath is not null);

    private static IReadOnlyList<int> ParseConfidences(string? packed)
    {
        if (string.IsNullOrWhiteSpace(packed)) return [];
        var values = new List<int>();
        foreach (string part in packed.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part, out int value))
                values.Add(value);
        return values;
    }
}
