using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Isup;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// API de la conexión ISUP: estado del servicio y de los equipos (Sistema → Conexión ISUP), reconectar o despertar un equipo, y la
/// descarga de corta vida con que los equipos ISUP reciben fotos (el túnel ISAPI no lleva multipart).
/// </summary>
public static class IsupApi
{
    public static void MapIsupApi(this WebApplication app)
    {
        // Lo descarga el EQUIPO (no un usuario): sin sesión. El nombre es aleatorio (128 bits) y vence a los pocos minutos.
        app.MapGet("/api/isup/files/{name}", (string name, IsupService isup) =>
            isup.TakeDownload(name) is { } file ? Results.File(file.Content, file.ContentType) : Results.NotFound());

        // Lo que hay que escribir en el equipo para que se conecte (dirección y puerto de la plataforma ISUP). Lo usan los
        // formularios de todos los módulos, así que basta con tener sesión.
        app.MapGet("/api/isup/connection", async (HttpContext ctx, IsupService isup, IsupHostManager host, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;
            var status = await isup.GetHostStatusAsync(ct);
            // Sin dirección fija, cada equipo apunta a la IP de este servidor que está en su red: con una sola red es esa; con varias se
            // listan todas (addresses) y el formulario pide la de la red del equipo.
            var configured = host.Options.PublicAddress is { Length: > 0 } c ? c : null;
            var locals = status?.LocalAddresses ?? [];
            bool perDevice = configured is null && status?.PerDeviceAddress == true;
            return Results.Ok(new
            {
                enabled = host.Enabled,
                running = status is not null,
                address = configured ?? (perDevice ? (locals.Count == 1 ? locals[0] : null) : status?.PublicAddress),
                addresses = perDevice ? locals : [],
                port = host.Options.RegistrationPorts.FirstOrDefault(),
            });
        });

        app.MapGet("/api/isup/status", async (HttpContext ctx, IsupService isup, IsupHostManager host, VmsDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemServices, out _) is { } failure) return failure;
            var status = await isup.GetHostStatusAsync(ct);
            var equipment = await EquipmentByIsupIdAsync(db, ct);
            var devices = new List<IsupDeviceDto>();
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in status?.Devices ?? [])
            {
                known.Add(d.DeviceId);
                devices.Add(new IsupDeviceDto(d.DeviceId, d.Online, d.Address, d.Protocol, d.OnlineSince, d.Asleep, d.Firmware, d.AlarmLinkUp,
                    equipment.GetValueOrDefault(d.DeviceId) ?? [], d.AnnouncedAddress));
            }
            // Equipos del VMS que el host aún no conoce (servicio detenido o recién dados de alta).
            foreach (var (id, refs) in equipment.Where(kv => !known.Contains(kv.Key)))
                devices.Add(new IsupDeviceDto(id, false, null, null, null, false, null, false, refs));
            if (audit.ShouldLog($"isup-status:{ApiSecurity.CurrentSession(ctx)?.UserId}", TimeSpan.FromMinutes(30)))
                await audit.LogAsync(ctx, "isup", "status-viewed", detail: "Consultó el estado de la conexión ISUP.");
            return Results.Ok(new IsupStatusDto(host.Enabled, host.IsRunning && status is not null,
                host.Enabled && !host.IsRunning ? host.LastError ?? "El servicio ISUP no está en ejecución." : null,
                status?.PublicAddress, status?.Listeners ?? [], devices.OrderBy(d => d.DeviceId, StringComparer.Ordinal).ToList(),
                status?.Rejections ?? [], host.AllowLegacy, status?.PerDeviceAddress ?? false, status?.LocalAddresses ?? []));
        });

        // Compatibilidad ISUP 2.0/4.0: esas versiones no prueban la clave al registrarse (basta el ID para suplantar al
        // equipo). Apagada por omisión; encenderla exige escribir HABILITAR, y el cambio relanza el servicio ISUP.
        app.MapPut("/api/isup/legacy", async (HttpContext ctx, IsupLegacyRequestDto request, IsupHostManager host, AuditService audit,
            CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemServices, out _) is { } failure) return failure;
            if (request.Enabled && !string.Equals(request.Confirm?.Trim(), "HABILITAR", StringComparison.Ordinal))
            {
                await audit.LogAsync(ctx, "isup", "legacy-enabled", detail:
                    "Intentó habilitar los equipos ISUP 2.0/4.0 sin confirmar el riesgo.", success: false);
                return Results.BadRequest(new { error = "Para habilitarlo escriba HABILITAR, confirmando que conoce el riesgo." });
            }
            if (request.Enabled == host.AllowLegacy) return Results.NoContent();
            await host.SetAllowLegacyAsync(request.Enabled, ct);
            await audit.LogAsync(ctx, "isup", request.Enabled ? "legacy-enabled" : "legacy-disabled", detail: request.Enabled
                ? "Habilitó los equipos ISUP 2.0/4.0, confirmando el riesgo: se registran sin probar su clave (con solo el ID " +
                  "se puede suplantar a un equipo). Se abrió el puerto de alarmas 2.0/4.0 y se relanzó el servicio ISUP."
                : "Deshabilitó los equipos ISUP 2.0/4.0: solo se aceptan equipos ISUP 5.0. Se relanzó el servicio ISUP.");
            return Results.NoContent();
        });

        // Reconectar: el servidor da de baja la conexión del equipo y este vuelve a registrarse solo (útil tras cambiar algo en él).
        app.MapPost("/api/isup/devices/{deviceId}/reconnect", async (string deviceId, HttpContext ctx, IsupService isup, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemServices, out _) is { } failure) return failure;
            bool ok = await isup.LogoutAsync(deviceId, ct);
            await audit.LogAsync(ctx, "isup", "device-reconnect-requested", "isup-device", deviceId, null,
                ok ? $"Pidió que el equipo con ID ISUP {deviceId} vuelva a registrarse." : $"Pidió reconectar el equipo con ID ISUP {deviceId}, que no estaba conectado.",
                success: ok);
            return ok ? Results.NoContent() : Results.NotFound(new { error = "El equipo no está conectado por ISUP." });
        });

        app.MapPost("/api/isup/devices/{deviceId}/wake", async (string deviceId, HttpContext ctx, IsupService isup, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemServices, out _) is { } failure) return failure;
            try
            {
                await isup.WakeUpAsync(deviceId, ct);
                await audit.LogAsync(ctx, "isup", "device-wake-requested", "isup-device", deviceId, null,
                    $"Despertó el equipo con ID ISUP {deviceId} (vuelve a registrarse en alrededor de un minuto).");
                return Results.NoContent();
            }
            catch (Core.Drivers.DriverException ex)
            {
                await audit.LogAsync(ctx, "isup", "device-wake-requested", "isup-device", deviceId, null, ex.Message, success: false);
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    /// <summary>ID ISUP → equipos del VMS que lo usan (un mismo equipo físico puede estar en dos módulos).</summary>
    public static async Task<Dictionary<string, List<IsupEquipmentRefDto>>> EquipmentByIsupIdAsync(VmsDbContext db, CancellationToken ct)
    {
        var map = new Dictionary<string, List<IsupEquipmentRefDto>>(StringComparer.Ordinal);
        void Add(string kind, IEnumerable<IIsupEquipment> items)
        {
            foreach (var e in items)
            {
                if (string.IsNullOrWhiteSpace(e.IsupDeviceId)) continue;
                var id = e.IsupDeviceId.Trim();
                if (!map.TryGetValue(id, out var list)) map[id] = list = [];
                list.Add(new IsupEquipmentRefDto(kind, e.Id, e.Name));
            }
        }
        Add("alarm", await db.AlarmPanels.AsNoTracking().Where(x => x.IsupDeviceId != null).ToListAsync(ct));
        Add("access", await db.AccessDevices.AsNoTracking().Where(x => x.IsupDeviceId != null).ToListAsync(ct));
        Add("video", await db.Devices.AsNoTracking().Where(x => x.IsupDeviceId != null).ToListAsync(ct));
        Add("speaker", await db.Speakers.AsNoTracking().Where(x => x.IsupDeviceId != null).ToListAsync(ct));
        Add("intercom", await db.Intercoms.AsNoTracking().Where(x => x.IsupDeviceId != null).ToListAsync(ct));
        return map;
    }
}
