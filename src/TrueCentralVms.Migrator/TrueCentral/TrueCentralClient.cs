using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Migrator.TrueCentral;

public sealed class TrueCentralException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>Lo mínimo del padrón existente para no duplicar: identificador, nombre y tarjetas.</summary>
public sealed record ExistingPerson(int Id, string EmployeeNo, string FullName, IReadOnlyList<string> Cards);

/// <summary>
/// Cliente de la API del servidor TrueCentral para el alta del padrón. No
/// manda la cabecera <c>X-TCVMS-Client</c> a propósito: esa la usa el
/// cliente de escritorio y consume un puesto de la licencia; el migrador
/// queda registrado en la bitácora por su User-Agent.
/// </summary>
public sealed class TrueCentralClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;
    private string _baseUrl = "";

    public TrueCentralClient(bool ignoreCertificate = true)
    {
        var handler = new HttpClientHandler();
        if (ignoreCertificate)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"TrueCentral-Migrador/{Version}");
    }

    public static string Version =>
        typeof(TrueCentralClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public string BaseUrl => _baseUrl;
    public string? Username { get; private set; }
    public string? Role { get; private set; }

    public void Dispose() => _http.Dispose();

    public static string NormalizeUrl(string url)
    {
        string u = url.Trim().TrimEnd('/');
        if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            u = "http://" + u;
        if (Uri.TryCreate(u, UriKind.Absolute, out var uri)) u = uri.GetLeftPart(UriPartial.Authority);
        return u;
    }

    public async Task LoginAsync(string serverUrl, string username, string password, CancellationToken ct)
    {
        _baseUrl = NormalizeUrl(serverUrl);
        _http.DefaultRequestHeaders.Authorization = null;
        var login = await SendAsync<LoginResponse>(HttpMethod.Post, "/api/auth/login", new LoginRequest(username, password), ct);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        Username = login.Username;
        Role = login.Role;
        if (!string.Equals(login.Role, "Admin", StringComparison.OrdinalIgnoreCase))
            throw new TrueCentralException($"El usuario '{login.Username}' tiene rol {login.Role}: para dar de alta personas hace falta un administrador.");
    }

    /// <summary>Todo el padrón actual (paginado de a 500).</summary>
    public async Task<List<ExistingPerson>> GetPersonsAsync(CancellationToken ct)
    {
        var result = new List<ExistingPerson>();
        int page = 1;
        while (true)
        {
            var pageDto = await SendAsync<PersonPage>(HttpMethod.Get, $"/api/access/persons?page={page}&pageSize=500", null, ct);
            foreach (var p in pageDto.Items)
                result.Add(new ExistingPerson(p.Id, p.EmployeeNo, p.FullName, p.Cards.Select(c => c.Number).ToList()));
            if (pageDto.Items.Count < 500 || result.Count >= pageDto.Total) break;
            page++;
        }
        return result;
    }

    /// <summary>Niveles de acceso disponibles, para ofrecer asignar uno a las personas importadas.</summary>
    public Task<List<AccessLevelDto>> GetLevelsAsync(CancellationToken ct) =>
        SendAsync<List<AccessLevelDto>>(HttpMethod.Get, "/api/access/levels", null, ct);

    public Task<AccessPersonDto> CreatePersonAsync(AccessPersonWriteDto person, CancellationToken ct) =>
        SendAsync<AccessPersonDto>(HttpMethod.Post, "/api/access/persons", person, ct);

    /// <summary>Ficha completa de una persona (qué dedos tiene, si tiene rostro, tarjetas, niveles).</summary>
    public Task<AccessPersonDto> GetPersonAsync(int id, CancellationToken ct) =>
        SendAsync<AccessPersonDto>(HttpMethod.Get, $"/api/access/persons/{id}", null, ct);

    public Task<AccessPersonDto> UpdatePersonAsync(int id, AccessPersonWriteDto person, CancellationToken ct) =>
        SendAsync<AccessPersonDto>(HttpMethod.Put, $"/api/access/persons/{id}", person, ct);

    // ------------------------------------------------------------------

    private sealed record PersonPage(int Total, int Page, int PageSize, List<AccessPersonDto> Items);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, _baseUrl + path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);

        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, ct); }
        catch (HttpRequestException ex)
        {
            throw new TrueCentralException($"No se pudo conectar con TrueCentral en {_baseUrl}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TrueCentralException($"TrueCentral no respondió a tiempo ({path}).");
        }

        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new TrueCentralException(DescribeError(response.StatusCode, text), response.StatusCode);
            try
            {
                return JsonSerializer.Deserialize<T>(text, Json)
                       ?? throw new TrueCentralException($"TrueCentral devolvió una respuesta vacía a {path}.");
            }
            catch (JsonException)
            {
                throw new TrueCentralException($"TrueCentral devolvió algo inesperado a {path}: {Trim(text)}");
            }
        }
    }

    private static string DescribeError(HttpStatusCode status, string body)
    {
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var err)) message = err.GetString();
        }
        catch (JsonException) { /* no era JSON */ }

        return status switch
        {
            HttpStatusCode.Unauthorized => message ?? "TrueCentral rechazó las credenciales.",
            HttpStatusCode.Forbidden => message ?? "El usuario no tiene permiso para esta operación.",
            HttpStatusCode.PaymentRequired => message ?? "La licencia de TrueCentral no incluye el módulo de control de acceso.",
            HttpStatusCode.NotFound => "TrueCentral no tiene esa ruta: ¿la dirección apunta al servidor VMS y la versión incluye control de acceso?",
            _ => message ?? $"TrueCentral respondió HTTP {(int)status}: {Trim(body)}",
        };
    }

    private static string Trim(string text) => text.Length > 200 ? text[..200] + "…" : text;
}
