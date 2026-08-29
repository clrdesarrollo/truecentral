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

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

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

    /// <summary>Uso de CPU/RAM/disco de la máquina del servidor (indicadores del navbar).</summary>
    public Task<SystemMetricsDto> GetSystemMetricsAsync(CancellationToken ct = default) =>
        SendAsync<SystemMetricsDto>(HttpMethod.Get, "/api/system/metrics", null, ct);

    public Task<StreamGrantDto> RequestStreamAsync(int deviceId, int rtspChannel, StreamProfile profile, CancellationToken ct = default) =>
        SendAsync<StreamGrantDto>(HttpMethod.Post, "/api/streams/request",
            new StreamRequestDto(deviceId, rtspChannel, profile), ct);

    /// <summary>Orden PTZ continua (stop=false inicia, stop=true detiene).</summary>
    public Task PtzAsync(int deviceId, int channelNumber, PtzCommand command, int speed, bool stop, CancellationToken ct = default) =>
        SendAsync<object?>(HttpMethod.Post, $"/api/devices/{deviceId}/channels/{channelNumber}/ptz",
            new PtzRequestDto(command, speed, stop), ct);

    /// <summary>Operación sobre un preset PTZ (ir / guardar / borrar).</summary>
    public Task PtzPresetAsync(int deviceId, int channelNumber, PtzPresetAction action, int index, CancellationToken ct = default) =>
        SendAsync<object?>(HttpMethod.Post, $"/api/devices/{deviceId}/channels/{channelNumber}/ptz-preset",
            new PtzPresetRequestDto(action, index), ct);

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
