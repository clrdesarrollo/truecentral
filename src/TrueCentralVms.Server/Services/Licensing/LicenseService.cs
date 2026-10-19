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
///
/// Blindaje del estado local (lo que no viene firmado en el .lic: inicio de la
/// prueba, última revalidación, revocación recibida, hora más alta vista):
/// <list type="bullet">
/// <item>Tres copias cifradas y autenticadas (<see cref="LicenseStateStore"/>:
/// archivo, registro, base de datos). Al arrancar se combinan quedándose con
/// lo más restrictivo, y se reescriben las tres: borrar o editar una no sirve.</item>
/// <item>Marca de prueba en el registro que sobrevive a la desinstalación.</item>
/// <item>Si no queda ninguna copia, la prueba empezó a más tardar con el primer
/// evento de la bitácora: borrar el estado no regala otra prueba.</item>
/// <item>Reloj: se compara contra la mayor de la hora más alta vista y la fecha
/// firmada del .lic más nuevo aceptado (con tolerancia de
/// <see cref="ClockTolerance"/>). Una revalidación en línea, que trae la hora
/// del servidor de licencias firmada, demuestra que el reloj está bien.</item>
/// <item>No se acepta un .lic más antiguo que el último aceptado, ni el mismo
/// archivo después de que el servidor lo invalidó.</item>
/// </list>
/// </summary>
public sealed class LicenseService : BackgroundService
{
    private const string LicenseFileName = "license.lic";

    /// <summary>
    /// Cuánto puede estar el reloj ANTES de la referencia sin considerarlo un
    /// retroceso: cubre ajustes de NTP y una zona horaria mal puesta y
    /// corregida. No se acumula (la referencia es siempre la más alta vista).
    /// </summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromHours(12);

    /// <summary>Holgura al comparar fechas de generación de archivos (redondeos del servidor).</summary>
    private static readonly TimeSpan GeneratedSlack = TimeSpan.FromMinutes(1);

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

    private readonly LicenseStateStore _store;
    private readonly LicenseQuotaReconciler _quotas;
    private PersistedLicenseState _state = new();
    private LicensePayload? _payload;
    private string? _licenseFileJson;
    private LicenseSnapshot _snapshot;
    private LicenseState? _lastAuditedState;
    /// <summary>El .lic del disco fue reemplazado por uno más antiguo que el aceptado: restringido.</summary>
    private string? _tamperMessage;
    /// <summary>Qué se reconstruyó al arrancar (para la bitácora); null si las copias estaban bien.</summary>
    private string? _startupNote;

    public LicenseService(IConfiguration config, IHostEnvironment env, EmbeddedPostgres postgres,
        IServiceScopeFactory scopes, AuditService audit, IHubContext<VmsHub> hub, TokenService tokens,
        LicenseQuotaReconciler quotas, ILogger<LicenseService> logger)
    {
        _quotas = quotas;
        _scopes = scopes;
        _audit = audit;
        _hub = hub;
        _tokens = tokens;
        _logger = logger;

        Directory = Path.Combine(Path.GetDirectoryName(postgres.DataDirectory) ?? postgres.DataDirectory, "license");
        System.IO.Directory.CreateDirectory(Directory);

#if DEBUG
        _trialDays = Math.Max(0, config.GetValue<int?>("Licensing:TrialDays") ?? LicensingConstants.TrialDays);
#else
        _trialDays = LicensingConstants.TrialDays;
#endif
        _store = new LicenseStateStore(Directory, scopes, logger);
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
    /// Módulos que incluye la licencia (o la prueba), para que la interfaz oculte
    /// los demás. Sin licencia vigente (vencida, revocada, prueba terminada) no
    /// hay ninguno: la interfaz deja solo lo necesario para regularizarla.
    /// </summary>
    public IReadOnlyList<string> LicensedModules() => Snapshot.Operational ? LicensedModules(Snapshot) : [];

    private static IReadOnlyList<string> LicensedModules(LicenseSnapshot s) =>
        LicenseFeatures.Modules.Select(m => m.ModuleKey!).Distinct().Where(s.Features.IsEnabled).ToList();

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
                 // Liberar puestos de cliente: justo lo que hace falta cuando
                 // la licencia no da para que entre alguien más.
                 || path.StartsWithSegments("/api/system/desktop-seats")
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
            [LicenseFeatures.FencePanels] = await db.CercoPanels.CountAsync(p => p.Enabled, ct),
            [LicenseFeatures.AccessDoors] = await db.AccessDoors.CountAsync(d => d.Enabled && d.AccessDevice!.Enabled, ct),
            [LicenseFeatures.Videowalls] = await db.Walls.CountAsync(ct),
            [LicenseFeatures.VideowallDecoders] = await db.Decoders.CountAsync(d => d.Enabled, ct),
            [LicenseFeatures.SpeakerChannels] = await db.Speakers.CountAsync(s => s.Enabled, ct),
            [LicenseFeatures.IntercomDevices] = await db.Intercoms.CountAsync(i => i.Enabled, ct),
            [LicenseFeatures.IoModules] = await db.IoModules.CountAsync(m => m.Enabled, ct),
            [LicenseFeatures.AlarmScreens] = await db.AlarmScreens.CountAsync(s => s.Status == AlarmScreenStatus.Approved, ct),
            [LicenseFeatures.AutomationRules] = await db.Workflows.CountAsync(w => w.Enabled, ct),
            [LicenseFeatures.MaxUsers] = await db.Users.CountAsync(u => u.Enabled, ct),
            [LicenseFeatures.MaxClientSessions] = _tokens.CountDesktopSessions(),
        };
        return ToDto(Snapshot, usage);
    }

    private LicenseStatusDto ToDto(LicenseSnapshot s, IReadOnlyDictionary<string, int> usage)
    {
        var modules = LicenseFeatures.BaseQuotas.Concat(LicenseFeatures.Modules).Select(m => new LicenseModuleDto(
            m.Name, m.ModuleKey,
            Enabled: s.Operational && (m.ModuleKey is null || s.Features.IsEnabled(m.ModuleKey)),
            m.QuotaKey,
            Quota: m.QuotaKey is null ? null : s.Features.Quota(m.QuotaKey),
            InUse: m.QuotaKey is null ? null : usage.GetValueOrDefault(m.QuotaKey),
            m.Unit, m.Area)).ToList();
        var addons = (s.Payload?.Addons ?? []).Select(a => new LicenseAddonDto(
            a.LicenseKey, a.Package, LicensePayload.ParseUtc(a.ExpiresAt), SummarizeAddon(a))).ToList();
        return new LicenseStatusDto(
            s.State, s.Operational, s.Mode, s.Message, s.Warning,
            s.Payload?.LicenseKey, s.Payload?.Customer?.Name, s.Payload?.Package,
            s.Payload?.IssuedAtUtc, s.ExpiresUtc, s.LastValidatedUtc, s.NextValidationUtc, s.GraceEndsUtc,
            s.DaysRemaining, HardwareIdentifier, Environment.MachineName, ServerVersion,
            Server.IsConfigured, Server.ServerUrl, modules, addons, BuildCodes(s.Payload));
    }

    /// <summary>
    /// Códigos instalados: la base y cada expansión con lo que aporta. El .lic
    /// trae las características EFECTIVAS (base + expansiones) y, aparte, las
    /// propias de cada expansión; lo de la base es la diferencia (los enteros
    /// se restan; un módulo es de la base si ninguna expansión lo trae).
    /// </summary>
    private static IReadOnlyList<LicenseCodeDto> BuildCodes(LicensePayload? payload)
    {
        if (payload is null) return [];
        var baseValues = new Dictionary<string, (int? Number, bool? Flag)>();
        foreach (var (key, element) in payload.Features)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int n))
            {
                int fromAddons = payload.Addons.Sum(a => a.Features.TryGetValue(key, out var v)
                    && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int x) ? x : 0);
                baseValues[key] = (Math.Max(0, n - fromAddons), null);
            }
            else if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                bool fromAddon = payload.Addons.Any(a => a.Features.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.True);
                baseValues[key] = (null, element.ValueKind == JsonValueKind.True && !fromAddon);
            }
        }

        var codes = new List<LicenseCodeDto>
        {
            new(payload.LicenseKey, "Base", payload.Package, payload.IssuedAtUtc, payload.ExpiresAtUtc,
                SummarizeItems(CodeItems(baseValues, includeEmptyQuotas: true)), CodeItems(baseValues, includeEmptyQuotas: true)),
        };
        foreach (var addon in payload.Addons)
        {
            var values = new Dictionary<string, (int? Number, bool? Flag)>();
            foreach (var (key, element) in addon.Features)
            {
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int n)) values[key] = (n, null);
                else if (element.ValueKind is JsonValueKind.True or JsonValueKind.False) values[key] = (null, element.ValueKind == JsonValueKind.True);
            }
            codes.Add(new LicenseCodeDto(addon.LicenseKey, "Expansion", addon.Package, null,
                LicensePayload.ParseUtc(addon.ExpiresAt), SummarizeAddon(addon), CodeItems(values, includeEmptyQuotas: false)));
        }
        return codes;
    }

    /// <summary>
    /// Lo que aporta un código, en el orden y con los nombres del catálogo de
    /// presentación. Una expansión muestra solo lo que suma; la base, también
    /// los cupos en cero de sus módulos incluidos.
    /// </summary>
    private static IReadOnlyList<LicenseCodeItemDto> CodeItems(
        IReadOnlyDictionary<string, (int? Number, bool? Flag)> values, bool includeEmptyQuotas)
    {
        var items = new List<LicenseCodeItemDto>();
        var shownModules = new HashSet<string>();
        foreach (var m in LicenseFeatures.BaseQuotas.Concat(LicenseFeatures.Modules))
        {
            bool moduleOn = m.ModuleKey is not null && values.TryGetValue(m.ModuleKey, out var mv) && mv.Flag == true;
            string? quota = null;
            if (m.QuotaKey is not null && values.TryGetValue(m.QuotaKey, out var q) && q.Number is { } n
                && (n > 0 || (includeEmptyQuotas && (m.ModuleKey is null || moduleOn))))
                quota = includeEmptyQuotas ? $"{n} {m.Unit}" : $"+{n} {m.Unit}";
            // El módulo y su cupo van en una sola fila ("Incluido · 32 canales de video").
            if (moduleOn && shownModules.Add(m.ModuleKey!))
                items.Add(new LicenseCodeItemDto(m.Area, m.Name, quota is null ? IncludedLabel : $"{IncludedLabel} · {quota}"));
            else if (quota is not null)
                items.Add(new LicenseCodeItemDto(m.Area, m.Name, quota));
        }
        return items;
    }

    private const string IncludedLabel = "Incluido";

    private static string SummarizeItems(IReadOnlyList<LicenseCodeItemDto> items)
    {
        int modules = items.Count(i => i.Value.StartsWith(IncludedLabel, StringComparison.Ordinal));
        return modules == 0 ? "—" : $"{modules} módulo(s)";
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
        if (RejectOlder(payload) is { } older)
            return LicenseOperationResult.Fail(older);

        await _lock.WaitAsync(ct);
        try
        {
            _payload = payload;
            _licenseFileJson = licenseFileJson;
            _tamperMessage = null;
            AcceptGenerated(payload, DateTime.UtcNow);
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
        await RestoreChannelsDisabledByLicenseAsync(ct);
        await _quotas.ReconcileAsync(Snapshot, ct);
        return LicenseOperationResult.Ok($"Licencia {payload.LicenseKey} {how}.");
    }

    /// <summary>
    /// Motivo para no aceptar un .lic (válido y firmado) por ser más antiguo
    /// que el último aceptado en este equipo, o por ser el mismo que el
    /// servidor de licencias ya invalidó; null si se puede aceptar.
    /// </summary>
    private string? RejectOlder(LicensePayload payload)
    {
        var generated = payload.GeneratedAtUtc;
        if (_state.HighestLicenseGeneratedUtc is { } highest && (generated is null || generated < highest - GeneratedSlack))
            return $"Este archivo de licencia es anterior al último aceptado en este equipo (generado el " +
                   $"{(generated is { } g ? g.ToString("yyyy-MM-dd HH:mm") : "—")} UTC; el último es del {highest:yyyy-MM-dd HH:mm} UTC). " +
                   "Pida a soporte un archivo actualizado.";
        if (_state.RejectedLicenseKey == payload.LicenseKey && _state.RejectedAtUtc is { } rejected
            && (generated is null || generated <= rejected))
            return $"El servidor de licencias invalidó la licencia {payload.LicenseKey} el {rejected:yyyy-MM-dd}: " +
                   "este archivo es anterior a eso. Pida a soporte uno emitido después.";
        return null;
    }

    /// <summary>
    /// Registra un .lic recién aceptado (bajo el lock): sube la fecha de
    /// generación más nueva, olvida una invalidación anterior de esa misma
    /// licencia y, si el servidor lo acaba de firmar, usa su hora para dar por
    /// bueno el reloj de este equipo.
    /// </summary>
    private void AcceptGenerated(LicensePayload payload, DateTime now)
    {
        if (payload.GeneratedAtUtc is not { } generated) return;
        if (_state.HighestLicenseGeneratedUtc is null || generated > _state.HighestLicenseGeneratedUtc)
            _state.HighestLicenseGeneratedUtc = generated;
        if (_state.RejectedLicenseKey == payload.LicenseKey && _state.RejectedAtUtc is { } rejected && generated > rejected)
        {
            _state.RejectedLicenseKey = null;
            _state.RejectedAtUtc = null;
        }
        // Firmado hace instantes por el servidor de licencias con su propia
        // hora: si coincide con el reloj de este equipo, el reloj está bien
        // (aunque haya quedado una referencia "del futuro" de un reloj que
        // estuvo adelantado y ya se corrigió).
        if ((now - generated).Duration() <= ClockTolerance && _state.LastSeenClockUtc > now)
        {
            _logger.LogInformation("Licencia: la hora firmada por el servidor de licencias confirma el reloj de este equipo.");
            _state.LastSeenClockUtc = now;
        }
    }

    /// <summary>Archivo de solicitud (.req) para activar un equipo sin internet desde el backoffice de CLRobotics.</summary>
    public LicenseActivationRequestDto BuildActivationRequest(string activationCode) =>
        BuildActivationRequest([activationCode]);

    /// <summary>
    /// Solicitud (.req) para una lista de códigos: el archivo se sube en la
    /// licencia BASE del backoffice (su .lic ya trae las expansiones ligadas),
    /// así que <c>activation_code</c> es la base instalada o, si aún no hay, el
    /// primer código. Los demás van en <c>addon_codes</c> para que soporte
    /// sepa qué expansiones espera el cliente (el servidor de licencias no lo lee).
    /// </summary>
    public LicenseActivationRequestDto BuildActivationRequest(IEnumerable<string?> codes)
    {
        var list = codes.Select(NormalizeCode).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string activationCode = _payload?.LicenseKey ?? list.FirstOrDefault() ?? "";
        var addonCodes = list.Where(c => !string.Equals(c, activationCode, StringComparison.OrdinalIgnoreCase)).ToList();
        var request = new
        {
            schema_version = LicensingConstants.ActivationRequestSchema,
            product = LicenseFeatures.ProductCode,
            activation_code = activationCode,
            addon_codes = addonCodes.Count > 0 ? addonCodes : null,
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
        await RestoreChannelsDisabledByLicenseAsync(ct);
        await _quotas.ReconcileAsync(Snapshot, ct);
        return outcome;
    }

    /// <summary>
    /// El cliente ingresa el código de una expansión que compró. La expansión
    /// ya está ligada a la licencia base en el servidor de licencias y llega en
    /// el archivo de la base: aquí se confirma que es de ESTA licencia y está
    /// vigente, y se revalida para traerla en el acto. Sin conexión con el
    /// servidor de licencias se explica cómo cargarla (.req → .lic).
    /// </summary>
    public async Task<LicenseOperationResult> AddAddonAsync(string addonCode, CancellationToken ct)
    {
        string code = NormalizeCode(addonCode);
        if (code.Length == 0)
            return LicenseOperationResult.Fail("Ingrese el código de la expansión.");
        if (_payload is not { } payload)
            return LicenseOperationResult.Fail("Primero active la licencia base: las expansiones se suman a ella.");
        if (string.Equals(code, payload.LicenseKey, StringComparison.OrdinalIgnoreCase))
            return LicenseOperationResult.Fail("Ese es el código de la licencia base, no de una expansión.");
        if (payload.Addons.FirstOrDefault(a => string.Equals(a.LicenseKey, code, StringComparison.OrdinalIgnoreCase)) is { } included)
            return LicenseOperationResult.Ok($"La expansión {included.LicenseKey} ya está incluida en la licencia: {SummarizeAddon(included)}.");
        if (!Server.IsConfigured)
            return LicenseOperationResult.Fail(
                $"Este servidor no está conectado al servidor de licencias. Para cargar la expansión {code}: genere la solicitud " +
                "de activación (.req) en esta pantalla, envíela a soporte y luego importe el archivo .lic actualizado de la " +
                "licencia base, que ya la incluye.");

        string baseCode = _state.ActivationCode ?? payload.LicenseKey;
        var check = await Server.CheckAddonAsync(code, baseCode, HardwareIdentifier, ct);
        if (!check.Reachable)
            return LicenseOperationResult.Fail(check.Message);
        if (!check.Success || check.Valid != true)
            return LicenseOperationResult.Fail(check.Message.Length > 0 ? check.Message : "El servidor de licencias no reconoce esa expansión.");

        var refresh = await RefreshAsync(ct);
        if (!refresh.Success)
            return LicenseOperationResult.Fail($"La expansión {code} es válida, pero no se pudo revalidar la licencia: {refresh.Message}");
        bool arrived = _payload?.Addons.Any(a => string.Equals(a.LicenseKey, code, StringComparison.OrdinalIgnoreCase)) == true;
        return arrived
            ? LicenseOperationResult.Ok($"Expansión {code} aplicada: {check.Summary ?? "cupos actualizados"}.")
            : LicenseOperationResult.Fail($"La expansión {code} es válida, pero el archivo de licencia todavía no la trae. Intente «Revalidar ahora» en unos minutos.");
    }

    /// <summary>
    /// Ingreso único en línea: la licencia base y sus expansiones en una sola
    /// lista, en cualquier orden. Sin base instalada, la base es el primer
    /// código que el servidor de licencias acepta activar; el resto (y los que
    /// no eran base) se suman como expansiones. Con base instalada, su propio
    /// código revalida y los demás se suman como expansiones.
    /// </summary>
    public async Task<LicenseApplyResultDto> ApplyCodesAsync(IEnumerable<string?> rawCodes, CancellationToken ct)
    {
        var codes = rawCodes.Select(NormalizeCode).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (codes.Count == 0)
            return new LicenseApplyResultDto(false, "Ingrese al menos un código.", []);
        if (!Server.IsConfigured)
            return new LicenseApplyResultDto(false,
                "Este servidor no está conectado al servidor de licencias: elija «Sin internet» para activarla con archivos.", []);

        var results = new Dictionary<string, LicenseApplyItemDto>(StringComparer.OrdinalIgnoreCase);
        string? baseCode = _payload?.LicenseKey;
        if (baseCode is null)
        {
            foreach (string code in codes)
            {
                var activation = await ActivateOnlineAsync(code, ct);
                results[code] = new LicenseApplyItemDto(code, activation.Success ? "Base" : null, activation.Success, activation.Message, "activate");
                if (activation.Success) { baseCode = _payload?.LicenseKey ?? code; break; }
            }
            if (baseCode is null)
                return new LicenseApplyResultDto(false,
                    "Ninguno de los códigos se pudo activar como licencia base. Una expansión se suma a una licencia ya activa: ingrese también el código de la licencia base.",
                    codes.Select(c => results[c]).ToList());
        }

        foreach (string code in codes)
        {
            if (results.TryGetValue(code, out var done) && done.Success) continue;
            if (string.Equals(code, baseCode, StringComparison.OrdinalIgnoreCase))
            {
                var refresh = await RefreshAsync(ct);
                results[code] = new LicenseApplyItemDto(code, "Base", refresh.Success,
                    refresh.Success ? $"Licencia base {code} revalidada." : refresh.Message, "refresh");
                continue;
            }
            var addon = await AddAddonAsync(code, ct);
            results[code] = new LicenseApplyItemDto(code, addon.Success ? "Expansion" : null, addon.Success, addon.Message, "addon");
        }

        var items = codes.Select(c => results[c]).ToList();
        int ok = items.Count(i => i.Success);
        string message = ok == items.Count
            ? (items.Count == 1 ? items[0].Message : $"Se aplicaron los {items.Count} códigos.")
            : $"Se aplicaron {ok} de {items.Count} códigos; revise el detalle de cada uno.";
        return new LicenseApplyResultDto(ok == items.Count, message, items);
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
        // Lo que el arranque tuvo que reparar del estado local queda en la bitácora:
        // copias borradas o editadas son, como mínimo, algo que revisar.
        if (_startupNote is { } note)
            await _audit.LogSystemAsync("license", "license-state-rebuilt", targetType: "license",
                targetId: _payload?.LicenseKey, detail: note);
        if (_tamperMessage is { } tamper)
            await _audit.LogSystemAsync("license", "license-file-tampered", targetType: "license",
                targetId: _payload?.LicenseKey, detail: tamper, success: false);
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
        // La revalidación en línea puede traer un .lic con otros módulos (una
        // expansión nueva) sin cambiar el estado: la interfaz también se entera.
        bool modulesChanged = !LicensedModules(before).SequenceEqual(LicensedModules(after));
        if (after.State != before.State || modulesChanged)
            await _hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "license", cancellationToken: ct);
        if (after.State != before.State)
        {
            await AuditStateAsync("cambio de estado");
        }
        else if (after.State is LicenseState.Trial or LicenseState.GracePeriod && after.DaysRemaining is <= 7
                 && _audit.ShouldLog("license-warning", TimeSpan.FromHours(24)))
        {
            await _audit.LogSystemAsync("license", "license-warning", targetType: "license",
                targetId: after.Payload?.LicenseKey, detail: after.Warning);
        }
        await RestoreChannelsDisabledByLicenseAsync(ct);
        await _quotas.ReconcileAsync(Snapshot, ct);
    }

    /// <summary>
    /// Canales que el cupo de la licencia dejó deshabilitados
    /// (<see cref="Data.Entities.Channel.DisabledByLicense"/>): si ahora caben
    /// (licencia nueva, expansión, fin del modo restringido) se habilitan solos,
    /// en orden de equipo y número de canal. Los que siguen sin caber conservan
    /// la marca para la próxima vez. Nunca hace fallar la operación que lo invoca.
    /// </summary>
    private async Task RestoreChannelsDisabledByLicenseAsync(CancellationToken ct)
    {
        if (!IsModuleEnabled(LicenseFeatures.ModuleVideo)) return;
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var pending = await db.Channels.Include(c => c.Device)
                .Where(c => c.DisabledByLicense && !c.Enabled)
                .OrderBy(c => c.DeviceId).ThenBy(c => c.ChannelNumber)
                .ToListAsync(ct);
            if (pending.Count == 0) return;

            int quota = Quota(LicenseFeatures.VideoChannels);
            int available = quota - await db.Channels.CountAsync(c => c.Enabled, ct);
            if (available <= 0) return;

            var restored = pending.Take(available).ToList();
            foreach (var channel in restored)
            {
                channel.Enabled = true;
                channel.DisabledByLicense = false;
            }
            await db.SaveChangesAsync(ct);

            int still = pending.Count - restored.Count;
            string perDevice = string.Join(", ", restored.GroupBy(c => c.Device.Name)
                .Select(g => $"'{g.Key}' ({string.Join(", ", g.Select(c => c.ChannelNumber))})"));
            _logger.LogInformation("Licencia: {Count} canal(es) deshabilitados por cupo vuelven a estar habilitados.", restored.Count);
            await _audit.LogSystemAsync("license", "license-channels-restored", targetType: "license",
                targetId: Snapshot.Payload?.LicenseKey,
                detail: $"Habilitó {restored.Count} canal(es) que el cupo de la licencia había dejado deshabilitados " +
                        $"(cupo {quota} canales): {perDevice}." + (still > 0 ? $" {still} siguen esperando cupo." : ""));
            await scope.ServiceProvider.GetRequiredService<MediaMtxManager>().RefreshPathsAsync(ct);
            await _hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "channels", cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudieron habilitar los canales que esperaban cupo de la licencia.");
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
                // Memoria que sobrevive a desactivar: el mismo archivo no vuelve a entrar.
                _state.RejectedLicenseKey = payload.LicenseKey;
                _state.RejectedAtUtc = DateTime.UtcNow;
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
                // Un archivo "fresco" más antiguo que el aceptado no se toma
                // (servidor restaurado de un respaldo, o respuesta repetida).
                string? older = verification.Ok ? RejectOlder(verification.Payload!) : null;
                if (verification.Ok && older is null)
                {
                    _payload = verification.Payload;
                    _licenseFileJson = fresh;
                    _tamperMessage = null;
                    File.WriteAllText(Path.Combine(Directory, LicenseFileName), fresh);
                    AcceptGenerated(verification.Payload!, DateTime.UtcNow);
                    _state.LastValidatedUtc = verification.Payload!.GeneratedAtUtc ?? DateTime.UtcNow;
                }
                else
                {
                    // Firma inválida en un archivo "fresco": servidor o clave
                    // equivocados. Se conserva el archivo anterior.
                    _state.LastHeartbeatError = verification.Error ?? older;
                    Save();
                    Refresh(DateTime.UtcNow);
                    return LicenseOperationResult.Fail((verification.Error ?? older)!);
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
        var before = Volatile.Read(ref _snapshot);
        var snapshot = Evaluate(now);
        Volatile.Write(ref _snapshot, snapshot);
        // La licencia dejó de estar vigente: los clientes de escritorio salen
        // (el login ya no los deja volver a entrar, ver AuthApi). Pasa una sola
        // vez, en la transición; el panel web sigue para poder regularizarla.
        if (before is { Operational: true } && !snapshot.Operational)
            DisconnectDesktopClients(snapshot);
        return snapshot;
    }

    private void DisconnectDesktopClients(LicenseSnapshot snapshot)
    {
        int closed = _tokens.RevokeDesktopSessions("licencia no vigente");
        if (closed == 0) return;
        _logger.LogWarning("Licencia no vigente: se cerraron {Count} sesión(es) de cliente de escritorio.", closed);
        // Sin esperar: Refresh es sincrónico y la bitácora nunca lanza.
        _ = _audit.LogSystemAsync("license", "license-clients-disconnected", targetType: "license",
            targetId: snapshot.Payload?.LicenseKey,
            detail: $"Se cerraron {closed} sesión(es) de cliente de escritorio porque la licencia dejó de estar vigente: {snapshot.Message}",
            data: new { sessions = closed }, success: false);
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
        if (_tamperMessage is { } tamper)
            return Restricted(payload, features, mode, tamper, expires);
        // El reloj antes que el vencimiento: con el reloj atrasado, una licencia
        // vencida parecería vigente.
        if (ClockRolledBack(now))
            return Restricted(payload, features, mode, ClockMessage(), expires);
        if (expires is { } exp && exp <= now)
            return Restricted(payload, features, mode, $"La licencia {payload.LicenseKey} venció el {exp:yyyy-MM-dd}. Renuévela para seguir operando.", expires);
        if (_state.ServerRejectionStatus is { } rejection)
            return Restricted(payload, features, mode,
                $"El servidor de licencias invalidó la licencia ({RejectionLabel(rejection)}): {_state.ServerRejectionMessage}", expires);

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
            return new LicenseSnapshot(LicenseState.Restricted, false, "TRIAL", ClockMessage(),
                "Sistema en modo restringido por licencia: " + ClockMessage(),
                null, LicenseFeatureSet.Empty, null, null, null, trialEnds, 0);
        if (now >= trialEnds)
            return new LicenseSnapshot(LicenseState.Unlicensed, false, "TRIAL",
                $"El período de prueba terminó el {trialEnds:yyyy-MM-dd}. Active el sistema con su código de activación o importe el archivo .lic.",
                "Período de prueba terminado: el sistema está en modo restringido.",
                null, LicenseFeatureSet.Empty, null, null, null, trialEnds, 0);

        int days = Math.Max(0, (int)Math.Ceiling((trialEnds - now).TotalDays));
        return new LicenseSnapshot(LicenseState.Trial, true, "TRIAL",
            $"Período de prueba hasta el {trialEnds:yyyy-MM-dd} ({days} día(s)).",
            days <= 30 ? $"El período de prueba termina el {trialEnds:yyyy-MM-dd} ({days} día(s)). Active su licencia para no interrumpir la operación." : null,
            null, new LicenseFeatureSet(LicensingConstants.TrialFeatures), null, null, null, trialEnds, days);
    }

    /// <summary>
    /// Hora mínima creíble: la mayor entre la hora más alta que vio este
    /// equipo y las fechas FIRMADAS por el servidor de licencias (el .lic más
    /// nuevo aceptado y el instalado). Estado recién creado = sin referencia.
    /// </summary>
    private DateTime ClockFloor()
    {
        var floor = _state.LastSeenClockUtc;
        if (_state.HighestLicenseGeneratedUtc is { } highest && highest > floor) floor = highest;
        if (_payload?.GeneratedAtUtc is { } generated && generated > floor) floor = generated;
        return floor;
    }

    /// <summary>true si el reloj del sistema está más de <see cref="ClockTolerance"/> antes de <see cref="ClockFloor"/>.</summary>
    private bool ClockRolledBack(DateTime now)
    {
        var floor = ClockFloor();
        return floor > DateTime.UnixEpoch && now < floor - ClockTolerance;
    }

    private string ClockMessage() =>
        $"El reloj del servidor está atrasado: este equipo ya registró actividad o licencias del {ClockFloor():yyyy-MM-dd HH:mm} UTC. " +
        "Corrija la fecha y hora del sistema" + (_payload is { IsOffline: false } && Server.IsConfigured
            ? " (si ya está bien, use «Revalidar ahora»: la hora del servidor de licencias lo confirma)."
            : ".");

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

    /// <summary>
    /// Arma el estado combinando las copias válidas (ver <see cref="LicenseStateStore"/>):
    /// lo de la licencia sale de la más nueva; el inicio de la prueba es el más
    /// antiguo de todas, la hora más alta vista la mayor, y una invalidación
    /// recibida no se pierde. Después reescribe las tres copias (repara las que
    /// faltaban o se editaron).
    /// </summary>
    private void Load()
    {
        var copies = _store.ReadStates();
        var marker = _store.ReadMarker();
        bool migrated = false;
        PersistedLicenseState state;
        if (copies.Count > 0)
        {
            state = copies.MaxBy(c => c.State.Revision).State;
            foreach (var (_, copy) in copies) Absorb(state, copy);
        }
        else if (marker is null && _store.ReadLegacy() is { } legacy)
        {
            // Primera vez después de actualizar desde una versión con state.json
            // en claro. Solo si no hay marca: si la hay, el sistema ya pasó por
            // esta versión y un state.json nuevo sería un intento de volver atrás.
            state = legacy;
            migrated = true;
        }
        else
        {
            state = new PersistedLicenseState();
        }

        if (marker is not null)
        {
            state.TrialStartedUtc = LicenseStateStore.Earliest(state.TrialStartedUtc, marker.TrialStartedUtc);
            if (marker.LastSeenClockUtc > state.LastSeenClockUtc) state.LastSeenClockUtc = marker.LastSeenClockUtc;
        }

        // La bitácora: la prueba empezó, a más tardar, con el primer evento
        // registrado. Y si no quedó ninguna copia del estado, la hora más alta
        // vista es, al menos, la del último evento.
        var (firstEvent, lastEvent) = AuditRange();
        if (firstEvent is { } first)
            state.TrialStartedUtc = LicenseStateStore.Earliest(state.TrialStartedUtc, first);
        if (copies.Count == 0 && !migrated && lastEvent is { } last && last > state.LastSeenClockUtc)
            state.LastSeenClockUtc = last;

        var sources = copies.Select(c => c.Source).ToList();
        if (migrated)
            _startupNote = "Estado de licencia migrado al formato cifrado (archivo, registro y base de datos).";
        else if (copies.Count == 0 && (marker is not null || firstEvent is not null))
            _startupNote = "No se encontró ninguna copia del estado de licencia: se reconstruyó desde la marca de prueba y la bitácora " +
                           $"(prueba iniciada el {state.TrialStartedUtc:yyyy-MM-dd}).";
        else if (copies.Count is > 0 and < 3)
            _startupNote = $"Faltaban copias del estado de licencia; se restauraron desde: {string.Join(", ", sources)}.";
        if (_startupNote is not null) _logger.LogWarning("Licencia: {Note}", _startupNote);

        _state = state;
        Save();
        if (migrated) _store.DeleteLegacy();

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
                // Alguien cambió el .lic del disco por uno más antiguo (o por
                // el que el servidor ya invalidó): no se le da por bueno.
                if (RejectOlder(verification.Payload!) is { } older)
                {
                    _tamperMessage = "El archivo de licencia del disco fue reemplazado por uno no válido para este equipo. " + older;
                    _logger.LogError("Licencia: {Message}", _tamperMessage);
                }
                else
                {
                    if (verification.Payload!.GeneratedAtUtc is { } generated
                        && (_state.HighestLicenseGeneratedUtc is null || generated > _state.HighestLicenseGeneratedUtc))
                        _state.HighestLicenseGeneratedUtc = generated;  // instalaciones anteriores a este registro
                    _state.ActivationCode ??= verification.Payload.LicenseKey;  // estado reconstruido sin el código
                    Save();
                }
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

    /// <summary>Suma a <paramref name="target"/> lo más restrictivo de otra copia válida.</summary>
    private static void Absorb(PersistedLicenseState target, PersistedLicenseState other)
    {
        target.TrialStartedUtc = LicenseStateStore.Earliest(target.TrialStartedUtc, other.TrialStartedUtc);
        if (other.LastSeenClockUtc > target.LastSeenClockUtc) target.LastSeenClockUtc = other.LastSeenClockUtc;
        if (other.HighestLicenseGeneratedUtc > (target.HighestLicenseGeneratedUtc ?? DateTime.MinValue))
            target.HighestLicenseGeneratedUtc = other.HighestLicenseGeneratedUtc;
        if (other.RejectedAtUtc > (target.RejectedAtUtc ?? DateTime.MinValue))
        {
            target.RejectedLicenseKey = other.RejectedLicenseKey;
            target.RejectedAtUtc = other.RejectedAtUtc;
        }
    }

    /// <summary>Fecha del primer y del último evento de la bitácora (null si no hay o no se pudo leer).</summary>
    private (DateTime? First, DateTime? Last) AuditRange()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var first = db.AuditEvents.Min(e => (DateTime?)e.Timestamp);
            var last = db.AuditEvents.Max(e => (DateTime?)e.Timestamp);
            return (first, last);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo leer la bitácora para el estado de licencia.");
            return (null, null);
        }
    }

    /// <summary>Guarda el estado en sus tres copias (y la marca de prueba). Nunca lanza.</summary>
    private void Save()
    {
        try
        {
            _state.Revision++;
            _store.Write(_state);
            _store.WriteInstallerHint(_state.ActivationCode ?? _payload?.LicenseKey);
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
