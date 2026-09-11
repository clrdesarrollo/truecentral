namespace TrueCentralVms.Core.Drivers;

/// <summary>Datos de conexión de gestión a un dispositivo (puerto SDK/ONVIF, no RTSP).</summary>
public sealed record DeviceConnectionInfo(string Host, int Port, string Username, string Password);

/// <summary>
/// Resultado de sondear un dispositivo: credenciales validadas e información
/// real del equipo (modelo, serie, firmware) más sus canales.
/// </summary>
public sealed record DeviceProbeInfo(
    string? Model,
    string? SerialNumber,
    string? FirmwareVersion,
    /// <summary>Tipo sugerido según lo reportado por el equipo: "Camera" | "Dvr" | "Nvr" | "Xvr".</summary>
    string SuggestedType,
    int AnalogChannelCount,
    int IpChannelCount,
    IReadOnlyList<DeviceChannelInfo> Channels,
    /// <summary>Puerto RTSP real reportado por el equipo (null = el driver no pudo consultarlo).</summary>
    int? RtspPort = null);

/// <summary>
/// Canal detectado. <paramref name="ChannelNumber"/> es el número interno del
/// SDK (en NVR Hikvision los canales IP parten en 33); <paramref name="RtspChannel"/>
/// es el índice 1..N que usan las URL RTSP del fabricante.
/// <paramref name="MainStreamUrl"/>/<paramref name="SubStreamUrl"/> son URLs
/// RTSP SIN credenciales resueltas por el driver cuando el fabricante no usa
/// plantilla (ONVIF: GetStreamUri); null = usar <see cref="IDeviceDriver.BuildRtspUrl"/>.
/// </summary>
public sealed record DeviceChannelInfo(
    int ChannelNumber, int RtspChannel, string Name, bool IsOnline,
    string? MainStreamUrl = null, string? SubStreamUrl = null,
    /// <summary>true si el equipo reporta que este canal tiene PTZ (el control se muestra solo en ese caso).</summary>
    bool SupportsPtz = false);

public enum StreamProfile { Main = 0, Sub = 1 }

/// <summary>Movimientos PTZ continuos (arrancan con stop=false y terminan con stop=true).</summary>
public enum PtzCommand
{
    TiltUp, TiltDown, PanLeft, PanRight,
    UpLeft, UpRight, DownLeft, DownRight,
    ZoomIn, ZoomOut,
    FocusNear, FocusFar,
    IrisOpen, IrisClose,
}

/// <summary>Operaciones sobre puntos preestablecidos (presets) del PTZ.</summary>
public enum PtzPresetAction
{
    /// <summary>Mover la cámara al preset.</summary>
    Goto,
    /// <summary>Guardar la posición actual como preset.</summary>
    Set,
    /// <summary>Eliminar el preset.</summary>
    Clear,
}

/// <summary>
/// Driver de gestión de una marca de dispositivos (cámaras, DVR, NVR, XVR).
/// El transporte de video NO pasa por aquí (lo hace MediaMTX vía RTSP): el
/// driver valida credenciales, obtiene datos del equipo, enumera canales,
/// construye la URL RTSP del fabricante y captura snapshots.
/// </summary>
public interface IDeviceDriver
{
    /// <summary>
    /// Valida usuario/contraseña contra el equipo y devuelve su información y
    /// canales. Lanza <see cref="DriverException"/> con mensaje en español si
    /// el equipo es inalcanzable o rechaza las credenciales.
    /// </summary>
    Task<DeviceProbeInfo> ProbeAsync(DeviceConnectionInfo info, CancellationToken ct = default);

    /// <summary>URL RTSP del fabricante para un canal/perfil (con credenciales embebidas y URL-encoded).</summary>
    string BuildRtspUrl(DeviceConnectionInfo info, int rtspPort, int rtspChannel, StreamProfile profile);

    /// <summary>Fotograma JPEG del canal, o null si el equipo/driver no lo soporta.</summary>
    Task<byte[]?> CaptureSnapshotAsync(DeviceConnectionInfo info, int channelNumber, CancellationToken ct = default);

    /// <summary>
    /// Movimiento PTZ continuo: stop=false lo inicia y stop=true lo detiene
    /// (velocidad 1..7). Devuelve false si el driver o el canal no lo
    /// soportan. Implementación por defecto: sin soporte PTZ.
    /// </summary>
    Task<bool> PtzControlAsync(DeviceConnectionInfo info, int channelNumber, PtzCommand command, int speed, bool stop,
        CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>
    /// Operación sobre un preset PTZ (ir, guardar o borrar; índice 1..300).
    /// Devuelve false si el driver o el canal no lo soportan.
    /// </summary>
    Task<bool> PtzPresetAsync(DeviceConnectionInfo info, int channelNumber, PtzPresetAction action, int presetIndex,
        CancellationToken ct = default) => Task.FromResult(false);

    // ------------------------------------------------------------------
    // Reproducción remota: el video grabado vive en el DVR/NVR/tarjeta del
    // propio equipo; el VMS consulta los segmentos y reproduce por RTSP.
    // ------------------------------------------------------------------

    /// <summary>
    /// Segmentos grabados en el almacenamiento del equipo para un canal y un
    /// rango horario, en HORA LOCAL del equipo (los grabadores operan y
    /// responden en su propia hora). Lista vacía = sin grabaciones o sin
    /// soporte (implementación por defecto).
    /// </summary>
    Task<IReadOnlyList<RecordingSegment>> QueryRecordingsAsync(DeviceConnectionInfo info, int channelNumber,
        DateTime localStart, DateTime localEnd, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RecordingSegment>>([]);

    /// <summary>
    /// Días del mes (1..31) que tienen grabación en el canal. Lo usa el
    /// calendario del módulo Reproducción para marcar de un vistazo dónde
    /// buscar. Lista vacía = el equipo no sabe responderlo (el calendario
    /// simplemente no muestra marcas); no significa "sin grabaciones".
    /// </summary>
    Task<IReadOnlyList<int>> QueryRecordedDaysAsync(DeviceConnectionInfo info, int channelNumber,
        int year, int month, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<int>>([]);

    /// <summary>
    /// URL RTSP de reproducción del almacenamiento del equipo para el rango
    /// dado (hora local del equipo), con credenciales embebidas y URL-encoded;
    /// null si el driver no soporta playback remoto.
    /// </summary>
    string? BuildPlaybackUrl(DeviceConnectionInfo info, int rtspPort, int rtspChannel,
        DateTime localStart, DateTime localEnd) => null;

    /// <summary>
    /// true si la URL de <see cref="BuildPlaybackUrl"/> arranca exactamente en
    /// el instante pedido (Hikvision y Dahua llevan el rango en la propia
    /// URL). ONVIF no lo cumple: su posicionamiento viaja en una cabecera
    /// RTSP que el media server no envía, así que el equipo reproduce desde el
    /// comienzo de la grabación y el cliente lo avisa.
    /// </summary>
    bool SupportsExactPlaybackSeek => true;

    // ------------------------------------------------------------------
    // Reconocimiento de patentes (ANPR/LPR): la cámara ITS empuja cada
    // lectura por su canal de alarma; el VMS no analiza video.
    // ------------------------------------------------------------------

    /// <summary>true si el driver sabe recibir reconocimientos de patentes del equipo.</summary>
    bool SupportsAnpr => false;

    /// <summary>
    /// Abre el canal de eventos de patentes del equipo. Cada reconocimiento
    /// llega por <paramref name="onPlate"/> (en un hilo del SDK: el manejador
    /// debe encolar y volver rápido). Liberar la suscripción cierra el canal.
    /// </summary>
    Task<IPlateSubscription> SubscribePlatesAsync(DeviceConnectionInfo info, Action<PlateRecognition> onPlate,
        CancellationToken ct = default) =>
        throw new DriverException("Este driver no entrega reconocimientos de patentes.");

    // ------------------------------------------------------------------
    // Eventos del equipo (analíticas y alarmas): detección de movimiento,
    // cruce de línea, intrusión, pérdida de video, entradas de alarma...
    // El equipo los EMPUJA; el VMS no analiza video. Las analíticas se
    // configuran en el propio equipo: el VMS solo las escucha.
    // ------------------------------------------------------------------

    /// <summary>true si el driver sabe recibir los eventos de analítica/alarma del equipo.</summary>
    bool SupportsEvents => false;

    /// <summary>
    /// Abre el canal de eventos del equipo. Cada evento llega por
    /// <paramref name="onEvent"/> (en un hilo del SDK: el manejador debe
    /// encolar y volver rápido). Liberar la suscripción cierra el canal.
    /// </summary>
    Task<IDeviceEventSubscription> SubscribeEventsAsync(DeviceConnectionInfo info, Action<DeviceEvent> onEvent,
        CancellationToken ct = default) =>
        throw new DriverException("Este driver no entrega eventos del equipo.");
}

/// <summary>
/// Un evento empujado por una cámara o grabador. <paramref name="ChannelNumber"/>
/// es el número interno del SDK (0 = el equipo completo, sin canal);
/// <paramref name="At"/> viene en hora local del equipo.
/// </summary>
public sealed record DeviceEvent(
    int ChannelNumber,
    Contracts.VideoEventKind Kind,
    /// <summary>Descripción legible ("Cruce de línea (regla 'Portón')").</summary>
    string Description,
    DateTime At,
    /// <summary>Nombre de la regla de analítica en el equipo, si la informa.</summary>
    string? RuleName = null,
    /// <summary>Número de entrada de alarma (contacto seco), cuando corresponde.</summary>
    int? AlarmInput = null,
    /// <summary>Foto adjunta al evento (JPEG), si el equipo la envía.</summary>
    byte[]? Image = null);

/// <summary>Suscripción viva a los eventos de un equipo. Liberarla cierra el canal.</summary>
public interface IDeviceEventSubscription : IAsyncDisposable
{
    /// <summary>false cuando la suscripción se cayó y hay que rehacerla.</summary>
    bool IsAlive { get; }
}

/// <summary>Tramo grabado en el equipo (horas locales del equipo).</summary>
public sealed record RecordingSegment(DateTime Start, DateTime End, RecordingKind Kind);

/// <summary>Origen de la grabación (colorea la línea de tiempo del cliente).</summary>
public enum RecordingKind { Continuous, Motion, Alarm, Manual, Other }

/// <summary>Capacidades del driver, para que el panel adapte el asistente.</summary>
public sealed record DriverCapabilities(bool SupportsSnapshot, bool SupportsDiscovery, int DefaultSdkPort,
    int DefaultRtspPort,
    /// <summary>El driver puede recibir reconocimientos de patentes (módulo Aplicaciones).</summary>
    bool SupportsAnpr = false,
    /// <summary>El driver puede recibir eventos de analítica/alarma del equipo (automatizaciones).</summary>
    bool SupportsEvents = false);

/// <summary>
/// Fábrica de un driver, identificada por una clave estable que se guarda en
/// la base de datos. Para soportar una marca nueva basta con implementar esta
/// interfaz en su propio proyecto y registrarla en el arranque del servidor.
/// </summary>
public interface IDeviceDriverFactory
{
    /// <summary>Clave estable ("hikvision-netsdk", "dahua-netsdk", "onvif").</summary>
    string DriverKey { get; }

    /// <summary>Nombre para mostrar en el panel ("Hikvision (SDK nativo)").</summary>
    string DisplayName { get; }

    DriverCapabilities Capabilities { get; }

    IDeviceDriver Create();
}

/// <summary>Error de un driver con mensaje apto para mostrar al usuario.</summary>
public class DriverException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Registro de drivers disponibles, poblado por inyección de dependencias.</summary>
public sealed class DriverRegistry
{
    private readonly Dictionary<string, IDeviceDriverFactory> _factories;

    public DriverRegistry(IEnumerable<IDeviceDriverFactory> factories)
    {
        _factories = factories.ToDictionary(f => f.DriverKey, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IDeviceDriverFactory> All => _factories.Values;

    public IDeviceDriverFactory? Find(string driverKey) =>
        _factories.TryGetValue(driverKey, out var factory) ? factory : null;
}
