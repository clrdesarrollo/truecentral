using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

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
    /// <summary>
    /// Tope de capturas simultáneas en todo el motor. Las cámaras de una misma
    /// acción se disparan a la vez (fotos del mismo instante), así que el tope
    /// debe cubrir una acción típica sin serializarla; solo frena ráfagas de
    /// muchas ejecuciones juntas contra los equipos.
    /// </summary>
    private static readonly SemaphoreSlim Throttle = new(12, 12);

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

        // Cámaras capturables, en el orden que definió el usuario (el aviso y
        // el correo muestran las fotos en ese orden).
        var active = new List<(Channel Channel, IDeviceDriverFactory Factory)>();
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
            active.Add((channel, factory));
        }

        // Cada ronda dispara todas las cámaras a la vez, para que las fotos
        // sean del mismo instante y no una tras otra. Una cámara que falla
        // sale de las rondas siguientes (igual que antes).
        for (int shot = 0; shot < count && active.Count > 0; shot++)
        {
            if (shot > 0 && interval > 0) await Task.Delay(TimeSpan.FromSeconds(interval), ct);

            var captures = await Task.WhenAll(active.Select(a => CaptureAsync(a.Channel, a.Factory, ct)));

            var failed = new List<int>();
            for (int i = 0; i < active.Count; i++)
            {
                var channel = active[i].Channel;
                var (jpeg, error) = captures[i];

                if (error is not null) { failures.Add($"'{channel.Name}' ({error})"); failed.Add(i); continue; }
                if (jpeg is null or { Length: 0 })
                {
                    failures.Add($"'{channel.Name}' (el equipo no entregó imagen)");
                    failed.Add(i);
                    continue;
                }

                // La cámara que disparó el evento: se marca la línea/región de
                // la regla (sin el recuadro del objeto: esta foto es posterior).
                if (context.Trigger.Overlay is { } overlay && context.Trigger.ChannelId == channel.Id && OperatingSystem.IsWindows())
                    jpeg = EventPhotoAnnotator.Annotate(jpeg, overlay, includeTarget: false) ?? jpeg;

                string suffix = $"{channel.Device.Name}-{channel.Name}" + (count > 1 ? $"-{shot + 1}" : "");
                if (store.Save(jpeg, context.Trigger.At, suffix) is not { } relative)
                {
                    failures.Add($"'{channel.Name}' (no se pudo guardar en disco)");
                    failed.Add(i);
                    continue;
                }

                context.Files.Add(new WorkflowFile(relative, store.FullPath(relative)!,
                    $"{WorkflowStore.Sanitize(suffix)}-{context.Trigger.At:yyyyMMdd-HHmmss}.jpg"));
                taken.Add(relative);
                if (!context.Channels.Contains(channel.Id)) context.Channels.Add(channel.Id);
            }

            for (int i = failed.Count - 1; i >= 0; i--) active.RemoveAt(failed[i]);
        }

        if (taken.Count == 0)
            return WorkflowStepResult.Fail($"No se pudo capturar ninguna foto: {string.Join("; ", failures)}.");

        string detail = $"{taken.Count} foto(s) capturada(s).";
        if (failures.Count > 0) detail += $" Sin imagen: {string.Join("; ", failures)}.";
        return WorkflowStepResult.Ok(detail, taken);
    }

    /// <summary>Una captura contra el equipo; devuelve la imagen o el motivo del error (nunca lanza, salvo cancelación).</summary>
    private async Task<(byte[]? Jpeg, string? Error)> CaptureAsync(Channel channel, IDeviceDriverFactory factory,
        CancellationToken ct)
    {
        try
        {
            await Throttle.WaitAsync(ct);
            try
            {
                var connection = new DeviceConnectionInfo(channel.Device.Host, channel.Device.SdkPort,
                    channel.Device.Username, credentials.Unprotect(channel.Device.PasswordCiphertext));
                return (await factory.Create().CaptureSnapshotAsync(connection, channel.ChannelNumber, ct), null);
            }
            finally { Throttle.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo capturar la foto del canal {Channel}.", channel.Id);
            return (null, ex.Message);
        }
    }
}
