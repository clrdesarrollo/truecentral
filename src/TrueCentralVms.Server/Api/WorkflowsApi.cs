using TrueCentralVms.Server.Services.Licensing;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Workflows;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// API del módulo Automatizaciones: mantenedor de workflows (administrador),
/// historial de ejecuciones (cualquier usuario con sesión), configuración del
/// correo saliente y sonidos para los parlantes IP.
///
/// El editor del panel se arma con /api/workflows/catalog: los disparadores,
/// las acciones y las marcas de plantilla salen del servidor, así una acción
/// nueva aparece sin tocar el JavaScript.
/// </summary>
public static class WorkflowsApi
{
    private const int MaxTake = 200;
    private const int MaxActions = 20;
    private const long MaxAudioBytes = 8 * 1024 * 1024;

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    /// <summary>Marcas que se pueden usar en los textos de las acciones.</summary>
    private static readonly WorkflowPlaceholderDto[] Placeholders =
    [
        new("workflow", "Nombre de la automatización"),
        new("panel", "Nombre del panel"),
        new("evento", "Descripción del evento"),
        new("tipo", "Naturaleza (Alarma, Falla, Armado...)"),
        new("severidad", "Severidad (Crítica, Advertencia, Informativa)"),
        new("codigo", "Código del evento (ej. 1130)"),
        new("area", "Nombre del área"),
        new("areanumero", "Número del área"),
        new("zona", "Nombre de la zona"),
        new("zonanumero", "Número de la zona"),
        new("operador", "Usuario que operó el panel"),
        new("origen", "Cómo se supo (panel, sondeo, VMS)"),
        new("estado", "Estado de conexión del panel"),
        new("fecha", "Fecha (dd-mm-aaaa)"),
        new("hora", "Hora (hh:mm:ss)"),
        new("fechahora", "Fecha y hora"),
        new("servidor", "Nombre del servidor"),
    ];

    private static readonly WorkflowTriggerInfoDto[] Triggers =
    [
        new(WorkflowTriggerTypes.AlarmEvent, "Evento de panel de alarma",
            "Cuando un panel informa una alarma, un armado, una anulación o una falla."),
        new(WorkflowTriggerTypes.PanelStatus, "Conexión con un panel",
            "Cuando el servidor pierde o recupera la conexión con un panel de alarma."),
    ];

    public static void MapWorkflowsApi(this WebApplication app)
    {
        // ------------------------------------------------------------------
        // Catálogo para el editor
        // ------------------------------------------------------------------
        app.MapGet("/api/workflows/catalog", async (HttpContext ctx, WorkflowEngine engine, SmtpSender smtp,
            CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var settings = await smtp.LoadAsync(ct);
            return Results.Ok(new WorkflowCatalogDto(
                Triggers,
                engine.Executors.Select(e => new WorkflowActionInfoDto(e.Type, e.Label, e.Description, e.UsesSecret)).ToList(),
                Placeholders,
                settings is { Enabled: true, Host.Length: > 0 },
                MediaMtxManager.LocateFfmpeg() is not null));
        });

        // Cámaras elegibles en la acción "capturar foto" (lista plana: el
        // editor no debería pedir los canales equipo por equipo).
        app.MapGet("/api/workflows/cameras", async (HttpContext ctx, VmsDbContext db, DriverRegistry drivers,
            CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var channels = await db.Channels.AsNoTracking().Include(c => c.Device)
                .Where(c => c.Enabled)
                .OrderBy(c => c.Device.Name).ThenBy(c => c.ChannelNumber)
                .Select(c => new { c.Id, c.DeviceId, DeviceName = c.Device.Name, c.Name, c.Device.DriverKey })
                .ToListAsync(ct);
            return Results.Ok(channels.Select(c => new WorkflowCameraDto(c.Id, c.DeviceId, c.DeviceName, c.Name,
                drivers.Find(c.DriverKey)?.Capabilities.SupportsSnapshot ?? false)));
        });

        // Parlantes elegibles en la acción "sonar parlante IP" (inventario del módulo Parlantes).
        app.MapGet("/api/workflows/speakers", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var speakers = await db.Speakers.AsNoTracking().OrderBy(s => s.GroupName).ThenBy(s => s.Name).ToListAsync(ct);
            return Results.Ok(speakers.Select(s => new WorkflowSpeakerDto(s.Id, s.Name, s.GroupName, s.Enabled,
                s.SupportsLibrary, s.SupportsTts, s.SupportsLiveAudio)));
        });

        // ------------------------------------------------------------------
        // Mantenedor de automatizaciones
        // ------------------------------------------------------------------
        app.MapGet("/api/workflows", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var workflows = await db.Workflows.AsNoTracking().Include(w => w.Actions)
                .OrderBy(w => w.Name).ToListAsync(ct);
            return Results.Ok(workflows.Select(WorkflowMapper.ToDto));
        });

        app.MapGet("/api/workflows/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var workflow = await db.Workflows.AsNoTracking().Include(w => w.Actions)
                .FirstOrDefaultAsync(w => w.Id == id, ct);
            return workflow is null ? Results.NotFound() : Results.Ok(WorkflowMapper.ToDto(workflow));
        });

        app.MapPost("/api/workflows", async (HttpContext ctx, WorkflowWriteDto request, VmsDbContext db, LicenseService license,
            WorkflowEngine engine, CredentialProtector protector, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out var session) is { } failure) return failure;
            if (Validate(request, engine) is { } invalid) return Error(invalid);
            if (await db.Workflows.AnyAsync(w => w.Name == request.Name.Trim(), ct))
                return Error("Ya existe una automatización con ese nombre.");
            if (license.Deny(LicenseFeatures.ModuleAutomation, request.Enabled ? LicenseFeatures.AutomationRules : null,
                    await db.Workflows.CountAsync(w => w.Enabled, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "workflow", request.Name.Trim());

            var workflow = new Workflow
            {
                Name = request.Name.Trim(),
                Description = Clean(request.Description),
                Enabled = request.Enabled,
                TriggerType = request.TriggerType,
                ConditionsJson = WorkflowJson.Serialize(request.Conditions ?? new WorkflowConditionsDto()),
                CooldownSeconds = Math.Clamp(request.CooldownSeconds, 0, 86_400),
                CreatedBy = session.Username,
            };
            ApplyActions(workflow, request, [], protector);
            db.Workflows.Add(workflow);
            await db.SaveChangesAsync(ct);
            engine.Invalidate();

            await audit.LogAsync(ctx, "workflows", "workflow-created",
                targetType: "workflow", targetId: workflow.Id.ToString(), targetName: workflow.Name,
                detail: $"Creó la automatización '{workflow.Name}' ({TriggerLabel(workflow.TriggerType)}) con " +
                        $"{workflow.Actions.Count} acción(es): {ActionSummary(workflow, engine)}.",
                data: new { workflow.TriggerType, workflow.Enabled, Conditions = request.Conditions });
            return Results.Created($"/api/workflows/{workflow.Id}", WorkflowMapper.ToDto(workflow));
        });

        app.MapPut("/api/workflows/{id:int}", async (HttpContext ctx, int id, WorkflowWriteDto request, VmsDbContext db, LicenseService license,
            WorkflowEngine engine, CredentialProtector protector, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (Validate(request, engine) is { } invalid) return Error(invalid);

            var workflow = await db.Workflows.Include(w => w.Actions).FirstOrDefaultAsync(w => w.Id == id, ct);
            if (workflow is null) return Results.NotFound();
            if (await db.Workflows.AnyAsync(w => w.Name == request.Name.Trim() && w.Id != id, ct))
                return Error("Ya existe otra automatización con ese nombre.");

            string before = $"{(workflow.Enabled ? "activa" : "pausada")}, {workflow.Actions.Count} acción(es)";
            var previous = workflow.Actions.ToList();

            workflow.Name = request.Name.Trim();
            workflow.Description = Clean(request.Description);
            if (!workflow.Enabled && request.Enabled
                && license.Deny(LicenseFeatures.ModuleAutomation, LicenseFeatures.AutomationRules,
                    await db.Workflows.CountAsync(w => w.Enabled && w.Id != id, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "workflow", workflow.Name);
            workflow.Enabled = request.Enabled;
            workflow.TriggerType = request.TriggerType;
            workflow.ConditionsJson = WorkflowJson.Serialize(request.Conditions ?? new WorkflowConditionsDto());
            workflow.CooldownSeconds = Math.Clamp(request.CooldownSeconds, 0, 86_400);
            workflow.UpdatedAt = DateTime.UtcNow;

            // Las acciones se reemplazan completas; las contraseñas que el
            // panel no reenvía (nunca las recibe) se arrastran por Id.
            db.WorkflowActions.RemoveRange(previous);
            workflow.Actions.Clear();
            ApplyActions(workflow, request, previous, protector);

            await db.SaveChangesAsync(ct);
            engine.Invalidate();

            await audit.LogAsync(ctx, "workflows", "workflow-updated",
                targetType: "workflow", targetId: workflow.Id.ToString(), targetName: workflow.Name,
                detail: $"Modificó la automatización '{workflow.Name}': antes {before}; ahora " +
                        $"{(workflow.Enabled ? "activa" : "pausada")}, {workflow.Actions.Count} acción(es) " +
                        $"({ActionSummary(workflow, engine)}), disparador {TriggerLabel(workflow.TriggerType)}.",
                data: new { workflow.TriggerType, workflow.Enabled, Conditions = request.Conditions });
            return Results.Ok(WorkflowMapper.ToDto(workflow));
        });

        app.MapDelete("/api/workflows/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            WorkflowEngine engine, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var workflow = await db.Workflows.FindAsync([id], ct);
            if (workflow is null) return Results.NotFound();

            db.Workflows.Remove(workflow);
            await db.SaveChangesAsync(ct);
            engine.Invalidate();

            await audit.LogAsync(ctx, "workflows", "workflow-deleted",
                targetType: "workflow", targetId: id.ToString(), targetName: workflow.Name,
                detail: $"Eliminó la automatización '{workflow.Name}'. El historial de ejecuciones se conserva.");
            return Results.NoContent();
        });

        // Prueba manual: ejecuta las acciones DE VERDAD con un evento de ejemplo.
        app.MapPost("/api/workflows/{id:int}/test", async (HttpContext ctx, int id, WorkflowEngine engine,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out var session) is { } failure) return failure;
            var run = await engine.TestAsync(id, session.Username, ct);
            if (run is null) return Results.NotFound();

            await audit.LogAsync(ctx, "workflows", "workflow-tested",
                targetType: "workflow", targetId: id.ToString(), targetName: run.WorkflowName,
                detail: $"Probó a mano la automatización '{run.WorkflowName}': " +
                        (run.Success ? "todas las acciones se ejecutaron." : $"falló — {run.Error}"),
                success: run.Success, data: new { run.Steps });
            return Results.Ok(run);
        });

        // ------------------------------------------------------------------
        // Historial de ejecuciones
        // ------------------------------------------------------------------
        app.MapGet("/api/workflows/runs", async (HttpContext ctx, VmsDbContext db, AuditService audit,
            int? workflowId, bool? onlyErrors, int? skip, int? take, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;

            var query = db.WorkflowRuns.AsNoTracking().AsQueryable();
            if (workflowId is { } id) query = query.Where(r => r.WorkflowId == id);
            if (onlyErrors == true) query = query.Where(r => !r.Success);

            int total = await query.CountAsync(ct);
            var runs = await query.OrderByDescending(r => r.StartedAt)
                .Skip(Math.Max(skip ?? 0, 0))
                .Take(Math.Clamp(take ?? 50, 1, MaxTake))
                .ToListAsync(ct);

            // Consulta legítima pero repetitiva (la pantalla se refresca sola):
            // se registra una vez cada 10 minutos por usuario.
            if (audit.ShouldLog($"wf-runs:{session.UserId}", TimeSpan.FromMinutes(10)))
                await audit.LogAsync(ctx, "workflows", "search",
                    detail: "Consultó el historial de ejecuciones de automatizaciones.");

            return Results.Ok(new { total, items = runs.Select(r => WorkflowMapper.ToDto(r)) });
        });

        app.MapGet("/api/workflows/runs/{id:long}", async (HttpContext ctx, long id, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var run = await db.WorkflowRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            return run is null ? Results.NotFound() : Results.Ok(WorkflowMapper.ToDto(run));
        });

        // Foto capturada por una ejecución (la ruta viene del propio historial).
        app.MapGet("/api/workflows/files/{**path}", (HttpContext ctx, string path, WorkflowStore store) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            if (store.FullPath(path) is not { } full || !File.Exists(full)) return Results.NotFound();
            return Results.File(full, Path.GetExtension(full).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                ? "image/jpeg" : "application/octet-stream");
        });

        // ------------------------------------------------------------------
        // Alertas y acuse de recibo ("¿quién la vio y cuándo?")
        // ------------------------------------------------------------------
        app.MapGet("/api/workflows/alerts", async (HttpContext ctx, VmsDbContext db,
            bool? pending, int? skip, int? take, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;

            var query = db.WorkflowAlerts.AsNoTracking().AsQueryable();
            if (pending == true) query = query.Where(a => a.RequiresAck && a.AcknowledgedAt == null);

            int total = await query.CountAsync(ct);
            int pendingCount = await db.WorkflowAlerts
                .CountAsync(a => a.RequiresAck && a.AcknowledgedAt == null, ct);
            var alerts = await query.OrderByDescending(a => a.RaisedAt)
                .Skip(Math.Max(skip ?? 0, 0))
                .Take(Math.Clamp(take ?? 50, 1, MaxTake))
                .ToListAsync(ct);

            return Results.Ok(new WorkflowAlertListDto(total, pendingCount,
                alerts.Select(WorkflowMapper.ToDto).ToList()));
        });

        app.MapGet("/api/workflows/alerts/{id:long}", async (HttpContext ctx, long id, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var alert = await db.WorkflowAlerts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
            return alert is null ? Results.NotFound() : Results.Ok(WorkflowMapper.ToDto(alert));
        });

        // Darse por enterado. Cualquier usuario con sesión puede hacerlo (el
        // que esté frente al puesto); la PRIMERA confirmación es la que queda:
        // el registro no se puede reescribir ni "mejorar" después.
        app.MapPost("/api/workflows/alerts/{id:long}/ack", async (HttpContext ctx, long id, VmsDbContext db,
            IHubContext<VmsHub> hub, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;

            var alert = await db.WorkflowAlerts.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (alert is null) return Results.NotFound();

            if (alert.AcknowledgedAt is not null)
            {
                // Ya la tomó otro: no es un error, pero el registro no cambia.
                return Results.Ok(WorkflowMapper.ToDto(alert));
            }

            alert.AcknowledgedAt = DateTime.UtcNow;
            alert.AcknowledgedByUserId = session.UserId;
            alert.AcknowledgedBy = session.Username;
            alert.AcknowledgedFrom = ctx.Request.Headers.ContainsKey("X-TCVMS-Client") ? "client" : "web";
            alert.AcknowledgedIp = ctx.Connection.RemoteIpAddress?.ToString();
            await db.SaveChangesAsync(ct);

            var dto = WorkflowMapper.ToDto(alert);
            await hub.Clients.All.SendAsync(VmsHubContract.WorkflowAlertAcknowledged, dto, ct);

            await audit.LogAsync(ctx, "workflows", "alert-acknowledged",
                targetType: "workflow-alert", targetId: alert.Id.ToString(), targetName: alert.WorkflowName,
                detail: $"Se dio por enterado de la alerta «{alert.Title}» ({alert.TriggerSummary}), " +
                        $"emitida a las {alert.RaisedAt.ToLocalTime():dd-MM-yyyy HH:mm:ss}, " +
                        $"{dto.ResponseSeconds} s después de emitida.",
                data: new { alert.Id, alert.WorkflowId, alert.RaisedAt, dto.ResponseSeconds, alert.TriggerSummary });
            return Results.Ok(dto);
        });

        // ------------------------------------------------------------------
        // Servidor de correo saliente
        // ------------------------------------------------------------------
        app.MapGet("/api/workflows/smtp", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var settings = await db.SmtpSettings.AsNoTracking().OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
            return Results.Ok(settings is null
                ? new SmtpSettingsDto(false, "", 587, SmtpSecurity.StartTls, "", false, false, "", "CLR TrueCentral VMS", null)
                : WorkflowMapper.ToDto(settings));
        });

        app.MapPut("/api/workflows/smtp", async (HttpContext ctx, SmtpSettingsWriteDto request, VmsDbContext db,
            CredentialProtector protector, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (request.Enabled)
            {
                if (string.IsNullOrWhiteSpace(request.Host)) return Error("Indique el servidor de correo saliente.");
                if (request.Port is < 1 or > 65535) return Error("El puerto debe estar entre 1 y 65535.");
                if (string.IsNullOrWhiteSpace(request.FromAddress)) return Error("Indique la dirección del remitente.");
                if (SmtpSender.ParseAddresses(request.FromAddress).Count == 0)
                    return Error("La dirección del remitente no es válida.");
            }

            var settings = await db.SmtpSettings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
            if (settings is null)
            {
                settings = new SmtpSettings { Id = 1 };
                db.SmtpSettings.Add(settings);
            }
            settings.Enabled = request.Enabled;
            settings.Host = request.Host.Trim();
            settings.Port = request.Port;
            settings.Security = request.Security;
            settings.Username = request.Username.Trim();
            settings.AllowInvalidCertificate = request.AllowInvalidCertificate;
            settings.FromAddress = request.FromAddress.Trim();
            settings.FromName = string.IsNullOrWhiteSpace(request.FromName) ? "CLR TrueCentral VMS" : request.FromName.Trim();
            settings.UpdatedAt = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(request.Password))
                settings.PasswordCiphertext = protector.Protect(request.Password);
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "workflows", "smtp-updated",
                targetType: "smtp", targetName: settings.Host,
                detail: $"Configuró el correo saliente: {(settings.Enabled ? "habilitado" : "deshabilitado")}, " +
                        $"{settings.Host}:{settings.Port} ({settings.Security}), remitente '{settings.FromAddress}', " +
                        $"usuario '{settings.Username}'" +
                        (string.IsNullOrEmpty(request.Password) ? "." : " (contraseña actualizada)."));
            return Results.Ok(WorkflowMapper.ToDto(settings));
        });

        app.MapPost("/api/workflows/smtp/test", async (HttpContext ctx, SmtpTestRequestDto request, SmtpSender smtp,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out var session) is { } failure) return failure;
            if (string.IsNullOrWhiteSpace(request.To)) return Error("Indique a qué dirección enviar la prueba.");

            var settings = await smtp.LoadAsync(ct);
            if (settings is null || string.IsNullOrWhiteSpace(settings.Host))
                return Error("Primero guarde la configuración del servidor de correo.");

            string? error = await smtp.SendAsync(settings, request.To, null,
                "Prueba de correo de CLR TrueCentral VMS",
                $"""
                 Este es un correo de prueba enviado por CLR TrueCentral VMS.

                 Servidor: {Environment.MachineName}
                 Fecha y hora: {DateTime.Now:dd-MM-yyyy HH:mm:ss}
                 Solicitado por: {session.Username}

                 Si lo recibió, las automatizaciones ya pueden enviar avisos por correo.
                 """, null, ct);

            await audit.LogAsync(ctx, "workflows", "smtp-tested",
                targetType: "smtp", targetName: settings.Host,
                detail: error is null
                    ? $"Envió un correo de prueba a '{request.To}' por {settings.Host}:{settings.Port}."
                    : $"Falló el correo de prueba a '{request.To}' por {settings.Host}:{settings.Port}: {error}",
                success: error is null);

            return error is null ? Results.Ok(new { ok = true }) : Error(error);
        });

        // ------------------------------------------------------------------
        // Sonidos para los parlantes IP
        // ------------------------------------------------------------------
        app.MapGet("/api/workflows/audio", (HttpContext ctx, WorkflowStore store) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.Ok(store.ListAudio());
        });

        // El archivo en sí: lo descarga el cliente de escritorio para hacer
        // sonar la alarma en el equipo del operador.
        app.MapGet("/api/workflows/audio/{name}/file", (HttpContext ctx, string name, WorkflowStore store) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            if (store.FindAudio(name) is not { } path) return Results.NotFound();
            return Results.File(path, AudioContentType(path), Path.GetFileName(path));
        });

        app.MapPost("/api/workflows/audio", async (HttpContext ctx, IFormFile file, WorkflowStore store,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (file.Length == 0) return Error("El archivo está vacío.");
            if (file.Length > MaxAudioBytes) return Error("El sonido no puede pesar más de 8 MB.");

            await using var stream = file.OpenReadStream();
            string? warning = await store.SaveAudioAsync(file.FileName, stream, ct);

            await audit.LogAsync(ctx, "workflows", "audio-uploaded",
                targetType: "audio", targetName: file.FileName,
                detail: $"Subió el sonido '{file.FileName}' ({file.Length / 1024} kB) para los parlantes IP" +
                        (warning is null ? " y quedó convertido a G.711." : $". Advertencia: {warning}"),
                success: warning is null);

            return warning is null
                ? Results.Ok(new { ok = true, items = store.ListAudio() })
                : Error(warning);
        }).DisableAntiforgery();

        app.MapDelete("/api/workflows/audio/{name}", async (HttpContext ctx, string name, WorkflowStore store,
            AuditService audit) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (!store.DeleteAudio(name)) return Results.NotFound();
            await audit.LogAsync(ctx, "workflows", "audio-deleted",
                targetType: "audio", targetName: name,
                detail: $"Eliminó el sonido '{name}' de los parlantes IP.");
            return Results.NoContent();
        });
    }

    // ------------------------------------------------------------------
    // Validación y volcado de acciones
    // ------------------------------------------------------------------

    private static string? Validate(WorkflowWriteDto request, WorkflowEngine engine)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
            return "El nombre es obligatorio (máximo 128 caracteres).";
        if (!WorkflowTriggerTypes.All.Contains(request.TriggerType))
            return $"Disparador desconocido: '{request.TriggerType}'.";
        if (request.Actions is null || request.Actions.Count == 0)
            return "Agregue al menos una acción.";
        if (request.Actions.Count > MaxActions)
            return $"Una automatización admite hasta {MaxActions} acciones.";

        foreach (var action in request.Actions)
        {
            var executor = engine.FindExecutor(action.Type);
            if (executor is null) return $"Acción desconocida: '{action.Type}'.";
            if (action.DelaySeconds is < 0 or > 600) return "La espera de una acción debe estar entre 0 y 600 segundos.";
            if (action.Config.ValueKind != JsonValueKind.Object) return $"La configuración de la acción «{executor.Label}» no es válida.";
            if (executor.Validate(action.Config) is { } invalid) return $"«{executor.Label}»: {invalid}";
        }

        var conditions = request.Conditions;
        if (conditions is not null)
        {
            if (conditions.FromTime is { Length: > 0 } from && !TimeSpan.TryParse(from, out _))
                return "La hora de inicio de la ventana horaria no es válida (use hh:mm).";
            if (conditions.ToTime is { Length: > 0 } to && !TimeSpan.TryParse(to, out _))
                return "La hora de término de la ventana horaria no es válida (use hh:mm).";
            if (conditions.DaysOfWeek is { Count: > 0 } days && days.Any(d => d is < 0 or > 6))
                return "Los días de la semana deben ir de 0 (domingo) a 6 (sábado).";
            if (conditions.SustainedSeconds is < 0 or > 600)
                return "La condición sostenida debe estar entre 0 y 600 segundos.";
        }
        return null;
    }

    /// <summary>
    /// Vuelca las acciones del formulario sobre la entidad. Las contraseñas
    /// que el panel no reenvía se toman de la acción anterior con el mismo Id:
    /// editar un workflow no debe obligar a retipear la clave del FTP.
    /// </summary>
    private static void ApplyActions(Workflow workflow, WorkflowWriteDto request,
        IReadOnlyList<WorkflowAction> previous, CredentialProtector protector)
    {
        int order = 1;
        foreach (var action in request.Actions)
        {
            byte[]? secret = null;
            if (!string.IsNullOrEmpty(action.Secret))
                secret = protector.Protect(action.Secret);
            else if (action.Id > 0)
                secret = previous.FirstOrDefault(p => p.Id == action.Id)?.SecretCiphertext;

            workflow.Actions.Add(new WorkflowAction
            {
                Order = order++,
                Type = action.Type,
                Enabled = action.Enabled,
                ContinueOnError = action.ContinueOnError,
                DelaySeconds = action.DelaySeconds,
                ConfigJson = action.Config.GetRawText(),
                SecretCiphertext = secret,
            });
        }
    }

    private static string TriggerLabel(string triggerType) =>
        Triggers.FirstOrDefault(t => t.Key == triggerType)?.Label ?? triggerType;

    private static string ActionSummary(Workflow workflow, WorkflowEngine engine) =>
        string.Join(", ", workflow.Actions.OrderBy(a => a.Order)
            .Select(a => engine.FindExecutor(a.Type)?.Label ?? a.Type));

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string AudioContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        ".m4a" or ".aac" => "audio/mp4",
        _ => "application/octet-stream",
    };
}
