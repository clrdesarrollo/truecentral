using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;

namespace TrueCentralVms.Server.Services.Workflows.Actions;

/// <summary>
/// Avisa a los operadores conectados. Cada aviso queda registrado como
/// <see cref="WorkflowAlert"/>: nace PENDIENTE y solo se cierra cuando alguien
/// se da por enterado, dejando usuario, hora y origen. Así siempre se puede
/// responder "quién la vio y cuándo" — y también "nadie la vio", que es la
/// pregunta incómoda que un sistema de seguridad tiene que poder contestar.
///
/// El aviso lleva la última foto capturada en la misma ejecución y puede hacer
/// sonar una alarma en el equipo del operador (los mismos sonidos que se
/// cargan para los parlantes IP, o el pitido del sistema).
///
/// Configuración: <c>{ "title":"...", "message":"...", "severity":"Critical",
/// "attachSnapshot":true, "sound":"sirena", "soundRepeat":2, "requireAck":true,
/// "channelIds":[4] }</c> (sin <c>channelIds</c>, la ventana muestra el vivo de
/// las cámaras que capturaron foto en la misma ejecución).
/// </summary>
public sealed class NotifyAction(
    IHubContext<VmsHub> hub,
    WorkflowStore store,
    IServiceScopeFactory scopeFactory,
    ILogger<NotifyAction> logger) : IWorkflowActionExecutor
{
    public string Type => WorkflowActionTypes.Notify;
    public string Label => "Avisar a los operadores";
    public string Description => "Aviso en pantalla (con foto y alarma sonora) que el operador debe confirmar.";

    public string? Validate(JsonElement config)
    {
        string? sound = config.TryGetProperty("sound", out var value) ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(sound) || sound == WorkflowNotificationDto.SystemSoundName) return null;
        return store.FindAudio(sound) is null
            ? $"El sonido '{sound}' ya no está cargado en el servidor (Automatizaciones → Sonidos)."
            : null;
    }

    public async Task<WorkflowStepResult> ExecuteAsync(WorkflowActionContext context, CancellationToken ct)
    {
        string title = context.Render(context.Text("title", "{tipo} en {panel}"));
        string message = context.Render(context.Text("message", "{evento} · {zona} · {fechahora}"));
        var severity = Enum.TryParse<AlarmSeverity>(context.Text("severity", "Warning"), true, out var parsed)
            ? parsed
            : AlarmSeverity.Warning;

        // Las fotos de esta ejecución: la última es la principal (la más
        // cercana al hecho) y todas quedan en la alerta para que la ventana
        // del operador las muestre como una tira.
        bool withPhotos = context.Flag("attachSnapshot", true) && context.Files.Count > 0;
        var images = withPhotos ? context.Files.Select(f => f.RelativePath).ToList() : [];
        string? image = images.Count > 0 ? images[^1] : null;

        string sound = context.Text("sound").Trim();
        // 0 = sonar hasta que alguien confirme la alerta o cierre la ventana.
        int repeat = Math.Clamp(context.Number("soundRepeat", 1), 0, 5);
        // Un sonido borrado del servidor no debe dejar el aviso mudo: se cae
        // al pitido del sistema, que siempre está disponible en el equipo.
        if (sound.Length > 0 && sound != WorkflowNotificationDto.SystemSoundName && store.FindAudio(sound) is null)
            sound = WorkflowNotificationDto.SystemSoundName;

        bool requireAck = context.Flag("requireAck", true);

        // Cámaras para el video en vivo de la ventana de alarma: las que
        // indique la acción o, si no indica ninguna, las que capturaron foto.
        var channels = context.Numbers("channelIds").ToList();
        if (channels.Count == 0) channels = [.. context.Channels];

        var alert = new WorkflowAlert
        {
            WorkflowId = context.Workflow.Id,
            WorkflowName = Cut(context.Workflow.Name, 128),
            RaisedAt = DateTime.UtcNow,
            Title = Cut(title, 160),
            Message = Cut(message, 512),
            Severity = severity,
            ImagePath = image,
            ImagePathsJson = images.Count > 0 ? WorkflowJson.Serialize(images) : null,
            ChannelIdsJson = channels.Count > 0 ? WorkflowJson.Serialize(channels) : null,
            Sound = sound.Length == 0 ? null : sound,
            SoundRepeat = repeat,
            TriggerSummary = Cut(context.Trigger.Summary, 256),
            RequiresAck = requireAck,
        };

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            db.WorkflowAlerts.Add(alert);
            await db.SaveChangesAsync(ct);
            context.Alerts.Add(alert.Id);
        }
        catch (Exception ex)
        {
            // Sin registro no hay acuse de recibo posible: es un fallo de la
            // acción, no un detalle. El operador igual recibe el aviso.
            logger.LogError(ex, "No se pudo registrar la alerta de la automatización '{Name}'.", context.Workflow.Name);
            await PushAsync(context, alert, 0, false, ct);
            return WorkflowStepResult.Fail(
                $"El aviso se envió pero NO se pudo registrar para acuse de recibo: {ex.Message}");
        }

        await PushAsync(context, alert, alert.Id, requireAck, ct);

        string detail = $"Aviso enviado a los operadores conectados: «{alert.Title}»";
        if (image is not null) detail += ", con la foto capturada";
        if (sound.Length > 0)
            detail += $", con alarma sonora ({(sound == WorkflowNotificationDto.SystemSoundName ? "pitido del sistema" : $"'{sound}'")}" +
                      (repeat == 0 ? ", hasta que alguien confirme" : repeat > 1 ? $" ×{repeat}" : "") + ")";
        detail += requireAck ? $". Alerta {alert.Id} pendiente de confirmación." : ".";
        return WorkflowStepResult.Ok(detail);
    }

    private Task PushAsync(WorkflowActionContext context, WorkflowAlert alert, long alertId, bool requireAck,
        CancellationToken ct) =>
        hub.Clients.All.SendAsync(VmsHubContract.WorkflowNotification,
            new WorkflowNotificationDto(context.Workflow.Id, context.Workflow.Name, alert.Title, alert.Message,
                alert.Severity, alert.RaisedAt, alert.ImagePath, alert.Sound, alert.SoundRepeat, alertId, requireAck),
            ct);

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
