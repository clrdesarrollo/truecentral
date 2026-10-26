using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.PatrolAgent;

/// <summary>Error que el servidor devolvió con mensaje (4xx/5xx con {"error": ...}).</summary>
public sealed class ServerRejectedException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// HTTPS saliente hacia el servidor. El servidor de TrueCentral suele tener un
/// certificado autofirmado: al enrolarse se fija la huella de su clave pública
/// (SPKI) y desde ahí solo se acepta ese certificado o uno válido de una CA.
/// </summary>
public sealed class ServerClient : IDisposable
{
    private readonly HttpClient _http;
    private string? _seenSpki;

    public ServerClient(string serverUrl, string? pinnedSpki, string? token)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                if (cert is null) return false;
                _seenSpki = Spki(cert);
                if (errors == SslPolicyErrors.None) return true;
                // Sin huella fijada (enrolamiento): se confía la primera vez y se guarda.
                return pinnedSpki is null || string.Equals(pinnedSpki, _seenSpki, StringComparison.OrdinalIgnoreCase);
            },
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };
        if (token is not null)
            _http.DefaultRequestHeaders.Authorization = new(PatrolAgentProtocol.AuthScheme, token);
    }

    /// <summary>Huella SPKI del certificado que presentó el servidor en la última conexión.</summary>
    public string? SeenSpki => _seenSpki;

    public static string Spki(X509Certificate2 cert) =>
        Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));

    public Task<PatrolAgentEnrollResponse> EnrollAsync(PatrolAgentEnrollRequest request, CancellationToken ct) =>
        PostAsync<PatrolAgentEnrollResponse>("api/patrol-agent/enroll", request, ct);

    public Task<PatrolAgentHeartbeatResponse> HeartbeatAsync(PatrolAgentHeartbeat request, CancellationToken ct) =>
        PostAsync<PatrolAgentHeartbeatResponse>("api/patrol-agent/heartbeat", request, ct);

    public Task<PatrolUploadResultDto> UploadAsync(PatrolAgentUpload request, CancellationToken ct) =>
        PostAsync<PatrolUploadResultDto>("api/patrol-agent/uploads", request, ct);

    public Task EraseReportAsync(PatrolAgentEraseReport request, CancellationToken ct) =>
        PostAsync<object>("api/patrol-agent/erase-report", request, ct);

    public Task CommandResultAsync(PatrolAgentCommandResult request, CancellationToken ct) =>
        PostAsync<object>("api/patrol-agent/command-result", request, ct);

    public Task EventAsync(PatrolAgentReaderEvent request, CancellationToken ct) =>
        PostAsync<object>("api/patrol-agent/events", request, ct);

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(path, body, ct);
        if (!response.IsSuccessStatusCode)
        {
            string message = $"El servidor respondió {(int)response.StatusCode}.";
            try
            {
                var error = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(ct);
                if (error?.GetValueOrDefault("error") is { } e) message = e.ToString()!;
            }
            catch (Exception) { /* cuerpo sin JSON */ }
            throw new ServerRejectedException((int)response.StatusCode, message);
        }
        if (response.Content.Headers.ContentLength == 0 || typeof(T) == typeof(object)) return default!;
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    public void Dispose() => _http.Dispose();
}
