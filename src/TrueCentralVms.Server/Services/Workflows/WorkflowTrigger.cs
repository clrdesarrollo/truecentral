using System.Text;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services.Workflows;

/// <summary>
/// Lo que ocurrió y disparó (o puede disparar) una automatización. Es un
/// objeto plano y ya resuelto: el motor no vuelve a la base para evaluar
/// condiciones ni para reemplazar las marcas de los textos.
///
/// <see cref="Fields"/> son las marcas disponibles para las plantillas
/// ({panel}, {zona}, ...): las llena quien construye el disparo, de modo que
/// un disparador nuevo solo tiene que aportar sus propios campos.
/// </summary>
public sealed record WorkflowTrigger(
    string Type,
    string Summary,
    IReadOnlyDictionary<string, string> Fields,
    DateTime At,
    int? PanelId = null,
    AlarmEventKind? Kind = null,
    AlarmSeverity? Severity = null,
    int? AreaNumber = null,
    int? ZoneNumber = null,
    string? Code = null,
    string? Source = null,
    string? Text = null,
    AlarmPanelStatus? PanelStatus = null)
{
    /// <summary>Etiquetas en español de la naturaleza de un evento de panel.</summary>
    public static string KindLabel(AlarmEventKind kind) => kind switch
    {
        AlarmEventKind.Alarm => "Alarma",
        AlarmEventKind.Restore => "Restauración",
        AlarmEventKind.Arm => "Armado",
        AlarmEventKind.Disarm => "Desarmado",
        AlarmEventKind.Bypass => "Anulación",
        AlarmEventKind.Trouble => "Falla",
        AlarmEventKind.System => "Sistema",
        AlarmEventKind.ZoneTriggered => "Sensor interrumpido",
        _ => "Información",
    };

    public static string SeverityLabel(AlarmSeverity severity) => severity switch
    {
        AlarmSeverity.Critical => "Crítica",
        AlarmSeverity.Warning => "Advertencia",
        _ => "Informativa",
    };

    public static string StatusLabel(AlarmPanelStatus status) => status switch
    {
        AlarmPanelStatus.Online => "En línea",
        AlarmPanelStatus.Offline => "Sin conexión",
        AlarmPanelStatus.AuthFailed => "Credenciales rechazadas",
        _ => "Desconocido",
    };

    public static string SourceLabel(string? source) => source switch
    {
        "panel" => "informado por el panel",
        "poll" => "detectado por sondeo",
        "vms" => "orden dada desde el VMS",
        _ => source ?? "",
    };

    /// <summary>Disparo a partir de un evento de panel ya guardado en el historial.</summary>
    public static WorkflowTrigger FromAlarmEvent(AlarmEvent evt)
    {
        // Las horas se muestran SIEMPRE en hora local del servidor: los
        // correos y los reportes los lee gente, no máquinas.
        var local = DateTime.SpecifyKind(evt.ReceivedAt, DateTimeKind.Utc).ToLocalTime();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["panel"] = evt.PanelName,
            ["panelid"] = evt.AlarmPanelId.ToString(),
            ["evento"] = evt.Description,
            ["tipo"] = KindLabel(evt.Kind),
            ["severidad"] = SeverityLabel(evt.Severity),
            ["codigo"] = evt.Code ?? "",
            ["area"] = evt.AreaName ?? "",
            ["areanumero"] = evt.AreaNumber?.ToString() ?? "",
            ["zona"] = evt.ZoneName ?? "",
            ["zonanumero"] = evt.ZoneNumber?.ToString() ?? "",
            ["operador"] = evt.Operator ?? "",
            ["origen"] = SourceLabel(evt.Source),
            ["estado"] = "",
        };
        FillCommon(fields, local);

        var summary = new StringBuilder($"Panel '{evt.PanelName}' · {KindLabel(evt.Kind)}: {evt.Description}");
        if (evt.ZoneName is { Length: > 0 }) summary.Append($" (zona '{evt.ZoneName}')");
        else if (evt.AreaName is { Length: > 0 }) summary.Append($" (área '{evt.AreaName}')");

        return new WorkflowTrigger(WorkflowTriggerTypes.AlarmEvent, Truncate(summary.ToString(), 256), fields, local,
            PanelId: evt.AlarmPanelId, Kind: evt.Kind, Severity: evt.Severity,
            AreaNumber: evt.AreaNumber, ZoneNumber: evt.ZoneNumber, Code: evt.Code,
            Source: evt.Source, Text: evt.Description);
    }

    /// <summary>Disparo por cambio de conexión del servidor con un panel.</summary>
    public static WorkflowTrigger FromPanelStatus(AlarmPanel panel, AlarmPanelStatus status, string? message)
    {
        var local = DateTime.Now;
        string label = StatusLabel(status);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["panel"] = panel.Name,
            ["panelid"] = panel.Id.ToString(),
            ["evento"] = message is { Length: > 0 } ? $"{label}: {message}" : label,
            ["tipo"] = "Conexión",
            ["severidad"] = status == AlarmPanelStatus.Online ? SeverityLabel(AlarmSeverity.Info) : SeverityLabel(AlarmSeverity.Warning),
            ["codigo"] = "",
            ["area"] = "",
            ["areanumero"] = "",
            ["zona"] = "",
            ["zonanumero"] = "",
            ["operador"] = "",
            ["origen"] = "detectado por el servidor",
            ["estado"] = label,
        };
        FillCommon(fields, local);

        return new WorkflowTrigger(WorkflowTriggerTypes.PanelStatus,
            Truncate($"Panel '{panel.Name}' · {label}" + (message is { Length: > 0 } ? $": {message}" : ""), 256),
            fields, local,
            PanelId: panel.Id,
            Severity: status == AlarmPanelStatus.Online ? AlarmSeverity.Info : AlarmSeverity.Warning,
            Source: "poll", Text: message, PanelStatus: status);
    }

    /// <summary>
    /// Disparo de ejemplo para la prueba manual del editor: ejecuta las
    /// acciones reales (correo, FTP, parlante) con datos inventados, sin
    /// esperar a que el panel se alarme de verdad.
    /// </summary>
    public static WorkflowTrigger Sample(Workflow workflow, AlarmPanel? panel)
    {
        var local = DateTime.Now;
        bool status = workflow.TriggerType == WorkflowTriggerTypes.PanelStatus;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["panel"] = panel?.Name ?? "Panel de prueba",
            ["panelid"] = panel?.Id.ToString() ?? "0",
            ["evento"] = status ? "Sin conexión: prueba manual" : "Alarma de intrusión (prueba manual)",
            ["tipo"] = status ? "Conexión" : KindLabel(AlarmEventKind.Alarm),
            ["severidad"] = SeverityLabel(status ? AlarmSeverity.Warning : AlarmSeverity.Critical),
            ["codigo"] = status ? "" : "1130",
            ["area"] = panel?.Areas.FirstOrDefault()?.Name ?? "Área de prueba",
            ["areanumero"] = panel?.Areas.FirstOrDefault()?.Number.ToString() ?? "1",
            ["zona"] = status ? "" : panel?.Zones.FirstOrDefault()?.Name ?? "Zona de prueba",
            ["zonanumero"] = status ? "" : panel?.Zones.FirstOrDefault()?.Number.ToString() ?? "1",
            ["operador"] = "",
            ["origen"] = "prueba manual",
            ["estado"] = status ? StatusLabel(AlarmPanelStatus.Offline) : "",
        };
        FillCommon(fields, local);

        return new WorkflowTrigger(workflow.TriggerType,
            $"Prueba manual · {fields["panel"]} · {fields["evento"]}", fields, local,
            PanelId: panel?.Id,
            Kind: status ? null : AlarmEventKind.Alarm,
            Severity: status ? AlarmSeverity.Warning : AlarmSeverity.Critical,
            AreaNumber: panel?.Areas.FirstOrDefault()?.Number,
            ZoneNumber: status ? null : panel?.Zones.FirstOrDefault()?.Number,
            Code: status ? null : "1130",
            Source: "vms", Text: fields["evento"],
            PanelStatus: status ? AlarmPanelStatus.Offline : null);
    }

    private static void FillCommon(Dictionary<string, string> fields, DateTime local)
    {
        fields["fecha"] = local.ToString("dd-MM-yyyy");
        fields["hora"] = local.ToString("HH:mm:ss");
        fields["fechahora"] = local.ToString("dd-MM-yyyy HH:mm:ss");
        fields["servidor"] = Environment.MachineName;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>
    /// Reemplaza las marcas {clave} del texto por los datos del evento. Las
    /// marcas desconocidas se dejan tal cual: es más fácil ver el error en el
    /// correo que perseguir un campo que desapareció sin explicación.
    /// <paramref name="escape"/> transforma cada valor reemplazado (en las URL
    /// se escapa, para que un nombre de zona con espacios no la rompa).
    /// </summary>
    public string Render(string? template, string workflowName, Func<string, string>? escape = null)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var result = new StringBuilder(template.Length + 32);
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] != '{') { result.Append(template[i]); continue; }
            int close = template.IndexOf('}', i + 1);
            if (close < 0) { result.Append(template[i..]); break; }
            string key = template[(i + 1)..close];
            if (key.Equals("workflow", StringComparison.OrdinalIgnoreCase)) result.Append(escape?.Invoke(workflowName) ?? workflowName);
            else if (Fields.TryGetValue(key, out var value)) result.Append(escape?.Invoke(value) ?? value);
            else result.Append(template[i..(close + 1)]);
            i = close;
        }
        return result.ToString();
    }
}
