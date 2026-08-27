using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Hubs;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Cierre de sesiones de streaming: MediaMTX no avisa cuando un lector se
/// desconecta, así que cada 5 s se consulta su API (127.0.0.1) y las sesiones
/// abiertas cuyo identificador ya no aparece se marcan terminadas. Los cambios
/// se publican por SignalR para el dashboard.
/// </summary>
public sealed class SessionAccounting(
    IServiceScopeFactory scopeFactory,
    MediaMtxManager mtx,
    IHubContext<VmsHub> hub,
    ILogger<SessionAccounting> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(8), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Pasada de contabilidad de sesiones fallida (¿MediaMTX reiniciando?).");
            }
            await Task.Delay(Interval, ct);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();

        var open = await db.StreamSessions.Where(s => s.EndedAt == null).ToListAsync(ct);
        if (open.Count == 0) return;

        if (!mtx.IsRunning)
        {
            // Sin media server no puede haber lectores: cerrar todo lo abierto.
            foreach (var s in open) s.EndedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await BroadcastActiveSessionsAsync(db, hub, ct);
            return;
        }

        var liveIds = await GetLiveIdsAsync(ct);
        if (liveIds is null) return; // API inalcanzable: no cerrar nada por un fallo transitorio

        bool changed = false;
        foreach (var session in open)
        {
            // Gracia de 10 s: la sesión recién autorizada puede tardar un
            // instante en aparecer en la lista de la API.
            if (liveIds.Contains(session.MtxSessionId)) continue;
            if (DateTime.UtcNow - session.StartedAt < TimeSpan.FromSeconds(10)) continue;

            session.EndedAt = DateTime.UtcNow;
            changed = true;
            logger.LogInformation("Streaming: {User} dejó de ver {Device} canal {Channel} ({Profile}).",
                session.Username, session.DeviceName, session.RtspChannel, session.Profile);
        }
        if (changed)
        {
            await db.SaveChangesAsync(ct);
            await BroadcastActiveSessionsAsync(db, hub, ct);
        }
    }

    /// <summary>Identificadores vivos según MediaMTX (sesiones y conexiones RTSP).</summary>
    private async Task<HashSet<string>?> GetLiveIdsAsync(CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string endpoint in new[] { "/v3/rtspsessions/list", "/v3/rtspconns/list" })
        {
            try
            {
                using var response = await _http.GetAsync(mtx.ApiBaseUrl + endpoint, ct);
                if (!response.IsSuccessStatusCode) return null;
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (json.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var item in items.EnumerateArray())
                        if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                            ids.Add(id.GetString()!);
            }
            catch
            {
                return null;
            }
        }
        return ids;
    }

    /// <summary>Publica la lista de sesiones activas (compartido con el callback de autorización).</summary>
    public static async Task BroadcastActiveSessionsAsync(VmsDbContext db, IHubContext<VmsHub> hub, CancellationToken ct = default)
    {
        var active = await db.StreamSessions
            .Where(s => s.EndedAt == null)
            .OrderBy(s => s.StartedAt)
            .Select(s => new ActiveSessionDto(s.Id, s.Username, s.DeviceName, s.RtspChannel, s.Profile, s.ClientIp, s.StartedAt))
            .ToListAsync(ct);
        await hub.Clients.All.SendAsync(VmsHubContract.SessionsChanged, active, ct);
    }
}
