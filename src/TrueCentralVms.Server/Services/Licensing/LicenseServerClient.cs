using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrueCentralVms.Server.Services.Licensing;

/// <summary>
/// Cliente HTTP de la API de productos del servidor central de licencias
/// (<c>/api/v1/</c>). Convención de ese servidor: los errores de negocio
/// llegan como HTTP 200 con <c>success:false</c> y un mensaje; los 4xx son
/// payloads malformados o API key inválida. Los fallos de red se devuelven
/// como <see cref="LicenseServerResult.Reachable"/> = false para que el
/// llamador distinga "el servidor dijo que no" de "no hubo respuesta".
/// </summary>
public sealed class LicenseServerClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ILogger _logger;

    public LicenseServerClient(string? serverUrl, string? apiKey, ILogger logger)
    {
        _logger = logger;
        ServerUrl = string.IsNullOrWhiteSpace(serverUrl) ? null : serverUrl.Trim().TrimEnd('/');
        ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (ApiKey is not null)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Api-Key", ApiKey);
    }

    public string? ServerUrl { get; }
    public string? ApiKey { get; }

    /// <summary>true si hay URL y API key configuradas (activación/heartbeat en línea posibles).</summary>
    public bool IsConfigured => ServerUrl is not null && ApiKey is not null;

    public Task<LicenseServerResult> ActivateAsync(string activationCode, string hardwareId, string hostname,
        string osInfo, string appVersion, CancellationToken ct) =>
        PostAsync("/api/v1/licenses/activate/", new
        {
            activation_code = activationCode,
            hardware_id = hardwareId,
            hostname,
            os_info = osInfo,
            app_version = appVersion,
        }, ct);

    public Task<LicenseServerResult> ValidateAsync(string activationCode, string hardwareId, string hostname,
        string appVersion, CancellationToken ct) =>
        PostAsync("/api/v1/licenses/validate/", new
        {
            activation_code = activationCode,
            hardware_id = hardwareId,
            hostname,
            app_version = appVersion,
        }, ct);

    public Task<LicenseServerResult> DeactivateAsync(string activationCode, string hardwareId, CancellationToken ct) =>
        PostAsync("/api/v1/licenses/deactivate/", new
        {
            activation_code = activationCode,
            hardware_id = hardwareId,
        }, ct);

    /// <summary>
    /// Ingreso de una expansión en esta instalación. Si se vendió suelta («por
    /// canjear»), el servidor la liga en este momento a la licencia base de
    /// este equipo (exige la base activada aquí, por eso va el equipo); si ya
    /// estaba ligada, confirma que sea de esta base y esté vigente. Los cupos
    /// llegan en el archivo de la base al revalidar.
    /// </summary>
    public Task<LicenseServerResult> CheckAddonAsync(string addonCode, string activationCode, string hardwareId,
        CancellationToken ct) =>
        PostAsync("/api/v1/licenses/addon/", new
        {
            addon_code = addonCode,
            activation_code = activationCode,
            hardware_id = hardwareId,
        }, ct);

    private async Task<LicenseServerResult> PostAsync(string path, object body, CancellationToken ct)
    {
        if (!IsConfigured)
            return LicenseServerResult.Unreachable("El servidor de licencias no está configurado (Licensing:ServerUrl / Licensing:ApiKey).");

        HttpResponseMessage response;
        string text;
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(body, Json), System.Text.Encoding.UTF8, "application/json");
            response = await _http.PostAsync(ServerUrl + path, content, ct);
            text = await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return LicenseServerResult.Unreachable("El servidor de licencias no respondió a tiempo.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            _logger.LogDebug(ex, "Servidor de licencias inalcanzable ({Url}).", ServerUrl);
            return LicenseServerResult.Unreachable($"Sin conexión con el servidor de licencias: {ex.Message}");
        }

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            return LicenseServerResult.Unreachable("El servidor de licencias rechazó la API key del producto (Licensing:ApiKey).");
        // Límite de peticiones por IP del servidor de licencias: como sin
        // conexión, la licencia sigue en gracia.
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            return LicenseServerResult.Unreachable("El servidor de licencias recibió demasiadas consultas desde esta red; intente de nuevo en unos minutos.");
        if (!response.IsSuccessStatusCode)
            return LicenseServerResult.Unreachable($"El servidor de licencias respondió HTTP {(int)response.StatusCode}.");

        LicenseServerResponse? parsed;
        try { parsed = JsonSerializer.Deserialize<LicenseServerResponse>(text, Json); }
        catch (JsonException) { return LicenseServerResult.Unreachable("Respuesta ilegible del servidor de licencias."); }
        if (parsed is null)
            return LicenseServerResult.Unreachable("Respuesta vacía del servidor de licencias.");

        string? licenseFile = parsed.LicenseFile is { ValueKind: JsonValueKind.Object } element
            ? element.GetRawText()
            : null;
        return new LicenseServerResult(
            Reachable: true,
            Success: parsed.Success,
            Valid: parsed.Valid,
            Status: parsed.Status,
            Message: parsed.Message ?? "",
            LicenseFileJson: licenseFile,
            Summary: parsed.Summary);
    }

    private sealed class LicenseServerResponse
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("valid")] public bool? Valid { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("license_file")] public JsonElement? LicenseFile { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
    }
}

/// <summary>
/// Respuesta del servidor de licencias. <paramref name="Reachable"/> false =
/// no hubo respuesta útil (red, timeout, API key). <paramref name="Valid"/>
/// aplica a validate y a la consulta de expansión; <paramref name="Summary"/>,
/// solo a esta última (lo que aporta la expansión).
/// </summary>
public sealed record LicenseServerResult(bool Reachable, bool Success, bool? Valid, string? Status, string Message,
    string? LicenseFileJson, string? Summary = null)
{
    public static LicenseServerResult Unreachable(string message) => new(false, false, null, null, message, null);
}
