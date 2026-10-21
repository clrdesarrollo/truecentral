using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Licensing;
using TrueCentralVms.Server.Services.Workflows;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// API de las pantallas de alarma, en dos partes:
/// <list type="bullet">
/// <item><c>/api/screen/*</c>: lo que consume la propia pantalla con su
/// credencial (emparejamiento anónimo, configuración, resincronización, fotos,
/// sonidos y actualización remota).</item>
/// <item><c>/api/alarm-screens/*</c>: el mantenedor del panel (aprobar,
/// configurar, reiniciar, quitar), con el permiso «Pantallas de alarma».</item>
/// </list>
/// Todo lo que cambia algo queda en la bitácora.
/// </summary>
public static class AlarmScreensApi
{
    private const int MaxName = 128;

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    /// <summary>
    /// La pantalla que hace el pedido (la identificó el middleware por su
    /// credencial). 401 = credencial desconocida (la pantalla vuelve a
    /// emparejarse); 403 = pantalla deshabilitada (conserva la credencial y espera).
    /// </summary>
    private static IResult? RequireScreen(HttpContext ctx, out ScreenIdentity screen)
    {
        screen = AlarmScreenService.CurrentScreen(ctx)!;
        if (screen is not null) return null;
        return ctx.Items[AlarmScreenService.DisabledItemKey] is true
            ? Results.Json(new { error = "La pantalla está deshabilitada desde el panel." }, statusCode: StatusCodes.Status403Forbidden)
            : Results.Json(new { error = "Credencial de pantalla inválida." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    private static string? RemoteIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    private static string StatusLabel(string status) => status switch
    {
        AlarmScreenStatus.Approved => "aprobada",
        AlarmScreenStatus.Rejected => "rechazada",
        AlarmScreenStatus.Disabled => "deshabilitada",
        _ => "pendiente de aprobación",
    };

    private static IReadOnlyList<string> AudioOutputsOf(AlarmScreen screen)
    {
        try
        {
            return screen.AudioOutputsJson is { Length: > 0 } json
                ? JsonSerializer.Deserialize<List<string>>(json) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static AlarmScreenDto ToDto(AlarmScreen s, AlarmScreenService service, int pending) => new(
        s.Id, s.Name, s.Status, s.MachineId, s.Hostname, s.AppVersion,
        s.Status == AlarmScreenStatus.Pending ? s.PairingCode : null,
        s.TokenHash is not null, service.IsOnline(s.Id), s.LastSeenAt, s.LastIp,
        s.Volume, s.Layout, s.AudioOutput, AudioOutputsOf(s), s.CreatedAt, s.ApprovedAt, s.ApprovedBy, pending);

    private static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".ogg" => "audio/ogg",
        ".m4a" or ".aac" => "audio/aac",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        _ => "application/octet-stream",
    };

    public static void MapAlarmScreensApi(this WebApplication app)
    {
        // ==================================================================
        // Lo que consume la pantalla
        // ==================================================================

        // Alta o re-anuncio del equipo. Anónimo por naturaleza: todavía no
        // tiene credencial. Devuelve el código que la pantalla muestra para
        // que un administrador la apruebe en el panel.
        app.MapPost("/api/screen/pair", async (HttpContext ctx, AlarmScreenPairStartRequest request, VmsDbContext db,
            AlarmScreenService screens, AuditService audit, Services.Https.ServerCertificateManager certificates, CancellationToken ct) =>
        {
            string machineId = request.MachineId?.Trim() ?? "";
            if (machineId.Length is 0 or > 64) return Error("Falta el identificador del equipo (machineId).", StatusCodes.Status400BadRequest);
            string hostname = Cut(string.IsNullOrWhiteSpace(request.Hostname) ? machineId : request.Hostname.Trim(), MaxName);

            // La pantalla dice qué llave recibió: si no es la nuestra, entre ella y el servidor hay alguien que
            // descifra la conexión (o un proxy). No se le da código: aprobarla le entregaría la credencial.
            string ownKey = Services.Https.ServerCertificateManager.SpkiFingerprint(certificates.Current);
            if (request.ServerKey is { Length: > 0 } seen && !seen.Equals(ownKey, StringComparison.OrdinalIgnoreCase))
            {
                if (audit.ShouldLog($"screen-key-mismatch:{machineId}", TimeSpan.FromMinutes(30)))
                    await audit.LogSystemAsync("screens", "screen-key-mismatch", targetType: "alarm-screen", targetName: hostname,
                        detail: $"El equipo «{hostname}» ({RemoteIp(ctx)}) pidió emparejarse viendo otra llave de servidor " +
                                $"({seen[..Math.Min(16, seen.Length)]}…, la nuestra es {ownKey[..16]}…): posible interceptación de la red. " +
                                "No se le dio código.",
                        success: false, username: AlarmScreenService.ScreenActor(hostname), clientIp: RemoteIp(ctx),
                        origin: AlarmScreenService.FromScreen);
                return Error("La pantalla no está hablando directamente con el servidor (otra llave): revise la red.",
                    StatusCodes.Status409Conflict);
            }
            string? version = string.IsNullOrWhiteSpace(request.Version) ? null : Cut(request.Version.Trim(), 32);

            var screen = await db.AlarmScreens.FirstOrDefaultAsync(s => s.MachineId == machineId, ct);
            bool isNew = screen is null;
            if (screen is null)
            {
                screen = new AlarmScreen
                {
                    MachineId = machineId,
                    Name = hostname,
                    Hostname = hostname,
                    AppVersion = version,
                    Status = AlarmScreenStatus.Pending,
                    PairingCode = AlarmScreenService.NewPairingCode(),
                };
                db.AlarmScreens.Add(screen);
            }
            else
            {
                screen.Hostname = hostname;
                screen.AppVersion = version ?? screen.AppVersion;
                // Pendiente sin código (p. ej. tras «reiniciar emparejamiento»): se le da uno.
                if (screen.Status == AlarmScreenStatus.Pending) screen.PairingCode ??= AlarmScreenService.NewPairingCode();
            }
            screen.LastIp = RemoteIp(ctx) is { } ip ? Cut(ip, 64) : screen.LastIp;
            await db.SaveChangesAsync(ct);

            if (isNew || audit.ShouldLog($"screen-pair:{machineId}", TimeSpan.FromMinutes(30)))
            {
                await audit.LogSystemAsync("screens", "screen-pair-requested", targetType: "alarm-screen",
                    targetId: screen.Id.ToString(), targetName: screen.Name,
                    detail: $"El equipo «{hostname}» ({RemoteIp(ctx)}) pidió emparejarse como pantalla de alarma " +
                            $"(estado: {StatusLabel(screen.Status)}).",
                    data: new { machineId, hostname, version },
                    username: AlarmScreenService.ScreenActor(hostname), clientIp: RemoteIp(ctx), origin: AlarmScreenService.FromScreen);
                await screens.NotifyPanelAsync();
            }

            // Ya emparejada y con credencial: no se vuelve a emparejar sola (sería
            // una forma de robarle la caseta). Solo el administrador la reinicia.
            string code = screen.Status == AlarmScreenStatus.Pending ? screen.PairingCode ?? "" : "";
            return Results.Ok(new AlarmScreenPairStartResponse(screen.Id, code, screen.Status));
        });

        // Sondeo del emparejamiento: la credencial se entrega UNA vez, a quien
        // presente el MachineId y el código que muestra la pantalla.
        app.MapGet("/api/screen/pair/{machineId}", async (HttpContext ctx, string machineId, string? code, VmsDbContext db,
            AlarmScreenService screens, AuditService audit, CancellationToken ct) =>
        {
            var screen = await db.AlarmScreens.FirstOrDefaultAsync(s => s.MachineId == machineId, ct);
            if (screen is null) return Results.NotFound(new { error = "Equipo desconocido." });
            if (screen.Status is AlarmScreenStatus.Rejected or AlarmScreenStatus.Disabled)
                return Results.Ok(new AlarmScreenPairStatusResponse { Status = screen.Status });
            if (screen.PairingCode is null || !AlarmScreenService.SameCode(code?.Trim(), screen.PairingCode))
                return Error("Código de emparejamiento inválido.", StatusCodes.Status403Forbidden);
            if (screen.Status != AlarmScreenStatus.Approved)
                return Results.Ok(new AlarmScreenPairStatusResponse { Status = AlarmScreenStatus.Pending });

            string token = AlarmScreenService.NewToken();
            screen.TokenHash = AlarmScreenService.HashToken(token);
            screen.PairingCode = null;
            screen.LastIp = RemoteIp(ctx) is { } ip ? Cut(ip, 64) : screen.LastIp;
            await db.SaveChangesAsync(ct);

            var settings = await AlarmScreenService.SettingsAsync(db, ct);
            await audit.LogSystemAsync("screens", "screen-paired", targetType: "alarm-screen",
                targetId: screen.Id.ToString(), targetName: screen.Name,
                detail: $"La pantalla de alarma «{screen.Name}» ({screen.Hostname}, {RemoteIp(ctx)}) recibió su credencial " +
                        "y quedó emparejada.",
                username: AlarmScreenService.ScreenActor(screen.Name), clientIp: RemoteIp(ctx), origin: AlarmScreenService.FromScreen);
            await screens.NotifyPanelAsync();
            return Results.Ok(new AlarmScreenPairStatusResponse
            {
                Status = AlarmScreenStatus.Approved,
                Token = token,
                ScreenId = screen.Id,
                ScreenName = screen.Name,
                SiteName = AlarmScreenService.SiteNameOf(settings),
            });
        });

        app.MapGet("/api/screen/session", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (RequireScreen(ctx, out var screen) is { } failure) return failure;
            var settings = await AlarmScreenService.SettingsAsync(db, ct);
            return Results.Ok(new AlarmScreenSessionDto
            {
                ScreenId = screen.Id,
                ScreenName = screen.Name,
                SiteName = AlarmScreenService.SiteNameOf(settings),
                ServerVersion = typeof(AlarmScreensApi).Assembly.GetName().Version?.ToString(3) ?? "",
            });
        });

        app.MapGet("/api/screen/config", async (HttpContext ctx, VmsDbContext db, AlarmScreenService screens, CancellationToken ct) =>
        {
            if (RequireScreen(ctx, out var identity) is { } failure) return failure;
            var screen = await db.AlarmScreens.AsNoTracking().FirstOrDefaultAsync(s => s.Id == identity.Id, ct);
            if (screen is null) return Results.NotFound();
            var settings = await AlarmScreenService.SettingsAsync(db, ct);
            return Results.Ok(new AlarmScreenConfigDto
            {
                ScreenName = screen.Name,
                SiteName = AlarmScreenService.SiteNameOf(settings),
                Volume = Math.Clamp(screen.Volume, 0, 100),
                Layout = screen.Layout,
                AudioOutput = screen.AudioOutput,
                RepeatBeepSeconds = settings.RepeatBeepSeconds,
                MaxReminders = settings.MaxReminders,
                AlarmSound = settings.AlarmSound,
                HighPrioritySound = settings.HighPrioritySound,
                ReminderSound = settings.ReminderSound,
                Sounds = screens.Sounds(),
            });
        });

        app.MapGet("/api/screen/alerts/recent", async (HttpContext ctx, AlarmScreenService screens, CancellationToken ct) =>
        {
            if (RequireScreen(ctx, out var screen) is { } failure) return failure;
            return Results.Ok(await screens.RecentAsync(screen.Id, ct));
        });

        app.MapGet("/api/screen/alerts/{id:long}/photo", async (HttpContext ctx, long id, VmsDbContext db,
            WorkflowStore store, CancellationToken ct) =>
        {
            if (RequireScreen(ctx, out var screen) is { } failure) return failure;
            var alert = await db.WorkflowAlerts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
            // Solo la foto de un aviso que le llegó a ESTA pantalla.
            if (alert is null || !AlarmScreenService.Targets(alert, screen.Id)) return Results.NotFound();
            return store.FullPath(alert.ImagePath) is { } path && File.Exists(path)
                ? Results.File(path, ContentTypeOf(path))
                : Results.NotFound();
        });

        app.MapGet("/api/screen/sounds/{name}", (HttpContext ctx, string name, WorkflowStore store) =>
        {
            if (RequireScreen(ctx, out _) is { } failure) return failure;
            return store.FindAudio(name) is { } path ? Results.File(path, ContentTypeOf(path)) : Results.NotFound();
        });

        // La pantalla informa por qué salidas puede sonar (HDMI, jack, USB):
        // las descubre ella y el panel las ofrece. Es un inventario, no una
        // credencial: se guarda tal cual y el próximo reporte lo corrige.
        app.MapPost("/api/screen/audio-outputs", async (HttpContext ctx, string[] outputs, VmsDbContext db,
            AlarmScreenService screens, CancellationToken ct) =>
        {
            if (RequireScreen(ctx, out var screen) is { } failure) return failure;
            var clean = outputs.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => Cut(o.Trim(), 200))
                .Distinct().Take(20).ToArray();
            string json = JsonSerializer.Serialize(clean);
            await db.AlarmScreens.Where(s => s.Id == screen.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.AudioOutputsJson, json), ct);
            await screens.NotifyPanelAsync();
            return Results.NoContent();
        });

        // Actualización remota: la pantalla consulta la versión publicada y la
        // descarga junto con su manifiesto firmado (.manifest y .manifest.sig). El
        // script de la Pi verifica la firma con la llave pública de CLRobotics que
        // trae instalada: el servidor solo reenvía, no puede fabricar una firma.
        app.MapGet("/api/screen/client/version", (HttpContext ctx, AlarmScreenService screens) =>
        {
            if (RequireScreen(ctx, out _) is { } failure) return failure;
            if (screens.LatestRelease() is not { } release) return Results.NoContent();
            return Results.Ok(new AlarmScreenClientVersionDto
            {
                Version = release.Version,
                DownloadUrl = $"/api/screen/client/download/{release.FileName}",
                Sha256 = release.Sha256,
                Size = release.Size,
                PublishedAt = release.PublishedAt,
            });
        });

        app.MapGet("/api/screen/client/download/{fileName}", (HttpContext ctx, string fileName, AlarmScreenService screens) =>
        {
            if (RequireScreen(ctx, out _) is { } failure) return failure;
            if (screens.LatestRelease() is not { } release) return Results.NotFound();
            if (string.Equals(release.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                return Results.File(release.FullPath, "application/gzip", release.FileName);
            // El manifiesto y su firma, solo los del paquete vigente.
            foreach (string suffix in new[] { ".manifest", ".manifest.sig" })
                if (string.Equals(release.FileName + suffix, fileName, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(release.FullPath + suffix))
                    return Results.File(release.FullPath + suffix, "text/plain", release.FileName + suffix);
            return Results.NotFound();
        });

        // ==================================================================
        // Mantenedor (panel web)
        // ==================================================================

        app.MapGet("/api/alarm-screens", async (HttpContext ctx, VmsDbContext db, AlarmScreenService screens,
            LicenseService license, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.ScreensConfigure, out _) is { } failure) return failure;
            var list = await db.AlarmScreens.AsNoTracking().OrderBy(s => s.Status == AlarmScreenStatus.Pending ? 0 : 1)
                .ThenBy(s => s.Name).ToListAsync(ct);
            var settings = await AlarmScreenService.SettingsAsync(db, ct);
            var pending = await AlarmScreenService.PendingByScreenAsync(db, list.Select(s => s.Id).ToList(), settings.ResyncHours, ct);
            return Results.Ok(new
            {
                items = list.Select(s => ToDto(s, screens, s.Status == AlarmScreenStatus.Approved ? pending.GetValueOrDefault(s.Id) : 0)),
                licensed = license.IsModuleEnabled(LicenseFeatures.ModuleAlarmScreens),
                quota = license.Quota(LicenseFeatures.AlarmScreens),
                inUse = list.Count(s => s.Status == AlarmScreenStatus.Approved),
                clientVersion = screens.LatestRelease()?.Version,
            });
        });

        app.MapPost("/api/alarm-screens/{id:int}/approve", async (HttpContext ctx, int id, AlarmScreenApproveDto request,
            VmsDbContext db, AlarmScreenService screens, LicenseService license, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.ScreensConfigure, out var session) is { } failure) return failure;
            string name = request.Name?.Trim() ?? "";
            if (name.Length is 0 or > MaxName) return Error($"El nombre es obligatorio (máximo {MaxName} caracteres).");
            var screen = await db.AlarmScreens.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (screen is null) return Results.NotFound();
            if (screen.Status != AlarmScreenStatus.Pending)
                return Error($"La pantalla no está esperando aprobación (está {StatusLabel(screen.Status)}).", StatusCodes.Status409Conflict);
            if (await db.AlarmScreens.AnyAsync(s => s.Id != id && s.Name == name, ct))
                return Error("Ya hay otra pantalla con ese nombre.", StatusCodes.Status409Conflict);
            if (license.Deny(LicenseFeatures.ModuleAlarmScreens, LicenseFeatures.AlarmScreens,
                    await db.AlarmScreens.CountAsync(s => s.Status == AlarmScreenStatus.Approved, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "alarm-screen", name);

            screen.Name = name;
            screen.Status = AlarmScreenStatus.Approved;
            screen.ApprovedAt = DateTime.UtcNow;
            screen.ApprovedBy = Cut(session.Username, 64);
            // Si ya tenía credencial (se aprueba de nuevo tras rechazarla), se descarta: la nueva sale por el código.
            screen.TokenHash = null;
            screen.PairingCode ??= AlarmScreenService.NewPairingCode();
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "screens", "screen-approved", targetType: "alarm-screen",
                targetId: screen.Id.ToString(), targetName: screen.Name,
                detail: $"Aprobó el emparejamiento de la pantalla de alarma «{screen.Name}» (equipo {screen.Hostname}, " +
                        $"{screen.LastIp ?? "IP desconocida"}).",
                data: new { screen.MachineId, screen.Hostname, screen.LastIp });
            await screens.NotifyPanelAsync();
            return Results.Ok(ToDto(screen, screens, 0));
        });

        app.MapPost("/api/alarm-screens/{id:int}/reject", async (HttpContext ctx, int id, VmsDbContext db,
            AlarmScreenService screens, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.ScreensConfigure, out _) is { } failure) return failure;
            var screen = await db.AlarmScreens.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (screen is null) return Results.NotFound();
            if (screen.Status != AlarmScreenStatus.Pending)
                return Error($"La pantalla no está esperando aprobación (está {StatusLabel(screen.Status)}).", StatusCodes.Status409Conflict);
            screen.Status = AlarmScreenStatus.Rejected;
            screen.PairingCode = null;
            screen.TokenHash = null;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "screens", "screen-rejected", targetType: "alarm-screen",
                targetId: screen.Id.ToString(), targetName: screen.Name,
                detail: $"Rechazó el emparejamiento del equipo «{screen.Hostname}» ({screen.LastIp ?? "IP desconocida"}) como pantalla de alarma.",
                data: new { screen.MachineId, screen.Hostname, screen.LastIp });
            await screens.NotifyPanelAsync();
            return Results.Ok(ToDto(screen, screens, 0));
        });

        app.MapPut("/api/alarm-screens/{id:int}", async (HttpContext ctx, int id, AlarmScreenWriteDto request, VmsDbContext db,
            AlarmScreenService screens, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.ScreensConfigure, out _) is { } failure) return failure;
            string name = request.Name?.Trim() ?? "";
            if (name.Length is 0 or > MaxName) return Error($"El nombre es obligatorio (máximo {MaxName} caracteres).");
            if (request.Volume is < 0 or > 100) return Error("El volumen debe estar entre 0 y 100.");
            if (request.Layout is not (AlarmScreenLayouts.Horizontal or AlarmScreenLayouts.Vertical))
                return Error("Disposición desconocida: debe ser apaisada u horizontal, o en pie (vertical).");
            string? output = string.IsNullOrWhiteSpace(request.AudioOutput) ? null : request.AudioOutput.Trim();
            if (output is { Length: > 256 }) return Error("El nombre de la salida de audio es demasiado largo.");
            var screen = await db.AlarmScreens.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (screen is null) return Results.NotFound();
            if (await db.AlarmScreens.AnyAsync(s => s.Id != id && s.Name == name, ct))
                return Error("Ya hay otra pantalla con ese nombre.", StatusCodes.Status409Conflict);

            var changes = new List<string>();
            if (screen.Name != name) changes.Add($"nombre «{screen.Name}» → «{name}»");
            if (screen.Volume != request.Volume) changes.Add($"volumen {screen.Volume} → {request.Volume}");
            if (screen.Layout != request.Layout)
                changes.Add(request.Layout == AlarmScreenLayouts.Vertical ? "monitor en pie" : "monitor apaisado");
            if (screen.AudioOutput != output) changes.Add($"salida de audio «{screen.AudioOutput ?? "la del sistema"}» → «{output ?? "la del sistema"}»");
            if (changes.Count == 0) return Results.Ok(ToDto(screen, screens, 0));

            string before = screen.Name;
            screen.Name = name;
            screen.Volume = request.Volume;
            screen.Layout = request.Layout;
            screen.AudioOutput = output;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "screens", "screen-updated", targetType: "alarm-screen",
                targetId: screen.Id.ToString(), targetName: screen.Name,
                detail: $"Modificó la pantalla de alarma «{before}»: {string.Join("; ", changes)}.",
                data: new { screen.Name, screen.Volume, screen.Layout, screen.AudioOutput });
            // Se aplica sin ir a la caseta ni reiniciar nada.
            await screens.ConfigChangedAsync(screen.Id, ct);
            await screens.NotifyPanelAsync();
            return Results.Ok(ToDto(screen, screens, 0));
        });

        app.MapPost("/api/alarm-screens/{id:int}/disable", async (HttpContext ctx, int id, VmsDbContext db,
            AlarmScreenService screens, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.ScreensConfigure, out _) is { } failure) return failure;
            var screen = await db.AlarmScreens.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (screen is null) return Results.NotFound();
            if (screen.Status != AlarmScreenStatus.Approved)
                return Error($"Solo se puede deshabilitar una pantalla aprobada (está {StatusLabel(screen.Status)}).", StatusCodes.Status409Conflict);
            screen.Status = AlarmScreenStatus.Disabled;
            await db.SaveChangesAsync(ct);
            screens.Disconnect(screen.Id, "deshabilitada desde el panel");
            await audit.LogAsync(ctx, "screens", "screen-disabled", targetType: "alarm-screen",
                targetId: screen.Id.ToString(), targetName: screen.Name,
                detail: $"Deshabilitó la pantalla de alarma «{screen.Name}»: deja de recibir avisos hasta que se habilite.");
            await screens.NotifyPanelAsync();
            return Results.Ok(ToDto(screen, screens, 0));
        });

        app.MapPost("/api/alarm-screens/{id:int}/enable", async (HttpContext ctx, int id, VmsDbContext db,
            AlarmScreenService screens, LicenseService license, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.ScreensConfigure, out _) is { } failure) return failure;
            var screen = await db.AlarmScreens.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (screen is null) return Results.NotFound();
            if (screen.Status != AlarmScreenStatus.Disabled)
                return Error($"La pantalla no está deshabilitada (está {StatusLabel(screen.Status)}).", StatusCodes.Status409Conflict);
            if (license.Deny(LicenseFeatures.ModuleAlarmScreens, LicenseFeatures.AlarmScreens,
                    await db.AlarmScreens.CountAsync(s => s.Status == AlarmScreenStatus.Approved, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "alarm-screen", screen.Name);
            screen.Status = AlarmScreenStatus.Approved;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "screens", "screen-enabled", targetType: "alarm-screen",
                targetId: screen.Id.ToString(), targetName: screen.Name,
                detail: $"Volvió a habilitar la pantalla de alarma «{screen.Name}».");
            await screens.NotifyPanelAsync();
            return Results.Ok(ToDto(screen, screens, 0));
        });

        // La pantalla perdió su credencial (se reinstaló, se cambió la tarjeta
        // SD) o hay que cambiarla: se descarta y vuelve a pedir aprobación con
        // un código nuevo.
        app.MapPost("/api/alarm-screens/{id:int}/reset-pairing", async (HttpContext ctx, int id, VmsDbContext db,
            AlarmScreenService screens, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.ScreensConfigure, out _) is { } failure) return failure;
            var screen = await db.AlarmScreens.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (screen is null) return Results.NotFound();
            string previous = screen.Status;
            screen.Status = AlarmScreenStatus.Pending;
            screen.TokenHash = null;
            screen.PairingCode = AlarmScreenService.NewPairingCode();
            screen.ApprovedAt = null;
            screen.ApprovedBy = null;
            await db.SaveChangesAsync(ct);
            screens.Disconnect(screen.Id, "emparejamiento reiniciado desde el panel");
            await audit.LogAsync(ctx, "screens", "screen-pairing-reset", targetType: "alarm-screen",
                targetId: screen.Id.ToString(), targetName: screen.Name,
                detail: $"Reinició el emparejamiento de la pantalla de alarma «{screen.Name}» (estaba {StatusLabel(previous)}): " +
                        "su credencial dejó de valer y debe aprobarse de nuevo con el código que mostrará.");
            await screens.NotifyPanelAsync();
            return Results.Ok(ToDto(screen, screens, 0));
        });

        app.MapPost("/api/alarm-screens/{id:int}/reboot", async (HttpContext ctx, int id, VmsDbContext db,
            AlarmScreenService screens, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.ScreensConfigure, out _) is { } failure) return failure;
            var screen = await db.AlarmScreens.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (screen is null) return Results.NotFound();
            if (!screens.IsOnline(screen.Id))
            {
                await audit.LogAsync(ctx, "screens", "screen-reboot", targetType: "alarm-screen",
                    targetId: screen.Id.ToString(), targetName: screen.Name,
                    detail: $"Pidió reiniciar la pantalla de alarma «{screen.Name}», pero no está conectada.", success: false);
                return Error("La pantalla no está conectada: no se le puede ordenar el reinicio.", StatusCodes.Status409Conflict);
            }
            await screens.RebootAsync(screen.Id, ct);
            await audit.LogAsync(ctx, "screens", "screen-reboot", targetType: "alarm-screen",
                targetId: screen.Id.ToString(), targetName: screen.Name,
                detail: $"Ordenó reiniciar el equipo de la pantalla de alarma «{screen.Name}».");
            return Results.Ok(new { ok = true });
        });

        app.MapDelete("/api/alarm-screens/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            AlarmScreenService screens, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.ScreensConfigure, out _) is { } failure) return failure;
            var screen = await db.AlarmScreens.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (screen is null) return Results.NotFound();
            db.AlarmScreens.Remove(screen);
            await db.SaveChangesAsync(ct);
            screens.Disconnect(screen.Id, "eliminada desde el panel");
            await audit.LogAsync(ctx, "screens", "screen-deleted", targetType: "alarm-screen",
                targetId: screen.Id.ToString(), targetName: screen.Name,
                detail: $"Quitó la pantalla de alarma «{screen.Name}» (equipo {screen.Hostname}, estaba {StatusLabel(screen.Status)}). " +
                        "Si el equipo vuelve a anunciarse, aparecerá como pendiente de aprobación.",
                data: new { screen.MachineId, screen.Hostname });
            await screens.NotifyPanelAsync();
            return Results.NoContent();
        });

        // ------------------------------------------------------------------
        // Ajustes comunes (sonidos, recordatorio, nombre de la instalación)
        // ------------------------------------------------------------------
        app.MapGet("/api/alarm-screens/settings", async (HttpContext ctx, VmsDbContext db, AlarmScreenService screens,
            WorkflowStore store, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.ScreensConfigure, out _) is { } failure) return failure;
            var s = await AlarmScreenService.SettingsAsync(db, ct);
            return Results.Ok(new
            {
                settings = new AlarmScreenSettingsDto(s.SiteName, s.RepeatBeepSeconds, s.MaxReminders,
                    s.AlarmSound, s.HighPrioritySound, s.ReminderSound, s.ResyncHours, screens.LatestRelease()?.Version),
                // Los sonidos de la biblioteca (Automatizaciones → Sonidos) que se pueden elegir.
                sounds = store.ListAudio().Select(a => a.DisplayName).ToList(),
                defaultSiteName = Environment.MachineName,
                packageDirectory = screens.PackageDirectory,
            });
        });

        app.MapPut("/api/alarm-screens/settings", async (HttpContext ctx, AlarmScreenSettingsDto request, VmsDbContext db,
            WorkflowStore store, AlarmScreenService screens, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.ScreensConfigure, out _) is { } failure) return failure;
            string siteName = request.SiteName?.Trim() ?? "";
            if (siteName.Length > MaxName) return Error($"El nombre de la instalación no puede superar los {MaxName} caracteres.");
            if (request.RepeatBeepSeconds is < 0 or > 600) return Error("El recordatorio debe ser de 0 a 600 segundos (0 = solo al llegar).");
            if (request.MaxReminders is < 0 or > 1000) return Error("Las insistencias deben ser de 0 a 1000 (0 = sin límite).");
            if (request.ResyncHours is < 1 or > 168) return Error("La ventana de resincronización debe ser de 1 a 168 horas.");
            string? Sound(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            foreach (var sound in new[] { Sound(request.AlarmSound), Sound(request.HighPrioritySound), Sound(request.ReminderSound) })
                if (sound is not null && store.FindAudio(sound) is null)
                    return Error($"El sonido «{sound}» no está cargado en el servidor (Automatizaciones → Sonidos).");

            var settings = await db.AlarmScreenSettings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
            bool isNew = settings is null;
            settings ??= new AlarmScreenSettings();
            var changes = new List<string>();
            void Track(string label, object? before, object? after)
            {
                if (!Equals(before, after)) changes.Add($"{label} «{before ?? "—"}» → «{after ?? "—"}»");
            }
            Track("nombre de la instalación", settings.SiteName, siteName);
            Track("recordatorio (s)", settings.RepeatBeepSeconds, request.RepeatBeepSeconds);
            Track("insistencias", settings.MaxReminders, request.MaxReminders);
            Track("sonido de aviso", settings.AlarmSound, Sound(request.AlarmSound));
            Track("sonido crítico", settings.HighPrioritySound, Sound(request.HighPrioritySound));
            Track("sonido de recordatorio", settings.ReminderSound, Sound(request.ReminderSound));
            Track("resincronización (h)", settings.ResyncHours, request.ResyncHours);

            settings.SiteName = siteName;
            settings.RepeatBeepSeconds = request.RepeatBeepSeconds;
            settings.MaxReminders = request.MaxReminders;
            settings.AlarmSound = Sound(request.AlarmSound);
            settings.HighPrioritySound = Sound(request.HighPrioritySound);
            settings.ReminderSound = Sound(request.ReminderSound);
            settings.ResyncHours = request.ResyncHours;
            settings.UpdatedAt = DateTime.UtcNow;
            if (isNew) db.AlarmScreenSettings.Add(settings);
            await db.SaveChangesAsync(ct);

            if (changes.Count > 0)
                await audit.LogAsync(ctx, "screens", "screen-settings-updated", targetType: "alarm-screen-settings",
                    detail: $"Modificó los ajustes de las pantallas de alarma: {string.Join("; ", changes)}.",
                    data: new { settings.SiteName, settings.RepeatBeepSeconds, settings.MaxReminders, settings.AlarmSound,
                        settings.HighPrioritySound, settings.ReminderSound, settings.ResyncHours });
            // Se aplica en todas las pantallas sin ir a las casetas.
            await screens.ConfigChangedAsync(null, ct);
            return Results.Ok(new AlarmScreenSettingsDto(settings.SiteName, settings.RepeatBeepSeconds, settings.MaxReminders,
                settings.AlarmSound, settings.HighPrioritySound, settings.ReminderSound, settings.ResyncHours,
                screens.LatestRelease()?.Version));
        });
    }
}
