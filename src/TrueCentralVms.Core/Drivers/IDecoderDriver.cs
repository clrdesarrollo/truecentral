namespace TrueCentralVms.Core.Drivers;

/// <summary>Datos de conexión a un decodificador.</summary>
public sealed record DecoderConnectionInfo(string Host, int Port, string Username, string Password);

/// <summary>Tipo de salida física de video de un decoder.</summary>
public enum DisplayOutputType
{
    Vga,
    Bnc,
    Hdmi,
    Dvi,
    Other,
}

/// <summary>Una salida de video reportada por el decoder.</summary>
/// <param name="WindowModes">Cantidades de sub-ventanas soportadas por la salida (1, 4, 6, 9, 12, 16, ...).</param>
public sealed record DisplayOutputInfo(DisplayOutputType Type, int Index, int ChannelNo, IReadOnlyList<int> WindowModes)
{
    public string Label => $"{Type.ToString().ToUpperInvariant()} {Index}";
}

/// <summary>Estado en vivo de una salida física.</summary>
/// <param name="ChannelNo">Canal de display, el mismo de <see cref="DisplayOutputInfo.ChannelNo"/>.</param>
/// <param name="Resolution">Resolución actual legible ("1920×1080 a 60 Hz", "PAL"); null = el equipo no la informa.</param>
/// <param name="Connected">Hay un monitor conectado a la salida; null = el equipo no lo informa.</param>
public sealed record DisplayOutputStatus(int ChannelNo, string? Resolution, bool? Connected);

/// <summary>Entrada de señal local del decodificador (el PC de un operador cableado por HDMI, por ejemplo).</summary>
/// <param name="Type">Tipo de conector: "Hdmi", "Vga", "Dvi", "Bnc", "Dp", "Sdi"...</param>
/// <param name="Signal">Hay señal en la entrada; null = el equipo no lo informa.</param>
public sealed record LocalInputInfo(int Number, string Name, string Type, bool? Signal, string? Resolution);

/// <summary>Capacidades reportadas por un decodificador.</summary>
public sealed class DecoderCapabilities
{
    /// <summary>Primer número de canal de decodificación.</summary>
    public int DecodeChannelStart { get; init; }
    /// <summary>Cantidad de canales de decodificación.</summary>
    public int DecodeChannelCount { get; init; }
    /// <summary>Salidas de video físicas (HDMI/VGA/BNC/DVI).</summary>
    public IReadOnlyList<DisplayOutputInfo> Displays { get; init; } = Array.Empty<DisplayOutputInfo>();
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }

    public IEnumerable<int> DecodeChannels =>
        Enumerable.Range(DecodeChannelStart, DecodeChannelCount);
}

/// <summary>Cómo debe el decoder obtener el stream de la fuente.</summary>
public enum StreamSourceMode
{
    /// <summary>Conexión directa a un dispositivo (IP, puerto, usuario, canal) con protocolo del fabricante.</summary>
    DeviceChannel = 0,
    /// <summary>Por URL (ej. RTSP). Útil para fuentes ONVIF o de otras marcas.</summary>
    Url = 1,
}

/// <summary>Descripción de la fuente de stream que un canal de decodificación debe reproducir.</summary>
public sealed record StreamSource
{
    public StreamSourceMode Mode { get; init; } = StreamSourceMode.DeviceChannel;

    // Modo DeviceChannel
    public string Host { get; init; } = "";
    public int Port { get; init; } = 8000;
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    /// <summary>Número de canal en el dispositivo remoto.</summary>
    public int Channel { get; init; } = 1;
    /// <summary>0 = stream principal, 1 = substream.</summary>
    public int StreamType { get; init; } = 0;
    /// <summary>0 = TCP, 1 = UDP.</summary>
    public int TransportProtocol { get; init; } = 0;

    // Modo Url
    public string? Url { get; init; }
}

/// <summary>Estado de un canal de decodificación.</summary>
public sealed record DecodeChannelStatus(int DecodeChannel, bool Decoding);

/// <summary>
/// Posición de una sub-ventana en la grilla base de su pantalla: casilla
/// superior-izquierda (índice en orden de lectura) y cuántas casillas abarca
/// (agrupación de ventanas para layouts personalizados).
/// </summary>
public sealed record WallWindowSlot(int SlotIndex, int SpanCols, int SpanRows);

/// <summary>
/// Layout de una pantalla del wall para enviar al equipo: salida física,
/// posición en la grilla y canales de decodificación de sus sub-ventanas
/// (en orden de lectura). Cuando <paramref name="Slots"/> no es null, cada
/// canal trae su casilla y span en la grilla base de <paramref name="BaseMode"/>
/// casillas (ventanas agrupadas); si es null, las ventanas son 1×1 en orden.
/// </summary>
public sealed record ScreenWindowLayout(
    int DisplayChannel, int Row, int Col, IReadOnlyList<int> WindowDecodeChannels,
    IReadOnlyList<WallWindowSlot>? Slots = null, int BaseMode = 0);

/// <summary>
/// Ventana flotante del muro: rect libre en unidades de celda del wall (1.0 =
/// el ancho/alto de un monitor; puede cruzar monitores) que se dibuja ENCIMA
/// del mosaico. El orden de la lista define la pila (la última queda arriba).
/// </summary>
public sealed record FloatingWindowLayout(int DecodeChannel, double X, double Y, double W, double H);

/// <summary>
/// Abstracción de un driver de decodificador de video wall.
/// Implementaciones: Hikvision HCNetSDK hoy; Dahua, ONVIF u otros mañana.
/// Una instancia representa una sesión contra un decoder concreto.
/// </summary>
public interface IDecoderDriver : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(DecoderConnectionInfo info, CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);

    Task<DecoderCapabilities> GetCapabilitiesAsync(CancellationToken ct = default);

    /// <summary>
    /// Descarta el estado del muro cacheado en la sesión: la próxima
    /// sincronización verifica todo contra el equipo (escaneo completo) en vez
    /// de aplicar un diff en memoria. Úselo en la re-sincronización manual.
    /// </summary>
    void InvalidateWallCache() { }

    /// <summary>Comienza a decodificar la fuente indicada en el canal de decodificación dado.</summary>
    Task StartDecodingAsync(int decodeChannel, StreamSource source, CancellationToken ct = default);

    /// <summary>Detiene la decodificación del canal dado (la salida queda en negro/logo).</summary>
    Task StopDecodingAsync(int decodeChannel, CancellationToken ct = default);

    /// <summary>
    /// Aplica el layout completo del wall en el equipo: por cada pantalla, su
    /// salida física, su posición (fila, columna) y los canales de
    /// decodificación de sus sub-ventanas, más las ventanas flotantes (rect
    /// libre, siempre en la capa superior). En equipos de familia video wall
    /// además vincula las salidas a pantallas del muro si aún no lo están.
    /// Devuelve el resultado por salida (clave = DisplayChannel; null = OK).
    /// </summary>
    Task<IReadOnlyDictionary<int, string?>> ConfigureWallAsync(
        IReadOnlyList<ScreenWindowLayout> screens,
        IReadOnlyList<FloatingWindowLayout>? floating = null,
        CancellationToken ct = default);

    /// <summary>
    /// Libera los recursos en pantalla de un canal que dejó de usarse. En los
    /// equipos de familia video wall cierra la ventana del muro; en decoders
    /// clásicos no hace nada.
    /// </summary>
    Task CloseWindowAsync(int decodeChannel, CancellationToken ct = default);

    /// <summary>
    /// Pantalla completa instantánea: redimensiona la ventana del canal para
    /// cubrir toda su pantalla (fullscreen=true) o la devuelve a su sub-celda
    /// (fullscreen=false), sin detener ni reiniciar ninguna decodificación. Es
    /// el mecanismo que usa HikCentral para hacerlo inmediato.
    /// </summary>
    Task ZoomWindowAsync(int decodeChannel, bool fullscreen, CancellationToken ct = default);

    /// <summary>
    /// Muro completo instantáneo: redimensiona la ventana del canal para
    /// cubrir TODO el muro —todas las pantallas como una sola— (on=true) o la
    /// devuelve a su sub-celda (on=false), sin tocar ninguna decodificación.
    /// </summary>
    Task ZoomWindowToWallAsync(int decodeChannel, bool on, CancellationToken ct = default) =>
        throw new NotSupportedException("Este decodificador no soporta ocupar el muro completo.");

    /// <summary>
    /// Resolución y monitor conectado de cada salida física (después de
    /// <see cref="GetCapabilitiesAsync"/>). Vacío = el equipo no lo informa.
    /// </summary>
    Task<IReadOnlyList<DisplayOutputStatus>> GetOutputStatusAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DisplayOutputStatus>>([]);

    /// <summary>Entradas de señal locales del equipo. null = el equipo o el driver no las informa.</summary>
    Task<IReadOnlyList<LocalInputInfo>?> GetLocalInputsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LocalInputInfo>?>(null);

    /// <summary>
    /// El equipo puede mostrar en cada monitor el número de la salida que lo
    /// alimenta (para saber qué monitor es cuál al armar el muro). Válido
    /// después de <see cref="GetCapabilitiesAsync"/>.
    /// </summary>
    bool CanIdentifyOutputs => false;

    /// <summary>Muestra (show=true) u oculta el número de salida en todos los monitores.</summary>
    Task ShowOutputNumbersAsync(bool show, CancellationToken ct = default) =>
        throw new NotSupportedException("Este decodificador no puede mostrar el número de salida en los monitores.");
}

/// <summary>
/// El driver no tiene el mapa de ventanas del wall en memoria (típico tras un
/// reinicio del servidor): el orquestador debe sincronizar el layout con el
/// equipo y reintentar la operación.
/// </summary>
public sealed class WallSyncRequiredException : InvalidOperationException
{
    public WallSyncRequiredException(string message) : base(message) { }
}

/// <summary>Fábrica de drivers; cada protocolo/marca registra la suya.</summary>
public interface IDecoderDriverFactory
{
    /// <summary>Clave estable que se guarda en la BD (ej. "hikvision-netsdk").</summary>
    string DriverKey { get; }
    /// <summary>Nombre para mostrar en la UI (ej. "Hikvision (HCNetSDK)").</summary>
    string DisplayName { get; }
    IDecoderDriver Create();
}

/// <summary>Registro de drivers disponibles, resuelto por clave.</summary>
public sealed class DecoderDriverRegistry
{
    private readonly Dictionary<string, IDecoderDriverFactory> _factories;

    public DecoderDriverRegistry(IEnumerable<IDecoderDriverFactory> factories)
    {
        _factories = factories.ToDictionary(f => f.DriverKey, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IDecoderDriverFactory> Factories => _factories.Values;

    public IDecoderDriver Create(string driverKey)
    {
        if (!_factories.TryGetValue(driverKey, out var factory))
            throw new InvalidOperationException(
                $"No hay driver registrado con la clave '{driverKey}'. Disponibles: {string.Join(", ", _factories.Keys)}");
        return factory.Create();
    }
}
