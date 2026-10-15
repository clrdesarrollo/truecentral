using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TrueCentralVms.AlarmScreen.Models;
using TrueCentralVms.Core.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace TrueCentralVms.AlarmScreen.Services;

public enum LinkState
{
    /// <summary>Buscando el servidor por autodiscovery.</summary>
    Searching,
    /// <summary>Esperando que el administrador apruebe esta pantalla.</summary>
    Pairing,
    Connecting,
    Online,
    Offline,
    /// <summary>El equipo quedó sin salida: rechazado, deshabilitado, o emparejado con una credencial que ya no tiene.</summary>
    Blocked
}

/// <summary>
/// Todo el trato con el servidor de TrueCentral: autodiscovery, emparejamiento, REST y
/// SignalR. La pantalla no guarda historial; solo consume esto y pinta.
/// </summary>
public sealed class ServerLink(ClientConfig config) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Lo que dice la pantalla cuando no hay nada que contar.</summary>
    private const string Connected = "Conectada";

    private HubConnection? _hub;
    private HttpClient? _http;

    public LinkState State { get; private set; } = LinkState.Searching;
    public string StatusText { get; private set; } = "Iniciando…";
    public string PairingCode { get; private set; } = "";

    public AlarmScreenSessionDto? Session { get; private set; }

    public event Action? Changed;
    public event Action<AlarmScreenAlertDto>? AlarmRaised;
    public event Action<AlarmScreenAlertDto>? AlarmUpdated;
    public event Action<AlarmScreenAckDto>? Acknowledged;
    public event Action<AlarmScreenBulkAckDto>? BulkAcknowledged;
    public event Action? Resynced;

    /// <summary>El administrador cambió sonidos, volumen, disposición o el nombre de la pantalla.</summary>
    public event Action? ConfigChanged;

    /// <summary>El administrador pidió reiniciar este equipo desde el panel.</summary>
    public event Action? RebootRequested;

    /// <summary>Fuerza una URL escrita a mano (el broadcast no cruza VLANs).</summary>
    public void UseServer(string url)
    {
        config.ServerUrl = url.TrimEnd('/');
        ConfigService.Save(config);
    }

    // ---------- bucle de vida ----------

    /// <summary>
    /// Lleva la caseta desde "recién encendida" hasta "conectada", y la mantiene ahí. Nunca
    /// termina por su cuenta: una caseta no puede quedarse esperando a que alguien la atienda
    /// después de un corte de luz.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await EnsureServerAsync(ct)) continue;
                if (!await EnsurePairedAsync(ct)) continue;
                await ConnectAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _failedAttempts++;
                Report(LinkState.Offline, "Sin conexión con el servidor: " + ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    /// <summary>Tras cuántos intentos fallidos vale la pena preguntarle a la red dónde está el servidor.</summary>
    private const int AttemptsBeforeRediscovering = 3;
    private int _failedAttempts;

    private async Task<bool> EnsureServerAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.ServerUrl))
            return await DiscoverAsync(ct, "Buscando el servidor en la red…");

        // La dirección guardada dejó de responder: puede que el servidor cambiara de IP o de
        // puerto. Se vuelve a preguntar por broadcast, pero solo se cambia si alguien contesta;
        // un servidor caído no debe hacernos perder la dirección buena.
        if (_failedAttempts >= AttemptsBeforeRediscovering)
        {
            _failedAttempts = 0;
            var known = config.ServerUrl;
            var servers = await DiscoveryClient.FindAsync(TimeSpan.FromSeconds(3), ct);
            var other = servers.FirstOrDefault(s =>
                !string.Equals(s.Url, known, StringComparison.OrdinalIgnoreCase));

            if (other is not null)
            {
                UseServer(other.Url);
                Report(LinkState.Searching, $"El servidor cambió de dirección: {other.Url}");
            }
        }
        return true;
    }

    private async Task<bool> DiscoverAsync(CancellationToken ct, string message)
    {
        Report(LinkState.Searching, message);
        var servers = await DiscoveryClient.FindAsync(TimeSpan.FromSeconds(3), ct);
        if (servers.Count == 0)
        {
            Report(LinkState.Searching, "No se encontró servidor. Reintentando…");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return false;
        }

        var server = servers[0];
        UseServer(server.Url);
        Report(LinkState.Searching, $"Servidor encontrado: {server.Name} ({server.Url})");
        return true;
    }

    private async Task<bool> EnsurePairedAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(config.DeviceToken)) return true;

        using var http = PlainClient();
        var start = await http.PostAsJsonAsync("/api/screen/pair",
            new AlarmScreenPairStartRequest(config.MachineId, Environment.MachineName, Version()), Json, ct);

        if (!start.IsSuccessStatusCode)
        {
            _failedAttempts++;
            Report(LinkState.Offline, $"El servidor rechazó el registro (HTTP {(int)start.StatusCode}).");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return false;
        }

        var started = await start.Content.ReadFromJsonAsync<AlarmScreenPairStartResponse>(Json, ct);
        if (started is null) return false;

        if (started.PairingCode.Length == 0)
        {
            Report(LinkState.Blocked, started.Status switch
            {
                AlarmScreenStatus.Rejected => "El administrador rechazó este equipo como pantalla de alarma.",
                AlarmScreenStatus.Disabled => "Esta pantalla está deshabilitada desde el panel de TrueCentral.",
                // el servidor ya entregó la credencial de este equipo y aquí no está: solo el administrador la reinicia
                _ => "Este equipo ya figura emparejado pero perdió su credencial. " +
                     "Pida al administrador «Reiniciar emparejamiento» en Pantallas de alarma.",
            });
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
            return false;
        }

        PairingCode = started.PairingCode;
        Report(LinkState.Pairing, "Esperando aprobación del administrador.");

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);

            var poll = await http.GetAsync(
                $"/api/screen/pair/{Uri.EscapeDataString(config.MachineId)}?code={PairingCode}", ct);
            if (!poll.IsSuccessStatusCode) continue;

            var status = await poll.Content.ReadFromJsonAsync<AlarmScreenPairStatusResponse>(Json, ct);
            if (status is null) continue;

            if (status.Status is AlarmScreenStatus.Rejected or AlarmScreenStatus.Disabled)
            {
                Report(LinkState.Blocked, "El administrador rechazó este dispositivo.");
                return false;
            }
            if (status.Token is not { Length: > 0 }) continue;

            config.DeviceToken = status.Token;
            config.ScreenName = status.ScreenName ?? "";
            config.SiteName = status.SiteName ?? config.SiteName;
            ConfigService.Save(config);
            PairingCode = "";
            Report(LinkState.Connecting, $"Emparejada como «{config.ScreenName}».");
            return true;
        }
        return false;
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        Report(LinkState.Connecting, "Conectando…");

        _http = AuthenticatedClient();
        _hub = BuildHub();

        var closed = new TaskCompletionSource();
        _hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        _hub.Reconnecting += _ =>
        {
            Report(LinkState.Offline, "Conexión perdida. Reconectando…");
            return Task.CompletedTask;
        };
        _hub.Reconnected += async _ =>
        {
            Report(LinkState.Online, "Reconectada. Resincronizando…");
            // una caseta que perdió la red no puede quedar con estado viejo
            Resynced?.Invoke();
            await Task.CompletedTask;
        };

        try
        {
            await _hub.StartAsync(ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            // el administrador reinició el emparejamiento o quitó la pantalla: se vuelve a pedir código
            await DisposeHubAsync();
            config.DeviceToken = "";
            ConfigService.Save(config);
            Report(LinkState.Pairing, "Credencial rechazada. Reiniciando emparejamiento…");
            return;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            // deshabilitada desde el panel: la credencial sigue valiendo, así que se conserva y se
            // espera a que la vuelvan a habilitar (sin pedir un emparejamiento nuevo)
            await DisposeHubAsync();
            Report(LinkState.Blocked, "Esta pantalla está deshabilitada desde el panel de TrueCentral.");
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return;
        }

        Session = await _http.GetFromJsonAsync<AlarmScreenSessionDto>("/api/screen/session", Json, ct);
        if (Session is not null)
        {
            config.ScreenName = Session.ScreenName;
            config.SiteName = Session.SiteName;
            ConfigService.Save(config);
        }

        Report(LinkState.Online, Connected);
        _failedAttempts = 0;
        Resynced?.Invoke();

        using var heartbeat = new Timer(_ => _ = SafeInvokeAsync(AlarmScreenProtocol.Heartbeat),
            null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        await using var registration = ct.Register(() => closed.TrySetResult());
        await closed.Task;

        // se cuenta también la caída limpia: si el servidor se movió, el hub falla sin excepción
        _failedAttempts++;
        Report(LinkState.Offline, "Desconectada del servidor.");
        await DisposeHubAsync();
        if (!ct.IsCancellationRequested) await Task.Delay(TimeSpan.FromSeconds(3), ct);
    }

    private HubConnection BuildHub()
    {
        // la credencial va en la query para que valga también en la negociación, y en cabecera
        // para los transportes que sí la permiten
        var url = $"{config.ServerUrl}{AlarmScreenProtocol.HubPath}?access_token={Uri.EscapeDataString(config.DeviceToken)}";

        var hub = new HubConnectionBuilder()
            .WithUrl(url, o =>
            {
                o.Headers[AlarmScreenProtocol.TokenHeader] = config.DeviceToken;
                o.HttpMessageHandlerFactory = _ => CreateHandler();
            })
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .Build();

        hub.On<AlarmScreenAlertDto>(AlarmScreenProtocol.AlarmRaised, alarm =>
        {
            AlarmRaised?.Invoke(alarm);
            _ = SafeInvokeAsync(AlarmScreenProtocol.ConfirmDelivery, alarm.Id);
        });
        hub.On<AlarmScreenAlertDto>(AlarmScreenProtocol.AlarmUpdated, alarm => AlarmUpdated?.Invoke(alarm));
        hub.On<AlarmScreenAckDto>(AlarmScreenProtocol.AlarmAcknowledged, ack => Acknowledged?.Invoke(ack));
        hub.On<AlarmScreenBulkAckDto>(AlarmScreenProtocol.AlarmsBulkAcknowledged, ack => BulkAcknowledged?.Invoke(ack));
        // además de la configuración puede haber cambiado la ventana de resincronización, así que
        // también conviene releer la lista
        hub.On(AlarmScreenProtocol.ConfigChanged, () => { ConfigChanged?.Invoke(); Resynced?.Invoke(); });
        hub.On(AlarmScreenProtocol.Reboot, () => RebootRequested?.Invoke());
        return hub;
    }

    // ---------- operaciones ----------

    public async Task<List<AlarmScreenAlertDto>> LoadRecentAsync(CancellationToken ct)
    {
        if (_http is null) return [];
        try
        {
            return await _http.GetFromJsonAsync<List<AlarmScreenAlertDto>>("/api/screen/alerts/recent", Json, ct) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public Task AcknowledgeAsync(long alertId) => SafeInvokeAsync(AlarmScreenProtocol.Acknowledge, alertId);

    /// <summary>Confirma en lote lo que llegó por resincronización.</summary>
    public Task ConfirmDeliveriesAsync(long[] alarmIds)
        => alarmIds.Length == 0 ? Task.CompletedTask : SafeInvokeAsync(AlarmScreenProtocol.ConfirmDeliveries, alarmIds);

    /// <summary>
    /// La lista ya quedó al día tras una reconexión. Sin este aviso el «Resincronizando…» se
    /// queda en pantalla hasta la siguiente caída, y el guardia ve ocupada a una caseta que
    /// hace rato terminó.
    /// </summary>
    public void MarkResynced()
    {
        if (State == LinkState.Online && StatusText != Connected) Report(LinkState.Online, Connected);
    }

    public Task AcknowledgeAllAsync() => SafeInvokeAsync(AlarmScreenProtocol.AcknowledgeAll);

    /// <summary>
    /// Configuración vigente de esta pantalla: sonidos del sitio, volumen y disposición del
    /// equipo. Se pide al conectar y cada vez que el servidor avisa <c>ConfigChanged</c>.
    /// </summary>
    public async Task<AlarmScreenConfigDto?> LoadConfigAsync(CancellationToken ct)
    {
        if (_http is null) return null;
        try { return await _http.GetFromJsonAsync<AlarmScreenConfigDto>("/api/screen/config", Json, ct); }
        catch { return null; }
    }

    /// <summary>
    /// Baja los audios que falten y devuelve nombre → ruta local. El hash es el nombre del
    /// archivo en caché: lo que no cambió no se vuelve a descargar, y una caseta con mala red
    /// no se queda sin sonido por eso.
    /// </summary>
    public async Task<Dictionary<string, string>> EnsureSoundsAsync(
        IReadOnlyList<AlarmScreenSoundDto> sounds, string cacheDir, CancellationToken ct)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_http is null || sounds.Count == 0) return resolved;

        try { Directory.CreateDirectory(cacheDir); }
        catch { return resolved; }

        foreach (var sound in sounds)
        {
            // el nombre del archivo en caché es el hash: no se vuelve a bajar lo que no cambió
            var extension = sound.Extension.StartsWith('.') && sound.Extension.Length <= 6 ? sound.Extension : ".bin";
            var path = Path.Combine(cacheDir, $"{sound.Sha256}{extension}");
            try
            {
                if (!File.Exists(path))
                {
                    var bytes = await DownloadAsync(sound.Url, ct);
                    if (bytes is null || bytes.Length == 0) continue;
                    await File.WriteAllBytesAsync(path, bytes, ct);
                }
                resolved[sound.Name] = path;
            }
            catch
            {
                // un audio que no baja no puede impedir que suenen los demás
            }
        }
        return resolved;
    }


    /// <summary>
    /// Le dice al servidor por qué salidas de audio puede sonar este equipo, para que el panel
    /// pueda ofrecerlas. Si falla no pasa nada: se reintenta en la siguiente conexión y mientras
    /// tanto el panel sigue mostrando el último inventario conocido.
    /// </summary>
    public async Task ReportAudioOutputsAsync(IReadOnlyList<string> outputs, CancellationToken ct)
    {
        if (_http is null || outputs.Count == 0) return;
        try { await _http.PostAsJsonAsync("/api/screen/audio-outputs", outputs, Json, ct); }
        catch { /* inventario, no credencial: no vale la pena molestar al guardia */ }
    }

    /// <summary>Descarga la foto de una alarma. Devuelve null si aún no está o si falla la red.</summary>
    public async Task<byte[]?> DownloadAsync(string path, CancellationToken ct)
    {
        if (_http is null) return null;
        try
        {
            var response = await _http.GetAsync(path, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct) : null;
        }
        catch
        {
            return null;
        }
    }

    // ---------- infraestructura ----------

    /// <summary>
    /// Envío sin esperar resultado. Esperarlo dentro de un handler del hub puede bloquear el
    /// canal, que procesa los mensajes en orden.
    /// </summary>
    private async Task SafeInvokeAsync(string method, params object?[] args)
    {
        try
        {
            if (_hub is { State: HubConnectionState.Connected })
                await _hub.SendCoreAsync(method, args, CancellationToken.None);
        }
        catch
        {
            // el hub reconecta solo; perder un heartbeat o un ConfirmDelivery no es fatal
        }
    }

    private HttpClient PlainClient()
        => new(CreateHandler()) { BaseAddress = new Uri(config.ServerUrl), Timeout = TimeSpan.FromSeconds(20) };

    private HttpClient AuthenticatedClient()
    {
        var http = PlainClient();
        http.DefaultRequestHeaders.Add(AlarmScreenProtocol.TokenHeader, config.DeviceToken);
        return http;
    }

    /// <summary>
    /// Instalaciones on-premise suelen usar certificados autofirmados; el servidor está en la
    /// misma LAN y se identifica con el device token, no con el certificado.
    /// </summary>
    private static HttpMessageHandler CreateHandler() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    };

    private void Report(LinkState state, string text)
    {
        State = state;
        StatusText = text;
        Changed?.Invoke();
    }

    private static string Version()
        => typeof(ServerLink).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private async Task DisposeHubAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
        _hub = null;
        _http?.Dispose();
        _http = null;
    }

    public async ValueTask DisposeAsync() => await DisposeHubAsync();

    /// <summary>Una pantalla reintenta para siempre: rendirse deja una caseta sin avisos que nadie nota.</summary>
    private sealed class ForeverRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context) => context.PreviousRetryCount switch
        {
            0 => TimeSpan.Zero,
            1 => TimeSpan.FromSeconds(2),
            2 => TimeSpan.FromSeconds(5),
            3 => TimeSpan.FromSeconds(10),
            _ => TimeSpan.FromSeconds(20)
        };
    }
}
