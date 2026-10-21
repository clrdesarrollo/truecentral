using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Supervisor;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Panel de inicio: un resumen en una sola consulta del estado del servidor,
/// de los equipos y de la seguridad de las instalaciones (intrusión, cerco,
/// accesos, patentes y alertas) con la actividad de las últimas 24 horas.
///
/// Cada bloque respeta lo mismo que su página: el permiso del rol (sin él, el
/// bloque viene en null y la página no lo muestra) y el alcance por ubicación
/// (solo cuenta lo que el usuario ve). No se registra en la bitácora: es un
/// resumen de conteos que se refresca solo, no una consulta del historial.
/// </summary>
public static class DashboardApi
{
    private const int Hours = 24;
    private const int FeedSize = 12;
    private const int ListSize = 6;

    // Eventos del cerco que son una intrusión o un intento (no un cambio de estado).
    private static readonly CercoEventKind[] FenceIntrusionKinds =
        [CercoEventKind.Alarm, CercoEventKind.FenceCut, CercoEventKind.Panic, CercoEventKind.Tamper];

    public static void MapDashboardApi(this WebApplication app)
    {
        app.MapGet("/api/dashboard", async (HttpContext ctx, VmsDbContext db, SystemMetrics metrics,
            ServiceSupervisor supervisor, CercoConnectionManager cercoConns, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var now = DateTime.UtcNow;
            // Ventana alineada a la hora: 24 columnas completas, la última es la hora en curso.
            var hourStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
            var from = hourStart.AddHours(-(Hours - 1));
            var prevFrom = from.AddHours(-Hours);

            // ---------------- Servidor ----------------
            var overview = supervisor.Snapshot();
            var supervised = overview.Services.Where(s => s.State != ManagedServiceState.Disabled).ToList();
            var m = metrics.Read();
            var server = new
            {
                version = overview.Server.Version,
                startedAt = overview.Server.StartedAtUtc,
                uptimeSeconds = overview.Server.UptimeSeconds,
                cpu = m.CpuPercent, ram = m.RamPercent, ramUsedGb = m.RamUsedGb, ramTotalGb = m.RamTotalGb,
                disk = m.DiskPercent, diskUsedGb = m.DiskUsedGb, diskTotalGb = m.DiskTotalGb, diskName = m.DiskName,
                servicesTotal = supervised.Count,
                servicesRunning = supervised.Count(s => s.State == ManagedServiceState.Running),
                servicesFailed = supervised.Where(s => s.State == ManagedServiceState.Failed).Select(s => s.Name).ToList(),
            };

            // ---------------- Equipos ----------------
            var videoRows = await db.Devices.AsNoTracking()
                .Select(d => new { d.Id, d.Name, d.Status, d.LastSeenAt,
                    Channels = d.Channels.Where(c => c.Enabled).Select(c => new { c.Id, c.Name, c.IsOnline }).ToList() })
                .ToListAsync(ct);
            videoRows = videoRows.Where(d => scope.CanViewDevice(d.Id)).ToList();
            // Un canal queda marcado en línea aunque su grabador se caiga: sin el equipo, la cámara no transmite.
            var cameras = videoRows.SelectMany(d => d.Channels.Select(c => new
                    { c.Id, c.Name, IsOnline = c.IsOnline && d.Status == DeviceStatus.Online, Device = d }))
                .Where(c => scope.CanViewChannel(c.Id)).ToList();

            var accessRows = (await db.AccessDevices.AsNoTracking().Select(d => new { d.Id, d.Name, d.Status, d.LastSeenAt }).ToListAsync(ct))
                .Where(d => scope.CanViewAccessDevice(d.Id)).ToList();
            var intercomRows = (await db.Intercoms.AsNoTracking().Select(d => new { d.Id, d.Name, d.Status, d.LastSeenAt }).ToListAsync(ct))
                .Where(d => scope.CanViewIntercom(d.Id)).ToList();
            var speakerRows = (await db.Speakers.AsNoTracking().Where(s => s.Enabled).Select(d => new { d.Id, d.Name, d.Status, d.LastSeenAt }).ToListAsync(ct))
                .Where(d => scope.CanViewSpeaker(d.Id)).ToList();
            var ioRows = (await db.IoModules.AsNoTracking().Where(s => s.Enabled).Select(d => new { d.Id, d.Name, d.Status, d.LastSeenAt }).ToListAsync(ct))
                .Where(d => scope.CanViewIoModule(d.Id)).ToList();

            // ---------------- Intrusión (paneles de alarma) ----------------
            var panels = await db.AlarmPanels.AsNoTracking().Where(p => p.Enabled && !p.DisabledByLicense)
                .Include(p => p.Areas).Include(p => p.Zones).AsSplitQuery()
                .ToListAsync(ct);
            panels = panels.Where(p => scope.CanViewPanel(p.Id)).ToList();
            var areas = panels.SelectMany(p => p.Areas.Where(a => a.Enabled && scope.CanViewArea(p.Id, a.Number))).ToList();
            var zones = panels.SelectMany(p => p.Zones.Where(z => scope.CanViewZone(p.Id, z.Number))).ToList();
            var intrusion = new
            {
                panels = panels.Count,
                panelsOnline = panels.Count(p => p.Status == AlarmPanelStatus.Online),
                areas = areas.Count,
                armed = areas.Count(a => a.ArmState is AlarmArmState.Away or AlarmArmState.Stay or AlarmArmState.Vacation),
                armedAway = areas.Count(a => a.ArmState is AlarmArmState.Away or AlarmArmState.Vacation),
                disarmed = areas.Count(a => a.ArmState == AlarmArmState.Disarmed),
                inAlarm = areas.Count(a => a.InAlarm),
                zones = zones.Count,
                zonesAlarm = zones.Count(z => z.InAlarm),
                zonesOpen = zones.Count(z => z.Status == AlarmZoneStatus.Triggered),
                zonesBypassed = zones.Count(z => z.Bypassed),
                zonesTrouble = zones.Count(z => z.Status is AlarmZoneStatus.Fault or AlarmZoneStatus.Offline || z.Tamper || z.LowBattery),
                tamper = panels.Count(p => p.PanelTamper),
                acLoss = panels.Count(p => p.AcLoss),
            };

            // ---------------- Cerco eléctrico ----------------
            var fences = (await db.CercoPanels.AsNoTracking().Where(p => p.Enabled && !p.DisabledByLicense)
                    .Include(p => p.Zones).ToListAsync(ct))
                .Where(p => scope.CanViewCerco(p.Id)).ToList();
            bool FenceOnline(Data.Entities.CercoPanel p) => cercoConns.IsConnected(p.DeviceId);
            var fence = new
            {
                panels = fences.Count,
                online = fences.Count(FenceOnline),
                armed = fences.Count(p => p.Armed),
                inAlarm = fences.Count(p => p.Siren || p.Zones.Any(z => z.Enabled && z.InAlarm)),
                faults = fences.Count(p => FenceOnline(p) && (!p.HvOk || !p.FenceOk || p.ArcFault)),
                onBattery = fences.Count(p => p.PowerSource == CercoPowerSource.Battery),
            };

            // ---------------- Puertas ----------------
            var doorRows = (await db.AccessDoors.AsNoTracking().Where(d => d.Enabled)
                    .Select(d => new { d.Id, d.IsOpen, d.Mode }).ToListAsync(ct))
                .Where(d => scope.CanViewDoor(d.Id)).ToList();
            var doors = new
            {
                total = doorRows.Count,
                open = doorRows.Count(d => d.IsOpen == true),
                remainOpen = doorRows.Count(d => d.Mode == AccessDoorMode.RemainOpen),
                remainLocked = doorRows.Count(d => d.Mode == AccessDoorMode.RemainLocked),
            };

            // ---------------- Equipos sin conexión (lo primero que hay que revisar) ----------------
            // Las cámaras de un grabador caído no se repiten: va el grabador, no cada canal.
            var offline = new List<DashboardOfflineItem>();
            offline.AddRange(videoRows.Where(d => d.Status != DeviceStatus.Online)
                .Select(d => new DashboardOfflineItem(d.Name, "video",
                    d.Status == DeviceStatus.AuthFailed ? "Clave rechazada" : "Equipo de video", d.LastSeenAt, "#/devices")));
            offline.AddRange(cameras.Where(c => !c.IsOnline && c.Device.Status == DeviceStatus.Online)
                .Select(c => new DashboardOfflineItem(c.Name, "camera", $"Cámara de {c.Device.Name}", null, "#/devices")));
            offline.AddRange(accessRows.Where(d => d.Status != AccessDeviceStatus.Online)
                .Select(d => new DashboardOfflineItem(d.Name, "access",
                    d.Status == AccessDeviceStatus.AuthFailed ? "Clave rechazada" : "Control de acceso", d.LastSeenAt, "#/access")));
            offline.AddRange(panels.Where(p => p.Status != AlarmPanelStatus.Online)
                .Select(p => new DashboardOfflineItem(p.Name, "alarm",
                    p.Status == AlarmPanelStatus.AuthFailed ? "Clave rechazada" : "Panel de alarma", p.LastSeenAt, "#/alarm-panels")));
            offline.AddRange(fences.Where(p => !FenceOnline(p))
                .Select(p => new DashboardOfflineItem(p.Name, "fence", "Panel de cerco", p.LastSeenAt, "#/cerco-panels")));
            offline.AddRange(intercomRows.Where(d => d.Status != IntercomStatus.Online)
                .Select(d => new DashboardOfflineItem(d.Name, "intercom", "Citófono", d.LastSeenAt, "#/intercoms")));
            offline.AddRange(speakerRows.Where(d => d.Status != SpeakerStatus.Online)
                .Select(d => new DashboardOfflineItem(d.Name, "speaker", "Parlante IP", d.LastSeenAt, "#/speakers")));
            offline.AddRange(ioRows.Where(d => d.Status != IoModuleStatus.Online)
                .Select(d => new DashboardOfflineItem(d.Name, "io", "Módulo de E/S", d.LastSeenAt, "#/io-modules")));
            // Lo que se cayó hace menos va primero (lo reciente suele ser lo urgente); lo nunca visto, al final.
            var offlineList = offline.OrderByDescending(o => o.LastSeenAt ?? DateTime.MinValue).ThenBy(o => o.Name).ToList();

            // ---------------- Actividad 24 h (por hora) ----------------
            int[] Bucket(IEnumerable<(DateTime Hour, int Count)> rows)
            {
                var series = new int[Hours];
                foreach (var (hour, count) in rows)
                {
                    int index = (int)Math.Floor((DateTime.SpecifyKind(hour, DateTimeKind.Utc) - from).TotalHours);
                    if (index is >= 0 and < Hours) series[index] += count;
                }
                return series;
            }
            async Task<int[]> HourlyAsync<T>(IQueryable<T> query, System.Linq.Expressions.Expression<Func<T, DateTime>> at)
            {
                var rows = await query.Select(at).Where(t => t >= from)
                    .GroupBy(t => new { t.Date, t.Hour })
                    .Select(g => new { g.Key.Date, g.Key.Hour, Count = g.Count() })
                    .ToListAsync(ct);
                return Bucket(rows.Select(r => (r.Date.AddHours(r.Hour), r.Count)));
            }
            async Task<int> PreviousAsync<T>(IQueryable<T> query, System.Linq.Expressions.Expression<Func<T, DateTime>> at) =>
                await query.Select(at).CountAsync(t => t >= prevFrom && t < from, ct);

            var feed = new List<DashboardFeedItem>();
            // Puntos con más eventos en las últimas 24 h (puertas, zonas, cercos, cámaras de patentes).
            var hotspots = new List<DashboardHotspot>();
            object? activity = null;
            var series = new Dictionary<string, int[]>();
            var previous = new Dictionary<string, int>();

            if (scope.Has(Permissions.AlarmsMonitor))
            {
                var q = db.AlarmEvents.AsNoTracking();
                if (scope.FiltersView)
                {
                    var (areaKeys, zoneKeys, panelIds) = scope.VisibleAlarmKeys();
                    q = q.Where(e =>
                        (e.ZoneNumber != null && zoneKeys.Contains((long)e.AlarmPanelId * 100_000 + e.ZoneNumber.Value)) ||
                        (e.ZoneNumber == null && e.AreaNumber != null && areaKeys.Contains((long)e.AlarmPanelId * 100_000 + e.AreaNumber.Value)) ||
                        (e.ZoneNumber == null && e.AreaNumber == null && panelIds.Contains(e.AlarmPanelId)));
                }
                var alarms = q.Where(e => e.Kind == AlarmEventKind.Alarm);
                series["alarm"] = await HourlyAsync(alarms, e => e.ReceivedAt);
                previous["alarm"] = await PreviousAsync(alarms, e => e.ReceivedAt);
                hotspots.AddRange((await q.Where(e => e.ReceivedAt >= from && e.Kind != AlarmEventKind.Info && e.Kind != AlarmEventKind.System)
                        .GroupBy(e => new { e.PanelName, e.AreaName, e.ZoneName })
                        .Select(g => new { g.Key.PanelName, g.Key.AreaName, g.Key.ZoneName, Count = g.Count() })
                        .OrderByDescending(g => g.Count).Take(ListSize).ToListAsync(ct))
                    .Select(g => new DashboardHotspot("alarm", g.ZoneName ?? g.AreaName ?? g.PanelName,
                        g.ZoneName is null && g.AreaName is null ? "Panel de alarma" : g.PanelName, g.Count, "#/alarms")));
                feed.AddRange((await q.Where(e => e.Severity != AlarmSeverity.Info)
                        .OrderByDescending(e => e.ReceivedAt).Take(FeedSize).ToListAsync(ct))
                    .Select(e => new DashboardFeedItem("alarm", e.ReceivedAt, e.Description,
                        Join(e.PanelName, e.AreaName, e.ZoneName), e.Severity.ToString(), "#/alarms")));
            }
            if (scope.Has(Permissions.CercoMonitor))
            {
                var q = db.CercoEvents.AsNoTracking();
                if (scope.FiltersView)
                {
                    var allowed = scope.AllowedIds(ResourceKind.Fence).ToList();
                    q = q.Where(e => allowed.Contains(e.CercoPanelId));
                }
                var intrusions = q.Where(e => FenceIntrusionKinds.Contains(e.Kind));
                series["fence"] = await HourlyAsync(intrusions, e => e.ReceivedAt);
                previous["fence"] = await PreviousAsync(intrusions, e => e.ReceivedAt);
                hotspots.AddRange((await q.Where(e => e.ReceivedAt >= from && e.Severity != CercoSeverity.Info)
                        .GroupBy(e => new { e.PanelName, e.ZoneName })
                        .Select(g => new { g.Key.PanelName, g.Key.ZoneName, Count = g.Count() })
                        .OrderByDescending(g => g.Count).Take(ListSize).ToListAsync(ct))
                    .Select(g => new DashboardHotspot("fence", g.ZoneName ?? g.PanelName,
                        g.ZoneName is null ? "Cerco eléctrico" : g.PanelName, g.Count, "#/cerco")));
                feed.AddRange((await q.Where(e => e.Severity != CercoSeverity.Info)
                        .OrderByDescending(e => e.ReceivedAt).Take(FeedSize).ToListAsync(ct))
                    .Select(e => new DashboardFeedItem("fence", e.ReceivedAt, e.Description,
                        Join(e.PanelName, e.ZoneName), e.Severity.ToString(), "#/cerco")));
            }
            if (scope.Has(Permissions.AccessMonitor))
            {
                var q = ScopeQueries.AccessEvents(db, db.AccessEvents.AsNoTracking(), scope);
                var granted = q.Where(e => e.Kind == AccessEventKind.Granted);
                var denied = q.Where(e => e.Kind == AccessEventKind.Denied);
                series["granted"] = await HourlyAsync(granted, e => e.ReceivedAt);
                series["denied"] = await HourlyAsync(denied, e => e.ReceivedAt);
                previous["granted"] = await PreviousAsync(granted, e => e.ReceivedAt);
                previous["denied"] = await PreviousAsync(denied, e => e.ReceivedAt);
                hotspots.AddRange((await q.Where(e => e.ReceivedAt >= from && e.Kind != AccessEventKind.Other)
                        .GroupBy(e => new { e.DeviceName, e.DoorName })
                        .Select(g => new
                        {
                            g.Key.DeviceName, g.Key.DoorName, Count = g.Count(),
                            Denied = g.Count(e => e.Kind == AccessEventKind.Denied || e.Kind == AccessEventKind.Alarm),
                        })
                        .OrderByDescending(g => g.Count).Take(ListSize).ToListAsync(ct))
                    .Select(g => new DashboardHotspot("access", g.DoorName ?? g.DeviceName,
                        g.DoorName is null ? "Control de acceso" : g.DeviceName, g.Count, "#/access-events", g.Denied)));
                feed.AddRange((await q.Where(e => e.Kind == AccessEventKind.Denied || e.Kind == AccessEventKind.Alarm)
                        .OrderByDescending(e => e.ReceivedAt).Take(FeedSize).ToListAsync(ct))
                    .Select(e => new DashboardFeedItem("access", e.ReceivedAt, e.Description,
                        Join(e.PersonName, e.DoorName ?? e.DeviceName), e.Kind == AccessEventKind.Alarm ? "Critical" : "Warning",
                        "#/access-events")));
            }
            if (scope.Has(Permissions.AnprView))
            {
                var q = db.PlateEvents.AsNoTracking();
                if (scope.FiltersView)
                {
                    var allowed = scope.AllowedIds(ResourceKind.Camera).ToList();
                    q = q.Where(p => db.Channels.Any(c => c.DeviceId == p.DeviceId && c.ChannelNumber == p.ChannelNumber
                                                          && allowed.Contains(c.Id)));
                }
                series["plate"] = await HourlyAsync(q, e => e.ReceivedAt);
                previous["plate"] = await PreviousAsync(q, e => e.ReceivedAt);
                hotspots.AddRange((await q.Where(e => e.ReceivedAt >= from)
                        .GroupBy(e => e.Device.Name)
                        .Select(g => new { Name = g.Key, Count = g.Count() })
                        .OrderByDescending(g => g.Count).Take(ListSize).ToListAsync(ct))
                    .Select(g => new DashboardHotspot("plate", g.Name, "Lectura de patentes", g.Count, "#/anpr")));
                feed.AddRange((await q.OrderByDescending(e => e.ReceivedAt).Take(4)
                        .Select(e => new { e.ReceivedAt, e.PlateNumber, Device = e.Device.Name }).ToListAsync(ct))
                    .Select(e => new DashboardFeedItem("plate", e.ReceivedAt, $"Patente {e.PlateNumber}", e.Device, "Info", "#/anpr")));
            }
            int? pendingAlerts = null;
            if (scope.Has(Permissions.EventsAttend))
            {
                var q = db.WorkflowAlerts.AsNoTracking();
                if (session.Role != Roles.Admin)
                {
                    string me = $",{session.UserId},";
                    if (!scope.FiltersView)
                        q = q.Where(a => a.RecipientUserIds == null || a.RecipientUserIds.Contains(me));
                    else
                    {
                        var keys = scope.VisibleResourceKeys();
                        q = q.Where(a => (a.RecipientUserIds != null && a.RecipientUserIds.Contains(me)) ||
                                         (a.RecipientUserIds == null && (a.ResourceKey == null || keys.Contains(a.ResourceKey))));
                    }
                }
                series["alert"] = await HourlyAsync(q, a => a.RaisedAt);
                previous["alert"] = await PreviousAsync(q, a => a.RaisedAt);
                pendingAlerts = await q.CountAsync(a => a.RequiresAck && a.AcknowledgedAt == null, ct);
                feed.AddRange((await q.OrderByDescending(a => a.RaisedAt).Take(FeedSize).ToListAsync(ct))
                    .Select(a => new DashboardFeedItem("alert", a.RaisedAt, a.Title,
                        a.LocationPath ?? a.Message, a.Severity.ToString(), "#/event-center")));
            }
            if (series.Count > 0) activity = new { from, hours = Hours, series, previous };

            return Results.Ok(new
            {
                generatedAt = now,
                server,
                equipment = new
                {
                    video = new { total = videoRows.Count, online = videoRows.Count(d => d.Status == DeviceStatus.Online) },
                    cameras = new { total = cameras.Count, online = cameras.Count(c => c.IsOnline) },
                    access = new { total = accessRows.Count, online = accessRows.Count(d => d.Status == AccessDeviceStatus.Online) },
                    alarmPanels = new { total = intrusion.panels, online = intrusion.panelsOnline },
                    fence = new { total = fence.panels, online = fence.online },
                    intercoms = new { total = intercomRows.Count, online = intercomRows.Count(d => d.Status == IntercomStatus.Online) },
                    speakers = new { total = speakerRows.Count, online = speakerRows.Count(d => d.Status == SpeakerStatus.Online) },
                    ioModules = new { total = ioRows.Count, online = ioRows.Count(d => d.Status == IoModuleStatus.Online) },
                },
                intrusion,
                fence,
                doors,
                pendingAlerts,
                activity,
                feed = feed.OrderByDescending(f => f.At).Take(FeedSize).ToList(),
                offline = new { total = offlineList.Count, items = offlineList.Take(ListSize).ToList() },
                hotspots = activity is null ? null : hotspots.OrderByDescending(h => h.Count).Take(ListSize).ToList(),
            });
        });
    }

    private static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private sealed record DashboardOfflineItem(string Name, string Kind, string Detail, DateTime? LastSeenAt, string Href);

    private sealed record DashboardHotspot(string Source, string Name, string Detail, int Count, string Href, int? Denied = null);

    private sealed record DashboardFeedItem(string Source, DateTime At, string Title, string Detail, string Severity, string Href);
}
