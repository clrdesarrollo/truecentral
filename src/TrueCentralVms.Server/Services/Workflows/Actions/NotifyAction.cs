using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services.Licensing;

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
/// las cámaras que capturaron foto en la misma ejecución o, si no hubo, el de
/// las cámaras asociadas al recurso que disparó).
///
/// Si el recurso que disparó tiene ficha en Recursos, la alerta guarda su
/// ubicación y sus consignas: la ventana del operador las muestra (así el
/// puesto no abre además la verificación de ese recurso).
///
/// Destino (<c>"destination"</c>): la ventana de alarma del cliente de
/// escritorio (<c>popup</c>, lo de siempre), las pantallas de alarma de las
/// casetas (<c>screens</c>, todas o las de <c>"screenIds"</c>) o ambas
/// (<c>both</c>). Es UNA sola alerta: confirmarla en cualquier lado la cierra
/// en todos. Si las pantallas no están licenciadas o no hay ninguna, el aviso
/// va igual a la ventana: un aviso que nadie puede ver es peor que uno en el
/// lugar equivocado.
/// </summary>
public sealed class NotifyAction(
    ScopedHub hub,
    WorkflowStore store,
    AlarmScreenService screens,
    LicenseService license,
    IServiceScopeFactory scopeFactory,
    ILogger<NotifyAction> logger) : IWorkflowActionExecutor
{
    public string Type => WorkflowActionTypes.Notify;
    public string Label => "Avisar a los operadores";
    public string Description =>
        "Aviso (con foto y alarma sonora) en la ventana de alarma de los operadores, en las pantallas de alarma de las casetas o en ambas.";

    public string? Validate(JsonElement config)
    {
        string? destination = config.TryGetProperty("destination", out var target) && target.ValueKind == JsonValueKind.String
            ? target.GetString()
            : null;
        if (destination is not null && !NotifyDestinations.IsValid(destination))
            return $"Destino del aviso desconocido: '{destination}'.";
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

        // Las fotos de esta ejecución, en el orden en que las capturó la
        // acción "Capturar foto" (el orden de cámaras que definió el usuario):
        // la primera es la principal y todas quedan en la alerta para que la
        // ventana del operador las muestre como una tira en ese mismo orden.
        bool withPhotos = context.Flag("attachSnapshot", true) && context.Files.Count > 0;
        var images = withPhotos ? context.Files.Select(f => f.RelativePath).ToList() : [];
        string? image = images.Count > 0 ? images[0] : null;

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

        // El recurso que disparó (su ficha en Recursos): su ubicación y sus
        // consignas viajan con la alerta (la ventana del operador las muestra
        // y quedan como constancia), completan las marcas {ubicacion} y
        // {consignas} y, si no hubo cámaras de las anteriores, pone sus
        // cámaras asociadas (la principal primero).
        var briefing = await BriefingAsync(context, ct);
        if (channels.Count == 0 && briefing is not null)
            channels = briefing.Cameras.Select(c => c.ChannelId).ToList();
        title = ResourceMarks(title, briefing);
        message = ResourceMarks(message, briefing);

        // Destinatarios: usuarios concretos (userIds) o, sin ninguno, todos
        // los operadores conectados. Se resuelven contra la base en cada
        // aviso: un usuario borrado o desactivado deja de recibirlos.
        var wantedUsers = context.Numbers("userIds").Distinct().ToList();
        var recipients = new List<(int Id, string Username)>();
        string? recipientsWarning = null;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            if (wantedUsers.Count > 0)
            {
                recipients = await db.Users.AsNoTracking()
                    .Where(u => wantedUsers.Contains(u.Id) && u.Enabled)
                    .OrderBy(u => u.Username)
                    .Select(u => new ValueTuple<int, string>(u.Id, u.Username))
                    .ToListAsync(ct);
                if (recipients.Count == 0)
                    // Antes que dejar el aviso sin nadie que lo vea, va a todos.
                    recipientsWarning = "ninguno de los destinatarios configurados existe o está activo: se avisó a todos los operadores";
                else if (recipients.Count < wantedUsers.Count)
                    recipientsWarning = $"{wantedUsers.Count - recipients.Count} destinatario(s) configurado(s) ya no existe(n) o está(n) desactivado(s)";
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudieron resolver los destinatarios del aviso de '{Name}'; va a todos.", context.Workflow.Name);
            recipients = [];
            recipientsWarning = "no se pudieron resolver los destinatarios: se avisó a todos los operadores";
        }

        // Dónde se muestra: la ventana de alarma, las pantallas de las casetas o ambas.
        string destination = context.Text("destination", NotifyDestinations.Popup).Trim();
        if (!NotifyDestinations.IsValid(destination)) destination = NotifyDestinations.Popup;
        bool showPopup = NotifyDestinations.ShowsPopup(destination);
        bool showOnScreens = NotifyDestinations.ShowsOnScreens(destination);
        var targetScreens = new List<(int Id, string Name)>();
        bool allScreens = true;
        string? screensWarning = null;
        if (showOnScreens)
        {
            var wantedScreens = context.Numbers("screenIds").Distinct().ToList();
            allScreens = wantedScreens.Count == 0;
            if (!license.IsModuleEnabled(LicenseFeatures.ModuleAlarmScreens))
            {
                showOnScreens = false;
                screensWarning = "las pantallas de alarma no están incluidas en la licencia";
            }
            else
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
                    targetScreens = await db.AlarmScreens.AsNoTracking()
                        .Where(x => x.Status == AlarmScreenStatus.Approved && (allScreens || wantedScreens.Contains(x.Id)))
                        .OrderBy(x => x.Name)
                        .Select(x => new ValueTuple<int, string>(x.Id, x.Name))
                        .ToListAsync(ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "No se pudieron resolver las pantallas del aviso de '{Name}'.", context.Workflow.Name);
                }
                if (targetScreens.Count == 0)
                {
                    showOnScreens = false;
                    screensWarning = allScreens
                        ? "no hay pantallas de alarma aprobadas"
                        : "ninguna de las pantallas elegidas existe o está habilitada";
                }
                else if (!allScreens && targetScreens.Count < wantedScreens.Count)
                    screensWarning = $"{wantedScreens.Count - targetScreens.Count} pantalla(s) elegida(s) ya no existe(n) o está(n) deshabilitada(s)";
            }
            // Sin ninguna pantalla que lo muestre, el aviso va a la ventana de alarma.
            if (!showOnScreens && !showPopup)
            {
                showPopup = true;
                screensWarning += ": se avisó en la ventana de alarma de los operadores";
            }
        }

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
            // El recurso que ORIGINÓ el evento (decide quién ve la alerta), que
            // puede no ser el de las consignas (una zona sin ficha usa las de su área).
            ResourceKey = briefing is null ? null : briefing.OriginKey ?? ResourceCatalog.Key(briefing.Kind, briefing.Id),
            LocationPath = briefing?.LocationPath is { } path ? Cut(path, 1100) : null,
            Instructions = briefing?.Instructions is { } instructions && !string.IsNullOrWhiteSpace(instructions)
                ? Cut(instructions.Trim(), 4000)
                : null,
            RequiresAck = requireAck,
            RecipientUserIds = recipients.Count > 0 ? "," + string.Join(",", recipients.Select(r => r.Id)) + "," : null,
            Recipients = recipients.Count > 0 ? Cut(string.Join(", ", recipients.Select(r => r.Username)), 512) : null,
            ShowPopup = showPopup,
            ShowOnScreens = showOnScreens,
            ScreenIds = showOnScreens && !allScreens ? Cut("," + string.Join(",", targetScreens.Select(x => x.Id)) + ",", 512) : null,
            Screens = showOnScreens && !allScreens ? Cut(string.Join(", ", targetScreens.Select(x => x.Name)), 512) : null,
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
            // Sin registro las pantallas no pueden mostrarlo (necesitan el id para
            // confirmarlo): va a la ventana de alarma, que sí puede.
            await PushAsync(context, alert, recipients, 0, false, ct);
            return WorkflowStepResult.Fail(
                $"El aviso se envió pero NO se pudo registrar para acuse de recibo: {ex.Message}");
        }

        if (alert.ShowPopup) await PushAsync(context, alert, recipients, alert.Id, requireAck, ct);
        // Persistir primero, difundir después: una pantalla desconectada lo
        // recupera al resincronizar.
        if (alert.ShowOnScreens) await screens.PublishRaisedAsync(alert, ct);

        string where = alert.ShowOnScreens
            ? (allScreens
                ? $"a las pantallas de alarma ({targetScreens.Count})"
                : $"a las pantallas de alarma {string.Join(", ", targetScreens.Select(x => x.Name))}")
            : "";
        string detail = !alert.ShowPopup
            ? $"Aviso enviado {where}: «{alert.Title}»"
            : (recipients.Count > 0
                ? $"Aviso enviado a {string.Join(", ", recipients.Select(r => r.Username))}"
                : "Aviso enviado a los operadores conectados") +
              (alert.ShowOnScreens ? $" y {where}" : "") + $": «{alert.Title}»";
        if (recipientsWarning is not null) detail += $" ({recipientsWarning})";
        if (screensWarning is not null) detail += $" ({screensWarning})";
        if (image is not null) detail += ", con la foto capturada";
        if (sound.Length > 0)
            detail += $", con alarma sonora ({(sound == WorkflowNotificationDto.SystemSoundName ? "pitido del sistema" : $"'{sound}'")}" +
                      (repeat == 0 ? ", hasta que alguien confirme" : repeat > 1 ? $" ×{repeat}" : "") + ")";
        detail += requireAck ? $". Alerta {alert.Id} pendiente de confirmación." : ".";
        return WorkflowStepResult.Ok(detail);
    }

    /// <summary>Empuja el aviso a todos los conectados o solo a los grupos de los destinatarios.</summary>
    private Task PushAsync(WorkflowActionContext context, WorkflowAlert alert, List<(int Id, string Username)> recipients,
        long alertId, bool requireAck, CancellationToken ct)
    {
        var dto = new WorkflowNotificationDto(context.Workflow.Id, context.Workflow.Name, alert.Title, alert.Message,
            alert.Severity, alert.RaisedAt, alert.ImagePath, alert.Sound, alert.SoundRepeat, alertId, requireAck,
            alert.ResourceKey);
        // Dirigido: solo a sus destinatarios. Para todos: a quien ve el recurso
        // que lo originó (alcance por ubicación), o a todos si no tiene recurso.
        var ids = recipients.Select(r => r.Id).ToHashSet();
        string? resourceKey = alert.ResourceKey;
        return hub.SendToAsync(VmsHubContract.WorkflowNotification, dto,
            s => ids.Count > 0 ? ids.Contains(s.UserId) : s.CanViewResourceKey(resourceKey), ct);
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    private static bool UsesResourceMarks(string text) =>
        text.Contains("{ubicacion}", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("{consignas}", StringComparison.OrdinalIgnoreCase);

    private static string ResourceMarks(string text, ResourceBriefingDto? briefing) => !UsesResourceMarks(text)
        ? text
        : text.Replace("{ubicacion}", briefing?.LocationPath ?? "sin ubicación", StringComparison.OrdinalIgnoreCase)
              .Replace("{consignas}", briefing?.Instructions ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>Ficha del recurso que disparó la automatización (zona o área, puerta, cámara), si la tiene.</summary>
    private async Task<ResourceBriefingDto?> BriefingAsync(WorkflowActionContext context, CancellationToken ct)
    {
        var t = context.Trigger;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            return await ResourceBriefings.ForEventAsync(db, new ResourceBriefings.EventRef(
                AlarmPanelId: t.Type == WorkflowTriggerTypes.AlarmEvent ? t.PanelId : null,
                AreaNumber: t.AreaNumber, ZoneNumber: t.ZoneNumber,
                AccessDeviceId: t.AccessDeviceId, DoorNumber: t.DoorNumber,
                ChannelId: t.ChannelId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sin la ficha el aviso sale igual, solo sin esos datos.
            logger.LogDebug(ex, "No se pudo leer la ficha del recurso que disparó '{Name}'.", context.Workflow.Name);
            return null;
        }
    }
}
