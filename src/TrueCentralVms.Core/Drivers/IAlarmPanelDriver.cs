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
