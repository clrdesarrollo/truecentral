namespace TrueCentralVms.Core.Contracts;

// DTOs del mantenedor de dispositivos. Los enums viajan como texto en JSON
// (JsonStringEnumConverter registrado en el servidor).

public enum DeviceType { Camera, Dvr, Nvr, Xvr }

public enum DeviceStatus { Unknown, Online, Offline, AuthFailed }

/// <summary>Dispositivo tal como lo ven el panel y el cliente. NUNCA incluye la contraseña.</summary>
public sealed record DeviceDto(
    int Id,
    string Name,
    DeviceType DeviceType,
    string DriverKey,
    string Host,
    int SdkPort,
    int RtspPort,
    string Username,
    string? Model,
    string? SerialNumber,
    string? FirmwareVersion,
    int ChannelCount,
    DeviceStatus Status,
    DateTime? LastSeenAt,
    DateTime CreatedAt);

/// <summary>
/// Alta/edición de dispositivo. En edición, Password null o vacía = mantener
/// la actual. El tipo (Cámara/DVR/NVR/XVR) NO se envía: lo determina el
/// servidor automáticamente según lo que reporta el propio equipo.
/// </summary>
public sealed record DeviceWriteDto(
    string Name,
    string DriverKey,
    string Host,
    int SdkPort,
    int RtspPort,
    string Username,
    string? Password);

/// <summary>Resultado del botón "Probar conexión" del asistente (no persiste nada).</summary>
public sealed record DeviceProbeResultDto(
    bool Success,
    string? Error,
    string? Model,
    string? SerialNumber,
    string? FirmwareVersion,
    DeviceType? SuggestedType,
    int AnalogChannelCount,
    int IpChannelCount,
    IReadOnlyList<ProbedChannelDto> Channels,
    /// <summary>Puerto RTSP real detectado por SDK (null = no consultable; se usa el del formulario).</summary>
    int? DetectedRtspPort = null);

public sealed record ProbedChannelDto(int ChannelNumber, int RtspChannel, string Name, bool IsOnline);

public sealed record ChannelDto(int Id, int DeviceId, int ChannelNumber, int RtspChannel, string Name, bool Enabled, bool IsOnline, bool SupportsPtz);

/// <summary>Orden PTZ del cliente (Speed 1..7; Stop=true detiene el movimiento en curso).</summary>
public sealed record PtzRequestDto(Drivers.PtzCommand Command, int Speed, bool Stop);

/// <summary>Operación sobre un preset PTZ (índice 1..300).</summary>
public sealed record PtzPresetRequestDto(Drivers.PtzPresetAction Action, int Index);

/// <summary>
/// Edición de un canal: nombre visible, habilitado, y marca PTZ manual (para
/// domos que la detección automática no ve, ej. conectados al DVR por ONVIF).
/// </summary>
public sealed record ChannelWriteDto(string Name, bool Enabled, bool SupportsPtz);

public sealed record DriverDto(
    string Key,
    string DisplayName,
    bool SupportsSnapshot,
    bool SupportsDiscovery,
    int DefaultSdkPort,
    int DefaultRtspPort);
