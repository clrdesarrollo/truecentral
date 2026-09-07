using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services.Workflows.Actions;

/// <summary>
/// Captura una foto (snapshot JPEG) de una o más cámaras y la deja disponible
/// para las acciones siguientes: el correo la adjunta y el FTP la sube. Las
/// imágenes se guardan en disco (<see cref="WorkflowStore"/>) y el historial
/// de la ejecución conserva su ruta para poder verlas después en el panel.
///
/// Configuración: <c>{ "channelIds": [12, 13], "count": 1, "intervalSeconds": 2 }</c>
/// </summary>
public sealed class SnapshotAction(
    IServiceScopeFactory scopeFactory,
    DriverRegistry drivers,
    CredentialProtector credentials,
    WorkflowStore store,
    ILogger<SnapshotAction> logger) : IWorkflowActionExecutor
{
    /// <summary>Las capturas van contra el equipo: no más de dos a la vez en todo el motor.</summary>
    private static readonly SemaphoreSlim Throttle = new(2, 2);

    public string Type => WorkflowActionTypes.Snapshot;
    public string Label => "Capturar foto";
    public string Description => "Toma una foto de las cámaras elegidas; queda disponible para adjuntarla o subirla.";

    public string? Validate(JsonElement config)
    {
        if (!config.TryGetProperty("channelIds", out var channels) ||
            channels.ValueKind != JsonValueKind.Array || channels.GetArrayLength() == 0)
            return "Elija al menos una cámara para capturar.";
        return null;
    }

    public async Task<WorkflowStepResult> ExecuteAsync(WorkflowActionContext context, CancellationToken ct)
    {
        var channelIds = context.Numbers("channelIds");
        if (channelIds.Count == 0) return WorkflowStepResult.Fail("La acción no tiene cámaras configuradas.");

        int count = Math.Clamp(context.Number("count", 1), 1, 5);
        int interval = Math.Clamp(context.Number("intervalSeconds", 2), 0, 30);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var channels = await db.Channels.AsNoTracking().Include(c => c.Device)
            .Where(c => channelIds.Contains(c.Id))
            .ToListAsync(ct);

        var taken = new List<string>();
        var failures = new List<string>();

        foreach (int channelId in channelIds)
        {
            var channel = channels.FirstOrDefault(c => c.Id == channelId);
            if (channel is null) { failures.Add($"la cámara {channelId} ya no existe"); continue; }

            var factory = drivers.Find(channel.Device.DriverKey);
            if (factory is null || !factory.Capabilities.SupportsSnapshot)
            {
                failures.Add($"'{channel.Name}' (el driver no soporta capturas)");
                continue;
            }

            for (int shot = 0; shot < count; shot++)
            {
                if (shot > 0 && interval > 0) await Task.Delay(TimeSpan.FromSeconds(interval), ct);
                try
                {
                    await Throttle.WaitAsync(ct);
                    byte[]? jpeg;
                    try
                    {
                        var connection = new DeviceConnectionInfo(channel.Device.Host, channel.Device.SdkPort,
                            channel.Device.Username, credentials.Unprotect(channel.Device.PasswordCiphertext));
                        jpeg = await factory.Create().CaptureSnapshotAsync(connection, channel.ChannelNumber, ct);
                    }
                    finally { Throttle.Release(); }

                    if (jpeg is null or { Length: 0 })
                    {
                        failures.Add($"'{channel.Name}' (el equipo no entregó imagen)");
                        break;
                    }

                    string suffix = $"{channel.Device.Name}-{channel.Name}" + (count > 1 ? $"-{shot + 1}" : "");
                    if (store.Save(jpeg, context.Trigger.At, suffix) is not { } relative)
                    {
                        failures.Add($"'{channel.Name}' (no se pudo guardar en disco)");
                        break;
                    }

                    context.Files.Add(new WorkflowFile(relative, store.FullPath(relative)!,
                        $"{WorkflowStore.Sanitize(suffix)}-{context.Trigger.At:yyyyMMdd-HHmmss}.jpg"));
                    taken.Add(relative);
                    if (!context.Channels.Contains(channel.Id)) context.Channels.Add(channel.Id);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "No se pudo capturar la foto del canal {Channel}.", channelId);
                    failures.Add($"'{channel.Name}' ({ex.Message})");
                    break;
                }
            }
        }

        if (taken.Count == 0)
            return WorkflowStepResult.Fail($"No se pudo capturar ninguna foto: {string.Join("; ", failures)}.");

        string detail = $"{taken.Count} foto(s) capturada(s).";
        if (failures.Count > 0) detail += $" Sin imagen: {string.Join("; ", failures)}.";
        return WorkflowStepResult.Ok(detail, taken);
    }
}
