using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services.Workflows.Actions;

/// <summary>
/// Da una orden a una o más puertas del control de acceso: pulso de
/// apertura, mantener abierta, bloquear o volver a normal. Es lo que permite
/// "patente de la lista → abrir el portón" o "alarma de incendio → liberar
/// las puertas de evacuación".
///
/// La orden pasa por <see cref="AccessControlService"/> igual que la de un
/// operador (deja el modo anotado y lo publica al monitoreo) y queda en la
/// bitácora con el actor "sistema" y el nombre de la automatización.
///
/// Configuración: <c>{ "doorIds": [3, 4], "command": "Open" }</c>
/// (command: Open | Close | RemainOpen | RemainLocked).
/// </summary>
public sealed class DoorAction(
    IServiceScopeFactory scopeFactory,
    AuditService audit,
    ILogger<DoorAction> logger) : IWorkflowActionExecutor
{
    public string Type => WorkflowActionTypes.Door;
    public string Label => "Orden a una puerta";
    public string Description => "Abre, mantiene abierta, bloquea o cierra puertas del control de acceso.";

    public string? Validate(JsonElement config)
    {
        if (!config.TryGetProperty("doorIds", out var doors) || doors.ValueKind != JsonValueKind.Array || doors.GetArrayLength() == 0)
            return "Elija al menos una puerta.";
        string command = config.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
        if (!Enum.TryParse<AccessDoorCommand>(command, true, out _))
            return "Elija qué orden dar a la puerta (abrir, mantener abierta, bloquear o cerrar).";
        return null;
    }

    public async Task<WorkflowStepResult> ExecuteAsync(WorkflowActionContext context, CancellationToken ct)
    {
        var doorIds = context.Numbers("doorIds");
        if (doorIds.Count == 0) return WorkflowStepResult.Fail("La acción no tiene puertas configuradas.");
        if (!Enum.TryParse<AccessDoorCommand>(context.Text("command", "Open"), true, out var command))
            command = AccessDoorCommand.Open;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        // Resuelto aquí y no por constructor: el servicio de acceso publica en
        // el motor de automatizaciones, que a su vez contiene esta acción.
        var access = scope.ServiceProvider.GetRequiredService<AccessControlService>();

        var doors = await db.AccessDoors.Include(d => d.AccessDevice)
            .Where(d => doorIds.Contains(d.Id)).ToListAsync(ct);

        var done = new List<string>();
        var failures = new List<string>();
        string actor = $"automatización '{context.Workflow.Name}'";

        foreach (int id in doorIds)
        {
            var door = doors.FirstOrDefault(d => d.Id == id);
            if (door is null) { failures.Add($"la puerta {id} ya no existe"); continue; }
            var device = door.AccessDevice!;
            string target = $"{device.Name} · {door.Name}";

            string? refusal = null;
            if (!door.Enabled) refusal = "la puerta está desactivada en el VMS";
            else if (!device.Enabled) refusal = $"el equipo '{device.Name}' está pausado";
            else if (device.Status != AccessDeviceStatus.Online)
                refusal = $"el equipo '{device.Name}' no está en línea ({device.LastError ?? "sin conexión"})";
            if (refusal is not null)
            {
                failures.Add($"'{target}': {refusal}");
                await AuditAsync(command, door.Id, target, $"La {actor} no pudo {VerbInfinitive(command)} '{door.Name}' de '{device.Name}': {refusal}.", false);
                continue;
            }

            try
            {
                await access.CommandDoorAsync(db, door, command, ct);
                done.Add(target);
                await AuditAsync(command, door.Id, target,
                    $"{Verb(command)} la puerta '{door.Name}' del equipo '{device.Name}' por la {actor} ({context.Trigger.Summary}).", true);
            }
            catch (Exception ex) when (ex is DriverException or InvalidOperationException)
            {
                failures.Add($"'{target}': {ex.Message}");
                await AuditAsync(command, door.Id, target,
                    $"El equipo '{device.Name}' rechazó la orden de la {actor} sobre '{door.Name}': {ex.Message}", false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Fallo dando la orden {Command} a la puerta {Door}.", command, target);
                failures.Add($"'{target}': {ex.Message}");
            }
        }

        string what = Verb(command);
        if (done.Count == 0)
            return WorkflowStepResult.Fail($"No se pudo dar la orden a ninguna puerta: {string.Join("; ", failures)}.");
        string detail = $"{what} {done.Count} puerta(s): {string.Join(", ", done)}.";
        if (failures.Count > 0) detail += $" Con problemas: {string.Join("; ", failures)}.";
        return failures.Count == 0 ? WorkflowStepResult.Ok(detail) : WorkflowStepResult.Fail(detail);
    }

    private Task AuditAsync(AccessDoorCommand command, int doorId, string target, string detail, bool success) =>
        audit.LogSystemAsync("access", command switch
            {
                AccessDoorCommand.Open => "door-opened",
                AccessDoorCommand.Close => "door-closed",
                AccessDoorCommand.RemainOpen => "door-remain-open",
                _ => "door-remain-locked",
            },
            targetType: "access-door", targetId: doorId.ToString(), targetName: target,
            detail: detail, success: success, origin: "server");

    private static string Verb(AccessDoorCommand command) => command switch
    {
        AccessDoorCommand.Open => "Abrió",
        AccessDoorCommand.Close => "Cerró",
        AccessDoorCommand.RemainOpen => "Dejó abierta",
        _ => "Bloqueó",
    };

    private static string VerbInfinitive(AccessDoorCommand command) => command switch
    {
        AccessDoorCommand.Open => "abrir",
        AccessDoorCommand.Close => "cerrar",
        AccessDoorCommand.RemainOpen => "dejar abierta",
        _ => "bloquear",
    };
}
