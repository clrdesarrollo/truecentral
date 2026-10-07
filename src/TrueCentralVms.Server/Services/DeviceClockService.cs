using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Hora de los equipos: cada tanto le pregunta a cada equipo qué hora tiene y
/// en qué zona, lo compara con lo que dice la política
/// (<see cref="DeviceClockPolicy"/>) y, si se desfasó o está en otra zona, lo
/// pone en hora solo (si la política y el equipo lo permiten), dejándolo en la
/// bitácora.
///
/// No es cosmético. Un terminal de acceso evalúa los horarios de los niveles y
/// la vigencia de las personas con SU hora local: uno que quedó con la zona de
/// fábrica (China, UTC+8) abre y cierra once horas corrido respecto de lo que
/// se configuró en el VMS, y nadie lo nota hasta que alguien no puede entrar.
///
/// Lo leído vive en memoria: la página lo muestra y, tras un reinicio del
/// servidor, se vuelve a leer en la primera vuelta.
/// </summary>
public sealed class DeviceClockService(
    IServiceScopeFactory scopeFactory,
    AccessControlService access,
    AuditService audit,
    ILogger<DeviceClockService> logger) : BackgroundService
{
    public const string AccessKind = "access";

    /// <summary>Equipos que se consultan a la vez (los ISAPI son lentos y limitan sesiones).</summary>
    private const int Parallelism = 4;

    /// <summary>
    /// Tras una corrección automática no se vuelve a intentar antes de esto:
    /// si el equipo vuelve a desfasarse enseguida es que algo más le maneja la
    /// hora (su propio NTP, otro sistema) y pelearle cada pocos minutos solo
    /// llenaría la bitácora.
    /// </summary>
    private static readonly TimeSpan CorrectionCooldown = TimeSpan.FromHours(1);

    private sealed class State
    {
        public DeviceClock? Clock;
        public string? Error;
        public DateTime? CheckedAt;
        public DateTime? LastCorrectionAt;
        public bool? LastCorrectionOk;
        public string? LastCorrectionDetail;
        public DateTime? LastAutoAttemptAt;
        /// <summary>El firmware no aceptó el horario de verano: el desfase base es lo mejor que se le puede dejar.</summary>
        public bool DaylightUnsupported;
        /// <summary>Lo último que se dejó en la bitácora como problema, para no repetirlo en cada vuelta.</summary>
        public string? LoggedProblem;
        public readonly SemaphoreSlim Lock = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, State> _states = new();
    private readonly SemaphoreSlim _wake = new(0);

    private static string Key(string kind, int id) => $"{kind}:{id}";
    private State StateOf(string kind, int id) => _states.GetOrAdd(Key(kind, id), _ => new State());

    /// <summary>Revisar ya, sin esperar la vuelta (se agregó un equipo o se le encendió la corrección).</summary>
    public void RequestCheck()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    /// <summary>
    /// Cambió la política: lo que se corrigió con la anterior ya no cuenta para
    /// la espera entre correcciones (la nueva se aplica en esta misma vuelta) y
    /// los problemas se vuelven a avisar con la vara nueva.
    /// </summary>
    public void PolicyChanged()
    {
        foreach (var state in _states.Values)
        {
            state.LastAutoAttemptAt = null;
            state.LoggedProblem = null;
        }
        RequestCheck();
    }

    /// <summary>Olvida lo leído de un equipo (se reinició, se restableció o se borró).</summary>
    public void Forget(string kind, int id) => _states.TryRemove(Key(kind, id), out _);

    public static async Task<DeviceClockPolicy> LoadPolicyAsync(VmsDbContext db, CancellationToken ct) =>
        await db.DeviceClockPolicies.AsNoTracking().OrderBy(p => p.Id).FirstOrDefaultAsync(ct) ?? DeviceClockPolicy.Default();

    // ------------------------------------------------------------------
    // Vuelta periódica
    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Que el sondeo de estado de los equipos haga su primera vuelta antes:
        // así ya se sabe cuáles están en línea.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            DeviceClockPolicy policy = DeviceClockPolicy.Default();
            try
            {
                using (var scope = scopeFactory.CreateScope())
                    policy = await LoadPolicyAsync(scope.ServiceProvider.GetRequiredService<VmsDbContext>(), stoppingToken);
                await CheckAllAsync(policy, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "La revisión de la hora de los equipos falló."); }

            try { await _wake.WaitAsync(TimeSpan.FromMinutes(Math.Clamp(policy.CheckMinutes, 1, 1440)), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task CheckAllAsync(DeviceClockPolicy policy, CancellationToken ct)
    {
        List<AccessDevice> devices;
        using (var scope = scopeFactory.CreateScope())
            devices = await scope.ServiceProvider.GetRequiredService<VmsDbContext>().AccessDevices.AsNoTracking()
                .Where(d => d.Enabled && d.Status == AccessDeviceStatus.Online).ToListAsync(ct);

        using var limiter = new SemaphoreSlim(Parallelism);
        await Task.WhenAll(devices.Select(async device =>
        {
            await limiter.WaitAsync(ct);
            try { await CheckOneAsync(device, policy, allowCorrection: true, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "No se pudo revisar la hora del equipo {Device}.", device.Name);
            }
            finally { limiter.Release(); }
        }));
    }

    /// <summary>
    /// Lee el reloj de un equipo y, si corresponde y se permite, lo corrige.
    /// Una lectura pedida a mano no corrige: el operador mira primero y decide.
    /// </summary>
    public async Task CheckOneAsync(AccessDevice device, DeviceClockPolicy policy, bool allowCorrection, CancellationToken ct)
    {
        IAccessControlDriver driver;
        try { driver = access.DriverOf(device); }
        catch (DriverException) { return; }
        if (!driver.SupportsClock) return;

        var state = StateOf(AccessKind, device.Id);
        await state.Lock.WaitAsync(ct);
        try
        {
            var connection = access.ConnectionOf(device);
            if (!await ReadAsync(state, driver, connection, ct)) return;

            var zone = policy.Zone();
            var reasons = Problems(state, policy, zone, driver);
            if (reasons.Count == 0)
            {
                state.LoggedProblem = null;
                return;
            }

            bool correct = allowCorrection && policy.AutoCorrect && device.ClockAutoCorrect &&
                           (state.LastAutoAttemptAt is null || DateTime.UtcNow - state.LastAutoAttemptAt > CorrectionCooldown);
            string problem = string.Join("; ", reasons);
            if (!correct)
            {
                // Avisar una vez por problema, no en cada vuelta.
                if (allowCorrection && state.LoggedProblem != problem)
                {
                    state.LoggedProblem = problem;
                    await audit.LogSystemAsync("maintenance", "clock-drift",
                        targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                        detail: $"El equipo '{device.Name}' ({device.Host}) {problem}." +
                                (policy.AutoCorrect && !device.ClockAutoCorrect ? " No se corrige solo: tiene la corrección automática apagada." : ""),
                        success: false);
                }
                return;
            }

            state.LastAutoAttemptAt = DateTime.UtcNow;
            var setting = SettingFor(policy, zone, driver);
            try
            {
                string? note = await driver.SetClockAsync(connection, setting, ct);
                RememberNote(state, note);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                await ReadAsync(state, driver, connection, ct);
                string result = Summary(state, setting) + (note is null ? "" : $" Nota: {note}");
                Record(state, true, $"Corrección automática: {problem}. {result}");
                state.LoggedProblem = null;
                await audit.LogSystemAsync("maintenance", "clock-corrected",
                    targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                    detail: $"Puso en hora el equipo '{device.Name}' ({device.Host}) automáticamente: {problem}. {result}");
            }
            catch (DriverException ex)
            {
                Record(state, false, $"No se pudo poner en hora solo: {ex.Message}");
                await audit.LogSystemAsync("maintenance", "clock-corrected",
                    targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                    detail: $"No se pudo poner en hora el equipo '{device.Name}' ({device.Host}), que {problem}: {ex.Message}",
                    success: false);
            }
        }
        finally { state.Lock.Release(); }
    }

    /// <summary>
    /// Deja el reloj de un equipo como se pide (acción del operador) y lo
    /// vuelve a leer para mostrar cómo quedó. Devuelve la nota del driver.
    /// </summary>
    public async Task<string?> ApplyAsync(AccessDevice device, DeviceClockSetting setting, CancellationToken ct)
    {
        var driver = access.DriverOf(device);
        if (!driver.SupportsClock) throw new DriverException("Este equipo no acepta que el VMS le cambie la hora.");
        if (setting.Mode == DeviceTimeMode.Ntp && !driver.SupportsNtp)
            throw new DriverException("Este equipo no sabe usar un servidor NTP: póngalo en hora con la hora del servidor.");

        var state = StateOf(AccessKind, device.Id);
        await state.Lock.WaitAsync(ct);
        try
        {
            var connection = access.ConnectionOf(device);
            string? note;
            try { note = await driver.SetClockAsync(connection, setting, ct); }
            catch (DriverException ex)
            {
                Record(state, false, $"No se pudo poner en hora: {ex.Message}");
                throw;
            }
            RememberNote(state, note);
            // Una corrección hecha a mano también cuenta para la espera entre
            // correcciones automáticas: que la supervisión no la deshaga enseguida.
            state.LastAutoAttemptAt = DateTime.UtcNow;
            state.LoggedProblem = null;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            await ReadAsync(state, driver, connection, ct);
            Record(state, true, Summary(state, setting) + (note is null ? "" : $" Nota: {note}"));
            return note;
        }
        finally { state.Lock.Release(); }
    }

    // ------------------------------------------------------------------
    // Para la página
    // ------------------------------------------------------------------

    /// <summary>El equipo con lo último que se sabe de su reloj.</summary>
    public MaintainedDeviceDto ToDto(AccessDevice device, string? location, DeviceClockPolicy policy)
    {
        IAccessControlDriver? driver = null;
        try { driver = access.DriverOf(device); }
        catch (DriverException) { /* driver desconocido: sin funciones */ }

        var resets = new List<DeviceResetMode>();
        if (driver?.SupportedResets.HasFlag(DeviceResetModes.KeepNetwork) == true) resets.Add(DeviceResetMode.KeepNetwork);
        if (driver?.SupportedResets.HasFlag(DeviceResetModes.Full) == true) resets.Add(DeviceResetMode.Full);

        _states.TryGetValue(Key(AccessKind, device.Id), out var state);
        var zone = policy.Zone();

        string health;
        double? drift = null;
        string? zoneProblem = null;
        DeviceClockDto? clock = null;
        if (driver is null || !driver.SupportsClock) health = "unsupported";
        else if (!device.Enabled || device.Status != AccessDeviceStatus.Online) health = "offline";
        else if (state?.Clock is null) health = state?.Error is not null ? "error" : "unknown";
        else
        {
            var c = state.Clock;
            drift = Math.Round(DeviceTimeZones.Drift(c, zone).TotalSeconds, 1);
            var mismatch = DeviceTimeZones.ZoneMismatch(c, zone);
            if (mismatch is { OnlyDaylight: true } && state.DaylightUnsupported) mismatch = null;
            zoneProblem = mismatch?.Reason;
            health = state.Error is not null ? "error"
                : zoneProblem is not null ? "zone"
                : Math.Abs(drift.Value) > policy.ThresholdSeconds ? "drift"
                : "ok";
            var shownNow = c.LocalTime + (DateTime.UtcNow - c.ReadAtUtc);
            clock = new DeviceClockDto(
                shownNow.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                c.UtcOffset is { } o ? (int)o.TotalMinutes : null, c.TimeZone, c.ZoneDescription, c.DaylightSaving,
                c.Mode, c.NtpServer, c.NtpIntervalMinutes, c.ReadAtUtc);
        }

        return new MaintainedDeviceDto(
            AccessKind, device.Id, device.Name, device.Model, device.Host, location, device.Status.ToString(),
            driver?.SupportsClock ?? false, driver?.SupportsNtp ?? false, driver?.SupportsReboot ?? false, resets,
            device.ClockAutoCorrect, health, clock, drift, zoneProblem, state?.Error, state?.CheckedAt,
            state?.LastCorrectionAt, state?.LastCorrectionOk, state?.LastCorrectionDetail);
    }

    // ------------------------------------------------------------------

    private async Task<bool> ReadAsync(State state, IAccessControlDriver driver, AccessConnectionInfo connection,
        CancellationToken ct)
    {
        try
        {
            state.Clock = await driver.GetClockAsync(connection, ct);
            state.Error = null;
            return true;
        }
        catch (DriverException ex)
        {
            state.Error = ex.Message;
            return false;
        }
        finally
        {
            state.CheckedAt = DateTime.UtcNow;
        }
    }

    /// <summary>Lo que está mal en el reloj del equipo según la política, en palabras.</summary>
    private static List<string> Problems(State state, DeviceClockPolicy policy, TimeZoneInfo zone, IAccessControlDriver driver)
    {
        var reasons = new List<string>();
        var clock = state.Clock!;
        var drift = DeviceTimeZones.Drift(clock, zone);
        if (Math.Abs(drift.TotalSeconds) > policy.ThresholdSeconds)
            reasons.Add($"estaba {(drift > TimeSpan.Zero ? "adelantado" : "atrasado")} {Span(drift)}");
        if (DeviceTimeZones.ZoneMismatch(clock, zone) is { } mismatch && !(mismatch.OnlyDaylight && state.DaylightUnsupported))
            reasons.Add(mismatch.Reason);
        if (policy.Mode == DeviceTimeMode.Ntp && driver.SupportsNtp && !string.IsNullOrWhiteSpace(policy.NtpServer) &&
            (clock.Mode != DeviceTimeMode.Ntp ||
             !string.Equals(clock.NtpServer?.Trim(), policy.NtpServer.Trim(), StringComparison.OrdinalIgnoreCase)))
            reasons.Add(clock.Mode == DeviceTimeMode.Ntp
                ? $"tomaba la hora de {clock.NtpServer} y debería tomarla de {policy.NtpServer}"
                : $"tenía la hora fijada a mano y debería tomarla del servidor NTP {policy.NtpServer}");
        return reasons;
    }

    /// <summary>Cómo dejar un equipo según la política (NTP solo si el equipo lo sabe usar).</summary>
    public static DeviceClockSetting SettingFor(DeviceClockPolicy policy, TimeZoneInfo zone, IAccessControlDriver driver) =>
        policy.Mode == DeviceTimeMode.Ntp && driver.SupportsNtp && !string.IsNullOrWhiteSpace(policy.NtpServer)
            ? new DeviceClockSetting(zone, DeviceTimeMode.Ntp, null, policy.NtpServer, policy.NtpIntervalMinutes)
            : new DeviceClockSetting(zone, DeviceTimeMode.Manual);

    private static void RememberNote(State state, string? note)
    {
        if (note?.Contains("horario de verano", StringComparison.OrdinalIgnoreCase) == true) state.DaylightUnsupported = true;
    }

    private static void Record(State state, bool ok, string detail)
    {
        state.LastCorrectionAt = DateTime.UtcNow;
        state.LastCorrectionOk = ok;
        state.LastCorrectionDetail = detail.Length <= 400 ? detail : detail[..400] + "…";
    }

    /// <summary>Cómo quedó, en una frase.</summary>
    private static string Summary(State state, DeviceClockSetting setting)
    {
        string how = setting.Mode == DeviceTimeMode.Ntp
            ? $"tomando la hora de {setting.NtpServer} cada {setting.NtpIntervalMinutes} min"
            : "con la hora del servidor";
        if (state.Clock is not { } c) return $"Quedó en {DeviceTimeZones.Describe(setting.Zone)}, {how}.";
        string where = c.ZoneDescription ?? DeviceTimeZones.Describe(setting.Zone);
        return $"Quedó en {where}, {how}; su hora ahora es {c.LocalTime:dd-MM-yyyy HH:mm:ss}.";
    }

    /// <summary>"3 min 20 s", "11 h", "45 s".</summary>
    public static string Span(TimeSpan value)
    {
        var v = value.Duration();
        if (v.TotalSeconds < 60) return $"{v.TotalSeconds:0} s";
        if (v.TotalMinutes < 60) return v.Seconds == 0 ? $"{(int)v.TotalMinutes} min" : $"{(int)v.TotalMinutes} min {v.Seconds} s";
        if (v.TotalHours < 48) return v.Minutes == 0 ? $"{(int)v.TotalHours} h" : $"{(int)v.TotalHours} h {v.Minutes} min";
        return $"{(int)v.TotalDays} días";
    }
}
