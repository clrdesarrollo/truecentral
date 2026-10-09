using TrueCentralVms.Server.Services.Licensing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Core.Domain;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Mantenedor de decodificadores de muro. Igual que con los dispositivos, al
/// crear o editar con credenciales nuevas SIEMPRE se valida contra el equipo
/// real (login + lectura de capacidades): si falla, no se guarda nada y el
/// error del SDK llega al panel.
/// </summary>
public static class DecodersApi
{
    /// <summary>Última identificación de monitores por decodificador (la nueva posterga el apagado de la anterior).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> IdentifyGenerations = new();

    private static DecoderDto ToDto(Decoder d) => new(
        d.Id, d.Name, d.DriverKey, d.Host, d.Port, d.Username, d.Enabled, d.Model, d.Notes, d.CreatedAt);

    private static DecoderCapabilitiesDto ToDto(DecoderCapabilities caps) => new(
        caps.DecodeChannelStart, caps.DecodeChannelCount,
        caps.Displays
            .Select(o => new DisplayOutputDto(o.Type.ToString(), o.Index, o.ChannelNo, o.Label, o.WindowModes))
            .ToList(),
        caps.Model, caps.SerialNumber);

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    private static string? ValidateWrite(DecoderWriteDto request, DecoderDriverRegistry drivers)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
            return "El nombre es obligatorio (máximo 128 caracteres).";
        if (string.IsNullOrWhiteSpace(request.Host))
            return "La dirección (IP o hostname) es obligatoria.";
        if (request.Port is < 1 or > 65535)
            return "El puerto debe estar entre 1 y 65535.";
        if (string.IsNullOrWhiteSpace(request.Username))
            return "El usuario del decodificador es obligatorio.";
        if (!drivers.Factories.Any(f => string.Equals(f.DriverKey, request.DriverKey, StringComparison.OrdinalIgnoreCase)))
            return $"Driver de decodificación desconocido: '{request.DriverKey}'.";
        return null;
    }

    /// <summary>Conecta al equipo y lee sus capacidades; el error del driver se devuelve como mensaje.</summary>
    private static async Task<(DecoderCapabilities? Caps, string? Error)> ProbeAsync(
        DecoderDriverRegistry drivers, string driverKey, DecoderConnectionInfo conn, CancellationToken ct)
    {
        await using var driver = drivers.Create(driverKey);
        try
        {
            await driver.ConnectAsync(conn, ct);
            return (await driver.GetCapabilitiesAsync(ct), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public static void MapDecodersApi(this IEndpointRouteBuilder app)
    {
        // Drivers de decodificación disponibles (combo del panel).
        app.MapGet("/api/decoders/drivers", (HttpContext ctx, DecoderDriverRegistry drivers) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            return Results.Ok(drivers.Factories
                .Select(f => new DecoderDriverDto(f.DriverKey, f.DisplayName))
                .OrderBy(d => d.DisplayName)
                .ToList());
        });

        app.MapGet("/api/decoders", async (HttpContext ctx, VmsDbContext db) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var decoders = await db.Decoders.OrderBy(d => d.Name).ToListAsync();
            return Results.Ok(decoders.Select(ToDto));
        });

        app.MapGet("/api/decoders/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var decoder = await db.Decoders.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
            return decoder is null ? Results.NotFound() : Results.Ok(ToDto(decoder));
        });

        // Estado en vivo para la página del decodificador y el editor de muros:
        // salidas con resolución y monitor conectado, entradas locales, canales
        // de decodificación y qué monitor de qué muro alimenta cada salida. Lo
        // de los muros sale de la base, así que se informa aunque el equipo no
        // responda.
        app.MapGet("/api/decoders/{id:int}/overview", async (HttpContext ctx, int id, VmsDbContext db,
            DecoderSessionManager sessions, ILogger<Program> logger, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAny(ctx, out _, Permissions.DevicesManage, Permissions.WallOperate) is { } failure) return failure;
            var decoder = await db.Decoders.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (decoder is null) return Results.NotFound();

            var walls = await db.Walls.AsNoTracking()
                .Include(w => w.Screens).ThenInclude(s => s.Windows)
                .Include(w => w.Floating)
                .Where(w => w.DecoderId == id)
                .OrderBy(w => w.Name)
                .AsSplitQuery()
                .ToListAsync(ct);
            var uses = walls
                .SelectMany(w => w.Screens.Select(s => (s.DisplayChannel, s.Label, Use: new OutputWallUseDto(w.Id, w.Name, s.Row, s.Col))))
                .GroupBy(x => x.DisplayChannel)
                .ToDictionary(g => g.Key, g => g.First());
            var usage = walls
                .Select(w => new DecoderWallUsageDto(w.Id, w.Name, w.Rows, w.Columns,
                    w.Screens.Sum(s => s.Windows.Count), w.Floating.Count))
                .ToList();

            DecoderCapabilities? caps = null;
            IReadOnlyList<DisplayOutputStatus> status = [];
            IReadOnlyList<LocalInputInfo>? inputs = null;
            bool canIdentify = false;
            string? error = null;
            try
            {
                (caps, status, inputs, canIdentify) = await sessions.WithDriverAsync(decoder, async d =>
                {
                    var c = await d.GetCapabilitiesAsync(ct);
                    // El estado de salidas y entradas es un extra: si el equipo
                    // no lo soporta, la página igual muestra sus salidas.
                    IReadOnlyList<DisplayOutputStatus> s = [];
                    try { s = await d.GetOutputStatusAsync(ct); }
                    catch (Exception ex) { logger.LogDebug(ex, "Estado de salidas del decodificador {Id} no disponible", id); }
                    IReadOnlyList<LocalInputInfo>? i = null;
                    try { i = await d.GetLocalInputsAsync(ct); }
                    catch (Exception ex) { logger.LogDebug(ex, "Entradas locales del decodificador {Id} no disponibles", id); }
                    return (c, s, i, d.CanIdentifyOutputs);
                }, ct);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            List<DecoderOutputDto> outputs;
            if (caps is not null)
            {
                outputs = caps.Displays.Select(o =>
                {
                    var live = status.FirstOrDefault(x => x.ChannelNo == o.ChannelNo);
                    uses.TryGetValue(o.ChannelNo, out var use);
                    return new DecoderOutputDto(o.Type.ToString(), o.Index, o.ChannelNo, o.Label, o.WindowModes,
                        live?.Resolution, live?.Connected, use.Use);
                }).ToList();
                string? model = caps.Model ?? caps.SerialNumber;
                if (!string.IsNullOrWhiteSpace(model) && decoder.Model != model)
                {
                    decoder.Model = model;
                    await db.SaveChangesAsync(ct);
                }
            }
            else
            {
                // Sin conexión: las salidas que la base sabe que se usan ("HDMI 3").
                outputs = uses.Values.OrderBy(x => x.DisplayChannel).Select(x =>
                {
                    string[] parts = x.Label.Split(' ', 2);
                    string type = parts[0].Length > 0 ? char.ToUpperInvariant(parts[0][0]) + parts[0][1..].ToLowerInvariant() : "Other";
                    int index = parts.Length > 1 && int.TryParse(parts[1], out int n) ? n : 0;
                    return new DecoderOutputDto(type, index, x.DisplayChannel, x.Label, [], null, null, x.Use);
                }).ToList();
            }

            return Results.Ok(new DecoderOverviewDto(
                ToDto(decoder), caps is not null, error,
                caps?.Model ?? decoder.Model, caps?.SerialNumber,
                caps?.DecodeChannelStart, caps?.DecodeChannelCount,
                usage.Sum(w => w.Windows + w.Floating),
                outputs,
                inputs?.Select(i => new LocalInputDto(i.Number, i.Name, i.Type, i.Signal, i.Resolution)).ToList(),
                caps is not null && canIdentify,
                usage));
        });

        // Identificar monitores: cada monitor muestra el número de la salida
        // que lo alimenta durante unos segundos (al armar el muro, para saber
        // qué monitor físico es cuál). Se apagan solos.
        app.MapPost("/api/decoders/{id:int}/identify", async (HttpContext ctx, int id, VmsDbContext db,
            DecoderSessionManager sessions, AuditService audit, ILogger<Program> logger, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.DevicesManage, out _) is { } failure) return failure;
            var decoder = await db.Decoders.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
            if (decoder is null) return Results.NotFound();

            const int seconds = 15;
            try
            {
                await sessions.WithDriverAsync(decoder, async d =>
                {
                    await d.GetCapabilitiesAsync(ct);
                    await d.ShowOutputNumbersAsync(true, ct);
                }, ct);
            }
            catch (Exception ex)
            {
                await audit.LogAsync(ctx, "wall", "decoder-identified",
                    targetType: "decoder", targetId: id.ToString(), targetName: decoder.Name,
                    detail: $"Intentó mostrar los números de salida en los monitores de '{decoder.Name}': {ex.Message}", success: false);
                return Error(ex is NotSupportedException ? ex.Message : $"El decodificador rechazó la orden: {ex.Message}");
            }

            // Una identificación nueva posterga el apagado de la anterior.
            int generation = IdentifyGenerations.AddOrUpdate(id, 1, (_, g) => g + 1);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                if (IdentifyGenerations.TryGetValue(id, out int current) && current != generation) return;
                try { await sessions.WithDriverAsync(decoder, d => d.ShowOutputNumbersAsync(false)); }
                catch (Exception ex) { logger.LogWarning(ex, "No se pudieron ocultar los números de salida del decodificador {Id}", id); }
            });

            await audit.LogAsync(ctx, "wall", "decoder-identified",
                targetType: "decoder", targetId: id.ToString(), targetName: decoder.Name,
                detail: $"Mostró durante {seconds} s el número de salida en los monitores de '{decoder.Name}'.");
            return Results.Ok(new { seconds });
        });

        app.MapPost("/api/decoders", async (HttpContext ctx, DecoderWriteDto request, VmsDbContext db, LicenseService license,
            DecoderDriverRegistry drivers, CredentialProtector protector, IHubContext<VmsHub> hub,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.DevicesManage, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            if (string.IsNullOrEmpty(request.Password))
                return Error("La contraseña del decodificador es obligatoria.");
            if (license.Deny(LicenseFeatures.ModuleVideowall, request.Enabled ? LicenseFeatures.VideowallDecoders : null,
                    await db.Decoders.CountAsync(d => d.Enabled, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "decoder", request.Name?.Trim());
            if (await db.Decoders.AnyAsync(d => d.Host == request.Host && d.Port == request.Port, ct))
                return Error("Ya existe un decodificador con esa dirección y puerto.", StatusCodes.Status409Conflict);

            var conn = new DecoderConnectionInfo(request.Host.Trim(), request.Port, request.Username, request.Password);
            var (caps, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
            if (caps is null) return Error(probeError!);

            var decoder = new Decoder
            {
                Name = request.Name.Trim(),
                DriverKey = request.DriverKey,
                Host = request.Host.Trim(),
                Port = request.Port,
                Username = request.Username,
                PasswordCiphertext = protector.Protect(request.Password),
                Enabled = request.Enabled,
                Notes = request.Notes,
                Model = caps.Model ?? caps.SerialNumber,
            };
            db.Decoders.Add(decoder);
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "wall", "decoder-created",
                targetType: "decoder", targetId: decoder.Id.ToString(), targetName: decoder.Name,
                detail: $"Agregó el decodificador '{decoder.Name}' ({decoder.Host}:{decoder.Port}, modelo {decoder.Model ?? "—"}).");
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "decoders", cancellationToken: ct);
            return Results.Ok(new { decoder = ToDto(decoder), capabilities = ToDto(caps) });
        });

        app.MapPut("/api/decoders/{id:int}", async (HttpContext ctx, int id, DecoderWriteDto request, VmsDbContext db, LicenseService license,
            DecoderDriverRegistry drivers, CredentialProtector protector, DecoderSessionManager sessions,
            IHubContext<VmsHub> hub, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.DevicesManage, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);

            var decoder = await db.Decoders.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (decoder is null) return Results.NotFound();
            if (await db.Decoders.AnyAsync(d => d.Id != id && d.Host == request.Host && d.Port == request.Port, ct))
                return Error("Ya existe otro decodificador con esa dirección y puerto.", StatusCodes.Status409Conflict);

            string password = string.IsNullOrEmpty(request.Password)
                ? protector.Unprotect(decoder.PasswordCiphertext)
                : request.Password;

            bool connectionChanged =
                decoder.Host != request.Host.Trim() ||
                decoder.Port != request.Port ||
                decoder.Username != request.Username ||
                decoder.DriverKey != request.DriverKey ||
                !string.IsNullOrEmpty(request.Password);
            if (connectionChanged)
            {
                // La sesión viva apunta a los datos antiguos: cerrarla antes de
                // validar, o el driver reutiliza el login anterior.
                await sessions.InvalidateAsync(id);
                var conn = new DecoderConnectionInfo(request.Host.Trim(), request.Port, request.Username, password);
                var (caps, probeError) = await ProbeAsync(drivers, request.DriverKey, conn, ct);
                if (caps is null) return Error(probeError!);
                decoder.Model = caps.Model ?? caps.SerialNumber;
            }

            decoder.Name = request.Name.Trim();
            decoder.DriverKey = request.DriverKey;
            decoder.Host = request.Host.Trim();
            decoder.Port = request.Port;
            decoder.Username = request.Username;
            decoder.PasswordCiphertext = protector.Protect(password);
            if (!decoder.Enabled && request.Enabled
                && license.Deny(LicenseFeatures.ModuleVideowall, LicenseFeatures.VideowallDecoders,
                    await db.Decoders.CountAsync(d => d.Enabled && d.Id != id, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "decoder", decoder.Name);
            decoder.Enabled = request.Enabled;
            decoder.Notes = request.Notes;
            decoder.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "wall", "decoder-updated",
                targetType: "decoder", targetId: decoder.Id.ToString(), targetName: decoder.Name,
                detail: $"Modificó el decodificador '{decoder.Name}' ({decoder.Host}:{decoder.Port}" +
                        (connectionChanged ? ", con cambios de conexión)." : ")."));
            await sessions.InvalidateAsync(id);
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "decoders", cancellationToken: ct);
            return Results.Ok(ToDto(decoder));
        });

        app.MapDelete("/api/decoders/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            DecoderSessionManager sessions, IHubContext<VmsHub> hub, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.DevicesManage, out _) is { } failure) return failure;
            var decoder = await db.Decoders.FindAsync([id], ct);
            if (decoder is null) return Results.NotFound();
            if (await db.Walls.AnyAsync(w => w.DecoderId == id, ct))
                return Error("Hay muros de video usando este decodificador; elimínelos primero.");

            db.Decoders.Remove(decoder);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "wall", "decoder-deleted",
                targetType: "decoder", targetId: id.ToString(), targetName: decoder.Name,
                detail: $"Eliminó el decodificador '{decoder.Name}' ({decoder.Host}:{decoder.Port}).");
            await sessions.InvalidateAsync(id);
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "decoders", cancellationToken: ct);
            return Results.Ok();
        });

        // Prueba de conexión: devuelve las salidas y canales reales del equipo
        // (el asistente de muros los usa para armar la grilla).
        app.MapPost("/api/decoders/{id:int}/test", async (HttpContext ctx, int id, VmsDbContext db,
            DecoderSessionManager sessions, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAny(ctx, out _, Permissions.DevicesManage, Permissions.WallOperate) is { } failure) return failure;
            var decoder = await db.Decoders.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (decoder is null) return Results.NotFound();

            try
            {
                var caps = await sessions.WithDriverAsync(decoder, d => d.GetCapabilitiesAsync(ct), ct);
                string? model = caps.Model ?? caps.SerialNumber;
                if (!string.IsNullOrWhiteSpace(model) && decoder.Model != model)
                {
                    decoder.Model = model;
                    await db.SaveChangesAsync(ct);
                }
                await audit.LogAsync(ctx, "wall", "decoder-tested",
                    targetType: "decoder", targetId: id.ToString(), targetName: decoder.Name,
                    detail: $"Probó el decodificador '{decoder.Name}': conexión correcta.");
                return Results.Ok(new DecoderProbeResultDto(true, null, ToDto(caps)));
            }
            catch (Exception ex)
            {
                await audit.LogAsync(ctx, "wall", "decoder-tested",
                    targetType: "decoder", targetId: id.ToString(), targetName: decoder.Name,
                    detail: $"Probó el decodificador '{decoder.Name}': {ex.Message}", success: false);
                return Results.Ok(new DecoderProbeResultDto(false, ex.Message, null));
            }
        });

        // Diagnóstico: vuelca el estado real del equipo (muro, salidas,
        // ventanas, estado de decodificación) como texto plano.
        app.MapGet("/api/decoders/{id:int}/diagnostics", async (HttpContext ctx, int id, VmsDbContext db,
            DecoderSessionManager sessions, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAny(ctx, out _, Permissions.DevicesManage, Permissions.WallOperate) is { } failure) return failure;
            var decoder = await db.Decoders.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (decoder is null) return Results.NotFound();
            try
            {
                string text = await sessions.WithDriverAsync(decoder, d =>
                    d is IDecoderDiagnostics diag
                        ? diag.GetDiagnosticsAsync(ct)
                        : Task.FromResult("El driver de este decodificador no expone diagnóstico."), ct);
                return Results.Text(text);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        });
    }
}
