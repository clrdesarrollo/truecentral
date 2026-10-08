namespace TrueCentralVms.Core.Contracts;

// DTOs compartidos entre el servidor, el cliente WPF y (vía JSON) el panel
// web. Son el contrato de la API: cualquier cambio aquí es un cambio de
// protocolo y debe mantenerse compatible entre versiones cercanas.

// ---------------------------------------------------------------------------
// Autenticación y configuración inicial
// ---------------------------------------------------------------------------

public sealed record LoginRequest(string Username, string Password);

public sealed record LoginResponse(string Token, string Username, string Role, DateTime ExpiresAt);

/// <summary>Cambio de clave autenticado por la clave actual (sirve también con la clave vencida).</summary>
public sealed record ChangePasswordRequest(string Username, string CurrentPassword, string NewPassword);

/// <summary>
/// Estado de la configuración inicial. <paramref name="SetupRequired"/> indica
/// que no existe ningún usuario (servidor "desactivado");
/// <paramref name="IsLocalRequest"/> le dice al panel si esta solicitud vino de
/// loopback (el asistente solo funciona desde la máquina del servidor).
/// </summary>
public sealed record SetupStatusDto(bool SetupRequired, bool IsLocalRequest, string ServerVersion);

public sealed record SetupAdminRequest(string Username, string Password);

// ---------------------------------------------------------------------------
// Usuarios
// ---------------------------------------------------------------------------

/// <param name="RestrictToLocations">Alcance por ubicación (solo operadores): false = todas las ubicaciones.</param>
/// <param name="ViewOutsideScope">Con alcance restringido: ve el resto sin poder operarlo.</param>
/// <param name="LocationIds">Ubicaciones de su alcance (cada una incluye sus sububicaciones).</param>
/// <param name="Role">Nivel derivado de sus roles: "Admin" si tiene el rol Administrador, si no "Operator".</param>
/// <param name="Editable">La sesión que lo pide puede modificarlo (no tiene más permisos ni más alcance que ella).</param>
/// <param name="IsSuperAdmin">El administrador creado al activar la plataforma: acceso
/// total sin importar los roles; solo él modifica su usuario y nadie lo elimina.</param>
/// <param name="EffectiveScope">Lo que efectivamente ve y opera (roles + límite propio), en palabras.</param>
public sealed record UserDto(int Id, string Username, string Role, bool Enabled, DateTime CreatedAt, DateTime PasswordChangedAt,
    bool RestrictToLocations = false, bool ViewOutsideScope = false, IReadOnlyList<int>? LocationIds = null,
    IReadOnlyList<int>? RoleIds = null, IReadOnlyList<string>? RoleNames = null, bool Editable = true,
    bool IsSuperAdmin = false, string? EffectiveScope = null);

/// <summary>
/// Alta/edición de usuario. En edición, Password null o vacía = no cambiar; los
/// campos del alcance en null = no cambiar (un cliente anterior no lo borra).
/// <paramref name="RoleIds"/> null = según <paramref name="Role"/> (clientes
/// anteriores a los roles: "Admin" → Administrador, "Operator" → Operador) o,
/// en edición sin Role, conservar los actuales.
/// </summary>
public sealed record UserWriteDto(string Username, string? Password, string? Role, bool Enabled,
    bool? RestrictToLocations = null, bool? ViewOutsideScope = null, IReadOnlyList<int>? LocationIds = null,
    IReadOnlyList<int>? RoleIds = null);

/// <summary>Alcance del usuario conectado (para mostrarlo y adaptar la interfaz).</summary>
/// <param name="Restricted">true = solo opera (y salvo <paramref name="ViewOutsideScope"/>, solo ve) sus ubicaciones.</param>
/// <param name="Locations">Lo que abarca, en palabras: ubicaciones (con su ruta) y recursos sueltos.</param>
public sealed record UserScopeDto(bool Restricted, bool ViewOutsideScope, IReadOnlyList<int> LocationIds, IReadOnlyList<string> Locations);

/// <summary>
/// Qué puede OPERAR la sesión (armar, abrir una puerta, mover un PTZ, hablar…),
/// no solo ver. Lo arma el servidor con las mismas reglas con que valida cada
/// orden; la interfaz lo consulta para deshabilitar lo que respondería 403. Con
/// <paramref name="All"/> opera todo lo que ve y las listas van vacías.
/// </summary>
/// <param name="Locations">Ubicaciones sobre las que puede dar órdenes (con sus sububicaciones).</param>
/// <param name="Channels">Canales (PTZ y presets), por id.</param>
/// <param name="Areas">Áreas de alarma como "panel/número".</param>
/// <param name="Zones">Zonas de alarma como "panel/número" (anular / restituir).</param>
/// <param name="WholePanels">Paneles cuyas áreas puede operar todas: las órdenes de "todo el panel".</param>
/// <param name="Doors">Puertas de control de acceso, por id.</param>
public sealed record OperableDto(
    bool All,
    IReadOnlyList<int> Locations,
    IReadOnlyList<int> Channels,
    IReadOnlyList<string> Areas,
    IReadOnlyList<string> Zones,
    IReadOnlyList<int> WholePanels,
    IReadOnlyList<int> Doors,
    IReadOnlyList<int> Fences,
    IReadOnlyList<int> Speakers,
    IReadOnlyList<int> Intercoms);

// ---------------------------------------------------------------------------
// Salud del sistema
// ---------------------------------------------------------------------------

/// <summary>
/// Uso de recursos de la máquina del servidor (indicadores CPU/RAM/disco del
/// cliente y del panel). Los GB vienen redondeados a 1 decimal.
/// </summary>
public sealed record SystemMetricsDto(
    double CpuPercent,
    double RamPercent, double RamUsedGb, double RamTotalGb,
    double DiskPercent, double DiskUsedGb, double DiskTotalGb, string DiskName);

// ---------------------------------------------------------------------------
// Supervisor de servicios (watchdog)
// ---------------------------------------------------------------------------

/// <summary>Estado de un servicio supervisado por el watchdog del servidor.</summary>
public enum ManagedServiceState
{
    /// <summary>Detenido a pedido de un administrador: el watchdog no lo toca.</summary>
    Stopped,
    Starting,
    Running,
    Stopping,
    /// <summary>Cayó o dejó de responder; con auto-reinicio activo el watchdog lo reintenta.</summary>
    Failed,
    /// <summary>Deshabilitado en la configuración: no se ejecuta ni se supervisa.</summary>
    Disabled,
}

/// <summary>
/// Un servicio supervisado: proceso hijo (PostgreSQL, MediaMTX) o subsistema
/// interno del servidor (monitor de dispositivos, paneles de alarma, ...).
/// <paramref name="Kind"/> es "process" o "subsystem". <paramref name="CanStop"/>
/// es false en los esenciales (la base de datos): solo se pueden reiniciar.
/// </summary>
public sealed record ManagedServiceDto(
    string Id, string Name, string Description, string Kind,
    ManagedServiceState State, DateTime? SinceUtc, string? Detail,
    string? LastError, DateTime? LastErrorAtUtc,
    int RestartCount, int FailedAttempts, DateTime? NextRetryAtUtc,
    bool AutoRestart, bool CanStop, bool CanControl);

/// <summary>El propio proceso del servidor (host de todos los servicios).</summary>
public sealed record ServerProcessDto(
    string Version, int ProcessId, DateTime StartedAtUtc, double UptimeSeconds,
    bool IsWindowsService, string ServiceName, double WorkingSetMb,
    int CheckIntervalSeconds, bool CanRestart, string? RestartHint);

public sealed record ServicesOverviewDto(ServerProcessDto Server, IReadOnlyList<ManagedServiceDto> Services);

public sealed record AutoRestartRequest(bool Enabled);

// ---------------------------------------------------------------------------
// Contrato del hub SignalR
// ---------------------------------------------------------------------------

/// <summary>
/// Contrato del hub de tiempo real. Solo eventos servidor→cliente: las
/// escrituras siempre van por la API REST.
/// </summary>
public static class VmsHubContract
{
    public const string HubPath = "/hubs/vms";

    /// <summary>Cambió el estado de un dispositivo (payload: DeviceDto).</summary>
    public const string DeviceStatusChanged = nameof(DeviceStatusChanged);

    /// <summary>Cambió la configuración de una entidad; recargar (payload: string "devices" | "channels" | "users" | "decoders" | "walls" | "anpr-sources" | "alarm-panels").</summary>
    public const string ConfigChanged = nameof(ConfigChanged);

    /// <summary>Cambió el estado de un muro de video (payload: WallDto).</summary>
    public const string WallStateChanged = nameof(WallStateChanged);

    /// <summary>Cambió el conjunto de sesiones de streaming activas (payload: ActiveSessionDto[]).</summary>
    public const string SessionsChanged = nameof(SessionsChanged);

    /// <summary>Llegó un reconocimiento de patente nuevo (payload: PlateEventDto).</summary>
    public const string PlateRecognized = nameof(PlateRecognized);

    /// <summary>Cambió el estado de un panel de alarma: áreas, zonas o conexión (payload: AlarmPanelDto).</summary>
    public const string AlarmPanelStateChanged = nameof(AlarmPanelStateChanged);

    /// <summary>Llegó un evento de un panel de alarma (payload: AlarmEventDto).</summary>
    public const string AlarmEventReceived = nameof(AlarmEventReceived);

    /// <summary>Terminó la ejecución de una automatización (payload: WorkflowRunDto).</summary>
    public const string WorkflowRunCompleted = nameof(WorkflowRunCompleted);

    /// <summary>Una automatización avisa a los operadores (payload: WorkflowNotificationDto).</summary>
    public const string WorkflowNotification = nameof(WorkflowNotification);

    /// <summary>Alguien se dio por enterado de una alerta; el resto puede bajarla (payload: WorkflowAlertDto).</summary>
    public const string WorkflowAlertAcknowledged = nameof(WorkflowAlertAcknowledged);

    /// <summary>Un parlante IP cambió de estado de conexión (payload: SpeakerDto).</summary>
    public const string SpeakerStatusChanged = nameof(SpeakerStatusChanged);

    /// <summary>Un equipo de control de acceso cambió de estado de conexión (payload: AccessDeviceDto).</summary>
    public const string AccessDeviceStatusChanged = nameof(AccessDeviceStatusChanged);

    /// <summary>Una puerta cambió de modo o de estado de hoja (payload: AccessDoorStateDto).</summary>
    public const string AccessDoorStateChanged = nameof(AccessDoorStateChanged);

    /// <summary>Alguien pasó (o lo rechazaron) por una puerta (payload: AccessEventDto).</summary>
    public const string AccessEventReceived = nameof(AccessEventReceived);

    /// <summary>Cambió lo escrito en los equipos para una persona (payload: AccessPersonDto).</summary>
    public const string AccessPersonSyncChanged = nameof(AccessPersonSyncChanged);

    /// <summary>Avanzó la pasada de escritura en los equipos (payload: AccessSyncProgressDto).</summary>
    public const string AccessSyncProgress = nameof(AccessSyncProgress);

    /// <summary>Un frente de citofonía cambió de estado de conexión (payload: IntercomDto).</summary>
    public const string IntercomStatusChanged = nameof(IntercomStatusChanged);

    /// <summary>Una llamada de citofonía empezó a sonar, fue contestada o terminó (payload: IntercomCallDto).</summary>
    public const string IntercomCallChanged = nameof(IntercomCallChanged);

    /// <summary>Un servicio supervisado cambió de estado (payload: ManagedServiceDto).</summary>
    public const string ServiceStateChanged = nameof(ServiceStateChanged);

    /// <summary>Cambió el estado de un panel de cerco: armado, sirena, voltaje o conexión (payload: CercoPanelDto).</summary>
    public const string CercoPanelStateChanged = nameof(CercoPanelStateChanged);

    /// <summary>Cambió la lista de controles RF de un panel de cerco (payload: { panelId, remotes: CercoRemoteDto[] }).</summary>
    public const string CercoRemotesChanged = nameof(CercoRemotesChanged);

    /// <summary>Llegó un evento de un panel de cerco (payload: CercoEventDto).</summary>
    public const string CercoEventReceived = nameof(CercoEventReceived);

    /// <summary>Avance de una actualización de firmware (OTA) de un panel de cerco (payload: { panelId, state, pct, err }).</summary>
    public const string CercoOtaProgress = nameof(CercoOtaProgress);
}
