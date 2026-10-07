using System.Net;
using System.Net.Http;
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
///
/// Si el servidor rechaza la sesión (venció, la reemplazó otro login del
/// puesto o la revocó: usuario deshabilitado o eliminado, contraseña o rol
/// cambiados), no se insiste con el mismo token: se renueva la sesión con las
/// credenciales de esta ejecución y, si el servidor tampoco la acepta, se
/// avisa con <see cref="SessionRejected"/> y se deja de intentar.
/// </summary>
public sealed class VmsHubClient : IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly HubConnection _connection;
    private readonly Func<string?, Task<SessionRenewal>> _renewSession;
    private volatile bool _disposed;
    /// <summary>Token con que salió el último intento de conexión: el que el servidor pudo rechazar.</summary>
    private volatile string? _connectionToken;
    /// <summary>La reconexión automática se rindió por un 401 (el Closed llega entonces como "reintentos agotados").</summary>
    private volatile bool _reconnectRejected;

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

    /// <summary>Llegó un evento de control de acceso (paso, rechazo, puerta forzada...).
    /// Por el hub los enums viajan como número; System.Text.Json los lee igual.</summary>
    public event Action<AccessEventDto>? AccessEventReceived;

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

    /// <summary>
    /// El servidor rechazó la sesión de esta conexión y tampoco aceptó
    /// renovarla (usuario deshabilitado o eliminado, contraseña cambiada...):
    /// hay que volver a iniciar sesión. El hub ya no reintenta.
    /// </summary>
    public event Action? SessionRejected;

    /// <param name="renewSession">Renueva la sesión si el token dado sigue
    /// siendo el vigente (<see cref="ApiClient.RenewSessionAsync"/>).</param>
    public VmsHubClient(string baseUrl, Func<string?> tokenProvider, Func<string?, Task<SessionRenewal>> renewSession)
    {
        _renewSession = renewSession;
        _connection = new HubConnectionBuilder()
            .WithUrl($"{baseUrl.TrimEnd('/')}{VmsHubContract.HubPath}", options =>
            {
                // SignalR lo envía como ?access_token=, que el middleware del
                // servidor acepta además del header Bearer.
                options.AccessTokenProvider = () =>
                {
                    string? token = tokenProvider();
                    _connectionToken = token;
                    return Task.FromResult(token);
                };
            })
            .WithAutomaticReconnect(new ReconnectPolicy(() => _reconnectRejected = true))
            .Build();

        _connection.On<string>(VmsHubContract.ConfigChanged, entity => ConfigChanged?.Invoke(entity));
        _connection.On<DeviceDto>(VmsHubContract.DeviceStatusChanged, dto => DeviceStatusChanged?.Invoke(dto));
        _connection.On<WallDto>(VmsHubContract.WallStateChanged, dto => WallStateChanged?.Invoke(dto));
        _connection.On<PlateEventDto>(VmsHubContract.PlateRecognized, dto => PlateRecognized?.Invoke(dto));
        _connection.On<AlarmPanelDto>(VmsHubContract.AlarmPanelStateChanged, dto => AlarmPanelStateChanged?.Invoke(dto));
        _connection.On<AlarmEventDto>(VmsHubContract.AlarmEventReceived, dto => AlarmEventReceived?.Invoke(dto));
        _connection.On<AccessEventDto>(VmsHubContract.AccessEventReceived, dto => AccessEventReceived?.Invoke(dto));
        _connection.On<CercoPanelDto>(VmsHubContract.CercoPanelStateChanged, dto => CercoPanelStateChanged?.Invoke(dto));
        _connection.On<CercoEventDto>(VmsHubContract.CercoEventReceived, dto => CercoEventReceived?.Invoke(dto));
        _connection.On<WorkflowNotificationDto>(VmsHubContract.WorkflowNotification, dto => WorkflowNotification?.Invoke(dto));
        _connection.On<WorkflowAlertDto>(VmsHubContract.WorkflowAlertAcknowledged, dto => WorkflowAlertAcknowledged?.Invoke(dto));
        _connection.On<SpeakerDto>(VmsHubContract.SpeakerStatusChanged, dto => SpeakerStatusChanged?.Invoke(dto));
        _connection.On<IntercomDto>(VmsHubContract.IntercomStatusChanged, dto => IntercomStatusChanged?.Invoke(dto));
        _connection.On<IntercomCallDto>(VmsHubContract.IntercomCallChanged, dto => IntercomCallChanged?.Invoke(dto));

        _connection.Reconnecting += _ => { ConnectionStateChanged?.Invoke(false); return Task.CompletedTask; };
        _connection.Reconnected += _ => { ConnectionStateChanged?.Invoke(true); return Task.CompletedTask; };
        _connection.Closed += async error =>
        {
            ConnectionStateChanged?.Invoke(false);
            if (_disposed) return;
            // Sesión rechazada: el servidor cerró sin error (pidiendo no
            // reconectarse, lo que solo hace cuando la sesión deja de valer) o
            // la reconexión chocó con un 401. Se renueva en el acto: si se
            // puede (sesión vencida o reemplazada, cambio de rol), el tiempo
            // real vuelve enseguida con la sesión nueva.
            bool rejected = error is null || IsUnauthorized(error) || _reconnectRejected;
            _reconnectRejected = false;
            await RestartLoopAsync(sessionRejected: rejected, immediate: rejected);
        };
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        try
        {
            await _connection.StartAsync(ct);
            ConnectionStateChanged?.Invoke(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !_disposed)
        {
            // La reconexión automática solo cubre conexiones ya establecidas:
            // si el primer intento falla, se reintenta aparte (sin apuro, para
            // que la ventana principal alcance a suscribirse a los avisos).
            _ = RestartLoopAsync(sessionRejected: IsUnauthorized(ex), immediate: false);
            throw;
        }
    }

    /// <summary>
    /// Reintenta hasta reconectar, cada 5 s; solo el primer intento tras un
    /// corte del servidor sale en el acto, así que nunca hay un bucle apretado.
    /// Con la sesión rechazada, antes de reintentar se renueva: si el servidor
    /// no la acepta, se avisa (<see cref="SessionRejected"/>) y se deja de intentar.
    /// </summary>
    private async Task RestartLoopAsync(bool sessionRejected, bool immediate)
    {
        while (!_disposed)
        {
            if (!immediate) await Task.Delay(RetryDelay);
            immediate = false;
            if (_disposed) return;

            if (sessionRejected)
            {
                var renewal = await _renewSession(_connectionToken);
                if (renewal == SessionRenewal.Rejected)
                {
                    if (!_disposed) SessionRejected?.Invoke();
                    return;
                }
                if (renewal == SessionRenewal.Unavailable) continue; // sin servidor: más tarde
            }

            try
            {
                await _connection.StartAsync();
                if (_connection.State == HubConnectionState.Connected) ConnectionStateChanged?.Invoke(true);
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                // servidor aún caído: seguir intentando; con 401, renovar antes
                sessionRejected = IsUnauthorized(ex);
            }
        }
    }

    /// <summary>
    /// El middleware de tokens del servidor responde 401 en la ruta del hub
    /// cuando la sesión ya no vale (antes de abrir la conexión).
    /// </summary>
    private static bool IsUnauthorized(Exception? ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsUnauthorized),
        _ => false,
    };

    /// <summary>
    /// Los reintentos por omisión de SignalR (0, 2, 10 y 30 s; después sigue
    /// <see cref="RestartLoopAsync"/>), salvo ante un 401: con el mismo token
    /// no tiene sentido insistir, hay que renovar la sesión.
    /// </summary>
    private sealed class ReconnectPolicy(Action onSessionRejected) : IRetryPolicy
    {
        private static readonly TimeSpan[] Delays =
            [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            if (IsUnauthorized(retryContext.RetryReason))
            {
                onSessionRejected();
                return null;
            }
            return retryContext.PreviousRetryCount < Delays.Length ? Delays[retryContext.PreviousRetryCount] : null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return _connection.DisposeAsync();
    }
}
