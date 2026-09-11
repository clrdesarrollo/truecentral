using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services.Workflows.Actions;

/// <summary>
/// Mueve una cámara PTZ a un preset guardado en el equipo: "sensor del
/// portón interrumpido → apuntar el domo al portón y recién entonces sacar
/// la foto". Se ejecuta con el driver del equipo (Hikvision, Dahua, ONVIF).
///
/// Configuración: <c>{ "channelId": 12, "preset": 3 }</c>
/// </summary>
public sealed class PtzPresetAction(
    IServiceScopeFactory scopeFactory,
    DriverRegistry drivers,
    CredentialProtector credentials,
    AuditService audit,
    ILogger<PtzPresetAction> logger) : IWorkflowActionExecutor
{
    public string Type => WorkflowActionTypes.PtzPreset;
    public string Label => "Mover cámara PTZ a preset";
    public string Description => "Apunta una cámara PTZ a un preset guardado en el equipo (1..300).";

    public string? Validate(JsonElement config)
    {
        if (!config.TryGetProperty("channelId", out var channel) || channel.ValueKind != JsonValueKind.Number || channel.GetInt32() <= 0)
            return "Elija la cámara PTZ.";
        if (!config.TryGetProperty("preset", out var preset) || preset.ValueKind != JsonValueKind.Number ||
            preset.GetInt32() is < 1 or > 300)
            return "El preset debe estar entre 1 y 300.";
        return null;
    }

    public async Task<WorkflowStepResult> ExecuteAsync(WorkflowActionContext context, CancellationToken ct)
    {
        int channelId = context.Number("channelId", 0);
        int preset = context.Number("preset", 1);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var channel = await db.Channels.AsNoTracking().Include(c => c.Device).FirstOrDefaultAsync(c => c.Id == channelId, ct);
        if (channel is null) return WorkflowStepResult.Fail("La cámara ya no existe.");
        if (!channel.SupportsPtz) return WorkflowStepResult.Fail($"La cámara '{channel.Name}' no tiene PTZ.");

        var factory = drivers.Find(channel.Device.DriverKey);
        if (factory is null) return WorkflowStepResult.Fail($"Driver '{channel.Device.DriverKey}' no disponible.");

        string target = $"{channel.Device.Name} · {channel.Name}";
        try
        {
            var connection = new DeviceConnectionInfo(channel.Device.Host, channel.Device.SdkPort,
                channel.Device.Username, credentials.Unprotect(channel.Device.PasswordCiphertext));
            bool ok = await factory.Create().PtzPresetAsync(connection, channel.ChannelNumber,
                TrueCentralVms.Core.Drivers.PtzPresetAction.Goto, preset, ct);

            await audit.LogSystemAsync("ptz", "ptz-preset-goto",
                targetType: "channel", targetId: $"{channel.DeviceId}/{channel.ChannelNumber}", targetName: target,
                detail: ok
                    ? $"La automatización '{context.Workflow.Name}' movió '{target}' al preset {preset} ({context.Trigger.Summary})."
                    : $"El equipo rechazó mover '{target}' al preset {preset} pedido por la automatización '{context.Workflow.Name}'.",
                success: ok, origin: "server");

            return ok
                ? WorkflowStepResult.Ok($"'{target}' movida al preset {preset}.")
                : WorkflowStepResult.Fail($"El equipo rechazó mover '{target}' al preset {preset}.");
        }
        catch (DriverException ex)
        {
            return WorkflowStepResult.Fail($"'{target}': {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fallo moviendo {Target} al preset {Preset}.", target, preset);
            return WorkflowStepResult.Fail($"'{target}': error inesperado: {ex.Message}");
        }
    }
}
