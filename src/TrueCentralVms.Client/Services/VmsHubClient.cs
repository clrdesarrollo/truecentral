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

    /// <summary>Cambió una entidad de configuración ("devices" | "channels" | "users" | "decoders" | "walls" | "anpr-sources" | "alarm-panels"): recargar.</summary>
    public event Action<string>? ConfigChanged;

    /// <summary>Cambió el estado en línea de un dispositivo.</summary>
    public event Action<DeviceDto>? DeviceStatusChanged;

    /// <summary>Cambió el estado de un muro de video (otro operador o el panel).</summary>
    public event Action<WallDto>? WallStateChanged;

    /// <summary>Llegó un reconocimiento de patente (módulo Aplicaciones).</summary>
    public event Action<PlateEventDto>? PlateRecognized;

    /// <summary>Cambió el estado de un panel de alarma (conexión, áreas o zonas).</summary>
    public event Action<AlarmPanelDto>? AlarmPanelStateChanged;

    /// <summary>Llegó un evento de un panel de alarma (alarma, armado, falla...).</summary>
    public event Action<AlarmEventDto>? AlarmEventReceived;

    /// <summary>Cambió el estado de un panel de cerco eléctrico (armado, sirena, cerco, conexión).</summary>
    public event Action<CercoPanelDto>? CercoPanelStateChanged;

    /// <summary>Evento empujado por un panel de cerco (caída, alarma de zona, pánico, armado…).</summary>
    public event Action<CercoEventDto>? CercoEventReceived;

    /// <summary>Una automatización pide avisar al operador (puede traer una foto).</summary>
    public event Action<WorkflowNotificationDto>? WorkflowNotification;

    /// <summary>Alguien se dio por enterado de una alerta (se puede bajar de pantalla).</summary>
    public event Action<WorkflowAlertDto>? WorkflowAlertAcknowledged;

    /// <summary>Un parlante IP cambió de estado de conexión.</summary>
    public event Action<SpeakerDto>? SpeakerStatusChanged;

    /// <summary>Un frente de citofonía cambió de estado de conexión.</summary>
    public event Action<IntercomDto>? IntercomStatusChanged;

    /// <summary>Una llamada de citofonía empezó a sonar, fue contestada o terminó.</summary>
    public event Action<IntercomCallDto>? IntercomCallChanged;

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
        _connection.On<WallDto>(VmsHubContract.WallStateChanged, dto => WallStateChanged?.Invoke(dto));
        _connection.On<PlateEventDto>(VmsHubContract.PlateRecognized, dto => PlateRecognized?.Invoke(dto));
        _connection.On<AlarmPanelDto>(VmsHubContract.AlarmPanelStateChanged, dto => AlarmPanelStateChanged?.Invoke(dto));
        _connection.On<AlarmEventDto>(VmsHubContract.AlarmEventReceived, dto => AlarmEventReceived?.Invoke(dto));
        _connection.On<CercoPanelDto>(VmsHubContract.CercoPanelStateChanged, dto => CercoPanelStateChanged?.Invoke(dto));
        _connection.On<CercoEventDto>(VmsHubContract.CercoEventReceived, dto => CercoEventReceived?.Invoke(dto));
        _connection.On<WorkflowNotificationDto>(VmsHubContract.WorkflowNotification, dto => WorkflowNotification?.Invoke(dto));
        _connection.On<WorkflowAlertDto>(VmsHubContract.WorkflowAlertAcknowledged, dto => WorkflowAlertAcknowledged?.Invoke(dto));
        _connection.On<SpeakerDto>(VmsHubContract.SpeakerStatusChanged, dto => SpeakerStatusChanged?.Invoke(dto));
        _connection.On<IntercomDto>(VmsHubContract.IntercomStatusChanged, dto => IntercomStatusChanged?.Invoke(dto));
        _connection.On<IntercomCallDto>(VmsHubContract.IntercomCallChanged, dto => IntercomCallChanged?.Invoke(dto));

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
