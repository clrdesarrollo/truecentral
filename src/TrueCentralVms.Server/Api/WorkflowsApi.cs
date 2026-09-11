using TrueCentralVms.Server.Services.Licensing;
using System.Security.Cryptography;
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
/// correo saliente, sonidos para los parlantes IP y la entrada de llamadas
/// externas (webhook).
///
/// El editor visual del panel se arma con /api/workflows/catalog: los
/// disparadores (con sus marcas), las acciones y las marcas comunes salen del
/// servidor, así una acción nueva aparece sin tocar el JavaScript.
/// </summary>
public static class WorkflowsApi
{
    private const int MaxTake = 200;
    private const int MaxActions = 20;
    private const long MaxAudioBytes = 8 * 1024 * 1024;

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    /// <summary>Marcas comunes a todos los disparadores.</summary>
    private static readonly WorkflowPlaceholderDto[] Placeholders =
    [
        new("workflow", "Nombre de la automatización"),
        new("evento", "Descripción del evento"),
        new("tipo", "Naturaleza del evento"),
        new("severidad", "Severidad (Crítica, Advertencia, Informativa)"),
        new("equipo", "Nombre del equipo (panel, cámara, terminal, parlante)"),
        new("origen", "Cómo se supo"),
        new("fecha", "Fecha (dd-mm-aaaa)"),
        new("hora", "Hora (hh:mm:ss)"),
        new("fechahora", "Fecha y hora"),
        new("servidor", "Nombre del servidor"),
    ];

    private static readonly WorkflowPlaceholderDto[] PanelPlaceholders =
    [
        new("panel", "Nombre del panel"),
        new("codigo", "Código del evento (ej. 1130)"),
        new("area", "Nombre del área"),
        new("areanumero", "Número del área"),
        new("zona", "Nombre de la zona"),
        new("zonanumero", "Número de la zona"),
        new("operador", "Usuario que operó el panel"),
        new("estado", "Estado de conexión del panel"),
    ];

    private static readonly WorkflowTriggerInfoDto[] Triggers =
    [
        new(WorkflowTriggerTypes.AlarmEvent, "Evento de panel de alarma",
            "Cuando un panel informa una alarma, un sensor interrumpido, un armado, una anulación o una falla.",
            PanelPlaceholders, "Paneles de alarma"),
        new(WorkflowTriggerTypes.PanelStatus, "Conexión con un panel",
            "Cuando el servidor pierde o recupera la conexión con un panel de alarma.",
            PanelPlaceholders, "Paneles de alarma"),
        new(WorkflowTriggerTypes.VideoEvent, "Evento de cámara (analítica)",
            "Cuando una cámara o grabador informa movimiento, cruce de línea, intrusión, pérdida de video, entrada de alarma, etc. Las reglas se configuran en el equipo.",
            [
                new("camara", "Nombre de la cámara (canal)"),
                new("canal", "Número del canal"),
                new("regla", "Nombre de la regla de analítica"),
                new("entrada", "Número de la entrada de alarma"),
                new("horaequipo", "Hora según el equipo"),
            ], "Video"),
        new(WorkflowTriggerTypes.PlateRecognized, "Lectura de patente",
            "Cuando una cámara ANPR lee una patente (lista blanca/negra, confianza mínima).",
            [
                new("patente", "Patente leída"),
                new("confianza", "Confianza de la lectura (0–100)"),
                new("camara", "Nombre de la cámara"),
                new("tipovehiculo", "Tipo de vehículo"),
                new("colorvehiculo", "Color del vehículo"),
                new("marca", "Marca del vehículo"),
                new("colorpatente", "Color de la placa"),
                new("velocidad", "Velocidad (km/h)"),
                new("carril", "Carril"),
                new("direccion", "Sentido (entrada/salida)"),
                new("infraccion", "Infracción informada"),
                new("horaequipo", "Hora según la cámara"),
            ], "Video"),
        new(WorkflowTriggerTypes.AccessEvent, "Evento de control de acceso",
            "Cuando alguien pasa (o lo rechazan) por una puerta, o la puerta informa forzada, mantenida abierta o sabotaje.",
            [
                new("puerta", "Nombre de la puerta"),
                new("puertanumero", "Número de la puerta en el equipo"),
                new("persona", "Nombre de la persona"),
                new("personaid", "Identificador de la persona"),
                new("tarjeta", "Número de tarjeta"),
                new("credencial", "Credencial usada (tarjeta, huella, rostro...)"),
                new("resultado", "Resultado (concedido, denegado, alarma...)"),
            ], "Control de acceso"),
        new(WorkflowTriggerTypes.DeviceStatus, "Conexión de un equipo",
            "Cuando una cámara/grabador, un terminal de acceso o un parlante IP pierde o recupera la conexión.",
            [
                new("equipotipo", "Clase de equipo"),
                new("direccion", "Dirección IP del equipo"),
                new("modelo", "Modelo"),
                new("estado", "Estado de conexión"),
            ], "Sistema"),
        new(WorkflowTriggerTypes.Schedule, "Horario programado",
            "A las horas indicadas, los días de la semana marcados (armar a las 22:00, abrir el portón a las 07:30…).",
            [], "Sistema"),
        new(WorkflowTriggerTypes.Webhook, "Llamada externa (HTTP)",
            "Cuando otro sistema llama a la URL de esta automatización (POST /api/workflows/hook/{clave}). Los campos del cuerpo JSON quedan como marcas.",
            [
                new("cuerpo", "Cuerpo JSON completo de la llamada"),
                new("ip", "Dirección IP que llamó"),
            ], "Sistema"),
    ];

    private static string ActionGroup(string type) => type switch
    {
        WorkflowActionTypes.Snapshot => "Capturar",
        WorkflowActionTypes.Notify or WorkflowActionTypes.Email or WorkflowActionTypes.Speaker => "Avisar",
        WorkflowActionTypes.Door or WorkflowActionTypes.Panel or WorkflowActionTypes.PtzPreset => "Equipos",
        _ => "Integración",
    };

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
                engine.Executors.Select(e => new WorkflowActionInfoDto(e.Type, e.Label, e.Description, e.UsesSecret, ActionGroup(e.Type))).ToList(),
                Placeholders,
                settings is { Enabled: true, Host.Length: > 0 },
                MediaMtxManager.LocateFfmpeg() is not null));
        });

        // Cámaras elegibles en la acción "capturar foto" y en los filtros por
        // canal (lista plana: el editor no debería pedir los canales equipo
        // por equipo).
        app.MapGet("/api/workflows/cameras", async (HttpContext ctx, VmsDbContext db, DriverRegistry drivers,
            CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var channels = await db.Channels.AsNoTracking().Include(c => c.Device)
                .Where(c => c.Enabled)
                .OrderBy(c => c.Device.Name).ThenBy(c => c.ChannelNumber)
                .Select(c => new { c.Id, c.DeviceId, DeviceName = c.Device.Name, c.Name, c.Device.DriverKey, c.SupportsPtz, c.ChannelNumber })
                .ToListAsync(ct);
            return Results.Ok(channels.Select(c => new
            {
                channelId = c.Id,
                deviceId = c.DeviceId,
                deviceName = c.DeviceName,
                channelName = c.Name,
                // Para la vista previa del editor (GET /api/devices/{id}/snapshot/{channelNumber}).
                channelNumber = c.ChannelNumber,
                supportsSnapshot = drivers.Find(c.DriverKey)?.Capabilities.SupportsSnapshot ?? false,
                supportsPtz = c.SupportsPtz,
            }));
        });

        // Equipos de video (cámaras/grabadores) para los disparadores de
        // conexión, analítica y patentes.
        app.MapGet("/api/workflows/devices", async (HttpContext ctx, VmsDbContext db, DriverRegistry drivers,
            CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var devices = await db.Devices.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
            return Results.Ok(devices.Select(d =>
            {
                var caps = drivers.Find(d.DriverKey)?.Capabilities;
                return new WorkflowDeviceDto(d.Id, d.Name, d.DriverKey, caps?.SupportsEvents ?? false,
                    caps?.SupportsAnpr ?? false, d.AnprEnabled);
            }));
        });

        // Puertas del control de acceso (acción "orden a una puerta" y filtros).
        app.MapGet("/api/workflows/doors", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var doors = await db.AccessDoors.AsNoTracking().Include(d => d.AccessDevice)
                .OrderBy(d => d.AccessDevice!.Name).ThenBy(d => d.Number).ToListAsync(ct);
            return Results.Ok(doors.Select(d => new WorkflowDoorDto(d.Id, d.AccessDeviceId, d.AccessDevice!.Name, d.Number, d.Name, d.Enabled)));
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
                ConditionsJson = WorkflowJson.Serialize(NormalizeConditions(request)),
                CooldownSeconds = Math.Clamp(request.CooldownSeconds, 0, 86_400),
                CreatedBy = session.Username,
            };
            Apply(workflow, request, [], protector);
            db.Workflows.Add(workflow);
            await db.SaveChangesAsync(ct);
            engine.Invalidate();

            await audit.LogAsync(ctx, "workflows", "workflow-created",
                targetType: "workflow", targetId: workflow.Id.ToString(), targetName: workflow.Name,
                detail: $"Creó la automatización '{workflow.Name}' ({TriggerLabel(workflow.TriggerType)}) con " +
                        $"{workflow.Actions.Count} acción(es): {ActionSummary(workflow, engine)}" +
                        (request.Graph is not null ? $"; diagrama de {request.Graph.Nodes.Count} pasos." : "."),
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
            workflow.ConditionsJson = WorkflowJson.Serialize(NormalizeConditions(request, WorkflowJson.Conditions(workflow.ConditionsJson)));
            workflow.CooldownSeconds = Math.Clamp(request.CooldownSeconds, 0, 86_400);
            workflow.UpdatedAt = DateTime.UtcNow;

            // Las acciones se reemplazan completas; las contraseñas que el
            // panel no reenvía (nunca las recibe) se arrastran por Id.
            db.WorkflowActions.RemoveRange(previous);
            workflow.Actions.Clear();
            Apply(workflow, request, previous, protector);

            await db.SaveChangesAsync(ct);
            engine.Invalidate();

            await audit.LogAsync(ctx, "workflows", "workflow-updated",
                targetType: "workflow", targetId: workflow.Id.ToString(), targetName: workflow.Name,
                detail: $"Modificó la automatización '{workflow.Name}': antes {before}; ahora " +
                        $"{(workflow.Enabled ? "activa" : "pausada")}, {workflow.Actions.Count} acción(es) " +
                        $"({ActionSummary(workflow, engine)}), disparador {TriggerLabel(workflow.TriggerType)}" +
                        (request.Graph is not null ? $", diagrama de {request.Graph.Nodes.Count} pasos." : "."),
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

        // Duplicar: una copia completa (diagrama, filtro, acciones y sus
        // contraseñas) que nace PAUSADA con "(copia)" en el nombre, para
        // armar una automatización parecida cambiando solo la zona o la cámara.
        app.MapPost("/api/workflows/{id:int}/duplicate", async (HttpContext ctx, int id, VmsDbContext db, LicenseService license,
            WorkflowEngine engine, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out var session) is { } failure) return failure;
            var source = await db.Workflows.AsNoTracking().Include(w => w.Actions).FirstOrDefaultAsync(w => w.Id == id, ct);
            if (source is null) return Results.NotFound();
            if (license.Deny(LicenseFeatures.ModuleAutomation, null, 0) is { } denied)
                return await license.DenyAsync(ctx, denied, "workflow", source.Name);

            string baseName = $"{source.Name} (copia)";
            string name = baseName;
            for (int n = 2; await db.Workflows.AnyAsync(w => w.Name == name, ct); n++)
                name = $"{baseName} {n}";
            if (name.Length > 128) return Error("El nombre de la copia supera los 128 caracteres: acorte el original.");

            // La clave de llamada externa es una credencial: la copia recibe una propia.
            var conditions = WorkflowJson.Conditions(source.ConditionsJson);
            if (source.TriggerType == WorkflowTriggerTypes.Webhook)
                conditions = conditions with { HookKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20)) };

            var copy = new Workflow
            {
                Name = name,
                Description = source.Description,
                Enabled = false,
                TriggerType = source.TriggerType,
                ConditionsJson = WorkflowJson.Serialize(conditions),
                GraphJson = source.GraphJson,
                CooldownSeconds = source.CooldownSeconds,
                CreatedBy = session.Username,
            };
            foreach (var action in source.Actions.OrderBy(a => a.Order))
                copy.Actions.Add(new WorkflowAction
                {
                    Order = action.Order,
                    NodeId = action.NodeId,
                    Type = action.Type,
                    Enabled = action.Enabled,
                    ContinueOnError = action.ContinueOnError,
                    DelaySeconds = action.DelaySeconds,
                    ConfigJson = action.ConfigJson,
                    SecretCiphertext = action.SecretCiphertext,
                });
            db.Workflows.Add(copy);
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "workflows", "workflow-duplicated",
                targetType: "workflow", targetId: copy.Id.ToString(), targetName: copy.Name,
                detail: $"Duplicó la automatización '{source.Name}' como '{copy.Name}' (pausada, {copy.Actions.Count} acción(es)).",
                data: new { SourceId = source.Id, source.TriggerType });
            return Results.Created($"/api/workflows/{copy.Id}", WorkflowMapper.ToDto(copy));
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
                        (run.Success ? "todos los pasos se ejecutaron." : $"falló — {run.Error}"),
                success: run.Success, data: new { run.Steps });
            return Results.Ok(run);
        });

        // Clave nueva para una automatización de llamada externa (el editor la
        // pide al elegir ese disparador; se guarda con las condiciones).
        app.MapPost("/api/workflows/hook-key", (HttpContext ctx) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            return Results.Ok(new { key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20)) });
        });

        // ------------------------------------------------------------------
        // Llamada externa (webhook): otro sistema dispara la automatización.
        // Sin sesión: la clave de la URL es la credencial (40 hex, generada
        // por el servidor). El cuerpo JSON, si viene, aporta marcas.
        // ------------------------------------------------------------------
        app.MapPost("/api/workflows/hook/{key}", async (HttpContext ctx, string key, WorkflowEngine engine,
            AuditService audit, CancellationToken ct) =>
        {
            string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
            var workflow = key.Length is >= 20 and <= 64 && key.All(Uri.IsHexDigit)
                ? await engine.FindByHookKeyAsync(key, ct)
                : null;
            if (workflow is null)
            {
                // Se registra (acelerado por IP) para que un escaneo de claves
                // quede en la bitácora sin inundarla.
                if (audit.ShouldLog($"wf-hook-bad:{ip}", TimeSpan.FromMinutes(1)))
                    await audit.LogSystemAsync("workflows", "workflow-webhook-rejected",
                        detail: $"Llamada externa rechazada desde {ip}: clave desconocida o automatización pausada.",
                        success: false, clientIp: ip, origin: "server");
                return Results.NotFound();
            }

            JsonElement? body = null;
            if (ctx.Request.ContentLength is > 0 and <= 64 * 1024)
            {
                try
                {
                    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                    body = doc.RootElement.Clone();
                }
                catch (JsonException) { return Error("El cuerpo de la llamada no es JSON válido."); }
            }
            else if (ctx.Request.ContentLength > 64 * 1024)
            {
                return Error("El cuerpo de la llamada no puede superar los 64 kB.");
            }

            engine.Publish(WorkflowTrigger.FromWebhook(workflow, body, ip));
            await audit.LogSystemAsync("workflows", "workflow-webhook",
                targetType: "workflow", targetId: workflow.Id.ToString(), targetName: workflow.Name,
                detail: $"Llamada externa desde {ip} disparó la automatización '{workflow.Name}'.",
                clientIp: ip, origin: "server");
            return Results.Accepted(value: new { ok = true, workflow = workflow.Name });
        }).DisableAntiforgery();

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
    // Validación y volcado
    // ------------------------------------------------------------------

    private static string? Validate(WorkflowWriteDto request, WorkflowEngine engine)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
            return "El nombre es obligatorio (máximo 128 caracteres).";
        if (!WorkflowTriggerTypes.All.Contains(request.TriggerType))
            return $"Disparador desconocido: '{request.TriggerType}'.";

        if (request.Graph is not null)
        {
            if (WorkflowGraph.Validate(request.Graph, engine) is { } bad) return bad;
        }
        else
        {
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
        }

        var conditions = request.Conditions;
        if (conditions is not null && WorkflowGraph.ValidateConditions(conditions) is { } invalidCondition)
            return char.ToUpperInvariant(invalidCondition[0]) + invalidCondition[1..];

        if (request.TriggerType == WorkflowTriggerTypes.Schedule &&
            (conditions?.ScheduleTimes is not { Count: > 0 }))
            return "Indique al menos una hora en que debe ejecutarse.";
        return null;
    }

    /// <summary>
    /// Condiciones a guardar. En las automatizaciones de llamada externa la
    /// clave se conserva si el editor no la manda (nunca se reescribe sola).
    /// </summary>
    private static WorkflowConditionsDto NormalizeConditions(WorkflowWriteDto request, WorkflowConditionsDto? current = null)
    {
        var conditions = request.Conditions ?? new WorkflowConditionsDto();
        if (request.TriggerType == WorkflowTriggerTypes.Webhook)
        {
            string? key = string.IsNullOrWhiteSpace(conditions.HookKey) ? current?.HookKey : conditions.HookKey.Trim();
            if (string.IsNullOrWhiteSpace(key)) key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
            conditions = conditions with { HookKey = key };
        }
        else if (conditions.HookKey is not null)
        {
            conditions = conditions with { HookKey = null };
        }
        return conditions;
    }

    /// <summary>Vuelca las acciones: desde el diagrama si viene, o la lista lineal (clientes antiguos).</summary>
    private static void Apply(Workflow workflow, WorkflowWriteDto request,
        IReadOnlyList<WorkflowAction> previous, CredentialProtector protector)
    {
        if (request.Graph is not null)
        {
            WorkflowGraph.Apply(workflow, request.Graph, previous, protector);
            return;
        }

        workflow.GraphJson = null;
        int order = 1;
        foreach (var action in request.Actions ?? [])
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
