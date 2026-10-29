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
    int DisabledWithSignalCount = 0,
    /// <summary>Ubicación del equipo en el árbol de Recursos (null = por ubicar): la heredan sus canales.</summary>
    int? LocationId = null,
    /// <summary>Ruta legible de esa ubicación ("Casa matriz › Bodega").</summary>
    string? Location = null,
    /// <summary>ID ISUP del equipo si se conecta por ISUP (entonces Host y puertos no aplican).</summary>
    string? IsupDeviceId = null,
    /// <summary>Hay clave ISUP guardada (la clave nunca viaja).</summary>
    bool HasIsupKey = false,
    /// <summary>Es la cámara de un terminal de acceso: se administra desde Control de acceso
    /// (dirección y credenciales son las del terminal) y no ocupa canal de video de la licencia.</summary>
    int? AccessDeviceId = null,
    /// <summary>Puerto HTTP (ISAPI): por ahí se leen y operan su altavoz y sus entradas y salidas.</summary>
    int HttpPort = 80,
    /// <summary>Tiene altavoz (audio bidireccional); se ofrece como parlante, incluido.</summary>
    bool HasSpeaker = false,
    bool UseSpeaker = true,
    /// <summary>Entradas y salidas de alarma (0 si no tiene o no se le preguntó); se ofrecen como módulo de E/S, incluido.</summary>
    int IoInputs = 0,
    int IoOutputs = 0,
    bool UseIo = true,
    /// <summary>Se le preguntó qué partes tiene (las ve el VMS en esta marca).</summary>
    bool PartsChecked = false,
    /// <summary>Por qué alguna de sus partes no está en uso aunque la tenga, o null.</summary>
    string? PartsNote = null);

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
    IReadOnlyList<int>? EnabledChannels = null,
    /// <summary>Ubicación en el árbol de Recursos: null = conservar la actual (en el alta,
    /// por ubicar); 0 = por ubicar. Los canales que seguían al equipo se mueven con él.</summary>
    int? LocationId = null,
    /// <summary>Solo con un driver ISUP: ID con que el equipo se registra en el servidor.</summary>
    string? IsupDeviceId = null,
    /// <summary>Solo con un driver ISUP: su clave; vacía en edición = conservar la guardada.</summary>
    string? IsupKey = null,
    /// <summary>Puerto HTTP (ISAPI) para su altavoz y sus entradas y salidas: null = conservar (en el alta, 80).</summary>
    int? HttpPort = null,
    /// <summary>Ofrecer su altavoz como parlante: null = conservar (en el alta, sí).</summary>
    bool? UseSpeaker = null,
    /// <summary>Ofrecer sus entradas y salidas de alarma como módulo de E/S: null = conservar (en el alta, sí).</summary>
    bool? UseIo = null);

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

/// <summary>Canal de un equipo. DisabledByLicense: quedó deshabilitado por el cupo de canales de la licencia (se habilita solo al haber cupo).
/// LocationId: ubicación del canal en el árbol de Recursos (null = por ubicar).</summary>
public sealed record ChannelDto(int Id, int DeviceId, int ChannelNumber, int RtspChannel, string Name, bool Enabled, bool IsOnline, bool SupportsPtz, bool UseFfmpegProxy = false, bool DisabledByLicense = false, int? LocationId = null);

/// <summary>Resultado de "Habilitar canales con señal": cuántos se habilitaron y cuántos quedaron fuera por cupo.</summary>
public sealed record ChannelBulkEnableResultDto(int Enabled, int LeftWithoutQuota, string Message);

/// <summary>Orden PTZ del cliente (Speed 1..7; Stop=true detiene el movimiento en curso).</summary>
public sealed record PtzRequestDto(Drivers.PtzCommand Command, int Speed, bool Stop);

/// <summary>
/// Movimiento PTZ por velocidades (joystick): Pan, Tilt y Zoom en −1..1, a la
/// vez; los tres en 0 detienen. Mientras se mueve, el cliente lo repite al
/// menos cada segundo: sin noticias por 3 s el servidor detiene la cámara.
/// </summary>
public sealed record PtzMoveRequestDto(double Pan, double Tilt, double Zoom);

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
    int DefaultRtspPort,
    /// <summary>El equipo se conecta por ISUP: el asistente pide ID y clave en vez de dirección y credenciales.</summary>
    bool UsesIsup = false);
