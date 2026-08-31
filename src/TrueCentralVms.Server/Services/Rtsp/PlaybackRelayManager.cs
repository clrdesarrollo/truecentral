using System.Collections.Concurrent;

namespace TrueCentralVms.Server.Services.Rtsp;

/// <summary>
/// Dueño de los relés de reproducción acelerada. Cada concesión con velocidad
/// distinta de 1× abre uno; se cierran al pedir otra velocidad sobre la misma
/// ruta, al envejecer o al apagar el servidor (cada relé sostiene una sesión
/// RTSP viva contra el equipo, y los grabadores limitan cuántas admiten).
/// </summary>
public sealed class PlaybackRelayManager(ILogger<PlaybackRelayManager> logger) : IAsyncDisposable
{
    /// <summary>Un relé no dura más que la reproducción más larga razonable.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<string, (RtspScaleRelay Relay, DateTime Started)> _relays = new();

    public async Task<bool> StartAsync(string path, string deviceUrl, double scale, string publishUrl,
        CancellationToken ct)
    {
        await SweepAsync();
        await StopAsync(path);
        // Cada cambio de velocidad o de posición pide una concesión nueva, con
        // ruta nueva. Sin cerrar la anterior del MISMO canal, cada gesto del
        // operador dejaría viva una sesión de reproducción más contra el
        // grabador, y los equipos admiten unas pocas.
        await StopSiblingsAsync(path);
        try
        {
            var relay = await RtspScaleRelay.StartAsync(deviceUrl, scale, publishUrl, path, logger, ct);
            _relays[path] = (relay, DateTime.UtcNow);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning("No se pudo abrir el relé de reproducción a {Scale}×: {Error}", scale, ex.Message);
            return false;
        }
    }

    public async Task StopAsync(string path)
    {
        if (!_relays.TryRemove(path, out var entry)) return;
        try { await entry.Relay.DisposeAsync(); }
        catch (Exception ex) { logger.LogDebug("Error cerrando el relé '{Path}': {Error}", path, ex.Message); }
    }

    /// <summary>
    /// Cierra los relés del mismo dispositivo y canal. Las rutas se llaman
    /// <c>pb-{dispositivo}-{canal}-{azar}</c>: comparar hasta el último guion
    /// identifica al mismo canal sin arrastrar más estado.
    /// </summary>
    private async Task StopSiblingsAsync(string path)
    {
        int lastDash = path.LastIndexOf('-');
        if (lastDash <= 0) return;
        string prefix = path[..(lastDash + 1)];
        foreach (string other in _relays.Keys)
            if (other != path && other.StartsWith(prefix, StringComparison.Ordinal))
                await StopAsync(other);
    }

    private async Task SweepAsync()
    {
        foreach (var (path, entry) in _relays)
            if (DateTime.UtcNow - entry.Started > MaxAge)
                await StopAsync(path);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (string path in _relays.Keys)
            await StopAsync(path);
    }
}
