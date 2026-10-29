using System.Linq.Expressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services.Workflows;

namespace TrueCentralVms.Server.Services.Licensing;

/// <summary>
/// Hace cumplir los cupos de la licencia también DESPUÉS de habilitar: las API
/// solo los controlan al crear o encender un recurso, así que lo encendido por
/// otra vía (editando la base de datos, una licencia que se achicó o un módulo
/// que salió de ella) se recorta aquí, en cada ciclo de licencia. Lo recortado
/// queda marcado (<c>DisabledByLicense</c>) y vuelve a encenderse solo cuando
/// hay cupo otra vez, igual que los canales de video (que siguen su propio
/// camino en <see cref="LicenseService"/>).
///
/// Reglas: se apaga lo más NUEVO primero y se restaura lo más ANTIGUO primero;
/// solo con la licencia vigente (en modo restringido ya se bloquea todo lo
/// demás); nunca se apaga al superadministrador ni a un administrador. Cada
/// recorte o restauración avisa al servicio del recurso exactamente como lo
/// hace su API al cambiar el habilitado, y queda en la bitácora.
///
/// Fuera de esto: los muros de video (no se pueden apagar, solo existir) y las
/// pantallas de alarma (van por aprobación, no por habilitado).
/// </summary>
public sealed class LicenseQuotaReconciler(IServiceScopeFactory scopes, AuditService audit,
    IHubContext<VmsHub> hub, ILogger<LicenseQuotaReconciler> logger)
{
    /// <summary>Un tipo de recurso con cupo: cómo se cuenta, se apaga y se avisa.</summary>
    private sealed record Spec<T>(
        string Label,
        string? Module,
        string Quota,
        Func<VmsDbContext, IQueryable<T>> Query,
        Expression<Func<T, bool>> On,
        Expression<Func<T, bool>> Waiting,
        Expression<Func<T, bool>> Trimmable,
        Action<T, bool> Apply,
        Func<T, string> Describe,
        Func<IServiceProvider, List<T>, List<T>, CancellationToken, Task> After) where T : class;

    public async Task ReconcileAsync(LicenseSnapshot license, CancellationToken ct)
    {
        if (!license.Operational) return;
        var f = license.Features;
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<VmsDbContext>();

        await RunAsync(db, sp, f, new Spec<Device>(
            "fuentes ANPR", LicenseFeatures.ModuleAnpr, LicenseFeatures.AnprChannels,
            d => d.Devices, x => x.AnprEnabled, x => x.AnprDisabledByLicense && !x.AnprEnabled, x => true,
            (x, on) => { x.AnprEnabled = on; x.AnprDisabledByLicense = !on; }, x => x.Name,
            async (s, off, _, _) =>
            {
                var anpr = s.GetService<AnprService>();
                foreach (var d in off) if (anpr is not null) await anpr.DetachAsync(d.Id);
                anpr?.RequestReconcile();
                await Changed("anpr-sources");
            }), ct);

        await RunAsync(db, sp, f, new Spec<AlarmPanel>(
            "paneles de alarma", LicenseFeatures.ModuleAlarms, LicenseFeatures.AlarmPanels,
            d => d.AlarmPanels, x => x.Enabled, x => x.DisabledByLicense && !x.Enabled, x => true,
            (x, on) => { x.Enabled = on; x.DisabledByLicense = !on; }, x => x.Name,
            async (s, off, _, _) =>
            {
                var service = s.GetService<AlarmPanelService>();
                foreach (var p in off) if (service is not null) await service.DetachAsync(p.Id);
                service?.RequestReconcile();
                await Changed("alarm-panels");
            }), ct);

        await RunAsync(db, sp, f, new Spec<CercoPanel>(
            "paneles de cerco", LicenseFeatures.ModuleFence, LicenseFeatures.FencePanels,
            d => d.CercoPanels.Include(p => p.Zones), x => x.Enabled, x => x.DisabledByLicense && !x.Enabled, x => true,
            (x, on) => { x.Enabled = on; x.DisabledByLicense = !on; }, x => x.Name,
            async (s, off, on, token) =>
            {
                // El receptor solo mira el habilitado al conectar: se corta el
                // socket y la reconexión ya no se acepta.
                var conns = s.GetRequiredService<CercoConnectionManager>();
                foreach (var p in off) conns.Disconnect(p.DeviceId);
                var scoped = s.GetRequiredService<ScopedHub>();
                foreach (var p in off.Concat(on))
                    await scoped.SendAsync(VmsHubContract.CercoPanelStateChanged,
                        CercoMapper.ToDto(p, conns.IsConnected(p.DeviceId)), u => u.CanViewCerco(p.Id), token);
            }), ct);

        await RunAccessAsync(db, sp, f, ct);

        await RunAsync(db, sp, f, new Spec<Decoder>(
            "decodificadores", LicenseFeatures.ModuleVideowall, LicenseFeatures.VideowallDecoders,
            d => d.Decoders, x => x.Enabled, x => x.DisabledByLicense && !x.Enabled, x => true,
            (x, on) => { x.Enabled = on; x.DisabledByLicense = !on; }, x => x.Name,
            async (s, off, _, _) =>
            {
                var sessions = s.GetService<DecoderSessionManager>();
                foreach (var d in off) if (sessions is not null) await sessions.InvalidateAsync(d.Id);
                await Changed("decoders");
            }), ct);

        await RunAsync(db, sp, f, new Spec<Speaker>(
            "parlantes IP", LicenseFeatures.ModuleSpeakers, LicenseFeatures.SpeakerChannels,
            // El altavoz de un terminal de acceso viene con el terminal: no cuenta ni se apaga por cupo.
            d => d.Speakers.Where(s => s.AccessDeviceId == null), x => x.Enabled, x => x.DisabledByLicense && !x.Enabled, x => true,
            (x, on) => { x.Enabled = on; x.DisabledByLicense = !on; }, x => x.Name,
            async (s, _, _, _) =>
            {
                s.GetService<SpeakerService>()?.RequestReconcile();
                await Changed("speakers");
            }), ct);

        await RunAsync(db, sp, f, new Spec<Intercom>(
            "frentes de citofonía", LicenseFeatures.ModuleIntercom, LicenseFeatures.IntercomDevices,
            d => d.Intercoms, x => x.Enabled, x => x.DisabledByLicense && !x.Enabled, x => true,
            (x, on) => { x.Enabled = on; x.DisabledByLicense = !on; }, x => x.Name,
            async (s, _, _, token) =>
            {
                // El video propio del frente es una ruta de MediaMTX.
                if (s.GetService<MediaMtxManager>() is { } mtx) await mtx.RefreshPathsAsync(token);
                s.GetService<IntercomService>()?.RequestReconcile();
                await Changed("intercoms");
            }), ct);

        await RunAsync(db, sp, f, new Spec<IoModule>(
            "módulos de E/S", LicenseFeatures.ModuleIo, LicenseFeatures.IoModules,
            // Las entradas y salidas de un terminal de acceso vienen con el terminal.
            d => d.IoModules.Where(m => m.AccessDeviceId == null), x => x.Enabled, x => x.DisabledByLicense && !x.Enabled, x => true,
            (x, on) => { x.Enabled = on; x.DisabledByLicense = !on; }, x => x.Name,
            async (s, off, _, _) =>
            {
                var service = s.GetService<IoModuleService>();
                foreach (var m in off) if (service is not null) await service.DetachAsync(m.Id);
                service?.RequestReconcile();
                await Changed("io-modules");
            }), ct);

        // Rondas: solo cuentan los bastones aprobados (uno pendiente no ocupa cupo).
        await RunAsync(db, sp, f, new Spec<PatrolReader>(
            "bastones de ronda", LicenseFeatures.ModulePatrol, LicenseFeatures.PatrolReaders,
            d => d.PatrolReaders.Where(r => r.Status == PatrolReaderStatus.Approved),
            x => x.Enabled, x => x.DisabledByLicense && !x.Enabled, x => true,
            (x, on) => { x.Enabled = on; x.DisabledByLicense = !on; x.UpdatedAt = DateTime.UtcNow; }, x => x.Name,
            async (_, _, _, _) => await Changed("patrol")), ct);

        await RunAsync(db, sp, f, new Spec<PatrolCheckpoint>(
            "puntos de control de ronda", LicenseFeatures.ModulePatrol, LicenseFeatures.PatrolCheckpoints,
            d => d.PatrolCheckpoints, x => x.Enabled, x => x.DisabledByLicense && !x.Enabled, x => true,
            (x, on) => { x.Enabled = on; x.DisabledByLicense = !on; x.UpdatedAt = DateTime.UtcNow; }, x => x.Name,
            async (_, _, _, _) => await Changed("patrol")), ct);

        // Un vehículo apagado por licencia conserva sus cámaras configuradas: las concesiones de vivo lo rechazan por el vehículo.
        await RunAsync(db, sp, f, new Spec<Vehicle>(
            "vehículos de flota", LicenseFeatures.ModuleFleet, LicenseFeatures.FleetVehicles,
            d => d.Vehicles, x => x.Enabled, x => x.DisabledByLicense && !x.Enabled, x => true,
            (x, on) => { x.Enabled = on; x.DisabledByLicense = !on; x.UpdatedAt = DateTime.UtcNow; }, x => x.Code,
            async (_, _, _, _) => await Changed("fleet")), ct);

        await RunAsync(db, sp, f, new Spec<Workflow>(
            "automatizaciones", LicenseFeatures.ModuleAutomation, LicenseFeatures.AutomationRules,
            d => d.Workflows, x => x.Enabled, x => x.DisabledByLicense && !x.Enabled, x => true,
            (x, on) => { x.Enabled = on; x.DisabledByLicense = !on; }, x => x.Name,
            async (s, _, _, _) =>
            {
                // El motor guarda las reglas habilitadas en memoria.
                s.GetService<WorkflowEngine>()?.Invalidate();
                await Changed("workflows");
            }), ct);

        await RunAsync(db, sp, f, new Spec<User>(
            "usuarios", null, LicenseFeatures.MaxUsers,
            d => d.Users, x => x.Enabled, x => x.DisabledByLicense && !x.Enabled,
            // Nunca el superadministrador ni un administrador: el sistema no puede quedar sin quien lo administre.
            x => !x.IsSuperAdmin && x.Role != Roles.Admin,
            (x, on) => { x.Enabled = on; x.DisabledByLicense = !on; }, x => x.Username,
            async (s, off, _, _) =>
            {
                var tokens = s.GetRequiredService<TokenService>();
                foreach (var u in off) tokens.RevokeUser(u.Id);
                s.GetService<UserScopeService>()?.Invalidate();
                await Changed("users");
            }), ct);
    }

    /// <summary>Recorta o restaura un tipo de recurso según su cupo.</summary>
    private async Task RunAsync<T>(VmsDbContext db, IServiceProvider sp, LicenseFeatureSet f, Spec<T> spec,
        CancellationToken ct) where T : class
    {
        try
        {
            int allowed = spec.Module is null || f.IsEnabled(spec.Module) ? f.Quota(spec.Quota) : 0;
            int inUse = await spec.Query(db).CountAsync(spec.On, ct);
            List<T> off = [], on = [];
            if (inUse > allowed)
            {
                off = await spec.Query(db).Where(spec.On).Where(spec.Trimmable)
                    .OrderByDescending(e => EF.Property<int>(e, "Id")).Take(inUse - allowed).ToListAsync(ct);
                foreach (var e in off) spec.Apply(e, false);
            }
            else if (inUse < allowed)
            {
                on = await spec.Query(db).Where(spec.Waiting)
                    .OrderBy(e => EF.Property<int>(e, "Id")).Take(allowed - inUse).ToListAsync(ct);
                foreach (var e in on) spec.Apply(e, true);
            }
            if (off.Count == 0 && on.Count == 0) return;
            await db.SaveChangesAsync(ct);
            await ReportAsync(spec.Label, spec.Module, allowed, off.Select(spec.Describe).ToList(), on.Select(spec.Describe).ToList());
            await spec.After(sp, off, on, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Licencia: no se pudo revisar el cupo de {Label}.", spec.Label);
        }
    }

    /// <summary>
    /// Control de acceso: el cupo es de PUERTAS, pero lo que se enciende y apaga
    /// es el controlador entero (sus puertas cuentan solo si está habilitado).
    /// </summary>
    private async Task RunAccessAsync(VmsDbContext db, IServiceProvider sp, LicenseFeatureSet f, CancellationToken ct)
    {
        const string label = "controladores de acceso";
        try
        {
            int allowed = f.IsEnabled(LicenseFeatures.ModuleAccess) ? f.Quota(LicenseFeatures.AccessDoors) : 0;
            var devices = await db.AccessDevices
                .Select(d => new { Device = d, Doors = d.Doors.Count(door => door.Enabled) })
                .ToListAsync(ct);
            int inUse = devices.Where(x => x.Device.Enabled).Sum(x => x.Doors);
            List<AccessDevice> off = [], on = [];
            if (inUse > allowed)
            {
                foreach (var x in devices.Where(x => x.Device.Enabled && x.Doors > 0).OrderByDescending(x => x.Device.Id))
                {
                    if (inUse <= allowed) break;
                    x.Device.Enabled = false;
                    x.Device.DisabledByLicense = true;
                    inUse -= x.Doors;
                    off.Add(x.Device);
                }
            }
            else
            {
                foreach (var x in devices.Where(x => x.Device.DisabledByLicense && !x.Device.Enabled).OrderBy(x => x.Device.Id))
                {
                    if (inUse + x.Doors > allowed) break;
                    x.Device.Enabled = true;
                    x.Device.DisabledByLicense = false;
                    inUse += x.Doors;
                    on.Add(x.Device);
                }
            }
            if (off.Count == 0 && on.Count == 0) return;
            await db.SaveChangesAsync(ct);
            await ReportAsync(label + " (cupo de puertas)", LicenseFeatures.ModuleAccess, allowed,
                off.Select(d => d.Name).ToList(), on.Select(d => d.Name).ToList());
            sp.GetService<AccessControlService>()?.RequestReconcile();
            // Sus partes (cámara, altavoz, entradas y salidas) se apagan y vuelven con él.
            foreach (var device in off.Concat(on))
                await AccessTerminalParts.FollowEnabledAsync(db, device, ct);
            await db.SaveChangesAsync(ct);
            if (sp.GetService<MediaMtxManager>() is { } mtx) await mtx.RefreshPathsAsync(ct);
            sp.GetService<SpeakerService>()?.RequestReconcile();
            sp.GetService<IoModuleService>()?.RequestReconcile();
            await Changed("access-devices");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Licencia: no se pudo revisar el cupo de {Label}.", label);
        }
    }

    private async Task ReportAsync(string label, string? module, int allowed, List<string> off, List<string> on)
    {
        string limit = module is not null && allowed == 0
            ? $"el módulo «{LicenseService.ModuleName(module)}» no está en la licencia"
            : $"cupo {allowed}";
        if (off.Count > 0)
        {
            string detail = $"Apagó {label} por sobrepasar la licencia ({limit}): {string.Join(", ", off)}. " +
                            "Volverán a encenderse solos cuando haya cupo.";
            logger.LogWarning("Licencia: {Detail}", detail);
            await audit.LogSystemAsync("license", "license-quota-trimmed", targetType: "license",
                detail: detail, data: new { label, allowed, items = off }, success: false);
        }
        if (on.Count > 0)
        {
            string detail = $"Volvió a encender {label} que la licencia había apagado, al haber cupo ({limit}): {string.Join(", ", on)}.";
            logger.LogInformation("Licencia: {Detail}", detail);
            await audit.LogSystemAsync("license", "license-quota-restored", targetType: "license",
                detail: detail, data: new { label, allowed, items = on });
        }
    }

    private Task Changed(string topic) => hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, topic);
}
