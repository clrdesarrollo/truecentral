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
    DateTime CreatedAt,
    /// <summary>El equipo es fuente del módulo Reconocimiento de patentes.</summary>
    bool AnprEnabled = false,
    /// <summary>Canales habilitados (los que el cliente muestra y tienen ruta de streaming).</summary>
    int EnabledChannelCount = 0,
    /// <summary>Solo en respuestas de alta/edición/revalidación: aviso no bloqueante
    /// (p. ej. canales que quedaron deshabilitados por el cupo de la licencia).</summary>
    string? Warning = null,
    /// <summary>Canales deshabilitados que SÍ tienen señal (cámaras que los operadores
    /// no ven). 0 si el equipo no está en línea. Las entradas sin cámara no cuentan.</summary>
    int DisabledWithSignalCount = 0);

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
    string? Password,
    /// <summary>Solo en el alta: números de canal que quedan habilitados. Null = todos
    /// los que el equipo reporta activos, siempre que quepan en el cupo de la licencia;
    /// si no caben, el servidor exige esta selección (a lo sumo los canales disponibles).</summary>
    IReadOnlyList<int>? EnabledChannels = null);

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
    int? DetectedRtspPort = null,
    /// <summary>Cupo de canales de video de la licencia y cuántos ya están en uso (sin
    /// contar el equipo que se edita): el asistente limita con esto la selección.</summary>
    int VideoChannelQuota = 0,
    int VideoChannelsInUse = 0,
    int AvailableVideoChannels = 0);

public sealed record ProbedChannelDto(int ChannelNumber, int RtspChannel, string Name, bool IsOnline);

/// <summary>Canal de un equipo. DisabledByLicense: quedó deshabilitado por el cupo de canales de la licencia (se habilita solo al haber cupo).</summary>
public sealed record ChannelDto(int Id, int DeviceId, int ChannelNumber, int RtspChannel, string Name, bool Enabled, bool IsOnline, bool SupportsPtz, bool UseFfmpegProxy = false, bool DisabledByLicense = false);

/// <summary>Resultado de "Habilitar canales con señal": cuántos se habilitaron y cuántos quedaron fuera por cupo.</summary>
public sealed record ChannelBulkEnableResultDto(int Enabled, int LeftWithoutQuota, string Message);

/// <summary>Orden PTZ del cliente (Speed 1..7; Stop=true detiene el movimiento en curso).</summary>
public sealed record PtzRequestDto(Drivers.PtzCommand Command, int Speed, bool Stop);

/// <summary>Operación sobre un preset PTZ (índice 1..300).</summary>
public sealed record PtzPresetRequestDto(Drivers.PtzPresetAction Action, int Index);

/// <summary>
/// Edición de un canal: nombre visible, habilitado, marca PTZ manual (para
/// domos que la detección automática no ve, ej. conectados al DVR por ONVIF)
/// y proxy FFmpeg (cámaras cuyo SDP inválido rechaza MediaMTX).
/// </summary>
public sealed record ChannelWriteDto(string Name, bool Enabled, bool SupportsPtz, bool UseFfmpegProxy = false);

public sealed record DriverDto(
    string Key,
    string DisplayName,
    bool SupportsSnapshot,
    bool SupportsDiscovery,
    int DefaultSdkPort,
    int DefaultRtspPort);
