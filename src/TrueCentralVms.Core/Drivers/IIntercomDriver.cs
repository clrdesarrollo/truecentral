namespace TrueCentralVms.Core.Drivers;

/// <summary>
/// Datos de conexión a un frente de citofonía (videoportero de calle). Los
/// equipos Hikvision usan DOS canales: el SDK (puerto 8000) para la
/// señalización de llamadas y la voz, e ISAPI (HTTP) para identificación,
/// estado de llamada y apertura de puerta.
/// </summary>
public sealed record IntercomConnectionInfo(string Host, int Port, int HttpPort, string Username, string Password);

/// <summary>Identificación y capacidades del frente, leídas al validar las credenciales.</summary>
public sealed record IntercomInfo(
    string? Model,
    string? SerialNumber,
    string? FirmwareVersion,
    /// <summary>Nombre configurado en el propio equipo.</summary>
    string? DeviceName,
    /// <summary>Puertas (relés de cerradura) que puede abrir a distancia.</summary>
    int DoorCount,
    /// <summary>El botón de llamada ya está configurado para llamar a la central (el VMS).</summary>
    bool CallCenterEnabled,
    /// <summary>Códec de la voz ("ulaw", "alaw").</summary>
    string? AudioCodec,
    /// <summary>
    /// Segundos entre cuadros completos (I-frames) del stream principal. Hasta
    /// recibir uno el cliente no puede mostrar imagen: con valores de fábrica
    /// como 13 s el video de la llamada tarda eso en aparecer.
    /// </summary>
    double? KeyFrameSeconds = null);

/// <summary>Señal de llamada que empuja el frente (o que el servidor deduce de su estado).</summary>
public enum IntercomSignalKind
{
    /// <summary>Alguien tocó el timbre: la llamada está sonando en la central.</summary>
    Ringing,
    /// <summary>El visitante cortó antes de que contestaran.</summary>
    Cancelled,
    /// <summary>La llamada fue contestada (por la central o por otro receptor).</summary>
    Answered,
    /// <summary>La llamada fue rechazada.</summary>
    Rejected,
    /// <summary>Se cumplió el tiempo máximo de timbre sin respuesta.</summary>
    RingTimeout,
    /// <summary>La conversación terminó (colgó el frente o se cumplió su tope de conversación).</summary>
    HungUp,
    /// <summary>El frente está ocupado en otra llamada.</summary>
    Busy,
}

/// <summary>
/// Señal recibida del frente. <paramref name="Origin"/> describe quién llama
/// cuando el equipo lo informa (edificio/torre/piso/depto del frente).
/// </summary>
public sealed record IntercomCallSignal(IntercomSignalKind Kind, string? Origin, DateTime ReceivedAt);

/// <summary>Órdenes que la central envía al frente sobre la llamada en curso.</summary>
public enum IntercomCommand { Answer, Reject, HangUp }

/// <summary>Estado de llamada que reporta el frente al consultarlo.</summary>
public enum IntercomLineState { Idle, Ringing, InCall }

/// <summary>
/// Enlace de señalización de llamadas abierto con el frente: por él llegan
/// las llamadas a la central y salen las órdenes de contestar/rechazar/colgar.
/// Liberarlo cierra el enlace (y la sesión con el equipo).
/// </summary>
public interface IIntercomCallLink : IAsyncDisposable
{
    /// <summary>false cuando el equipo o la red cortaron el enlace (hay que reabrirlo).</summary>
    bool IsAlive { get; }

    Task SendAsync(IntercomCommand command, CancellationToken ct = default);
}

/// <summary>
/// Canal de voz bidireccional con el frente. Lo que capta el micrófono del
/// equipo llega por el callback indicado al abrirlo (en el códec de la
/// sesión); lo que se escribe suena en su parlante. Quien escribe debe
/// hacerlo al ritmo real de reproducción.
/// </summary>
public interface IIntercomVoiceSession : IAsyncDisposable
{
    /// <summary>Formato de ida y vuelta: "ulaw" o "alaw" (8 kHz mono).</summary>
    string Codec { get; }

    bool IsAlive { get; }

    Task WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default);
}

/// <summary>
/// Driver de una marca de citofonía. Los métodos lanzan
/// <see cref="DriverException"/> con mensaje en español cuando el equipo es
/// inalcanzable, rechaza las credenciales o rechaza la orden.
/// </summary>
public interface IIntercomDriver
{
    /// <summary>Valida credenciales (ambos canales) y lee identificación y capacidades.</summary>
    Task<IntercomInfo> ProbeAsync(IntercomConnectionInfo info, CancellationToken ct = default);

    /// <summary>Configura los botones de llamada del frente para que llamen a la central.</summary>
    Task EnableCallCenterAsync(IntercomConnectionInfo info, CancellationToken ct = default);

    /// <summary>Estado de llamada actual del frente (consulta liviana, sirve de sondeo de conexión).</summary>
    Task<IntercomLineState> GetLineStateAsync(IntercomConnectionInfo info, CancellationToken ct = default);

    /// <summary>
    /// Abre el enlace de señalización. <paramref name="onSignal"/> se invoca en
    /// un hilo del SDK: debe volver rápido y no lanzar.
    /// </summary>
    Task<IIntercomCallLink> OpenCallLinkAsync(IntercomConnectionInfo info, Action<IntercomCallSignal> onSignal,
        CancellationToken ct = default);

    /// <summary>
    /// Abre la voz bidireccional. <paramref name="onAudio"/> recibe las tramas
    /// del micrófono del frente en un hilo del SDK (copiarlas y volver).
    /// </summary>
    Task<IIntercomVoiceSession> OpenVoiceAsync(IntercomConnectionInfo info, Action<ReadOnlyMemory<byte>> onAudio,
        CancellationToken ct = default);

    /// <summary>Orden de contestar/rechazar/colgar SIN enlace de señalización (respaldo por ISAPI).</summary>
    Task SendCommandAsync(IntercomConnectionInfo info, IntercomCommand command, CancellationToken ct = default);

    /// <summary>Deja el stream principal con un cuadro completo por segundo (el video de la llamada aparece al instante).</summary>
    Task OptimizeVideoAsync(IntercomConnectionInfo info, CancellationToken ct = default);

    /// <summary>Abre (pulso) la puerta indicada, numerada desde 1.</summary>
    Task OpenDoorAsync(IntercomConnectionInfo info, int door, CancellationToken ct = default);
}

public interface IIntercomDriverFactory
{
    /// <summary>Clave estable ("hikvision-intercom").</summary>
    string DriverKey { get; }

    string DisplayName { get; }

    /// <summary>Puerto del SDK (señalización y voz).</summary>
    int DefaultPort { get; }

    /// <summary>Puerto HTTP (ISAPI).</summary>
    int DefaultHttpPort { get; }

    IIntercomDriver Create();
}

/// <summary>Registro de drivers de citofonía, poblado por inyección de dependencias.</summary>
public sealed class IntercomDriverRegistry
{
    private readonly Dictionary<string, IIntercomDriverFactory> _factories;

    public IntercomDriverRegistry(IEnumerable<IIntercomDriverFactory> factories)
    {
        _factories = factories.ToDictionary(f => f.DriverKey, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IIntercomDriverFactory> All => _factories.Values;

    public IIntercomDriverFactory? Find(string driverKey) =>
        _factories.TryGetValue(driverKey, out var factory) ? factory : null;
}
