using System.Net.WebSockets;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Hikvision;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;
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

    private static string? ValidateWrite(IntercomWriteDto request, IntercomDriverRegistry drivers)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
            return "El nombre es obligatorio (máximo 128 caracteres).";
        if (string.IsNullOrWhiteSpace(request.Host))
            return "La dirección (IP o hostname) es obligatoria.";
        if (request.Port is < 1 or > 65535 || request.HttpPort is < 1 or > 65535)
            return "Los puertos deben estar entre 1 y 65535.";
        if (string.IsNullOrWhiteSpace(request.Username))
            return "El usuario del frente es obligatorio.";
        if (request.GroupName is { Length: > 64 })
            return "El grupo no puede superar los 64 caracteres.";
        if (drivers.Find(request.DriverKey) is null)
            return $"Driver desconocido: '{request.DriverKey}'.";
        return null;
    }

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

    private static void ApplyProbe(Intercom intercom, IntercomInfo info)
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

    public static void MapIntercomsApi(this WebApplication app)
    {
        // ------------------------------------------------------------------
        // Mantenedor
        // ------------------------------------------------------------------
        app.MapGet("/api/intercoms/drivers", (HttpContext ctx, IntercomDriverRegistry drivers) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.Ok(drivers.All.Select(f => new IntercomDriverDto(f.DriverKey, f.DisplayName, f.DefaultPort, f.DefaultHttpPort)));
        });

        // Canales de video elegibles como cámara del frente (selector del mantenedor).
        app.MapGet("/api/intercoms/channels", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var channels = await db.Channels.AsNoTracking()
                .OrderBy(c => c.Device.Name).ThenBy(c => c.ChannelNumber)
                .Select(c => new { c.Id, Name = c.Device.Name + " · " + c.Name, c.Device.Host, c.Enabled })
                .ToListAsync(ct);
            return Results.Ok(channels);
        });

        app.MapGet("/api/intercoms", async (HttpContext ctx, VmsDbContext db, IntercomService service, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var intercoms = await db.Intercoms.AsNoTracking().Include(i => i.Channel).ThenInclude(c => c!.Device)
                .OrderBy(i => i.GroupName).ThenBy(i => i.Name).ToListAsync(ct);
            return Results.Ok(intercoms.Select(service.ToDto));
        });

        app.MapGet("/api/intercoms/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, IntercomService service, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var intercom = await LoadAsync(db, id, ct);
            return intercom is null ? Results.NotFound() : Results.Ok(service.ToDto(intercom));
        });

        app.MapPost("/api/intercoms", async (HttpContext ctx, IntercomWriteDto request, VmsDbContext db, IntercomDriverRegistry drivers,
            LicenseService license, CredentialProtector protector, IHubContext<VmsHub> hub, IntercomService service, AuditService audit,
            CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            if (string.IsNullOrEmpty(request.Password)) return Error("La contraseña del frente es obligatoria.");
            if (await ValidateChannelAsync(db, request.ChannelId, ct) is { } badChannel) return badChannel;
            if (license.Deny(LicenseFeatures.ModuleIntercom, request.Enabled ? LicenseFeatures.IntercomDevices : null,
                    await db.Intercoms.CountAsync(i => i.Enabled, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "intercom", request.Name?.Trim());
            string host = request.Host.Trim();
            if (await db.Intercoms.AnyAsync(i => i.Host == host && i.Port == request.Port, ct))
                return Error("Ya existe un frente con esa dirección y puerto.", StatusCodes.Status409Conflict);

            var conn = new IntercomConnectionInfo(host, request.Port, request.HttpPort, request.Username, request.Password);
            var (info, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
            if (info is null)
            {
                await audit.LogAsync(ctx, "intercom", "intercom-created", targetType: "intercom", targetName: request.Name.Trim(),
                    detail: $"Alta de frente de citofonía rechazada por el equipo {host}:{request.Port}: {probeError}", success: false);
                return Error(probeError!);
            }

            var intercom = new Intercom
            {
                Name = request.Name.Trim(),
                DriverKey = request.DriverKey,
                Host = host,
                Port = request.Port,
                HttpPort = request.HttpPort,
                Username = request.Username,
                PasswordCiphertext = protector.Protect(request.Password),
                GroupName = GroupOf(request),
                ChannelId = request.ChannelId,
                Enabled = request.Enabled,
            };
            ApplyProbe(intercom, info);
            string? callCenterError = request.ConfigureCallCenter ? await ConfigureCallCenterAsync(drivers, intercom, conn, ct) : null;
            bool optimize = request.OptimizeVideo && info.KeyFrameSeconds > 2;
            string? videoError = optimize ? await OptimizeVideoAsync(drivers, request.DriverKey, conn, ct) : null;
            db.Intercoms.Add(intercom);
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "intercom", "intercom-created",
                targetType: "intercom", targetId: intercom.Id.ToString(), targetName: intercom.Name,
                detail: $"Agregó el frente de citofonía '{intercom.Name}' ({intercom.DriverKey}, {intercom.Host}:{intercom.Port}, " +
                        $"{intercom.Model ?? "modelo desconocido"}, {intercom.DoorCount} puerta(s)" +
                        (intercom.GroupName is null ? "" : $", grupo '{intercom.GroupName}'") + ").",
                data: new { intercom.Host, intercom.Port, intercom.HttpPort, intercom.DriverKey, intercom.Model, intercom.SerialNumber, intercom.ChannelId });
            if (request.ConfigureCallCenter && !info.CallCenterEnabled)
                await audit.LogAsync(ctx, "intercom", "call-center-configured", targetType: "intercom", targetId: intercom.Id.ToString(),
                    targetName: intercom.Name, success: callCenterError is null,
                    detail: callCenterError is null
                        ? $"Configuró el botón de '{intercom.Name}' para llamar a la central."
                        : $"No se pudo configurar el botón de '{intercom.Name}' para llamar a la central: {callCenterError}");
            if (optimize) await AuditVideoAsync(ctx, audit, intercom, info, videoError);
            service.RequestReconcile();
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "intercoms", cancellationToken: ct);
            var saved = await LoadAsync(db, intercom.Id, ct);
            return Results.Ok(service.ToDto(saved!));
        });

        app.MapPut("/api/intercoms/{id:int}", async (HttpContext ctx, int id, IntercomWriteDto request, VmsDbContext db, LicenseService license,
            IntercomDriverRegistry drivers, CredentialProtector protector, IHubContext<VmsHub> hub, IntercomService service,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            if (await ValidateChannelAsync(db, request.ChannelId, ct) is { } badChannel) return badChannel;
            var intercom = await LoadAsync(db, id, ct);
            if (intercom is null) return Results.NotFound();
            string host = request.Host.Trim();
            if (await db.Intercoms.AnyAsync(i => i.Id != id && i.Host == host && i.Port == request.Port, ct))
                return Error("Ya existe otro frente con esa dirección y puerto.", StatusCodes.Status409Conflict);

            string password = string.IsNullOrEmpty(request.Password) ? protector.Unprotect(intercom.PasswordCiphertext) : request.Password;
            bool connectionChanged = intercom.Host != host || intercom.Port != request.Port || intercom.HttpPort != request.HttpPort ||
                                     intercom.Username != request.Username || intercom.DriverKey != request.DriverKey ||
                                     !string.IsNullOrEmpty(request.Password);
            var conn = new IntercomConnectionInfo(host, request.Port, request.HttpPort, request.Username, password);
            IntercomInfo? probed = null;
            if (connectionChanged)
            {
                HikvisionIntercomDriver.Forget(service.ConnectionOf(intercom));
                var (info, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
                if (info is null) return Error(probeError!);
                ApplyProbe(intercom, info);
                probed = info;
            }
            else if (request.OptimizeVideo)
            {
                // Solo para leer el intervalo de cuadros completos; si el equipo no contesta, se guarda igual.
                (probed, _) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
            }

            var changes = new List<string>();
            if (intercom.Name != request.Name.Trim()) changes.Add($"nombre '{intercom.Name}' → '{request.Name.Trim()}'");
            if (intercom.Host != host || intercom.Port != request.Port) changes.Add($"dirección {intercom.Host}:{intercom.Port} → {host}:{request.Port}");
            if (intercom.HttpPort != request.HttpPort) changes.Add($"puerto HTTP {intercom.HttpPort} → {request.HttpPort}");
            if (intercom.Username != request.Username) changes.Add($"usuario '{intercom.Username}' → '{request.Username}'");
            if (!string.IsNullOrEmpty(request.Password)) changes.Add("contraseña cambiada");
            if (intercom.GroupName != GroupOf(request)) changes.Add($"grupo '{intercom.GroupName}' → '{GroupOf(request)}'");
            if (intercom.ChannelId != request.ChannelId) changes.Add($"canal de video {intercom.ChannelId?.ToString() ?? "ninguno"} → {request.ChannelId?.ToString() ?? "ninguno"}");
            if (intercom.Enabled != request.Enabled) changes.Add(request.Enabled ? "activado" : "desactivado");

            intercom.Name = request.Name.Trim();
            intercom.DriverKey = request.DriverKey;
            intercom.Host = host;
            intercom.Port = request.Port;
            intercom.HttpPort = request.HttpPort;
            intercom.Username = request.Username;
            intercom.PasswordCiphertext = protector.Protect(password);
            intercom.GroupName = GroupOf(request);
            intercom.ChannelId = request.ChannelId;
            if (!intercom.Enabled && request.Enabled
                && license.Deny(LicenseFeatures.ModuleIntercom, LicenseFeatures.IntercomDevices,
                    await db.Intercoms.CountAsync(i => i.Enabled && i.Id != id, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "intercom", intercom.Name);
            intercom.Enabled = request.Enabled;
            bool wasCallCenter = intercom.CallCenterEnabled;
            string? callCenterError = request.ConfigureCallCenter ? await ConfigureCallCenterAsync(drivers, intercom, conn, ct) : null;
            bool optimize = request.OptimizeVideo && probed?.KeyFrameSeconds > 2;
            string? videoError = optimize ? await OptimizeVideoAsync(drivers, request.DriverKey, conn, ct) : null;
            intercom.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "intercom", "intercom-updated",
                targetType: "intercom", targetId: intercom.Id.ToString(), targetName: intercom.Name,
                detail: $"Modificó el frente de citofonía '{intercom.Name}': " + (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios."));
            if (request.ConfigureCallCenter && !wasCallCenter)
                await audit.LogAsync(ctx, "intercom", "call-center-configured", targetType: "intercom", targetId: intercom.Id.ToString(),
                    targetName: intercom.Name, success: callCenterError is null,
                    detail: callCenterError is null
                        ? $"Configuró el botón de '{intercom.Name}' para llamar a la central."
                        : $"No se pudo configurar el botón de '{intercom.Name}' para llamar a la central: {callCenterError}");
            if (optimize) await AuditVideoAsync(ctx, audit, intercom, probed!, videoError);
            service.RequestReconcile();
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "intercoms", cancellationToken: ct);
            var saved = await LoadAsync(db, intercom.Id, ct);
            return Results.Ok(service.ToDto(saved!));
        });

        app.MapDelete("/api/intercoms/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, IHubContext<VmsHub> hub,
            IntercomService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var intercom = await db.Intercoms.FindAsync([id], ct);
            if (intercom is null) return Results.NotFound();
            if (service.ActiveCallOf(id) is { State: IntercomCallState.InCall } call)
                return Error($"El frente está en una conversación ({call.AnsweredBy}); termínela antes de eliminarlo.", StatusCodes.Status409Conflict);
            HikvisionIntercomDriver.Forget(service.ConnectionOf(intercom));
            db.Intercoms.Remove(intercom);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "intercom", "intercom-deleted",
                targetType: "intercom", targetId: id.ToString(), targetName: intercom.Name,
                detail: $"Eliminó el frente de citofonía '{intercom.Name}' ({intercom.Host}:{intercom.Port}). Su historial de llamadas se conserva.");
            service.RequestReconcile();
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "intercoms", cancellationToken: ct);
            return Results.Ok();
        });

        // Probar conexión (sin persistir). Con ?intercomId= reutiliza la clave guardada.
        app.MapPost("/api/intercoms/probe", async (HttpContext ctx, IntercomWriteDto request, VmsDbContext db, IntercomDriverRegistry drivers,
            CredentialProtector protector, AuditService audit, int? intercomId, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            string? password = request.Password;
            if (string.IsNullOrEmpty(password) && intercomId is int existingId &&
                await db.Intercoms.FindAsync([existingId], ct) is { } existing)
                password = protector.Unprotect(existing.PasswordCiphertext);
            if (string.IsNullOrEmpty(password)) return Error("La contraseña del frente es obligatoria.");

            string host = request.Host.Trim();
            var conn = new IntercomConnectionInfo(host, request.Port, request.HttpPort, request.Username, password);
            var (info, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
            await audit.LogAsync(ctx, "intercom", "intercom-probed", targetType: "intercom", targetName: request.Name?.Trim(),
                detail: info is null
                    ? $"Probó la conexión con el frente {host}:{request.Port}: {probeError}"
                    : $"Probó la conexión con el frente {host}:{request.Port}: correcta ({info.Model}).",
                success: info is not null);
            if (info is null)
                return Results.Ok(new IntercomProbeResultDto(false, probeError, null, null, null, null, 0, false, null, null));

            // Sugerir la cámara del propio frente si ya está dada de alta como dispositivo.
            int? suggested = await db.Channels.AsNoTracking().Where(c => c.Device.Host == host)
                .OrderBy(c => c.ChannelNumber).Select(c => (int?)c.Id).FirstOrDefaultAsync(ct);
            return Results.Ok(new IntercomProbeResultDto(true, null, info.Model, info.SerialNumber, info.FirmwareVersion, info.DeviceName,
                info.DoorCount, info.CallCenterEnabled, info.AudioCodec, suggested, info.KeyFrameSeconds));
        });

        // ------------------------------------------------------------------
        // Llamadas
        // ------------------------------------------------------------------
        app.MapGet("/api/intercoms/calls/active", (HttpContext ctx, IntercomService service) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.Ok(service.ActiveCalls());
        });

        app.MapGet("/api/intercoms/calls", async (HttpContext ctx, VmsDbContext db, AuditService audit, int? intercomId, string? state,
            DateTime? from, DateTime? to, int? skip, int? take, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var query = db.IntercomCalls.AsNoTracking();
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
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
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
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
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
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
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
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
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
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            if (!ctx.WebSockets.IsWebSocketRequest) return Error("Este recurso solo acepta WebSocket.", StatusCodes.Status400BadRequest);

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
