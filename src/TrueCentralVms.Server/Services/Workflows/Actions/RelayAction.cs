using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services.Workflows.Actions;

/// <summary>
/// Opera salidas (relés) de los módulos de E/S: un pulso para levantar una
/// barrera o abrir un portón, encender o apagar una sirena, alternar, o hacer
/// parpadear una baliza. Es lo que permite "patente de la lista → levantar la
/// barrera" con la barrera cableada a un relé del módulo.
///
/// La orden pasa por <see cref="IoModuleService"/> igual que la de un operador
/// y queda en la bitácora con el actor "sistema" y el nombre de la automatización.
///
/// Configuración: <c>{ "relayKeys": ["3:0"], "command": "Pulse", "seconds": 1,
/// "blinkSeconds": 10, "intervalMs": 500 }</c> (relayKeys: "módulo:salida", desde 0;
/// command: Pulse | On | Off | Toggle | Blink; seconds: duración del pulso;
/// blinkSeconds: duración del parpadeo, 0 = hasta otra orden).
/// </summary>
public sealed class RelayAction(
    IServiceScopeFactory scopeFactory,
    IoModuleService io,
    AuditService audit) : IWorkflowActionExecutor
{
    public string Type => WorkflowActionTypes.Relay;
    public string Label => "Operar relé";
    public string Description => "Da un pulso, enciende, apaga, alterna o hace parpadear salidas (relés) de un módulo de E/S: barreras, portones, balizas.";

    private static List<(int ModuleId, int Output)> Keys(JsonElement config)
    {
        var result = new List<(int, int)>();
        if (!config.TryGetProperty("relayKeys", out var keys) || keys.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in keys.EnumerateArray())
        {
            var parts = (item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "").Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int module) && int.TryParse(parts[1], out int output) && output >= 0)
                result.Add((module, output));
        }
        return result.Distinct().ToList();
    }

    private static double? Double(JsonElement config, string key) =>
        config.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    public string? Validate(JsonElement config)
    {
        if (Keys(config).Count == 0) return "Elija al menos un relé.";
        string command = config.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
        if (!Enum.TryParse<IoOutputCommand>(command, true, out var parsed))
            return "Elija qué hacer con el relé (pulso, encender, apagar, alternar o parpadear).";
        if (parsed == IoOutputCommand.Pulse && Double(config, "seconds") is { } pulse && pulse is < 0.1 or > 3600)
            return "El pulso debe durar entre 0,1 y 3600 segundos.";
        if (parsed == IoOutputCommand.Blink)
        {
            if (Double(config, "blinkSeconds") is < 0 or > 3600) return "El parpadeo dura entre 0 (hasta otra orden) y 3600 segundos.";
            if (Double(config, "intervalMs") is { } period && period is < 100 or > 60_000)
                return "El parpadeo debe alternar cada 100 a 60000 ms.";
        }
        return null;
    }

    public async Task<WorkflowStepResult> ExecuteAsync(WorkflowActionContext context, CancellationToken ct)
    {
        var keys = Keys(context.Config);
        if (keys.Count == 0) return WorkflowStepResult.Fail("La acción no tiene relés configurados.");
        if (!Enum.TryParse<IoOutputCommand>(context.Text("command", "Pulse"), true, out var command))
            command = IoOutputCommand.Pulse;
        double? seconds = Double(context.Config, command == IoOutputCommand.Blink ? "blinkSeconds" : "seconds");
        int? intervalMs = Double(context.Config, "intervalMs") is { } period ? (int)period : null;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var moduleIds = keys.Select(k => k.ModuleId).Distinct().ToList();
        var modules = await db.IoModules.AsNoTracking().Where(m => moduleIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, ct);

        var done = new List<string>();
        var failures = new List<string>();
        string actor = $"automatización '{context.Workflow.Name}'";
        foreach (var (moduleId, output) in keys)
        {
            if (!modules.TryGetValue(moduleId, out var module)) { failures.Add($"el módulo {moduleId} ya no existe"); continue; }
            if (!module.Enabled)
            {
                failures.Add($"'{module.Name}' está pausado");
                await AuditAsync(module.Id, module.Name, $"La {actor} no pudo operar DO{output} del módulo '{module.Name}': está pausado.", false);
                continue;
            }

            var outcome = await io.CommandAsync(moduleId, output, command, seconds, intervalMs, ct);
            if (outcome.Success) done.Add(outcome.Message.TrimEnd('.'));
            else failures.Add(outcome.Message.TrimEnd('.'));
            await AuditAsync(module.Id, module.Name,
                outcome.Success ? $"{outcome.Message.TrimEnd('.')} por la {actor} ({context.Trigger.Summary})." : $"La {actor} falló: {outcome.Message}",
                outcome.Success);
        }

        if (done.Count == 0)
            return WorkflowStepResult.Fail($"No se pudo operar ningún relé: {string.Join("; ", failures)}.");
        string detail = string.Join("; ", done) + ".";
        if (failures.Count > 0) detail += $" Con problemas: {string.Join("; ", failures)}.";
        return failures.Count == 0 ? WorkflowStepResult.Ok(detail) : WorkflowStepResult.Fail(detail);
    }

    private Task AuditAsync(int moduleId, string moduleName, string detail, bool success) =>
        audit.LogSystemAsync("io", "output-command",
            targetType: "io-module", targetId: moduleId.ToString(), targetName: moduleName,
            detail: detail, success: success, origin: "server");
}
