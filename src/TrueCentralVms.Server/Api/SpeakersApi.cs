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
using TrueCentralVms.Server.Services.Workflows;

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

    private static string? ValidateWrite(SpeakerWriteDto request, SpeakerDriverRegistry drivers)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
            return "El nombre es obligatorio (máximo 128 caracteres).";
        if (string.IsNullOrWhiteSpace(request.Host))
            return "La dirección (IP o hostname) es obligatoria.";
        if (request.Port is < 1 or > 65535)
            return "El puerto debe estar entre 1 y 65535.";
        if (string.IsNullOrWhiteSpace(request.Username))
            return "El usuario del parlante es obligatorio.";
        if (request.GroupName is { Length: > 64 })
            return "El grupo no puede superar los 64 caracteres.";
        if (drivers.Find(request.DriverKey) is null)
            return $"Driver desconocido: '{request.DriverKey}'.";
        return null;
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

    private static string? GroupOf(SpeakerWriteDto request) =>
        string.IsNullOrWhiteSpace(request.GroupName) ? null : request.GroupName.Trim();

    private static void ApplyProbe(Speaker speaker, SpeakerInfo info)
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

    public static void MapSpeakersApi(this WebApplication app)
    {
        // ------------------------------------------------------------------
        // Catálogos
        // ------------------------------------------------------------------
        app.MapGet("/api/speakers/drivers", (HttpContext ctx, SpeakerDriverRegistry drivers) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.Ok(drivers.All.Select(f => new SpeakerDriverDto(f.DriverKey, f.DisplayName, f.DefaultPort, f.DefaultHttps)));
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
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var speakers = await db.Speakers.AsNoTracking().OrderBy(s => s.GroupName).ThenBy(s => s.Name).ToListAsync(ct);
            return Results.Ok(speakers.Select(service.ToDto));
        });

        app.MapGet("/api/speakers/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, SpeakerService service, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var speaker = await db.Speakers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            return speaker is null ? Results.NotFound() : Results.Ok(service.ToDto(speaker));
        });

        app.MapPost("/api/speakers", async (HttpContext ctx, SpeakerWriteDto request, VmsDbContext db, SpeakerDriverRegistry drivers,
            CredentialProtector protector, IHubContext<VmsHub> hub, SpeakerService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            if (string.IsNullOrEmpty(request.Password)) return Error("La contraseña del parlante es obligatoria.");
            string host = request.Host.Trim();
            if (await db.Speakers.AnyAsync(s => s.Host == host && s.Port == request.Port, ct))
                return Error("Ya existe un parlante con esa dirección y puerto.", StatusCodes.Status409Conflict);

            var conn = new SpeakerConnectionInfo(host, request.Port, request.UseHttps, request.Username, request.Password);
            var (info, libraryCount, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
            if (info is null)
            {
                await audit.LogAsync(ctx, "speakers", "speaker-created", targetType: "speaker", targetName: request.Name.Trim(),
                    detail: $"Alta de parlante rechazada por el equipo {host}:{request.Port}: {probeError}", success: false);
                return Error(probeError!);
            }

            var speaker = new Speaker
            {
                Name = request.Name.Trim(),
                DriverKey = request.DriverKey,
                Host = host,
                Port = request.Port,
                UseHttps = request.UseHttps,
                Username = request.Username,
                PasswordCiphertext = protector.Protect(request.Password),
                GroupName = GroupOf(request),
                Enabled = request.Enabled,
            };
            ApplyProbe(speaker, info);
            db.Speakers.Add(speaker);
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "speakers", "speaker-created",
                targetType: "speaker", targetId: speaker.Id.ToString(), targetName: speaker.Name,
                detail: $"Agregó el parlante '{speaker.Name}' ({speaker.DriverKey}, {speaker.Host}:{speaker.Port}, {speaker.Model ?? "modelo desconocido"}" +
                        (speaker.GroupName is null ? "" : $", grupo '{speaker.GroupName}'") +
                        $", biblioteca: {libraryCount} audios).",
                data: new { speaker.Host, speaker.Port, speaker.UseHttps, speaker.DriverKey, speaker.Model, speaker.SerialNumber, speaker.GroupName });
            service.RequestReconcile();
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "speakers", cancellationToken: ct);
            return Results.Ok(service.ToDto(speaker));
        });

        app.MapPut("/api/speakers/{id:int}", async (HttpContext ctx, int id, SpeakerWriteDto request, VmsDbContext db,
            SpeakerDriverRegistry drivers, CredentialProtector protector, IHubContext<VmsHub> hub, SpeakerService service,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            var speaker = await db.Speakers.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (speaker is null) return Results.NotFound();
            string host = request.Host.Trim();
            if (await db.Speakers.AnyAsync(s => s.Id != id && s.Host == host && s.Port == request.Port, ct))
                return Error("Ya existe otro parlante con esa dirección y puerto.", StatusCodes.Status409Conflict);

            string password = string.IsNullOrEmpty(request.Password) ? protector.Unprotect(speaker.PasswordCiphertext) : request.Password;
            bool connectionChanged = speaker.Host != host || speaker.Port != request.Port || speaker.UseHttps != request.UseHttps ||
                                     speaker.Username != request.Username || speaker.DriverKey != request.DriverKey ||
                                     !string.IsNullOrEmpty(request.Password);
            if (connectionChanged)
            {
                HikvisionSpeakerDriver.Forget(service.ConnectionOf(speaker));
                var conn = new SpeakerConnectionInfo(host, request.Port, request.UseHttps, request.Username, password);
                var (info, _, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
                if (info is null) return Error(probeError!);
                ApplyProbe(speaker, info);
            }

            var changes = new List<string>();
            if (speaker.Name != request.Name.Trim()) changes.Add($"nombre '{speaker.Name}' → '{request.Name.Trim()}'");
            if (speaker.Host != host || speaker.Port != request.Port) changes.Add($"dirección {speaker.Host}:{speaker.Port} → {host}:{request.Port}");
            if (speaker.UseHttps != request.UseHttps) changes.Add(request.UseHttps ? "HTTPS activado" : "HTTPS desactivado");
            if (speaker.Username != request.Username) changes.Add($"usuario '{speaker.Username}' → '{request.Username}'");
            if (!string.IsNullOrEmpty(request.Password)) changes.Add("contraseña cambiada");
            if (speaker.GroupName != GroupOf(request)) changes.Add($"grupo '{speaker.GroupName}' → '{GroupOf(request)}'");
            if (speaker.Enabled != request.Enabled) changes.Add(request.Enabled ? "activado" : "desactivado");

            speaker.Name = request.Name.Trim();
            speaker.DriverKey = request.DriverKey;
            speaker.Host = host;
            speaker.Port = request.Port;
            speaker.UseHttps = request.UseHttps;
            speaker.Username = request.Username;
            speaker.PasswordCiphertext = protector.Protect(password);
            speaker.GroupName = GroupOf(request);
            speaker.Enabled = request.Enabled;
            speaker.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "speakers", "speaker-updated",
                targetType: "speaker", targetId: speaker.Id.ToString(), targetName: speaker.Name,
                detail: $"Modificó el parlante '{speaker.Name}': " + (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios."));
            service.RequestReconcile();
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "speakers", cancellationToken: ct);
            return Results.Ok(service.ToDto(speaker));
        });

        app.MapDelete("/api/speakers/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, IHubContext<VmsHub> hub,
            SpeakerService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var speaker = await db.Speakers.FindAsync([id], ct);
            if (speaker is null) return Results.NotFound();
            if (service.BusyOf(id) is { } busy)
                return Error($"El parlante está en uso ({busy}); deténgalo antes de eliminarlo.", StatusCodes.Status409Conflict);
            HikvisionSpeakerDriver.Forget(service.ConnectionOf(speaker));
            db.Speakers.Remove(speaker);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "speakers", "speaker-deleted",
                targetType: "speaker", targetId: id.ToString(), targetName: speaker.Name,
                detail: $"Eliminó el parlante '{speaker.Name}' ({speaker.Host}:{speaker.Port}).");
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "speakers", cancellationToken: ct);
            return Results.Ok();
        });

        // Probar conexión (sin persistir). Con ?speakerId= reutiliza la clave guardada.
        app.MapPost("/api/speakers/probe", async (HttpContext ctx, SpeakerWriteDto request, VmsDbContext db, SpeakerDriverRegistry drivers,
            CredentialProtector protector, AuditService audit, int? speakerId, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            string? password = request.Password;
            if (string.IsNullOrEmpty(password) && speakerId is int existingId &&
                await db.Speakers.FindAsync([existingId], ct) is { } existing)
                password = protector.Unprotect(existing.PasswordCiphertext);
            if (string.IsNullOrEmpty(password)) return Error("La contraseña del parlante es obligatoria.");

            var conn = new SpeakerConnectionInfo(request.Host.Trim(), request.Port, request.UseHttps, request.Username, password);
            var (info, libraryCount, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
            await audit.LogAsync(ctx, "speakers", "speaker-probed", targetType: "speaker", targetName: request.Name.Trim(),
                detail: info is null
                    ? $"Probó la conexión con el parlante {request.Host.Trim()}:{request.Port}: {probeError}"
                    : $"Probó la conexión con el parlante {request.Host.Trim()}:{request.Port}: correcta ({info.Model}).",
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
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var speaker = await db.Speakers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (speaker is null) return Results.NotFound();
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
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var speaker = await db.Speakers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (speaker is null) return Results.NotFound();
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
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
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
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
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
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
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
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
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
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var speaker = await db.Speakers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (speaker is null) return Results.NotFound();
            try
            {
                var state = await service.DriverOf(speaker).GetPlaybackStateAsync(service.ConnectionOf(speaker), ct);
                return Results.Ok(new { playing = state.IsPlaying, current = state.CurrentName, busyWith = service.BusyOf(id) });
            }
            catch (DriverException ex) { return Error(ex.Message, StatusCodes.Status502BadGateway); }
        });

        app.MapPut("/api/speakers/{id:int}/volume", async (HttpContext ctx, int id, SpeakerVolumeRequestDto request, VmsDbContext db,
            SpeakerService service, IHubContext<VmsHub> hub, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            if (request.Volume is < 0 or > 100) return Error("El volumen debe estar entre 0 y 100.");
            var speaker = await db.Speakers.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (speaker is null) return Results.NotFound();
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
                await hub.Clients.All.SendAsync(VmsHubContract.SpeakerStatusChanged, service.ToDto(speaker), ct);
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
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            if (request.SpeakerIds is not { Count: > 0 }) return Error("Elija al menos un parlante.");
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
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            if (request.SpeakerIds is not { Count: > 0 }) return Error("Elija al menos un parlante.");
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
        app.Map("/api/speakers/talk", async (HttpContext ctx, string? ids, SpeakerService service, AuditService audit, ILoggerFactory loggers) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            if (!ctx.WebSockets.IsWebSocketRequest) return Error("Este recurso solo acepta WebSocket.", StatusCodes.Status400BadRequest);
            var speakerIds = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, out int id) ? id : 0).Where(id => id > 0).Distinct().ToList();
            if (speakerIds.Count == 0) return Error("Indique los parlantes (?ids=1,2).", StatusCodes.Status400BadRequest);

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
                detail: $"Inició voz en vivo hacia {targets}." +
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
}
