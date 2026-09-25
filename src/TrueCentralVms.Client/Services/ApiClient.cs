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

    /// <summary>Identificación para la bitácora de auditoría del servidor: con
    /// esta cabecera el origen queda como "cliente de escritorio" (y no panel
    /// web) junto con la máquina desde la que se operó.</summary>
    private static readonly string ClientIdentity =
        $"wpf/{typeof(ApiClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"} ({Environment.MachineName})";

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

    public ApiClient()
    {
        _http.DefaultRequestHeaders.Add("X-TCVMS-Client", ClientIdentity);
        _downloads.DefaultRequestHeaders.Add("X-TCVMS-Client", ClientIdentity);
    }

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
    /// Cierra la sesión en el servidor: revoca el token y deja el "Cierre de
    /// sesión" en la bitácora de auditoría. Es de mejor esfuerzo — si el
    /// servidor no responde, la sesión local se abandona igual (el token
    /// caduca solo) y el cliente vuelve al login.
    /// </summary>
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        try
        {
            if (BaseUrl is not null && Token is not null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/auth/logout");
                using var response = await _http.SendAsync(request, ct);
            }
        }
        catch { /* sin red o servidor caído: el token queda expirando solo */ }
        finally
        {
            // Sin contraseña guardada no hay relogin: nada revive esta sesión.
            Token = null;
            _password = null;
            _http.DefaultRequestHeaders.Authorization = null;
        }
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

    /// <summary>Alertas de automatizaciones (pendientes de confirmar o todas).</summary>
    public Task<WorkflowAlertListDto?> GetAlertsAsync(bool pendingOnly = true, int take = 20, CancellationToken ct = default) =>
        SendAsync<WorkflowAlertListDto?>(HttpMethod.Get,
            $"/api/workflows/alerts?pending={(pendingOnly ? "true" : "false")}&take={take}", null, ct);

    /// <summary>Detalle completo de una alerta (todas sus fotos y su ejecución).</summary>
    public Task<WorkflowAlertDto?> GetAlertAsync(long alertId, CancellationToken ct = default) =>
        SendAsync<WorkflowAlertDto?>(HttpMethod.Get, $"/api/workflows/alerts/{alertId}", null, ct);

    /// <summary>Ejecución de una automatización (los pasos que corrió y su resultado).</summary>
    public Task<WorkflowRunDto?> GetWorkflowRunAsync(long runId, CancellationToken ct = default) =>
        SendAsync<WorkflowRunDto?>(HttpMethod.Get, $"/api/workflows/runs/{runId}", null, ct);

    /// <summary>
    /// Se da por enterado de una alerta: el servidor registra quién y cuándo.
    /// Devuelve la alerta ya confirmada (si otro operador se adelantó, con SU
    /// nombre: la primera confirmación es la que vale).
    /// </summary>
    public Task<WorkflowAlertDto?> AcknowledgeAlertAsync(long alertId, CancellationToken ct = default) =>
        SendAsync<WorkflowAlertDto?>(HttpMethod.Post, $"/api/workflows/alerts/{alertId}/ack", null, ct);

    /// <summary>
    /// Sonido de alarma cargado en el servidor (los mismos de los parlantes
    /// IP). Devuelve el contenido y su nombre de archivo —la extensión importa
    /// para reproducirlo—, o null si ya no existe.
    /// </summary>
    public async Task<(byte[] Content, string FileName)?> GetWorkflowSoundAsync(string name, CancellationToken ct = default)
    {
        if (BaseUrl is null || string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{BaseUrl}/api/workflows/audio/{Uri.EscapeDataString(name)}/file");
            if (Token is not null)
                request.Headers.Authorization = new("Bearer", Token);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            string fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                              ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                              ?? name + ".wav";
            return (await response.Content.ReadAsByteArrayAsync(ct), fileName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Foto capturada por una automatización (la ruta viene en el aviso del
    /// hub). null si ya no está en el servidor.
    /// </summary>
    public async Task<byte[]?> GetWorkflowImageAsync(string relativePath, CancellationToken ct = default)
    {
        if (BaseUrl is null || string.IsNullOrWhiteSpace(relativePath)) return null;
        try
        {
            string path = string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/workflows/files/{path}");
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

    // ---------------------------------------------------------------------
    // Vistas guardadas del monitoreo en vivo (Custom View de iVMS-4200)
    // ---------------------------------------------------------------------

    /// <summary>Vistas del usuario más las compartidas por otros puestos.</summary>
    public Task<List<LiveViewDto>> GetLiveViewsAsync(CancellationToken ct = default) =>
        SendAsync<List<LiveViewDto>>(HttpMethod.Get, "/api/live-views", null, ct);

    public Task<LiveViewDto> CreateLiveViewAsync(LiveViewSaveRequest request, CancellationToken ct = default) =>
        SendAsync<LiveViewDto>(HttpMethod.Post, "/api/live-views", request, ct);

    public Task<LiveViewDto> UpdateLiveViewAsync(int id, LiveViewSaveRequest request, CancellationToken ct = default) =>
        SendAsync<LiveViewDto>(HttpMethod.Put, $"/api/live-views/{id}", request, ct);

    public Task DeleteLiveViewAsync(int id, CancellationToken ct = default) =>
        SendAsync<object?>(HttpMethod.Delete, $"/api/live-views/{id}", null, ct);

    /// <summary>Pide la versión vigente de la vista y deja el registro de que
    /// este puesto la cargó (la grilla la arma el cliente).</summary>
    public Task<LiveViewDto> ApplyLiveViewAsync(int id, CancellationToken ct = default) =>
        SendAsync<LiveViewDto>(HttpMethod.Post, $"/api/live-views/{id}/apply", null, ct);

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

    // -----------------------------------------------------------------
    // Parlantes IP
    // -----------------------------------------------------------------

    public Task<List<SpeakerDto>> GetSpeakersAsync(CancellationToken ct = default) =>
        SendAsync<List<SpeakerDto>>(HttpMethod.Get, "/api/speakers", null, ct);

    /// <summary>Biblioteca de audios guardada en el propio parlante.</summary>
    public Task<List<SpeakerAudioItemDto>> GetSpeakerLibraryAsync(int speakerId, CancellationToken ct = default) =>
        SendAsync<List<SpeakerAudioItemDto>>(HttpMethod.Get, $"/api/speakers/{speakerId}/library", null, ct);

    /// <summary>Sonidos subidos al servidor (los mismos de Automatizaciones → Sonidos).</summary>
    public Task<List<WorkflowAudioDto>> GetSpeakerSoundsAsync(CancellationToken ct = default) =>
        SendAsync<List<WorkflowAudioDto>>(HttpMethod.Get, "/api/speakers/sounds", null, ct);

    /// <summary>Reproduce en uno o más parlantes (sonido del servidor, audio de la biblioteca o texto a voz).</summary>
    public Task<SpeakerOperationResultDto> PlaySpeakersAsync(SpeakerPlayRequestDto request, CancellationToken ct = default) =>
        SendAsync<SpeakerOperationResultDto>(HttpMethod.Post, "/api/speakers/play", request, ct);

    public Task<SpeakerOperationResultDto> StopSpeakersAsync(IReadOnlyList<int> speakerIds, CancellationToken ct = default) =>
        SendAsync<SpeakerOperationResultDto>(HttpMethod.Post, "/api/speakers/stop", new SpeakerStopRequestDto(speakerIds), ct);

    /// <summary>Archivo de un audio de la biblioteca del parlante, para escucharlo en este equipo sin hacerlo sonar afuera.</summary>
    public async Task<(byte[] Content, string FileName)?> GetSpeakerAudioFileAsync(int speakerId, long audioId, CancellationToken ct = default)
    {
        using var response = await SendDownloadAsync($"/api/speakers/{speakerId}/library/{audioId}/file", ct, allowRelogin: true);
        if (!response.IsSuccessStatusCode) return null;
        string name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? $"audio-{audioId}";
        return (await response.Content.ReadAsByteArrayAsync(ct), name);
    }

    public Task<SpeakerDto> SetSpeakerVolumeAsync(int speakerId, int volume, CancellationToken ct = default) =>
        SendAsync<SpeakerDto>(HttpMethod.Put, $"/api/speakers/{speakerId}/volume", new SpeakerVolumeRequestDto(volume), ct);

    // -----------------------------------------------------------------
    // Citofonía
    // -----------------------------------------------------------------

    public Task<List<IntercomDto>> GetIntercomsAsync(CancellationToken ct = default) =>
        SendAsync<List<IntercomDto>>(HttpMethod.Get, "/api/intercoms", null, ct);

    /// <summary>Llamadas sonando o en conversación ahora (al conectar, para no perder una que ya suena).</summary>
    public Task<List<IntercomCallDto>> GetActiveIntercomCallsAsync(CancellationToken ct = default) =>
        SendAsync<List<IntercomCallDto>>(HttpMethod.Get, "/api/intercoms/calls/active", null, ct);

    public Task<IntercomCallPageDto> GetIntercomCallsAsync(int? intercomId, int skip, int take, CancellationToken ct = default) =>
        SendAsync<IntercomCallPageDto>(HttpMethod.Get,
            $"/api/intercoms/calls?skip={skip}&take={take}{(intercomId is int id ? $"&intercomId={id}" : "")}", null, ct);

    public Task<IntercomActionResultDto> AnswerIntercomCallAsync(long callId, CancellationToken ct = default) =>
        SendAsync<IntercomActionResultDto>(HttpMethod.Post, $"/api/intercoms/calls/{callId}/answer", null, ct);

    public Task<IntercomActionResultDto> RejectIntercomCallAsync(long callId, CancellationToken ct = default) =>
        SendAsync<IntercomActionResultDto>(HttpMethod.Post, $"/api/intercoms/calls/{callId}/reject", null, ct);

    public Task<IntercomActionResultDto> HangUpIntercomCallAsync(long callId, CancellationToken ct = default) =>
        SendAsync<IntercomActionResultDto>(HttpMethod.Post, $"/api/intercoms/calls/{callId}/hangup", null, ct);

    public Task<IntercomActionResultDto> OpenIntercomDoorAsync(int intercomId, int door, CancellationToken ct = default) =>
        SendAsync<IntercomActionResultDto>(HttpMethod.Post, $"/api/intercoms/{intercomId}/doors/{door}/open", null, ct);

    // -----------------------------------------------------------------
    // Paneles de alarma
    // -----------------------------------------------------------------

    // ---------- Paneles de cerco eléctrico ----------

    public Task<List<CercoPanelDto>> GetCercoPanelsAsync(CancellationToken ct = default) =>
        SendAsync<List<CercoPanelDto>>(HttpMethod.Get, "/api/cerco/panels", null, ct);

    /// <summary>Últimos eventos de cerco (todos los paneles).</summary>
    public Task<List<CercoEventDto>> GetCercoEventsAsync(int take, CancellationToken ct = default) =>
        SendAsync<List<CercoEventDto>>(HttpMethod.Get, $"/api/cerco/events?take={take}", null, ct);

    /// <summary>Orden al panel: "arm", "disarm" o "silence". El estado resultante llega por el hub.</summary>
    public Task SendCercoCommandAsync(int panelId, string command, CancellationToken ct = default) =>
        SendAsync<object?>(HttpMethod.Post, $"/api/cerco/panels/{panelId}/{command}", null, ct);

    /// <summary>Paneles de alarma con el estado de sus áreas y zonas.</summary>
    public Task<List<AlarmPanelDto>> GetAlarmPanelsAsync(CancellationToken ct = default) =>
        SendAsync<List<AlarmPanelDto>>(HttpMethod.Get, "/api/alarms/panels", null, ct);

    /// <summary>Pide al servidor releer el estado del panel ahora mismo.</summary>
    public Task<AlarmPanelDto> RefreshAlarmPanelAsync(int panelId, CancellationToken ct = default) =>
        SendAsync<AlarmPanelDto>(HttpMethod.Post, $"/api/alarms/panels/{panelId}/refresh", null, ct);

    /// <summary>Arma un área (0 = todas). La respuesta trae el estado resultante.</summary>
    public Task<AlarmPanelDto> ArmAlarmAreaAsync(int panelId, int area, AlarmArmMode mode, CancellationToken ct = default) =>
        SendAsync<AlarmPanelDto>(HttpMethod.Post, $"/api/alarms/panels/{panelId}/areas/{area}/arm", new AlarmArmRequestDto(mode), ct);

    public Task<AlarmPanelDto> DisarmAlarmAreaAsync(int panelId, int area, CancellationToken ct = default) =>
        SendAsync<AlarmPanelDto>(HttpMethod.Post, $"/api/alarms/panels/{panelId}/areas/{area}/disarm", null, ct);

    /// <summary>Silencia/borra la alarma activa de un área (0 = todas).</summary>
    public Task<AlarmPanelDto> ClearAlarmAsync(int panelId, int area, CancellationToken ct = default) =>
        SendAsync<AlarmPanelDto>(HttpMethod.Post, $"/api/alarms/panels/{panelId}/areas/{area}/clear-alarm", null, ct);

    /// <summary>Anula (bypass) o restituye una zona.</summary>
    public Task<AlarmPanelDto> SetAlarmZoneBypassAsync(int panelId, int zone, bool bypassed, CancellationToken ct = default) =>
        SendAsync<AlarmPanelDto>(HttpMethod.Post, $"/api/alarms/panels/{panelId}/zones/{zone}/bypass",
            new AlarmZoneBypassRequestDto(bypassed), ct);

    /// <summary>Historial de eventos de alarma, del más reciente al más antiguo.</summary>
    public Task<List<AlarmEventDto>> GetAlarmEventsAsync(int? panelId = null, string? kind = null,
        DateTime? from = null, DateTime? to = null, string? text = null, int take = 200, CancellationToken ct = default)
    {
        var query = new List<string> { $"take={take}" };
        if (panelId is > 0) query.Add($"panelId={panelId}");
        if (!string.IsNullOrWhiteSpace(kind)) query.Add($"kind={Uri.EscapeDataString(kind)}");
        if (from is { } f) query.Add($"from={Uri.EscapeDataString(f.ToUniversalTime().ToString("o"))}");
        if (to is { } t) query.Add($"to={Uri.EscapeDataString(t.ToUniversalTime().ToString("o"))}");
        if (!string.IsNullOrWhiteSpace(text)) query.Add($"text={Uri.EscapeDataString(text)}");
        return SendAsync<List<AlarmEventDto>>(HttpMethod.Get, $"/api/alarms/events?{string.Join('&', query)}", null, ct);
    }

    /// <summary>Estado del licenciamiento del servidor (avisos del cliente y apartado Licencia).</summary>
    public Task<LicenseStatusDto> GetLicenseAsync(CancellationToken ct = default) =>
        SendAsync<LicenseStatusDto>(HttpMethod.Get, "/api/system/license", null, ct);

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

    /// <summary>
    /// Motivo por el que el EQUIPO rechazó una reproducción (el media server
    /// solo devuelve un error genérico al lector). null si no hay motivo
    /// registrado o el servidor no lo soporta.
    /// </summary>
    public async Task<string?> GetPlaybackFailureAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var failure = await SendAsync<PlaybackFailureDto>(HttpMethod.Get,
                $"/api/playback/failure/{Uri.EscapeDataString(path)}", null, ct);
            return failure.Message;
        }
        catch
        {
            return null; // sin conexión o servidor antiguo: se muestra el error original
        }
    }

    private sealed record PlaybackFailureDto(int Code, string? Reason, string? Message);

    /// <summary>Orden PTZ continua (stop=false inicia, stop=true detiene).</summary>
    public Task PtzAsync(int deviceId, int channelNumber, PtzCommand command, int speed, bool stop, CancellationToken ct = default) =>
        SendAsync<object?>(HttpMethod.Post, $"/api/devices/{deviceId}/channels/{channelNumber}/ptz",
            new PtzRequestDto(command, speed, stop), ct);

    /// <summary>Operación sobre un preset PTZ (ir / guardar / borrar).</summary>
    public Task PtzPresetAsync(int deviceId, int channelNumber, PtzPresetAction action, int index, CancellationToken ct = default) =>
        SendAsync<object?>(HttpMethod.Post, $"/api/devices/{deviceId}/channels/{channelNumber}/ptz-preset",
            new PtzPresetRequestDto(action, index), ct);

    /// <summary>
    /// Reporta a la bitácora de auditoría del servidor un evento que ocurrió
    /// en esta máquina (captura, grabación local, exportación guardada). Nunca
    /// lanza: la auditoría no debe romper la operación que el usuario ya hizo.
    /// </summary>
    public async Task ReportAuditEventAsync(ClientAuditEventDto request)
    {
        try
        {
            await SendAsync<object?>(HttpMethod.Post, "/api/audit/client-event", request, CancellationToken.None);
        }
        catch
        {
            // Sin conexión o servidor antiguo: el archivo local ya existe igual.
        }
    }

    /// <summary>
    /// Exporta un tramo grabado a un archivo MP4 local. El servidor lo arma
    /// con FFmpeg mientras el equipo va entregando el video, así que la
    /// descarga avanza al ritmo del grabador y puede cancelarse. Usa su
    /// propio HttpClient SIN tiempo límite: el de la API cortaría la
    /// transferencia a los pocos segundos.
    /// </summary>
    public async Task DownloadPlaybackAsync(int deviceId, int rtspChannel, DateTime startLocal, DateTime endLocal,
        string destinationPath, IProgress<long>? progress, string format = "mp4", CancellationToken ct = default)
    {
        if (BaseUrl is null)
            throw new ApiException("Sin conexión con el servidor.");

        string path = $"/api/playback/{deviceId}/{rtspChannel}/download" +
                      $"?start={Uri.EscapeDataString(startLocal.ToString("s"))}" +
                      $"&end={Uri.EscapeDataString(endLocal.ToString("s"))}" +
                      $"&format={Uri.EscapeDataString(format)}";

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

        // Un servidor más ANTIGUO no conoce la ruta y, en vez de un 404,
        // devuelve el HTML del panel (la aplicación de una sola página atiende
        // todo lo que no sea API). Sin esto, el cliente moriría intentando
        // leer ese HTML como JSON y el usuario vería un error incomprensible.
        if (response.Content.Headers.ContentType?.MediaType == "text/html")
            throw new ApiException(
                "El servidor no reconoce esta función: probablemente tenga una versión más antigua que este cliente.");

        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }
}
