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
using TrueCentralVms.Core.Domain;

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

    /// <summary>
    /// Alertas que puede ver una sesión: todas para el administrador; para el
    /// resto, las dirigidas a todos (sin destinatarios) o las que lo incluyen
    /// (los ids se guardan como ",3,5," justamente para buscar ",id,"). Con
    /// alcance por ubicación, una alerta para todos se ve solo si el recurso
    /// que la originó está en su alcance (o no tiene recurso); la dirigida a
    /// él la ve igual: la eligió quien configuró la automatización.
    /// </summary>
    private static async Task<IQueryable<WorkflowAlert>> VisibleAlertsAsync(HttpContext ctx, VmsDbContext db, SessionInfo session)
    {
        var alerts = db.WorkflowAlerts.AsNoTracking();
        if (session.Role == Core.Domain.Roles.Admin) return alerts;
        string me = $",{session.UserId},";
        var scope = await ctx.ScopeAsync(session);
        if (!scope.FiltersView)
            return alerts.Where(a => a.RecipientUserIds == null || a.RecipientUserIds.Contains(me));
        var keys = scope.VisibleResourceKeys();
        return alerts.Where(a => (a.RecipientUserIds != null && a.RecipientUserIds.Contains(me)) ||
                                 (a.RecipientUserIds == null && (a.ResourceKey == null || keys.Contains(a.ResourceKey))));
    }

    /// <summary>La alerta tal como la ve el usuario: sin cámaras fuera de su alcance (no podría abrirlas).</summary>
    private static WorkflowAlertDto ScopedDto(WorkflowAlert alert, UserScope scope)
    {
        var dto = WorkflowMapper.ToDto(alert);
        return scope.FiltersView ? dto with { ChannelIds = dto.ChannelIds.Where(id => scope.CanViewChannel(id)).ToList() } : dto;
    }

    /// <summary>
    /// Quién recibe una alerta por el hub: los destinatarios elegidos o, si es
    /// para todos, quien atiende alertas (permiso de su rol) y ve su recurso.
    /// </summary>
    public static bool ReceivesAlert(UserScope scope, string? recipientUserIds, string? resourceKey) =>
        scope.IsAdmin
        || (recipientUserIds is not null
            ? recipientUserIds.Contains($",{scope.UserId},")
            : scope.Has(Permissions.EventsAttend) && scope.CanViewResourceKey(resourceKey));

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
                new("linea", "Número de la línea/regla en la cámara (1, 2...)"),
                new("entrada", "Número de la entrada de alarma"),
                new("horaequipo", "Hora según el equipo"),
            ], "Video"),
        new(WorkflowTriggerTypes.PlateRecognized, "Lectura de patente",
            "Cuando una cámara ANPR lee una patente (padrón de vehículos, lista blanca/negra, confianza mínima).",
            [
                new("patente", "Patente leída"),
                new("padron", "Estado en el padrón (Autorizada, No autorizada, No registrada)"),
                new("motivopadron", "Por qué no está autorizada"),
                new("persona", "Dueño del vehículo (padrón)"),
                new("empleado", "Identificador del dueño"),
                new("departamento", "Departamento del dueño"),
                new("vehiculo", "Nombre del vehículo en el padrón"),
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
            "Cuando una cámara/grabador, un terminal de acceso, un parlante IP o un módulo de E/S pierde o recupera la conexión.",
            [
                new("equipotipo", "Clase de equipo"),
                new("direccion", "Dirección IP del equipo"),
                new("modelo", "Modelo"),
                new("estado", "Estado de conexión"),
            ], "Sistema"),
        new(WorkflowTriggerTypes.IoInput, "Entrada de módulo de E/S",
            "Cuando una entrada de un módulo de E/S se activa o vuelve a reposo: pulsador, lazo detector de la barrera, sensor de puerta.",
            [
                new("modulo", "Nombre del módulo"),
                new("entrada", "Nombre de la entrada"),
                new("entradanumero", "Rótulo de la entrada en el módulo (DI0, DI1...)"),
                new("estado", "Activa o En reposo"),
                new("direccion", "Dirección IP del módulo"),
            ], "Entradas y salidas"),
        new(WorkflowTriggerTypes.PatrolEvent, "Evento de rondas",
            "Cuando una ronda no se inicia a tiempo, queda sin terminar o incompleta, se cumple o empieza; cuando la memoria de un bastón supera el 80 % o una base de descarga deja de responder.",
            [
                new("ruta", "Nombre de la ruta"),
                new("detalle", "Detalle (causa, puntos no marcados, bastón)"),
                new("ubicacion", "Ubicación de la ruta"),
                new("horaprogramada", "Hora programada de la ronda (HH:mm)"),
                new("guardias", "Guardias que la hicieron"),
            ], "Control de rondas"),
        new(WorkflowTriggerTypes.FleetEvent, "Evento de vehículo (Flota)",
            "Cuando un vehículo de la flota informa pánico, choque, exceso de velocidad o conducción brusca, entra o sale de una geocerca, o se conecta o desconecta.",
            [
                new("vehiculo", "Código del vehículo"),
                new("patente", "Patente del vehículo"),
                new("velocidad", "Velocidad al momento (km/h)"),
                new("latitud", "Latitud"),
                new("longitud", "Longitud"),
                new("canal", "Cámara o entrada del grabador"),
            ], "Flota"),
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
        WorkflowActionTypes.Door or WorkflowActionTypes.Panel or WorkflowActionTypes.PtzPreset or WorkflowActionTypes.Relay => "Equipos",
        WorkflowActionTypes.VehicleAccess => "Registrar",
        _ => "Integración",
    };

    public static void MapWorkflowsApi(this WebApplication app)
    {
        // ------------------------------------------------------------------
        // Catálogo para el editor
        // ------------------------------------------------------------------
        app.MapGet("/api/workflows/catalog", async (HttpContext ctx, WorkflowEngine engine, SmtpSender smtp,
            VmsDbContext db, AlarmScreenService screens, LicenseService license, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsView, out _) is { } failure) return failure;
            var settings = await smtp.LoadAsync(ct);
            // Destinatarios posibles de un aviso (solo nombre y rol: nada sensible).
            var users = await db.Users.AsNoTracking().OrderBy(u => u.Username)
                .Select(u => new WorkflowUserDto(u.Id, u.Username, u.Role, u.Enabled)).ToListAsync(ct);
            // Pantallas de alarma donde se puede mostrar un aviso (las emparejadas).
            var alarmScreens = (await db.AlarmScreens.AsNoTracking()
                    .Where(x => x.Status == AlarmScreenStatus.Approved || x.Status == AlarmScreenStatus.Disabled)
                    .OrderBy(x => x.Name).Select(x => new { x.Id, x.Name, x.Status }).ToListAsync(ct))
                .Select(x => new AlarmScreenOptionDto(x.Id, x.Name, screens.IsOnline(x.Id), x.Status == AlarmScreenStatus.Approved))
                .ToList();
            return Results.Ok(new WorkflowCatalogDto(
                // «Evento de vehículo» solo con el módulo Flota en la licencia.
                Triggers.Where(t => (t.Key != WorkflowTriggerTypes.FleetEvent || license.IsModuleEnabled(LicenseFeatures.ModuleFleet))
                    && (t.Key != WorkflowTriggerTypes.PatrolEvent || license.IsModuleEnabled(LicenseFeatures.ModulePatrol))).ToArray(),
                engine.Executors.Select(e => new WorkflowActionInfoDto(e.Type, e.Label, e.Description, e.UsesSecret, ActionGroup(e.Type))).ToList(),
                Placeholders,
                settings is { Enabled: true, Host.Length: > 0 },
                MediaMtxManager.LocateFfmpeg() is not null,
                users,
                alarmScreens,
                license.IsModuleEnabled(LicenseFeatures.ModuleAlarmScreens)));
        });

        // Cámaras elegibles en la acción "capturar foto" y en los filtros por
        // canal (lista plana: el editor no debería pedir los canales equipo
        // por equipo).
        app.MapGet("/api/workflows/cameras", async (HttpContext ctx, VmsDbContext db, DriverRegistry drivers,
            CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsView, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var channels = await db.Channels.AsNoTracking().Include(c => c.Device)
                .Where(c => c.Enabled)
                .OrderBy(c => c.Device.Name).ThenBy(c => c.ChannelNumber)
                .Select(c => new { c.Id, c.DeviceId, DeviceName = c.Device.Name, c.Name, c.Device.DriverKey, c.SupportsPtz, c.ChannelNumber })
                .ToListAsync(ct);
            return Results.Ok(channels.Where(c => scope.CanViewChannel(c.Id)).Select(c => new
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

        // Líneas/regiones de la analítica que admiten las cámaras elegidas en
        // el disparador "evento de cámara": se leen del propio equipo, así el
        // editor ofrece tantas como su capacidad (y marca las dibujadas).
        // known = false: ningún equipo lo informó (el editor usa una lista genérica).
        app.MapGet("/api/workflows/analytics-rules", async (HttpContext ctx, string? channelIds, string? deviceIds,
            string? kinds, VmsDbContext db, DriverRegistry drivers, CredentialProtector protector, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsView, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            static List<int> Ids(string? csv) => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => int.TryParse(v, out int n) ? n : 0).Where(n => n > 0).Distinct().ToList();
            var channelList = Ids(channelIds);
            var deviceList = Ids(deviceIds);
            var kindList = (kinds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(k => Enum.TryParse<VideoEventKind>(k, true, out var kind) ? kind : (VideoEventKind?)null)
                .OfType<VideoEventKind>().Distinct().ToList();
            if (kindList.Count == 0) kindList = [VideoEventKind.LineCrossing];

            var channels = await db.Channels.AsNoTracking().Include(c => c.Device)
                .Where(c => c.Enabled && (channelList.Contains(c.Id) || deviceList.Contains(c.DeviceId)))
                .OrderBy(c => c.Id).Take(16).ToListAsync(ct);
            channels = channels.Where(c => scope.CanViewChannel(c.Id)).ToList();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var lookups = channels.SelectMany(c => kindList.Select(async kind =>
            {
                if (drivers.Find(c.Device.DriverKey) is not { } factory) return null;
                try
                {
                    var connection = DeviceConnections.Of(c.Device, protector);
                    return await factory.Create().GetAnalyticsRulesAsync(connection,
                        c.RtspChannel > 0 ? c.RtspChannel : c.ChannelNumber, kind, timeout.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
            }));
            var found = (await Task.WhenAll(lookups)).OfType<IReadOnlyList<AnalyticsRuleInfo>>().ToList();

            // Varias cámaras o tipos: la capacidad mayor; una línea cuenta como
            // dibujada si lo está en alguna de ellas.
            var rules = found.SelectMany(r => r).GroupBy(r => r.Id).OrderBy(g => g.Key)
                .Select(g => new { id = g.Key, configured = g.Any(r => r.Configured), enabled = g.Any(r => r.Enabled) })
                .ToList();
            return Results.Ok(new { known = found.Count > 0, rules });
        });

        // Equipos de video (cámaras/grabadores) para los disparadores de
        // conexión, analítica y patentes.
        app.MapGet("/api/workflows/devices", async (HttpContext ctx, VmsDbContext db, DriverRegistry drivers,
            CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsView, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var devices = await db.Devices.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
            return Results.Ok(devices.Where(d => scope.CanViewDevice(d.Id)).Select(d =>
            {
                // La cámara de un terminal de acceso sirve para «Conexión de un equipo», no para analíticas ni patentes.
                var caps = d.AccessDeviceId is null ? drivers.Find(d.DriverKey)?.Capabilities : null;
                return new WorkflowDeviceDto(d.Id, d.Name, d.DriverKey, caps?.SupportsEvents ?? false,
                    caps?.SupportsAnpr ?? false, d.AnprEnabled);
            }));
        });

        // Puertas del control de acceso (acción "orden a una puerta" y filtros).
        app.MapGet("/api/workflows/doors", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsView, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var doors = await db.AccessDoors.AsNoTracking().Include(d => d.AccessDevice)
                .OrderBy(d => d.AccessDevice!.Name).ThenBy(d => d.Number).ToListAsync(ct);
            return Results.Ok(doors.Where(d => scope.CanViewDoor(d.Id)).Select(d => new WorkflowDoorDto(d.Id, d.AccessDeviceId, d.AccessDevice!.Name, d.Number, d.Name, d.Enabled)));
        });

        // Parlantes elegibles en la acción "sonar parlante IP" (inventario del módulo Parlantes).
        app.MapGet("/api/workflows/speakers", async (HttpContext ctx, VmsDbContext db, SpeakerService service, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsView, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var speakers = await db.Speakers.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
            return Results.Ok(speakers.Where(s => scope.CanViewSpeaker(s.Id)).Select(s => new WorkflowSpeakerDto(s.Id, s.Name, service.GroupsOf(s.Id), s.Enabled,
                s.SupportsLibrary, s.SupportsTts, s.SupportsLiveAudio)));
        });

        // Grupos de parlantes elegibles en la misma acción (se guardan por nombre).
        app.MapGet("/api/workflows/speaker-groups", (HttpContext ctx, SpeakerService service) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsView, out _) is { } failure) return failure;
            return Results.Ok(service.Groups);
        });

        // Módulos de E/S con sus entradas y salidas (acción "operar relé" y disparador de entradas).
        app.MapGet("/api/workflows/io-modules", async (HttpContext ctx, VmsDbContext db, IoModuleService io, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsView, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var modules = await db.IoModules.AsNoTracking().Include(m => m.Points).OrderBy(m => m.Name).ToListAsync(ct);
            return Results.Ok(modules.Where(m => scope.CanViewIoModule(m.Id)).Select(m =>
            {
                var dto = io.ToDto(m);
                return new WorkflowIoModuleDto(m.Id, m.Name, m.Enabled,
                    dto.Inputs.Select(p => new WorkflowIoPointDto(p.Number, p.Code, p.Name)).ToList(),
                    dto.Outputs.Select(p => new WorkflowIoPointDto(p.Number, p.Code, p.Name)).ToList());
            }));
        });

        // Vehículos y geocercas para el disparador «Evento de vehículo» (vacío sin el módulo Flota en la licencia).
        app.MapGet("/api/workflows/fleet", async (HttpContext ctx, VmsDbContext db, LicenseService license, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsView, out var session) is { } failure) return failure;
            if (!license.IsModuleEnabled(LicenseFeatures.ModuleFleet))
                return Results.Ok(new { vehicles = Array.Empty<object>(), geofences = Array.Empty<object>() });
            var scope = await ctx.ScopeAsync(session);
            var vehicles = (await db.Vehicles.AsNoTracking().OrderBy(v => v.Code).Select(v => new { v.Id, v.Code, v.Plate, v.DeviceId }).ToListAsync(ct))
                .Where(v => scope.CanViewDevice(v.DeviceId))
                .Select(v => new { v.Id, name = v.Plate is { Length: > 0 } ? $"{v.Code} · {v.Plate}" : v.Code });
            var geofences = await db.Geofences.AsNoTracking().OrderBy(f => f.Name).Select(f => new { f.Id, name = f.Name }).ToListAsync(ct);
            return Results.Ok(new { vehicles, geofences });
        });

        // ------------------------------------------------------------------
        // Mantenedor de automatizaciones
        // ------------------------------------------------------------------
        app.MapGet("/api/workflows", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsView, out _) is { } failure) return failure;
            var workflows = await db.Workflows.AsNoTracking().Include(w => w.Actions)
                .OrderBy(w => w.Name).ToListAsync(ct);
            bool showHookKey = ApiSecurity.Has(ctx, Permissions.WorkflowsManage);
            return Results.Ok(workflows.Select(w => showHookKey ? WorkflowMapper.ToDto(w) : WorkflowMapper.WithoutHookKey(WorkflowMapper.ToDto(w))));
        });

        app.MapGet("/api/workflows/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsView, out _) is { } failure) return failure;
            var workflow = await db.Workflows.AsNoTracking().Include(w => w.Actions)
                .FirstOrDefaultAsync(w => w.Id == id, ct);
            if (workflow is null) return Results.NotFound();
            // La clave del webhook es su credencial: solo la ve quien puede administrar automatizaciones.
            var dto = WorkflowMapper.ToDto(workflow);
            return Results.Ok(ApiSecurity.Has(ctx, Permissions.WorkflowsManage) ? dto : WorkflowMapper.WithoutHookKey(dto));
        });

        app.MapPost("/api/workflows", async (HttpContext ctx, WorkflowWriteDto request, VmsDbContext db, LicenseService license,
            WorkflowEngine engine, CredentialProtector protector, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsManage, out var session) is { } failure) return failure;
            if (Validate(request, engine) is { } invalid) return Error(invalid);
            if (await OutOfScopeAsync(ctx, WorkflowResourceScope.References(request), db, audit, request.Name.Trim(), "crear", ct) is { } outOfScope)
                return outOfScope;
            if (await db.Workflows.AnyAsync(w => w.Name == request.Name.Trim(), ct))
                return Error("Ya existe una automatización con ese nombre.");
            if (license.Deny(LicenseFeatures.ModuleAutomation, request.Enabled ? LicenseFeatures.AutomationRules : null,
                    await db.Workflows.CountAsync(w => w.Enabled, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "workflow", request.Name.Trim());

            var workflow = new Workflow
            {
                Name = request.Name.Trim(),
                Description = Clean(request.Description),
                GroupName = CleanGroup(request.Group),
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
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsManage, out _) is { } failure) return failure;
            if (Validate(request, engine) is { } invalid) return Error(invalid);

            var workflow = await db.Workflows.Include(w => w.Actions).FirstOrDefaultAsync(w => w.Id == id, ct);
            if (workflow is null) return Results.NotFound();
            // Solo lo que se AGREGA: lo que ya tenía (p. ej. armado por un administrador) se puede conservar.
            var added = WorkflowResourceScope.References(request);
            added.ExceptWith(WorkflowResourceScope.References(workflow.Actions));
            if (await OutOfScopeAsync(ctx, added, db, audit, workflow.Name, "modificar", ct) is { } outOfScope)
                return outOfScope;
            if (await db.Workflows.AnyAsync(w => w.Name == request.Name.Trim() && w.Id != id, ct))
                return Error("Ya existe otra automatización con ese nombre.");

            string before = $"{(workflow.Enabled ? "activa" : "pausada")}, {workflow.Actions.Count} acción(es)";
            var previous = workflow.Actions.ToList();

            workflow.Name = request.Name.Trim();
            workflow.Description = Clean(request.Description);
            string? groupBefore = workflow.GroupName;
            if (request.Group is not null) workflow.GroupName = CleanGroup(request.Group);
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
                        (request.Graph is not null ? $", diagrama de {request.Graph.Nodes.Count} pasos" : "") +
                        (groupBefore != workflow.GroupName ? $"; grupo {GroupLabel(groupBefore)} → {GroupLabel(workflow.GroupName)}." : "."),
                data: new { workflow.TriggerType, workflow.Enabled, Conditions = request.Conditions, Group = workflow.GroupName });
            return Results.Ok(WorkflowMapper.ToDto(workflow));
        });

        // Grupos: solo ordenan el listado del panel (el motor no los mira).
        app.MapPost("/api/workflows/group", async (HttpContext ctx, WorkflowGroupAssignDto request, VmsDbContext db,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsManage, out _) is { } failure) return failure;
            var ids = (request.Ids ?? []).Distinct().ToList();
            if (ids.Count == 0) return Error("Elija al menos una automatización.");
            string? group = CleanGroup(request.Group);
            var workflows = await db.Workflows.Where(w => ids.Contains(w.Id)).ToListAsync(ct);
            if (workflows.Count == 0) return Results.NotFound();
            foreach (var w in workflows) { w.GroupName = group; w.UpdatedAt = DateTime.UtcNow; }
            await db.SaveChangesAsync(ct);
            string names = string.Join(", ", workflows.Select(w => $"'{w.Name}'").Take(10)) + (workflows.Count > 10 ? "…" : "");
            await audit.LogAsync(ctx, "workflows", "workflow-group-changed",
                targetType: "workflow", targetId: string.Join(",", workflows.Select(w => w.Id)),
                targetName: group ?? "Sin grupo",
                detail: $"Movió {workflows.Count} automatización(es) a {GroupLabel(group)}: {names}.",
                data: new { Ids = workflows.Select(w => w.Id), Group = group });
            return Results.Ok(new { moved = workflows.Count, group });
        });

        app.MapPost("/api/workflows/group/rename", async (HttpContext ctx, WorkflowGroupRenameDto request, VmsDbContext db,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsManage, out _) is { } failure) return failure;
            string? from = CleanGroup(request.From);
            if (from is null) return Error("Indique el grupo a renombrar.");
            string? to = CleanGroup(request.To);
            var workflows = await db.Workflows.Where(w => w.GroupName == from).ToListAsync(ct);
            if (workflows.Count == 0) return Results.NotFound();
            foreach (var w in workflows) { w.GroupName = to; w.UpdatedAt = DateTime.UtcNow; }
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "workflows", "workflow-group-renamed",
                targetType: "workflow-group", targetId: from, targetName: from,
                detail: to is null
                    ? $"Disolvió el grupo '{from}': sus {workflows.Count} automatización(es) quedaron sin grupo."
                    : $"Renombró el grupo '{from}' a '{to}' ({workflows.Count} automatización(es)).",
                data: new { From = from, To = to, Count = workflows.Count });
            return Results.Ok(new { renamed = workflows.Count, group = to });
        });

        app.MapDelete("/api/workflows/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            WorkflowEngine engine, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsManage, out _) is { } failure) return failure;
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
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsManage, out var session) is { } failure) return failure;
            var source = await db.Workflows.AsNoTracking().Include(w => w.Actions).FirstOrDefaultAsync(w => w.Id == id, ct);
            if (source is null) return Results.NotFound();
            if (license.Deny(LicenseFeatures.ModuleAutomation, null, 0) is { } denied)
                return await license.DenyAsync(ctx, denied, "workflow", source.Name);

            if (await OutOfScopeAsync(ctx, WorkflowResourceScope.References(source.Actions), db, audit, source.Name, "duplicar", ct)
                is { } outOfScope)
                return outOfScope;

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
                GroupName = source.GroupName,
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
            VmsDbContext db, AuditService audit, string? plate, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsManage, out var session) is { } failure) return failure;
            // Probar a mano es ejecutarla ahora: exige alcance sobre todo lo que toca.
            var tested = await db.Workflows.AsNoTracking().Include(w => w.Actions).FirstOrDefaultAsync(w => w.Id == id, ct);
            if (tested is null) return Results.NotFound();
            if (await OutOfScopeAsync(ctx, WorkflowResourceScope.References(tested.Actions), db, audit, tested.Name, "probar", ct)
                is { } outOfScope)
                return outOfScope;
            var run = await engine.TestAsync(id, session.Username, ct, plate);
            if (run is null) return Results.NotFound();

            await audit.LogAsync(ctx, "workflows", "workflow-tested",
                targetType: "workflow", targetId: id.ToString(), targetName: run.WorkflowName,
                detail: $"Probó a mano la automatización '{run.WorkflowName}'" +
                        (string.IsNullOrWhiteSpace(plate) ? ": " : $" con la patente {plate.Trim()}: ") +
                        (run.Success ? "todos los pasos se ejecutaron." : $"falló — {run.Error}"),
                success: run.Success, data: new { run.Steps });
            return Results.Ok(run);
        });

        // Clave nueva para una automatización de llamada externa (el editor la
        // pide al elegir ese disparador; se guarda con las condiciones).
        app.MapPost("/api/workflows/hook-key", (HttpContext ctx) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsManage, out _) is { } failure) return failure;
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
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsView, out var session) is { } failure) return failure;

            var query = db.WorkflowRuns.AsNoTracking().AsQueryable();
            if ((await ctx.ScopeAsync(session)).FiltersView)
            {
                var visible = await VisibleAlertsAsync(ctx, db, session);
                query = query.Where(r => visible.Any(a => a.RunId == r.Id));
            }
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
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsView, out var session) is { } failure) return failure;
            var run = await db.WorkflowRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            if (run is null) return Results.NotFound();
            if ((await ctx.ScopeAsync(session)).FiltersView &&
                !await (await VisibleAlertsAsync(ctx, db, session)).AnyAsync(a => a.RunId == id, ct))
                return Results.NotFound();
            return Results.Ok(WorkflowMapper.ToDto(run));
        });

        // Foto capturada por una ejecución (la ruta viene del propio historial).
        app.MapGet("/api/workflows/files/{**path}", async (HttpContext ctx, string path, WorkflowStore store, VmsDbContext db) =>
        {
            if (ApiSecurity.RequireAny(ctx, out var session, Permissions.EventsAttend, Permissions.WorkflowsView) is { } failure) return failure;
            // Con la vista filtrada, solo las fotos de alertas que puede ver.
            if ((await ctx.ScopeAsync(session)).FiltersView &&
                !await (await VisibleAlertsAsync(ctx, db, session)).AnyAsync(a =>
                    a.ImagePath == path || (a.ImagePathsJson != null && a.ImagePathsJson.Contains(path))))
                return Results.NotFound();
            if (store.FullPath(path) is not { } full || !File.Exists(full)) return Results.NotFound();
            return Results.File(full, Path.GetExtension(full).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                ? "image/jpeg" : "application/octet-stream");
        }).AllowUrlTicket(UrlTicketUse.Media);

        // ------------------------------------------------------------------
        // Alertas y acuse de recibo ("¿quién la vio y cuándo?")
        // ------------------------------------------------------------------
        app.MapGet("/api/workflows/alerts", async (HttpContext ctx, VmsDbContext db,
            bool? pending, int? skip, int? take, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.EventsAttend, out var session) is { } failure) return failure;

            // Un operador ve las alertas dirigidas a todos o a él; el
            // administrador las ve todas (es quien responde "quién la vio").
            var visible = await VisibleAlertsAsync(ctx, db, session);
            var scope = await ctx.ScopeAsync(session);
            var query = visible;
            if (pending == true) query = query.Where(a => a.RequiresAck && a.AcknowledgedAt == null);

            int total = await query.CountAsync(ct);
            int pendingCount = await visible.CountAsync(a => a.RequiresAck && a.AcknowledgedAt == null, ct);
            var alerts = await query.OrderByDescending(a => a.RaisedAt)
                .Skip(Math.Max(skip ?? 0, 0))
                .Take(Math.Clamp(take ?? 50, 1, MaxTake))
                .ToListAsync(ct);

            return Results.Ok(new WorkflowAlertListDto(total, pendingCount,
                alerts.Select(a => ScopedDto(a, scope)).ToList()));
        });

        app.MapGet("/api/workflows/alerts/{id:long}", async (HttpContext ctx, long id, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.EventsAttend, out var session) is { } failure) return failure;
            var alert = await (await VisibleAlertsAsync(ctx, db, session)).FirstOrDefaultAsync(a => a.Id == id, ct);
            return alert is null ? Results.NotFound() : Results.Ok(ScopedDto(alert, await ctx.ScopeAsync(session)));
        });

        // Repetición del evento: los segundos antes y después que rigen cuando la
        // acción «Avisar» no dice otros (Automatizaciones → Repetición del evento).
        app.MapGet("/api/workflows/clip-settings", (HttpContext ctx, AlertClips clips) =>
        {
            if (ApiSecurity.RequireAny(ctx, out _, Permissions.WorkflowsView, Permissions.WorkflowsManage) is { } failure) return failure;
            return Results.Ok(clips.Settings);
        });

        app.MapPut("/api/workflows/clip-settings", async (HttpContext ctx, AlertClipSettingsDto request, AlertClips clips,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsManage, out _) is { } failure) return failure;
            var before = clips.Settings;
            var clean = AlertClips.Clean(request);
            await clips.SaveSettingsAsync(clean, ct);
            await audit.LogAsync(ctx, "workflows", "alert-clip-settings-updated",
                detail: $"Repetición del evento por omisión: {clean.PreSeconds} s antes y {clean.PostSeconds} s después " +
                        $"(antes {before.PreSeconds} s y {before.PostSeconds} s).",
                data: new { before, after = clean });
            return Results.Ok(clean);
        });

        // Repetición del evento: si la grabación de esa cámara ya cubre el
        // clip. El cliente vuelve a preguntar mientras diga "recording" y,
        // cuando está listo, pide la reproducción normal con ese rango.
        app.MapGet("/api/workflows/alerts/{id:long}/clip", async (HttpContext ctx, long id, int channelId,
            VmsDbContext db, AlertClips clips, LicenseService license, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.EventsAttend, out var session) is { } failure) return failure;
            if (ApiSecurity.Require(ctx, Permissions.PlaybackView, out _) is { } noPlayback) return noPlayback;
            if (license.Deny(LicenseFeatures.ModulePlayback, null, 0, 0) is { } denied)
                return await license.DenyAsync(ctx, denied, "workflow-alert", $"aviso {id}");

            var alert = await (await VisibleAlertsAsync(ctx, db, session)).FirstOrDefaultAsync(a => a.Id == id, ct);
            if (alert is null) return Results.NotFound();
            if (alert.ClipPreSeconds is null)
                return Results.Json(new { error = "Este aviso no tiene repetición del evento." }, statusCode: StatusCodes.Status409Conflict);
            // Solo las cámaras del aviso y que el usuario ve (las demás ni siquiera le llegan en el aviso).
            var channels = WorkflowJson.TryDeserialize<List<int>>(alert.ChannelIdsJson) ?? [];
            if (!channels.Contains(channelId) || !(await ctx.ScopeAsync(session)).CanViewChannel(channelId))
                return Results.NotFound();
            var channel = await db.Channels.AsNoTracking().Include(c => c.Device).FirstOrDefaultAsync(c => c.Id == channelId, ct);
            if (channel is null) return Results.NotFound();
            // Las cámaras móviles se reproducen por la flota, con su propio permiso.
            if (channel.Device.IsMobile)
                return Results.Ok(new AlertClipDto(AlertClipStates.Missing, channel.DeviceId, channel.RtspChannel,
                    alert.RaisedAt, alert.RaisedAt, alert.RaisedAt, 0, false,
                    "La repetición no está disponible para cámaras de vehículos: búsquela en Flota."));

            return Results.Ok(await clips.StatusAsync(alert, channel, ct));
        });

        // Darse por enterado. Cualquier usuario con sesión puede hacerlo (el
        // que esté frente al puesto); la PRIMERA confirmación es la que queda:
        // el registro no se puede reescribir ni "mejorar" después.
        app.MapPost("/api/workflows/alerts/{id:long}/ack", async (HttpContext ctx, long id, VmsDbContext db,
            ScopedHub hub, AlarmScreenService screens, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.EventsAttend, out var session) is { } failure) return failure;

            // Solo puede darse por enterado de lo que puede ver (destinatarios y alcance).
            if (!await (await VisibleAlertsAsync(ctx, db, session)).AnyAsync(a => a.Id == id, ct))
                return Results.NotFound();
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
            // A quienes recibieron la alerta (no a todos: puede ser dirigida o de un recurso ajeno).
            await hub.SendToAsync(VmsHubContract.WorkflowAlertAcknowledged, dto,
                s => ReceivesAlert(s, alert.RecipientUserIds, alert.ResourceKey), ct);
            // Y a las pantallas de alarma que la muestran: dejan de sonar.
            await screens.PublishAcknowledgedAsync(alert, ct);

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
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsManage, out _) is { } failure) return failure;
            var settings = await db.SmtpSettings.AsNoTracking().OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
            return Results.Ok(settings is null
                ? new SmtpSettingsDto(false, "", 587, SmtpSecurity.StartTls, "", false, false, "", "CLR TrueCentral VMS", null)
                : WorkflowMapper.ToDto(settings));
        });

        app.MapPut("/api/workflows/smtp", async (HttpContext ctx, SmtpSettingsWriteDto request, VmsDbContext db,
            CredentialProtector protector, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsManage, out _) is { } failure) return failure;
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
            // La contraseña guardada no viaja a otro servidor de correo (lo mismo que con los equipos).
            if (string.IsNullOrEmpty(request.Password) && settings.PasswordCiphertext is { Length: > 0 }
                && !StoredPassword.CanReuse(settings.Host, request.Host))
                return Error("Cambió el servidor de correo: escriba de nuevo la contraseña. La guardada no se envía a otro servidor.");
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
            if (ApiSecurity.Require(ctx, Permissions.WorkflowsManage, out var session) is { } failure) return failure;
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
        }).AllowUrlTicket(UrlTicketUse.Media);

        app.MapPost("/api/workflows/audio", async (HttpContext ctx, IFormFile file, WorkflowStore store,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SoundsManage, out _) is { } failure) return failure;
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
            if (ApiSecurity.Require(ctx, Permissions.SoundsManage, out _) is { } failure) return failure;
            if (!store.DeleteAudio(name)) return Results.NotFound();
            await audit.LogAsync(ctx, "workflows", "audio-deleted",
                targetType: "audio", targetName: name,
                detail: $"Eliminó el sonido '{name}' de los parlantes IP.");
            return Results.NoContent();
        });

        // Nivel del sonido (pico y medio) y cuánto admite de amplificación.
        app.MapGet("/api/workflows/audio/{name}/level", async (HttpContext ctx, string name, WorkflowStore store,
            CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            if (store.FindAudio(name) is null) return Results.NotFound();
            var level = await store.MeasureAudioAsync(name, ct);
            double suggested = level?.PeakDb is { } peak ? Math.Max(Math.Round(-peak - WorkflowStore.GainHeadroomDb, 1), 0) : 0;
            return Results.Ok(new WorkflowAudioLevelDto(level?.PeakDb, level?.MeanDb, level?.DurationSeconds, suggested));
        });

        // Ganancia: amplifica (o atenúa) el sonido y lo vuelve a convertir.
        // Sin ganancia indicada sube al máximo sin saturar.
        app.MapPost("/api/workflows/audio/{name}/gain", async (HttpContext ctx, string name, WorkflowAudioGainRequestDto? request,
            WorkflowStore store, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SoundsManage, out _) is { } failure) return failure;
            if (store.FindAudio(name) is null) return Results.NotFound();
            var (applied, error) = await store.ApplyGainAsync(name, request?.GainDb, ct);
            await audit.LogAsync(ctx, "workflows", "audio-gain",
                targetType: "audio", targetName: name,
                detail: error is null
                    ? $"Amplificó el sonido '{name}' en {applied:+0.0;-0.0} dB{(request?.GainDb is null ? " (al máximo sin saturar)" : "")} y lo reconvirtió a G.711."
                    : $"No se pudo amplificar el sonido '{name}': {error}",
                data: new { name, requestedGainDb = request?.GainDb, appliedGainDb = applied },
                success: error is null);
            if (error is not null) return Error(error);
            var level = await store.MeasureAudioAsync(name, ct);
            return Results.Ok(new
            {
                ok = true, appliedGainDb = applied,
                level = new WorkflowAudioLevelDto(level?.PeakDb, level?.MeanDb, level?.DurationSeconds,
                    level?.PeakDb is { } p ? Math.Max(Math.Round(-p - WorkflowStore.GainHeadroomDb, 1), 0) : 0),
                items = store.ListAudio(),
            });
        });
    }

    // ------------------------------------------------------------------
    // Validación y volcado
    // ------------------------------------------------------------------

    /// <summary>
    /// 403 si la automatización usa recursos fuera del alcance de quien la arma (queda en la bitácora); null = permitido.
    /// </summary>
    private static async Task<IResult?> OutOfScopeAsync(HttpContext ctx, IEnumerable<string> references, VmsDbContext db,
        AuditService audit, string workflowName, string verb, CancellationToken ct)
    {
        if (ctx.Items["scope"] is not UserScope scope) return null;
        var denied = await WorkflowResourceScope.DeniedAsync(scope, references, db, ct);
        if (denied.Count == 0) return null;
        string list = string.Join(", ", denied);
        await audit.LogAsync(ctx, "workflows", "workflow-scope-denied", targetType: "workflow", targetName: workflowName,
            detail: $"Rechazado: intentó {verb} la automatización '{workflowName}' con recursos fuera de su alcance: {list}.",
            success: false, data: new { Denied = denied });
        return Error($"La automatización usa recursos fuera de su alcance: {list}. Quítelos o pida a un administrador que la arme.",
            StatusCodes.Status403Forbidden);
    }

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

    /// <summary>Nombre de grupo normalizado (espacios, máx. 64); null = sin grupo.</summary>
    private static string? CleanGroup(string? group)
    {
        string text = string.Join(' ', (group ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length == 0 ? null : text.Length > 64 ? text[..64].TrimEnd() : text;
    }

    private static string GroupLabel(string? group) => group is null ? "sin grupo" : $"el grupo '{group}'";

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
