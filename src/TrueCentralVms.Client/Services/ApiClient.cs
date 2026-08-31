using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Client.Services;

/// <summary>Error de la API con mensaje apto para mostrar al usuario.</summary>
public sealed class ApiException(string message) : Exception(message);

/// <summary>
/// Cliente REST del servidor TrueCentral. Todos los DTO son los records
/// compartidos de Core: el formato de cable es exactamente el del servidor.
/// Si el servidor invalida la sesión (p. ej. por reinicio, los tokens viven
/// en memoria), se reintenta el login con las credenciales de esta ejecución
/// y se repite la solicitud, sin molestar al usuario.
/// </summary>
public sealed class ApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http = new()
    {
        // Configurable en Configuración → Red (se aplica al iniciar la app).
        Timeout = TimeSpan.FromSeconds(Math.Clamp(ClientSettings.Load().ApiTimeoutSeconds, 5, 120)),
    };

    /// <summary>Cliente aparte para las exportaciones: una descarga dura lo
    /// que el equipo tarde en entregar el tramo, muy por encima del tiempo
    /// límite razonable para una llamada de API.</summary>
    private readonly HttpClient _downloads = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly SemaphoreSlim _reloginLock = new(1, 1);
    private string? _password;
    private DateTime _reloginFailedUntil = DateTime.MinValue;

    public string? BaseUrl { get; private set; }
    public string? Token { get; private set; }
    public string? Username { get; private set; }
    public string? Role { get; private set; }

    public async Task LoginAsync(string serverUrl, string username, string password, CancellationToken ct = default)
    {
        BaseUrl = serverUrl.TrimEnd('/');
        var login = await SendAsync<LoginResponse>(HttpMethod.Post, "/api/auth/login",
            new LoginRequest(username, password), ct, allowRelogin: false);
        Token = login.Token;
        Username = login.Username;
        Role = login.Role;
        _password = password;
        _http.DefaultRequestHeaders.Authorization = new("Bearer", login.Token);
    }

    /// <summary>
    /// Renueva la sesión con las credenciales del login original. Devuelve
    /// true si hay token nuevo (o si otro hilo ya lo renovó: las celdas de
    /// video y el sondeo de métricas pueden chocar con el 401 a la vez).
    /// Tras un intento fallido no se vuelve a intentar por 30 s, para no
    /// martillar el servidor con logins si la contraseña cambió.
    /// </summary>
    private async Task<bool> TryReloginAsync(string? staleToken, CancellationToken ct)
    {
        if (_password is null) return false;
        await _reloginLock.WaitAsync(ct);
        try
        {
            if (Token != staleToken) return true; // otro hilo ya renovó la sesión
            if (DateTime.UtcNow < _reloginFailedUntil) return false;
            try
            {
                await LoginAsync(BaseUrl!, Username!, _password, ct);
                return true;
            }
            catch
            {
                _reloginFailedUntil = DateTime.UtcNow.AddSeconds(30);
                return false;
            }
        }
        finally
        {
            _reloginLock.Release();
        }
    }

    public Task<List<DeviceDto>> GetDevicesAsync(CancellationToken ct = default) =>
        SendAsync<List<DeviceDto>>(HttpMethod.Get, "/api/devices", null, ct);

    public Task<List<ChannelDto>> GetChannelsAsync(int deviceId, CancellationToken ct = default) =>
        SendAsync<List<ChannelDto>>(HttpMethod.Get, $"/api/devices/{deviceId}/channels", null, ct);

    // -----------------------------------------------------------------
    // Muro de video
    // -----------------------------------------------------------------

    public Task<List<WallDto>> GetWallsAsync(CancellationToken ct = default) =>
        SendAsync<List<WallDto>>(HttpMethod.Get, "/api/walls", null, ct);

    public Task<WallDto> GetWallAsync(int wallId, CancellationToken ct = default) =>
        SendAsync<WallDto>(HttpMethod.Get, $"/api/walls/{wallId}", null, ct);

    /// <summary>Pone un canal del inventario en una ventana del muro.</summary>
    public Task<OperationResultDto> WallAssignAsync(int wallId, int windowId, int channelId, int streamType,
        CancellationToken ct = default) =>
        SendAsync<OperationResultDto>(HttpMethod.Post, $"/api/walls/{wallId}/assign",
            new AssignRequest(windowId, channelId, streamType), ct);

    /// <summary>
    /// Proyección: pone en una ventana una URL externa (este PC publicando su
    /// pantalla por RTSP) en lugar de un canal del inventario.
    /// </summary>
    public Task<OperationResultDto> WallAssignExternalAsync(int wallId, int windowId, string url, string label,
        CancellationToken ct = default) =>
        SendAsync<OperationResultDto>(HttpMethod.Post, $"/api/walls/{wallId}/assign-external",
            new ExternalAssignRequest(windowId, url, label), ct);

    public Task<OperationResultDto> WallCreateFloatingExternalAsync(int wallId, double x, double y, double w, double h,
        string url, string label, CancellationToken ct = default) =>
        SendAsync<OperationResultDto>(HttpMethod.Post, $"/api/walls/{wallId}/floating-external",
            new FloatingExternalCreateRequest(x, y, w, h, url, label), ct);

    public Task<OperationResultDto> WallAssignFloatingExternalAsync(int wallId, int floatingId, string url, string label,
        CancellationToken ct = default) =>
        SendAsync<OperationResultDto>(HttpMethod.Post, $"/api/walls/{wallId}/floating/{floatingId}/assign-external",
            new FloatingExternalAssignRequest(url, label), ct);

    public Task<OperationResultDto> WallClearAsync(int wallId, int windowId, CancellationToken ct = default) =>
        SendAsync<OperationResultDto>(HttpMethod.Post, $"/api/walls/{wallId}/clear", new ClearRequest(windowId), ct);

    public Task<Dictionary<int, OperationResultDto>> WallClearAllAsync(int wallId, CancellationToken ct = default) =>
        SendAsync<Dictionary<int, OperationResultDto>>(HttpMethod.Post, $"/api/walls/{wallId}/clear-all", null, ct);

    /// <summary>Intercambia las cámaras de dos ventanas (arrastrar y soltar).</summary>
    public Task<OperationResultDto> WallSwapAsync(int wallId, int windowAId, int windowBId, CancellationToken ct = default) =>
        SendAsync<OperationResultDto>(HttpMethod.Post, $"/api/walls/{wallId}/swap",
            new SwapRequest(windowAId, windowBId), ct);

    /// <summary>Re-empuja al decodificador la división de ventanas configurada.</summary>
    public Task<Dictionary<int, OperationResultDto>> WallSyncAsync(int wallId, CancellationToken ct = default) =>
        SendAsync<Dictionary<int, OperationResultDto>>(HttpMethod.Post, $"/api/walls/{wallId}/sync", null, ct);

    /// <summary>Cambia la división (cantidad de ventanas) de un monitor.</summary>
    public Task<Dictionary<int, OperationResultDto>> WallChangeWindowModeAsync(int wallId, int screenId, int windowMode,
        CancellationToken ct = default) =>
        SendAsync<Dictionary<int, OperationResultDto>>(HttpMethod.Put,
            $"/api/walls/{wallId}/screens/{screenId}/window-mode", new ScreenWindowModeRequest(windowMode), ct);

    public Task<Dictionary<int, OperationResultDto>> WallGroupAsync(int wallId, int screenId, List<int> windowIds,
        CancellationToken ct = default) =>
        SendAsync<Dictionary<int, OperationResultDto>>(HttpMethod.Post,
            $"/api/walls/{wallId}/screens/{screenId}/group", new GroupRequest(windowIds), ct);

    public Task<Dictionary<int, OperationResultDto>> WallUngroupAsync(int wallId, int windowId, CancellationToken ct = default) =>
        SendAsync<Dictionary<int, OperationResultDto>>(HttpMethod.Post,
            $"/api/walls/{wallId}/windows/{windowId}/ungroup", null, ct);

    /// <summary>Subdivide una ventana del mosaico en 4/9/16 sub-ventanas.</summary>
    public Task<Dictionary<int, OperationResultDto>> WallSubdivideAsync(int wallId, int windowId, int parts,
        CancellationToken ct = default) =>
        SendAsync<Dictionary<int, OperationResultDto>>(HttpMethod.Post,
            $"/api/walls/{wallId}/windows/{windowId}/subdivide", new SubdivideRequest(parts), ct);

    public Task<Dictionary<int, OperationResultDto>> WallFullscreenAsync(int wallId, int windowId, CancellationToken ct = default) =>
        SendAsync<Dictionary<int, OperationResultDto>>(HttpMethod.Post, $"/api/walls/{wallId}/fullscreen",
            new FullscreenRequest(windowId), ct);

    public Task<Dictionary<int, OperationResultDto>> WallExitFullscreenAsync(int wallId, int screenId, CancellationToken ct = default) =>
        SendAsync<Dictionary<int, OperationResultDto>>(HttpMethod.Post,
            $"/api/walls/{wallId}/screens/{screenId}/exit-fullscreen", null, ct);

    /// <summary>La cámara de una ventana ocupa TODO el muro.</summary>
    public Task<OperationResultDto> WallEnterWallFullscreenAsync(int wallId, int windowId, CancellationToken ct = default) =>
        SendAsync<OperationResultDto>(HttpMethod.Post, $"/api/walls/{wallId}/wall-fullscreen",
            new FullscreenRequest(windowId), ct);

    public Task<OperationResultDto> WallExitWallFullscreenAsync(int wallId, CancellationToken ct = default) =>
        SendAsync<OperationResultDto>(HttpMethod.Post, $"/api/walls/{wallId}/exit-wall-fullscreen", null, ct);

    // Ventanas flotantes (rect libre sobre el mosaico)
    public Task<OperationResultDto> WallCreateFloatingAsync(int wallId, double x, double y, double w, double h,
        int channelId, int streamType, CancellationToken ct = default) =>
        SendAsync<OperationResultDto>(HttpMethod.Post, $"/api/walls/{wallId}/floating",
            new FloatingCreateRequest(x, y, w, h, channelId, streamType), ct);

    public Task<OperationResultDto> WallMoveFloatingAsync(int wallId, int floatingId, double x, double y, double w, double h,
        CancellationToken ct = default) =>
        SendAsync<OperationResultDto>(HttpMethod.Put, $"/api/walls/{wallId}/floating/{floatingId}",
            new FloatingMoveRequest(x, y, w, h), ct);

    public Task<OperationResultDto> WallAssignFloatingAsync(int wallId, int floatingId, int channelId, int streamType,
        CancellationToken ct = default) =>
        SendAsync<OperationResultDto>(HttpMethod.Post, $"/api/walls/{wallId}/floating/{floatingId}/assign",
            new FloatingAssignRequest(channelId, streamType), ct);

    public Task<OperationResultDto> WallToggleFloatingFullscreenAsync(int wallId, int floatingId, CancellationToken ct = default) =>
        SendAsync<OperationResultDto>(HttpMethod.Post, $"/api/walls/{wallId}/floating/{floatingId}/fullscreen", null, ct);

    public Task<OperationResultDto> WallDeleteFloatingAsync(int wallId, int floatingId, CancellationToken ct = default) =>
        SendAsync<OperationResultDto>(HttpMethod.Delete, $"/api/walls/{wallId}/floating/{floatingId}", null, ct);

    // Layouts guardados
    public Task<List<LayoutPresetDto>> GetWallLayoutsAsync(int wallId, CancellationToken ct = default) =>
        SendAsync<List<LayoutPresetDto>>(HttpMethod.Get, $"/api/walls/{wallId}/layouts", null, ct);

    public Task<LayoutPresetDto> SaveWallLayoutAsync(int wallId, string name, CancellationToken ct = default) =>
        SendAsync<LayoutPresetDto>(HttpMethod.Post, $"/api/walls/{wallId}/layouts",
            new LayoutSaveRequest(name, null, null), ct);

    public Task<Dictionary<int, OperationResultDto>> ApplyWallLayoutAsync(int wallId, int layoutId, CancellationToken ct = default) =>
        SendAsync<Dictionary<int, OperationResultDto>>(HttpMethod.Post,
            $"/api/walls/{wallId}/layouts/{layoutId}/apply", null, ct);

    public Task DeleteWallLayoutAsync(int wallId, int layoutId, CancellationToken ct = default) =>
        SendAsync<object?>(HttpMethod.Delete, $"/api/walls/{wallId}/layouts/{layoutId}", null, ct);

    /// <summary>
    /// Fotograma JPEG de un canal para la vista previa del muro; null si el
    /// equipo o el driver no lo entregan (la miniatura simplemente no aparece).
    /// </summary>
    public async Task<byte[]?> GetSnapshotAsync(int deviceId, int channelNumber, CancellationToken ct = default)
    {
        if (BaseUrl is null) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{BaseUrl}/api/devices/{deviceId}/snapshot/{channelNumber}");
            if (Token is not null)
                request.Headers.Authorization = new("Bearer", Token);
            using var response = await _http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct) : null;
        }
        catch
        {
            return null;
        }
    }

    // -----------------------------------------------------------------
    // Aplicaciones → Reconocimiento de patentes
    // -----------------------------------------------------------------

    /// <summary>
    /// Historial de reconocimientos, del más reciente al más antiguo. Los
    /// filtros son opcionales; las fechas van en HORA LOCAL DEL EQUIPO (es la
    /// que fecha las lecturas).
    /// </summary>
    public Task<List<PlateEventDto>> GetPlateEventsAsync(int? deviceId = null, string? plate = null,
        DateTime? from = null, DateTime? to = null, int take = 100, CancellationToken ct = default)
    {
        var query = new List<string> { $"take={take}" };
        if (deviceId is > 0) query.Add($"deviceId={deviceId}");
        if (!string.IsNullOrWhiteSpace(plate)) query.Add($"plate={Uri.EscapeDataString(plate)}");
        if (from is { } f) query.Add($"from={Uri.EscapeDataString(f.ToString("s"))}");
        if (to is { } t) query.Add($"to={Uri.EscapeDataString(t.ToString("s"))}");
        return SendAsync<List<PlateEventDto>>(HttpMethod.Get, $"/api/anpr/events?{string.Join('&', query)}", null, ct);
    }

    /// <summary>Equipos capaces de entregar patentes, con su estado como fuente.</summary>
    public Task<List<AnprSourceDto>> GetAnprSourcesAsync(CancellationToken ct = default) =>
        SendAsync<List<AnprSourceDto>>(HttpMethod.Get, "/api/anpr/sources", null, ct);

    /// <summary>Enciende o apaga un equipo como fuente de patentes (solo administrador).</summary>
    public Task SetAnprSourceAsync(int deviceId, bool enabled, CancellationToken ct = default) =>
        SendAsync<object?>(HttpMethod.Put, $"/api/anpr/sources/{deviceId}", new AnprSourceWriteDto(enabled), ct);

    /// <summary>
    /// Foto de un reconocimiento: "scene" (escena completa) o "plate" (primer
    /// plano de la placa). null si el equipo no la envió.
    /// </summary>
    public async Task<byte[]?> GetPlateImageAsync(long eventId, string kind, CancellationToken ct = default)
    {
        if (BaseUrl is null) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/anpr/events/{eventId}/{kind}");
            if (Token is not null)
                request.Headers.Authorization = new("Bearer", Token);
            using var response = await _http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Uso de CPU/RAM/disco de la máquina del servidor (indicadores del navbar).</summary>
    public Task<SystemMetricsDto> GetSystemMetricsAsync(CancellationToken ct = default) =>
        SendAsync<SystemMetricsDto>(HttpMethod.Get, "/api/system/metrics", null, ct);

    public Task<StreamGrantDto> RequestStreamAsync(int deviceId, int rtspChannel, StreamProfile profile, CancellationToken ct = default) =>
        SendAsync<StreamGrantDto>(HttpMethod.Post, "/api/streams/request",
            new StreamRequestDto(deviceId, rtspChannel, profile), ct);

    /// <summary>Segmentos grabados de un canal para un día (hora local del equipo).</summary>
    public Task<List<RecordingSegmentDto>> GetRecordingSegmentsAsync(int deviceId, int channelNumber, DateTime date,
        CancellationToken ct = default) =>
        SendAsync<List<RecordingSegmentDto>>(HttpMethod.Get,
            $"/api/playback/{deviceId}/{channelNumber}/segments?date={date:yyyy-MM-dd}", null, ct);

    /// <summary>
    /// Días del mes (1..31) con grabación en el canal, para las marcas del
    /// calendario. Lista vacía = el equipo no lo informa.
    /// </summary>
    public Task<List<int>> GetRecordedDaysAsync(int deviceId, int channelNumber, int year, int month,
        CancellationToken ct = default) =>
        SendAsync<List<int>>(HttpMethod.Get,
            $"/api/playback/{deviceId}/{channelNumber}/days?year={year}&month={month}", null, ct);

    /// <summary>Concesión de reproducción de un rango grabado (hora local del equipo).</summary>
    /// <summary>
    /// Concesión de reproducción. <paramref name="speed"/> distinto de 1 hace
    /// que el servidor le pida al EQUIPO esa velocidad (el grabador entrega a
    /// tiempo real salvo que se le pida otra cosa).
    /// </summary>
    public Task<StreamGrantDto> RequestPlaybackAsync(int deviceId, int rtspChannel, DateTime startLocal, DateTime endLocal,
        double speed = 1, CancellationToken ct = default) =>
        SendAsync<StreamGrantDto>(HttpMethod.Post, "/api/playback/request",
            new PlaybackRequestDto(deviceId, rtspChannel, startLocal, endLocal, speed), ct);

    /// <summary>Orden PTZ continua (stop=false inicia, stop=true detiene).</summary>
    public Task PtzAsync(int deviceId, int channelNumber, PtzCommand command, int speed, bool stop, CancellationToken ct = default) =>
        SendAsync<object?>(HttpMethod.Post, $"/api/devices/{deviceId}/channels/{channelNumber}/ptz",
            new PtzRequestDto(command, speed, stop), ct);

    /// <summary>Operación sobre un preset PTZ (ir / guardar / borrar).</summary>
    public Task PtzPresetAsync(int deviceId, int channelNumber, PtzPresetAction action, int index, CancellationToken ct = default) =>
        SendAsync<object?>(HttpMethod.Post, $"/api/devices/{deviceId}/channels/{channelNumber}/ptz-preset",
            new PtzPresetRequestDto(action, index), ct);

    /// <summary>
    /// Exporta un tramo grabado a un archivo MP4 local. El servidor lo arma
    /// con FFmpeg mientras el equipo va entregando el video, así que la
    /// descarga avanza al ritmo del grabador y puede cancelarse. Usa su
    /// propio HttpClient SIN tiempo límite: el de la API cortaría la
    /// transferencia a los pocos segundos.
    /// </summary>
    public async Task DownloadPlaybackAsync(int deviceId, int rtspChannel, DateTime startLocal, DateTime endLocal,
        string destinationPath, IProgress<long>? progress, CancellationToken ct = default)
    {
        if (BaseUrl is null)
            throw new ApiException("Sin conexión con el servidor.");

        string path = $"/api/playback/{deviceId}/{rtspChannel}/download" +
                      $"?start={Uri.EscapeDataString(startLocal.ToString("s"))}" +
                      $"&end={Uri.EscapeDataString(endLocal.ToString("s"))}";

        using var response = await SendDownloadAsync(path, ct, allowRelogin: true);

        using var file = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;
            progress?.Report(total);
        }
        if (total == 0)
            throw new ApiException("El servidor no entregó video para ese tramo.");
    }

    private async Task<HttpResponseMessage> SendDownloadAsync(string path, CancellationToken ct, bool allowRelogin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        if (Token is not null)
            request.Headers.Authorization = new("Bearer", Token);

        HttpResponseMessage response;
        try
        {
            response = await _downloads.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            throw new ApiException("No se pudo conectar con el servidor. Verifique la URL y la red.");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && allowRelogin)
        {
            string? staleToken = Token;
            response.Dispose();
            if (await TryReloginAsync(staleToken, ct))
                return await SendDownloadAsync(path, ct, allowRelogin: false);
            throw new ApiException("La sesión expiró y no se pudo renovar. Vuelva a iniciar sesión.");
        }

        if (!response.IsSuccessStatusCode)
        {
            string? error = null;
            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("error", out var e))
                    error = e.GetString();
            }
            catch { /* cuerpo no JSON */ }
            response.Dispose();
            throw new ApiException(error ?? "No se pudo exportar el tramo.");
        }
        return response;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct,
        bool allowRelogin = true)
    {
        if (BaseUrl is null)
            throw new ApiException("Sin conexión con el servidor.");

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(method, BaseUrl + path);
            if (body is not null)
                request.Content = JsonContent.Create(body, options: Json);
            response = await _http.SendAsync(request, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException("El servidor no respondió a tiempo.");
        }
        catch (HttpRequestException)
        {
            throw new ApiException("No se pudo conectar con el servidor. Verifique la URL y la red.");
        }

        // Sesión invalidada (reinicio del servidor o caducidad): renovar el
        // token con las credenciales de esta ejecución y repetir UNA vez.
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && allowRelogin)
        {
            string? staleToken = Token;
            response.Dispose();
            if (await TryReloginAsync(staleToken, ct))
                return await SendAsync<T>(method, path, body, ct, allowRelogin: false);
            throw new ApiException("La sesión expiró y no se pudo renovar. Vuelva a iniciar sesión.");
        }

        if (!response.IsSuccessStatusCode)
        {
            string? error = null;
            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("error", out var e))
                    error = e.GetString();
            }
            catch { /* cuerpo no JSON */ }
            throw new ApiException(error ?? $"Error del servidor ({(int)response.StatusCode}).");
        }

        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }
}
