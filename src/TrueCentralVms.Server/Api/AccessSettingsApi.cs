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
/// Configuración PROPIA de un equipo de control de acceso (parámetros de sus
/// puertas y de sus lectores), para la página del equipo y para la pestaña
/// Parámetros de la ficha de cada puerta en Recursos. El VMS no guarda nada de
/// esto: cada lectura va al equipo y cada cambio se le escribe y se relee.
/// Solo administradores, igual que el resto del mantenedor.
/// </summary>
public static class AccessSettingsApi
{
    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    private static IAccessDeviceSettingsProvider? ProviderOf(IEnumerable<IAccessDeviceSettingsProvider> providers, AccessDevice device) =>
        providers.FirstOrDefault(p => p.DriverKey.Equals(device.DriverKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>La puerta a la que pertenece un bloque ("door:2", o el lector de entrada de la 2), o null si es del equipo entero.</summary>
    private static AccessDoor? DoorOfSection(IAccessDeviceSettingsProvider provider, IEnumerable<AccessDoor> doors, string section) =>
        doors.FirstOrDefault(d => provider.SectionsOfDoor(d.Number)
            .Any(s => s.Key.Equals(section, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Escribe un bloque en el equipo y lo deja en la bitácora. Si el bloque es
    /// de una puerta (ella o uno de sus lectores) la entrada va a nombre de la
    /// puerta, así sale también en el historial de su ficha.
    /// </summary>
    private static async Task<IResult> ApplyAsync(HttpContext ctx, VmsDbContext db, AccessDevice device,
        IAccessDeviceSettingsProvider provider, string section, AccessSettingsWriteDto request,
        AccessControlService service, AuditService audit, IHubContext<VmsHub> hub, CancellationToken ct,
        Func<AccessSettingsSectionDto, AccessSettingsSectionDto>? present = null)
    {
        // En la bitácora van las claves tocadas y sus valores, menos las
        // contraseñas del equipo (coacción, maestra), que no se anotan.
        string Described(KeyValuePair<string, string?> pair) =>
            pair.Key.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? $"{pair.Key} (cambiada)"
                : $"{pair.Key} = {pair.Value ?? "—"}";
        string changes = string.Join(", ", request.Values.Select(Described));
        var door = DoorOfSection(provider, device.Doors, section);
        var (targetType, targetId, targetName) = door is null
            ? ("access-device", device.Id.ToString(), device.Name)
            : ("access-door", door.Id.ToString(), door.Name);

        AccessSettingsSectionDto result;
        try
        {
            result = await provider.ApplyAsync(service.ConnectionOf(device), section, request.Values, ct);
        }
        catch (DriverException ex)
        {
            await audit.LogAsync(ctx, "access", "device-settings-updated",
                targetType: targetType, targetId: targetId, targetName: targetName,
                detail: $"El equipo '{device.Name}' no aceptó cambios en {section}" +
                        (door is null ? "" : $" (puerta '{door.Name}')") + $": {ex.Message} ({changes})",
                success: false);
            return Error(ex.Message, StatusCodes.Status502BadGateway);
        }

        // Si cambió el nombre de una puerta en el equipo, el VMS lo sigue
        // mientras nadie la haya renombrado en Recursos (mismo criterio que
        // la revalidación).
        bool renamed = false;
        if (result.Kind == "door" && result.Subtitle is { Length: > 0 } doorName &&
            device.Doors.FirstOrDefault(d => d.Number == result.Number) is { NameFromDevice: true } named && named.Name != doorName)
        {
            named.Name = doorName;
            named.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            renamed = true;
        }

        await audit.LogAsync(ctx, "access", "device-settings-updated",
            targetType: targetType, targetId: targetId, targetName: targetName,
            detail: $"Cambió la configuración del equipo '{device.Name}' ({device.Host}), {result.Title.ToLowerInvariant()}" +
                    (door is null || result.Kind == "door" ? "" : $" de la puerta '{door.Name}'") + $": {changes}.",
            data: new { section, accessDeviceId = device.Id, doorId = door?.Id,
                values = request.Values.Where(v => !v.Key.Contains("password", StringComparison.OrdinalIgnoreCase)) });
        if (renamed)
        {
            EquipmentLocation.Changed(ctx);
            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "access-devices", cancellationToken: ct);
        }
        return Results.Ok(present is null ? result : present(result));
    }

    /// <summary>El bloque con su papel en la puerta en el título ("Lector 1 · lector de entrada").</summary>
    private static AccessSettingsSectionDto WithRole(AccessSettingsSectionDto section, string? role) =>
        role is null ? section : section with { Title = $"{section.Title} · {role}" };

    /// <summary>
    /// La puerta para su pestaña Parámetros: tiene que existir y estar en el
    /// alcance de quien la pide (se valida el recurso concreto, no su equipo).
    /// </summary>
    private static async Task<(AccessDoor? Door, IResult? Failure)> DoorInScopeAsync(HttpContext ctx, VmsDbContext db,
        int doorId, CancellationToken ct)
    {
        if (ApiSecurity.Require(ctx, Permissions.AccessConfigure, out var session) is { } failure) return (null, failure);
        if (!(await ctx.ScopeAsync(session)).CanViewResource(ResourceKind.Door, doorId)) return (null, Results.NotFound());
        var door = await db.AccessDoors.Include(d => d.AccessDevice!).ThenInclude(a => a.Doors)
            .FirstOrDefaultAsync(d => d.Id == doorId, ct);
        return door?.AccessDevice is null ? (null, Results.NotFound()) : (door, null);
    }

    public static void MapAccessSettingsApi(this WebApplication app)
    {
        app.MapGet("/api/access/devices/{id:int}/settings", async (HttpContext ctx, int id, VmsDbContext db,
            IEnumerable<IAccessDeviceSettingsProvider> providers, AccessControlService service, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.AccessConfigure, out _) is { } failure) return failure;
            var device = await db.AccessDevices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
            if (device is null) return Results.NotFound();
            if (ProviderOf(providers, device) is not { } provider)
                return Error("Los equipos de esta marca todavía no se configuran desde el VMS: use la página web del equipo.",
                    StatusCodes.Status501NotImplemented);
            try
            {
                return Results.Ok(await provider.ReadAsync(service.ConnectionOf(device), ct));
            }
            catch (DriverException ex)
            {
                return Error(ex.Message, StatusCodes.Status502BadGateway);
            }
        });

        app.MapPut("/api/access/devices/{id:int}/settings/{section}", async (HttpContext ctx, int id, string section,
            AccessSettingsWriteDto request, VmsDbContext db, IEnumerable<IAccessDeviceSettingsProvider> providers,
            AccessControlService service, AuditService audit, IHubContext<VmsHub> hub, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.AccessConfigure, out _) is { } failure) return failure;
            if (request?.Values is null || request.Values.Count == 0) return Error("No hay cambios que guardar.");
            var device = await db.AccessDevices.Include(d => d.Doors).FirstOrDefaultAsync(d => d.Id == id, ct);
            if (device is null) return Results.NotFound();
            if (ProviderOf(providers, device) is not { } provider)
                return Error("Los equipos de esta marca todavía no se configuran desde el VMS.", StatusCodes.Status501NotImplemented);
            return await ApplyAsync(ctx, db, device, provider, section, request, service, audit, hub, ct);
        });

        // ---- Por puerta: pestaña Parámetros de su ficha en Recursos ----

        // La puerta y sus lectores, con el papel de cada uno en el título.
        app.MapGet("/api/access/doors/{doorId:int}/settings", async (HttpContext ctx, int doorId, VmsDbContext db,
            IEnumerable<IAccessDeviceSettingsProvider> providers, AccessControlService service, CancellationToken ct) =>
        {
            var (door, failure) = await DoorInScopeAsync(ctx, db, doorId, ct);
            if (failure is not null) return failure;
            var device = door!.AccessDevice!;
            if (ProviderOf(providers, device) is not { } provider)
                return Error("Los equipos de esta marca todavía no se configuran desde el VMS: use la página web del equipo.",
                    StatusCodes.Status501NotImplemented);
            AccessDeviceSettingsDto all;
            try { all = await provider.ReadAsync(service.ConnectionOf(device), ct); }
            catch (DriverException ex) { return Error(ex.Message, StatusCodes.Status502BadGateway); }

            var sections = new List<AccessSettingsSectionDto>();
            var mine = provider.SectionsOfDoor(door.Number);
            foreach (var wanted in mine)
                if (all.Sections.FirstOrDefault(s => s.Key.Equals(wanted.Key, StringComparison.OrdinalIgnoreCase)) is { } found)
                    sections.Add(WithRole(found, wanted.Role));
            // Los avisos de la lectura que hablan de OTRA puerta u otro lector no van en esta ficha.
            var own = mine.Select(s => s.Key.Replace("door:", "puerta ").Replace("reader:", "lector ") + ".").ToList();
            var notes = all.Notes.Where(n => !System.Text.RegularExpressions.Regex.IsMatch(n, @"(puerta|lector) \d+\.") ||
                                             own.Any(o => n.Contains(o, StringComparison.OrdinalIgnoreCase))).ToList();
            return Results.Ok(new AccessDoorSettingsDto(device.Id, device.Name, door.Number, sections, notes));
        });

        // Solo bloques de ESTA puerta: el resto del equipo se cambia en su página.
        app.MapPut("/api/access/doors/{doorId:int}/settings/{section}", async (HttpContext ctx, int doorId, string section,
            AccessSettingsWriteDto request, VmsDbContext db, IEnumerable<IAccessDeviceSettingsProvider> providers,
            AccessControlService service, AuditService audit, IHubContext<VmsHub> hub, CancellationToken ct) =>
        {
            var (door, failure) = await DoorInScopeAsync(ctx, db, doorId, ct);
            if (failure is not null) return failure;
            if (request?.Values is null || request.Values.Count == 0) return Error("No hay cambios que guardar.");
            var device = door!.AccessDevice!;
            if (ProviderOf(providers, device) is not { } provider)
                return Error("Los equipos de esta marca todavía no se configuran desde el VMS.", StatusCodes.Status501NotImplemented);
            var mine = provider.SectionsOfDoor(door.Number)
                .FirstOrDefault(s => s.Key.Equals(section, StringComparison.OrdinalIgnoreCase));
            if (mine is null) return Error("Ese bloque no es de esta puerta: se cambia en la página del equipo.");
            return await ApplyAsync(ctx, db, device, provider, section, request, service, audit, hub, ct,
                result => WithRole(result, mine.Role));
        });
    }
}
