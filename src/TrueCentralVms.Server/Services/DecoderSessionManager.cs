using System.Collections.Concurrent;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Mantiene una sesión de driver viva por decodificador y serializa las
/// operaciones contra cada equipo (los decoders no toleran bien comandos
/// concurrentes de la misma sesión).
/// </summary>
public sealed class DecoderSessionManager : IAsyncDisposable
{
    private sealed class Session
    {
        public required IDecoderDriver Driver { get; init; }
        public required SemaphoreSlim Lock { get; init; }
        public required string Fingerprint { get; init; }
    }

    private readonly DecoderDriverRegistry _registry;
    private readonly CredentialProtector _credentials;
    private readonly ILogger<DecoderSessionManager> _logger;
    private readonly ConcurrentDictionary<int, Session> _sessions = new();
    private readonly SemaphoreSlim _createLock = new(1, 1);

    public DecoderSessionManager(DecoderDriverRegistry registry, CredentialProtector credentials,
        ILogger<DecoderSessionManager> logger)
    {
        _registry = registry;
        _credentials = credentials;
        _logger = logger;
    }

    /// <summary>Datos de conexión con la contraseña descifrada al vuelo (nunca se persiste en claro).</summary>
    private DecoderConnectionInfo ConnectionOf(Decoder decoder) => new(
        decoder.Host, decoder.Port, decoder.Username,
        decoder.PasswordCiphertext.Length == 0 ? "" : _credentials.Unprotect(decoder.PasswordCiphertext));

    private string FingerprintOf(Decoder decoder)
    {
        var conn = ConnectionOf(decoder);
        return $"{decoder.DriverKey}|{conn.Host}|{conn.Port}|{conn.Username}|{conn.Password}";
    }

    /// <summary>
    /// Ejecuta una operación contra el decoder con la sesión compartida,
    /// reconectando si la configuración cambió o si aún no hay sesión.
    /// </summary>
    public async Task<T> WithDriverAsync<T>(Decoder decoder, Func<IDecoderDriver, Task<T>> action, CancellationToken ct = default)
    {
        var session = await GetOrCreateSessionAsync(decoder, ct);
        await session.Lock.WaitAsync(ct);
        try
        {
            if (!session.Driver.IsConnected)
                await session.Driver.ConnectAsync(ConnectionOf(decoder), ct);
            return await action(session.Driver);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    public Task WithDriverAsync(Decoder decoder, Func<IDecoderDriver, Task> action, CancellationToken ct = default) =>
        WithDriverAsync(decoder, async d => { await action(d); return true; }, ct);

    private async Task<Session> GetOrCreateSessionAsync(Decoder decoder, CancellationToken ct)
    {
        string fingerprint = FingerprintOf(decoder);
        if (_sessions.TryGetValue(decoder.Id, out var existing) && existing.Fingerprint == fingerprint)
            return existing;

        await _createLock.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(decoder.Id, out existing))
            {
                if (existing.Fingerprint == fingerprint)
                    return existing;

                // La configuración del decoder cambió: cerrar la sesión anterior.
                _sessions.TryRemove(decoder.Id, out _);
                try { await existing.Driver.DisposeAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error cerrando sesión previa del decoder {Id}", decoder.Id); }
            }

            var driver = _registry.Create(decoder.DriverKey);
            var session = new Session
            {
                Driver = driver,
                Lock = new SemaphoreSlim(1, 1),
                Fingerprint = fingerprint,
            };
            _sessions[decoder.Id] = session;
            return session;
        }
        finally
        {
            _createLock.Release();
        }
    }

    /// <summary>Cierra la sesión de un decoder (por ejemplo al eliminarlo o editarlo).</summary>
    public async Task InvalidateAsync(int decoderId)
    {
        if (_sessions.TryRemove(decoderId, out var session))
        {
            try { await session.Driver.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error cerrando sesión del decoder {Id}", decoderId); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList())
            await InvalidateAsync(id);
    }
}
