using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Hubs;

namespace TrueCentralVms.Server.Services.Licensing;

/// <summary>
/// Licenciamiento del servidor VMS contra el servidor central de licencias
/// de CLRobotics.
///
/// Dos mecanismos de confianza, uno en línea y otro sin conexión:
/// <list type="bullet">
/// <item><b>Archivo firmado (.lic)</b>: la fuente de verdad local. Se obtiene
/// al activar (en línea con el código, o sin internet importando el .lic que
/// soporte emite a partir de la solicitud .req de este equipo) y se verifica
/// con la clave pública compilada. Sin conexión no hace falta nada más.</item>
/// <item><b>Heartbeat</b> (licencias en modo ONLINE): cada
/// <c>heartbeat_interval_days</c> se revalida contra el servidor, que devuelve
/// un .lic fresco (con las expansiones compradas después y la revocación si
/// la hubo). Si no se logra, el sistema sigue operativo durante
/// <c>grace_period_days</c> avisando, y luego pasa a modo restringido.</item>
/// </list>
/// Sin licencia rige un período de prueba incorporado (Licensing:TrialDays,
/// con los cupos de <see cref="LicensingConstants.TrialFeatures"/>).
///
/// Modo restringido: la API rechaza con 402 toda escritura que no sea
/// administrar la licencia (ver el middleware en Program.cs) y las altas que
/// dependen de cupos. Nada se borra: al regularizar la licencia todo vuelve.
/// </summary>
public sealed class LicenseService : BackgroundService
{
    private const string LicenseFileName = "license.lic";
    private const string StateFileName = "state.json";

    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IServiceScopeFactory _scopes;
    private readonly AuditService _audit;
    private readonly IHubContext<VmsHub> _hub;
    private readonly TokenService _tokens;
    private readonly ILogger<LicenseService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly int _trialDays;
    private readonly TimeSpan _checkInterval;
    private readonly string _publicKey;

    private PersistedState _state = new();
    private LicensePayload? _payload;
    private string? _licenseFileJson;
    private LicenseSnapshot _snapshot;
    private LicenseState? _lastAuditedState;

    public LicenseService(IConfiguration config, IHostEnvironment env, EmbeddedPostgres postgres,
        IServiceScopeFactory scopes, AuditService audit, IHubContext<VmsHub> hub, TokenService tokens,
        ILogger<LicenseService> logger)
    {
        _scopes = scopes;
        _audit = audit;
        _hub = hub;
        _tokens = tokens;
        _logger = logger;

        Directory = Path.Combine(Path.GetDirectoryName(postgres.DataDirectory) ?? postgres.DataDirectory, "license");
        System.IO.Directory.CreateDirectory(Directory);

        _trialDays = Math.Max(0, config.GetValue<int?>("Licensing:TrialDays") ?? 30);
        _checkInterval = TimeSpan.FromMinutes(Math.Clamp(config.GetValue<int?>("Licensing:HeartbeatCheckMinutes") ?? 60, 1, 24 * 60));
        Server = new LicenseServerClient(config["Licensing:ServerUrl"], config["Licensing:ApiKey"], logger);

        _publicKey = LicensingConstants.ProductPublicKey;
#if DEBUG
        // Solo en desarrollo: apuntar a un servidor de licencias propio con su clave.
        if (!string.IsNullOrWhiteSpace(config["Licensing:PublicKey"]))
            _publicKey = config["Licensing:PublicKey"]!.Trim();
#endif
        ServerVersion = typeof(LicenseService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        Load();
        _snapshot = Evaluate(DateTime.UtcNow);
    }

    public string Directory { get; }
    public string ServerVersion { get; }
    public LicenseServerClient Server { get; }
    public string HardwareIdentifier => HardwareId.Value;

    /// <summary>Estado actual (inmutable; se reemplaza en cada cambio).</summary>
    public LicenseSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>true si el sistema puede operar (licencia o prueba vigentes).</summary>
    public bool IsOperational => Snapshot.Operational;

    public LicenseFeatureSet Features => Snapshot.Features;

    // ------------------------------------------------------------------
    // Enforcement
    // ------------------------------------------------------------------

    /// <summary>
    /// Motivo por el que NO se puede agregar <paramref name="adding"/>
    /// elemento(s) al cupo <paramref name="quotaKey"/> del módulo
    /// <paramref name="moduleKey"/> con <paramref name="inUse"/> ya en uso;
    /// null si la licencia lo permite.
    /// </summary>
    public string? Deny(string? moduleKey, string? quotaKey, int inUse, int adding = 1)
    {
        var snapshot = Snapshot;
        if (!snapshot.Operational)
            return snapshot.Message ?? "El sistema está en modo restringido por licencia.";
        if (moduleKey is not null && !snapshot.Features.IsEnabled(moduleKey))
            return $"El módulo «{ModuleName(moduleKey)}» no está incluido en la licencia. Amplíe la licencia para habilitarlo.";
        if (quotaKey is not null)
        {
            int quota = snapshot.Features.Quota(quotaKey);
            if (inUse + adding > quota)
                return $"La licencia permite {quota} {UnitOf(quotaKey)} y ya hay {inUse} en uso. Amplíe la licencia para agregar más.";
        }
        return null;
    }

    /// <summary>Cupo licenciado de una característica entera (0 si no está).</summary>
    public int Quota(string quotaKey) => Snapshot.Features.Quota(quotaKey);

    public bool IsModuleEnabled(string moduleKey) => Snapshot.Operational && Snapshot.Features.IsEnabled(moduleKey);

    /// <summary>
    /// true si la solicitud debe rechazarse en modo restringido: escrituras
    /// de la API (POST/PUT/DELETE/PATCH) salvo las rutas que permiten
    /// entrar, administrar la licencia y controlar los servicios, más el
    /// callback loopback de MediaMTX (las lecturas siguen; sin concesiones
    /// nuevas de streaming no hay video igual).
    /// </summary>
    public static bool IsRestrictedRequest(HttpContext ctx)
    {
        var path = ctx.Request.Path;
        if (!path.StartsWithSegments("/api")) return false;
        if (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method) || HttpMethods.IsOptions(ctx.Request.Method))
            return false;
        return !(path.StartsWithSegments("/api/auth")
                 || path.StartsWithSegments("/api/setup")
                 || path.StartsWithSegments("/api/system/license")
                 || path.StartsWithSegments("/api/system/services")
                 || path.StartsWithSegments("/api/system/restart")
                 || path.StartsWithSegments("/api/streaming")
                 || path.StartsWithSegments("/api/audit/client-event"));
    }

    /// <summary>Respuesta HTTP 402 estándar para un rechazo por licencia.</summary>
    public static IResult DeniedResult(string message) =>
        Results.Json(new { error = message, licenseDenied = true }, statusCode: StatusCodes.Status402PaymentRequired);

    /// <summary>Rechazo por licencia auditado con el actor del request.</summary>
    public async Task<IResult> DenyAsync(HttpContext ctx, string message, string? targetType = null, string? targetName = null)
    {
        await _audit.LogAsync(ctx, "license", "license-denied",
            targetType: targetType, targetName: targetName, detail: message, success: false);
        return DeniedResult(message);
    }

    public static string ModuleName(string moduleKey) =>
        LicenseFeatures.Modules.FirstOrDefault(m => m.ModuleKey == moduleKey)?.Name ?? moduleKey;

    public static string UnitOf(string quotaKey) =>
        LicenseFeatures.Modules.Concat(LicenseFeatures.BaseQuotas).FirstOrDefault(m => m.QuotaKey == quotaKey)?.Unit ?? quotaKey;

    // ------------------------------------------------------------------
    // Estado para el panel / cliente
    // ------------------------------------------------------------------

    public async Task<LicenseStatusDto> GetStatusAsync(VmsDbContext db, CancellationToken ct = default)
    {
        var usage = new Dictionary<string, int>
        {
            [LicenseFeatures.VideoChannels] = await db.Channels.CountAsync(c => c.Enabled, ct),
            [LicenseFeatures.AnprChannels] = await db.Devices.CountAsync(d => d.AnprEnabled, ct),
            [LicenseFeatures.AlarmPanels] = await db.AlarmPanels.CountAsync(p => p.Enabled, ct),
            [LicenseFeatures.AccessDoors] = 0,
            [LicenseFeatures.Videowalls] = await db.Walls.CountAsync(ct),
            [LicenseFeatures.VideowallDecoders] = await db.Decoders.CountAsync(d => d.Enabled, ct),
            [LicenseFeatures.SpeakerChannels] = await db.Speakers.CountAsync(s => s.Enabled, ct),
            [LicenseFeatures.AutomationRules] = await db.Workflows.CountAsync(w => w.Enabled, ct),
            [LicenseFeatures.MaxUsers] = await db.Users.CountAsync(u => u.Enabled, ct),
            [LicenseFeatures.MaxClientSessions] = _tokens.CountDesktopSessions(),
        };
        return ToDto(Snapshot, usage);
    }

    private LicenseStatusDto ToDto(LicenseSnapshot s, IReadOnlyDictionary<string, int> usage)
    {
        var modules = LicenseFeatures.Modules.Concat(LicenseFeatures.BaseQuotas).Select(m => new LicenseModuleDto(
            m.Name, m.ModuleKey,
            Enabled: s.Operational && (m.ModuleKey is null || s.Features.IsEnabled(m.ModuleKey)),
            m.QuotaKey,
            Quota: m.QuotaKey is null ? null : s.Features.Quota(m.QuotaKey),
            InUse: m.QuotaKey is null ? null : usage.GetValueOrDefault(m.QuotaKey),
            m.Unit)).ToList();
        var addons = (s.Payload?.Addons ?? []).Select(a => new LicenseAddonDto(
            a.LicenseKey, a.Package, LicensePayload.ParseUtc(a.ExpiresAt), SummarizeAddon(a))).ToList();
        return new LicenseStatusDto(
            s.State, s.Operational, s.Mode, s.Message, s.Warning,
            s.Payload?.LicenseKey, s.Payload?.Customer?.Name, s.Payload?.Package,
            s.Payload?.IssuedAtUtc, s.ExpiresUtc, s.LastValidatedUtc, s.NextValidationUtc, s.GraceEndsUtc,
            s.DaysRemaining, HardwareIdentifier, Environment.MachineName, ServerVersion,
            Server.IsConfigured, Server.ServerUrl, modules, addons);
    }

    private static string SummarizeAddon(LicenseAddon addon)
    {
        var parts = new List<string>();
        foreach (var (key, element) in addon.Features)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int n) && n > 0)
                parts.Add($"+{n} {UnitOf(key)}");
            else if (element.ValueKind == JsonValueKind.True)
                parts.Add(ModuleName(key));
        }
        return parts.Count == 0 ? "—" : string.Join(", ", parts);
    }

    // ------------------------------------------------------------------
    // Operaciones (activar, importar, solicitar, revalidar, desactivar)
    // ------------------------------------------------------------------

    /// <summary>Activación en línea con el código de activación del certificado.</summary>
    public async Task<LicenseOperationResult> ActivateOnlineAsync(string activationCode, CancellationToken ct)
    {
        activationCode = NormalizeCode(activationCode);
        if (activationCode.Length == 0)
            return LicenseOperationResult.Fail("Ingrese el código de activación.");
        if (!Server.IsConfigured)
            return LicenseOperationResult.Fail("Este servidor no tiene configurado el servidor de licencias: use la activación sin conexión (solicitud .req + archivo .lic).");

        var result = await Server.ActivateAsync(activationCode, HardwareIdentifier, Environment.MachineName,
            OsInfo(), ServerVersion, ct);
        if (!result.Reachable)
            return LicenseOperationResult.Fail(result.Message);
        if (!result.Success || result.LicenseFileJson is null)
            return LicenseOperationResult.Fail(result.Message.Length > 0 ? result.Message : "El servidor de licencias rechazó la activación.");

        return await InstallAsync(result.LicenseFileJson, activationCode, "activada en línea", ct);
    }

    /// <summary>Importa un archivo .lic (activación sin conexión o .lic regenerado por soporte).</summary>
    public Task<LicenseOperationResult> ImportAsync(string licenseFileJson, CancellationToken ct) =>
        InstallAsync(licenseFileJson, null, "importada desde archivo", ct);

    private async Task<LicenseOperationResult> InstallAsync(string licenseFileJson, string? activationCode,
        string how, CancellationToken ct)
    {
        var verification = LicenseFileVerifier.Verify(licenseFileJson, _publicKey);
        if (!verification.Ok)
            return LicenseOperationResult.Fail(verification.Error!);
        var payload = verification.Payload!;
        if (payload.HardwareId is { Length: > 0 } hw && !string.Equals(hw, HardwareIdentifier, StringComparison.OrdinalIgnoreCase))
            return LicenseOperationResult.Fail(
                $"El archivo de licencia está vinculado a otro equipo ({hw}); este servidor es {HardwareIdentifier}. Genere la solicitud de activación desde este equipo.");
        if (payload.ExpiresAtUtc is { } expires && expires <= DateTime.UtcNow)
            return LicenseOperationResult.Fail($"La licencia {payload.LicenseKey} venció el {expires:yyyy-MM-dd}.");

        await _lock.WaitAsync(ct);
        try
        {
            _payload = payload;
            _licenseFileJson = licenseFileJson;
            _state.ActivationCode = activationCode ?? payload.LicenseKey;
            _state.LastValidatedUtc = payload.GeneratedAtUtc ?? DateTime.UtcNow;
            _state.LastHeartbeatError = null;
            _state.LastHeartbeatAttemptUtc = null;
            _state.ServerRejectionStatus = null;
            _state.ServerRejectionMessage = null;
            _state.ServerRejectionAtUtc = null;
            File.WriteAllText(Path.Combine(Directory, LicenseFileName), licenseFileJson);
            Save();
            Refresh(DateTime.UtcNow);
        }
        finally
        {
            _lock.Release();
        }

        _logger.LogInformation("Licencia {Key} {How} ({Mode}).", payload.LicenseKey, how, payload.Validation?.Mode);
        await _hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "license", cancellationToken: ct);
        return LicenseOperationResult.Ok($"Licencia {payload.LicenseKey} {how}.");
    }

    /// <summary>Archivo de solicitud (.req) para activar un equipo sin internet desde el backoffice de CLRobotics.</summary>
    public LicenseActivationRequestDto BuildActivationRequest(string activationCode)
    {
        activationCode = NormalizeCode(activationCode);
        var request = new
        {
            schema_version = LicensingConstants.ActivationRequestSchema,
            product = LicenseFeatures.ProductCode,
            activation_code = activationCode,
            hardware_id = HardwareIdentifier,
            hostname = Environment.MachineName,
            os_info = OsInfo(),
            app_version = ServerVersion,
            generated_at = DateTime.UtcNow.ToString("o"),
        };
        string content = JsonSerializer.Serialize(request, StateJson);
        string suffix = activationCode.Length > 0 ? activationCode : HardwareIdentifier;
        return new LicenseActivationRequestDto($"truecentral_{suffix}.req", content);
    }

    /// <summary>Revalidación en línea inmediata (heartbeat a pedido).</summary>
    public async Task<LicenseOperationResult> RefreshAsync(CancellationToken ct)
    {
        if (_payload is null)
            return LicenseOperationResult.Fail("No hay una licencia instalada que revalidar.");
        if (!Server.IsConfigured)
            return LicenseOperationResult.Fail("El servidor de licencias no está configurado; importe el .lic actualizado que le entregue soporte.");
        var outcome = await HeartbeatAsync(ct);
        return outcome;
    }

    /// <summary>
    /// Libera el cupo de este equipo en el servidor (si es alcanzable) y quita
    /// la licencia local. El sistema vuelve al período de prueba si aún queda,
    /// o a sin licencia. Para migrar el servidor a otra máquina.
    /// </summary>
    public async Task<LicenseOperationResult> DeactivateAsync(CancellationToken ct)
    {
        if (_payload is null)
            return LicenseOperationResult.Fail("No hay una licencia instalada.");
        string key = _payload.LicenseKey;
        string note = "";
        if (Server.IsConfigured)
        {
            var result = await Server.DeactivateAsync(key, HardwareIdentifier, ct);
            note = result.Reachable && result.Success
                ? " Cupo liberado en el servidor de licencias."
                : $" No se pudo liberar el cupo en el servidor de licencias ({result.Message}); pídalo a soporte.";
        }
        else
        {
            note = " Sin conexión con el servidor de licencias: pida a soporte que libere el cupo de este equipo.";
        }

        await _lock.WaitAsync(ct);
        try
        {
            _payload = null;
            _licenseFileJson = null;
            _state.ActivationCode = null;
            _state.LastValidatedUtc = null;
            _state.LastHeartbeatError = null;
            _state.ServerRejectionStatus = null;
            _state.ServerRejectionMessage = null;
            _state.ServerRejectionAtUtc = null;
            try { File.Delete(Path.Combine(Directory, LicenseFileName)); }
            catch (IOException) { /* se reintenta en el próximo arranque */ }
            Save();
            Refresh(DateTime.UtcNow);
        }
        finally
        {
            _lock.Release();
        }
        await _hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "license", cancellationToken: ct);
        return LicenseOperationResult.Ok($"Licencia {key} desactivada en este equipo.{note}");
    }

    // ------------------------------------------------------------------
    // Heartbeat
    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await AuditStateAsync("arranque");
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fallo en el ciclo de licenciamiento.");
            }
            try { await Task.Delay(_checkInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var before = Snapshot;
        // Anti-retroceso de reloj: se recuerda la hora más alta vista.
        if (now > _state.LastSeenClockUtc)
        {
            _state.LastSeenClockUtc = now;
            Save();
        }
        var snapshot = Refresh(now);

        bool due = _payload is not null && !_payload.IsOffline && Server.IsConfigured
                   && snapshot.State != LicenseState.Restricted
                   && (snapshot.NextValidationUtc is null || now >= snapshot.NextValidationUtc.Value);
        // También se intenta (cada ciclo) mientras se está en gracia o
        // restringido por gracia agotada: apenas vuelva la red se regulariza.
        bool retry = _payload is not null && !_payload.IsOffline && Server.IsConfigured
                     && (snapshot.State == LicenseState.GracePeriod || (snapshot.State == LicenseState.Restricted && _state.ServerRejectionStatus is null));
        if (due || retry)
            await HeartbeatAsync(ct);

        var after = Snapshot;
        if (after.State != before.State)
        {
            await AuditStateAsync("cambio de estado");
            await _hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "license", cancellationToken: ct);
        }
        else if (after.State is LicenseState.Trial or LicenseState.GracePeriod && after.DaysRemaining is <= 7
                 && _audit.ShouldLog("license-warning", TimeSpan.FromHours(24)))
        {
            await _audit.LogSystemAsync("license", "license-warning", targetType: "license",
                targetId: after.Payload?.LicenseKey, detail: after.Warning);
        }
    }

    private async Task<LicenseOperationResult> HeartbeatAsync(CancellationToken ct)
    {
        var payload = _payload;
        if (payload is null) return LicenseOperationResult.Fail("Sin licencia.");
        string code = _state.ActivationCode ?? payload.LicenseKey;
        var result = await Server.ValidateAsync(code, HardwareIdentifier, Environment.MachineName, ServerVersion, ct);

        await _lock.WaitAsync(ct);
        try
        {
            _state.LastHeartbeatAttemptUtc = DateTime.UtcNow;
            if (!result.Reachable)
            {
                _state.LastHeartbeatError = result.Message;
                Save();
                Refresh(DateTime.UtcNow);
                _logger.LogWarning("Heartbeat de licencia sin respuesta: {Message}", result.Message);
                return LicenseOperationResult.Fail(result.Message);
            }
            if (!result.Success)
            {
                // "No encontrada" con la key de este producto: se trata como
                // error transitorio (p. ej. servidor mal configurado), no como
                // revocación; la gracia sigue corriendo.
                _state.LastHeartbeatError = result.Message;
                Save();
                Refresh(DateTime.UtcNow);
                return LicenseOperationResult.Fail(result.Message);
            }
            if (result.Valid == false)
            {
                _state.ServerRejectionStatus = result.Status ?? "INVALID";
                _state.ServerRejectionMessage = result.Message;
                _state.ServerRejectionAtUtc = DateTime.UtcNow;
                _state.LastHeartbeatError = null;
                Save();
                Refresh(DateTime.UtcNow);
                _logger.LogWarning("El servidor de licencias invalidó la licencia {Key}: {Status} — {Message}",
                    payload.LicenseKey, result.Status, result.Message);
                await _audit.LogSystemAsync("license", "license-validation-failed", targetType: "license",
                    targetId: payload.LicenseKey, detail: $"El servidor de licencias invalidó la licencia ({result.Status}): {result.Message}",
                    success: false);
                return LicenseOperationResult.Fail(result.Message);
            }
            if (result.LicenseFileJson is { } fresh)
            {
                var verification = LicenseFileVerifier.Verify(fresh, _publicKey);
                if (verification.Ok)
                {
                    _payload = verification.Payload;
                    _licenseFileJson = fresh;
                    File.WriteAllText(Path.Combine(Directory, LicenseFileName), fresh);
                    _state.LastValidatedUtc = verification.Payload!.GeneratedAtUtc ?? DateTime.UtcNow;
                }
                else
                {
                    // Firma inválida en un archivo "fresco": servidor o clave
                    // equivocados. Se conserva el archivo anterior.
                    _state.LastHeartbeatError = verification.Error;
                    Save();
                    Refresh(DateTime.UtcNow);
                    return LicenseOperationResult.Fail(verification.Error!);
                }
            }
            else
            {
                _state.LastValidatedUtc = DateTime.UtcNow;
            }
            _state.LastHeartbeatError = null;
            _state.ServerRejectionStatus = null;
            _state.ServerRejectionMessage = null;
            _state.ServerRejectionAtUtc = null;
            Save();
            Refresh(DateTime.UtcNow);
        }
        finally
        {
            _lock.Release();
        }
        _logger.LogInformation("Licencia {Key} revalidada en línea.", payload.LicenseKey);
        await _audit.LogSystemAsync("license", "license-validated", targetType: "license",
            targetId: payload.LicenseKey, detail: "Licencia revalidada en línea contra el servidor de licencias.");
        return LicenseOperationResult.Ok("Licencia revalidada en línea.");
    }

    private async Task AuditStateAsync(string reason)
    {
        var s = Snapshot;
        if (_lastAuditedState == s.State) return;
        _lastAuditedState = s.State;
        string action = s.State switch
        {
            LicenseState.Active => "license-active",
            LicenseState.Trial => "license-trial",
            LicenseState.GracePeriod => "license-grace",
            LicenseState.Restricted => "license-restricted",
            _ => "license-unlicensed",
        };
        await _audit.LogSystemAsync("license", action, targetType: "license", targetId: s.Payload?.LicenseKey,
            detail: $"Estado de licencia ({reason}): {StateLabel(s.State)}. {s.Message}".Trim(),
            success: s.Operational);
    }

    // ------------------------------------------------------------------
    // Evaluación del estado
    // ------------------------------------------------------------------

    private LicenseSnapshot Refresh(DateTime now)
    {
        var snapshot = Evaluate(now);
        Volatile.Write(ref _snapshot, snapshot);
        return snapshot;
    }

    private LicenseSnapshot Evaluate(DateTime now)
    {
        var payload = _payload;
        if (payload is null)
            return EvaluateTrial(now);

        var features = LicenseFeatureSet.FromPayload(payload);
        string mode = payload.IsOffline ? "OFFLINE" : "ONLINE";
        var expires = payload.ExpiresAtUtc;

        if (payload.HardwareId is { Length: > 0 } hw && !string.Equals(hw, HardwareIdentifier, StringComparison.OrdinalIgnoreCase))
            return Restricted(payload, features, mode, $"El archivo de licencia pertenece a otro equipo ({hw}).", expires);
        if (expires is { } exp && exp <= now)
            return Restricted(payload, features, mode, $"La licencia {payload.LicenseKey} venció el {exp:yyyy-MM-dd}. Renuévela para seguir operando.", expires);
        if (_state.ServerRejectionStatus is { } rejection)
            return Restricted(payload, features, mode,
                $"El servidor de licencias invalidó la licencia ({RejectionLabel(rejection)}): {_state.ServerRejectionMessage}", expires);
        if (ClockRolledBack(now))
            return Restricted(payload, features, mode,
                $"El reloj del servidor retrocedió (última hora conocida {_state.LastSeenClockUtc:yyyy-MM-dd HH:mm} UTC). Corrija la hora del sistema.", expires);

        int? daysToExpiry = expires is { } e2 ? Math.Max(0, (int)Math.Ceiling((e2 - now).TotalDays)) : null;
        string? expiryWarning = daysToExpiry is <= 30
            ? $"La licencia vence el {expires:yyyy-MM-dd} ({daysToExpiry} día(s))."
            : null;

        if (payload.IsOffline)
            return new LicenseSnapshot(LicenseState.Active, true, mode, "Licencia vigente (modo sin conexión).",
                expiryWarning, payload, features, _state.LastValidatedUtc, null, null, expires, daysToExpiry);

        var lastValidated = _state.LastValidatedUtc ?? payload.GeneratedAtUtc ?? now;
        var nextValidation = lastValidated.AddDays(payload.HeartbeatDays);
        var graceEnds = nextValidation.AddDays(payload.GraceDays);
        int daysOfGrace = Math.Max(0, (int)Math.Ceiling((graceEnds - now).TotalDays));
        int? daysRemaining = daysToExpiry is { } d ? Math.Min(d, daysOfGrace) : daysOfGrace;

        if (now >= graceEnds)
            return Restricted(payload, features, mode,
                $"No se ha podido revalidar la licencia en línea desde el {lastValidated:yyyy-MM-dd} y el período de gracia terminó. " +
                $"Restablezca la conexión con el servidor de licencias o importe un .lic actualizado. " +
                (_state.LastHeartbeatError is { } err ? $"Último error: {err}" : ""),
                expires, lastValidated, nextValidation, graceEnds);

        if (now >= nextValidation && _state.LastHeartbeatAttemptUtc is not null)
            return new LicenseSnapshot(LicenseState.GracePeriod, true, mode,
                "Licencia vigente, pendiente de revalidación en línea.",
                $"No se ha podido revalidar la licencia en línea desde el {lastValidated:yyyy-MM-dd}" +
                (_state.LastHeartbeatError is { } err2 ? $" ({err2})" : "") +
                $". El sistema pasará a modo restringido el {graceEnds:yyyy-MM-dd} si no se revalida.",
                payload, features, lastValidated, nextValidation, graceEnds, expires, daysRemaining);

        return new LicenseSnapshot(LicenseState.Active, true, mode, "Licencia vigente.", expiryWarning,
            payload, features, lastValidated, nextValidation, graceEnds, expires, daysToExpiry);
    }

    private LicenseSnapshot EvaluateTrial(DateTime now)
    {
        if (_trialDays <= 0)
            return new LicenseSnapshot(LicenseState.Unlicensed, false, "NONE",
                "Sin licencia. Active el sistema con su código de activación o importe el archivo .lic.",
                "El sistema no está licenciado: solo se puede administrar la licencia.",
                null, LicenseFeatureSet.Empty, null, null, null, null, 0);

        if (_state.TrialStartedUtc is null)
        {
            _state.TrialStartedUtc = now;
            Save();
        }
        var trialEnds = _state.TrialStartedUtc.Value.AddDays(_trialDays);
        if (ClockRolledBack(now))
            return new LicenseSnapshot(LicenseState.Restricted, false, "TRIAL",
                $"El reloj del servidor retrocedió (última hora conocida {_state.LastSeenClockUtc:yyyy-MM-dd HH:mm} UTC). Corrija la hora del sistema.",
                null, null, LicenseFeatureSet.Empty, null, null, null, trialEnds, 0);
        if (now >= trialEnds)
            return new LicenseSnapshot(LicenseState.Unlicensed, false, "TRIAL",
                $"El período de prueba terminó el {trialEnds:yyyy-MM-dd}. Active el sistema con su código de activación o importe el archivo .lic.",
                "Período de prueba terminado: el sistema está en modo restringido.",
                null, LicenseFeatureSet.Empty, null, null, null, trialEnds, 0);

        int days = Math.Max(0, (int)Math.Ceiling((trialEnds - now).TotalDays));
        return new LicenseSnapshot(LicenseState.Trial, true, "TRIAL",
            $"Período de prueba hasta el {trialEnds:yyyy-MM-dd} ({days} día(s)).",
            days <= 7 ? $"El período de prueba termina en {days} día(s). Active su licencia para no interrumpir la operación." : null,
            null, new LicenseFeatureSet(LicensingConstants.TrialFeatures), null, null, null, trialEnds, days);
    }

    /// <summary>true si el reloj del sistema está más de un día ANTES de la
    /// última hora vista (estado recién creado = sin referencia, no cuenta).</summary>
    private bool ClockRolledBack(DateTime now) =>
        _state.LastSeenClockUtc > DateTime.UnixEpoch && now < _state.LastSeenClockUtc.AddDays(-1);

    private static LicenseSnapshot Restricted(LicensePayload payload, LicenseFeatureSet features, string mode, string message,
        DateTime? expires, DateTime? lastValidated = null, DateTime? nextValidation = null, DateTime? graceEnds = null) =>
        new(LicenseState.Restricted, false, mode, message, "Sistema en modo restringido por licencia: " + message,
            payload, features, lastValidated, nextValidation, graceEnds, expires, 0);

    public static string StateLabel(LicenseState state) => state switch
    {
        LicenseState.Active => "activa",
        LicenseState.Trial => "período de prueba",
        LicenseState.GracePeriod => "en período de gracia",
        LicenseState.Restricted => "restringida",
        _ => "sin licencia",
    };

    private static string RejectionLabel(string status) => status.ToUpperInvariant() switch
    {
        "REVOKED" => "revocada",
        "SUSPENDED" => "suspendida",
        "EXPIRED" => "vencida",
        _ => status,
    };

    // ------------------------------------------------------------------
    // Persistencia
    // ------------------------------------------------------------------

    private void Load()
    {
        string statePath = Path.Combine(Directory, StateFileName);
        try
        {
            if (File.Exists(statePath))
                _state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(statePath), StateJson) ?? new PersistedState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer el estado de licenciamiento; se parte de cero.");
            _state = new PersistedState();
        }

        string licensePath = Path.Combine(Directory, LicenseFileName);
        if (!File.Exists(licensePath)) return;
        try
        {
            string json = File.ReadAllText(licensePath);
            var verification = LicenseFileVerifier.Verify(json, _publicKey);
            if (verification.Ok)
            {
                _payload = verification.Payload;
                _licenseFileJson = json;
            }
            else
            {
                _logger.LogError("El archivo de licencia instalado no es válido: {Error}", verification.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo leer el archivo de licencia instalado.");
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(Path.Combine(Directory, StateFileName), JsonSerializer.Serialize(_state, StateJson));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo guardar el estado de licenciamiento.");
        }
    }

    private static string NormalizeCode(string? raw)
    {
        string cleaned = new((raw ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (cleaned.Length != 25) return (raw ?? "").Trim().ToUpperInvariant();
        return string.Join('-', Enumerable.Range(0, 5).Select(i => cleaned.Substring(i * 5, 5)));
    }

    private static string OsInfo()
    {
        string os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        return os.Length > 255 ? os[..255] : os;
    }

    private sealed class PersistedState
    {
        public DateTime? TrialStartedUtc { get; set; }
        public string? ActivationCode { get; set; }
        public DateTime? LastValidatedUtc { get; set; }
        public DateTime? LastHeartbeatAttemptUtc { get; set; }
        public string? LastHeartbeatError { get; set; }
        public string? ServerRejectionStatus { get; set; }
        public string? ServerRejectionMessage { get; set; }
        public DateTime? ServerRejectionAtUtc { get; set; }
        public DateTime LastSeenClockUtc { get; set; }
    }
}

/// <summary>Foto inmutable del estado de licenciamiento.</summary>
public sealed record LicenseSnapshot(
    LicenseState State,
    bool Operational,
    string Mode,
    string? Message,
    string? Warning,
    LicensePayload? Payload,
    LicenseFeatureSet Features,
    DateTime? LastValidatedUtc,
    DateTime? NextValidationUtc,
    DateTime? GraceEndsUtc,
    DateTime? ExpiresUtc,
    int? DaysRemaining);

public sealed record LicenseOperationResult(bool Success, string Message)
{
    public static LicenseOperationResult Ok(string message) => new(true, message);
    public static LicenseOperationResult Fail(string message) => new(false, message);
}
