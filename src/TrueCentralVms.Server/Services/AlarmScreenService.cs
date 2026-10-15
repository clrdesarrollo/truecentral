using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Api;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services.Workflows;

namespace TrueCentralVms.Server.Services;

/// <summary>Pantalla identificada por su credencial.</summary>
public sealed record ScreenIdentity(int Id, string Name);

/// <summary>Paquete de la app de la pantalla publicado para la actualización remota.</summary>
public sealed record ScreenClientRelease(string Version, string FileName, string FullPath, string Sha256, long Size,
    DateTimeOffset PublishedAt);

/// <summary>
/// Pantallas de alarma de las casetas: las conexiones vivas, la entrega de los
/// avisos, el acuse sincronizado y el autodiscovery por UDP.
///
/// El acuse es UNO SOLO para todos: la alerta de TrueCentral. Confirmarla en
/// una pantalla la cierra en las demás pantallas y en la ventana de alarma de
/// los operadores (y confirmarla en TrueCentral calla las pantallas). La
/// primera confirmación es la que queda, venga de donde venga.
///
/// Regla de oro heredada de la central de alarmas: persistir primero, difundir
/// después. La alerta ya está guardada cuando se difunde, así que una pantalla
/// que reconecta la recupera al resincronizar.
/// </summary>
public sealed partial class AlarmScreenService(
    IHubContext<AlarmScreenHub> hub,
    ScopedHub users,
    IServiceScopeFactory scopeFactory,
    WorkflowStore store,
    AuditService audit,
    IServer server,
    IConfiguration config,
    IHostEnvironment environment,
    ILogger<AlarmScreenService> logger) : BackgroundService
{
    /// <summary>Valor de <see cref="WorkflowAlert.AcknowledgedFrom"/> para lo confirmado en una pantalla.</summary>
    public const string FromScreen = "screen";

    /// <summary>Clave de <see cref="HttpContext.Items"/> con la pantalla que hace el pedido.</summary>
    public const string ItemKey = "alarm-screen";

    /// <summary>Clave de <see cref="HttpContext.Items"/>: la credencial es de una pantalla deshabilitada.</summary>
    public const string DisabledItemKey = "alarm-screen-disabled";

    /// <summary>Una pantalla que se desconecta no se da por caída hasta pasado este margen (reconexiones, reinicios).</summary>
    private static readonly TimeSpan OfflineGrace = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Connection(HubCallerContext Context, int ScreenId, string ScreenName);

    /// <summary>Conexiones vivas del hub de pantallas, por id de conexión (son pocas).</summary>
    private readonly ConcurrentDictionary<string, Connection> _connections = new();
    /// <summary>Pantallas que se desconectaron y aún no se dan por caídas: desde cuándo.</summary>
    private readonly ConcurrentDictionary<int, (DateTime Since, string Name)> _leaving = new();
    /// <summary>Pantallas que se registraron como caídas en la bitácora (para anotar cuando vuelvan).</summary>
    private readonly ConcurrentDictionary<int, string> _down = new();
    /// <summary>Hash de cada sonido, para que la pantalla no vuelva a bajar lo que no cambió.</summary>
    private readonly ConcurrentDictionary<string, (DateTime Stamp, long Size, string Sha)> _soundHashes = new();

    private ScreenClientRelease? _release;
    private DateTime _releaseReadAt;

    /// <summary>Grupo de SignalR con las conexiones de una pantalla.</summary>
    public static string Group(int screenId) => $"screen:{screenId}";

    public bool IsOnline(int screenId) => _connections.Values.Any(c => c.ScreenId == screenId);

    // ------------------------------------------------------------------
    // Credencial
    // ------------------------------------------------------------------

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string NewPairingCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>Compara sin delatar por el tiempo cuántos caracteres coinciden.</summary>
    public static bool SameCode(string? a, string? b) =>
        a is not null && b is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>
    /// La credencial que presenta el pedido: cabecera <see cref="AlarmScreenProtocol.TokenHeader"/>
    /// o, en el hub, <c>?access_token=</c> (el WebSocket no admite cabeceras en el navegador).
    /// </summary>
    public static string? TokenOf(HttpContext ctx)
    {
        string? token = ctx.Request.Headers[AlarmScreenProtocol.TokenHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token)) token = ctx.Request.Query["access_token"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    /// <summary>La pantalla que identificó el middleware en este pedido, si la hay.</summary>
    public static ScreenIdentity? CurrentScreen(HttpContext ctx) => ctx.Items[ItemKey] as ScreenIdentity;

    /// <summary>
    /// Dueña de la credencial: la pantalla si está aprobada, o
    /// <c>Disabled = true</c> si la credencial es buena pero la pantalla está
    /// deshabilitada (la pantalla conserva su credencial y espera, en vez de
    /// descartarla y pedir un emparejamiento nuevo).
    /// </summary>
    public async Task<(ScreenIdentity? Screen, bool Disabled)> ResolveAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200) return (null, false);
        string hash = HashToken(token);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var found = await db.AlarmScreens.AsNoTracking()
            .Where(s => s.TokenHash == hash)
            .Select(s => new { s.Id, s.Name, s.Status })
            .FirstOrDefaultAsync(ct);
        return found switch
        {
            { Status: AlarmScreenStatus.Approved } => (new ScreenIdentity(found.Id, found.Name), false),
            { Status: AlarmScreenStatus.Disabled } => (null, true),
            _ => (null, false),
        };
    }

    // ------------------------------------------------------------------
    // Conexiones
    // ------------------------------------------------------------------

    public async Task ConnectedAsync(HubCallerContext context, ScreenIdentity screen, string? ip)
    {
        bool wasOnline = IsOnline(screen.Id);
        _connections[context.ConnectionId] = new Connection(context, screen.Id, screen.Name);
        _leaving.TryRemove(screen.Id, out _);
        await TouchAsync(screen.Id, ip);
        logger.LogInformation("Pantalla de alarma «{Name}» conectada desde {Ip}.", screen.Name, ip);
        if (wasOnline) return;

        // Volvió después de haberse dado por caída: queda la constancia de
        // cuánto tiempo esa caseta estuvo sin recibir avisos.
        if (_down.TryRemove(screen.Id, out _))
            await audit.LogSystemAsync("screens", "screen-online", targetType: "alarm-screen",
                targetId: screen.Id.ToString(), targetName: screen.Name,
                detail: $"La pantalla de alarma «{screen.Name}» volvió a conectarse desde {ip}.",
                username: ScreenActor(screen.Name), clientIp: ip, origin: FromScreen);
        await NotifyPanelAsync();
    }

    public async Task DisconnectedAsync(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var connection)) return;
        if (IsOnline(connection.ScreenId)) return;
        _leaving[connection.ScreenId] = (DateTime.UtcNow, connection.ScreenName);
        logger.LogInformation("Pantalla de alarma «{Name}» desconectada.", connection.ScreenName);
        await NotifyPanelAsync();
    }

    /// <summary>Corta las conexiones de una pantalla (deshabilitada, reemparejada o eliminada).</summary>
    public int Disconnect(int screenId, string reason)
    {
        int count = 0;
        foreach (var (id, connection) in _connections)
        {
            if (connection.ScreenId != screenId || !_connections.TryRemove(id, out _)) continue;
            try
            {
                connection.Context.Abort();
                count++;
                logger.LogInformation("Pantalla «{Name}»: conexión cortada ({Reason}).", connection.ScreenName, reason);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No se pudo cortar la conexión de la pantalla «{Name}».", connection.ScreenName);
            }
        }
        // Se la quitaron a propósito: no es una caída que haya que anotar.
        _leaving.TryRemove(screenId, out _);
        _down.TryRemove(screenId, out _);
        return count;
    }

    /// <summary>Señal de vida: última vez vista y desde qué dirección.</summary>
    public async Task TouchAsync(int screenId, string? ip)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var now = DateTime.UtcNow;
            string? lastIp = string.IsNullOrWhiteSpace(ip) ? null : Cut(ip, 64);
            await db.AlarmScreens.Where(s => s.Id == screenId).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.LastSeenAt, now)
                .SetProperty(s => s.LastIp, s => lastIp ?? s.LastIp));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No se pudo anotar la señal de vida de la pantalla {Id}.", screenId);
        }
    }

    /// <summary>Avisa al panel (a quien configura pantallas) que relea la lista.</summary>
    public Task NotifyPanelAsync() =>
        users.ToPermittedAsync(Permissions.ScreensConfigure, VmsHubContract.AlarmScreensChanged, null);

    /// <summary>Pide a una pantalla (o a todas) que recargue su configuración.</summary>
    public Task ConfigChangedAsync(int? screenId = null, CancellationToken ct = default) => screenId is { } id
        ? hub.Clients.Group(Group(id)).SendAsync(AlarmScreenProtocol.ConfigChanged, ct)
        : hub.Clients.All.SendAsync(AlarmScreenProtocol.ConfigChanged, ct);

    public Task RebootAsync(int screenId, CancellationToken ct = default) =>
        hub.Clients.Group(Group(screenId)).SendAsync(AlarmScreenProtocol.Reboot, ct);

    // ------------------------------------------------------------------
    // Avisos
    // ------------------------------------------------------------------

    /// <summary>¿El aviso va a esa pantalla?</summary>
    public static bool Targets(WorkflowAlert alert, int screenId) =>
        alert.ShowOnScreens && (alert.ScreenIds is null || alert.ScreenIds.Contains($",{screenId},"));

    /// <summary>Ids de las pantallas de ",1,4,".</summary>
    public static List<int> ParseIds(string? ids) =>
        (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => int.TryParse(x, out int id) ? id : 0).Where(id => id > 0).Distinct().ToList();

    /// <summary>Las conexiones a las que va un aviso: todas, o los grupos de sus pantallas.</summary>
    private IClientProxy Audience(WorkflowAlert alert) => alert.ScreenIds is null
        ? hub.Clients.All
        : hub.Clients.Groups(ParseIds(alert.ScreenIds).Select(Group).ToList());

    /// <summary>Difunde un aviso nuevo a sus pantallas (ya guardado: persistir primero, difundir después).</summary>
    public async Task PublishRaisedAsync(WorkflowAlert alert, CancellationToken ct = default)
    {
        if (!alert.ShowOnScreens) return;
        try
        {
            await Audience(alert).SendAsync(AlarmScreenProtocol.AlarmRaised, ToScreenDto(alert), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // La alerta ya está guardada: la pantalla la recupera al resincronizar.
            logger.LogWarning(ex, "No se pudo difundir la alerta {Id} a las pantallas de alarma.", alert.Id);
        }
    }

    /// <summary>Alguien confirmó el aviso (en TrueCentral u otra pantalla): las pantallas lo cierran.</summary>
    public async Task PublishAcknowledgedAsync(WorkflowAlert alert, CancellationToken ct = default)
    {
        if (!alert.ShowOnScreens || alert.AcknowledgedAt is not { } at) return;
        try
        {
            await Audience(alert).SendAsync(AlarmScreenProtocol.AlarmAcknowledged,
                new AlarmScreenAckDto(alert.Id, alert.AcknowledgedBy, alert.AcknowledgedFrom, AsUtc(at)), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "No se pudo avisar a las pantallas que la alerta {Id} fue confirmada.", alert.Id);
        }
    }

    /// <summary>
    /// El guardia marcó el aviso como visto en una pantalla. La primera
    /// confirmación gana (aquí, en otra pantalla o en TrueCentral); si ya
    /// estaba confirmada, igual se reenvía el estado para que la pantalla
    /// quede al día.
    /// </summary>
    public async Task AcknowledgeFromScreenAsync(long alertId, int screenId, string? ip, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        // El nombre y el estado se leen ahora: la pantalla pudo cambiar de
        // nombre o quedar deshabilitada desde que se conectó.
        var screen = await db.AlarmScreens.AsNoTracking()
            .Where(s => s.Id == screenId && s.Status == AlarmScreenStatus.Approved)
            .Select(s => new ScreenIdentity(s.Id, s.Name)).FirstOrDefaultAsync(ct);
        if (screen is null) return;

        var alert = await db.WorkflowAlerts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == alertId, ct);
        if (alert is null || !Targets(alert, screen.Id)) return;

        if (alert.AcknowledgedAt is null)
        {
            var now = TruncatedNow();
            string by = Cut(screen.Name, 64);
            string? from = string.IsNullOrWhiteSpace(ip) ? null : Cut(ip, 64);
            int updated = await db.WorkflowAlerts
                .Where(a => a.Id == alertId && a.AcknowledgedAt == null)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(a => a.AcknowledgedAt, now)
                    .SetProperty(a => a.AcknowledgedBy, by)
                    .SetProperty(a => a.AcknowledgedFrom, FromScreen)
                    .SetProperty(a => a.AcknowledgedScreenId, screen.Id)
                    .SetProperty(a => a.AcknowledgedIp, from), ct);
            alert = await db.WorkflowAlerts.AsNoTracking().FirstAsync(a => a.Id == alertId, ct);
            if (updated > 0)
            {
                await TellOperatorsAsync(alert, ct);
                var dto = WorkflowMapper.ToDto(alert);
                await audit.LogSystemAsync("workflows", "alert-acknowledged",
                    targetType: "workflow-alert", targetId: alert.Id.ToString(), targetName: alert.WorkflowName,
                    detail: $"La pantalla de alarma «{screen.Name}» marcó como vista la alerta «{alert.Title}» " +
                            $"({alert.TriggerSummary}), emitida a las {alert.RaisedAt.ToLocalTime():dd-MM-yyyy HH:mm:ss}, " +
                            $"{dto.ResponseSeconds} s después de emitida.",
                    data: new { alert.Id, alert.WorkflowId, alert.RaisedAt, dto.ResponseSeconds, alert.TriggerSummary, ScreenId = screen.Id },
                    username: ScreenActor(screen.Name), clientIp: ip, origin: FromScreen);
            }
        }
        await PublishAcknowledgedAsync(alert, ct);
    }

    /// <summary>«Todas vistas» en una pantalla: confirma los avisos pendientes que le llegaron a ella.</summary>
    public async Task AcknowledgeAllFromScreenAsync(int screenId, string? ip, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var screen = await db.AlarmScreens.AsNoTracking()
            .Where(s => s.Id == screenId && s.Status == AlarmScreenStatus.Approved)
            .Select(s => new ScreenIdentity(s.Id, s.Name)).FirstOrDefaultAsync(ct);
        if (screen is null) return;

        var settings = await SettingsAsync(db, ct);
        var since = DateTime.UtcNow.AddHours(-Math.Clamp(settings.ResyncHours, 1, 168));
        string me = $",{screen.Id},";
        var pending = await db.WorkflowAlerts.AsNoTracking()
            .Where(a => a.ShowOnScreens && a.AcknowledgedAt == null && a.RaisedAt >= since &&
                        (a.ScreenIds == null || a.ScreenIds.Contains(me)))
            .Select(a => a.Id).ToListAsync(ct);
        if (pending.Count == 0) return;

        var now = TruncatedNow();
        string by = Cut(screen.Name, 64);
        string? from = string.IsNullOrWhiteSpace(ip) ? null : Cut(ip, 64);
        await db.WorkflowAlerts
            .Where(a => pending.Contains(a.Id) && a.AcknowledgedAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(a => a.AcknowledgedAt, now)
                .SetProperty(a => a.AcknowledgedBy, by)
                .SetProperty(a => a.AcknowledgedFrom, FromScreen)
                .SetProperty(a => a.AcknowledgedScreenId, screen.Id)
                .SetProperty(a => a.AcknowledgedIp, from), ct);

        // Las que confirmó ESTA orden (otra pudo adelantarse con alguna).
        var mine = await db.WorkflowAlerts.AsNoTracking()
            .Where(a => pending.Contains(a.Id) && a.AcknowledgedScreenId == screen.Id && a.AcknowledgedAt == now)
            .ToListAsync(ct);
        foreach (var alert in mine) await TellOperatorsAsync(alert, ct);

        try
        {
            await hub.Clients.All.SendAsync(AlarmScreenProtocol.AlarmsBulkAcknowledged,
                new AlarmScreenBulkAckDto(pending, screen.Name, FromScreen, AsUtc(now)), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "No se pudo difundir el «todas vistas» de la pantalla «{Name}».", screen.Name);
        }

        if (mine.Count > 0)
            await audit.LogSystemAsync("screens", "screen-acknowledged-all", targetType: "alarm-screen",
                targetId: screen.Id.ToString(), targetName: screen.Name,
                detail: $"La pantalla de alarma «{screen.Name}» marcó como vistas {mine.Count} alerta(s) pendiente(s) " +
                        $"con «Todas vistas»: {string.Join("; ", mine.Take(20).Select(a => $"«{a.Title}» ({a.RaisedAt.ToLocalTime():HH:mm:ss})"))}" +
                        (mine.Count > 20 ? "…" : "."),
                data: new { AlertIds = mine.Select(a => a.Id).ToList() },
                username: ScreenActor(screen.Name), clientIp: ip, origin: FromScreen);
    }

    /// <summary>
    /// Los operadores de TrueCentral que recibieron la alerta la bajan de su
    /// ventana de alarma (el cliente la saca y cierra la ventana si no queda
    /// otra pendiente).
    /// </summary>
    private async Task TellOperatorsAsync(WorkflowAlert alert, CancellationToken ct)
    {
        try
        {
            await users.SendToAsync(VmsHubContract.WorkflowAlertAcknowledged, WorkflowMapper.ToDto(alert),
                s => WorkflowsApi.ReceivesAlert(s, alert.RecipientUserIds, alert.ResourceKey), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "No se pudo avisar a los operadores que la alerta {Id} se confirmó en una pantalla.", alert.Id);
        }
    }

    /// <summary>Constancia de que la pantalla recibió esos avisos (la primera entrega es la que vale).</summary>
    public async Task ConfirmDeliveriesAsync(int screenId, IReadOnlyCollection<long> alertIds, CancellationToken ct = default)
    {
        var ids = alertIds.Where(id => id > 0).Distinct().Take(500).ToArray();
        if (ids.Length == 0) return;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var now = DateTime.UtcNow;
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "AlarmScreenDeliveries" ("AlertId", "ScreenId", "DeliveredAt")
                SELECT a."Id", {screenId}, {now} FROM "WorkflowAlerts" a
                WHERE a."Id" = ANY({ids}) AND a."ShowOnScreens"
                ON CONFLICT ("AlertId", "ScreenId") DO NOTHING
                """, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "No se pudo anotar la entrega de avisos a la pantalla {Id}.", screenId);
        }
    }

    /// <summary>
    /// Avisos recientes de una pantalla, con su estado. Es lo que pide al
    /// conectarse y al reconectarse: una caseta que perdió la red no puede
    /// quedarse con el estado viejo en pantalla.
    /// </summary>
    public async Task<List<AlarmScreenAlertDto>> RecentAsync(int screenId, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var settings = await SettingsAsync(db, ct);
        var since = DateTime.UtcNow.AddHours(-Math.Clamp(settings.ResyncHours, 1, 168));
        string me = $",{screenId},";
        var alerts = await db.WorkflowAlerts.AsNoTracking()
            .Where(a => a.ShowOnScreens && a.RaisedAt >= since && (a.ScreenIds == null || a.ScreenIds.Contains(me)))
            .OrderByDescending(a => a.RaisedAt)
            .Take(200)
            .ToListAsync(ct);
        return alerts.Select(ToScreenDto).ToList();
    }

    /// <summary>Avisos dirigidos a la pantalla que siguen sin confirmar (para el panel).</summary>
    public static async Task<Dictionary<int, int>> PendingByScreenAsync(VmsDbContext db, IReadOnlyCollection<int> screenIds,
        int resyncHours, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddHours(-Math.Clamp(resyncHours, 1, 168));
        var pending = await db.WorkflowAlerts.AsNoTracking()
            .Where(a => a.ShowOnScreens && a.AcknowledgedAt == null && a.RaisedAt >= since)
            .Select(a => a.ScreenIds).ToListAsync(ct);
        return screenIds.ToDictionary(id => id,
            id => pending.Count(ids => ids is null || ids.Contains($",{id},")));
    }

    public static AlarmScreenAlertDto ToScreenDto(WorkflowAlert alert) => new()
    {
        Id = alert.Id,
        Title = alert.Title,
        Message = alert.Message,
        LocationPath = alert.LocationPath,
        Instructions = alert.Instructions,
        WorkflowName = alert.WorkflowName,
        Priority = alert.Severity switch
        {
            AlarmSeverity.Critical => 1,
            AlarmSeverity.Warning => 2,
            _ => 3,
        },
        OccurredAt = AsUtc(alert.RaisedAt),
        PhotoUrl = alert.ImagePath is { Length: > 0 } ? $"/api/screen/alerts/{alert.Id}/photo" : null,
        // El pitido del sistema es del equipo del operador: la pantalla usa su sonido general.
        SoundFile = alert.Sound is { Length: > 0 } sound && sound != WorkflowNotificationDto.SystemSoundName ? sound : null,
        Acknowledged = alert.AcknowledgedAt is not null,
        AcknowledgedAt = alert.AcknowledgedAt is { } at ? AsUtc(at) : null,
        AcknowledgedBy = alert.AcknowledgedBy,
        AcknowledgedFrom = alert.AcknowledgedFrom,
    };

    // ------------------------------------------------------------------
    // Configuración, sonidos y actualización remota
    // ------------------------------------------------------------------

    public static async Task<AlarmScreenSettings> SettingsAsync(VmsDbContext db, CancellationToken ct) =>
        await db.AlarmScreenSettings.AsNoTracking().OrderBy(x => x.Id).FirstOrDefaultAsync(ct) ?? new AlarmScreenSettings();

    /// <summary>Nombre de la instalación que muestran las pantallas.</summary>
    public static string SiteNameOf(AlarmScreenSettings settings) =>
        string.IsNullOrWhiteSpace(settings.SiteName) ? Environment.MachineName : settings.SiteName.Trim();

    /// <summary>
    /// Los sonidos de la biblioteca de automatizaciones (los mismos de los
    /// parlantes y de la ventana de alarma), con su hash: la pantalla los
    /// guarda por hash y no vuelve a bajar lo que no cambió.
    /// </summary>
    public IReadOnlyList<AlarmScreenSoundDto> Sounds()
    {
        var result = new List<AlarmScreenSoundDto>();
        foreach (var audio in store.ListAudio())
        {
            if (store.FindAudio(audio.DisplayName) is not { } path) continue;
            try
            {
                var info = new FileInfo(path);
                var cached = _soundHashes.TryGetValue(path, out var known) &&
                             known.Stamp == info.LastWriteTimeUtc && known.Size == info.Length
                    ? known.Sha
                    : null;
                if (cached is null)
                {
                    using var stream = File.OpenRead(path);
                    cached = Convert.ToHexStringLower(SHA256.HashData(stream));
                    _soundHashes[path] = (info.LastWriteTimeUtc, info.Length, cached);
                }
                result.Add(new AlarmScreenSoundDto
                {
                    Name = audio.DisplayName,
                    Extension = info.Extension.ToLowerInvariant(),
                    Url = $"/api/screen/sounds/{Uri.EscapeDataString(audio.DisplayName)}",
                    Sha256 = cached,
                    Size = info.Length,
                });
            }
            catch (Exception ex)
            {
                // Un sonido que no se puede leer no deja sin los demás.
                logger.LogDebug(ex, "No se pudo leer el sonido '{Name}' para las pantallas.", audio.DisplayName);
            }
        }
        return result;
    }

    /// <summary>Carpeta donde se publica la app de la pantalla para la actualización remota.</summary>
    public string PackageDirectory => config["AlarmScreens:PackageDirectory"] is { Length: > 0 } directory
        ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory))
        : Path.Combine(environment.ContentRootPath, "screen-packages");

    [GeneratedRegex(@"^truecentral-screen-(?<version>\d+\.\d+\.\d+)-linux-arm64\.tar\.gz$", RegexOptions.IgnoreCase)]
    private static partial Regex PackagePattern();

    /// <summary>El paquete más nuevo publicado, o null si no hay ninguno.</summary>
    public ScreenClientRelease? LatestRelease()
    {
        // El hash de un paquete de ~100 MB no se recalcula en cada consulta de cada pantalla.
        if (_release is not null && DateTime.UtcNow - _releaseReadAt < TimeSpan.FromMinutes(5)) return _release;
        try
        {
            string root = PackageDirectory;
            if (!Directory.Exists(root)) return null;
            var best = Directory.EnumerateFiles(root, "truecentral-screen-*.tar.gz")
                .Select(path => (Path: path, Match: PackagePattern().Match(Path.GetFileName(path))))
                .Where(x => x.Match.Success)
                .Select(x => (x.Path, Version: Version.Parse(x.Match.Groups["version"].Value)))
                .OrderByDescending(x => x.Version)
                .FirstOrDefault();
            if (best.Path is null) return null;
            var info = new FileInfo(best.Path);
            using var stream = File.OpenRead(best.Path);
            _release = new ScreenClientRelease(best.Version.ToString(3), info.Name, info.FullName,
                Convert.ToHexStringLower(SHA256.HashData(stream)), info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            _releaseReadAt = DateTime.UtcNow;
            return _release;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo leer la carpeta de paquetes de las pantallas ({Root}).", PackageDirectory);
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Servicio de fondo: autodiscovery y caídas
    // ------------------------------------------------------------------

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(DiscoveryLoopAsync(stoppingToken), OfflineLoopAsync(stoppingToken));

    /// <summary>
    /// Una pantalla desconectada por más de <see cref="OfflineGrace"/> se da
    /// por caída: queda en la bitácora, porque mientras tanto esa caseta no
    /// recibe avisos y es lo primero que se pregunta después de un incidente.
    /// </summary>
    private async Task OfflineLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) { return; }

            foreach (var (screenId, leaving) in _leaving)
            {
                if (DateTime.UtcNow - leaving.Since < OfflineGrace || IsOnline(screenId)) continue;
                if (!_leaving.TryRemove(screenId, out _)) continue;
                _down[screenId] = leaving.Name;
                await audit.LogSystemAsync("screens", "screen-offline", targetType: "alarm-screen",
                    targetId: screenId.ToString(), targetName: leaving.Name,
                    detail: $"La pantalla de alarma «{leaving.Name}» lleva más de {OfflineGrace.TotalSeconds:0} s " +
                            $"sin conexión (desde las {leaving.Since.ToLocalTime():HH:mm:ss}): no está recibiendo avisos.",
                    success: false);
                await NotifyPanelAsync();
            }
        }
    }

    /// <summary>
    /// Responde a los <see cref="AlarmScreenProtocol.DiscoveryProbe"/> que
    /// emiten las pantallas recién instaladas para encontrar el servidor solas.
    /// El broadcast no cruza VLANs: la pantalla siempre deja escribir la
    /// dirección a mano.
    /// </summary>
    private async Task DiscoveryLoopAsync(CancellationToken ct)
    {
        if (!config.GetValue("AlarmScreens:DiscoveryEnabled", true))
        {
            logger.LogInformation("Pantallas de alarma: autodiscovery desactivado por configuración.");
            return;
        }
        int port = config.GetValue("AlarmScreens:DiscoveryPort", AlarmScreenProtocol.DiscoveryPort);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            udp.EnableBroadcast = true;
        }
        catch (SocketException ex)
        {
            // Otro proceso tiene el puerto: las pantallas igual se configuran con la dirección a mano.
            logger.LogWarning(ex, "Pantallas de alarma: no se pudo abrir el puerto UDP {Port} para el autodiscovery.", port);
            return;
        }
        logger.LogInformation("Pantallas de alarma: autodiscovery escuchando en UDP {Port}.", port);

        string serverId = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{Environment.MachineName}|{environment.ContentRootPath}")))[..16];
        string version = typeof(AlarmScreenService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var received = await udp.ReceiveAsync(ct);
                if (!Encoding.UTF8.GetString(received.Buffer).Contains(AlarmScreenProtocol.DiscoveryProbe, StringComparison.Ordinal))
                    continue;
                string siteName;
                using (var scope = scopeFactory.CreateScope())
                    siteName = SiteNameOf(await SettingsAsync(scope.ServiceProvider.GetRequiredService<VmsDbContext>(), ct));
                var response = new AlarmScreenDiscoveryResponse
                {
                    Name = siteName,
                    Url = BaseUrlFor(received.RemoteEndPoint.Address),
                    Version = version,
                    ServerId = serverId,
                };
                await udp.SendAsync(JsonSerializer.SerializeToUtf8Bytes(response, Json), received.RemoteEndPoint, ct);
                logger.LogDebug("Pantallas de alarma: autodiscovery respondido a {Remote}.", received.RemoteEndPoint);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Pantallas de alarma: falló una respuesta de autodiscovery.");
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>URL que se anuncia: la configurada, o la IP local por la que se llega a esa pantalla.</summary>
    private string BaseUrlFor(IPAddress client)
    {
        if (config["AlarmScreens:PublicUrl"] is { Length: > 0 } publicUrl) return publicUrl.TrimEnd('/');
        return $"http://{LocalAddressTowards(client)}:{ListeningPort()}";
    }

    /// <summary>
    /// IP local por la que este equipo alcanza a la pantalla: un socket UDP
    /// "conectado" no envía nada pero resuelve la ruta (con varias tarjetas de
    /// red es la única forma de anunciar la IP correcta).
    /// </summary>
    private static string LocalAddressTowards(IPAddress client)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(client, 65530));
            return (probe.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    private int ListeningPort()
    {
        foreach (string address in server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [])
            if (Uri.TryCreate(address.Replace("*", "localhost").Replace("+", "localhost").Replace("[::]", "localhost"),
                    UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp)
                return uri.Port;
        return 5090;
    }

    // ------------------------------------------------------------------
    // Apoyo
    // ------------------------------------------------------------------

    /// <summary>Actor de la bitácora para lo que hace una pantalla (no hay persona con sesión).</summary>
    public static string ScreenActor(string screenName) => Cut($"pantalla {screenName}", 64);

    /// <summary>Hora actual truncada a microsegundos, como la guarda PostgreSQL (para poder compararla).</summary>
    private static DateTime TruncatedNow()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
    }

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, value.Kind == DateTimeKind.Local ? DateTimeKind.Local : DateTimeKind.Utc));

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
