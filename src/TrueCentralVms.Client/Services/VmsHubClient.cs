using Microsoft.AspNetCore.SignalR.Client;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.Services;

/// <summary>
/// Conexión SignalR al hub del VMS con reconexión automática. Solo eventos
/// servidor→cliente; los comandos siempre van por la API REST.
/// </summary>
public sealed class VmsHubClient : IAsyncDisposable
{
    private readonly HubConnection _connection;

    /// <summary>Cambió una entidad de configuración ("devices" | "channels" | "users"): recargar.</summary>
    public event Action<string>? ConfigChanged;

    /// <summary>Cambió el estado en línea de un dispositivo.</summary>
    public event Action<DeviceDto>? DeviceStatusChanged;

    /// <summary>true = conectado al hub; false = reconectando/caído.</summary>
    public event Action<bool>? ConnectionStateChanged;

    public VmsHubClient(string baseUrl, string token)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl($"{baseUrl.TrimEnd('/')}{VmsHubContract.HubPath}?access_token={Uri.EscapeDataString(token)}")
            .WithAutomaticReconnect()
            .Build();

        _connection.On<string>(VmsHubContract.ConfigChanged, entity => ConfigChanged?.Invoke(entity));
        _connection.On<DeviceDto>(VmsHubContract.DeviceStatusChanged, dto => DeviceStatusChanged?.Invoke(dto));

        _connection.Reconnecting += _ => { ConnectionStateChanged?.Invoke(false); return Task.CompletedTask; };
        _connection.Reconnected += _ => { ConnectionStateChanged?.Invoke(true); return Task.CompletedTask; };
        _connection.Closed += _ => { ConnectionStateChanged?.Invoke(false); return Task.CompletedTask; };
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _connection.StartAsync(ct);
        ConnectionStateChanged?.Invoke(true);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
