using TrueCentralVms.Server.Services.Licensing;
using System.Net.WebSockets;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Hikvision;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Isup;
using TrueCentralVms.Server.Services.SpeakerAgents;
using TrueCentralVms.Server.Services.Workflows;
using TrueCentralVms.Core.Domain;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// API del módulo Parlantes IP: mantenedor (administrador), biblioteca de
/// audios de cada equipo, órdenes de reproducción/detención/volumen (cualquier
/// usuario con sesión, auditadas) y el WebSocket de voz en vivo del operador.
/// </summary>
public static class SpeakersApi
{
    private const long MaxUploadBytes = 20 * 1024 * 1024;
    private static readonly TimeSpan TalkIdleTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TalkMaxDuration = TimeSpan.FromMinutes(10);

    /// <summary>Idiomas de texto a voz que ofrecen los parlantes Hikvision, con etiqueta en español.</summary>
    private static readonly IReadOnlyList<SpeakerTtsLanguageDto> TtsLanguages =
    [
        new("spanish", "Español"),
        new("english", "Inglés"),
        new("brazilianPortuguese", "Portugués (Brasil)"),
        new("french", "Francés"),
        new("russian", "Ruso"),
        new("japanese", "Japonés"),
        new("korean", "Coreano"),
        new("thai", "Tailandés"),
    ];

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    /// <param name="isupKeyStored">El parlante ya tiene clave ISUP guardada (en edición, dejarla vacía la conserva).</param>
    private static string? ValidateWrite(SpeakerWriteDto request, SpeakerDriverRegistry drivers, bool isupKeyStored = false)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
            return "El nombre es obligatorio (máximo 128 caracteres).";
        if (drivers.Find(request.DriverKey) is not { } factory)
            return $"Driver desconocido: '{request.DriverKey}'.";
        // Un TrueCentral Speaker se enrola con un código y se conecta él: no hay dirección ni credenciales que pedir.
        if (factory.UsesAgent) return null;
        // Por ISUP el parlante se conecta solo: se piden su ID y su clave, no dirección ni credenciales.
        if (factory.UsesIsup)
            return IsupEquipmentRules.Validate(request.IsupDeviceId, request.IsupKey, isupKeyStored);
        if (string.IsNullOrWhiteSpace(request.Host))
            return "La dirección (IP o hostname) es obligatoria.";
        if (request.Port is < 1 or > 65535)
            return "El puerto debe estar entre 1 y 65535.";
        if (string.IsNullOrWhiteSpace(request.Username))
            return "El usuario del parlante es obligatorio.";
        return null;
    }

    private static bool UsesIsup(SpeakerDriverRegistry drivers, string driverKey) => drivers.Find(driverKey)?.UsesIsup == true;

    private static bool UsesAgent(SpeakerDriverRegistry drivers, string driverKey) => drivers.Find(driverKey)?.UsesAgent == true;

    private const string AwaitingEnrollment = "Esperando enrolamiento: genere un código y escríbalo en el TrueCentral Speaker.";

    /// <summary>Dónde está el parlante, para la bitácora: dirección y puerto, su ID ISUP o, si es un agente, desde dónde se conectó.</summary>
    private static string WhereOf(Speaker s) =>
        s.IsupDeviceId is not null || s.DriverKey == SpeakerAgentProtocol.DriverKey ? SpeakerService.WhereOf(s) : $"{s.Host}:{s.Port}";

    /// <summary>
    /// Olvida el equipo enrolado (token, código e id). Devuelve el id que tenía: la conexión se corta DESPUÉS de guardar,
    /// porque la desconexión dispara un sondeo que si lee la base antes vería todavía el equipo enrolado.
    /// </summary>
    private static string? ForgetAgent(Speaker speaker)
    {
        string? agentId = speaker.AgentId;
        speaker.AgentId = null;
        speaker.AgentTokenHash = null;
        speaker.EnrollmentCodeHash = null;
        speaker.EnrollmentExpiresAt = null;
        return agentId;
    }

    private static async Task<(SpeakerInfo? Info, int LibraryCount, string? Error)> ProbeAsync(SpeakerDriverRegistry drivers,
        string driverKey, SpeakerConnectionInfo conn, CancellationToken ct)
    {
        var factory = drivers.Find(driverKey)!;
        try
        {
            var driver = factory.Create();
            var info = await driver.ProbeAsync(conn, ct);
            int count = 0;
            if (info.Capabilities.SupportsLibrary)
            {
                try { count = (await driver.GetLibraryAsync(conn, ct)).Count; }
                catch (DriverException) { /* la biblioteca se lee después */ }
            }
            return (info, count, null);
        }
        catch (DriverException ex) { return (null, 0, ex.Message); }
        catch (Exception ex) { return (null, 0, $"Error inesperado al comunicarse con el parlante: {ex.Message}"); }
    }

    private const string TerminalManagedInAccess =
        "Es el altavoz de un terminal de acceso o de una cámara: se administra desde ese equipo " +
        "(Control de acceso → Equipos, o Fuentes de video); su dirección y credenciales son las del equipo.";

    internal static void ApplyProbe(Speaker speaker, SpeakerInfo info)
    {
        speaker.Model = info.Model;
        speaker.SerialNumber = info.SerialNumber;
        speaker.FirmwareVersion = info.FirmwareVersion;
        speaker.SupportsLibrary = info.Capabilities.SupportsLibrary;
        speaker.SupportsTts = info.Capabilities.SupportsTts;
        speaker.SupportsLiveAudio = info.Capabilities.SupportsLiveAudio;
        speaker.Volume = info.Volume;
        speaker.Status = SpeakerStatus.Online;
        speaker.LastError = null;
        speaker.LastSeenAt = DateTime.UtcNow;
    }

    private static string SourceLabel(SpeakerPlayRequestDto request) => request.Source?.ToLowerInvariant() switch
    {
        SpeakerPlaySources.Server => $"sonido del servidor '{request.Sound}'",
        SpeakerPlaySources.Library => $"audio de la biblioteca '{request.LibraryName}'",
        SpeakerPlaySources.Tts => $"texto a voz «{request.Text}»",
        _ => $"origen '{request.Source}'",
    };

    /// <summary>Alcance por ubicación de una orden sobre varios parlantes: si alguno queda fuera, no se ejecuta.</summary>
    private static async Task<IResult?> SpeakersOutOfScopeAsync(HttpContext ctx, SessionInfo session, IEnumerable<int> ids, string attempted)
    {
        var scope = await ctx.ScopeAsync(session);
        var denied = ids.Where(id => !scope.CanOperateSpeaker(id)).Distinct().ToList();
        return denied.Count == 0
            ? null
            : await ctx.OutOfScopeAsync(session, "speaker", string.Join(",", denied), null, attempted);
    }

    public static void MapSpeakersApi(this WebApplication app)
    {
        // ------------------------------------------------------------------
        // Catálogos
        // ------------------------------------------------------------------
        app.MapGet("/api/speakers/drivers", (HttpContext ctx, SpeakerDriverRegistry drivers) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.Ok(drivers.All.Select(f => new SpeakerDriverDto(f.DriverKey, f.DisplayName, f.DefaultPort, f.DefaultHttps, f.UsesIsup, f.UsesAgent)));
        });

        app.MapGet("/api/speakers/tts-languages", (HttpContext ctx) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.Ok(TtsLanguages);
        });

        // Sonidos del servidor (los mismos de Automatizaciones → Sonidos).
        app.MapGet("/api/speakers/sounds", (HttpContext ctx, WorkflowStore store) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.Ok(store.ListAudio());
        });

        // ------------------------------------------------------------------
        // Mantenedor
        // ------------------------------------------------------------------
        app.MapGet("/api/speakers", async (HttpContext ctx, VmsDbContext db, SpeakerService service, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var speakers = await db.Speakers.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
            return Results.Ok(speakers.Where(s => scope.CanViewSpeaker(s.Id)).Select(service.ToDto));
        });

        app.MapGet("/api/speakers/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, SpeakerService service, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var speaker = await db.Speakers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            return speaker is null || !(await ctx.ScopeAsync(session)).CanViewSpeaker(speaker.Id)
                ? Results.NotFound()
                : Results.Ok(service.ToDto(speaker));
        });

        app.MapPost("/api/speakers", async (HttpContext ctx, SpeakerWriteDto request, VmsDbContext db, SpeakerDriverRegistry drivers, LicenseService license,
            CredentialProtector protector, IHubContext<VmsHub> hub, SpeakerService service, AuditService audit,
            IsupService isup, IsupHostManager isupHost, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SpeakersConfigure, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            bool viaIsup = UsesIsup(drivers, request.DriverKey);
            bool viaAgent = UsesAgent(drivers, request.DriverKey);
            if (!viaIsup && !viaAgent && string.IsNullOrEmpty(request.Password)) return Error("La contraseña del parlante es obligatoria.");
            if (license.Deny(LicenseFeatures.ModuleSpeakers, request.Enabled ? LicenseFeatures.SpeakerChannels : null,
                    await db.Speakers.CountAsync(s => s.Enabled && s.AccessDeviceId == null && s.DeviceId == null, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "speaker", request.Name?.Trim());
            string host = viaIsup || viaAgent ? "" : request.Host.Trim();
            string? isupId = viaIsup ? request.IsupDeviceId!.Trim() : null;
            if (viaAgent) { /* sin dirección: lo identifica el equipo que se enrole */ }
            else if (isupId is not null)
            {
                if (await db.Speakers.AnyAsync(s => s.IsupDeviceId == isupId, ct))
                    return Error($"Ya existe un parlante con el ID ISUP {isupId}.", StatusCodes.Status409Conflict);
                if (await IsupEquipmentRules.KeyConflictAsync(db, protector, isupId, request.IsupKey, IsupEquipmentRules.Speaker, null, ct) is { } conflict)
                    return Error(conflict, StatusCodes.Status409Conflict);
            }
            else if (await db.Speakers.AnyAsync(s => s.Host == host && s.Port == request.Port, ct))
                return Error("Ya existe un parlante con esa dirección y puerto.", StatusCodes.Status409Conflict);
            var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, null, ct);
            if (locationError is not null) return Error(locationError, StatusCodes.Status404NotFound);

            var speaker = new Speaker
            {
                Name = request.Name.Trim(),
                DriverKey = request.DriverKey,
                Host = host,
                Port = viaIsup || viaAgent ? 0 : request.Port,
                UseHttps = !viaIsup && !viaAgent && request.UseHttps,
                Username = viaIsup || viaAgent ? "" : request.Username,
                PasswordCiphertext = viaIsup || viaAgent ? [] : protector.Protect(request.Password!),
                IsupDeviceId = isupId,
                IsupKeyCiphertext = viaIsup ? IsupEquipmentRules.ProtectKey(request.IsupKey, protector) : null,
                LocationId = locationId,
                Enabled = request.Enabled,
            };

            // Por ISUP el parlante solo se conecta una vez que el servidor lo conoce: se guarda y se completa en su primer contacto.
            int libraryCount = 0;
            if (viaAgent)
            {
                // Se completa (modelo, versión) cuando el equipo se enrole con el código.
                speaker.Status = SpeakerStatus.Offline;
                speaker.LastError = AwaitingEnrollment;
                speaker.SupportsLiveAudio = true;
            }
            else if (isupId is not null && !isup.IsOnline(isupId))
            {
                speaker.Status = SpeakerStatus.Offline;
                speaker.LastError = await IsupEquipmentRules.NotConnectedMessageAsync(isup, isupHost, isupId, "parlante", ct);
            }
            else
            {
                var (info, count, probeError) = await ProbeAsync(drivers, request.DriverKey, service.ConnectionOf(speaker), ct);
                if (info is null)
                {
                    await audit.LogAsync(ctx, "speakers", "speaker-created", targetType: "speaker", targetName: request.Name.Trim(),
                        detail: $"Alta de parlante rechazada por el equipo {WhereOf(speaker)}: {probeError}", success: false);
                    return Error(probeError!);
                }
                ApplyProbe(speaker, info);
                libraryCount = count;
            }
            db.Speakers.Add(speaker);
            await db.SaveChangesAsync(ct);
            if (viaIsup) isup.InvalidateDirectory();

            await audit.LogAsync(ctx, "speakers", "speaker-created",
                targetType: "speaker", targetId: speaker.Id.ToString(), targetName: speaker.Name,
                detail: $"Agregó el parlante '{speaker.Name}' ({speaker.DriverKey}, {WhereOf(speaker)}, " +
                        (viaAgent ? "TrueCentral Speaker"
                            : speaker.Model is null && viaIsup ? "a la espera de que se conecte por ISUP" : speaker.Model ?? "modelo desconocido") +
                        (speaker.LocationId is null ? "" : $", en {EquipmentLocation.Describe(speaker.LocationId)}") +
                        $", biblioteca: {libraryCount} audios).",
                data: new { speaker.Host, speaker.Port, speaker.UseHttps, speaker.DriverKey, speaker.IsupDeviceId, speaker.Model, speaker.SerialNumber, speaker.LocationId });
            EquipmentLocation.Changed(ctx);
            service.RequestReconcile();
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "speakers", cancellationToken: ct);
            return Results.Ok(service.ToDto(speaker));
        });

        app.MapPut("/api/speakers/{id:int}", async (HttpContext ctx, int id, SpeakerWriteDto request, VmsDbContext db, LicenseService license,
            SpeakerDriverRegistry drivers, CredentialProtector protector, IHubContext<VmsHub> hub, SpeakerService service,
            AuditService audit, IsupService isup, IsupHostManager isupHost, SpeakerAgentHub agents, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SpeakersConfigure, out _) is { } failure) return failure;
            var speaker = await db.Speakers.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (speaker is null) return Results.NotFound();
            if (speaker.IsPart) return Error(TerminalManagedInAccess, StatusCodes.Status409Conflict);
            if (ValidateWrite(request, drivers, speaker.IsupKeyCiphertext is { Length: > 0 }) is { } invalid) return Error(invalid);
            bool viaIsup = UsesIsup(drivers, request.DriverKey);
            bool viaAgent = UsesAgent(drivers, request.DriverKey);
            bool wasAgent = speaker.DriverKey == SpeakerAgentProtocol.DriverKey;
            if (viaAgent && wasAgent)
                return await UpdateAgentSpeakerAsync(ctx, speaker, request, db, license, hub, service, audit, ct);
            if (viaAgent)
                return Error("Un parlante existente no se puede convertir en TrueCentral Speaker: agréguelo como parlante nuevo.");
            string host = viaIsup ? "" : request.Host.Trim();
            int port = viaIsup ? 0 : request.Port;
            string? isupId = viaIsup ? request.IsupDeviceId!.Trim() : null;
            string? isupKey = !viaIsup ? null
                : IsupEquipmentRules.EffectiveKey(request.IsupKey, speaker.IsupKeyCiphertext, protector);
            if (isupId is not null)
            {
                if (await db.Speakers.AnyAsync(s => s.Id != id && s.IsupDeviceId == isupId, ct))
                    return Error($"Ya existe otro parlante con el ID ISUP {isupId}.", StatusCodes.Status409Conflict);
                if (await IsupEquipmentRules.KeyConflictAsync(db, protector, isupId, isupKey, IsupEquipmentRules.Speaker, id, ct) is { } conflict)
                    return Error(conflict, StatusCodes.Status409Conflict);
            }
            else if (await db.Speakers.AnyAsync(s => s.Id != id && s.Host == host && s.Port == request.Port, ct))
                return Error("Ya existe otro parlante con esa dirección y puerto.", StatusCodes.Status409Conflict);
            var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, speaker.LocationId, ct);
            if (locationError is not null) return Error(locationError, StatusCodes.Status404NotFound);
            int? previousLocation = speaker.LocationId;

            if (!viaIsup && string.IsNullOrEmpty(request.Password) && !StoredPassword.CanReuse(speaker.Host, request.Host))
                return Error(StoredPassword.RetypeMessage);
            string password = viaIsup ? ""
                : !string.IsNullOrEmpty(request.Password) ? request.Password
                : speaker.PasswordCiphertext.Length > 0 ? protector.Unprotect(speaker.PasswordCiphertext)
                : "";
            if (!viaIsup && password.Length == 0) return Error("La contraseña del parlante es obligatoria.");
            bool connectionChanged = speaker.DriverKey != request.DriverKey || (viaIsup
                ? speaker.IsupDeviceId != isupId || !string.IsNullOrEmpty(request.IsupKey)
                : speaker.IsupDeviceId is not null || speaker.Host != host || speaker.Port != request.Port ||
                  speaker.UseHttps != request.UseHttps || speaker.Username != request.Username || !string.IsNullOrEmpty(request.Password));
            if (connectionChanged)
            {
                HikvisionSpeakerDriver.Forget(service.ConnectionOf(speaker));
                if (viaIsup && !isup.IsOnline(isupId!))
                {
                    // Aún no se conecta con estos datos: se completa (sin modelo = pendiente) en su próximo contacto.
                    speaker.Model = null;
                    speaker.Status = SpeakerStatus.Offline;
                    speaker.LastError = await IsupEquipmentRules.NotConnectedMessageAsync(isup, isupHost, isupId!, "parlante", ct);
                }
                // Con el mismo ID ISUP y conectado, cambiar solo la clave no requiere volver a leer el equipo.
                else if (!viaIsup || speaker.IsupDeviceId != isupId || speaker.DriverKey != request.DriverKey)
                {
                    var conn = new SpeakerConnectionInfo(host, port, !viaIsup && request.UseHttps, viaIsup ? "" : request.Username, password, isupId);
                    var (info, _, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
                    if (info is null) return Error(probeError!);
                    ApplyProbe(speaker, info);
                }
            }

            var changes = new List<string>();
            if (speaker.Name != request.Name.Trim()) changes.Add($"nombre '{speaker.Name}' → '{request.Name.Trim()}'");
            string whereBefore = WhereOf(speaker), whereAfter = viaIsup ? $"ISUP {isupId}" : $"{host}:{request.Port}";
            if (whereBefore != whereAfter) changes.Add($"conexión {whereBefore} → {whereAfter}");
            if (!viaIsup && speaker.UseHttps != request.UseHttps) changes.Add(request.UseHttps ? "HTTPS activado" : "HTTPS desactivado");
            if (!viaIsup && speaker.Username != request.Username) changes.Add($"usuario '{speaker.Username}' → '{request.Username}'");
            if (!viaIsup && !string.IsNullOrEmpty(request.Password)) changes.Add("contraseña cambiada");
            if (viaIsup && !string.IsNullOrEmpty(request.IsupKey)) changes.Add("clave ISUP cambiada");
            bool isupChanged = speaker.IsupDeviceId != isupId || (viaIsup && !string.IsNullOrEmpty(request.IsupKey));
            if (locationId != previousLocation)
                changes.Add(EquipmentLocation.Change(previousLocation, locationId));
            if (speaker.Enabled != request.Enabled) changes.Add(request.Enabled ? "activado" : "desactivado");

            string? revokedAgent = null;
            if (wasAgent)
            {
                changes.Add("deja de ser TrueCentral Speaker (se revocó el equipo enrolado)");
                revokedAgent = ForgetAgent(speaker);
            }
            speaker.Name = request.Name.Trim();
            speaker.DriverKey = request.DriverKey;
            speaker.Host = host;
            speaker.Port = port;
            speaker.UseHttps = !viaIsup && request.UseHttps;
            speaker.Username = viaIsup ? "" : request.Username;
            speaker.PasswordCiphertext = viaIsup ? [] : protector.Protect(password);
            speaker.IsupDeviceId = isupId;
            speaker.IsupKeyCiphertext = viaIsup ? IsupEquipmentRules.ProtectKey(isupKey, protector) : null;
            speaker.LocationId = locationId;
            if (!speaker.Enabled && request.Enabled
                && license.Deny(LicenseFeatures.ModuleSpeakers, LicenseFeatures.SpeakerChannels,
                    await db.Speakers.CountAsync(s => s.Enabled && s.Id != id && s.AccessDeviceId == null && s.DeviceId == null, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "speaker", speaker.Name);
            speaker.Enabled = request.Enabled;
            speaker.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            if (isupChanged) isup.InvalidateDirectory();
            await agents.DisconnectAsync(revokedAgent, SpeakerAgentProtocol.CloseCodes.Revoked, "El parlante se cambió a otro tipo de equipo.");

            await audit.LogAsync(ctx, "speakers", "speaker-updated",
                targetType: "speaker", targetId: speaker.Id.ToString(), targetName: speaker.Name,
                detail: $"Modificó el parlante '{speaker.Name}': " + (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios."));
            if (locationId != previousLocation) await EquipmentLocation.MovedAsync(ctx, ct);
            service.RequestReconcile();
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "speakers", cancellationToken: ct);
            return Results.Ok(service.ToDto(speaker));
        });

        app.MapDelete("/api/speakers/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, IHubContext<VmsHub> hub,
            SpeakerService service, AuditService audit, IsupService isup, SpeakerAgentHub agents, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SpeakersConfigure, out _) is { } failure) return failure;
            var speaker = await db.Speakers.FindAsync([id], ct);
            if (speaker is null) return Results.NotFound();
            if (speaker.IsPart) return Error(TerminalManagedInAccess, StatusCodes.Status409Conflict);
            if (service.BusyOf(id) is { } busy)
                return Error($"El parlante está en uso ({busy}); deténgalo antes de eliminarlo.", StatusCodes.Status409Conflict);
            HikvisionSpeakerDriver.Forget(service.ConnectionOf(speaker));
            db.Speakers.Remove(speaker);
            await db.SaveChangesAsync(ct);
            await agents.DisconnectAsync(speaker.AgentId, SpeakerAgentProtocol.CloseCodes.Revoked, "El parlante se eliminó de TrueCentral.");
            if (speaker.IsupDeviceId is not null) isup.InvalidateDirectory();
            await audit.LogAsync(ctx, "speakers", "speaker-deleted",
                targetType: "speaker", targetId: id.ToString(), targetName: speaker.Name,
                detail: $"Eliminó el parlante '{speaker.Name}' ({WhereOf(speaker)}).");
            EquipmentLocation.Changed(ctx);
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "speakers", cancellationToken: ct);
            return Results.Ok();
        });

        // Probar conexión (sin persistir). Con ?speakerId= reutiliza la clave guardada.
        app.MapPost("/api/speakers/probe", async (HttpContext ctx, SpeakerWriteDto request, VmsDbContext db, SpeakerDriverRegistry drivers,
            CredentialProtector protector, AuditService audit, IsupService isup, IsupHostManager isupHost, SpeakerAgentHub agents, int? speakerId,
            CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SpeakersConfigure, out _) is { } failure) return failure;
            var existing = speakerId is int existingId ? await db.Speakers.FindAsync([existingId], ct) : null;
            if (ValidateWrite(request, drivers, existing?.IsupKeyCiphertext is { Length: > 0 }) is { } invalid) return Error(invalid);

            SpeakerConnectionInfo conn;
            string where;
            if (UsesAgent(drivers, request.DriverKey))
            {
                // Un agente solo se puede "probar" una vez enrolado y conectado.
                if (existing?.AgentId is not { } agentId || !agents.IsOnline(agentId))
                    return Results.Ok(new SpeakerProbeResultDto(false, existing?.AgentId is null
                        ? AwaitingEnrollment
                        : "El TrueCentral Speaker no está conectado al servidor ahora.", null, null, null, false, false, false, null, 0));
                conn = new SpeakerConnectionInfo("", 0, false, "", "", null, agentId);
                where = $"agente en {existing.Host}";
            }
            else if (UsesIsup(drivers, request.DriverKey))
            {
                // Por ISUP no hay nada que "probar" desde aquí: el parlante tiene que estar conectado.
                string isupId = request.IsupDeviceId!.Trim();
                where = $"ISUP {isupId}";
                if (!isup.IsOnline(isupId))
                {
                    var status = await isup.GetHostStatusAsync(ct);
                    string message = status?.Rejections.Any(r => r.DeviceId == isupId && r.Reason.Contains("no está dado de alta", StringComparison.Ordinal)) == true
                        ? $"El parlante ya está intentando conectarse por ISUP con el ID {isupId}: guárdelo y el servidor lo aceptará."
                        : await IsupEquipmentRules.NotConnectedMessageAsync(isup, isupHost, isupId, "parlante", ct);
                    await audit.LogAsync(ctx, "speakers", "speaker-probed", targetType: "speaker", targetName: request.Name.Trim(),
                        detail: $"Probó la conexión con el parlante {where}: no está conectado.", success: false);
                    return Results.Ok(new SpeakerProbeResultDto(false, message, null, null, null, false, false, false, null, 0));
                }
                conn = new SpeakerConnectionInfo("", 0, false, "", "", isupId);
            }
            else
            {
                string? password = request.Password;
                if (string.IsNullOrEmpty(password) && existing is { PasswordCiphertext.Length: > 0 })
                {
                    if (!StoredPassword.CanReuse(existing.Host, request.Host)) return Error(StoredPassword.RetypeMessage);
                    password = protector.Unprotect(existing.PasswordCiphertext);
                }
                if (string.IsNullOrEmpty(password)) return Error("La contraseña del parlante es obligatoria.");
                conn = new SpeakerConnectionInfo(request.Host.Trim(), request.Port, request.UseHttps, request.Username, password);
                where = $"{request.Host.Trim()}:{request.Port}";
            }

            var (info, libraryCount, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
            await audit.LogAsync(ctx, "speakers", "speaker-probed", targetType: "speaker", targetName: request.Name.Trim(),
                detail: info is null
                    ? $"Probó la conexión con el parlante {where}: {probeError}"
                    : $"Probó la conexión con el parlante {where}: correcta ({info.Model}).",
                success: info is not null);
            if (info is null)
                return Results.Ok(new SpeakerProbeResultDto(false, probeError, null, null, null, false, false, false, null, 0));
            return Results.Ok(new SpeakerProbeResultDto(true, null, info.Model, info.SerialNumber, info.FirmwareVersion,
                info.Capabilities.SupportsLibrary, info.Capabilities.SupportsTts, info.Capabilities.SupportsLiveAudio, info.Volume, libraryCount));
        });

        // ------------------------------------------------------------------
        // Biblioteca del equipo
        // ------------------------------------------------------------------
        app.MapGet("/api/speakers/{id:int}/library", async (HttpContext ctx, int id, VmsDbContext db, SpeakerService service, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var speaker = await db.Speakers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (speaker is null || !(await ctx.ScopeAsync(session)).CanViewSpeaker(speaker.Id)) return Results.NotFound();
            if (!speaker.SupportsLibrary) return Results.Ok(Array.Empty<SpeakerAudioItemDto>());
            try
            {
                var items = await service.DriverOf(speaker).GetLibraryAsync(service.ConnectionOf(speaker), ct);
                _ = service.MarkOnlineAsync(id, CancellationToken.None);
                return Results.Ok(items.Select(i => new SpeakerAudioItemDto(i.Id, i.Name, i.Format, i.DurationSeconds, i.Bytes, i.BuiltIn)));
            }
            catch (DriverException ex) { return Error(ex.Message, StatusCodes.Status502BadGateway); }
        });

        // El archivo del audio, para escucharlo en el puesto del operador o en el
        // navegador sin hacerlo sonar en el parlante (acepta ?access_token= para <audio>).
        app.MapGet("/api/speakers/{id:int}/library/{audioId:long}/file", async (HttpContext ctx, int id, long audioId, VmsDbContext db,
            SpeakerService service, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var speaker = await db.Speakers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (speaker is null || !(await ctx.ScopeAsync(session)).CanViewSpeaker(speaker.Id)) return Results.NotFound();
            if (!speaker.SupportsLibrary) return Error("El parlante no tiene biblioteca de audios.");
            try
            {
                var (content, contentType, fileName) = await service.DriverOf(speaker).DownloadAudioAsync(service.ConnectionOf(speaker), audioId, ct);
                return Results.File(content, contentType, fileName);
            }
            catch (DriverException ex) { return Error(ex.Message, StatusCodes.Status502BadGateway); }
        });

        app.MapPost("/api/speakers/{id:int}/library", async (HttpContext ctx, int id, IFormFile file, string? name, VmsDbContext db,
            SpeakerService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SpeakersConfigure, out _) is { } failure) return failure;
            var speaker = await db.Speakers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (speaker is null) return Results.NotFound();
            if (!speaker.SupportsLibrary) return Error("El parlante no tiene biblioteca de audios.");
            if (file.Length == 0) return Error("El archivo está vacío.");
            if (file.Length > MaxUploadBytes) return Error("El audio no puede pesar más de 20 MB.");
            string format = Path.GetExtension(file.FileName).TrimStart('.').ToLowerInvariant();
            if (format is not ("mp3" or "wav" or "aac" or "mp2"))
                return Error("Formato no admitido por el parlante: use mp3, wav, aac o mp2.");
            string audioName = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(file.FileName) : name.Trim();

            byte[] content;
            await using (var stream = file.OpenReadStream())
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                content = buffer.ToArray();
            }
            try
            {
                var item = await service.DriverOf(speaker).UploadAudioAsync(service.ConnectionOf(speaker), audioName, format, content, ct);
                await audit.LogAsync(ctx, "speakers", "library-uploaded", targetType: "speaker", targetId: id.ToString(), targetName: speaker.Name,
                    detail: $"Subió el audio '{item.Name}' ({format}, {content.Length / 1024} kB) a la biblioteca del parlante '{speaker.Name}'.");
                return Results.Ok(new SpeakerAudioItemDto(item.Id, item.Name, item.Format, item.DurationSeconds, item.Bytes, item.BuiltIn));
            }
            catch (DriverException ex)
            {
                await audit.LogAsync(ctx, "speakers", "library-uploaded", targetType: "speaker", targetId: id.ToString(), targetName: speaker.Name,
                    detail: $"El parlante '{speaker.Name}' rechazó el audio '{audioName}': {ex.Message}", success: false);
                return Error(ex.Message, StatusCodes.Status502BadGateway);
            }
        }).DisableAntiforgery();

        app.MapPut("/api/speakers/{id:int}/library/{audioId:long}", async (HttpContext ctx, int id, long audioId, SpeakerAudioRenameDto request,
            VmsDbContext db, SpeakerService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SpeakersConfigure, out _) is { } failure) return failure;
            var speaker = await db.Speakers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (speaker is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Name)) return Error("Indique el nombre nuevo.");
            if (request.Name.Trim().Length > 255) return Error("El nombre no puede superar los 255 caracteres.");
            try
            {
                var item = await service.DriverOf(speaker).RenameAudioAsync(service.ConnectionOf(speaker), audioId, request.Name, ct);
                await audit.LogAsync(ctx, "speakers", "library-renamed", targetType: "speaker", targetId: id.ToString(), targetName: speaker.Name,
                    detail: $"Renombró el audio {audioId} de la biblioteca del parlante '{speaker.Name}' a '{item.Name}'.");
                return Results.Ok(new SpeakerAudioItemDto(item.Id, item.Name, item.Format, item.DurationSeconds, item.Bytes, item.BuiltIn));
            }
            catch (DriverException ex) { return Error(ex.Message, StatusCodes.Status502BadGateway); }
        });

        app.MapDelete("/api/speakers/{id:int}/library/{audioId:long}", async (HttpContext ctx, int id, long audioId, VmsDbContext db,
            SpeakerService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SpeakersConfigure, out _) is { } failure) return failure;
            var speaker = await db.Speakers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (speaker is null) return Results.NotFound();
            try
            {
                await service.DriverOf(speaker).DeleteAudioAsync(service.ConnectionOf(speaker), audioId, ct);
                await audit.LogAsync(ctx, "speakers", "library-deleted", targetType: "speaker", targetId: id.ToString(), targetName: speaker.Name,
                    detail: $"Borró el audio {audioId} de la biblioteca del parlante '{speaker.Name}'.");
                return Results.NoContent();
            }
            catch (DriverException ex) { return Error(ex.Message, StatusCodes.Status502BadGateway); }
        });

        app.MapPost("/api/speakers/{id:int}/library/tts", async (HttpContext ctx, int id, SpeakerTtsRequestDto request, VmsDbContext db,
            SpeakerService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SpeakersConfigure, out _) is { } failure) return failure;
            var speaker = await db.Speakers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (speaker is null) return Results.NotFound();
            if (!speaker.SupportsTts) return Error("El parlante no genera texto a voz.");
            if (string.IsNullOrWhiteSpace(request.Name)) return Error("Indique el nombre del audio.");
            if (string.IsNullOrWhiteSpace(request.Text)) return Error("Indique el texto a leer.");
            if (request.Text.Trim().Length > 100) return Error("El texto no puede superar los 100 caracteres (límite del parlante).");
            try
            {
                var item = await service.DriverOf(speaker).CreateTtsAsync(service.ConnectionOf(speaker), request.Name, request.Text,
                    request.Language, request.Voice, ct);
                await audit.LogAsync(ctx, "speakers", "tts-created", targetType: "speaker", targetId: id.ToString(), targetName: speaker.Name,
                    detail: $"Creó el audio de texto a voz '{item.Name}' en el parlante '{speaker.Name}': «{request.Text.Trim()}» ({request.Language}, {request.Voice}).");
                return Results.Ok(new SpeakerAudioItemDto(item.Id, item.Name, item.Format, item.DurationSeconds, item.Bytes, item.BuiltIn));
            }
            catch (DriverException ex) { return Error(ex.Message, StatusCodes.Status502BadGateway); }
        });

        // ------------------------------------------------------------------
        // Estado y volumen
        // ------------------------------------------------------------------
        app.MapGet("/api/speakers/{id:int}/status", async (HttpContext ctx, int id, VmsDbContext db, SpeakerService service, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var speaker = await db.Speakers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (speaker is null || !(await ctx.ScopeAsync(session)).CanViewSpeaker(speaker.Id)) return Results.NotFound();
            try
            {
                var state = await service.DriverOf(speaker).GetPlaybackStateAsync(service.ConnectionOf(speaker), ct);
                return Results.Ok(new { playing = state.IsPlaying, current = state.CurrentName, busyWith = service.BusyOf(id) });
            }
            catch (DriverException ex) { return Error(ex.Message, StatusCodes.Status502BadGateway); }
        });

        app.MapPut("/api/speakers/{id:int}/volume", async (HttpContext ctx, int id, SpeakerVolumeRequestDto request, VmsDbContext db,
            SpeakerService service, ScopedHub hub, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SpeakersPlay, out var session) is { } failure) return failure;
            if (request.Volume is < 0 or > 100) return Error("El volumen debe estar entre 0 y 100.");
            var speaker = await db.Speakers.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (speaker is null) return Results.NotFound();
            if (!(await ctx.ScopeAsync(session)).CanOperateSpeaker(speaker.Id))
                return await ctx.OutOfScopeAsync(session, "speaker", id.ToString(), speaker.Name, "cambiar el volumen de");
            try
            {
                await service.DriverOf(speaker).SetVolumeAsync(service.ConnectionOf(speaker), request.Volume, ct);
                int? previous = speaker.Volume;
                speaker.Volume = request.Volume;
                speaker.Status = SpeakerStatus.Online;
                speaker.LastError = null;
                speaker.LastSeenAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                await audit.LogAsync(ctx, "speakers", "volume-changed", targetType: "speaker", targetId: id.ToString(), targetName: speaker.Name,
                    detail: $"Cambió el volumen del parlante '{speaker.Name}' de {previous?.ToString() ?? "?"} a {request.Volume}.");
                await hub.SendAsync(VmsHubContract.SpeakerStatusChanged, service.ToDto(speaker), s => s.CanViewSpeaker(id), ct);
                return Results.Ok(service.ToDto(speaker));
            }
            catch (DriverException ex)
            {
                await audit.LogAsync(ctx, "speakers", "volume-changed", targetType: "speaker", targetId: id.ToString(), targetName: speaker.Name,
                    detail: $"El parlante '{speaker.Name}' rechazó el cambio de volumen a {request.Volume}: {ex.Message}", success: false);
                return Error(ex.Message, StatusCodes.Status502BadGateway);
            }
        });

        // ------------------------------------------------------------------
        // Reproducir / detener
        // ------------------------------------------------------------------
        app.MapPost("/api/speakers/play", async (HttpContext ctx, SpeakerPlayRequestDto request, VmsDbContext db, SpeakerService service,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SpeakersPlay, out var session) is { } failure) return failure;
            if (request.SpeakerIds is not { Count: > 0 }) return Error("Elija al menos un parlante.");
            if (await SpeakersOutOfScopeAsync(ctx, session, request.SpeakerIds, "hacer sonar") is { } outOfScope) return outOfScope;
            string label = SourceLabel(request);

            // Un sonido del servidor dura lo que dura el audio (hasta 2 min):
            // se espera un poco para informar errores inmediatos y si sigue
            // sonando se responde "en curso" y el resultado final se audita
            // cuando termine.
            var task = service.PlayAsync(request, session.Username, CancellationToken.None);
            var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2.5), ct));
            if (finished == task)
            {
                var result = await task;
                foreach (var item in result.Results)
                    await audit.LogAsync(ctx, "speakers", item.Success ? "play" : "play-failed",
                        targetType: "speaker", targetId: item.SpeakerId.ToString(), targetName: item.SpeakerName,
                        detail: $"Reproducción de {label} en '{item.SpeakerName}': {item.Message}", success: item.Success);
                return Results.Ok(result);
            }

            var ids = request.SpeakerIds.Distinct().ToList();
            var names = await db.Speakers.AsNoTracking().Where(s => ids.Contains(s.Id)).Select(s => new { s.Id, s.Name }).ToListAsync(ct);
            string? ip = ctx.Connection.RemoteIpAddress?.ToString();
            _ = task.ContinueWith(async t =>
            {
                if (t.IsFaulted) return;
                foreach (var item in t.Result.Results)
                    await audit.LogSystemAsync("speakers", item.Success ? "play" : "play-failed",
                        targetType: "speaker", targetId: item.SpeakerId.ToString(), targetName: item.SpeakerName,
                        detail: $"Reproducción de {label} en '{item.SpeakerName}': {item.Message}", success: item.Success,
                        userId: session.UserId, username: session.Username, clientIp: ip, origin: "api");
            }, TaskScheduler.Default);
            return Results.Ok(new SpeakerOperationResultDto(true, $"Transmitiendo {label} a {names.Count} parlante{(names.Count == 1 ? "" : "s")}…",
                names.Select(n => new SpeakerActionResultDto(n.Id, n.Name, true, "Transmisión en curso.")).ToList()));
        });

        app.MapPost("/api/speakers/stop", async (HttpContext ctx, SpeakerStopRequestDto request, SpeakerService service, AuditService audit,
            CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SpeakersPlay, out var session) is { } failure) return failure;
            if (request.SpeakerIds is not { Count: > 0 }) return Error("Elija al menos un parlante.");
            if (await SpeakersOutOfScopeAsync(ctx, session, request.SpeakerIds, "detener") is { } outOfScope) return outOfScope;
            var result = await service.StopAsync(request.SpeakerIds, ct);
            foreach (var item in result.Results)
                await audit.LogAsync(ctx, "speakers", "stop", targetType: "speaker", targetId: item.SpeakerId.ToString(), targetName: item.SpeakerName,
                    detail: $"Detuvo la reproducción en '{item.SpeakerName}': {item.Message}", success: item.Success);
            return Results.Ok(result);
        });

        // ------------------------------------------------------------------
        // Voz en vivo del operador (WebSocket): tramas binarias PCM 16 bits LE
        // mono a 8 kHz; el servidor convierte a G.711 y escribe en todos los
        // parlantes elegidos a la vez. Un texto "stop" o el cierre del socket
        // terminan la sesión; también se corta sin audio por 20 s o a los 10 min.
        // ------------------------------------------------------------------
        app.Map("/api/speakers/talk", async (HttpContext ctx, string? ids, string? group, SpeakerService service, AuditService audit, ILoggerFactory loggers) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SpeakersPlay, out var session) is { } failure) return failure;
            if (!ctx.WebSockets.IsWebSocketRequest) return Error("Este recurso solo acepta WebSocket.", StatusCodes.Status400BadRequest);
            var speakerIds = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, out int id) ? id : 0).Where(id => id > 0).Distinct().ToList();
            if (speakerIds.Count == 0) return Error("Indique los parlantes (?ids=1,2).", StatusCodes.Status400BadRequest);
            if (await SpeakersOutOfScopeAsync(ctx, session, speakerIds, "hablar por") is { } outOfScope) return outOfScope;

            var log = loggers.CreateLogger("TrueCentralVms.Server.Api.SpeakersTalk");
            SpeakerService.TalkSession talk;
            try { talk = await service.OpenTalkAsync(speakerIds, session.Username, ctx.RequestAborted); }
            catch (DriverException ex)
            {
                await audit.LogAsync(ctx, "speakers", "talk-rejected", targetType: "speaker", targetName: string.Join(",", speakerIds),
                    detail: $"No se pudo abrir la voz en vivo: {ex.Message}", success: false);
                return Error(ex.Message, StatusCodes.Status409Conflict);
            }

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            string targets = string.Join(", ", talk.Speakers.Select(s => $"'{s.Name}'"));
            await audit.LogAsync(ctx, "speakers", "talk-started", targetType: "speaker",
                targetId: string.Join(",", talk.Speakers.Select(s => s.Id)), targetName: targets,
                detail: (string.IsNullOrWhiteSpace(group) ? $"Inició voz en vivo hacia {targets}."
                            : $"Inició voz en vivo hacia el grupo '{group.Trim()[..Math.Min(64, group.Trim().Length)]}': {targets}.") +
                        (talk.Rejected.Count > 0 ? " No aceptaron: " + string.Join("; ", talk.Rejected.Select(r => $"{r.SpeakerName} ({r.Message})")) : ""));
            foreach (var rejected in talk.Rejected)
                await audit.LogAsync(ctx, "speakers", "talk-rejected", targetType: "speaker", targetId: rejected.SpeakerId.ToString(),
                    targetName: rejected.SpeakerName, detail: rejected.Message, success: false);

            // Avisar al cliente qué parlantes quedaron dentro y cuáles no.
            var hello = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "ready",
                speakers = talk.Speakers.Select(s => new { s.Id, s.Name }),
                rejected = talk.Rejected,
            });
            await socket.SendAsync(hello, WebSocketMessageType.Text, true, ctx.RequestAborted);

            string reason = "cerrado por el operador";
            var buffer = new byte[16 * 1024];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                    idle.CancelAfter(TalkIdleTimeout);
                    WebSocketReceiveResult received;
                    try { received = await socket.ReceiveAsync(buffer, idle.Token); }
                    catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
                    {
                        reason = "sin audio por 20 s";
                        break;
                    }
                    if (received.MessageType == WebSocketMessageType.Close) break;
                    if (received.MessageType == WebSocketMessageType.Text)
                    {
                        string text = System.Text.Encoding.UTF8.GetString(buffer, 0, received.Count);
                        if (text.Contains("stop", StringComparison.OrdinalIgnoreCase)) break;
                        continue;
                    }
                    await talk.WritePcmAsync(buffer.AsMemory(0, received.Count), ctx.RequestAborted);
                    if (talk.Alive.Count == 0)
                    {
                        reason = "todos los parlantes cerraron el canal";
                        break;
                    }
                    if (DateTime.UtcNow - talk.StartedAt > TalkMaxDuration)
                    {
                        reason = "tope de 10 minutos";
                        break;
                    }
                }
            }
            catch (WebSocketException ex)
            {
                reason = $"conexión perdida ({ex.Message})";
            }
            catch (OperationCanceledException)
            {
                reason = "conexión cerrada";
            }
            finally
            {
                await talk.DisposeAsync();
            }

            double seconds = Math.Round((DateTime.UtcNow - talk.StartedAt).TotalSeconds, 1);
            string failures = talk.Failures.Count > 0
                ? " Fallas: " + string.Join("; ", talk.Failures.Select(f => $"{talk.Speakers.FirstOrDefault(s => s.Id == f.Key)?.Name ?? f.Key.ToString()} ({f.Value})"))
                : "";
            await audit.LogAsync(ctx, "speakers", "talk-stopped", targetType: "speaker",
                targetId: string.Join(",", talk.Speakers.Select(s => s.Id)), targetName: targets,
                detail: $"Terminó la voz en vivo hacia {targets}: {seconds} s de audio ({talk.BytesSent / 16000.0:F1} s enviados), motivo: {reason}.{failures}");

            try
            {
                var bye = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { type = "closed", reason, seconds });
                if (socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(bye, WebSocketMessageType.Text, true, CancellationToken.None);
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
                }
            }
            catch (Exception ex) { log.LogDebug(ex, "No se pudo cerrar limpiamente el WebSocket de voz."); }
            return Results.Empty;
        });
    }

    /// <summary>
    /// Edición de un TrueCentral Speaker: solo nombre, grupo, ubicación y activo. La conexión no se edita (la define el
    /// equipo al enrolarse; para cambiarlo se genera otro código).
    /// </summary>
    private static async Task<IResult> UpdateAgentSpeakerAsync(HttpContext ctx, Speaker speaker, SpeakerWriteDto request, VmsDbContext db,
        LicenseService license, IHubContext<VmsHub> hub, SpeakerService service, AuditService audit, CancellationToken ct)
    {
        var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, speaker.LocationId, ct);
        if (locationError is not null) return Error(locationError, StatusCodes.Status404NotFound);
        int? previousLocation = speaker.LocationId;

        var changes = new List<string>();
        if (speaker.Name != request.Name.Trim()) changes.Add($"nombre '{speaker.Name}' → '{request.Name.Trim()}'");
        if (locationId != previousLocation) changes.Add(EquipmentLocation.Change(previousLocation, locationId));
        if (speaker.Enabled != request.Enabled) changes.Add(request.Enabled ? "activado" : "desactivado");

        if (!speaker.Enabled && request.Enabled
            && license.Deny(LicenseFeatures.ModuleSpeakers, LicenseFeatures.SpeakerChannels,
                await db.Speakers.CountAsync(s => s.Enabled && s.Id != speaker.Id, ct)) is { } denied)
            return await license.DenyAsync(ctx, denied, "speaker", speaker.Name);
        speaker.Name = request.Name.Trim();
        speaker.LocationId = locationId;
        speaker.Enabled = request.Enabled;
        speaker.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(ctx, "speakers", "speaker-updated",
            targetType: "speaker", targetId: speaker.Id.ToString(), targetName: speaker.Name,
            detail: $"Modificó el parlante '{speaker.Name}': " + (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios."));
        if (locationId != previousLocation) await EquipmentLocation.MovedAsync(ctx, ct);
        service.RequestReconcile();
        await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "speakers", cancellationToken: ct);
        return Results.Ok(service.ToDto(speaker));
    }

    // ------------------------------------------------------------------
    // TrueCentral Speaker (agente en Raspberry Pi / Windows)
    // ------------------------------------------------------------------

    public static void MapSpeakerAgentsApi(this WebApplication app)
    {
        // WebSocket del agente. Sin sesión de usuario: el agente se identifica con su token o se enrola con el código.
        app.Map(SpeakerAgentProtocol.ConnectPath, async (HttpContext ctx, SpeakerAgentHub agents) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) return Error("Este recurso solo acepta WebSocket.", StatusCodes.Status400BadRequest);
            await agents.HandleAsync(ctx);
            return Results.Empty;
        });

        // Genera un código de enrolamiento de un solo uso (15 min). Si el parlante ya tenía un equipo, este sigue
        // funcionando hasta que otro se enrole con el código nuevo (así se reemplaza un equipo dañado).
        app.MapPost("/api/speakers/{id:int}/agent/enrollment", async (HttpContext ctx, int id, VmsDbContext db, SpeakerService service,
            IHubContext<VmsHub> hub, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SpeakersConfigure, out _) is { } failure) return failure;
            var speaker = await db.Speakers.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (speaker is null) return Results.NotFound();
            if (speaker.DriverKey != SpeakerAgentProtocol.DriverKey) return Error("Este parlante no es un TrueCentral Speaker.");
            string code = SpeakerAgentHub.NewEnrollmentCode();
            speaker.EnrollmentCodeHash = SpeakerAgentHub.HashSecret(code);
            speaker.EnrollmentExpiresAt = DateTime.UtcNow + SpeakerAgentHub.EnrollmentLifetime;
            speaker.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "speakers", "agent-enrollment-created", targetType: "speaker", targetId: speaker.Id.ToString(),
                targetName: speaker.Name,
                detail: $"Generó un código de enrolamiento para el TrueCentral Speaker '{speaker.Name}' (vence en " +
                        $"{SpeakerAgentHub.EnrollmentLifetime.TotalMinutes:0} min" +
                        (speaker.AgentId is null ? ")." : "; el equipo actual sigue enrolado hasta que otro use el código)."));
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "speakers", cancellationToken: ct);
            return Results.Ok(new SpeakerEnrollmentDto(speaker.Id, SpeakerAgentHub.FormatCode(code), speaker.EnrollmentExpiresAt.Value));
        });

        // Revoca el equipo enrolado (y el código pendiente): se corta su conexión y deberá enrolarse de nuevo.
        app.MapDelete("/api/speakers/{id:int}/agent", async (HttpContext ctx, int id, VmsDbContext db, SpeakerService service,
            SpeakerAgentHub agents, IHubContext<VmsHub> hub, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SpeakersConfigure, out _) is { } failure) return failure;
            var speaker = await db.Speakers.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (speaker is null) return Results.NotFound();
            if (speaker.DriverKey != SpeakerAgentProtocol.DriverKey) return Error("Este parlante no es un TrueCentral Speaker.");
            if (service.BusyOf(id) is { } busy)
                return Error($"El parlante está en uso ({busy}); deténgalo antes de revocarlo.", StatusCodes.Status409Conflict);
            string where = WhereOf(speaker);
            bool hadAgent = speaker.AgentId is not null;
            string? revokedAgent = ForgetAgent(speaker);
            speaker.Status = SpeakerStatus.Offline;
            speaker.LastError = AwaitingEnrollment;
            speaker.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await agents.DisconnectAsync(revokedAgent, SpeakerAgentProtocol.CloseCodes.Revoked, "Un administrador revocó este equipo en TrueCentral.");
            await audit.LogAsync(ctx, "speakers", "agent-revoked", targetType: "speaker", targetId: speaker.Id.ToString(), targetName: speaker.Name,
                detail: hadAgent
                    ? $"Revocó el TrueCentral Speaker '{speaker.Name}' ({where}): el equipo ya no puede conectarse hasta enrolarlo de nuevo."
                    : $"Anuló el código de enrolamiento pendiente del TrueCentral Speaker '{speaker.Name}'.");
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "speakers", cancellationToken: ct);
            return Results.Ok(service.ToDto(speaker));
        });

        // Tono de prueba del altavoz (solo los TrueCentral Speaker lo exponen).
        app.MapPost("/api/speakers/{id:int}/test-tone", async (HttpContext ctx, int id, VmsDbContext db, SpeakerService service,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SpeakersPlay, out var session) is { } failure) return failure;
            if (await SpeakersOutOfScopeAsync(ctx, session, [id], "probar el altavoz de") is { } outOfScope) return outOfScope;
            var speaker = await db.Speakers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (speaker is null) return Results.NotFound();
            if (service.DriverOf(speaker) is not AgentSpeakerDriver driver) return Error("Este parlante no tiene tono de prueba.");
            if (service.BusyOf(id) is { } busy) return Error($"El parlante está ocupado ({busy}).", StatusCodes.Status409Conflict);
            try
            {
                await driver.TestToneAsync(service.ConnectionOf(speaker), 3, ct);
                await audit.LogAsync(ctx, "speakers", "test-tone", targetType: "speaker", targetId: speaker.Id.ToString(), targetName: speaker.Name,
                    detail: $"Hizo sonar el tono de prueba en '{speaker.Name}'.");
                return Results.Ok(new { message = "Tono de prueba enviado (3 s)." });
            }
            catch (DriverException ex)
            {
                await audit.LogAsync(ctx, "speakers", "test-tone", targetType: "speaker", targetId: speaker.Id.ToString(), targetName: speaker.Name,
                    detail: $"No se pudo hacer sonar el tono de prueba en '{speaker.Name}': {ex.Message}", success: false);
                return Error(ex.Message);
            }
        });
    }
}
