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
    public long UptimeSeconds { get; set; }
    public double? Cpu { get; set; }
    public double? Ram { get; set; }
    public double? RamUsedGb { get; set; }
    public double? RamTotalGb { get; set; }
    public double? Disk { get; set; }
    public double? DiskUsedGb { get; set; }
    public double? DiskTotalGb { get; set; }
    public string? DiskName { get; set; }
    public int ServicesTotal { get; set; }
    public int ServicesRunning { get; set; }
    public List<string> ServicesFailed { get; set; } = [];
    public List<string> ServicesWarning { get; set; } = [];
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
