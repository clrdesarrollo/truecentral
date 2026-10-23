namespace TrueCentralVms.Core.Contracts;

/// <summary>Cámara de un vehículo (canal de su grabador móvil). <see cref="IsOnline"/> exige además el grabador en línea.</summary>
public sealed record FleetChannelDto(int Id, int ChannelNumber, int RtspChannel, string Name, bool Enabled, bool IsOnline);

/// <summary>Última posición conocida de un vehículo (guardada: sobrevive a un reinicio del servidor).</summary>
public sealed record FleetPositionDto(double Latitude, double Longitude, double SpeedKmh, double HeadingDeg, int Satellites,
    DateTime DeviceTime, DateTimeOffset ReceivedAt);

/// <summary>Posición en vivo de un vehículo para el mapa (hub <c>FleetPositions</c> y <c>GET /api/fleet/positions</c>).
/// <paramref name="At"/> es la hora de la posición en UTC.</summary>
public sealed record FleetLivePositionDto(int VehicleId, int DeviceId, double Latitude, double Longitude, double SpeedKmh,
    double HeadingDeg, int Satellites, DateTime At, bool Online);

public sealed record FleetPositionBatchDto(IReadOnlyList<FleetLivePositionDto> Positions);

/// <summary>Punto de un recorrido (hora UTC).</summary>
public sealed record FleetTrackPointDto(DateTime At, double Latitude, double Longitude, double SpeedKmh, double HeadingDeg);

/// <summary>
/// Mapa base del módulo Flota (Sistema → Mapa). <paramref name="Mode"/>: "none" (sin mapa base: solo los vehículos sobre un fondo
/// liso), "raster" (plantilla de teselas de un proveedor, con su clave si la pide) o "pmtiles" (archivo del servidor, sin
/// internet). La posición inicial se usa si no hay vehículos con posición.
/// </summary>
public sealed record FleetMapSettingsDto(string Mode, string? RasterUrl, string? Attribution, bool PmtilesAvailable,
    double CenterLatitude, double CenterLongitude, int Zoom, int PositionRetentionDays);

public sealed record FleetMapSettingsWriteDto(string Mode, string? RasterUrl, string? Attribution, double CenterLatitude,
    double CenterLongitude, int Zoom, int PositionRetentionDays);

/// <summary>
/// Vehículo de la flota con su grabador móvil. <see cref="DeviceId"/> es el equipo de video (para las concesiones de vivo, que
/// van por equipo + canal RTSP como las de cualquier cámara).
/// </summary>
public sealed record VehicleDto(
    int Id,
    string Code,
    string? Plate,
    string? Kind,
    string? Notes,
    bool Enabled,
    bool DisabledByLicense,
    int DeviceId,
    string? IsupDeviceId,
    bool HasIsupKey,
    DeviceStatus Status,
    DateTime? LastSeenAt,
    string? Model,
    string? FirmwareVersion,
    int? LocationId,
    string? Location,
    IReadOnlyList<FleetChannelDto> Channels,
    FleetPositionDto? Position,
    DateTime CreatedAt,
    string? Warning = null);

/// <summary>Alta o edición de un vehículo. La clave ISUP vacía conserva la guardada (los equipos 2.0/4.0 no usan clave).</summary>
public sealed record VehicleWriteDto(
    string Code,
    string? Plate,
    string? Kind,
    string? Notes,
    string IsupDeviceId,
    string? IsupKey,
    int? LocationId,
    bool Enabled = true);

/// <summary>Equipo que intenta registrarse por ISUP sin estar dado de alta (candidato a vehículo).</summary>
public sealed record FleetDetectedDto(string IsupDeviceId, string Reason, DateTimeOffset FirstAt, DateTimeOffset LastAt, int Count);

/// <summary>
/// Importación de vehículos desde CSV (separador ; o ,): código;patente;tipo;id_isup[;clave_isup]. La primera fila puede ser de
/// encabezados. <paramref name="DryRun"/> solo valida.
/// </summary>
public sealed record VehicleImportRequestDto(string Csv, bool DryRun = false, int? LocationId = null);

/// <summary>Resultado de una fila de la importación (número de línea del archivo).</summary>
public sealed record VehicleImportRowDto(int Line, string Code, string? IsupDeviceId, bool Ok, string? Error);

public sealed record VehicleImportResultDto(int Created, int Failed, bool DryRun, IReadOnlyList<VehicleImportRowDto> Rows);
