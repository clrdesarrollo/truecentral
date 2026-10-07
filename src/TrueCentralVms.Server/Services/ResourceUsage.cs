using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Services.Workflows;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Qué automatizaciones usan un recurso (pestaña Automatizaciones de la ficha):
/// las que lo nombran en su disparador, en un nodo de condición o en una
/// acción. Aparte se cuentan las "generales" (sin equipo marcado), que también
/// se disparan con este recurso aunque no lo nombren.
/// </summary>
public static class ResourceUsage
{
    /// <summary>Lo que identifica al recurso en las condiciones y acciones de las automatizaciones.</summary>
    private sealed record Target(
        ResourceKind Kind, int Id,
        int OwnerId,          // equipo de video, equipo de acceso o panel (0 si el recurso es el equipo)
        int Number,           // canal, puerta, área o zona dentro del equipo
        int? AreaNumber,      // zonas: su área
        string? GroupName);   // parlantes: su grupo

    private static async Task<Target?> TargetAsync(VmsDbContext db, ResourceKind kind, int id, CancellationToken ct) => kind switch
    {
        ResourceKind.Camera => await db.Channels.Where(c => c.Id == id)
            .Select(c => new Target(kind, id, c.DeviceId, c.ChannelNumber, null, null)).FirstOrDefaultAsync(ct),
        ResourceKind.Door => await db.AccessDoors.Where(d => d.Id == id)
            .Select(d => new Target(kind, id, d.AccessDeviceId, d.Number, null, null)).FirstOrDefaultAsync(ct),
        ResourceKind.Partition => await db.AlarmAreas.Where(a => a.Id == id)
            .Select(a => new Target(kind, id, a.AlarmPanelId, a.Number, null, null)).FirstOrDefaultAsync(ct),
        ResourceKind.Zone => await db.AlarmZones.Where(z => z.Id == id)
            .Select(z => new Target(kind, id, z.AlarmPanelId, z.Number, z.AreaNumber, null)).FirstOrDefaultAsync(ct),
        ResourceKind.Fence => await db.CercoPanels.Where(p => p.Id == id)
            .Select(p => new Target(kind, id, 0, 0, null, null)).FirstOrDefaultAsync(ct),
        ResourceKind.Speaker => await db.Speakers.Where(s => s.Id == id)
            .Select(s => new Target(kind, id, 0, 0, null, s.GroupName)).FirstOrDefaultAsync(ct),
        ResourceKind.Intercom => await db.Intercoms.Where(i => i.Id == id)
            .Select(i => new Target(kind, id, 0, 0, null, null)).FirstOrDefaultAsync(ct),
        _ => null,
    };

    /// <summary>Usos del recurso, o null si el recurso ya no existe.</summary>
    public static async Task<ResourceWorkflowsDto?> FindAsync(VmsDbContext db, ResourceKind kind, int id, CancellationToken ct)
    {
        var target = await TargetAsync(db, kind, id, ct);
        if (target is null) return null;

        var workflows = await db.Workflows.AsNoTracking().Include(w => w.Actions)
            .AsSplitQuery().OrderBy(w => w.Name).ToListAsync(ct);
        var uses = new List<ResourceWorkflowUseDto>();
        int general = 0;
        foreach (var workflow in workflows)
        {
            var conditions = WorkflowJson.Conditions(workflow.ConditionsJson);
            var trigger = TriggerUse(workflow.TriggerType, conditions, target);
            if (trigger.Detail is { } detail)
                uses.Add(new(workflow.Id, workflow.Name, workflow.Enabled, "trigger", detail));
            else if (trigger.General)
                general++;

            var graph = WorkflowJson.TryDeserialize<WorkflowGraphDto>(workflow.GraphJson);
            foreach (var node in graph?.Nodes ?? [])
            {
                if (node.Kind != WorkflowNodeKinds.Condition || node.Conditions is null) continue;
                if (ConditionUse(node.Conditions, target) is { } detailCondition)
                    uses.Add(new(workflow.Id, workflow.Name, workflow.Enabled, "condition", detailCondition));
            }

            foreach (var action in workflow.Actions.OrderBy(a => a.Order))
            {
                if (ActionUse(action.Type, WorkflowJson.ParseConfig(action.ConfigJson), target) is { } detailAction)
                    uses.Add(new(workflow.Id, workflow.Name, workflow.Enabled && action.Enabled, "action",
                        action.Enabled ? detailAction : $"{detailAction} (acción desactivada)"));
            }
        }

        string? note = general == 0 ? null : kind switch
        {
            ResourceKind.Camera => "se disparan con eventos de video, patentes o conexión de cualquier cámara",
            ResourceKind.Door => "se disparan con eventos de acceso o conexión de cualquier equipo de acceso",
            ResourceKind.Partition or ResourceKind.Zone => "se disparan con eventos o conexión de cualquier panel de alarma",
            ResourceKind.Speaker => "se disparan con la conexión de cualquier parlante",
            _ => null,
        };
        return new ResourceWorkflowsDto(uses, note is null ? 0 : general, note);
    }

    private static bool Has(IReadOnlyList<int>? list, int value) => list is { Count: > 0 } && list.Contains(value);
    private static bool Has(IReadOnlyList<string>? list, string value) => list is { Count: > 0 } && list.Contains(value);
    private static bool None<T>(IReadOnlyList<T>? list) => list is null || list.Count == 0;

    /// <summary>Uso por el disparador: el detalle si lo nombra; General si no nombra a nadie pero lo alcanza igual.</summary>
    private static (string? Detail, bool General) TriggerUse(string triggerType, WorkflowConditionsDto c, Target t)
    {
        switch (t.Kind)
        {
            case ResourceKind.Camera:
                if (triggerType is WorkflowTriggerTypes.VideoEvent or WorkflowTriggerTypes.PlateRecognized)
                {
                    string what = triggerType == WorkflowTriggerTypes.VideoEvent ? "Eventos de video" : "Patentes leídas";
                    if (Has(c.ChannelIds, t.Id)) return ($"{what} de esta cámara", false);
                    if (Has(c.DeviceIds, t.OwnerId) && None(c.ChannelIds)) return ($"{what} de su equipo", false);
                    return (null, None(c.DeviceIds) && None(c.ChannelIds));
                }
                if (triggerType == WorkflowTriggerTypes.DeviceStatus)
                {
                    if (Has(c.DeviceIds, t.OwnerId)) return ("Conexión de su equipo", false);
                    return (null, AnyDevice(c, "video"));
                }
                break;

            case ResourceKind.Door:
                if (triggerType == WorkflowTriggerTypes.AccessEvent)
                {
                    if (Has(c.AccessDeviceIds, t.OwnerId))
                    {
                        if (Has(c.DoorNumbers, t.Number)) return ("Eventos de acceso de esta puerta", false);
                        if (None(c.DoorNumbers)) return ("Eventos de acceso de su equipo", false);
                        return (null, false);
                    }
                    return (null, None(c.AccessDeviceIds) && (None(c.DoorNumbers) || Has(c.DoorNumbers, t.Number)));
                }
                if (triggerType == WorkflowTriggerTypes.DeviceStatus)
                {
                    if (Has(c.AccessDeviceIds, t.OwnerId)) return ("Conexión de su equipo", false);
                    return (null, AnyDevice(c, "access"));
                }
                break;

            case ResourceKind.Partition:
            case ResourceKind.Zone:
                if (triggerType == WorkflowTriggerTypes.AlarmEvent)
                {
                    if (AlarmMatch(c, t) is { } detail) return (detail, false);
                    return (null, None(c.PanelIds) && NoAlarmFilter(c));
                }
                if (triggerType == WorkflowTriggerTypes.PanelStatus)
                {
                    if (Has(c.PanelIds, t.OwnerId)) return ("Conexión de su panel", false);
                    return (null, None(c.PanelIds));
                }
                break;

            case ResourceKind.Speaker:
                if (triggerType == WorkflowTriggerTypes.DeviceStatus)
                {
                    if (Has(c.SpeakerIds, t.Id)) return ("Conexión del parlante", false);
                    return (null, AnyDevice(c, "speaker"));
                }
                break;
        }
        return (null, false);
    }

    /// <summary>Disparador de conexión sin equipos marcados que incluye esta clase de equipo.</summary>
    private static bool AnyDevice(WorkflowConditionsDto c, string deviceKind) =>
        None(c.DeviceIds) && None(c.AccessDeviceIds) && None(c.SpeakerIds) &&
        (None(c.DeviceKinds) || Has(c.DeviceKinds, deviceKind));

    private static bool NoAlarmFilter(WorkflowConditionsDto c) =>
        None(c.AreaKeys) && None(c.ZoneKeys) && None(c.AreaNumbers) && None(c.ZoneNumbers);

    /// <summary>Un filtro de eventos de alarma (disparador o condición) que nombra al área o zona, o a su panel entero.</summary>
    private static string? AlarmMatch(WorkflowConditionsDto c, Target t)
    {
        bool panelOk = None(c.PanelIds) || Has(c.PanelIds, t.OwnerId);
        if (t.Kind == ResourceKind.Zone)
        {
            if (Has(c.ZoneKeys, $"{t.OwnerId}:{t.Number}")) return "Eventos de esta zona";
            if (None(c.ZoneKeys) && Has(c.ZoneNumbers, t.Number) && panelOk) return "Eventos de esta zona";
            if (t.AreaNumber is { } area && (Has(c.AreaKeys, $"{t.OwnerId}:{area}") ||
                                             (None(c.AreaKeys) && Has(c.AreaNumbers, area) && panelOk)))
                return "Eventos de su área";
        }
        else
        {
            if (Has(c.AreaKeys, $"{t.OwnerId}:{t.Number}")) return "Eventos de esta área";
            if (None(c.AreaKeys) && Has(c.AreaNumbers, t.Number) && panelOk) return "Eventos de esta área";
        }
        if (Has(c.PanelIds, t.OwnerId) && NoAlarmFilter(c)) return "Eventos de su panel (todas sus áreas y zonas)";
        return null;
    }

    /// <summary>Un nodo de condición del diagrama que consulta al recurso.</summary>
    private static string? ConditionUse(WorkflowConditionsDto c, Target t) => t.Kind switch
    {
        ResourceKind.Camera when Has(c.ChannelIds, t.Id) => "Pregunta por esta cámara",
        ResourceKind.Camera when Has(c.DeviceIds, t.OwnerId) => "Pregunta por su equipo",
        ResourceKind.Door when Has(c.AccessDeviceIds, t.OwnerId) && (None(c.DoorNumbers) || Has(c.DoorNumbers, t.Number))
            => Has(c.DoorNumbers, t.Number) ? "Pregunta por esta puerta" : "Pregunta por su equipo",
        ResourceKind.Partition or ResourceKind.Zone => AlarmMatch(c, t) is { } detail
            ? detail.Replace("Eventos de", "Pregunta por") : null,
        ResourceKind.Speaker when Has(c.SpeakerIds, t.Id) => "Pregunta por el parlante",
        _ => null,
    };

    private static IEnumerable<int> Ints(JsonElement config, string name)
    {
        if (!config.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int value)) yield return value;
    }

    private static int? Int(JsonElement config, string name) =>
        config.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n)
            ? n : null;

    private static string? Text(JsonElement config, string name) =>
        config.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Una acción que actúa sobre el recurso.</summary>
    private static string? ActionUse(string type, JsonElement config, Target t)
    {
        switch (t.Kind)
        {
            case ResourceKind.Camera:
                if (type == WorkflowActionTypes.Snapshot && Ints(config, "channelIds").Contains(t.Id)) return "Toma una captura de la cámara";
                if (type == WorkflowActionTypes.Notify && Ints(config, "channelIds").Contains(t.Id)) return "Muestra la cámara en el aviso al operador";
                if (type == WorkflowActionTypes.PtzPreset && Int(config, "channelId") == t.Id)
                    return Int(config, "preset") is { } preset ? $"Lleva la cámara al preset {preset}" : "Mueve la cámara a un preset";
                break;

            case ResourceKind.Door:
                if (type == WorkflowActionTypes.Door && Ints(config, "doorIds").Contains(t.Id))
                    return (Text(config, "command") ?? "") switch
                    {
                        "Open" => "Abre la puerta",
                        "Close" => "Cierra la puerta",
                        "RemainOpen" => "Deja la puerta siempre abierta",
                        "RemainLocked" => "Bloquea la puerta",
                        _ => "Da una orden a la puerta",
                    };
                break;

            case ResourceKind.Partition:
                if (type == WorkflowActionTypes.Panel && Int(config, "panelId") == t.OwnerId)
                {
                    int area = Math.Max(Int(config, "areaNumber") ?? 0, 0);
                    if (area != 0 && area != t.Number) break;
                    string which = area == 0 ? "todas las áreas del panel" : "el área";
                    return (Text(config, "command") ?? "") switch
                    {
                        "Arm" => (Text(config, "mode") == "Stay" ? "Arma parcial " : "Arma total ") + which,
                        "Disarm" => "Desarma " + which,
                        "ClearAlarm" => "Borra la alarma de " + which,
                        _ => "Da una orden a " + which,
                    };
                }
                break;

            case ResourceKind.Speaker:
                if (type == WorkflowActionTypes.Speaker)
                {
                    bool stop = string.Equals(Text(config, "command"), "stop", StringComparison.OrdinalIgnoreCase);
                    if (Ints(config, "speakerIds").Contains(t.Id))
                        return stop ? "Detiene el parlante" : "Hace sonar el parlante";
                    if (t.GroupName is { Length: > 0 } group &&
                        string.Equals(Text(config, "group")?.Trim(), group, StringComparison.OrdinalIgnoreCase))
                        return stop ? $"Detiene el grupo «{group}»" : $"Hace sonar el grupo «{group}»";
                }
                break;
        }
        return null;
    }
}
