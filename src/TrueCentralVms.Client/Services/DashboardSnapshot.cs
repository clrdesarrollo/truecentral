using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.Services;

// ---------------------------------------------------------------------------
// Lo que el cliente lee de /api/dashboard (el panel de inicio del web). El
// servidor lo arma como objeto anónimo: aquí van solo los campos que usan las
// tarjetas del Centro de mando. Cada bloque viene en null si a la sesión le
// falta el permiso de ese módulo.
// ---------------------------------------------------------------------------

public sealed class DashboardSnapshot
{
    public DateTime GeneratedAt { get; set; }
    public DashboardServer? Server { get; set; }
    public DashboardEquipment? Equipment { get; set; }
    public DashboardOffline? Offline { get; set; }
}

public sealed class DashboardServer
{
    public string? Version { get; set; }
    /// <summary>Desde que arrancó el servicio de TrueCentral (el último reinicio del servidor).</summary>
    public DateTime? StartedAt { get; set; }
    public double UptimeSeconds { get; set; }
    /// <summary>Desde que se encendió el equipo (Windows); null = servidor anterior.</summary>
    public double? OsUptimeSeconds { get; set; }
    /// <summary>Ingresos rechazados de las últimas 24 h; null sin permiso de bitácora o servidor anterior.</summary>
    public DashboardLogins? Logins { get; set; }
    public double? Cpu { get; set; }
    public double? Ram { get; set; }
    public double? RamUsedGb { get; set; }
    public double? RamTotalGb { get; set; }
    public double? Disk { get; set; }
    public double? DiskUsedGb { get; set; }
    public double? DiskTotalGb { get; set; }
    public string? DiskName { get; set; }
    public NetworkThroughputDto? Network { get; set; }
    public int ServicesTotal { get; set; }
    public int ServicesRunning { get; set; }
    public List<string> ServicesFailed { get; set; } = [];
    public List<string> ServicesWarning { get; set; } = [];
    /// <summary>Puestos de cliente de escritorio conectados ahora (null = servidor anterior que no lo informa).</summary>
    public int? ClientsDesktop { get; set; }
    /// <summary>Sesiones del panel web con el canal en vivo abierto.</summary>
    public int? ClientsWeb { get; set; }
    /// <summary>Puestos de escritorio que permite la licencia.</summary>
    public int? ClientsLimit { get; set; }
}

/// <summary>
/// Ingresos rechazados de las últimas 24 h: los fallos sueltos (una clave mal
/// escrita) y los ataques (intentos repetidos que bloquearon una cuenta o una IP).
/// </summary>
public sealed class DashboardLogins
{
    public int Failed { get; set; }
    public int FailedLastHour { get; set; }
    /// <summary>Veces que una cuenta o una IP quedó bloqueada por intentos repetidos.</summary>
    public int Locks { get; set; }
    /// <summary>Intentos que llegaron mientras estaba bloqueada.</summary>
    public int RejectedWhileLocked { get; set; }
    /// <summary>IP distintas de esos ataques.</summary>
    public int AttackIps { get; set; }
    public DateTime? LastAttackAt { get; set; }
    public int LockedAccountsNow { get; set; }
    public int LockedIpsNow { get; set; }
}

public sealed class DashboardCount
{
    public int Total { get; set; }
    public int Online { get; set; }
}

public sealed class DashboardEquipment
{
    public DashboardCount? Video { get; set; }
    public DashboardCount? Cameras { get; set; }
    public DashboardCount? Access { get; set; }
    public DashboardCount? AlarmPanels { get; set; }
    public DashboardCount? Fence { get; set; }
    public DashboardCount? Intercoms { get; set; }
    public DashboardCount? Speakers { get; set; }
    public DashboardCount? IoModules { get; set; }
}

public sealed class DashboardOffline
{
    public int Total { get; set; }
    public List<DashboardOfflineItem> Items { get; set; } = [];
}

public sealed class DashboardOfflineItem
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Detail { get; set; }
    public DateTime? LastSeenAt { get; set; }
}
