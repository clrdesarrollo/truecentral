using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Core.Isup;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services.Isup;

/// <summary>
/// Lado servidor del ISUP propio: cliente de la API interna del host (<see cref="IsupHostManager"/>) que los drivers usan como
/// transporte (<see cref="IIsupGateway"/>). Mantiene abierto el canal de eventos del host (equipos que entran y salen, alarmas,
/// rechazos), le entrega el directorio de equipos que pueden registrarse (todos los equipos del VMS con ID ISUP) y lo vuelve a
/// entregar cada vez que el host arranca o el directorio cambia.
/// </summary>
public sealed class IsupService : BackgroundService, IIsupGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IsupHostManager _host;
    private readonly IServiceScopeFactory _scopes;
    private readonly CredentialProtector _protector;
    private readonly IConfiguration _config;
    private readonly ILogger<IsupService> _logger;
    private readonly AuditService _audit;
    private volatile Dictionary<string, string> _namesById = new(StringComparer.Ordinal);
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, IsupDeviceState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<Action<IsupAlarm>>> _alarmSubscribers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (byte[] Content, string ContentType, DateTime ExpiresUtc)> _downloads = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _directoryWake = new(0);
    private volatile bool _connected;
    private volatile string? _hostPublicAddress;
    private DateTime _lastEventUtc;

    public IsupService(IsupHostManager host, IServiceScopeFactory scopes, CredentialProtector protector, IConfiguration config,
        AuditService audit, ILogger<IsupService> logger)
    {
        _audit = audit;
        _host = host;
        _scopes = scopes;
        _protector = protector;
        _config = config;
        _logger = logger;
        _http = new HttpClient { BaseAddress = new Uri(host.ApiBaseUrl), Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Add("X-Isup-Token", host.Token);
        // Un host nuevo no conoce a nadie: hay que volver a entregarle el directorio (lo hace el bucle al reconectar).
        host.ProcessStarted += () => _directoryWake.Release();
        IsupGateway.Current = this;
    }

    /// <summary>Un equipo ISUP entró o salió de línea (id, en línea). Corre en el hilo del canal de eventos: encolar y volver.</summary>
    public event Action<string, bool>? DeviceOnlineChanged;

    /// <summary>true mientras el canal de eventos con el host está abierto.</summary>
    public bool Connected => _connected;

    /// <summary>Último evento o latido recibido del host (UTC).</summary>
    public DateTime LastEventUtc => _lastEventUtc;

    // ====================== bucle: canal de eventos y directorio ======================

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        while (!ct.IsCancellationRequested)
        {
            if (!_host.Enabled) { await Task.Delay(TimeSpan.FromMinutes(1), ct); continue; }
            if (!_host.IsRunning) { await Task.Delay(TimeSpan.FromSeconds(2), ct); continue; }
            try
            {
                await PushDirectoryAsync(ct);
                await LoadStatusAsync(ct);
                // Mientras el canal está abierto, otro hilo vuelve a entregar el directorio cuando alguien lo pide.
                using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var directoryLoop = DirectoryLoopAsync(loopCts.Token);
                try { await ReadEventsAsync(ct); }
                finally { loopCts.Cancel(); await directoryLoop.ContinueWith(_ => { }); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (_connected) _logger.LogWarning("Canal de eventos con el servicio ISUP interrumpido: {Error}", ex.Message);
            }
            if (_connected)
            {
                _connected = false;
                // Sin canal no se sabe nada: todos pasan a desconocidos (fuera de línea) hasta reconectar.
                foreach (var id in _states.Where(kv => kv.Value.Online).Select(kv => kv.Key).ToList()) SetOnline(id, false);
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    private async Task DirectoryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _directoryWake.WaitAsync(ct);
            await Task.Delay(500, ct);                       // agrupa ráfagas de altas/ediciones
            while (_directoryWake.CurrentCount > 0) await _directoryWake.WaitAsync(ct);
            try { await PushDirectoryAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning("No se pudo actualizar el directorio ISUP: {Error}", ex.Message); }
        }
    }

    /// <summary>Pide volver a entregar el directorio al host (después de agregar, editar o borrar un equipo ISUP).</summary>
    public void InvalidateDirectory() => _directoryWake.Release();

    private async Task ReadEventsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("/events", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        _connected = true;
        _lastEventUtc = DateTime.UtcNow;
        _logger.LogInformation("Conectado al servicio ISUP interno.");
        while (!ct.IsCancellationRequested)
        {
            // El host late cada 15 s: 45 s sin nada = colgado.
            var lineTask = reader.ReadLineAsync(ct).AsTask();
            if (await Task.WhenAny(lineTask, Task.Delay(TimeSpan.FromSeconds(45), ct)) != lineTask)
                throw new TimeoutException("el servicio ISUP no envía latidos");
            var line = await lineTask;
            if (line is null) throw new IOException("el servicio ISUP cerró el canal de eventos");
            if (line.Length == 0) continue;
            _lastEventUtc = DateTime.UtcNow;
            IsupHostEvent? e;
            try { e = JsonSerializer.Deserialize<IsupHostEvent>(line, Json); }
            catch (JsonException) { continue; }
            if (e is null) continue;
            Dispatch(e);
        }
    }

    private void Dispatch(IsupHostEvent e)
    {
        switch (e.Kind)
        {
            case IsupEventKinds.Online:
                _states.AddOrUpdate(e.DeviceId, _ => new IsupDeviceState(e.DeviceId, true, e.Detail, null, e.At, false, null, false),
                    (_, s) => s with { Online = true, Address = e.Detail ?? s.Address, OnlineSince = e.At, Asleep = false });
                AuditConnection(e.DeviceId, true, e.Detail);
                // Antes de avisar se lee el estado completo: quien completa el equipo necesita saber si es ISUP 2.0/4.0 (sin ISAPI).
                // El enlace de alarmas se abre unos segundos después: se vuelve a leer.
                _ = Task.Run(async () =>
                {
                    await RefreshStateAsync(e.DeviceId, TimeSpan.Zero);
                    DeviceOnlineChanged?.Invoke(e.DeviceId, true);
                    await RefreshStateAsync(e.DeviceId, TimeSpan.FromSeconds(8));
                });
                break;
            case IsupEventKinds.Offline:
                SetOnline(e.DeviceId, false);
                AuditConnection(e.DeviceId, false, null);
                break;
            case IsupEventKinds.Rejected:
                // El host avisa una vez por equipo (reintenta cada pocos segundos); aquí además a lo más una fila por hora.
                if (_audit.ShouldLog($"isup-rejected:{e.DeviceId}", TimeSpan.FromHours(1)))
                    _ = _audit.LogSystemAsync("isup", "device-rejected", "isup-device", e.DeviceId, NameOf(e.DeviceId),
                        $"El equipo con ID ISUP {e.DeviceId} intentó registrarse y fue rechazado: {e.Detail}.", success: false);
                break;
            case IsupEventKinds.Sleep:
                if (_states.TryGetValue(e.DeviceId, out var st)) _states[e.DeviceId] = st with { Asleep = true };
                break;
            case IsupEventKinds.Alarm when e.Alarm is not null:
                LogFirstOfKind(e.Alarm);
                if (_alarmSubscribers.TryGetValue(e.DeviceId, out var list))
                {
                    Action<IsupAlarm>[] handlers;
                    lock (list) handlers = list.ToArray();
                    foreach (var h in handlers)
                    {
                        try { h(e.Alarm); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Error al entregar una alarma ISUP de {Device}.", e.DeviceId); }
                    }
                }
                break;
        }
    }

    private string? NameOf(string id) => _namesById.GetValueOrDefault(id);

    private readonly ConcurrentDictionary<string, byte> _alarmKindsSeen = new(StringComparer.Ordinal);

    /// <summary>
    /// Deja en el registro la primera alarma de cada tipo de cada equipo (tipo ISUP + eventType), recortada: así se ve el formato
    /// real que envía un modelo nuevo (las llamadas de un frente, los eventos de una cámara) sin llenar el registro.
    /// </summary>
    private void LogFirstOfKind(IsupAlarm alarm)
    {
        string text = alarm.Text ?? "";
        var m = System.Text.RegularExpressions.Regex.Match(text, @"<eventType>([^<]{1,60})</eventType>|""eventType""\s*:\s*""([^""]{1,60})""");
        string kind = alarm.Cid is not null ? "cid" : alarm.Basic is { } b ? $"basic-{b.AlarmType}"
            : m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : "?";
        if (_alarmKindsSeen.Count > 2000 || !_alarmKindsSeen.TryAdd($"{alarm.DeviceId}|{alarm.Type}|{kind}", 0)) return;
        string sample = text.Length > 800 ? text[..800] + "…" : text;
        _logger.LogInformation("ISUP: primera alarma «{Kind}» (tipo {Type}) de {Device}{Pictures}: {Text}", kind, alarm.Type, alarm.DeviceId,
            alarm.Pictures is { Count: > 0 } p ? $" con {p.Count} foto(s)" : "", sample.Replace("\r", "").Replace("\n", " "));
    }

    /// <summary>Conexión y desconexión de cada equipo en la bitácora (acelerado: un equipo que oscila no llena la tabla).</summary>
    private void AuditConnection(string id, bool online, string? address)
    {
        if (!_audit.ShouldLog($"isup-conn:{id}:{online}", TimeSpan.FromMinutes(5))) return;
        _ = _audit.LogSystemAsync("isup", online ? "device-connected" : "device-disconnected", "isup-device", id, NameOf(id),
            online ? $"El equipo con ID ISUP {id} se conectó al servidor{(address is { Length: > 0 } ? $" (IP {address})" : "")}."
                   : $"El equipo con ID ISUP {id} se desconectó del servidor.");
    }

    private void SetOnline(string id, bool online)
    {
        if (_states.TryGetValue(id, out var s)) _states[id] = s with { Online = online, OnlineSince = online ? s.OnlineSince : null };
        DeviceOnlineChanged?.Invoke(id, online);
    }

    private async Task RefreshStateAsync(string id, TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
            using var r = await _http.GetAsync($"/state/{Uri.EscapeDataString(id)}");
            if (r.IsSuccessStatusCode && await r.Content.ReadFromJsonAsync<IsupDeviceState>(Json) is { } s) _states[id] = s;
        }
        catch (Exception) { /* informativo */ }
    }

    private async Task LoadStatusAsync(CancellationToken ct)
    {
        var status = await GetHostStatusAsync(ct);
        if (status is null) return;
        _hostPublicAddress = status.PublicAddress;
        foreach (var d in status.Devices)
        {
            bool was = _states.TryGetValue(d.DeviceId, out var old) && old.Online;
            _states[d.DeviceId] = d;
            if (was != d.Online) DeviceOnlineChanged?.Invoke(d.DeviceId, d.Online);
        }
    }

    /// <summary>Estado completo del host (puertos, equipos, rechazos), o null si no responde.</summary>
    public async Task<IsupHostStatus?> GetHostStatusAsync(CancellationToken ct = default)
    {
        if (!_host.IsRunning) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            return await _http.GetFromJsonAsync<IsupHostStatus>("/status", Json, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }

    // ====================== directorio de equipos ======================

    /// <summary>
    /// Equipos del VMS que pueden registrarse por ISUP: todos los que tengan ID y clave, de cualquier tipo. Un mismo equipo físico
    /// puede figurar en dos módulos (p. ej. un frente de citofonía que además es terminal de acceso): entonces comparten el registro y
    /// deben tener la misma clave; si no, vale la primera y se avisa.
    /// </summary>
    public async Task<List<IsupDirectoryEntry>> BuildDirectoryAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var all = new List<IIsupEquipment>();
        all.AddRange(await db.AlarmPanels.AsNoTracking().Where(x => x.IsupDeviceId != null).ToListAsync(ct));
        all.AddRange(await db.AccessDevices.AsNoTracking().Where(x => x.IsupDeviceId != null).ToListAsync(ct));
        all.AddRange(await db.Devices.AsNoTracking().Where(x => x.IsupDeviceId != null).ToListAsync(ct));
        all.AddRange(await db.Speakers.AsNoTracking().Where(x => x.IsupDeviceId != null).ToListAsync(ct));
        all.AddRange(await db.Intercoms.AsNoTracking().Where(x => x.IsupDeviceId != null).ToListAsync(ct));
        var entries = new Dictionary<string, IsupDirectoryEntry>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var eq in all)
        {
            if (eq.IsupDeviceId is { Length: > 0 } nid) names[nid.Trim()] = names.TryGetValue(nid.Trim(), out var prev) ? $"{prev} / {eq.Name}" : eq.Name;
            if (string.IsNullOrWhiteSpace(eq.IsupDeviceId)) continue;
            // Sin clave: equipo ISUP 2.0/4.0, que se registra sin autenticarse.
            string key = "";
            try { if (eq.IsupKeyCiphertext is { Length: > 0 } cipher) key = _protector.Unprotect(cipher); }
            catch (Exception) { _logger.LogWarning("No se pudo descifrar la clave ISUP de «{Name}».", eq.Name); continue; }
            var id = eq.IsupDeviceId.Trim();
            if (entries.TryGetValue(id, out var existing))
            {
                if (existing.Key != key)
                    _logger.LogWarning("El ID ISUP {Id} figura en dos equipos con claves distintas; vale la del primero. Corrija «{Name}».", id, eq.Name);
                continue;
            }
            entries[id] = new IsupDirectoryEntry(id, key);
        }
        _namesById = names;
        return entries.Values.ToList();
    }

    private async Task PushDirectoryAsync(CancellationToken ct)
    {
        var entries = await BuildDirectoryAsync(ct);
        using var content = JsonContent.Create(entries, options: Json);
        using var r = await _http.PutAsync("/directory", content, ct);
        r.EnsureSuccessStatusCode();
    }

    // ====================== IIsupGateway (lo usan los drivers) ======================

    public bool IsOnline(string deviceId) => _states.TryGetValue(deviceId, out var s) && s.Online;

    public IsupDeviceState? GetState(string deviceId) => _states.GetValueOrDefault(deviceId);

    public async Task<IsupIsapiResponse> IsapiAsync(string deviceId, string method, string pathAndQuery, byte[]? body, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!_host.IsRunning) return new IsupIsapiResponse(false, [], -1, "El servicio ISUP del servidor no está en ejecución.");
        var url = $"/isapi/{Uri.EscapeDataString(deviceId)}?method={Uri.EscapeDataString(method)}&path={Uri.EscapeDataString(pathAndQuery)}" +
                  $"&timeout={(int)timeout.TotalMilliseconds}";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout + TimeSpan.FromSeconds(5));
        try
        {
            using var content = new ByteArrayContent(body ?? []);
            using var r = await _http.PostAsync(url, content, cts.Token);
            var data = await r.Content.ReadAsByteArrayAsync(cts.Token);
            bool ok = r.Headers.TryGetValues("X-Isup-Ok", out var okv) && okv.FirstOrDefault() == "1";
            int code = r.Headers.TryGetValues("X-Isup-Error-Code", out var cv) && int.TryParse(cv.FirstOrDefault(), out var c) ? c : 0;
            string? error = r.Headers.TryGetValues("X-Isup-Error", out var ev) ? Uri.UnescapeDataString(ev.FirstOrDefault() ?? "") : null;
            if (!r.IsSuccessStatusCode) return new IsupIsapiResponse(false, [], -1, $"El servicio ISUP respondió {(int)r.StatusCode}.");
            return new IsupIsapiResponse(ok, data, code, error);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new IsupIsapiResponse(false, [], 10, "El equipo no respondió a tiempo por ISUP.");
        }
    }

    public async Task<IsupDeviceInfo> GetDeviceInfoAsync(string deviceId, CancellationToken ct = default)
    {
        if (!_host.IsRunning) throw new DriverException("El servicio ISUP del servidor no está en ejecución.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(40));
        using var r = await _http.GetAsync($"/info/{Uri.EscapeDataString(deviceId)}", cts.Token);
        if (!r.IsSuccessStatusCode) throw new DriverException(await ErrorOf(r));
        return await r.Content.ReadFromJsonAsync<IsupDeviceInfo>(Json, cts.Token)
               ?? throw new DriverException("El servicio ISUP no entregó los datos del equipo.");
    }

    public Task PtzAsync(string deviceId, int channel, IsupPtzCommand command, bool start, int speed, CancellationToken ct = default) =>
        CommandAsync($"/ptz/{Uri.EscapeDataString(deviceId)}/{channel}?command={command}&start={start}&speed={speed}", ct);

    public Task PresetAsync(string deviceId, int channel, IsupPresetAction action, int index, CancellationToken ct = default) =>
        CommandAsync($"/preset/{Uri.EscapeDataString(deviceId)}/{channel}?action={action}&index={index}", ct);

    public Task SetAlarmOutputAsync(string deviceId, int output, bool on, int seconds, CancellationToken ct = default) =>
        CommandAsync($"/output/{Uri.EscapeDataString(deviceId)}/{output}?on={on}&seconds={seconds}", ct);

    /// <summary>Despierta un equipo en reposo (vuelve a registrarse en alrededor de un minuto).</summary>
    public Task WakeUpAsync(string deviceId, CancellationToken ct = default) => CommandAsync($"/wake/{Uri.EscapeDataString(deviceId)}", ct);

    /// <summary>Da de baja al equipo (se vuelve a registrar solo si sigue en el directorio).</summary>
    public async Task<bool> LogoutAsync(string deviceId, CancellationToken ct = default)
    {
        if (!_host.IsRunning) return false;
        using var r = await _http.PostAsync($"/logout/{Uri.EscapeDataString(deviceId)}", null, ct);
        return r.IsSuccessStatusCode;
    }

    private async Task CommandAsync(string url, CancellationToken ct)
    {
        if (!_host.IsRunning) throw new DriverException("El servicio ISUP del servidor no está en ejecución.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var r = await _http.PostAsync(url, null, cts.Token);
        if (r.IsSuccessStatusCode) return;
        throw new DriverException(await ErrorOf(r));
    }

    private static async Task<string> ErrorOf(HttpResponseMessage r)
    {
        try
        {
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 } msg) return msg;
        }
        catch (JsonException) { }
        return $"El servicio ISUP respondió {(int)r.StatusCode}.";
    }

    public IDisposable SubscribeAlarms(string deviceId, Action<IsupAlarm> onAlarm)
    {
        var list = _alarmSubscribers.GetOrAdd(deviceId, _ => []);
        lock (list) list.Add(onAlarm);
        return new Unsubscriber(() => { lock (list) list.Remove(onAlarm); });
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) dispose(); }
    }

    public string LiveSourceUrl(string deviceId, int channel, int streamType) =>
        $"{_host.ApiBaseUrl}/live/{Uri.EscapeDataString(deviceId)}/{channel}/{streamType}?t={_host.MediaToken}";

    public string PlaybackSourceUrl(string deviceId, int channel, DateTime localStart, DateTime localEnd) =>
        $"{_host.ApiBaseUrl}/playback/{Uri.EscapeDataString(deviceId)}/{channel}" +
        $"?from={localStart.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}&to={localEnd.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}&t={_host.MediaToken}";

    /// <summary>Máximo de capturas de cuadro simultáneas (cada una pide video al equipo y decodifica).</summary>
    private readonly SemaphoreSlim _grabs = new(2, 2);

    public async Task<byte[]?> GrabFrameAsync(string deviceId, int channel, int streamType, CancellationToken ct = default)
    {
        if (!IsOnline(deviceId) || MediaMtxManager.LocateFfmpeg() is not { } ffmpeg) return null;
        await _grabs.WaitAsync(ct);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // Mismos parámetros de lectura que el relé de MediaMTX: arranque en 1-2 s sin esperar el sondeo completo.
            foreach (string a in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-nostdin",
                         "-fflags", "+genpts", "-probesize", "400000", "-analyzeduration", "1000000",
                         "-f", "mpeg", "-i", LiveSourceUrl(deviceId, channel, streamType),
                         "-frames:v", "1", "-q:v", "3", "-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1",
                     })
                psi.ArgumentList.Add(a);
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return null;
            ChildProcessJob.Attach(process);
            _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            using var jpeg = new MemoryStream();
            try
            {
                await process.StandardOutput.BaseStream.CopyToAsync(jpeg, limit.Token);
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                _logger.LogInformation("ISUP: no llegó imagen de {Device} canal {Channel} para la foto en 20 s.", deviceId, channel);
                return null;
            }
            byte[] bytes = jpeg.ToArray();
            return bytes is [0xFF, 0xD8, ..] ? bytes : null;
        }
        finally
        {
            _grabs.Release();
        }
    }

    public async Task<IIsupVoiceSession> OpenVoiceAsync(string deviceId, int channel, Action<ReadOnlyMemory<byte>> onAudio, bool broadcast = false, CancellationToken ct = default)
    {
        if (!_host.IsRunning) throw new DriverException("El servicio ISUP del servidor no está en ejecución.");
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("X-Isup-Token", _host.Token);
        var uri = new Uri($"ws://127.0.0.1:{_host.ApiPort}/voice/{Uri.EscapeDataString(deviceId)}/{channel}?broadcast={(broadcast ? 1 : 0)}");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            await ws.ConnectAsync(uri, cts.Token);
            var buffer = new byte[4096];
            var first = await ws.ReceiveAsync(buffer, cts.Token);
            if (first.MessageType != WebSocketMessageType.Text) throw new DriverException("El servicio ISUP no confirmó la voz.");
            using var hello = JsonDocument.Parse(buffer.AsMemory(0, first.Count));
            var codec = hello.RootElement.GetProperty("codec").GetString() ?? "G711U";
            int frame = hello.RootElement.GetProperty("frame").GetInt32();
            return new VoiceSession(ws, codec, frame, onAudio, _logger);
        }
        catch (WebSocketException ex)
        {
            ws.Dispose();
            throw new DriverException($"No se pudo abrir la voz con el equipo por ISUP: {ex.Message}");
        }
        catch (Exception) { ws.Dispose(); throw; }
    }

    private sealed class VoiceSession : IIsupVoiceSession
    {
        private readonly ClientWebSocket _ws;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly Task _receiver;

        public VoiceSession(ClientWebSocket ws, string codec, int frame, Action<ReadOnlyMemory<byte>> onAudio, ILogger logger)
        {
            _ws = ws;
            Codec = codec;
            FrameBytes = frame;
            _receiver = Task.Run(async () =>
            {
                var buffer = new byte[16 * 1024];
                try
                {
                    while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                    {
                        var r = await _ws.ReceiveAsync(buffer, _cts.Token);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        if (r.MessageType == WebSocketMessageType.Binary && r.Count > 0) onAudio(buffer.AsMemory(0, r.Count).ToArray());
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
                catch (Exception ex) { logger.LogWarning(ex, "Error recibiendo la voz ISUP."); }
            });
        }

        public string Codec { get; }
        public int FrameBytes { get; }
        public bool IsAlive => _ws.State == WebSocketState.Open && !_receiver.IsCompleted;

        public async Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
        {
            if (!IsAlive) throw new DriverException("La voz con el equipo se cortó.");
            await _sendLock.WaitAsync(ct);
            try { await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct); }
            finally { _sendLock.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, t.Token);
                }
            }
            catch (Exception) { }
            _cts.Cancel();
            await _receiver.ContinueWith(_ => { });
            _ws.Dispose();
            _cts.Dispose();
        }
    }

    // ====================== descargas de corta vida (fotos para los equipos) ======================

    /// <summary>
    /// Publica un archivo que el equipo descarga por HTTP desde donde esté (las fotos de rostro de los terminales ISUP: el túnel ISAPI
    /// no lleva multipart). La URL usa la dirección pública del ISUP y el puerto del panel; el nombre es aleatorio y vence solo.
    /// </summary>
    public string PublishDownload(byte[] content, string contentType, TimeSpan lifetime)
    {
        foreach (var kv in _downloads.Where(kv => kv.Value.ExpiresUtc < DateTime.UtcNow).ToList()) _downloads.TryRemove(kv.Key, out _);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _downloads[token] = (content, contentType, DateTime.UtcNow + lifetime);
        var ext = contentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
        return $"{DownloadBaseUrl}/api/isup/files/{token}.{ext}";
    }

    /// <summary>Archivo publicado con <see cref="PublishDownload"/>, o null si no existe o venció.</summary>
    public (byte[] Content, string ContentType)? TakeDownload(string token)
    {
        token = Path.GetFileNameWithoutExtension(token);
        if (!_downloads.TryGetValue(token, out var d) || d.ExpiresUtc < DateTime.UtcNow) return null;
        return (d.Content, d.ContentType);
    }

    /// <summary>
    /// Base de las URL que descargan los equipos: Isup:DownloadBaseUrl, o la dirección pública del ISUP con el puerto HTTP sin
    /// cifrar (Web:HttpPort): los terminales no validan un certificado propio ni siguen una redirección a HTTPS.
    /// </summary>
    public string DownloadBaseUrl
    {
        get
        {
            if (_config["Isup:DownloadBaseUrl"] is { Length: > 0 } explicitBase) return explicitBase.TrimEnd('/');
            var host = _host.Options.PublicAddress is { Length: > 0 } a ? a : _hostPublicAddress ?? "127.0.0.1";
            var ports = Https.WebPorts.From(_config);
            return ports.HttpUrl(host) ?? ports.HttpsUrl(host);
        }
    }
}
