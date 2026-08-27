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
/// </summary>
public sealed class ApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public string? BaseUrl { get; private set; }
    public string? Token { get; private set; }
    public string? Username { get; private set; }
    public string? Role { get; private set; }

    public async Task LoginAsync(string serverUrl, string username, string password, CancellationToken ct = default)
    {
        BaseUrl = serverUrl.TrimEnd('/');
        var login = await SendAsync<LoginResponse>(HttpMethod.Post, "/api/auth/login",
            new LoginRequest(username, password), ct);
        Token = login.Token;
        Username = login.Username;
        Role = login.Role;
        _http.DefaultRequestHeaders.Authorization = new("Bearer", login.Token);
    }

    public Task<List<DeviceDto>> GetDevicesAsync(CancellationToken ct = default) =>
        SendAsync<List<DeviceDto>>(HttpMethod.Get, "/api/devices", null, ct);

    public Task<List<ChannelDto>> GetChannelsAsync(int deviceId, CancellationToken ct = default) =>
        SendAsync<List<ChannelDto>>(HttpMethod.Get, $"/api/devices/{deviceId}/channels", null, ct);

    public Task<StreamGrantDto> RequestStreamAsync(int deviceId, int rtspChannel, StreamProfile profile, CancellationToken ct = default) =>
        SendAsync<StreamGrantDto>(HttpMethod.Post, "/api/streams/request",
            new StreamRequestDto(deviceId, rtspChannel, profile), ct);

    /// <summary>Orden PTZ continua (stop=false inicia, stop=true detiene).</summary>
    public Task PtzAsync(int deviceId, int channelNumber, PtzCommand command, int speed, bool stop, CancellationToken ct = default) =>
        SendAsync<object?>(HttpMethod.Post, $"/api/devices/{deviceId}/channels/{channelNumber}/ptz",
            new PtzRequestDto(command, speed, stop), ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
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
