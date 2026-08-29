using Microsoft.AspNetCore.SignalR.Client;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.Services;

/// <summary>
/// Conexión SignalR al hub del VMS con reconexión automática. Solo eventos
/// servidor→cliente; los comandos siempre van por la API REST.
///
/// El token se consulta en cada intento de (re)conexión: tras un reinicio
/// del servidor el ApiClient renueva la sesión y la próxima reconexión ya
/// sale con el token nuevo. Y cuando la reconexión automática de SignalR se
/// rinde (~40 s de servidor caído), se sigue reintentando cada 5 s hasta que
/// vuelva o se cierre la aplicación.
/// </summary>
public sealed class VmsHubClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private volatile bool _disposed;

    /// <summary>Cambió una entidad de configuración ("devices" | "channels" | "users"): recargar.</summary>
    public event Action<string>? ConfigChanged;

    /// <summary>Cambió el estado en línea de un dispositivo.</summary>
    public event Action<DeviceDto>? DeviceStatusChanged;

    /// <summary>true = conectado al hub; false = reconectando/caído.</summary>
    public event Action<bool>? ConnectionStateChanged;

    public VmsHubClient(string baseUrl, Func<string?> tokenProvider)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl($"{baseUrl.TrimEnd('/')}{VmsHubContract.HubPath}", options =>
            {
                // SignalR lo envía como ?access_token=, que el middleware del
                // servidor acepta además del header Bearer.
                options.AccessTokenProvider = () => Task.FromResult(tokenProvider());
            })
            .WithAutomaticReconnect()
            .Build();

        _connection.On<string>(VmsHubContract.ConfigChanged, entity => ConfigChanged?.Invoke(entity));
        _connection.On<DeviceDto>(VmsHubContract.DeviceStatusChanged, dto => DeviceStatusChanged?.Invoke(dto));

        _connection.Reconnecting += _ => { ConnectionStateChanged?.Invoke(false); return Task.CompletedTask; };
        _connection.Reconnected += _ => { ConnectionStateChanged?.Invoke(true); return Task.CompletedTask; };
        _connection.Closed += async _ =>
        {
            ConnectionStateChanged?.Invoke(false);
            await RestartLoopAsync();
        };
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _connection.StartAsync(ct);
        ConnectionStateChanged?.Invoke(true);
    }

    private async Task RestartLoopAsync()
    {
        while (!_disposed)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            try
            {
                await _connection.StartAsync();
                ConnectionStateChanged?.Invoke(true);
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch
            {
                // servidor aún caído: seguir intentando
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return _connection.DisposeAsync();
    }
}
