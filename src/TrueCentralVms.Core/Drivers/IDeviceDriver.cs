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
}

/// <summary>Capacidades del driver, para que el panel adapte el asistente.</summary>
public sealed record DriverCapabilities(bool SupportsSnapshot, bool SupportsDiscovery, int DefaultSdkPort, int DefaultRtspPort);

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
