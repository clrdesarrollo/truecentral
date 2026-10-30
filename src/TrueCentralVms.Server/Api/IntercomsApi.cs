using System.Net.WebSockets;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Hikvision;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Isup;
using TrueCentralVms.Server.Services.Licensing;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// API del módulo Citofonía: mantenedor de frentes (administrador), llamadas
/// en curso y su historial, contestar/rechazar/colgar, apertura de puerta
/// (cualquier usuario con sesión, todo auditado) y el WebSocket de voz
/// bidireccional del operador con el frente.
/// </summary>
public static class IntercomsApi
{
    private static readonly TimeSpan VoiceIdleTimeout = TimeSpan.FromSeconds(30);

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    /// <param name="isupKeyStored">El frente ya tiene clave ISUP guardada (en edición, dejarla vacía la conserva).</param>
    private static string? ValidateWrite(IntercomWriteDto request, IntercomDriverRegistry drivers, bool isupKeyStored = false)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
            return "El nombre es obligatorio (máximo 128 caracteres).";
        if (request.GroupName is { Length: > 64 })
            return "El grupo no puede superar los 64 caracteres.";
        if (drivers.Find(request.DriverKey) is not { } factory)
            return $"Driver desconocido: '{request.DriverKey}'.";
        // Por ISUP el frente se conecta solo: se piden su ID y su clave, no dirección ni credenciales.
        if (factory.UsesIsup)
            return IsupEquipmentRules.Validate(request.IsupDeviceId, request.IsupKey, isupKeyStored);
        if (string.IsNullOrWhiteSpace(request.Host))
            return "La dirección (IP o hostname) es obligatoria.";
        if (request.Port is < 1 or > 65535 || request.HttpPort is < 1 or > 65535 || request.RtspPort is < 1 or > 65535)
            return "Los puertos deben estar entre 1 y 65535.";
        if (string.IsNullOrWhiteSpace(request.Username))
            return "El usuario del frente es obligatorio.";
        return null;
    }

    private static bool UsesIsup(IntercomDriverRegistry drivers, string driverKey) => drivers.Find(driverKey)?.UsesIsup == true;

    /// <summary>Dónde está el frente, para la bitácora: dirección y puerto, o su ID ISUP.</summary>
    private static string WhereOf(Intercom i) => i.IsupDeviceId is { } isupId ? $"ISUP {isupId}" : $"{i.Host}:{i.Port}";

    private static string? GroupOf(IntercomWriteDto request) =>
        string.IsNullOrWhiteSpace(request.GroupName) ? null : request.GroupName.Trim();

    private static async Task<(IntercomInfo? Info, string? Error)> ProbeAsync(IntercomDriverRegistry drivers, string driverKey,
        IntercomConnectionInfo conn, CancellationToken ct)
    {
        try { return (await drivers.Find(driverKey)!.Create().ProbeAsync(conn, ct), null); }
        catch (DriverException ex) { return (null, ex.Message); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, $"Error inesperado al comunicarse con el frente: {ex.Message}");
        }
    }

    internal static void ApplyProbe(Intercom intercom, IntercomInfo info)
    {
        intercom.Model = info.Model;
        intercom.SerialNumber = info.SerialNumber;
        intercom.FirmwareVersion = info.FirmwareVersion;
        intercom.DoorCount = Math.Max(1, info.DoorCount);
        intercom.CallCenterEnabled = info.CallCenterEnabled;
        intercom.Status = IntercomStatus.Online;
        intercom.LastError = null;
        intercom.LastSeenAt = DateTime.UtcNow;
    }

    /// <summary>Configura el botón del frente para llamar a la central; devuelve el error (o null) sin lanzar.</summary>
    private static async Task<string?> ConfigureCallCenterAsync(IntercomDriverRegistry drivers, Intercom intercom,
        IntercomConnectionInfo conn, CancellationToken ct)
    {
        if (intercom.CallCenterEnabled) return null;
        try
        {
            await drivers.Find(intercom.DriverKey)!.Create().EnableCallCenterAsync(conn, ct);
            intercom.CallCenterEnabled = true;
            return null;
        }
        catch (DriverException ex) { return ex.Message; }
    }

    /// <summary>Deja el video con un I-frame por segundo si viene con uno cada más de 2 s; devuelve el error (o null) sin lanzar.</summary>
    private static async Task<string?> OptimizeVideoAsync(IntercomDriverRegistry drivers, string driverKey,
        IntercomConnectionInfo conn, CancellationToken ct)
    {
        try
        {
            await drivers.Find(driverKey)!.Create().OptimizeVideoAsync(conn, ct);
            return null;
        }
        catch (DriverException ex) { return ex.Message; }
    }

    private static Task AuditVideoAsync(HttpContext ctx, AuditService audit, Intercom intercom, IntercomInfo info, string? error) =>
        audit.LogAsync(ctx, "intercom", "video-optimized", targetType: "intercom", targetId: intercom.Id.ToString(),
            targetName: intercom.Name, success: error is null,
            detail: error is null
                ? $"Ajustó el video de '{intercom.Name}' a un cuadro completo por segundo (venía cada {info.KeyFrameSeconds:0.#} s: la imagen tardaba en aparecer)."
                : $"No se pudo ajustar el video de '{intercom.Name}' (cuadro completo cada {info.KeyFrameSeconds:0.#} s): {error}");

    private static async Task<IResult?> ValidateChannelAsync(VmsDbContext db, int? channelId, CancellationToken ct) =>
        channelId is int id && !await db.Channels.AnyAsync(c => c.Id == id, ct)
            ? Error("El canal de video elegido no existe.")
            : null;

    private static async Task<Intercom?> LoadAsync(VmsDbContext db, int id, CancellationToken ct) =>
        await db.Intercoms.Include(i => i.Channel).ThenInclude(c => c!.Device).FirstOrDefaultAsync(i => i.Id == id, ct);

    private static IResult Command(IntercomService.CommandResult result) =>
        result.Success
            ? Results.Ok(new IntercomActionResultDto(true, result.Message, result.Call))
            : Results.Json(new { error = result.Message, call = result.Call }, statusCode: result.StatusCode);

    /// <summary>
    /// Alcance por ubicación de una orden sobre una llamada: vale el del frente
    /// que llama (se busca antes de actuar). Si la llamada no existe, lo dice el servicio.
    /// </summary>
    private static async Task<IResult?> CallOutOfScopeAsync(HttpContext ctx, SessionInfo session, long callId, string attempted)
    {
        var db = ctx.RequestServices.GetRequiredService<VmsDbContext>();
        if (await db.IntercomCalls.AsNoTracking().Where(c => c.Id == callId).Select(c => (int?)c.IntercomId)
                .FirstOrDefaultAsync(ctx.RequestAborted) is not { } intercomId)
            return null;
        return (await ctx.ScopeAsync(session)).CanOperateIntercom(intercomId)
            ? null
            : await ctx.OutOfScopeAsync(session, "intercom", intercomId.ToString(), null, attempted);
    }

    public static void MapIntercomsApi(this WebApplication app)
    {
        // ------------------------------------------------------------------
        // Mantenedor
        // ------------------------------------------------------------------
        app.MapGet("/api/intercoms/drivers", (HttpContext ctx, IntercomDriverRegistry drivers) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.Ok(drivers.All.Select(f => new IntercomDriverDto(f.DriverKey, f.DisplayName, f.DefaultPort, f.DefaultHttpPort, f.UsesIsup)));
        });

        // Canales de video elegibles como cámara del frente (selector del mantenedor).
        app.MapGet("/api/intercoms/channels", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.IntercomConfigure, out _) is { } failure) return failure;
            var channels = await db.Channels.AsNoTracking()
                .OrderBy(c => c.Device.Name).ThenBy(c => c.ChannelNumber)
                .Select(c => new { c.Id, Name = c.Device.Name + " · " + c.Name, c.Device.Host, c.Enabled })
                .ToListAsync(ct);
            return Results.Ok(channels);
        });

        app.MapGet("/api/intercoms", async (HttpContext ctx, VmsDbContext db, IntercomService service, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var intercoms = await db.Intercoms.AsNoTracking().Include(i => i.Channel).ThenInclude(c => c!.Device)
                .OrderBy(i => i.GroupName).ThenBy(i => i.Name).ToListAsync(ct);
            return Results.Ok(intercoms.Where(i => scope.CanViewIntercom(i.Id)).Select(service.ToDto));
        });

        app.MapGet("/api/intercoms/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, IntercomService service, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var intercom = await LoadAsync(db, id, ct);
            return intercom is null || !(await ctx.ScopeAsync(session)).CanViewIntercom(intercom.Id)
                ? Results.NotFound()
                : Results.Ok(service.ToDto(intercom));
        });

        app.MapPost("/api/intercoms", async (HttpContext ctx, IntercomWriteDto request, VmsDbContext db, IntercomDriverRegistry drivers,
            LicenseService license, CredentialProtector protector, IHubContext<VmsHub> hub, IntercomService service, AuditService audit,
            MediaMtxManager mtx, IsupService isup, IsupHostManager isupHost, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.IntercomConfigure, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            bool viaIsup = UsesIsup(drivers, request.DriverKey);
            if (!viaIsup && string.IsNullOrEmpty(request.Password)) return Error("La contraseña del frente es obligatoria.");
            if (await ValidateChannelAsync(db, request.ChannelId, ct) is { } badChannel) return badChannel;
            if (license.Deny(LicenseFeatures.ModuleIntercom, request.Enabled ? LicenseFeatures.IntercomDevices : null,
                    await db.Intercoms.CountAsync(i => i.Enabled, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "intercom", request.Name?.Trim());
            string host = viaIsup ? "" : request.Host.Trim();
            string? isupId = viaIsup ? request.IsupDeviceId!.Trim() : null;
            if (isupId is not null)
            {
                if (await db.Intercoms.AnyAsync(i => i.IsupDeviceId == isupId, ct))
                    return Error($"Ya existe un frente con el ID ISUP {isupId}.", StatusCodes.Status409Conflict);
                if (await IsupEquipmentRules.KeyConflictAsync(db, protector, isupId, request.IsupKey, IsupEquipmentRules.Intercom, null, ct) is { } conflict)
                    return Error(conflict, StatusCodes.Status409Conflict);
            }
            else if (await db.Intercoms.AnyAsync(i => i.Host == host && i.Port == request.Port, ct))
                return Error("Ya existe un frente con esa dirección y puerto.", StatusCodes.Status409Conflict);
            var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, null, ct);
            if (locationError is not null) return Error(locationError, StatusCodes.Status404NotFound);

            var intercom = new Intercom
            {
                Name = request.Name.Trim(),
                DriverKey = request.DriverKey,
                Host = host,
                Port = viaIsup ? 0 : request.Port,
                HttpPort = viaIsup ? 0 : request.HttpPort,
                RtspPort = viaIsup ? 0 : request.RtspPort,
                Username = viaIsup ? "" : request.Username,
                PasswordCiphertext = viaIsup ? [] : protector.Protect(request.Password!),
                IsupDeviceId = isupId,
                IsupKeyCiphertext = viaIsup ? IsupEquipmentRules.ProtectKey(request.IsupKey, protector) : null,
                GroupName = GroupOf(request),
                ChannelId = request.ChannelId,
                LocationId = locationId,
                Enabled = request.Enabled,
            };
            var conn = service.ConnectionOf(intercom);

            // Por ISUP el frente solo se conecta una vez que el servidor lo conoce: se guarda y en su primer contacto se leen sus
            // datos, se configura su botón y se ajusta su video (IntercomService).
            IntercomInfo? info = null;
            string? callCenterError = null, videoError = null;
            bool optimize = false;
            if (isupId is not null && !isup.IsOnline(isupId))
            {
                intercom.Status = IntercomStatus.Offline;
                intercom.LastError = await IsupEquipmentRules.NotConnectedMessageAsync(isup, isupHost, isupId, "frente", ct);
            }
            else
            {
                (info, string? probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
                if (info is null)
                {
                    await audit.LogAsync(ctx, "intercom", "intercom-created", targetType: "intercom", targetName: request.Name.Trim(),
                        detail: $"Alta de frente de citofonía rechazada por el equipo {WhereOf(intercom)}: {probeError}", success: false);
                    return Error(probeError!);
                }
                ApplyProbe(intercom, info);
                callCenterError = request.ConfigureCallCenter ? await ConfigureCallCenterAsync(drivers, intercom, conn, ct) : null;
                optimize = request.OptimizeVideo && info.KeyFrameSeconds > 2;
                videoError = optimize ? await OptimizeVideoAsync(drivers, request.DriverKey, conn, ct) : null;
            }
            db.Intercoms.Add(intercom);
            await db.SaveChangesAsync(ct);
            if (viaIsup) isup.InvalidateDirectory();

            await audit.LogAsync(ctx, "intercom", "intercom-created",
                targetType: "intercom", targetId: intercom.Id.ToString(), targetName: intercom.Name,
                detail: $"Agregó el frente de citofonía '{intercom.Name}' ({intercom.DriverKey}, {WhereOf(intercom)}, " +
                        (info is null ? "a la espera de que se conecte por ISUP" : $"{intercom.Model ?? "modelo desconocido"}, {intercom.DoorCount} puerta(s)") +
                        (intercom.GroupName is null ? "" : $", grupo '{intercom.GroupName}'") +
                        (intercom.LocationId is null ? "" : $", en {EquipmentLocation.Describe(intercom.LocationId)}") + ").",
                data: new { intercom.Host, intercom.Port, intercom.HttpPort, intercom.RtspPort, intercom.DriverKey, intercom.IsupDeviceId, intercom.Model, intercom.SerialNumber, intercom.ChannelId, intercom.LocationId });
            EquipmentLocation.Changed(ctx);
            if (info is not null && request.ConfigureCallCenter && !info.CallCenterEnabled)
                await audit.LogAsync(ctx, "intercom", "call-center-configured", targetType: "intercom", targetId: intercom.Id.ToString(),
                    targetName: intercom.Name, success: callCenterError is null,
                    detail: callCenterError is null
                        ? $"Configuró el botón de '{intercom.Name}' para llamar a la central."
                        : $"No se pudo configurar el botón de '{intercom.Name}' para llamar a la central: {callCenterError}");
            if (optimize) await AuditVideoAsync(ctx, audit, intercom, info!, videoError);
            await mtx.RefreshPathsAsync(ct); // ruta de su cámara propia
            service.RequestReconcile();
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "intercoms", cancellationToken: ct);
            var saved = await LoadAsync(db, intercom.Id, ct);
            return Results.Ok(service.ToDto(saved!));
        });

        app.MapPut("/api/intercoms/{id:int}", async (HttpContext ctx, int id, IntercomWriteDto request, VmsDbContext db, LicenseService license,
            IntercomDriverRegistry drivers, CredentialProtector protector, IHubContext<VmsHub> hub, IntercomService service,
            AuditService audit, MediaMtxManager mtx, IsupService isup, IsupHostManager isupHost, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.IntercomConfigure, out _) is { } failure) return failure;
            if (await ValidateChannelAsync(db, request.ChannelId, ct) is { } badChannel) return badChannel;
            var intercom = await LoadAsync(db, id, ct);
            if (intercom is null) return Results.NotFound();
            if (ValidateWrite(request, drivers, intercom.IsupKeyCiphertext is { Length: > 0 }) is { } invalid) return Error(invalid);
            bool viaIsup = UsesIsup(drivers, request.DriverKey);
            string host = viaIsup ? "" : request.Host.Trim();
            string? isupId = viaIsup ? request.IsupDeviceId!.Trim() : null;
            string? isupKey = !viaIsup ? null
                : IsupEquipmentRules.EffectiveKey(request.IsupKey, intercom.IsupKeyCiphertext, protector);
            if (isupId is not null)
            {
                if (await db.Intercoms.AnyAsync(i => i.Id != id && i.IsupDeviceId == isupId, ct))
                    return Error($"Ya existe otro frente con el ID ISUP {isupId}.", StatusCodes.Status409Conflict);
                if (await IsupEquipmentRules.KeyConflictAsync(db, protector, isupId, isupKey, IsupEquipmentRules.Intercom, id, ct) is { } conflict)
                    return Error(conflict, StatusCodes.Status409Conflict);
            }
            else if (await db.Intercoms.AnyAsync(i => i.Id != id && i.Host == host && i.Port == request.Port, ct))
                return Error("Ya existe otro frente con esa dirección y puerto.", StatusCodes.Status409Conflict);
            var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, intercom.LocationId, ct);
            if (locationError is not null) return Error(locationError, StatusCodes.Status404NotFound);
            int? previousLocation = intercom.LocationId;

            if (!viaIsup && string.IsNullOrEmpty(request.Password) && !StoredPassword.CanReuse(intercom.Host, request.Host))
                return Error(StoredPassword.RetypeMessage);
            string password = viaIsup ? ""
                : !string.IsNullOrEmpty(request.Password) ? request.Password
                : intercom.PasswordCiphertext.Length > 0 ? protector.Unprotect(intercom.PasswordCiphertext)
                : "";
            if (!viaIsup && password.Length == 0) return Error("La contraseña del frente es obligatoria.");
            bool connectionChanged = intercom.DriverKey != request.DriverKey || (viaIsup
                ? intercom.IsupDeviceId != isupId || !string.IsNullOrEmpty(request.IsupKey)
                : intercom.IsupDeviceId is not null || intercom.Host != host || intercom.Port != request.Port ||
                  intercom.HttpPort != request.HttpPort || intercom.Username != request.Username || !string.IsNullOrEmpty(request.Password));
            var conn = new IntercomConnectionInfo(host, viaIsup ? 0 : request.Port, viaIsup ? 0 : request.HttpPort,
                viaIsup ? "" : request.Username, password, viaIsup ? 0 : request.RtspPort, isupId);
            // Por ISUP solo se le puede hablar al frente si está conectado.
            bool reachable = !viaIsup || isup.IsOnline(isupId!);
            IntercomInfo? probed = null;
            if (connectionChanged)
            {
                HikvisionIntercomDriver.Forget(service.ConnectionOf(intercom));
                if (!reachable)
                {
                    // Aún no se conecta con estos datos: se completa (sin modelo = pendiente) en su próximo contacto.
                    intercom.Model = null;
                    intercom.Status = IntercomStatus.Offline;
                    intercom.LastError = await IsupEquipmentRules.NotConnectedMessageAsync(isup, isupHost, isupId!, "frente", ct);
                }
                // Con el mismo ID ISUP y conectado, cambiar solo la clave no requiere volver a leer el equipo.
                else if (!viaIsup || intercom.IsupDeviceId != isupId || intercom.DriverKey != request.DriverKey)
                {
                    var (info, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
                    if (info is null) return Error(probeError!);
                    ApplyProbe(intercom, info);
                    probed = info;
                }
            }
            else if (request.OptimizeVideo && reachable)
            {
                // Solo para leer el intervalo de cuadros completos; si el equipo no contesta, se guarda igual.
                (probed, _) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
            }

            var changes = new List<string>();
            if (intercom.Name != request.Name.Trim()) changes.Add($"nombre '{intercom.Name}' → '{request.Name.Trim()}'");
            string whereBefore = WhereOf(intercom), whereAfter = viaIsup ? $"ISUP {isupId}" : $"{host}:{request.Port}";
            if (whereBefore != whereAfter) changes.Add($"conexión {whereBefore} → {whereAfter}");
            if (!viaIsup && intercom.HttpPort != request.HttpPort) changes.Add($"puerto HTTP {intercom.HttpPort} → {request.HttpPort}");
            if (!viaIsup && intercom.RtspPort != request.RtspPort) changes.Add($"puerto RTSP {intercom.RtspPort} → {request.RtspPort}");
            if (!viaIsup && intercom.Username != request.Username) changes.Add($"usuario '{intercom.Username}' → '{request.Username}'");
            if (!viaIsup && !string.IsNullOrEmpty(request.Password)) changes.Add("contraseña cambiada");
            if (viaIsup && !string.IsNullOrEmpty(request.IsupKey)) changes.Add("clave ISUP cambiada");
            bool isupChanged = intercom.IsupDeviceId != isupId || (viaIsup && !string.IsNullOrEmpty(request.IsupKey));
            if (intercom.GroupName != GroupOf(request)) changes.Add($"grupo '{intercom.GroupName}' → '{GroupOf(request)}'");
            if (intercom.ChannelId != request.ChannelId) changes.Add($"canal de video {intercom.ChannelId?.ToString() ?? "ninguno"} → {request.ChannelId?.ToString() ?? "ninguno"}");
            if (locationId != previousLocation)
                changes.Add(EquipmentLocation.Change(previousLocation, locationId));
            if (intercom.Enabled != request.Enabled) changes.Add(request.Enabled ? "activado" : "desactivado");

            intercom.Name = request.Name.Trim();
            intercom.DriverKey = request.DriverKey;
            intercom.Host = host;
            intercom.Port = viaIsup ? 0 : request.Port;
            intercom.HttpPort = viaIsup ? 0 : request.HttpPort;
            intercom.RtspPort = viaIsup ? 0 : request.RtspPort;
            intercom.Username = viaIsup ? "" : request.Username;
            intercom.PasswordCiphertext = viaIsup ? [] : protector.Protect(password);
            intercom.IsupDeviceId = isupId;
            intercom.IsupKeyCiphertext = viaIsup ? IsupEquipmentRules.ProtectKey(isupKey, protector) : null;
            intercom.GroupName = GroupOf(request);
            intercom.ChannelId = request.ChannelId;
            intercom.LocationId = locationId;
            if (!intercom.Enabled && request.Enabled
                && license.Deny(LicenseFeatures.ModuleIntercom, LicenseFeatures.IntercomDevices,
                    await db.Intercoms.CountAsync(i => i.Enabled && i.Id != id, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "intercom", intercom.Name);
            intercom.Enabled = request.Enabled;
            bool wasCallCenter = intercom.CallCenterEnabled;
            string? callCenterError = request.ConfigureCallCenter && reachable ? await ConfigureCallCenterAsync(drivers, intercom, conn, ct) : null;
            bool optimize = request.OptimizeVideo && probed?.KeyFrameSeconds > 2;
            string? videoError = optimize ? await OptimizeVideoAsync(drivers, request.DriverKey, conn, ct) : null;
            intercom.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            if (isupChanged) isup.InvalidateDirectory();

            await audit.LogAsync(ctx, "intercom", "intercom-updated",
                targetType: "intercom", targetId: intercom.Id.ToString(), targetName: intercom.Name,
                detail: $"Modificó el frente de citofonía '{intercom.Name}': " + (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios."));
            if (locationId != previousLocation) await EquipmentLocation.MovedAsync(ctx, ct);
            if (request.ConfigureCallCenter && !wasCallCenter && reachable)
                await audit.LogAsync(ctx, "intercom", "call-center-configured", targetType: "intercom", targetId: intercom.Id.ToString(),
                    targetName: intercom.Name, success: callCenterError is null,
                    detail: callCenterError is null
                        ? $"Configuró el botón de '{intercom.Name}' para llamar a la central."
                        : $"No se pudo configurar el botón de '{intercom.Name}' para llamar a la central: {callCenterError}");
            if (optimize) await AuditVideoAsync(ctx, audit, intercom, probed!, videoError);
            await mtx.RefreshPathsAsync(ct); // dirección, credenciales o cámara pudieron cambiar
            service.RequestReconcile();
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "intercoms", cancellationToken: ct);
            var saved = await LoadAsync(db, intercom.Id, ct);
            return Results.Ok(service.ToDto(saved!));
        });

        app.MapDelete("/api/intercoms/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, IHubContext<VmsHub> hub,
            IntercomService service, AuditService audit, MediaMtxManager mtx, IsupService isup, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.IntercomConfigure, out _) is { } failure) return failure;
            var intercom = await db.Intercoms.FindAsync([id], ct);
            if (intercom is null) return Results.NotFound();
            if (service.ActiveCallOf(id) is { State: IntercomCallState.InCall } call)
                return Error($"El frente está en una conversación ({call.AnsweredBy}); termínela antes de eliminarlo.", StatusCodes.Status409Conflict);
            HikvisionIntercomDriver.Forget(service.ConnectionOf(intercom));
            db.Intercoms.Remove(intercom);
            await db.SaveChangesAsync(ct);
            if (intercom.IsupDeviceId is not null) isup.InvalidateDirectory();
            await audit.LogAsync(ctx, "intercom", "intercom-deleted",
                targetType: "intercom", targetId: id.ToString(), targetName: intercom.Name,
                detail: $"Eliminó el frente de citofonía '{intercom.Name}' ({WhereOf(intercom)}). Su historial de llamadas se conserva.");
            EquipmentLocation.Changed(ctx);
            await mtx.RefreshPathsAsync(ct);
            service.RequestReconcile();
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "intercoms", cancellationToken: ct);
            return Results.Ok();
        });

        // Probar conexión (sin persistir). Con ?intercomId= reutiliza la clave guardada.
        app.MapPost("/api/intercoms/probe", async (HttpContext ctx, IntercomWriteDto request, VmsDbContext db, IntercomDriverRegistry drivers,
            CredentialProtector protector, AuditService audit, IsupService isup, IsupHostManager isupHost, int? intercomId, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.IntercomConfigure, out _) is { } failure) return failure;
            var existing = intercomId is int existingId ? await db.Intercoms.FindAsync([existingId], ct) : null;
            if (ValidateWrite(request, drivers, existing?.IsupKeyCiphertext is { Length: > 0 }) is { } invalid) return Error(invalid);

            IntercomConnectionInfo conn;
            string where;
            string host = "";
            string? isupId = null;
            if (UsesIsup(drivers, request.DriverKey))
            {
                // Por ISUP no hay nada que "probar" desde aquí: el frente tiene que estar conectado.
                isupId = request.IsupDeviceId!.Trim();
                where = $"ISUP {isupId}";
                if (!isup.IsOnline(isupId))
                {
                    var status = await isup.GetHostStatusAsync(ct);
                    string message = status?.Rejections.Any(r => r.DeviceId == isupId && r.Reason.Contains("no está dado de alta", StringComparison.Ordinal)) == true
                        ? $"El frente ya está intentando conectarse por ISUP con el ID {isupId}: guárdelo y el servidor lo aceptará."
                        : await IsupEquipmentRules.NotConnectedMessageAsync(isup, isupHost, isupId, "frente", ct);
                    await audit.LogAsync(ctx, "intercom", "intercom-probed", targetType: "intercom", targetName: request.Name?.Trim(),
                        detail: $"Probó la conexión con el frente {where}: no está conectado.", success: false);
                    return Results.Ok(new IntercomProbeResultDto(false, message, null, null, null, null, 0, false, null, null));
                }
                conn = new IntercomConnectionInfo("", 0, 0, "", "", 0, isupId);
            }
            else
            {
                string? password = request.Password;
                if (string.IsNullOrEmpty(password) && existing is { PasswordCiphertext.Length: > 0 })
                {
                    if (!StoredPassword.CanReuse(existing.Host, request.Host)) return Error(StoredPassword.RetypeMessage);
                    password = protector.Unprotect(existing.PasswordCiphertext);
                }
                if (string.IsNullOrEmpty(password)) return Error("La contraseña del frente es obligatoria.");
                host = request.Host.Trim();
                conn = new IntercomConnectionInfo(host, request.Port, request.HttpPort, request.Username, password, request.RtspPort);
                where = $"{host}:{request.Port}";
            }

            var (info, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
            await audit.LogAsync(ctx, "intercom", "intercom-probed", targetType: "intercom", targetName: request.Name?.Trim(),
                detail: info is null
                    ? $"Probó la conexión con el frente {where}: {probeError}"
                    : $"Probó la conexión con el frente {where}: correcta ({info.Model}).",
                success: info is not null);
            if (info is null)
                return Results.Ok(new IntercomProbeResultDto(false, probeError, null, null, null, null, 0, false, null, null));

            // ¿El frente también está en Fuentes de video? Ese registro ocupa un canal de la licencia que la citofonía ya no necesita (el panel lo avisa).
            int? suggested = await db.Channels.AsNoTracking()
                .Where(c => isupId != null ? c.Device.IsupDeviceId == isupId : c.Device.Host == host)
                .OrderBy(c => c.ChannelNumber).Select(c => (int?)c.Id).FirstOrDefaultAsync(ct);
            return Results.Ok(new IntercomProbeResultDto(true, null, info.Model, info.SerialNumber, info.FirmwareVersion, info.DeviceName,
                info.DoorCount, info.CallCenterEnabled, info.AudioCodec, suggested, info.KeyFrameSeconds));
        });

        // ------------------------------------------------------------------
        // Cámara propia del frente: es parte del frente, no un canal de Fuentes
        // de video, así que no ocupa canales de la licencia de video. MediaMTX
        // la sirve en ic/{id}/main|sub; la concesión es la misma de un canal
        // (token de 60 s canjeado en /api/streaming/auth) con el alcance del frente.
        // ------------------------------------------------------------------
        app.MapPost("/api/intercoms/{id:int}/stream", async (HttpContext ctx, int id, IntercomStreamRequestDto request,
            VmsDbContext db, StreamTokenService streamTokens, MediaMtxManager mtx, IConfiguration config, AuditService audit,
            CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.IntercomAnswer, out var session) is { } failure) return failure;
            var intercom = await db.Intercoms.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
            if (intercom is null) return Results.NotFound();
            if (!(await ctx.ScopeAsync(session)).CanViewIntercom(id))
                return await ctx.OutOfScopeAsync(session, "intercom", id.ToString(), intercom.Name, "ver la cámara del frente");

            string? refusal = intercom.ChannelId is not null ? "Este frente muestra otra cámara (un canal de video), no la propia."
                : !intercom.Enabled ? "El frente está desactivado."
                : !mtx.IsRunning ? "El servicio de streaming no está disponible en el servidor."
                : null;
            if (refusal is not null)
            {
                // El cuadro reintenta solo cada pocos segundos: una fila cada 5 min basta.
                if (audit.ShouldLog($"intercom-video-rejected:{session.UserId}:{id}", TimeSpan.FromMinutes(5)))
                    await audit.LogAsync(ctx, "intercom", "video-rejected", targetType: "intercom", targetId: id.ToString(),
                        targetName: intercom.Name, success: false,
                        detail: $"No se pudo abrir la cámara del frente '{intercom.Name}': {refusal}");
                return Error(refusal, mtx.IsRunning ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status503ServiceUnavailable);
            }

            string path = MediaMtxManager.IntercomPathName(id, request.Profile);
            var (token, grant) = streamTokens.Issue(session.UserId, session.Username, path,
                deviceId: 0, deviceName: intercom.Name, rtspChannel: 1,
                profile: request.Profile == StreamProfile.Main ? "main" : "sub", intercomId: id);
            return Results.Ok(new StreamGrantDto(StreamsApi.PublicRtspUrl(ctx, config, mtx, path, token), token, grant.ExpiresAt));
        });

        // Foto de la cámara propia (consola web, que no reproduce RTSP). Se
        // renueva cada pocos segundos: caché corta compartida y una fila de
        // bitácora por usuario y frente cada 5 min.
        app.MapGet("/api/intercoms/{id:int}/snapshot", async (HttpContext ctx, int id, VmsDbContext db, IntercomService service,
            IMemoryCache cache, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.IntercomAnswer, out var session) is { } failure) return failure;
            var intercom = await db.Intercoms.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
            if (intercom is null) return Results.NotFound();
            if (!(await ctx.ScopeAsync(session)).CanViewIntercom(id))
                return await ctx.OutOfScopeAsync(session, "intercom", id.ToString(), intercom.Name, "ver la imagen del frente");
            if (intercom.ChannelId is not null || !intercom.Enabled) return Results.NotFound();

            if (audit.ShouldLog($"intercom-snap:{session.UserId}:{id}", TimeSpan.FromMinutes(5)))
                await audit.LogAsync(ctx, "intercom", "video-viewed", targetType: "intercom", targetId: id.ToString(),
                    targetName: intercom.Name, detail: $"Vio la imagen de la cámara del frente de citofonía '{intercom.Name}' desde el panel web.");

            string cacheKey = $"intercom-snapshot:{id}";
            if (!cache.TryGetValue(cacheKey, out byte[]? jpeg))
            {
                try { jpeg = await service.DriverOf(intercom).CaptureSnapshotAsync(service.ConnectionOf(intercom), ct); }
                catch (DriverException) { jpeg = null; }
                if (jpeg is not null) cache.Set(cacheKey, jpeg, TimeSpan.FromSeconds(2));
            }
            return jpeg is null ? Results.NotFound() : Results.File(jpeg, "image/jpeg");
        }).AllowUrlTicket(UrlTicketUse.Media);

        // ------------------------------------------------------------------
        // Llamadas
        // ------------------------------------------------------------------
        app.MapGet("/api/intercoms/calls/active", async (HttpContext ctx, IntercomService service) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.IntercomAnswer, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            return Results.Ok(service.ActiveCalls().Where(c => scope.CanViewIntercom(c.IntercomId)));
        });

        app.MapGet("/api/intercoms/calls", async (HttpContext ctx, VmsDbContext db, AuditService audit, int? intercomId, string? state,
            DateTime? from, DateTime? to, int? skip, int? take, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.IntercomAnswer, out var session) is { } failure) return failure;
            var query = db.IntercomCalls.AsNoTracking();
            // Alcance por ubicación: solo llamadas de frentes de sus ubicaciones (en la consulta).
            var scope = await ctx.ScopeAsync(session);
            if (scope.FiltersView)
            {
                var allowed = scope.AllowedIds(ResourceKind.Intercom).ToList();
                query = query.Where(c => allowed.Contains(c.IntercomId));
            }
            if (intercomId is int iid) query = query.Where(c => c.IntercomId == iid);
            if (!string.IsNullOrWhiteSpace(state) && Enum.TryParse<IntercomCallState>(state, true, out var parsed))
                query = query.Where(c => c.State == parsed);
            if (from is { } f) query = query.Where(c => c.StartedAt >= f.ToUniversalTime());
            if (to is { } t) query = query.Where(c => c.StartedAt < t.ToUniversalTime());
            int total = await query.CountAsync(ct);
            int size = Math.Clamp(take ?? 50, 1, 500);
            var calls = await query.OrderByDescending(c => c.StartedAt).Skip(Math.Max(0, skip ?? 0)).Take(size).ToListAsync(ct);
            var ids = calls.Select(c => c.IntercomId).Distinct().ToList();
            var intercoms = await db.Intercoms.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
            if (audit.ShouldLog($"intercom-history:{session.UserId}", TimeSpan.FromMinutes(5)))
                await audit.LogAsync(ctx, "intercom", "history-viewed", targetType: "intercom", targetId: intercomId?.ToString(),
                    detail: $"Consultó el historial de llamadas de citofonía ({total} resultado(s)).");
            return Results.Ok(new IntercomCallPageDto(
                calls.Select(c => IntercomService.ToDto(c, intercoms.GetValueOrDefault(c.IntercomId))).ToList(), total));
        });

        app.MapPost("/api/intercoms/calls/{callId:long}/answer", async (HttpContext ctx, long callId, IntercomService service,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.IntercomAnswer, out var session) is { } failure) return failure;
            if (await CallOutOfScopeAsync(ctx, session, callId, "contestar la llamada de") is { } outOfScope) return outOfScope;
            var result = await service.AnswerAsync(callId, session.UserId, session.Username, ct);
            await audit.LogAsync(ctx, "intercom", result.Success ? "call-answered" : "call-command-failed", targetType: "intercom",
                targetId: result.Call?.IntercomId.ToString(), targetName: result.IntercomName,
                detail: result.Success
                    ? $"Contestó la llamada de '{result.IntercomName}'."
                    : $"No pudo contestar la llamada de '{result.IntercomName}': {result.Message}",
                success: result.Success, data: new { callId });
            return Command(result);
        });

        app.MapPost("/api/intercoms/calls/{callId:long}/reject", async (HttpContext ctx, long callId, IntercomService service,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.IntercomAnswer, out var session) is { } failure) return failure;
            if (await CallOutOfScopeAsync(ctx, session, callId, "rechazar la llamada de") is { } outOfScope) return outOfScope;
            var result = await service.RejectAsync(callId, session.UserId, session.Username, ct);
            await audit.LogAsync(ctx, "intercom", result.Success ? "call-rejected" : "call-command-failed", targetType: "intercom",
                targetId: result.Call?.IntercomId.ToString(), targetName: result.IntercomName,
                detail: result.Success
                    ? $"Rechazó la llamada de '{result.IntercomName}'. {result.Message}"
                    : $"No pudo rechazar la llamada de '{result.IntercomName}': {result.Message}",
                success: result.Success, data: new { callId });
            return Command(result);
        });

        app.MapPost("/api/intercoms/calls/{callId:long}/hangup", async (HttpContext ctx, long callId, IntercomService service,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.IntercomAnswer, out var session) is { } failure) return failure;
            if (await CallOutOfScopeAsync(ctx, session, callId, "colgar la llamada de") is { } outOfScope) return outOfScope;
            var result = await service.HangUpAsync(callId, session.UserId, session.Username, session.Role == Roles.Admin, ct);
            if (!result.Success)
                await audit.LogAsync(ctx, "intercom", "call-command-failed", targetType: "intercom",
                    targetId: result.Call?.IntercomId.ToString(), targetName: result.IntercomName,
                    detail: $"No pudo colgar la llamada de '{result.IntercomName}': {result.Message}", success: false, data: new { callId });
            // El fin de la llamada lo audita el servicio (call-ended), con la duración.
            return Command(result);
        });

        app.MapPost("/api/intercoms/{id:int}/doors/{door:int}/open", async (HttpContext ctx, int id, int door, IntercomService service,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.IntercomAnswer, out var session) is { } failure) return failure;
            if (!(await ctx.ScopeAsync(session)).CanOperateIntercom(id))
                return await ctx.OutOfScopeAsync(session, "intercom", id.ToString(), null, $"abrir la puerta {door} de");
            var result = await service.OpenDoorAsync(id, door, session.Username, ct);
            await audit.LogAsync(ctx, "intercom", result.Success ? "door-opened" : "door-open-failed", targetType: "intercom",
                targetId: id.ToString(), targetName: result.IntercomName,
                detail: result.Success
                    ? $"Abrió la puerta {door} de '{result.IntercomName}'" + (result.Call is { } c ? $" durante la llamada #{c.Id}." : " (sin llamada en curso).")
                    : $"No pudo abrir la puerta {door} de '{result.IntercomName}': {result.Message}",
                success: result.Success, data: new { door, callId = result.Call?.Id });
            return Command(result);
        });

        // ------------------------------------------------------------------
        // Voz bidireccional (WebSocket): tramas binarias PCM 16 bits LE mono a
        // 8 kHz en AMBOS sentidos. El cliente manda su micrófono; el servidor
        // devuelve lo que capta el frente. Un texto "stop" o el cierre del
        // socket terminan la conversación (y cuelgan la llamada si era de una);
        // también se corta sin audio del cliente por 30 s.
        // ------------------------------------------------------------------
        app.Map("/api/intercoms/{id:int}/voice", async (HttpContext ctx, int id, IntercomService service, AuditService audit,
            ILoggerFactory loggers) =>
        {
            // Hablar y escuchar por el frente es atender la citofonía: el alcance solo dice SOBRE QUÉ frentes, no si puede.
            if (ApiSecurity.Require(ctx, Permissions.IntercomAnswer, out var session) is { } failure) return failure;
            if (!ctx.WebSockets.IsWebSocketRequest) return Error("Este recurso solo acepta WebSocket.", StatusCodes.Status400BadRequest);
            if (!(await ctx.ScopeAsync(session)).CanOperateIntercom(id))
                return await ctx.OutOfScopeAsync(session, "intercom", id.ToString(), null, "hablar por");

            var log = loggers.CreateLogger("TrueCentralVms.Server.Api.IntercomVoice");
            var (voice, error, name, call) = await service.OpenVoiceAsync(id, session.UserId, ctx.RequestAborted);
            if (voice is null)
            {
                await audit.LogAsync(ctx, "intercom", "talk-rejected", targetType: "intercom", targetId: id.ToString(), targetName: name,
                    detail: $"No se pudo abrir la conversación con '{name}': {error}", success: false);
                return Error(error!, StatusCodes.Status409Conflict);
            }

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await audit.LogAsync(ctx, "intercom", "talk-started", targetType: "intercom", targetId: id.ToString(), targetName: name,
                detail: call is null
                    ? $"Inició una conversación con '{name}' (sin llamada: habló por iniciativa propia)."
                    : $"Inició la conversación de la llamada #{call.Id} con '{name}'.",
                data: new { callId = call?.Id });
            var hello = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { type = "ready", intercom = name, callId = call?.Id });
            await socket.SendAsync(hello, WebSocketMessageType.Text, true, ctx.RequestAborted);

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, voice.Closed);
            var sendLock = new SemaphoreSlim(1, 1);

            // El frente colgó la llamada (abrió la puerta, tope de conversación) pero la voz sigue: avisar al cliente.
            voice.Detached += reason => _ = Task.Run(async () =>
            {
                try
                {
                    var notice = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { type = "detached", reason });
                    await sendLock.WaitAsync(stop.Token);
                    try { await socket.SendAsync(notice, WebSocketMessageType.Text, true, stop.Token); }
                    finally { sendLock.Release(); }
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
            });

            // Frente → operador.
            var downlink = Task.Run(async () =>
            {
                try
                {
                    await foreach (var pcm in voice.Downlink.Reader.ReadAllAsync(stop.Token))
                    {
                        await sendLock.WaitAsync(stop.Token);
                        try { await socket.SendAsync(pcm, WebSocketMessageType.Binary, true, stop.Token); }
                        finally { sendLock.Release(); }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
            });

            // Operador → frente.
            string reason = "cerrada por el operador";
            var buffer = new byte[16 * 1024];
            try
            {
                while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    idle.CancelAfter(VoiceIdleTimeout);
                    WebSocketReceiveResult received;
                    try { received = await socket.ReceiveAsync(buffer, idle.Token); }
                    catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                    {
                        reason = "sin audio del operador por 30 s";
                        break;
                    }
                    if (received.MessageType == WebSocketMessageType.Close) break;
                    if (received.MessageType == WebSocketMessageType.Text)
                    {
                        string text = System.Text.Encoding.UTF8.GetString(buffer, 0, received.Count);
                        if (text.Contains("stop", StringComparison.OrdinalIgnoreCase)) break;
                        continue;
                    }
                    await voice.WritePcmAsync(buffer.AsMemory(0, received.Count), stop.Token);
                    if (!voice.IsAlive)
                    {
                        reason = "el frente cerró el canal de voz";
                        break;
                    }
                }
            }
            catch (WebSocketException ex) { reason = $"conexión perdida ({ex.Message})"; }
            catch (OperationCanceledException) { reason = voice.CloseReason ?? "conexión cerrada"; }

            if (voice.CloseReason is { } serverReason) reason = serverReason;
            await service.OnVoiceClosedAsync(id, voice, session.Username, CancellationToken.None);
            try { await downlink; } catch { /* ya terminó */ }

            double seconds = Math.Round((DateTime.UtcNow - voice.StartedAt).TotalSeconds, 1);
            await audit.LogAsync(ctx, "intercom", "talk-stopped", targetType: "intercom", targetId: id.ToString(), targetName: name,
                detail: $"Terminó la conversación con '{name}': {seconds} s ({voice.BytesUp / 8000.0:F1} s de voz enviados, " +
                        $"{voice.BytesDown / 8000.0:F1} s recibidos), motivo: {reason}.",
                data: new { callId = call?.Id });

            try
            {
                var bye = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { type = "closed", reason, seconds });
                if (socket.State == WebSocketState.Open)
                {
                    await sendLock.WaitAsync();
                    await socket.SendAsync(bye, WebSocketMessageType.Text, true, CancellationToken.None);
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "fin", CancellationToken.None);
                }
            }
            catch (Exception ex) { log.LogDebug(ex, "No se pudo cerrar limpiamente el WebSocket de voz de citofonía."); }
            return Results.Empty;
        });
    }
}
