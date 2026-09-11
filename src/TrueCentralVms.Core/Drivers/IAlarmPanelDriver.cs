using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Core.Drivers;

/// <summary>
/// Datos de conexión a un panel de alarma (API HTTP del fabricante).
/// <paramref name="DeviceId"/> identifica al panel dentro de una pasarela
/// cuando el driver no habla con el equipo sino con un intermediario (Hik IP
/// Receiver Pro: uuid, serie, cuenta o ID ISUP del equipo); null para los
/// drivers que se conectan directo al panel.
/// </summary>
public sealed record AlarmConnectionInfo(string Host, int Port, bool UseHttps, string Username, string Password, string? DeviceId = null);

/// <summary>Identificación del panel obtenida al validar las credenciales.</summary>
public sealed record AlarmPanelInfo(string? Model, string? SerialNumber, string? FirmwareVersion, string? DeviceType);

public sealed record AlarmAreaState(int Number, string Name, bool Enabled, AlarmArmState ArmState, bool InAlarm, int ExitDelaySeconds = 0);

public sealed record AlarmZoneState(
    int Number,
    int? AreaNumber,
    string Name,
    string? ZoneType,
    string? DetectorType,
    AlarmZoneStatus Status,
    bool Bypassed,
    bool Armed,
    bool InAlarm,
    bool Tamper,
    bool LowBattery,
    int? Signal,
    string? Model);

/// <summary>Foto completa del estado del panel (áreas + zonas).</summary>
/// <summary>
/// Estado del chasis/host del panel (no de una zona): sabotaje de la tapa,
/// corriente de red y cantidad de fallas activas que informa el equipo.
/// </summary>
public sealed record AlarmHostState(bool Tamper, bool AcLoss, int FaultCount);

public sealed record AlarmPanelState(
    IReadOnlyList<AlarmAreaState> Areas,
    IReadOnlyList<AlarmZoneState> Zones,
    /// <summary>Estado del chasis (tapa/sabotaje, corriente). null si el equipo no lo expone.</summary>
    AlarmHostState? Host = null);

/// <summary>
/// Evento empujado por el panel. <see cref="Timestamp"/> viene en UTC (el
/// driver convierte la hora con zona que informa el equipo; si el equipo no
/// informa hora, el driver pone la del servidor).
/// </summary>
public sealed record AlarmPanelEvent(
    DateTime Timestamp,
    AlarmEventKind Kind,
    AlarmSeverity Severity,
    string? Code,
    string Description,
    int? AreaNumber,
    int? ZoneNumber,
    string? Operator,
    /// <summary>Contenido crudo tal como llegó (JSON/XML), para diagnóstico.</summary>
    string? Raw);

/// <summary>Canal de eventos vivo contra el panel. Liberarlo cierra la conexión.</summary>
public interface IAlarmSubscription : IAsyncDisposable
{
    /// <summary>false cuando el canal se cayó y hay que rehacerlo.</summary>
    bool IsAlive { get; }
}

/// <summary>
/// Driver de una marca de paneles de alarma. Todo lo que hace el VMS con un
/// panel pasa por aquí: validar credenciales, leer áreas y zonas, armar,
/// desarmar, anular zonas y recibir los eventos que el panel empuja.
/// Los métodos lanzan <see cref="DriverException"/> con mensaje en español
/// cuando el panel es inalcanzable, rechaza las credenciales o rechaza la
/// orden.
/// </summary>
public interface IAlarmPanelDriver
{
    /// <summary>Valida usuario/contraseña y devuelve la identificación del panel.</summary>
    Task<AlarmPanelInfo> ProbeAsync(AlarmConnectionInfo info, CancellationToken ct = default);

    /// <summary>Estado completo de áreas y zonas.</summary>
    Task<AlarmPanelState> GetStateAsync(AlarmConnectionInfo info, CancellationToken ct = default);

    /// <summary>Arma un área (0 = todas las áreas del panel).</summary>
    Task ArmAsync(AlarmConnectionInfo info, int areaNumber, AlarmArmMode mode, CancellationToken ct = default);

    /// <summary>Desarma un área (0 = todas).</summary>
    Task DisarmAsync(AlarmConnectionInfo info, int areaNumber, CancellationToken ct = default);

    /// <summary>Silencia/borra la alarma activa de un área (0 = todas) sin cambiar el armado.</summary>
    Task ClearAlarmAsync(AlarmConnectionInfo info, int areaNumber, CancellationToken ct = default);

    /// <summary>Anula (bypass) o restituye una zona.</summary>
    Task SetZoneBypassAsync(AlarmConnectionInfo info, int zoneNumber, bool bypassed, CancellationToken ct = default);

    /// <summary>
    /// Abre el canal de eventos del panel. Cada evento llega por
    /// <paramref name="onEvent"/> desde un hilo del driver (encolar y volver);
    /// <paramref name="onActivity"/> se invoca ante cualquier mensaje (incluso
    /// latidos) para que el servidor sepa que el panel sigue vivo.
    /// </summary>
    Task<IAlarmSubscription> SubscribeEventsAsync(AlarmConnectionInfo info, Action<AlarmPanelEvent> onEvent,
        Action? onActivity = null, CancellationToken ct = default);

    /// <summary>
    /// Traduce una notificación que el panel empujó al servidor por HTTP
    /// (p. ej. la "HTTP Host Notification" de Hikvision, que se configura en
    /// el panel con la URL del VMS). Devuelve null si no es un evento
    /// reconocible; el servidor relee el estado del panel de inmediato en
    /// cualquier caso. Opcional: por omisión el driver no la entiende.
    /// </summary>
    AlarmPanelEvent? ParsePushedEvent(string contentType, byte[] body) => null;
}

/// <summary>Resultado de asegurar el registro de un panel en su pasarela.</summary>
public enum GatewayRegistrationOutcome
{
    /// <summary>Ya estaba registrado y la pasarela lo ve en línea.</summary>
    Registered,
    /// <summary>Ya estaba registrado, pero la pasarela lo ve fuera de línea (el panel no reporta).</summary>
    RegisteredOffline,
    /// <summary>No estaba y se registró ahora con la clave indicada.</summary>
    ReRegistered,
    /// <summary>No está y no hay clave con que registrarlo.</summary>
    NotRegistered,
}

/// <summary>
/// Estado del registro. <paramref name="StableDeviceId"/> es el identificador
/// que conviene guardar (el ID ISUP/OTAP, que no cambia aunque el equipo se
/// vuelva a registrar; el uuid de la pasarela sí cambia).
/// </summary>
public sealed record GatewayRegistration(GatewayRegistrationOutcome Outcome, string? DevIndex, string? StableDeviceId, string? Message);

/// <summary>
/// Driver que no habla con el panel sino con una pasarela (receptora) en la
/// que el panel tiene que estar registrado para comunicarse. La pasarela es la
/// verdad de lo que funciona; el VMS guarda la intención (ID y clave) y con
/// esto la hace cumplir.
/// </summary>
public interface IAlarmGatewayDriver : IAlarmPanelDriver
{
    /// <summary>
    /// Comprueba que el equipo <paramref name="deviceId"/> esté registrado en la
    /// pasarela de <paramref name="info"/>; si falta y hay clave, lo registra.
    /// Con <paramref name="replaceIfPresent"/> (clave recién escrita por un
    /// administrador) se quita y se vuelve a registrar con esa clave, porque la
    /// registrada no se puede leer para compararla. Lanza <see cref="DriverException"/>
    /// solo si la pasarela no responde o rechaza el alta.
    /// </summary>
    Task<GatewayRegistration> EnsureRegisteredAsync(AlarmConnectionInfo info, string deviceId, string? deviceKey,
        string? protocol, bool replaceIfPresent = false, string? name = null, CancellationToken ct = default);

    /// <summary>Quita el equipo de la pasarela (por uuid o por ID estable). No falla si ya no está.</summary>
    Task UnregisterAsync(AlarmConnectionInfo info, string deviceId, CancellationToken ct = default);

    /// <summary>Equipos registrados en la pasarela: (uuid, ID estable, nombre, estado según la pasarela).</summary>
    Task<IReadOnlyList<(string DevIndex, string? StableDeviceId, string Name, string? Status)>> ListRegisteredAsync(
        AlarmConnectionInfo info, CancellationToken ct = default);

    /// <summary>
    /// Deja la pasarela en condiciones de entregar eventos. Algunas traen esa
    /// salida apagada de fábrica y rechazan la suscripción hasta habilitarla
    /// (el IP Receiver Pro pide «Automation Output → Protocol» con tipo
    /// «Private»), y sin eventos los paneles quedan solo con el sondeo. Es
    /// idempotente: no escribe nada si ya estaba. Lanza
    /// <see cref="DriverException"/> si la pasarela no responde o no deja
    /// cambiarlo. Por omisión no hay nada que preparar.
    /// </summary>
    Task EnsureEventsEnabledAsync(AlarmConnectionInfo info, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Fábrica de un driver de paneles de alarma, con clave estable que se guarda
/// en la base. Para soportar otra marca basta con implementarla y registrarla
/// en el arranque del servidor.
/// </summary>
public interface IAlarmPanelDriverFactory
{
    /// <summary>Clave estable ("hikvision-isapi").</summary>
    string DriverKey { get; }

    string DisplayName { get; }

    /// <summary>Puerto HTTP por omisión del fabricante.</summary>
    int DefaultPort { get; }

    /// <summary>true si el fabricante exige HTTPS por omisión.</summary>
    bool DefaultHttps { get; }

    /// <summary>
    /// true si el driver habla con una pasarela y necesita además el
    /// identificador del panel dentro de ella (<see cref="AlarmConnectionInfo.DeviceId"/>).
    /// </summary>
    bool NeedsDeviceId => false;

    IAlarmPanelDriver Create();
}

/// <summary>Registro de drivers de paneles de alarma (poblado por inyección de dependencias).</summary>
public sealed class AlarmDriverRegistry
{
    private readonly Dictionary<string, IAlarmPanelDriverFactory> _factories;

    public AlarmDriverRegistry(IEnumerable<IAlarmPanelDriverFactory> factories)
    {
        _factories = factories.ToDictionary(f => f.DriverKey, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IAlarmPanelDriverFactory> All => _factories.Values;

    public IAlarmPanelDriverFactory? Find(string driverKey) =>
        _factories.TryGetValue(driverKey, out var factory) ? factory : null;
}
