using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Historial de un recurso (pestaña Historial de la ficha): lo que informó su
/// módulo —accesos de una puerta, eventos de un área o zona, del cerco,
/// llamadas del citófono, patentes de una cámara—, los avisos de
/// automatizaciones que originó (con quién los atendió) y lo que quedó en la
/// bitácora sobre él (cambios, órdenes, quién lo vio), mezclado por fecha.
/// </summary>
public static class ResourceHistory
{
    private static readonly Dictionary<(string Category, string Action), string> AuditLabels =
        AuditCatalog.Categories
            .SelectMany(c => c.Actions.Select(a => (Key: (c.Key, a.Key), a.Label)))
            .ToDictionary(x => x.Key, x => x.Label);

    private static string Level(AlarmSeverity severity) => severity switch
    {
        AlarmSeverity.Critical => "alarm",
        AlarmSeverity.Warning => "warning",
        _ => "info",
    };

    private static string Level(CercoSeverity severity) => severity switch
    {
        CercoSeverity.Critical => "alarm",
        CercoSeverity.Warning => "warning",
        _ => "info",
    };

    private static string? CredentialText(AccessCredentialKind credential) => credential switch
    {
        AccessCredentialKind.Card => "tarjeta",
        AccessCredentialKind.Fingerprint => "huella",
        AccessCredentialKind.Face => "rostro",
        AccessCredentialKind.Pin => "clave",
        AccessCredentialKind.Remote => "apertura remota",
        AccessCredentialKind.ExitButton => "botón de salida",
        AccessCredentialKind.Qr => "código QR",
        AccessCredentialKind.Plate => "patente",
        _ => null,
    };

    private static string? Join(params string?[] parts)
    {
        var text = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return text.Length == 0 ? null : text;
    }

    /// <summary>Las entradas más recientes (a lo más <paramref name="take"/>), o null si el recurso ya no existe.</summary>
    public static async Task<List<ResourceHistoryItemDto>?> LoadAsync(
        VmsDbContext db, ResourceKind kind, int id, int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 10, 200);
        var items = new List<ResourceHistoryItemDto>();
        IQueryable<AuditEvent> audit = db.AuditEvents.AsNoTracking();
        string key = ResourceCatalog.Key(kind, id);

        switch (kind)
        {
            case ResourceKind.Camera:
            {
                var c = await db.Channels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                if (c is null) return null;
                // La bitácora de video nombra el canal por número interno o por el de RTSP.
                string byNumber = $"{c.DeviceId}/{c.ChannelNumber}", byRtsp = $"{c.DeviceId}/{c.RtspChannel}";
                audit = audit.Where(a => (a.TargetType == "channel" && (a.TargetId == byNumber || a.TargetId == byRtsp))
                                         || (a.TargetType == "resource" && a.TargetId == key));
                var plates = await db.PlateEvents.AsNoTracking()
                    .Where(p => p.DeviceId == c.DeviceId && p.ChannelNumber == c.ChannelNumber)
                    .OrderByDescending(p => p.ReceivedAt).Take(take)
                    .Select(p => new { p.ReceivedAt, p.PlateNumber, p.Confidence, p.VehicleType, p.VehicleColor })
                    .ToListAsync(ct);
                items.AddRange(plates.Select(p => new ResourceHistoryItemDto(p.ReceivedAt, "event",
                    $"Patente {p.PlateNumber}", Join($"{p.Confidence}% de confianza", p.VehicleType, p.VehicleColor), null, "info")));
                break;
            }
            case ResourceKind.Door:
            {
                var d = await db.AccessDoors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                if (d is null) return null;
                string doorId = id.ToString();
                audit = audit.Where(a => (a.TargetType == "access-door" && a.TargetId == doorId)
                                         || (a.TargetType == "resource" && a.TargetId == key));
                var events = await db.AccessEvents.AsNoTracking()
                    .Where(e => e.AccessDeviceId == d.AccessDeviceId && e.DoorNumber == d.Number)
                    .OrderByDescending(e => e.Timestamp).Take(take)
                    .Select(e => new { e.Timestamp, e.Kind, e.Credential, e.Description, e.PersonName, e.CardNumber })
                    .ToListAsync(ct);
                items.AddRange(events.Select(e => new ResourceHistoryItemDto(e.Timestamp, "event", e.Description,
                    Join(e.PersonName, CredentialText(e.Credential), e.PersonName is null ? e.CardNumber : null), null,
                    e.Kind switch { AccessEventKind.Denied => "warning", AccessEventKind.Alarm => "alarm", _ => "info" })));
                break;
            }
            case ResourceKind.Partition:
            case ResourceKind.Zone:
            {
                var (panelId, number) = kind == ResourceKind.Partition
                    ? await db.AlarmAreas.Where(a => a.Id == id).Select(a => new ValueTuple<int, int>(a.AlarmPanelId, a.Number)).FirstOrDefaultAsync(ct)
                    : await db.AlarmZones.Where(z => z.Id == id).Select(z => new ValueTuple<int, int>(z.AlarmPanelId, z.Number)).FirstOrDefaultAsync(ct);
                if (panelId == 0) return null;
                string targetType = kind == ResourceKind.Partition ? "alarm-area" : "alarm-zone";
                string targetId = $"{panelId}/{number}";
                audit = audit.Where(a => (a.TargetType == targetType && a.TargetId == targetId)
                                         || (a.TargetType == "resource" && a.TargetId == key));
                var events = db.AlarmEvents.AsNoTracking().Where(e => e.AlarmPanelId == panelId);
                events = kind == ResourceKind.Partition
                    ? events.Where(e => e.AreaNumber == number)
                    : events.Where(e => e.ZoneNumber == number);
                var rows = await events.OrderByDescending(e => e.Timestamp).Take(take)
                    .Select(e => new { e.Timestamp, e.Severity, e.Description, e.ZoneName, e.Operator, e.Code })
                    .ToListAsync(ct);
                items.AddRange(rows.Select(e => new ResourceHistoryItemDto(e.Timestamp, "event", e.Description,
                    Join(kind == ResourceKind.Partition ? e.ZoneName : null, e.Operator, e.Code is null ? null : $"código {e.Code}"),
                    null, Level(e.Severity))));
                break;
            }
            case ResourceKind.Fence:
            {
                if (!await db.CercoPanels.AnyAsync(p => p.Id == id, ct)) return null;
                string panelId = id.ToString();
                audit = audit.Where(a => (a.TargetType == "cerco-panel" && a.TargetId == panelId)
                                         || (a.TargetType == "resource" && a.TargetId == key));
                var rows = await db.CercoEvents.AsNoTracking().Where(e => e.CercoPanelId == id)
                    .OrderByDescending(e => e.Timestamp).Take(take)
                    .Select(e => new { e.Timestamp, e.Severity, e.Description, e.ZoneName })
                    .ToListAsync(ct);
                items.AddRange(rows.Select(e => new ResourceHistoryItemDto(e.Timestamp, "event", e.Description,
                    e.ZoneName, null, Level(e.Severity))));
                break;
            }
            case ResourceKind.Speaker:
            {
                if (!await db.Speakers.AnyAsync(s => s.Id == id, ct)) return null;
                string speakerId = id.ToString();
                audit = audit.Where(a => (a.TargetType == "speaker" && a.TargetId == speakerId)
                                         || (a.TargetType == "resource" && a.TargetId == key));
                break;
            }
            case ResourceKind.Intercom:
            {
                if (!await db.Intercoms.AnyAsync(i => i.Id == id, ct)) return null;
                string intercomId = id.ToString();
                audit = audit.Where(a => (a.TargetType == "intercom" && a.TargetId == intercomId)
                                         || (a.TargetType == "resource" && a.TargetId == key));
                var calls = await db.IntercomCalls.AsNoTracking().Where(c => c.IntercomId == id)
                    .OrderByDescending(c => c.StartedAt).Take(take)
                    .Select(c => new { c.StartedAt, c.AnsweredAt, c.EndedAt, c.State, c.AnsweredBy, c.DoorOpened, c.DoorOpenedBy })
                    .ToListAsync(ct);
                items.AddRange(calls.Select(c => new ResourceHistoryItemDto(c.StartedAt, "event",
                    c.State switch
                    {
                        IntercomCallState.Completed => "Llamada atendida",
                        IntercomCallState.Missed => "Llamada perdida",
                        IntercomCallState.Rejected => "Llamada rechazada",
                        IntercomCallState.InCall => "En conversación",
                        _ => "Llamando",
                    },
                    Join(c.AnsweredBy is null ? null : $"contestó {c.AnsweredBy}",
                         c.AnsweredAt is { } a && c.EndedAt is { } e ? $"{(int)(e - a).TotalSeconds} s" : null,
                         c.DoorOpened ? $"abrió la puerta{(c.DoorOpenedBy is null ? "" : $" ({c.DoorOpenedBy})")}" : null),
                    null, c.State == IntercomCallState.Missed ? "warning" : "info")));
                break;
            }
            default:
                return null;
        }

        // Avisos de automatizaciones originados por el recurso, con su acuse
        // de recibo: quién lo atendió y cuánto tardó.
        var alerts = await db.WorkflowAlerts.AsNoTracking().Where(a => a.ResourceKey == key)
            .OrderByDescending(a => a.RaisedAt).Take(take)
            .Select(a => new { a.RaisedAt, a.Title, a.WorkflowName, a.Severity, a.RequiresAck, a.AcknowledgedAt, a.AcknowledgedBy })
            .ToListAsync(ct);
        items.AddRange(alerts.Select(a => new ResourceHistoryItemDto(a.RaisedAt, "alert", a.Title,
            Join($"automatización '{a.WorkflowName}'",
                 a.AcknowledgedAt is { } ack
                     ? $"atendida por {a.AcknowledgedBy} a los {Math.Max(0, (int)Math.Round((ack - a.RaisedAt).TotalSeconds))} s"
                     : a.RequiresAck ? "sin acuse de recibo" : null),
            a.AcknowledgedBy, Level(a.Severity))));

        // Cambios de ubicación: el evento es de la ubicación, pero lleva la
        // lista de recursos que movió en sus datos.
        string pattern = $"%\"{key}\"%";
        var located = db.AuditEvents.AsNoTracking()
            .Where(a => a.Category == "locations" && a.DataJson != null && EF.Functions.ILike(a.DataJson, pattern));

        var auditRows = await audit.Union(located)
            .OrderByDescending(a => a.Timestamp).Take(take)
            .Select(a => new { a.Timestamp, a.Category, a.Action, a.Detail, a.Username, a.Success })
            .ToListAsync(ct);
        items.AddRange(auditRows.Select(a => new ResourceHistoryItemDto(a.Timestamp, "audit",
            AuditLabels.GetValueOrDefault((a.Category, a.Action), a.Action), a.Detail,
            string.IsNullOrEmpty(a.Username) ? null : a.Username, a.Success ? "info" : "warning")));

        return items.OrderByDescending(i => i.Timestamp).Take(take).ToList();
    }
}
