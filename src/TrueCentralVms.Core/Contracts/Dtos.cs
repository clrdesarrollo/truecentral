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

    /// <summary>Cambió la configuración de una entidad; recargar (payload: string "devices" | "channels" | "users" | "decoders" | "walls").</summary>
    public const string ConfigChanged = nameof(ConfigChanged);

    /// <summary>Cambió el estado de un muro de video (payload: WallDto).</summary>
    public const string WallStateChanged = nameof(WallStateChanged);

    /// <summary>Cambió el conjunto de sesiones de streaming activas (payload: ActiveSessionDto[]).</summary>
    public const string SessionsChanged = nameof(SessionsChanged);
}
