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

/// <summary>
/// Tipos de evento de un vehículo. Los del grabador vienen de sus alarmas ISUP (EN_ALARM_TYPE); los de geocerca los calcula el
/// servidor con cada posición; los de conexión, del registro ISUP del grabador (un vehículo apagado se desconecta: es informativo).
/// </summary>
public static class FleetEventKinds
{
    public const string Panic = "panic";
    public const string Overspeed = "overspeed";
    public const string Collision = "collision";
    public const string Rollover = "rollover";
    public const string HarshAcceleration = "harsh-acceleration";
    public const string HarshBraking = "harsh-braking";
    public const string HarshTurn = "harsh-turn";
    public const string AbnormalDriving = "abnormal-driving";
    public const string LaneDeparture = "lane-departure";
    public const string DoorOpenMoving = "door-open-moving";
    public const string Overload = "overload";
    public const string Adas = "adas";
    public const string Radar = "radar";
    public const string HighTemperature = "high-temperature";
    public const string AlarmInput = "alarm-input";
    public const string VideoLoss = "video-loss";
    public const string VideoTamper = "video-tamper";
    public const string StorageFault = "storage-fault";
    public const string GeofenceEnter = "geofence-enter";
    public const string GeofenceExit = "geofence-exit";
    public const string GeofenceOverspeed = "geofence-overspeed";
    public const string Connected = "connected";
    public const string Disconnected = "disconnected";

    /// <summary>Nombre para la interfaz y las plantillas de las automatizaciones.</summary>
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [Panic] = "Botón de pánico",
        [Overspeed] = "Exceso de velocidad",
        [Collision] = "Choque",
        [Rollover] = "Volcamiento",
        [HarshAcceleration] = "Aceleración brusca",
        [HarshBraking] = "Frenada brusca",
        [HarshTurn] = "Giro brusco",
        [AbnormalDriving] = "Conducción anómala",
        [LaneDeparture] = "Salida de pista",
        [DoorOpenMoving] = "Puerta abierta en marcha",
        [Overload] = "Sobrecarga",
        [Adas] = "Asistencia a la conducción (ADAS)",
        [Radar] = "Alarma del radar",
        [HighTemperature] = "Temperatura alta del grabador",
        [AlarmInput] = "Entrada de alarma",
        [VideoLoss] = "Pérdida de video",
        [VideoTamper] = "Cámara tapada",
        [StorageFault] = "Falla de disco o grabación",
        [GeofenceEnter] = "Entrada a geocerca",
        [GeofenceExit] = "Salida de geocerca",
        [GeofenceOverspeed] = "Exceso de velocidad en geocerca",
        [Connected] = "Vehículo conectado",
        [Disconnected] = "Vehículo desconectado",
    };

    public static string LabelOf(string kind) => Labels.TryGetValue(kind, out var l) ? l : kind;

    /// <summary>Gravedad por omisión: crítica (pánico, choque, volcamiento), informativa (conexión) o advertencia.</summary>
    public static AlarmSeverity SeverityOf(string kind) => kind switch
    {
        Panic or Collision or Rollover => AlarmSeverity.Critical,
        Connected or Disconnected => AlarmSeverity.Info,
        _ => AlarmSeverity.Warning,
    };
}

/// <summary>
/// Evento de un vehículo (hora UTC). Los informativos nacen atendidos; el resto queda pendiente hasta que un operador lo acusa.
/// Posición: la última conocida del vehículo al momento del evento (puede no haber).
/// </summary>
public sealed record FleetEventDto(long Id, int VehicleId, string VehicleCode, int DeviceId, DateTime At, string Kind, string Label,
    AlarmSeverity Severity, string Description, int? Channel, int? GeofenceId, double? Latitude, double? Longitude, double? SpeedKmh,
    DateTime? AcknowledgedAt, string? AcknowledgedBy, string? AckNote);

public sealed record FleetEventPageDto(IReadOnlyList<FleetEventDto> Events, int Total, int Pending);

public sealed record FleetEventAckDto(string? Note);

/// <summary>
/// Geocerca. <paramref name="Kind"/>: "polygon" (vértices), "circle" (un punto y <paramref name="RadiusM"/>) o "corridor" (una
/// línea y su ancho total <paramref name="RadiusM"/>). Puntos como [longitud, latitud]. <paramref name="VehicleIds"/> vacío = toda la
/// flota. <paramref name="SpeedLimitKmh"/>: velocidad máxima dentro (null = sin límite).
/// </summary>
public sealed record GeofenceDto(int Id, string Name, string Kind, IReadOnlyList<double[]> Points, double RadiusM, string Color,
    bool AlertOnEnter, bool AlertOnExit, int? SpeedLimitKmh, IReadOnlyList<int> VehicleIds, bool Enabled);

public sealed record GeofenceWriteDto(string Name, string Kind, IReadOnlyList<double[]> Points, double RadiusM, string? Color,
    bool AlertOnEnter, bool AlertOnExit, int? SpeedLimitKmh, IReadOnlyList<int>? VehicleIds, bool Enabled = true);
