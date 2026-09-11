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

public sealed record UserDto(int Id, string Username, string Role, bool Enabled, DateTime CreatedAt, DateTime PasswordChangedAt);

/// <summary>Alta/edición de usuario. En edición, Password null o vacía = no cambiar.</summary>
public sealed record UserWriteDto(string Username, string? Password, string Role, bool Enabled);

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

    /// <summary>Un servicio supervisado cambió de estado (payload: ManagedServiceDto).</summary>
    public const string ServiceStateChanged = nameof(ServiceStateChanged);
}
