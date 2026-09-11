using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services.Workflows.Actions;

/// <summary>
/// Arma, desarma o borra la alarma de un área (o de todas) de un panel de
/// alarma. Es lo que permite "a las 22:00 de lunes a viernes, armar la
/// bodega" o "acceso concedido en portería → desarmar la recepción".
///
/// La orden pasa por <see cref="AlarmPanelService"/> igual que la de un
/// operador: queda en el historial del panel con el operador
/// "automatización '...'" y en la bitácora.
///
/// Configuración: <c>{ "panelId": 2, "areaNumber": 0, "command": "Arm", "mode": "Away" }</c>
/// (areaNumber 0 = todas las áreas; command: Arm | Disarm | ClearAlarm; mode: Away | Stay).
/// </summary>
public sealed class PanelAction(
    IServiceScopeFactory scopeFactory,
    AuditService audit,
    ILogger<PanelAction> logger) : IWorkflowActionExecutor
{
    public string Type => WorkflowActionTypes.Panel;
    public string Label => "Armar / desarmar panel";
    public string Description => "Arma, desarma o borra la alarma de un área de un panel de alarma.";

    public string? Validate(JsonElement config)
    {
        if (!config.TryGetProperty("panelId", out var panel) || panel.ValueKind != JsonValueKind.Number || panel.GetInt32() <= 0)
            return "Elija el panel de alarma.";
        string command = config.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
        if (command is not ("Arm" or "Disarm" or "ClearAlarm"))
            return "Elija la orden: armar, desarmar o borrar la alarma.";
        return null;
    }

    public async Task<WorkflowStepResult> ExecuteAsync(WorkflowActionContext context, CancellationToken ct)
    {
        int panelId = context.Number("panelId", 0);
        int area = Math.Max(context.Number("areaNumber", 0), 0);
        string command = context.Text("command", "Arm");
        var mode = context.Text("mode", "Away").Equals("Stay", StringComparison.OrdinalIgnoreCase) ? AlarmArmMode.Stay : AlarmArmMode.Away;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        // Resuelto aquí y no por constructor: el servicio de paneles publica en
        // el motor de automatizaciones, que a su vez contiene esta acción.
        var service = scope.ServiceProvider.GetRequiredService<AlarmPanelService>();

        var panel = await db.AlarmPanels.AsNoTracking().Include(p => p.Areas).Include(p => p.Zones).AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == panelId, ct);
        if (panel is null) return WorkflowStepResult.Fail("El panel ya no existe.");
        if (!panel.Enabled) return WorkflowStepResult.Fail($"El panel '{panel.Name}' está pausado en el VMS.");
        if (area > 0 && panel.Areas.All(a => a.Number != area))
            return WorkflowStepResult.Fail($"El área {area} no existe en el panel '{panel.Name}'.");
        if (command == "Arm" && panel.PanelTamper)
            return WorkflowStepResult.Fail($"No se puede armar '{panel.Name}': la tapa del panel está abierta (sabotaje).");

        string areaName = area <= 0 ? "todas las áreas" : $"el área '{panel.Areas.First(a => a.Number == area).Name}'";
        string actor = $"automatización '{context.Workflow.Name}'";
        string modeLabel = mode == AlarmArmMode.Stay ? "parcial (en casa)" : "total (fuera)";

        Func<IAlarmPanelDriver, AlarmConnectionInfo, Task> order;
        AlarmEventKind kind;
        string description, auditKey, verb;
        switch (command)
        {
            case "Disarm":
                order = (d, c) => d.DisarmAsync(c, area, ct);
                kind = AlarmEventKind.Disarm;
                description = $"Desarmado de {areaName} por la {actor}";
                auditKey = "area-disarmed";
                verb = "Desarmó";
                break;
            case "ClearAlarm":
                order = (d, c) => d.ClearAlarmAsync(c, area, ct);
                kind = AlarmEventKind.Info;
                description = $"Alarma borrada en {areaName} por la {actor}";
                auditKey = "alarm-cleared";
                verb = "Borró la alarma de";
                break;
            default:
                order = (d, c) => d.ArmAsync(c, area, mode, ct);
                kind = AlarmEventKind.Arm;
                description = $"Armado {modeLabel} de {areaName} por la {actor}";
                auditKey = "area-armed";
                verb = $"Armó ({modeLabel})";
                break;
        }

        try
        {
            await service.ExecuteAsync(panel, order, kind, description, area, null, actor, ct);
            await audit.LogSystemAsync("alarms", auditKey,
                targetType: "alarm-panel", targetId: panel.Id.ToString(), targetName: panel.Name,
                detail: $"{verb} {areaName} del panel '{panel.Name}' por la {actor} ({context.Trigger.Summary}).",
                origin: "server");
            return WorkflowStepResult.Ok($"{verb} {areaName} del panel '{panel.Name}'.");
        }
        catch (DriverException ex)
        {
            await audit.LogSystemAsync("alarms", auditKey,
                targetType: "alarm-panel", targetId: panel.Id.ToString(), targetName: panel.Name,
                detail: $"El panel '{panel.Name}' rechazó la orden de la {actor} sobre {areaName}: {ex.Message}",
                success: false, origin: "server");
            return WorkflowStepResult.Fail($"El panel '{panel.Name}' rechazó la orden: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fallo dando la orden {Command} al panel {Panel}.", command, panel.Name);
            return WorkflowStepResult.Fail($"Error inesperado con el panel '{panel.Name}': {ex.Message}");
        }
    }
}
