using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Core.Isup;

// =============================================================================
// ISUP propio: los equipos Hikvision que están detrás de NAT, 4G o MPLS no se
// alcanzan por IP; son ellos los que se registran contra el servidor por ISUP
// (protocolo de plataforma de Hikvision) y por esa conexión se les habla ISAPI,
// se les pide el video y se reciben sus alarmas.
//
// El SDK ISUP corre en un proceso interno aparte (TrueCentralVms.IsupHost.exe)
// que el servidor lanza y vigila. Lo que viaja entre ambos (por 127.0.0.1) está
// definido aquí; los drivers solo ven IIsupGateway.
// =============================================================================

/// <summary>
/// Configuración del host ISUP. Los puertos son los que abren hacia los equipos
/// (públicos); <see cref="PublicAddress"/> es la dirección con que los equipos
/// llegan a este servidor y se les anuncia al registrarse (alarmas, video, voz).
/// </summary>
public sealed class IsupHostOptions
{
    /// <summary>
    /// IP (o nombre) con que los equipos llegan al servidor, la misma para todos. Vacío = a cada equipo se le anuncia la IP de este
    /// servidor por la que llegó (un servidor con dos redes tiene una IP en cada una).
    /// </summary>
    public string PublicAddress { get; set; } = "";
    /// <summary>
    /// Puertos de registro (TCP y UDP). El 7660 es el de fábrica de ISUP; el
    /// 7661 es donde se registraban los paneles en la receptora de Hikvision que
    /// el VMS usaba antes: escucharlo deja que se reconecten sin tocarlos.
    /// </summary>
    public int[] RegistrationPorts { get; set; } = [7660, 7661];
    /// <summary>Alarmas de los equipos ISUP 5.0 (MQTT).</summary>
    public int AlarmPort { get; set; } = 7662;
    /// <summary>
    /// ¿Se aceptan equipos ISUP 2.0/4.0? Esas versiones no prueban la clave ISUP al registrarse: basta conocer el ID
    /// para hacerse pasar por el equipo (recibir sus órdenes e inyectar alarmas). Apagado, solo entra ISUP 5.0 y no se
    /// escucha el puerto de alarmas 2.0/4.0. Encendido, igual nunca se reemplaza a un equipo 5.0 que está en línea.
    /// </summary>
    public bool AllowLegacy { get; set; }
    /// <summary>Alarmas de los equipos ISUP 2.0/4.0 (TCP y UDP). 0 = no se escucha.</summary>
    public int LegacyAlarmPort { get; set; } = 7663;
    /// <summary>Video en vivo (el equipo se conecta aquí a entregar el flujo que se le pidió).</summary>
    public int StreamPort { get; set; } = 7664;
    /// <summary>Reproducción de grabaciones.</summary>
    public int PlaybackPort { get; set; } = 7665;
    /// <summary>Voz bidireccional (parlantes y citofonía).</summary>
    public int VoicePort { get; set; } = 7666;
    /// <summary>Servidor de imágenes al que los equipos suben las fotos de sus eventos. 0 = apagado.</summary>
    public int PictureServerPort { get; set; } = 7667;
    /// <summary>Espera de respuesta de cada orden al equipo (los equipos por 4G tardan).</summary>
    public int CommandTimeoutMs { get; set; } = 15000;
}

/// <summary>Un equipo que puede registrarse: su ID ISUP y la clave que debe presentar.</summary>
public sealed record IsupDirectoryEntry(string DeviceId, string Key);

/// <summary>
/// Estado de un equipo ante el host. <paramref name="Address"/> es la IP con
/// que se registró (detrás de NAT es la privada que informa el propio equipo);
/// <paramref name="Protocol"/> "5.0" o "2.0/4.0".
/// </summary>
public sealed record IsupDeviceState(
    string DeviceId,
    bool Online,
    string? Address,
    string? Protocol,
    DateTimeOffset? OnlineSince,
    bool Asleep,
    string? Firmware,
    /// <summary>true si el equipo mantiene abierta su conexión de alarmas con el servidor.</summary>
    bool AlarmLinkUp,
    /// <summary>Dirección del servidor que se le anunció al registrarse (para alarmas, video y voz); null si aún no se le anunció.</summary>
    string? AnnouncedAddress = null);

/// <summary>
/// Un equipo que intentó registrarse y fue rechazado: ID desconocido (no está
/// en el directorio) o clave distinta. Sirve para darlo de alta desde la lista.
/// </summary>
public sealed record IsupRejection(string DeviceId, string Reason, DateTimeOffset FirstAt, DateTimeOffset LastAt, int Count);

/// <summary>Un puerto que el host debía abrir y si lo logró.</summary>
public sealed record IsupListenerState(string Name, int Port, bool Open, string? Error);

/// <summary>
/// Foto completa del host ISUP (página Sistema → Conexión ISUP). <paramref name="PublicAddress"/> es la dirección configurada o, si
/// cada equipo recibe la suya (<paramref name="PerDeviceAddress"/>), la de salida a internet, que se usa con los que aún no se
/// registraron. <paramref name="LocalAddresses"/> son las IPv4 de este servidor (sin adaptadores virtuales): con más de una, un equipo
/// tiene que apuntar a la de su red.
/// </summary>
public sealed record IsupHostStatus(
    string Version,
    string PublicAddress,
    IReadOnlyList<IsupListenerState> Listeners,
    IReadOnlyList<IsupDeviceState> Devices,
    IReadOnlyList<IsupRejection> Rejections,
    bool PerDeviceAddress = false,
    IReadOnlyList<string>? LocalAddresses = null);

/// <summary>Clase de evento que el host envía al servidor por /events (una línea JSON cada uno).</summary>
public static class IsupEventKinds
{
    public const string Online = "online";
    public const string Offline = "offline";
    public const string Sleep = "sleep";
    public const string Rejected = "rejected";
    public const string Alarm = "alarm";
    /// <summary>Latido del canal de eventos (cada 15 s), para saber que el host sigue vivo.</summary>
    public const string Ping = "ping";
}

/// <summary>Evento del host. Según <see cref="Kind"/> trae <see cref="Alarm"/> o solo el equipo.</summary>
public sealed record IsupHostEvent(
    string Kind,
    string DeviceId,
    DateTimeOffset At,
    string? Detail = null,
    IsupAlarm? Alarm = null);

/// <summary>
/// Alarma o evento que un equipo envió por ISUP, normalizado por el host.
/// <paramref name="Text"/> es la notificación ISAPI tal cual (XML
/// EventNotificationAlert o su JSON: los mismos documentos que entrega el
/// alertStream de una conexión directa), o null si llegó como estructura
/// binaria (alarma básica de ISUP 2.0/4.0, CID de un panel): entonces viene
/// decodificada en <paramref name="Basic"/> o <paramref name="Cid"/>.
/// <paramref name="Pictures"/> son las fotos que la acompañan, ya guardadas
/// en disco por el host (rutas locales).
/// </summary>
public sealed record IsupAlarm(
    string DeviceId,
    int Type,
    DateTimeOffset ReceivedAt,
    string? Text = null,
    IReadOnlyList<string>? Pictures = null,
    IsupBasicAlarm? Basic = null,
    IsupCidAlarm? Cid = null);

/// <summary>
/// Alarma básica de ISUP (NET_EHOME_ALARM_INFO): <paramref name="AlarmType"/>
/// según EN_ALARM_TYPE (5 pérdida de video, 6 entrada de alarma, 8 movimiento,
/// 101 cruce de línea…); <paramref name="Action"/> 0 = empieza, 1 = termina.
/// </summary>
public sealed record IsupBasicAlarm(int AlarmType, int Action, int Channel, int AlarmInput, int Disk, string? Remark, string? DeviceTime);

/// <summary>
/// Reporte Contact-ID de un panel de alarma (NET_EHOME_CID_INFO): código de 4
/// dígitos (calificador + evento, p. ej. 1103 = robo nuevo), partición, zona y
/// usuario que operó. Números de zona/usuario negativos = no informados.
/// </summary>
public sealed record IsupCidAlarm(
    int Code,
    int CidType,
    int SubSystem,
    string? Description,
    string? TriggerTime,
    int UserType,
    int UserNo,
    int ZoneNo,
    string? UserName);

/// <summary>
/// Datos del equipo por la configuración del SDK (NET_EHOME_DEVICE_INFO): lo que se sabe de un equipo ISUP 2.0/4.0, que no tiene
/// ISAPI. <paramref name="DeviceType"/> según el SDK (1 DVR, 3 DVS, 30 cámara IP, 40 domo IP…); <paramref name="AudioEncoding"/>
/// 0 G.722, 1 G.711µ, 2 G.711A…
/// </summary>
public sealed record IsupDeviceInfo(int AnalogChannels, int TotalChannels, int StartChannel, int MaxDigitalChannels, int DeviceType,
    int Disks, string? Serial, string? Firmware, int AudioEncoding);

/// <summary>Respuesta de una petición ISAPI por el túnel ISUP.</summary>
public sealed record IsupIsapiResponse(bool Ok, byte[] Body, int ErrorCode, string? Error);

/// <summary>Órdenes PTZ de ISUP (EN_PTZ_CMD), nombradas por el efecto.</summary>
public enum IsupPtzCommand : byte
{
    Up = 0, Down = 1, Left = 2, Right = 3, UpLeft = 4, DownLeft = 5, UpRight = 6, DownRight = 7,
    ZoomOut = 8, ZoomIn = 9, FocusNear = 10, FocusFar = 11, IrisOpen = 12, IrisClose = 13,
}

/// <summary>Acciones sobre un preset (NET_EHOME_PRESET_PARAM.byPresetCmd).</summary>
public enum IsupPresetAction : byte { Set = 1, Clear = 2, Goto = 3 }

/// <summary>
/// Voz bidireccional abierta con un equipo por ISUP. <see cref="Codec"/> es la
/// codificación que exige el equipo ("G711U", "G711A", "G722"…): lo que se
/// envía debe venir así, en tramas de <see cref="FrameBytes"/>.
/// </summary>
public interface IIsupVoiceSession : IAsyncDisposable
{
    string Codec { get; }
    int FrameBytes { get; }
    /// <summary>false cuando el equipo cortó o la sesión se cerró.</summary>
    bool IsAlive { get; }
    Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default);
}

/// <summary>
/// Lo que los drivers pueden pedirle al transporte ISUP. Lo implementa el
/// servidor (cliente del host ISUP) y lo publica en <see cref="IsupGateway.Current"/>.
/// </summary>
public interface IIsupGateway
{
    /// <summary>true si el equipo está registrado ahora.</summary>
    bool IsOnline(string deviceId);

    /// <summary>Estado del registro del equipo, o null si nunca se registró desde que arrancó el host.</summary>
    IsupDeviceState? GetState(string deviceId);

    /// <summary>
    /// Petición ISAPI por el túnel (verbo, ruta con su query y cuerpo opcional).
    /// El túnel no lleva cabeceras: nada de multipart. No lanza por errores del
    /// equipo: los informa en la respuesta.
    /// </summary>
    Task<IsupIsapiResponse> IsapiAsync(string deviceId, string method, string pathAndQuery, byte[]? body,
        TimeSpan timeout, CancellationToken ct = default);

    /// <summary>Datos y canales por la configuración del SDK (sirve a los equipos ISUP 2.0/4.0). Lanza DriverException si no responde.</summary>
    Task<IsupDeviceInfo> GetDeviceInfoAsync(string deviceId, CancellationToken ct = default);

    /// <summary>Movimiento PTZ (start=true lo inicia, false lo detiene; velocidad 0..7).</summary>
    Task PtzAsync(string deviceId, int channel, IsupPtzCommand command, bool start, int speed, CancellationToken ct = default);

    /// <summary>Ir a un preset, guardarlo o borrarlo (índices desde 1).</summary>
    Task PresetAsync(string deviceId, int channel, IsupPresetAction action, int index, CancellationToken ct = default);

    /// <summary>Activa o desactiva una salida de alarma (relé, desde 1); seconds = 0: hasta desactivarla.</summary>
    Task SetAlarmOutputAsync(string deviceId, int output, bool on, int seconds, CancellationToken ct = default);

    /// <summary>
    /// Recibe las alarmas que envíe el equipo mientras la suscripción viva.
    /// El manejador corre en un hilo del cliente: encolar y volver.
    /// </summary>
    IDisposable SubscribeAlarms(string deviceId, Action<IsupAlarm> onAlarm);

    /// <summary>
    /// URL local (127.0.0.1, con su token) que entrega el video en vivo del
    /// canal como MPEG-PS, para que ffmpeg lo publique en MediaMTX.
    /// <paramref name="streamType"/>: 0 principal, 1 secundario.
    /// </summary>
    string LiveSourceUrl(string deviceId, int channel, int streamType);

    /// <summary>Como <see cref="LiveSourceUrl"/>, para una reproducción entre dos horas locales del equipo (dosificada a tiempo real).</summary>
    string PlaybackSourceUrl(string deviceId, int channel, DateTime localStart, DateTime localEnd);

    /// <summary>
    /// Un cuadro JPEG del video en vivo del canal, para los equipos que no entregan foto por ISAPI (terminales, algunos
    /// frentes); null si el equipo no está conectado o no llegó imagen a tiempo.
    /// </summary>
    Task<byte[]?> GrabFrameAsync(string deviceId, int channel, int streamType, CancellationToken ct = default);

    /// <summary>
    /// Abre la voz con el equipo; el audio que mande llega por <paramref name="onAudio"/> en su codificación. Con
    /// <paramref name="broadcast"/> se pide solo bajada (parlantes): el equipo no capta ni envía audio.
    /// </summary>
    Task<IIsupVoiceSession> OpenVoiceAsync(string deviceId, int channel, Action<ReadOnlyMemory<byte>> onAudio, bool broadcast = false,
        CancellationToken ct = default);

    /// <summary>
    /// Publica un archivo (p. ej. la foto de un rostro) en una URL de corta vida
    /// que el equipo puede descargar desde donde esté: el túnel ISAPI no lleva
    /// multipart, así que los equipos ISUP reciben las fotos por URL.
    /// </summary>
    string PublishDownload(byte[] content, string contentType, TimeSpan lifetime);
}

/// <summary>Punto de acceso al transporte ISUP para los drivers (que no tienen inyección de dependencias).</summary>
public static class IsupGateway
{
    public static IIsupGateway? Current { get; set; }

    /// <summary>El transporte, o un error claro si el servicio ISUP no está disponible en este servidor.</summary>
    public static IIsupGateway Require() =>
        Current ?? throw new DriverException("El servicio ISUP del servidor no está disponible; revise Sistema → Servicios.");

    /// <summary>
    /// ¿Es una URL de video del servicio ISUP (<see cref="IIsupGateway.LiveSourceUrl"/> o <see cref="IIsupGateway.PlaybackSourceUrl"/>)?
    /// Entregan MPEG-PS por HTTP local: quien la consuma (MediaMTX, la exportación) la lee con FFmpeg y no como RTSP.
    /// </summary>
    public static bool IsMediaSource(string? url) =>
        url is not null && url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) &&
        (url.Contains("/live/", StringComparison.Ordinal) || url.Contains("/playback/", StringComparison.Ordinal));

    /// <summary>Valida un ID ISUP: 1 a 32 caracteres, letras, números, guion y guion bajo.</summary>
    public static string? ValidateDeviceId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "Falta el ID ISUP del equipo.";
        id = id.Trim();
        if (id.Length > 32) return "El ID ISUP puede tener como máximo 32 caracteres.";
        if (!id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return "El ID ISUP solo admite letras, números, guion y guion bajo.";
        return null;
    }

    /// <summary>Valida una clave ISUP (la «clave de cifrado» que se escribe en el equipo): 1 a 32 caracteres imprimibles, sin espacios.</summary>
    public static string? ValidateKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "Falta la clave ISUP del equipo.";
        if (key.Length > 32) return "La clave ISUP puede tener como máximo 32 caracteres.";
        if (key.Any(c => c <= ' ' || c > '~')) return "La clave ISUP solo admite caracteres ASCII imprimibles, sin espacios.";
        return null;
    }
}
