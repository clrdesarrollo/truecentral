using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Licensing;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// API de los módulos de entradas y salidas: mantenedor (Módulos de E/S),
/// estado en vivo de entradas y salidas, y órdenes a los relés (con permiso
/// «Operar relés» y el módulo dentro del alcance). Todo queda en la bitácora.
/// </summary>
public static class IoModulesApi
{
    private const int MaxPointName = 64;

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    private static string? ValidateWrite(IoModuleWriteDto request, IoModuleDriverRegistry drivers)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
            return "El nombre es obligatorio (máximo 128 caracteres).";
        if (string.IsNullOrWhiteSpace(request.Host) || request.Host.Trim().Length > 255)
            return "La dirección (IP o hostname) es obligatoria.";
        if (request.Port is < 1 or > 65535)
            return "El puerto debe estar entre 1 y 65535.";
        if (request.PollIntervalMs is < IoModuleService.MinPollMs or > IoModuleService.MaxPollMs)
            return $"La lectura de las entradas debe ser cada {IoModuleService.MinPollMs} a {IoModuleService.MaxPollMs} ms.";
        if (drivers.Find(request.DriverKey) is not { } factory)
            return $"Driver desconocido: '{request.DriverKey}'.";
        if (factory.Internal)
            return "Ese driver lo usa el servidor para las entradas y salidas de otros equipos (terminales de acceso).";
        if (ModelKeyOf(request) is { } key && factory.Create().Models.All(m => !string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase)))
            return $"Modelo desconocido para ese driver: '{key}'.";
        if (request.Points?.FirstOrDefault(p => (p.Name?.Trim().Length ?? 0) > MaxPointName) is { } longName)
            return $"El nombre de {Code(longName.Kind, longName.Number)} no puede superar los {MaxPointName} caracteres.";
        return null;
    }

    private static string? ModelKeyOf(IoModuleWriteDto request) =>
        string.IsNullOrWhiteSpace(request.ModelKey) ? null : request.ModelKey.Trim();

    private static string Code(IoPointKind kind, int number) => (kind == IoPointKind.Input ? "DI" : "DO") + number;

    /// <summary>Prueba la conexión. Devuelve la identificación o el motivo del fallo (nunca lanza).</summary>
    private static async Task<(IoModuleInfo? Info, string? Error)> ProbeAsync(IoModuleDriverRegistry drivers,
        string driverKey, string host, int port, string? modelKey, CancellationToken ct)
    {
        try
        {
            var info = await drivers.Find(driverKey)!.Create().ProbeAsync(new IoConnectionInfo(host, port), modelKey, ct);
            return (info, null);
        }
        catch (DriverException ex) { return (null, ex.Message); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return (null, $"Error inesperado al comunicarse con el módulo: {ex.Message}"); }
    }

    /// <summary>Aviso cuando el modelo elegido a mano no es el que informa el equipo.</summary>
    private static string? MismatchWarning(IoModuleInfo info) =>
        info.ReportedModel is { } reported && !string.Equals(reported, info.Model.Key, StringComparison.OrdinalIgnoreCase)
            ? $"El equipo se identifica como {reported}, pero se trabajará como {info.Model.Name} (elegido a mano)."
            : null;

    /// <summary>
    /// Deja una fila por cada entrada y salida que tiene el modelo (nombre por
    /// omisión = su rótulo, DI0/DO0), quita las que el modelo ya no tiene y
    /// aplica los nombres y la lógica que vienen en el formulario. Devuelve los
    /// cambios en palabras, para la bitácora.
    /// </summary>
    private static List<string> SyncPoints(IoModule module, IoModuleModel? model, IReadOnlyList<IoPointWriteDto>? requested)
    {
        var changes = new List<string>();
        if (model is not null)
        {
            module.Points.RemoveAll(p => p.Number >= (p.Kind == IoPointKind.Input ? model.Inputs : model.Outputs));
            void Ensure(IoPointKind kind, int count)
            {
                for (int n = 0; n < count; n++)
                    if (!module.Points.Any(p => p.Kind == kind && p.Number == n))
                        module.Points.Add(new IoPoint { Kind = kind, Number = n, Name = Code(kind, n) });
            }
            Ensure(IoPointKind.Input, model.Inputs);
            Ensure(IoPointKind.Output, model.Outputs);
        }

        foreach (var item in requested ?? [])
        {
            var point = module.Points.FirstOrDefault(p => p.Kind == item.Kind && p.Number == item.Number);
            if (point is null) continue;
            string name = string.IsNullOrWhiteSpace(item.Name) ? point.Code : item.Name.Trim();
            if (point.Name != name)
            {
                changes.Add($"{point.Code} '{point.Name}' → '{name}'");
                point.Name = name;
            }
            if (point.Kind == IoPointKind.Input && point.Inverted != item.Inverted)
            {
                changes.Add($"{point.Code} {(item.Inverted ? "activa al cerrar el contacto" : "activa al abrir el contacto")}");
                point.Inverted = item.Inverted;
            }
        }
        return changes;
    }

    /// <summary>
    /// Entradas y salidas de un terminal de acceso: la conexión, el modelo, la
    /// ubicación y si está activo los pone el terminal. Acá solo se cambian el
    /// los nombres de sus entradas y salidas (el del módulo es el del terminal).
    /// </summary>
    private static async Task<IResult> UpdateTerminalModuleAsync(HttpContext ctx, IoModule module, IoModuleWriteDto request,
        VmsDbContext db, IHubContext<VmsHub> hub, IoModuleService service, AuditService audit, CancellationToken ct)
    {
        if (request.Points?.FirstOrDefault(p => (p.Name?.Trim().Length ?? 0) > MaxPointName) is { } longName)
            return Error($"El nombre de {Code(longName.Kind, longName.Number)} no puede superar los {MaxPointName} caracteres.");
        var changes = new List<string>();
        // La lógica (activa al cerrar o abrir) la informa el terminal: solo los nombres.
        // Sin nombre vuelve al de fábrica del terminal ("Entrada de alarma 2"), no al rótulo DI1.
        var names = request.Points?.Select(p => p with
        {
            Name = string.IsNullOrWhiteSpace(p.Name) ? EquipmentParts.DefaultPointName(p.Kind, p.Number) : p.Name,
            Inverted = module.Points.FirstOrDefault(x => x.Kind == p.Kind && x.Number == p.Number)?.Inverted ?? false,
        }).ToList();
        changes.AddRange(SyncPoints(module, null, names));
        module.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(ctx, "io", "io-module-updated",
            targetType: "io-module", targetId: module.Id.ToString(), targetName: module.Name,
            detail: $"Modificó las entradas y salidas del terminal '{module.Name}': " +
                    (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios."));
        service.RequestReconcile();
        await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "io-modules", cancellationToken: ct);
        return Results.Ok(new { module = service.ToDto(module), warning = (string?)null });
    }

    public static void MapIoModulesApi(this WebApplication app)
    {
        // ------------------------------------------------------------------
        // Catálogo
        // ------------------------------------------------------------------
        app.MapGet("/api/io/drivers", (HttpContext ctx, IoModuleDriverRegistry drivers) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.Ok(drivers.All.Select(f =>
            {
                var driver = f.Create();
                return new IoDriverDto(f.DriverKey, f.DisplayName, f.DefaultPort,
                    driver.Models.Select(m => new IoModelDto(m.Key, m.Name, m.Inputs, m.Outputs, m.Description)).ToList(),
                    (int)driver.MinBlinkInterval.TotalMilliseconds, f.Internal);
            }));
        });

        // ------------------------------------------------------------------
        // Mantenedor
        // ------------------------------------------------------------------
        app.MapGet("/api/io/modules", async (HttpContext ctx, VmsDbContext db, IoModuleService service, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAny(ctx, out var session, Permissions.IoOperate, Permissions.IoConfigure) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var modules = await db.IoModules.AsNoTracking().Include(m => m.Points).OrderBy(m => m.Name).ToListAsync(ct);
            return Results.Ok(modules.Where(m => scope.CanViewIoModule(m.Id)).Select(service.ToDto));
        });

        app.MapGet("/api/io/modules/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, IoModuleService service, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAny(ctx, out var session, Permissions.IoOperate, Permissions.IoConfigure) is { } failure) return failure;
            var module = await db.IoModules.AsNoTracking().Include(m => m.Points).FirstOrDefaultAsync(m => m.Id == id, ct);
            return module is null || !(await ctx.ScopeAsync(session)).CanViewIoModule(module.Id)
                ? Results.NotFound()
                : Results.Ok(service.ToDto(module));
        });

        app.MapPost("/api/io/modules", async (HttpContext ctx, IoModuleWriteDto request, VmsDbContext db, IoModuleDriverRegistry drivers,
            LicenseService license, IHubContext<VmsHub> hub, IoModuleService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.IoConfigure, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            if (license.Deny(LicenseFeatures.ModuleIo, request.Enabled ? LicenseFeatures.IoModules : null,
                    await db.IoModules.CountAsync(m => m.Enabled && m.AccessDeviceId == null && m.DeviceId == null, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "io-module", request.Name.Trim());
            string host = request.Host.Trim();
            if (await db.IoModules.AnyAsync(m => m.Host == host && m.Port == request.Port, ct))
                return Error("Ya existe un módulo con esa dirección y puerto.", StatusCodes.Status409Conflict);
            var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, null, ct);
            if (locationError is not null) return Error(locationError, StatusCodes.Status404NotFound);

            // Con el modelo elegido a mano se puede dar de alta aunque el módulo
            // no responda (se instala apagado); sin él hay que leerlo del equipo.
            string? modelKey = ModelKeyOf(request);
            var (info, probeError) = await ProbeAsync(drivers, request.DriverKey, host, request.Port, modelKey, ct);
            if (info is null && modelKey is null)
            {
                await audit.LogAsync(ctx, "io", "io-module-created", targetType: "io-module", targetName: request.Name.Trim(),
                    detail: $"Alta de módulo de E/S rechazada: {host}:{request.Port} no respondió ({probeError}).", success: false);
                return Error($"{probeError} Si el módulo todavía no está conectado, elija el modelo a mano para darlo de alta igual.");
            }
            var model = info?.Model ?? service.ModelOf(request.DriverKey, modelKey)!;

            var module = new IoModule
            {
                Name = request.Name.Trim(),
                DriverKey = request.DriverKey,
                Host = host,
                Port = request.Port,
                ModelKey = modelKey,
                Model = model.Key,
                ReportedModel = info?.ReportedModel,
                PollIntervalMs = request.PollIntervalMs,
                LocationId = locationId,
                Enabled = request.Enabled,
                Status = info is null ? IoModuleStatus.Offline : IoModuleStatus.Online,
                LastError = info is null ? probeError : null,
                LastSeenAt = info is null ? null : DateTime.UtcNow,
            };
            SyncPoints(module, model, request.Points);
            db.IoModules.Add(module);
            await db.SaveChangesAsync(ct);

            string? warning = info is null
                ? $"El módulo no respondió ({probeError}); quedó dado de alta y el servidor lo seguirá intentando."
                : MismatchWarning(info);
            await audit.LogAsync(ctx, "io", "io-module-created",
                targetType: "io-module", targetId: module.Id.ToString(), targetName: module.Name,
                detail: $"Agregó el módulo de E/S '{module.Name}' ({module.Host}:{module.Port}, {model.Name}" +
                        (modelKey is null ? "" : " elegido a mano") +
                        (module.LocationId is null ? "" : $", en {EquipmentLocation.Describe(module.LocationId)}") +
                        $", {model.Inputs} entradas y {model.Outputs} salidas)." + (warning is null ? "" : $" {warning}"),
                data: new { module.Host, module.Port, module.DriverKey, module.Model, module.ModelKey, module.ReportedModel, module.LocationId });
            EquipmentLocation.Changed(ctx);
            service.RequestReconcile();
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "io-modules", cancellationToken: ct);
            return Results.Ok(new { module = service.ToDto(module), warning });
        });

        app.MapPut("/api/io/modules/{id:int}", async (HttpContext ctx, int id, IoModuleWriteDto request, VmsDbContext db,
            IoModuleDriverRegistry drivers, LicenseService license, IHubContext<VmsHub> hub, IoModuleService service,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.IoConfigure, out _) is { } failure) return failure;
            var module = await db.IoModules.Include(m => m.Points).FirstOrDefaultAsync(m => m.Id == id, ct);
            if (module is null) return Results.NotFound();
            if (module.IsPart)
                return await UpdateTerminalModuleAsync(ctx, module, request, db, hub, service, audit, ct);
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            string host = request.Host.Trim();
            if (await db.IoModules.AnyAsync(m => m.Id != id && m.Host == host && m.Port == request.Port, ct))
                return Error("Ya existe otro módulo con esa dirección y puerto.", StatusCodes.Status409Conflict);
            var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, module.LocationId, ct);
            if (locationError is not null) return Error(locationError, StatusCodes.Status404NotFound);
            if (!module.Enabled && request.Enabled
                && license.Deny(LicenseFeatures.ModuleIo, LicenseFeatures.IoModules,
                    await db.IoModules.CountAsync(m => m.Enabled && m.Id != id && m.AccessDeviceId == null && m.DeviceId == null, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "io-module", module.Name);

            var changes = new List<string>();
            string? warning = null;
            string? modelKey = ModelKeyOf(request);
            bool connectionChanged = module.Host != host || module.Port != request.Port ||
                                     module.DriverKey != request.DriverKey || module.ModelKey != modelKey;
            var model = service.ModelOf(module.DriverKey, module.Model);
            if (connectionChanged)
            {
                var (info, probeError) = await ProbeAsync(drivers, request.DriverKey, host, request.Port, modelKey, ct);
                if (info is null && modelKey is null)
                    return Error($"{probeError} Si el módulo todavía no está conectado, elija el modelo a mano.");
                var newModel = info?.Model ?? service.ModelOf(request.DriverKey, modelKey)!;
                if (module.Model != newModel.Key) changes.Add($"modelo {module.Model ?? "?"} → {newModel.Name}");
                if (module.ModelKey != modelKey) changes.Add(modelKey is null ? "modelo leído del equipo" : "modelo elegido a mano");
                module.Model = newModel.Key;
                module.ReportedModel = info?.ReportedModel ?? module.ReportedModel;
                module.Status = info is null ? IoModuleStatus.Offline : IoModuleStatus.Online;
                module.LastError = info is null ? probeError : null;
                if (info is not null) module.LastSeenAt = DateTime.UtcNow;
                warning = info is null ? $"El módulo no respondió ({probeError}); el servidor lo seguirá intentando." : MismatchWarning(info);
                model = newModel;
            }

            if (module.Name != request.Name.Trim()) changes.Add($"nombre '{module.Name}' → '{request.Name.Trim()}'");
            if (module.Host != host || module.Port != request.Port) changes.Add($"dirección {module.Host}:{module.Port} → {host}:{request.Port}");
            if (module.PollIntervalMs != request.PollIntervalMs) changes.Add($"lectura cada {module.PollIntervalMs} → {request.PollIntervalMs} ms");
            int? previousLocation = module.LocationId;
            if (locationId != previousLocation) changes.Add(EquipmentLocation.Change(previousLocation, locationId));
            if (module.Enabled != request.Enabled) changes.Add(request.Enabled ? "activado" : "pausado");

            module.Name = request.Name.Trim();
            module.DriverKey = request.DriverKey;
            module.Host = host;
            module.Port = request.Port;
            module.ModelKey = modelKey;
            module.PollIntervalMs = request.PollIntervalMs;
            module.LocationId = locationId;
            module.Enabled = request.Enabled;
            module.UpdatedAt = DateTime.UtcNow;
            changes.AddRange(SyncPoints(module, model, request.Points));
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "io", "io-module-updated",
                targetType: "io-module", targetId: module.Id.ToString(), targetName: module.Name,
                detail: $"Modificó el módulo de E/S '{module.Name}': " + (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios."));
            if (locationId != previousLocation) await EquipmentLocation.MovedAsync(ctx, ct);
            if (!module.Enabled) await service.DetachAsync(module.Id);
            service.RequestReconcile();
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "io-modules", cancellationToken: ct);
            return Results.Ok(new { module = service.ToDto(module), warning });
        });

        app.MapDelete("/api/io/modules/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, IHubContext<VmsHub> hub,
            IoModuleService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.IoConfigure, out _) is { } failure) return failure;
            var module = await db.IoModules.FindAsync([id], ct);
            if (module is null) return Results.NotFound();
            if (module.IsPart)
                return Error("Son las entradas y salidas de un terminal de acceso o de una cámara: se quitan desde ese equipo " +
                             "(Control de acceso → Equipos, o Fuentes de video).", StatusCodes.Status409Conflict);
            // Primero se cierra la conexión: suelta los pulsos y parpadeos en curso.
            await service.DetachAsync(id);
            db.IoModules.Remove(module);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "io", "io-module-deleted",
                targetType: "io-module", targetId: id.ToString(), targetName: module.Name,
                detail: $"Eliminó el módulo de E/S '{module.Name}' ({module.Host}:{module.Port}).");
            EquipmentLocation.Changed(ctx);
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "io-modules", cancellationToken: ct);
            return Results.Ok();
        });

        // Probar conexión (sin guardar nada).
        app.MapPost("/api/io/modules/probe", async (HttpContext ctx, IoModuleWriteDto request, IoModuleDriverRegistry drivers,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.IoConfigure, out _) is { } failure) return failure;
            if (ValidateWrite(request with { Name = string.IsNullOrWhiteSpace(request.Name) ? "-" : request.Name }, drivers) is { } invalid)
                return Error(invalid);
            string host = request.Host.Trim();
            var (info, probeError) = await ProbeAsync(drivers, request.DriverKey, host, request.Port, ModelKeyOf(request), ct);
            await audit.LogAsync(ctx, "io", "io-module-probed", targetType: "io-module", targetName: request.Name?.Trim(),
                detail: info is null
                    ? $"Probó la conexión con el módulo de E/S {host}:{request.Port}: {probeError}"
                    : $"Probó la conexión con el módulo de E/S {host}:{request.Port}: correcta ({info.ReportedModel ?? info.Model.Name}).",
                success: info is not null);
            return Results.Ok(info is null
                ? new IoProbeResultDto(false, probeError, null, null, 0, 0, null)
                : new IoProbeResultDto(true, null, info.ReportedModel, info.Model.Name, info.Model.Inputs, info.Model.Outputs,
                    MismatchWarning(info)));
        });

        // ------------------------------------------------------------------
        // Órdenes a las salidas (operador)
        // ------------------------------------------------------------------
        app.MapPost("/api/io/modules/{id:int}/outputs/{number:int}", async (HttpContext ctx, int id, int number,
            IoOutputCommandDto request, VmsDbContext db, IoModuleService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.IoOperate, out var session) is { } failure) return failure;
            var module = await db.IoModules.AsNoTracking().Include(m => m.Points).FirstOrDefaultAsync(m => m.Id == id, ct);
            if (module is null) return Results.NotFound();
            var scope = await ctx.ScopeAsync(session);
            if (!scope.CanViewIoModule(id)) return Results.NotFound();
            if (!scope.CanOperateIoModule(id))
                return await ctx.OutOfScopeAsync(session, "io-module", id.ToString(), module.Name, "operar un relé del módulo de E/S");
            if (!module.Enabled) return Error($"El módulo '{module.Name}' está pausado.");

            var outcome = await service.CommandAsync(id, number, request.Command, request.Seconds, request.IntervalMs, ct);
            await audit.LogAsync(ctx, "io", "output-command",
                targetType: "io-module", targetId: id.ToString(), targetName: module.Name,
                detail: outcome.Success ? $"Orden a mano desde el panel: {outcome.Message}" : outcome.Message,
                success: outcome.Success,
                data: new { output = number, request.Command, request.Seconds, request.IntervalMs });
            if (!outcome.Success) return Error(outcome.Message);
            return Results.Ok(new IoCommandResultDto(true, outcome.Message));
        });
    }
}
