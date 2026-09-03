using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Mantenedor de decodificadores de muro. Igual que con los dispositivos, al
/// crear o editar con credenciales nuevas SIEMPRE se valida contra el equipo
/// real (login + lectura de capacidades): si falla, no se guarda nada y el
/// error del SDK llega al panel.
/// </summary>
public static class DecodersApi
{
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

        app.MapPost("/api/decoders", async (HttpContext ctx, DecoderWriteDto request, VmsDbContext db,
            DecoderDriverRegistry drivers, CredentialProtector protector, IHubContext<VmsHub> hub,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            if (ValidateWrite(request, drivers) is { } invalid) return Error(invalid);
            if (string.IsNullOrEmpty(request.Password))
                return Error("La contraseña del decodificador es obligatoria.");
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

        app.MapPut("/api/decoders/{id:int}", async (HttpContext ctx, int id, DecoderWriteDto request, VmsDbContext db,
            DecoderDriverRegistry drivers, CredentialProtector protector, DecoderSessionManager sessions,
            IHubContext<VmsHub> hub, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
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
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
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
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
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
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
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
