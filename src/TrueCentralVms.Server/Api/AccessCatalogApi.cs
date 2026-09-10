using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Licensing;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// API de la operación del módulo Control de acceso: puertas, horarios,
/// niveles de acceso, padrón de personas e historial. El administrador de
/// dispositivos (alta y baja de equipos) vive aparte, en <see cref="AccessApi"/>.
///
/// Quién puede qué: operar una puerta y mirar el historial es de cualquier
/// usuario (es el trabajo del guardia); configurar horarios, niveles y
/// personas es de administrador, porque es decidir quién entra.
///
/// Todo lo que cambia el padrón deja a las personas afectadas PENDIENTES y
/// despierta al sincronizador: la respuesta al operador no espera a que
/// contesten los equipos, que pueden ser muchos y estar lentos.
/// </summary>
public static class AccessCatalogApi
{
    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    private const int MinutesPerDay = 24 * 60;
    /// <summary>Tramos por día que aceptan los equipos de la familia (el más chico manda).</summary>
    private const int MaxSegmentsPerDay = 8;

    // ==================================================================
    // Mapeo
    // ==================================================================

    private static AccessScheduleDto ToDto(AccessSchedule s, int levelCount) => new(
        s.Id, s.Name, s.Description, s.PlanNumber, s.IsBuiltIn,
        s.Segments.OrderBy(x => x.Day).ThenBy(x => x.StartMinutes)
            .Select(x => new AccessScheduleSegmentDto(x.Day, x.StartMinutes, x.EndMinutes)).ToList(),
        levelCount, s.CreatedAt, s.UpdatedAt);

    private static AccessLevelDto ToDto(AccessLevel l) => new(
        l.Id, l.Name, l.Description, l.AccessScheduleId, l.AccessSchedule?.Name ?? "", l.Enabled,
        l.Doors.Select(d => d.AccessDoorId).ToList(),
        l.Doors.Where(d => d.AccessDoor is not null)
            .Select(d => $"{d.AccessDoor!.AccessDevice?.Name ?? "?"} · {d.AccessDoor.Name}")
            .OrderBy(n => n).ToList(),
        l.Persons.Count, l.CreatedAt, l.UpdatedAt);

    /// <summary>Niveles con todo lo que su DTO necesita (puertas con equipo, horario y asignaciones).</summary>
    private static IQueryable<AccessLevel> LevelQuery(VmsDbContext db) => db.AccessLevels
        .Include(l => l.AccessSchedule)
        .Include(l => l.Persons)
        .Include(l => l.Doors).ThenInclude(d => d.AccessDoor).ThenInclude(d => d!.AccessDevice);

    /// <summary>Personas con todo lo que su DTO necesita (tarjetas, niveles y estado por equipo).</summary>
    private static IQueryable<AccessPerson> PersonQuery(VmsDbContext db) => db.AccessPersons
        .Include(p => p.Cards)
        .Include(p => p.Fingerprints)
        .Include(p => p.Face)
        .Include(p => p.Levels).ThenInclude(x => x.AccessLevel)
        .Include(p => p.Devices).ThenInclude(d => d.AccessDevice);

    // ==================================================================
    // Validación
    // ==================================================================

    /// <summary>
    /// Revisa los tramos de un horario: días válidos, fin después del
    /// comienzo, tope de tramos por día y nada que se pise. Los que se pisan
    /// se rechazan en vez de fusionarse en silencio: casi siempre son un error
    /// de tipeo del operador y conviene que lo vea.
    /// </summary>
    private static string? ValidateSegments(IReadOnlyList<AccessScheduleSegmentDto> segments)
    {
        if (segments.Count == 0)
            return "El horario necesita al menos un tramo (un horario sin tramos no deja pasar a nadie).";
        foreach (var segment in segments)
        {
            if (segment.Day is < 0 or > 6) return "El día de un tramo debe estar entre 0 (domingo) y 6 (sábado).";
            if (segment.StartMinutes is < 0 or >= MinutesPerDay || segment.EndMinutes is < 1 or > MinutesPerDay)
                return "Los tramos van entre las 00:00 y las 24:00.";
            if (segment.EndMinutes <= segment.StartMinutes)
                return "El fin de cada tramo tiene que ser posterior a su comienzo.";
        }
        foreach (var day in segments.GroupBy(s => s.Day))
        {
            if (day.Count() > MaxSegmentsPerDay)
                return $"Un día no puede tener más de {MaxSegmentsPerDay} tramos (es el tope de los equipos).";
            var ordered = day.OrderBy(s => s.StartMinutes).ToList();
            for (int i = 1; i < ordered.Count; i++)
                if (ordered[i].StartMinutes < ordered[i - 1].EndMinutes)
                    return $"Hay tramos que se pisan el día {DayName(day.Key)}; júntelos en uno solo.";
        }
        return null;
    }

    /// <summary>
    /// Página y tamaño de una consulta paginada. Van como opcionales para que
    /// omitirlos devuelva la primera página en vez de un 400 del enrutador
    /// (que además se comería la respuesta de "no autorizado").
    /// </summary>
    private static (int Page, int PageSize) Paging(int? page, int? pageSize) =>
        (Math.Max(page ?? 1, 1), Math.Clamp(pageSize is null or 0 ? 50 : pageSize.Value, 1, 500));

    private static string DayName(int day) => day switch
    {
        0 => "domingo", 1 => "lunes", 2 => "martes", 3 => "miércoles",
        4 => "jueves", 5 => "viernes", _ => "sábado",
    };

    /// <summary>Los tramos guardados como los quiere el asignador de ranuras.</summary>
    private static List<AccessTimeSegment> SegmentsOf(AccessSchedule schedule) =>
        schedule.Segments.Select(s => new AccessTimeSegment(s.Day, s.StartMinutes, s.EndMinutes)).ToList();

    /// <summary>
    /// Deja el horario apuntando a la ranura que le corresponde por su
    /// contenido. Si cambian los tramos, cambia la ranura: por eso se
    /// recalcula en cada guardado y no se asigna una sola vez al crear.
    /// </summary>
    private static async Task AssignSlotAsync(VmsDbContext db, AccessSchedule schedule, CancellationToken ct)
    {
        var slot = await AccessSyncService.SlotForAsync(db, AccessSyncService.Canonical(SegmentsOf(schedule)),
            schedule.Name, ct);
        await db.SaveChangesAsync(ct);   // la ranura nueva necesita su Id antes de referenciarla
        schedule.PlanNumber = slot.Number;
    }

    /// <summary>
    /// Crea el horario 24/7 si no está. Es el único que trae el sistema: sin
    /// él, el primer nivel de acceso no tendría qué elegir.
    /// </summary>
    public static async Task EnsureBuiltInScheduleAsync(VmsDbContext db, CancellationToken ct)
    {
        if (await db.AccessSchedules.AnyAsync(s => s.IsBuiltIn, ct)) return;
        var schedule = new AccessSchedule
        {
            Name = "Todo el día, todos los días (24/7)",
            Description = "Horario de fábrica: deja pasar a cualquier hora de cualquier día. No se puede editar ni borrar.",
            IsBuiltIn = true,
            Segments = Enumerable.Range(0, 7)
                .Select(day => new AccessScheduleSegment { Day = day, StartMinutes = 0, EndMinutes = MinutesPerDay - 1 })
                .ToList(),
        };
        db.AccessSchedules.Add(schedule);
        await AssignSlotAsync(db, schedule, ct);
        await db.SaveChangesAsync(ct);
    }

    // ==================================================================
    // Rutas
    // ==================================================================

    public static void MapAccessCatalogApi(this WebApplication app)
    {
        MapDoors(app);
        MapSchedules(app);
        MapLevels(app);
        MapPersons(app);
        MapEvents(app);

        // ------------------------------------------------------------------
        // Resumen del módulo (la portada de Control de acceso)
        // ------------------------------------------------------------------
        app.MapGet("/api/access/overview", async (HttpContext ctx, VmsDbContext db, LicenseService license,
            CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var since = DateTime.UtcNow.Date;

            var doors = await db.AccessDoors.AsNoTracking().Where(d => d.Enabled).ToListAsync(ct);
            var recent = await db.AccessEvents.AsNoTracking()
                .OrderByDescending(e => e.Timestamp).Take(15).ToListAsync(ct);

            return Results.Ok(new AccessOverviewDto(
                Devices: await db.AccessDevices.CountAsync(ct),
                DevicesOnline: await db.AccessDevices.CountAsync(d => d.Status == AccessDeviceStatus.Online, ct),
                Doors: doors.Count,
                DoorsRemainOpen: doors.Count(d => d.Mode == AccessDoorMode.RemainOpen),
                DoorsRemainLocked: doors.Count(d => d.Mode == AccessDoorMode.RemainLocked),
                Persons: await db.AccessPersons.CountAsync(ct),
                PersonsWithoutLevel: await db.AccessPersons.CountAsync(p => p.Levels.Count == 0, ct),
                PersonsPendingSync: await db.AccessPersons.CountAsync(p => p.SyncState == AccessSyncState.Pending, ct),
                PersonsFailedSync: await db.AccessPersons.CountAsync(p => p.SyncState == AccessSyncState.Failed, ct),
                Levels: await db.AccessLevels.CountAsync(ct),
                Schedules: await db.AccessSchedules.CountAsync(ct),
                EventsToday: await db.AccessEvents.CountAsync(e => e.Timestamp >= since, ct),
                DeniedToday: await db.AccessEvents.CountAsync(e => e.Timestamp >= since && e.Kind == AccessEventKind.Denied, ct),
                DoorLimit: license.Quota(LicenseFeatures.AccessDoors),
                RecentEvents: recent.Select(AccessEventService.ToDto).ToList()));
        });

        // ------------------------------------------------------------------
        // Complemento de enrolamiento
        // ------------------------------------------------------------------
        // El panel web no puede hablar con el lector USB del puesto: eso lo hace
        // un complemento que se instala en ese PC y escucha en 127.0.0.1. Estas
        // dos rutas sirven su instalador para que nadie tenga que ir a buscar el
        // archivo a mano.
        app.MapGet("/api/webcontrol/info", (HttpContext ctx, IConfiguration config, IWebHostEnvironment env) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var file = FindWebControlInstaller(config, env);
            return Results.Ok(new
            {
                available = file is not null,
                fileName = file?.Name,
                sizeMb = file is null ? (double?)null : Math.Round(file.Length / 1024d / 1024d, 1),
                // La versión va en el nombre del instalador (…-Setup-1.2.3.exe).
                version = file is null ? null : Path.GetFileNameWithoutExtension(file.Name).Split('-').LastOrDefault(),
            });
        });

        // La descarga acepta el token por query: la inicia el navegador (no
        // fetch) y no puede mandar la cabecera Authorization.
        app.MapGet("/api/webcontrol/installer", async (HttpContext ctx, IConfiguration config, IWebHostEnvironment env,
            AuditService audit) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var file = FindWebControlInstaller(config, env);
            if (file is null)
                return Error("Este servidor no tiene publicado el instalador del complemento de enrolamiento.",
                    StatusCodes.Status404NotFound);
            await audit.LogAsync(ctx, "access", "webcontrol-downloaded",
                detail: $"Descargó el instalador del complemento de enrolamiento ({file.Name}).");
            return Results.File(file.FullName, "application/octet-stream", file.Name);
        });

        // Escribir ahora lo que esté pendiente, sin esperar el reintento solo.
        app.MapPost("/api/access/sync", async (HttpContext ctx, AccessSyncService sync, AuditService audit,
            CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            int done = await sync.SyncPendingAsync(ct);
            await audit.LogAsync(ctx, "access", "sync-requested",
                detail: $"Escribió en los equipos lo que estaba pendiente: {done} persona(s) procesada(s).");
            return Results.Ok(new { processed = done, resent = 0 });
        });

        // REENVIAR TODO: reescribe el padrón completo aunque el VMS crea que los
        // equipos ya lo tienen. Es para cuando el equipo perdió lo suyo sin que
        // el VMS se entere (terminal reemplazado, vuelto a fábrica, o alguien
        // borró personas desde su pantalla).
        app.MapPost("/api/access/sync/full", async (HttpContext ctx, VmsDbContext db, AccessSyncService sync,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            int marked = await AccessSyncService.MarkForResendAsync(db, null, ct);
            await db.SaveChangesAsync(ct);
            int done = await sync.SyncPendingAsync(ct);
            await audit.LogAsync(ctx, "access", "sync-forced",
                detail: $"Forzó el reenvío del padrón completo a todos los equipos: {marked} persona(s) marcada(s), " +
                        $"{done} procesada(s).");
            return Results.Ok(new { processed = done, resent = marked });
        });

        // Lo mismo, pero contra UN equipo: es el caso habitual (se cambió ese
        // terminal, no los doce).
        app.MapPost("/api/access/devices/{id:int}/resync", async (HttpContext ctx, int id, VmsDbContext db,
            AccessControlService access, AccessSyncService sync, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var device = await db.AccessDevices.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (device is null) return Results.NotFound();
            if (!access.SupportsPersonSync(device.DriverKey))
                return Error($"Los equipos {device.DriverKey} no aceptan el padrón desde el VMS: " +
                             "las personas se cargan en el propio equipo.");

            int marked = await AccessSyncService.MarkForResendAsync(db, id, ct);
            await db.SaveChangesAsync(ct);
            int done = await sync.SyncPendingAsync(ct);
            await audit.LogAsync(ctx, "access", "sync-forced",
                targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                detail: $"Forzó el reenvío del padrón al equipo '{device.Name}': {marked} persona(s) marcada(s), " +
                        $"{done} procesada(s).");
            return Results.Ok(new { processed = done, resent = marked });
        });
    }

    /// <summary>
    /// Instalador del complemento publicado por este servidor, o null si no se
    /// copió. Se busca en <c>WebControl:InstallerPath</c> (por omisión, la
    /// carpeta <c>webcontrol</c> junto al servidor) y gana el más reciente.
    /// </summary>
    private static FileInfo? FindWebControlInstaller(IConfiguration config, IWebHostEnvironment env)
    {
        string configured = config["WebControl:InstallerPath"] ?? "webcontrol";
        string directory = Path.IsPathRooted(configured)
            ? Environment.ExpandEnvironmentVariables(configured)
            : Path.Combine(env.ContentRootPath, Environment.ExpandEnvironmentVariables(configured));
        if (!Directory.Exists(directory)) return null;
        return new DirectoryInfo(directory)
            .GetFiles("CLRTrueCentralVMS-Complemento-Setup-*.exe")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    // ==================================================================
    // Puertas
    // ==================================================================

    private static void MapDoors(WebApplication app)
    {
        app.MapGet("/api/access/doors", async (HttpContext ctx, VmsDbContext db, AccessControlService service,
            CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var doors = await db.AccessDoors.AsNoTracking().Include(d => d.AccessDevice)
                .OrderBy(d => d.AccessDevice!.Location).ThenBy(d => d.AccessDevice!.Name).ThenBy(d => d.Number)
                .ToListAsync(ct);
            return Results.Ok(doors.Select(service.ToDoorDto));
        });

        // Renombrar o pausar una puerta. El nombre es del VMS: una revalidación
        // del equipo no lo pisa, porque el operador lo puso a propósito.
        app.MapPut("/api/access/doors/{id:int}", async (HttpContext ctx, int id, AccessDoorWriteDto request,
            VmsDbContext db, AccessControlService service, IHubContext<VmsHub> hub, AccessSyncService sync,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
                return Error("El nombre de la puerta es obligatorio (máximo 128 caracteres).");
            var door = await db.AccessDoors.Include(d => d.AccessDevice).FirstOrDefaultAsync(d => d.Id == id, ct);
            if (door is null) return Results.NotFound();

            var changes = new List<string>();
            if (door.Name != request.Name.Trim()) changes.Add($"nombre '{door.Name}' → '{request.Name.Trim()}'");
            if (door.Enabled != request.Enabled) changes.Add(request.Enabled ? "activada" : "desactivada");

            bool enabledChanged = door.Enabled != request.Enabled;
            // Desde que el operador la nombra, el nombre es del VMS.
            if (door.Name != request.Name.Trim()) door.NameFromDevice = false;
            door.Name = request.Name.Trim();
            door.Enabled = request.Enabled;
            door.UpdatedAt = DateTime.UtcNow;
            // Una puerta apagada deja de dar permiso: hay que sacarla de los equipos.
            if (enabledChanged) await AccessSyncService.MarkDoorPendingAsync(db, door.Id, ct);
            await db.SaveChangesAsync(ct);
            if (enabledChanged) sync.RequestSync();

            await audit.LogAsync(ctx, "access", "door-updated",
                targetType: "access-door", targetId: door.Id.ToString(),
                targetName: $"{door.AccessDevice?.Name} · {door.Name}",
                detail: $"Modificó la puerta '{door.Name}' del equipo '{door.AccessDevice?.Name}': " +
                        (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios."));
            await hub.Clients.All.SendAsync(VmsHubContract.AccessDoorStateChanged, service.ToDoorDto(door), ct);
            return Results.Ok(service.ToDoorDto(door));
        });

        // Abrir, cerrar, mantener abierta o bloquear. Es la orden que más se
        // usa del módulo y la que más importa dejar registrada: quién abrió qué
        // puerta y cuándo.
        // Leer una tarjeta EN EL LECTOR del equipo, para no tipear el número a
        // mano. El equipo se queda unos diez segundos esperando; si nadie la
        // pasa devuelve 204 y el panel vuelve a pedirlo mientras el operador
        // tenga el diálogo abierto. Por eso "no vino nadie" no es un error.
        app.MapPost("/api/access/devices/{id:int}/capture-card", async (HttpContext ctx, int id,
            int? cardReaderNo, VmsDbContext db, AccessControlService service, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var device = await db.AccessDevices.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (device is null) return Results.NotFound();
            if (!device.Enabled) return Error($"El equipo '{device.Name}' está pausado.");
            if (!service.SupportsCardCapture(device.DriverKey))
                return Error($"El equipo '{device.Name}' no sabe leer una tarjeta a pedido.");
            if (device.Status != AccessDeviceStatus.Online)
                return Error($"El equipo '{device.Name}' no está en línea.");

            try
            {
                string? number = await service.CaptureCardAsync(device, cardReaderNo, ct);
                return number is null ? Results.NoContent() : Results.Ok(new { cardNo = number });
            }
            catch (DriverException ex) { return Error(ex.Message); }
        });

        app.MapPost("/api/access/doors/{id:int}/command", async (HttpContext ctx, int id, AccessDoorCommandDto request,
            VmsDbContext db, AccessControlService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            if (!Enum.TryParse<AccessDoorCommand>(request.Command, ignoreCase: true, out var command))
                return Error($"Orden desconocida: '{request.Command}'.");

            var door = await db.AccessDoors.Include(d => d.AccessDevice).FirstOrDefaultAsync(d => d.Id == id, ct);
            if (door is null) return Results.NotFound();
            var device = door.AccessDevice!;
            string target = $"{device.Name} · {door.Name}";
            string action = CommandAudit(command);

            if (!door.Enabled) return Error("La puerta está desactivada en el VMS.");
            if (!device.Enabled) return Error($"El equipo '{device.Name}' está pausado.");
            if (device.Status != AccessDeviceStatus.Online)
                return Error($"El equipo '{device.Name}' no está en línea: {device.LastError ?? "sin conexión"}.",
                    StatusCodes.Status502BadGateway);

            try
            {
                await service.CommandDoorAsync(db, door, command, ct);
                await audit.LogAsync(ctx, "access", action,
                    targetType: "access-door", targetId: door.Id.ToString(), targetName: target,
                    detail: $"{CommandVerb(command)} la puerta '{door.Name}' del equipo '{device.Name}'.");
                return Results.Ok(service.ToDoorDto(door));
            }
            catch (DriverException ex)
            {
                await audit.LogAsync(ctx, "access", action,
                    targetType: "access-door", targetId: door.Id.ToString(), targetName: target,
                    detail: $"El equipo '{device.Name}' rechazó la orden sobre '{door.Name}': {ex.Message}", success: false);
                return Error(ex.Message, StatusCodes.Status502BadGateway);
            }
        });
    }

    private static string CommandAudit(AccessDoorCommand command) => command switch
    {
        AccessDoorCommand.Open => "door-opened",
        AccessDoorCommand.Close => "door-closed",
        AccessDoorCommand.RemainOpen => "door-remain-open",
        _ => "door-remain-locked",
    };

    private static string CommandVerb(AccessDoorCommand command) => command switch
    {
        AccessDoorCommand.Open => "Abrió",
        AccessDoorCommand.Close => "Cerró",
        AccessDoorCommand.RemainOpen => "Dejó abierta",
        _ => "Bloqueó",
    };

    // ==================================================================
    // Horarios
    // ==================================================================

    private static void MapSchedules(WebApplication app)
    {
        app.MapGet("/api/access/schedules", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var schedules = await db.AccessSchedules.AsNoTracking().Include(s => s.Segments)
                .OrderByDescending(s => s.IsBuiltIn).ThenBy(s => s.Name).ToListAsync(ct);
            var counts = await db.AccessLevels.GroupBy(l => l.AccessScheduleId)
                .Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync(ct);
            return Results.Ok(schedules.Select(s =>
                ToDto(s, counts.FirstOrDefault(c => c.Id == s.Id)?.Count ?? 0)));
        });

        app.MapPost("/api/access/schedules", async (HttpContext ctx, AccessScheduleWriteDto request, VmsDbContext db,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
                return Error("El nombre del horario es obligatorio (máximo 128 caracteres).");
            if (ValidateSegments(request.Segments) is { } invalid) return Error(invalid);
            string name = request.Name.Trim();
            if (await db.AccessSchedules.AnyAsync(s => s.Name == name, ct))
                return Error("Ya existe un horario con ese nombre.", StatusCodes.Status409Conflict);

            var schedule = new AccessSchedule
            {
                Name = name,
                Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                Segments = request.Segments
                    .Select(s => new AccessScheduleSegment { Day = s.Day, StartMinutes = s.StartMinutes, EndMinutes = s.EndMinutes })
                    .ToList(),
            };
            db.AccessSchedules.Add(schedule);
            try { await AssignSlotAsync(db, schedule, ct); }
            catch (InvalidOperationException ex) { return Error(ex.Message); }
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "access", "schedule-created",
                targetType: "access-schedule", targetId: schedule.Id.ToString(), targetName: schedule.Name,
                detail: $"Creó el horario '{schedule.Name}' con {schedule.Segments.Count} tramo(s): {Describe(schedule)}.");
            return Results.Ok(ToDto(schedule, 0));
        });

        app.MapPut("/api/access/schedules/{id:int}", async (HttpContext ctx, int id, AccessScheduleWriteDto request,
            VmsDbContext db, AccessSyncService sync, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var schedule = await db.AccessSchedules.Include(s => s.Segments).FirstOrDefaultAsync(s => s.Id == id, ct);
            if (schedule is null) return Results.NotFound();
            if (schedule.IsBuiltIn)
                return Error("El horario 24/7 es del sistema y no se puede editar. Cree uno nuevo si necesita otro.");
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
                return Error("El nombre del horario es obligatorio (máximo 128 caracteres).");
            if (ValidateSegments(request.Segments) is { } invalid) return Error(invalid);
            string name = request.Name.Trim();
            if (await db.AccessSchedules.AnyAsync(s => s.Name == name && s.Id != id, ct))
                return Error("Ya existe otro horario con ese nombre.", StatusCodes.Status409Conflict);

            string before = Describe(schedule);
            schedule.Name = name;
            schedule.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
            db.AccessScheduleSegments.RemoveRange(schedule.Segments);
            schedule.Segments = request.Segments
                .Select(s => new AccessScheduleSegment { Day = s.Day, StartMinutes = s.StartMinutes, EndMinutes = s.EndMinutes })
                .ToList();
            schedule.UpdatedAt = DateTime.UtcNow;
            try { await AssignSlotAsync(db, schedule, ct); }
            catch (InvalidOperationException ex) { return Error(ex.Message); }

            // Cambiar el horario cambia cuándo puede pasar la gente: hay que
            // reescribir a todos los que dependen de él.
            await AccessSyncService.MarkSchedulePendingAsync(db, schedule.Id, ct);
            await db.SaveChangesAsync(ct);
            sync.RequestSync();

            await audit.LogAsync(ctx, "access", "schedule-updated",
                targetType: "access-schedule", targetId: schedule.Id.ToString(), targetName: schedule.Name,
                detail: $"Modificó el horario '{schedule.Name}': {before} → {Describe(schedule)}.");
            int levels = await db.AccessLevels.CountAsync(l => l.AccessScheduleId == id, ct);
            return Results.Ok(ToDto(schedule, levels));
        });

        app.MapDelete("/api/access/schedules/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var schedule = await db.AccessSchedules.Include(s => s.Segments).FirstOrDefaultAsync(s => s.Id == id, ct);
            if (schedule is null) return Results.NotFound();
            if (schedule.IsBuiltIn) return Error("El horario 24/7 es del sistema y no se puede borrar.");

            int levels = await db.AccessLevels.CountAsync(l => l.AccessScheduleId == id, ct);
            if (levels > 0)
                return Error($"El horario lo usan {levels} nivel(es) de acceso. Cámbieles el horario antes de borrarlo.",
                    StatusCodes.Status409Conflict);

            db.AccessSchedules.Remove(schedule);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "access", "schedule-deleted",
                targetType: "access-schedule", targetId: id.ToString(), targetName: schedule.Name,
                detail: $"Eliminó el horario '{schedule.Name}'.");
            return Results.Ok();
        });
    }

    /// <summary>El horario en una línea, para la bitácora ("lunes a viernes 08:00-18:00").</summary>
    private static string Describe(AccessSchedule schedule)
    {
        if (schedule.Segments.Count == 0) return "sin tramos";
        return string.Join("; ", schedule.Segments
            .OrderBy(s => s.Day).ThenBy(s => s.StartMinutes)
            .Select(s => $"{DayName(s.Day)} {Hhmm(s.StartMinutes)}-{Hhmm(s.EndMinutes)}"));
    }

    private static string Hhmm(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";

    // ==================================================================
    // Niveles de acceso
    // ==================================================================

    private static void MapLevels(WebApplication app)
    {
        app.MapGet("/api/access/levels", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var levels = await LevelQuery(db).AsNoTracking().OrderBy(l => l.Name).ToListAsync(ct);
            return Results.Ok(levels.Select(ToDto));
        });

        app.MapPost("/api/access/levels", async (HttpContext ctx, AccessLevelWriteDto request, VmsDbContext db,
            AccessSyncService sync, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (await ValidateLevelAsync(db, request, null, ct) is { } invalid) return Error(invalid);

            var level = new AccessLevel
            {
                Name = request.Name.Trim(),
                Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                AccessScheduleId = request.ScheduleId,
                Enabled = request.Enabled,
                Doors = request.DoorIds.Distinct().Select(d => new AccessLevelDoor { AccessDoorId = d }).ToList(),
            };
            db.AccessLevels.Add(level);
            await db.SaveChangesAsync(ct);

            var saved = await LevelQuery(db).FirstAsync(l => l.Id == level.Id, ct);
            await audit.LogAsync(ctx, "access", "level-created",
                targetType: "access-level", targetId: level.Id.ToString(), targetName: level.Name,
                detail: $"Creó el nivel de acceso '{level.Name}' con {saved.Doors.Count} puerta(s) " +
                        $"y el horario '{saved.AccessSchedule?.Name}'.");
            sync.RequestSync();
            return Results.Ok(ToDto(saved));
        });

        app.MapPut("/api/access/levels/{id:int}", async (HttpContext ctx, int id, AccessLevelWriteDto request,
            VmsDbContext db, AccessSyncService sync, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var level = await db.AccessLevels.Include(l => l.Doors).Include(l => l.AccessSchedule)
                .FirstOrDefaultAsync(l => l.Id == id, ct);
            if (level is null) return Results.NotFound();
            if (await ValidateLevelAsync(db, request, id, ct) is { } invalid) return Error(invalid);

            var changes = new List<string>();
            if (level.Name != request.Name.Trim()) changes.Add($"nombre '{level.Name}' → '{request.Name.Trim()}'");
            if (level.AccessScheduleId != request.ScheduleId) changes.Add("horario cambiado");
            if (level.Enabled != request.Enabled) changes.Add(request.Enabled ? "activado" : "desactivado");

            var wanted = request.DoorIds.Distinct().ToHashSet();
            var current = level.Doors.Select(d => d.AccessDoorId).ToHashSet();
            int added = wanted.Except(current).Count(), removed = current.Except(wanted).Count();
            if (added > 0 || removed > 0) changes.Add($"{added} puerta(s) agregada(s), {removed} quitada(s)");

            level.Name = request.Name.Trim();
            level.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
            level.AccessScheduleId = request.ScheduleId;
            level.Enabled = request.Enabled;
            level.UpdatedAt = DateTime.UtcNow;
            db.AccessLevelDoors.RemoveRange(level.Doors.Where(d => !wanted.Contains(d.AccessDoorId)));
            foreach (int doorId in wanted.Except(current))
                level.Doors.Add(new AccessLevelDoor { AccessLevelId = level.Id, AccessDoorId = doorId });

            await AccessSyncService.MarkLevelPendingAsync(db, level.Id, ct);
            await db.SaveChangesAsync(ct);
            sync.RequestSync();

            await audit.LogAsync(ctx, "access", "level-updated",
                targetType: "access-level", targetId: level.Id.ToString(), targetName: level.Name,
                detail: $"Modificó el nivel de acceso '{level.Name}': " +
                        (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios."));
            return Results.Ok(ToDto(await LevelQuery(db).FirstAsync(l => l.Id == id, ct)));
        });

        app.MapDelete("/api/access/levels/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            AccessSyncService sync, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var level = await db.AccessLevels.Include(l => l.Persons).FirstOrDefaultAsync(l => l.Id == id, ct);
            if (level is null) return Results.NotFound();

            int persons = level.Persons.Count;
            // Las personas pierden el permiso que daba este nivel: hay que
            // marcarlas ANTES de borrarlo, mientras todavía se sabe quiénes son.
            await AccessSyncService.MarkLevelPendingAsync(db, id, ct);
            db.AccessLevels.Remove(level);
            await db.SaveChangesAsync(ct);
            sync.RequestSync();

            await audit.LogAsync(ctx, "access", "level-deleted",
                targetType: "access-level", targetId: id.ToString(), targetName: level.Name,
                detail: $"Eliminó el nivel de acceso '{level.Name}'; {persons} persona(s) pierden ese permiso.");
            return Results.Ok();
        });

        // Asignación masiva: la pantalla "Asignar nivel de acceso" manda la
        // lista completa de personas que deben tenerlo.
        app.MapPut("/api/access/levels/{id:int}/persons", async (HttpContext ctx, int id, AccessAssignDto request,
            VmsDbContext db, AccessSyncService sync, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var level = await db.AccessLevels.Include(l => l.Persons).FirstOrDefaultAsync(l => l.Id == id, ct);
            if (level is null) return Results.NotFound();

            var wanted = request.PersonIds.Distinct().ToHashSet();
            var known = await db.AccessPersons.Where(p => wanted.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);
            if (known.Count != wanted.Count) return Error("Alguna de las personas indicadas ya no existe.");

            var current = level.Persons.Select(x => x.AccessPersonId).ToHashSet();
            var added = wanted.Except(current).ToList();
            var removed = current.Except(wanted).ToList();

            db.AccessLevelPersons.RemoveRange(level.Persons.Where(x => removed.Contains(x.AccessPersonId)));
            foreach (int personId in added)
                level.Persons.Add(new AccessLevelPerson { AccessLevelId = id, AccessPersonId = personId });

            await AccessSyncService.MarkPendingAsync(db, added.Concat(removed), ct);
            await db.SaveChangesAsync(ct);
            sync.RequestSync();

            await audit.LogAsync(ctx, "access", "level-assigned",
                targetType: "access-level", targetId: id.ToString(), targetName: level.Name,
                detail: $"Cambió quién tiene el nivel de acceso '{level.Name}': {added.Count} persona(s) agregada(s), " +
                        $"{removed.Count} quitada(s); quedan {wanted.Count}.");
            return Results.Ok(ToDto(await LevelQuery(db).FirstAsync(l => l.Id == id, ct)));
        });
    }

    private static async Task<string?> ValidateLevelAsync(VmsDbContext db, AccessLevelWriteDto request, int? id,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
            return "El nombre del nivel de acceso es obligatorio (máximo 128 caracteres).";
        string name = request.Name.Trim();
        if (await db.AccessSchedules.FindAsync([request.ScheduleId], ct) is null)
            return "El horario indicado no existe.";
        if (await db.AccessLevels.AnyAsync(l => l.Name == name && (id == null || l.Id != id), ct))
            return "Ya existe otro nivel de acceso con ese nombre.";
        if (request.DoorIds.Count == 0)
            return "El nivel de acceso necesita al menos una puerta.";
        var doorIds = request.DoorIds.Distinct().ToList();
        int found = await db.AccessDoors.CountAsync(d => doorIds.Contains(d.Id), ct);
        if (found != doorIds.Count) return "Alguna de las puertas indicadas ya no existe.";
        return null;
    }

    // ==================================================================
    // Personas
    // ==================================================================

    private static void MapPersons(WebApplication app)
    {
        app.MapGet("/api/access/persons", async (HttpContext ctx, VmsDbContext db, string? q, string? department,
            int? levelId, string? state, int? page, int? pageSize, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            (int pageNumber, int size) = Paging(page, pageSize);

            var query = PersonQuery(db).AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
            {
                string term = q.Trim();
                query = query.Where(p => EF.Functions.ILike(p.FirstName, $"%{term}%") ||
                                         EF.Functions.ILike(p.LastName, $"%{term}%") ||
                                         EF.Functions.ILike(p.EmployeeNo, $"%{term}%") ||
                                         p.Cards.Any(c => EF.Functions.ILike(c.Number, $"%{term}%")));
            }
            if (!string.IsNullOrWhiteSpace(department))
                query = query.Where(p => p.Department == department);
            if (levelId is int level)
                query = query.Where(p => p.Levels.Any(x => x.AccessLevelId == level));
            if (Enum.TryParse<AccessSyncState>(state, ignoreCase: true, out var syncState))
                query = query.Where(p => p.SyncState == syncState);

            int total = await query.CountAsync(ct);
            var persons = await query.OrderBy(p => p.LastName).ThenBy(p => p.FirstName)
                .Skip((pageNumber - 1) * size).Take(size).ToListAsync(ct);
            return Results.Ok(new
            {
                total,
                page = pageNumber,
                pageSize = size,
                items = persons.Select(AccessSyncService.ToPersonDto).ToList(),
            });
        });

        // Los departamentos no son una tabla: son lo que el operador fue
        // escribiendo. Esta ruta los junta para ofrecerlos en los filtros.
        app.MapGet("/api/access/departments", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.Ok(await db.AccessPersons.Where(p => p.Department != null)
                .Select(p => p.Department!).Distinct().OrderBy(d => d).ToListAsync(ct));
        });

        app.MapGet("/api/access/persons/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var person = await PersonQuery(db).AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            return person is null ? Results.NotFound() : Results.Ok(AccessSyncService.ToPersonDto(person));
        });

        app.MapPost("/api/access/persons", async (HttpContext ctx, AccessPersonWriteDto request, VmsDbContext db,
            CredentialProtector protector, AccessSyncService sync, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (await ValidatePersonAsync(db, request, null, ct) is { } invalid) return Error(invalid);

            string employeeNo = string.IsNullOrWhiteSpace(request.EmployeeNo)
                ? await NextEmployeeNoAsync(db, ct)
                : request.EmployeeNo.Trim();
            if (await db.AccessPersons.AnyAsync(p => p.EmployeeNo == employeeNo, ct))
                return Error($"Ya hay una persona con el identificador '{employeeNo}'.", StatusCodes.Status409Conflict);

            var person = new AccessPerson
            {
                EmployeeNo = employeeNo,
                FirstName = request.FirstName.Trim(),
                LastName = request.LastName.Trim(),
                Department = Trimmed(request.Department),
                Position = Trimmed(request.Position),
                Email = Trimmed(request.Email),
                Phone = Trimmed(request.Phone),
                Notes = Trimmed(request.Notes),
                ValidFrom = request.ValidFrom,
                ValidTo = request.ValidTo,
                Enabled = request.Enabled,
                PinCiphertext = string.IsNullOrWhiteSpace(request.PinCode) ? [] : protector.Protect(request.PinCode.Trim()),
                SyncState = AccessSyncState.Pending,
                Cards = request.Cards.Select(Normalize).Where(c => c.Length > 0).Distinct()
                    .Select(c => new AccessCard { Number = c }).ToList(),
                Levels = request.LevelIds.Distinct().Select(l => new AccessLevelPerson { AccessLevelId = l }).ToList(),
            };
            db.AccessPersons.Add(person);
            await db.SaveChangesAsync(ct);

            // Las huellas van después del primer guardado: necesitan el Id de
            // la persona, que recién existe acá.
            string fingerChanges = ApplyFingerprints(db, person, protector, request.Fingerprints);
            string faceChanges = ApplyFace(db, person, protector, request.Face, request.ClearFace);
            if (fingerChanges.Length > 0 || faceChanges.Length > 0) await db.SaveChangesAsync(ct);
            sync.RequestSync();

            await audit.LogAsync(ctx, "access", "person-created",
                targetType: "access-person", targetId: person.Id.ToString(), targetName: person.FullName,
                detail: $"Agregó al padrón a '{person.FullName}' (identificador {person.EmployeeNo}" +
                        (person.Department is null ? "" : $", {person.Department}") +
                        $") con {person.Cards.Count} tarjeta(s), {person.Fingerprints.Count} huella(s), " +
                        $"{(person.Face is null ? "sin rostro" : "rostro cargado")} " +
                        $"y {person.Levels.Count} nivel(es) de acceso.",
                data: new { person.EmployeeNo, person.Department, Cards = person.Cards.Count, Levels = person.Levels.Count });
            return Results.Ok(AccessSyncService.ToPersonDto(await PersonQuery(db).FirstAsync(p => p.Id == person.Id, ct)));
        });

        app.MapPut("/api/access/persons/{id:int}", async (HttpContext ctx, int id, AccessPersonWriteDto request,
            VmsDbContext db, CredentialProtector protector, AccessSyncService sync, AuditService audit,
            CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var person = await db.AccessPersons.Include(p => p.Cards).Include(p => p.Fingerprints)
                .Include(p => p.Face).Include(p => p.Levels)
                .FirstOrDefaultAsync(p => p.Id == id, ct);
            if (person is null) return Results.NotFound();
            if (await ValidatePersonAsync(db, request, id, ct) is { } invalid) return Error(invalid);

            var changes = new List<string>();
            if (person.FullName != $"{request.FirstName.Trim()} {request.LastName.Trim()}".Trim())
                changes.Add($"nombre '{person.FullName}' → '{request.FirstName.Trim()} {request.LastName.Trim()}'");
            if (person.Department != Trimmed(request.Department))
                changes.Add($"departamento '{person.Department ?? "—"}' → '{Trimmed(request.Department) ?? "—"}'");
            if (person.Enabled != request.Enabled) changes.Add(request.Enabled ? "activada" : "desactivada");
            if (person.ValidTo != request.ValidTo)
                changes.Add($"vigencia hasta {person.ValidTo:yyyy-MM-dd} → {request.ValidTo:yyyy-MM-dd}");

            person.FirstName = request.FirstName.Trim();
            person.LastName = request.LastName.Trim();
            person.Department = Trimmed(request.Department);
            person.Position = Trimmed(request.Position);
            person.Email = Trimmed(request.Email);
            person.Phone = Trimmed(request.Phone);
            person.Notes = Trimmed(request.Notes);
            person.ValidFrom = request.ValidFrom;
            person.ValidTo = request.ValidTo;
            person.Enabled = request.Enabled;
            person.UpdatedAt = DateTime.UtcNow;

            // La clave no viaja de vuelta al formulario: vacío significa "no la
            // toque", y para quitarla hay que pedirlo explícitamente.
            if (request.ClearPin)
            {
                if (person.PinCiphertext.Length > 0) changes.Add("clave quitada");
                person.PinCiphertext = [];
            }
            else if (!string.IsNullOrWhiteSpace(request.PinCode))
            {
                person.PinCiphertext = protector.Protect(request.PinCode.Trim());
                changes.Add("clave cambiada");
            }

            var cards = request.Cards.Select(Normalize).Where(c => c.Length > 0).Distinct().ToList();
            var currentCards = person.Cards.Select(c => c.Number).ToList();
            if (!cards.OrderBy(c => c).SequenceEqual(currentCards.OrderBy(c => c)))
                changes.Add($"tarjetas {currentCards.Count} → {cards.Count}");
            db.AccessCards.RemoveRange(person.Cards.Where(c => !cards.Contains(c.Number)));
            foreach (string number in cards.Except(currentCards))
                person.Cards.Add(new AccessCard { AccessPersonId = person.Id, Number = number });

            var levels = request.LevelIds.Distinct().ToHashSet();
            var currentLevels = person.Levels.Select(x => x.AccessLevelId).ToHashSet();
            if (!levels.SetEquals(currentLevels))
                changes.Add($"niveles de acceso {currentLevels.Count} → {levels.Count}");
            db.AccessLevelPersons.RemoveRange(person.Levels.Where(x => !levels.Contains(x.AccessLevelId)));
            foreach (int levelId in levels.Except(currentLevels))
                person.Levels.Add(new AccessLevelPerson { AccessPersonId = person.Id, AccessLevelId = levelId });

            if (ApplyFingerprints(db, person, protector, request.Fingerprints) is { Length: > 0 } fingerChanges)
                changes.Add(fingerChanges);
            if (ApplyFace(db, person, protector, request.Face, request.ClearFace) is { Length: > 0 } faceChanges)
                changes.Add(faceChanges);

            person.SyncState = AccessSyncState.Pending;
            await db.SaveChangesAsync(ct);
            sync.RequestSync();

            await audit.LogAsync(ctx, "access", "person-updated",
                targetType: "access-person", targetId: person.Id.ToString(), targetName: person.FullName,
                detail: $"Modificó a '{person.FullName}' ({person.EmployeeNo}): " +
                        (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios."));
            return Results.Ok(AccessSyncService.ToPersonDto(await PersonQuery(db).FirstAsync(p => p.Id == id, ct)));
        });

        // La foto del rostro va por su propia dirección y no dentro del JSON de
        // la persona: pesa cientos de veces más que el resto de la ficha, y el
        // listado del padrón la pediría para todos sin necesitarla.
        app.MapGet("/api/access/persons/{id:int}/face", async (HttpContext ctx, int id, VmsDbContext db,
            CredentialProtector protector, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var face = await db.AccessFaces.AsNoTracking().FirstOrDefaultAsync(f => f.AccessPersonId == id, ct);
            if (face is null) return Results.NotFound();
            return Results.File(protector.UnprotectBytes(face.ImageCiphertext), face.ContentType);
        });

        app.MapDelete("/api/access/persons/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            AccessControlService access, AccessSyncService sync, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var person = await db.AccessPersons.Include(p => p.Devices).ThenInclude(d => d.AccessDevice)
                .FirstOrDefaultAsync(p => p.Id == id, ct);
            if (person is null) return Results.NotFound();

            // Borrar del VMS tiene que borrar de los equipos: una persona que
            // desaparece del padrón pero sigue en el terminal seguiría entrando.
            var failed = new List<string>();
            foreach (var row in person.Devices)
            {
                var device = row.AccessDevice;
                if (device is null || !access.SupportsPersonSync(device.DriverKey)) continue;
                try { await access.DriverOf(device).RemovePersonAsync(access.ConnectionOf(device), person.EmployeeNo, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { failed.Add($"{device.Name} ({ex.Message})"); }
            }

            db.AccessPersons.Remove(person);
            await db.SaveChangesAsync(ct);
            sync.RequestSync();

            await audit.LogAsync(ctx, "access", "person-deleted",
                targetType: "access-person", targetId: id.ToString(), targetName: person.FullName,
                detail: $"Eliminó del padrón a '{person.FullName}' ({person.EmployeeNo})." +
                        (failed.Count > 0 ? $" NO se pudo borrar de: {string.Join("; ", failed)}." : ""),
                success: failed.Count == 0);

            return failed.Count == 0
                ? Results.Ok()
                : Error("La persona se eliminó del VMS, pero no se pudo borrar de estos equipos y podría seguir " +
                        $"entrando por ellos: {string.Join("; ", failed)}. Revíselos.", StatusCodes.Status502BadGateway);
        });

        // Reintentar la escritura de UNA persona (la típica: se arregló el
        // equipo y el operador no quiere esperar el reintento solo).
        app.MapPost("/api/access/persons/{id:int}/sync", async (HttpContext ctx, int id, VmsDbContext db,
            AccessSyncService sync, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var person = await db.AccessPersons.Include(p => p.Devices).FirstOrDefaultAsync(p => p.Id == id, ct);
            if (person is null) return Results.NotFound();

            person.SyncState = AccessSyncState.Pending;
            // Olvidar la huella obliga a reescribir aunque el equipo ya la
            // tuviera: es lo que el operador está pidiendo con este botón.
            foreach (var row in person.Devices) row.AppliedHash = null;
            await db.SaveChangesAsync(ct);
            await sync.SyncPendingAsync(ct);

            await audit.LogAsync(ctx, "access", "person-synced",
                targetType: "access-person", targetId: id.ToString(), targetName: person.FullName,
                detail: $"Forzó la reescritura de '{person.FullName}' ({person.EmployeeNo}) en sus equipos.");
            return Results.Ok(AccessSyncService.ToPersonDto(await PersonQuery(db).FirstAsync(p => p.Id == id, ct)));
        });
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Las tarjetas se guardan sin espacios: el equipo las devuelve pegadas y tienen que coincidir.</summary>
    private static string Normalize(string card) => (card ?? "").Trim().Replace(" ", "");

    private static async Task<string?> ValidatePersonAsync(VmsDbContext db, AccessPersonWriteDto request, int? id,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.FirstName) || request.FirstName.Trim().Length > 64)
            return "El nombre es obligatorio (máximo 64 caracteres).";
        if (string.IsNullOrWhiteSpace(request.LastName) || request.LastName.Trim().Length > 64)
            return "El apellido es obligatorio (máximo 64 caracteres).";
        if (request.ValidTo <= request.ValidFrom)
            return "El fin de la vigencia tiene que ser posterior a su comienzo.";
        if (request.EmployeeNo is { Length: > 32 })
            return "El identificador no puede superar los 32 caracteres.";
        if (request.PinCode is { } pin && !string.IsNullOrWhiteSpace(pin) &&
            (pin.Trim().Length is < 4 or > 8 || !pin.Trim().All(char.IsDigit)))
            return "La clave de teclado son entre 4 y 8 dígitos.";

        var cards = request.Cards.Select(Normalize).Where(c => c.Length > 0).ToList();
        if (cards.Any(c => c.Length > 32)) return "Un número de tarjeta no puede superar los 32 caracteres.";
        if (cards.Distinct().Count() != cards.Count) return "La misma tarjeta está repetida en la lista.";
        var clash = await db.AccessCards.Include(c => c.AccessPerson)
            .FirstOrDefaultAsync(c => cards.Contains(c.Number) && (id == null || c.AccessPersonId != id), ct);
        if (clash is not null)
            return $"La tarjeta {clash.Number} ya es de {clash.AccessPerson?.FirstName} {clash.AccessPerson?.LastName}.";

        var levelIds = request.LevelIds.Distinct().ToList();
        if (levelIds.Count > 0 && await db.AccessLevels.CountAsync(l => levelIds.Contains(l.Id), ct) != levelIds.Count)
            return "Alguno de los niveles de acceso indicados ya no existe.";

        if (request.Fingerprints is { } fingers)
        {
            if (fingers.Select(f => f.Number).Distinct().Count() != fingers.Count)
                return "Hay un dedo repetido en la lista de huellas.";
            foreach (var finger in fingers)
            {
                if (!AccessFingers.IsValid(finger.Number))
                    return $"Número de dedo inválido: {finger.Number} (van del 1 al {AccessFingers.Count}).";
                if (finger.Template is null) continue;   // "dejar la que ya está"
                if (!IsBase64(finger.Template, out int bytes))
                    return $"La plantilla de {AccessFingers.NameOf(finger.Number)} no es una captura válida.";
                if (bytes is < 64 or > MaxTemplateBytes)
                    return $"La plantilla de {AccessFingers.NameOf(finger.Number)} tiene un tamaño inesperado ({bytes} bytes).";
            }
            // Una huella nueva (con plantilla) solo puede llegar de una captura:
            // al crear la persona no hay ninguna que "conservar".
            if (id is null && fingers.Any(f => f.Template is null))
                return "Falta la captura de alguna huella.";
        }

        if (request.Face is { Image: { Length: > 0 } image } face)
        {
            if (!AccessFacePhoto.IsAllowed(face.ContentType))
                return "La foto del rostro tiene que ser JPG o PNG.";
            if (!IsBase64Image(image, out int bytes))
                return "La foto del rostro no se pudo leer.";
            if (bytes > AccessFacePhoto.MaxBytes)
                return $"La foto del rostro pesa {bytes / 1024} KB; el máximo es {AccessFacePhoto.MaxBytes / 1024} KB.";
        }
        return null;
    }

    /// <summary>¿Es base64 de una imagen de tamaño razonable? Devuelve cuántos bytes trae.</summary>
    private static bool IsBase64Image(string value, out int bytes)
    {
        bytes = 0;
        // Se decodifica de a poco: una foto no entra en el buffer de las
        // plantillas y no hay que reservar de más por si viene basura.
        Span<byte> buffer = new byte[AccessFacePhoto.MaxBytes + 8];
        if (!Convert.TryFromBase64String(value, buffer, out int written)) return false;
        bytes = written;
        return bytes > 0;
    }

    /// <summary>
    /// Deja en la persona el rostro que mandó el panel: lo reemplaza, lo quita
    /// o no lo toca. Devuelve el resumen para la bitácora.
    /// </summary>
    private static string ApplyFace(VmsDbContext db, AccessPerson person, CredentialProtector protector,
        AccessFaceWriteDto? wanted, bool clear)
    {
        if (clear)
        {
            if (person.Face is not { } gone) return "";
            db.AccessFaces.Remove(gone);
            person.Face = null;
            return "quitó el rostro";
        }
        if (wanted?.Image is not { Length: > 0 } image) return "";   // null = no tocarlo

        byte[] bytes = Convert.FromBase64String(image);
        string contentType = (wanted.ContentType ?? "image/jpeg").Trim().ToLowerInvariant();
        bool replaced = person.Face is not null;

        if (person.Face is not { } face)
        {
            face = new AccessFace { AccessPersonId = person.Id };
            person.Face = face;
            db.AccessFaces.Add(face);
        }
        face.ImageCiphertext = protector.ProtectBytes(bytes);
        face.ContentType = contentType;
        face.Bytes = bytes.Length;
        face.Source = wanted.Source;
        face.CreatedAt = DateTime.UtcNow;
        return replaced ? "cambió el rostro" : "cargó el rostro";
    }

    /// <summary>Tope de una plantilla biométrica (la del lector USB son 512 bytes; se deja aire).</summary>
    private const int MaxTemplateBytes = 4096;

    /// <summary>¿Es base64 válido? Devuelve además cuántos bytes trae.</summary>
    private static bool IsBase64(string value, out int bytes)
    {
        bytes = 0;
        Span<byte> buffer = new byte[MaxTemplateBytes + 8];
        if (!Convert.TryFromBase64String(value, buffer, out int written)) return false;
        bytes = written;
        return true;
    }

    /// <summary>
    /// Deja en la persona exactamente las huellas que mandó el panel. Una que
    /// llega sin plantilla es una que ya estaba (el panel nunca la recibe, así
    /// que no puede reenviarla); un dedo que no viene en la lista se borra.
    /// Devuelve el resumen para la bitácora.
    /// </summary>
    private static string ApplyFingerprints(VmsDbContext db, AccessPerson person, CredentialProtector protector,
        IReadOnlyList<AccessFingerprintWriteDto>? wanted)
    {
        if (wanted is null) return "";   // el formulario no las administra: no se tocan

        var changes = new List<string>();
        foreach (var gone in person.Fingerprints.Where(f => wanted.All(w => w.Number != f.Number)).ToList())
        {
            db.AccessFingerprints.Remove(gone);
            person.Fingerprints.Remove(gone);
            changes.Add($"{AccessFingers.NameOf(gone.Number)} borrada");
        }

        foreach (var write in wanted)
        {
            var existing = person.Fingerprints.FirstOrDefault(f => f.Number == write.Number);
            if (write.Template is null)
            {
                if (existing is null) changes.Add($"{AccessFingers.NameOf(write.Number)} sin captura");
                continue;
            }
            if (existing is null)
            {
                person.Fingerprints.Add(new AccessFingerprint
                {
                    AccessPersonId = person.Id,
                    Number = write.Number,
                    TemplateCiphertext = protector.Protect(write.Template),
                    Quality = write.Quality,
                    Source = Trimmed(write.Source),
                });
                changes.Add($"{AccessFingers.NameOf(write.Number)} enrolada");
            }
            else
            {
                existing.TemplateCiphertext = protector.Protect(write.Template);
                existing.Quality = write.Quality;
                existing.Source = Trimmed(write.Source);
                existing.CreatedAt = DateTime.UtcNow;
                changes.Add($"{AccessFingers.NameOf(write.Number)} recapturada");
            }
        }
        return changes.Count > 0 ? string.Join(", ", changes) : "";
    }

    /// <summary>
    /// Identificador para una persona nueva: el mayor numérico que haya, más
    /// uno. Se numeran desde 1000 para que se distingan a simple vista de los
    /// que puedan venir cargados de fábrica en un equipo.
    /// </summary>
    private static async Task<string> NextEmployeeNoAsync(VmsDbContext db, CancellationToken ct)
    {
        var existing = await db.AccessPersons.Select(p => p.EmployeeNo).ToListAsync(ct);
        int highest = 999;
        foreach (string value in existing)
            if (int.TryParse(value, out int number) && number > highest) highest = number;
        return (highest + 1).ToString();
    }

    // ==================================================================
    // Historial
    // ==================================================================

    private static void MapEvents(WebApplication app)
    {
        app.MapGet("/api/access/events", async (HttpContext ctx, VmsDbContext db, AuditService audit,
            DateTime? from, DateTime? to, int? deviceId, int? doorId, int? personId, string? kind, string? q,
            int? page, int? pageSize, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            (int pageNumber, int size) = Paging(page, pageSize);

            var query = db.AccessEvents.AsNoTracking().AsQueryable();
            if (from is { } start) query = query.Where(e => e.Timestamp >= start);
            if (to is { } end) query = query.Where(e => e.Timestamp <= end);
            if (deviceId is int device) query = query.Where(e => e.AccessDeviceId == device);
            if (personId is int person) query = query.Where(e => e.AccessPersonId == person);
            if (Enum.TryParse<AccessEventKind>(kind, ignoreCase: true, out var eventKind))
                query = query.Where(e => e.Kind == eventKind);
            if (doorId is int door)
            {
                // La puerta se identifica en el historial por equipo + número,
                // no por su Id: el historial sobrevive a que se borre la puerta.
                var target = await db.AccessDoors.AsNoTracking().FirstOrDefaultAsync(d => d.Id == door, ct);
                if (target is null) return Error("La puerta indicada ya no existe.");
                query = query.Where(e => e.AccessDeviceId == target.AccessDeviceId && e.DoorNumber == target.Number);
            }
            if (!string.IsNullOrWhiteSpace(q))
            {
                string term = q.Trim();
                query = query.Where(e => (e.PersonName != null && EF.Functions.ILike(e.PersonName, $"%{term}%")) ||
                                         (e.EmployeeNo != null && EF.Functions.ILike(e.EmployeeNo, $"%{term}%")) ||
                                         (e.CardNumber != null && EF.Functions.ILike(e.CardNumber, $"%{term}%")) ||
                                         EF.Functions.ILike(e.Description, $"%{term}%"));
            }

            int total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(e => e.Timestamp)
                .Skip((pageNumber - 1) * size).Take(size).ToListAsync(ct);

            // Solo se audita la búsqueda con filtros: el refresco automático de
            // la pantalla llenaría la bitácora de ruido.
            if (pageNumber == 1 && (from is not null || personId is not null || !string.IsNullOrWhiteSpace(q)))
                await audit.LogAsync(ctx, "access", "search",
                    detail: $"Consultó el historial de accesos ({total} resultado(s))" +
                            (q is { Length: > 0 } ? $" buscando '{q}'." : "."));

            return Results.Ok(new AccessEventPageDto(total, pageNumber, size,
                items.Select(AccessEventService.ToDto).ToList()));
        });
    }
}
