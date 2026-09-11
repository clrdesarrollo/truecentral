using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrueCentralVms.Migrator.HikCentral;

/// <summary>Departamento de HikCentral.</summary>
public sealed record HcpOrganization(string IndexCode, string Name, string ParentIndexCode);

/// <summary>Huella tal como la lista HikCentral: en 3.1 solo el índice y el nombre, sin plantilla.</summary>
public sealed record HcpFingerprint(string IndexCode, string Name, string Data, string RelatedCardNo);

public sealed record HcpPerson(
    string PersonId,
    string PersonCode,
    string OrgIndexCode,
    string FullName,
    string FamilyName,
    string GivenName,
    int Gender,
    string Phone,
    string Email,
    string Remark,
    DateTimeOffset? BeginTime,
    DateTimeOffset? EndTime,
    string PicUri,
    IReadOnlyList<string> Cards,
    IReadOnlyList<HcpFingerprint> Fingerprints);

/// <summary>Terminal de control de acceso registrado en HikCentral.</summary>
public sealed record HcpAccessDevice(string IndexCode, string Name, string Ip, int SdkPort, string Code, int Status);

public sealed class HikCentralException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
}

/// <summary>
/// Cliente de la OpenAPI de HikCentral Professional (pasarela "Artemis").
/// Cada petición va firmada con HMAC-SHA256 sobre método, Accept,
/// Content-Type, las cabeceras x-ca-* y la ruta; el secreto nunca viaja.
/// Verificado contra HCP 3.1.0: las rutas que no existen en la versión
/// contestan código 8 "This product version is not supported".
/// </summary>
public sealed class HikCentralClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _appKey;
    private readonly byte[] _secret;

    public HikCentralClient(string baseUrl, string appKey, string appSecret, bool ignoreCertificate = true)
    {
        _baseUrl = NormalizeUrl(baseUrl);
        _appKey = appKey.Trim();
        _secret = Encoding.UTF8.GetBytes(appSecret.Trim());
        var handler = new HttpClientHandler();
        if (ignoreCertificate)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public string BaseUrl => _baseUrl;

    /// <summary>"200.55.209.84" → "https://200.55.209.84"; se respeta el puerto si viene.</summary>
    public static string NormalizeUrl(string url)
    {
        string u = url.Trim().TrimEnd('/');
        if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            u = "https://" + u;
        // Si pegaron la URL del panel web (…/portal/…), quedarse con el origen.
        if (Uri.TryCreate(u, UriKind.Absolute, out var uri)) u = uri.GetLeftPart(UriPartial.Authority);
        return u;
    }

    public void Dispose() => _http.Dispose();

    // ------------------------------------------------------------------
    // Transporte firmado
    // ------------------------------------------------------------------

    /// <summary>Llama una ruta y devuelve el <c>data</c> de la respuesta (o el cuerpo completo si no trae sobre).</summary>
    public async Task<JsonElement> CallAsync(string path, object? body, CancellationToken ct)
    {
        var (text, contentType, status) = await SendAsync(path, body, ct);
        return ParseEnvelope(text, contentType, status, path);
    }

    /// <summary>Envía la petición firmada y devuelve el cuerpo tal cual, con su tipo.</summary>
    private async Task<(string Text, string ContentType, System.Net.HttpStatusCode Status)> SendAsync(string path, object? body, CancellationToken ct)
    {
        string payload = JsonSerializer.Serialize(body ?? new { }, Json);
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        string nonce = Guid.NewGuid().ToString();
        const string accept = "*/*";
        const string contentType = "application/json";

        string toSign = string.Join("\n", "POST", accept, contentType,
            $"x-ca-key:{_appKey}", $"x-ca-nonce:{nonce}", $"x-ca-timestamp:{timestamp}", path);
        string signature = Convert.ToBase64String(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(toSign)));

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path);
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.Add("x-ca-key", _appKey);
        request.Headers.Add("x-ca-nonce", nonce);
        request.Headers.Add("x-ca-timestamp", timestamp);
        request.Headers.Add("x-ca-signature-headers", "x-ca-key,x-ca-nonce,x-ca-timestamp");
        request.Headers.Add("x-ca-signature", signature);
        request.Content = new StringContent(payload, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, ct); }
        catch (HttpRequestException ex)
        {
            throw new HikCentralException($"No se pudo conectar con HikCentral en {_baseUrl}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HikCentralException($"HikCentral no respondió a tiempo ({path}).");
        }

        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new HikCentralException("HikCentral no expone la OpenAPI en esa dirección (404 en /artemis). ¿Es la URL del servidor HCP?");
            if (!response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(text))
                throw new HikCentralException($"HikCentral respondió HTTP {(int)response.StatusCode} a {path}.");
            return (text, response.Content.Headers.ContentType?.MediaType ?? "", response.StatusCode);
        }
    }

    private static JsonElement ParseEnvelope(string text, string contentType, System.Net.HttpStatusCode status, string path)
    {
        // La pasarela contesta JSON aunque anuncie application/xml en los errores.
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException)
        {
            throw new HikCentralException($"HikCentral respondió algo que no es JSON a {path} (HTTP {(int)status}, {contentType}).");
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var codeEl))
            {
                string code = codeEl.ToString();
                if (code != "0")
                {
                    string msg = root.TryGetProperty("msg", out var m) ? m.ToString() : "error sin descripción";
                    throw new HikCentralException(Describe(code, msg, path), code);
                }
                return root.TryGetProperty("data", out var data) ? data.Clone() : root.Clone();
            }
            return root.Clone();
        }
    }

    private static string Describe(string code, string msg, string path) => code switch
    {
        "8" => $"Esta versión de HikCentral no tiene la ruta {path}.",
        "9" or "13" => "HikCentral rechazó la firma: revise la clave y el secreto del socio de integración (Integration Partner).",
        "0x00052101" or "0x00052102" => "HikCentral rechazó la clave del socio de integración (AppKey desconocida o deshabilitada).",
        _ => $"HikCentral respondió [{code}] {msg} ({path}).",
    };

    // ------------------------------------------------------------------
    // Consultas
    // ------------------------------------------------------------------

    public async Task<string> GetVersionAsync(CancellationToken ct)
    {
        var data = await CallAsync("/artemis/api/common/v1/version", null, ct);
        string product = Str(data, "produceName") ?? "HikCentral";
        string version = Str(data, "softVersion") ?? "?";
        return $"{product} {version}";
    }

    public async Task<List<HcpOrganization>> GetOrganizationsAsync(CancellationToken ct)
    {
        var result = new List<HcpOrganization>();
        await foreach (var item in PagedAsync("/artemis/api/resource/v1/org/orgList", 500, ct))
            result.Add(new HcpOrganization(Str(item, "orgIndexCode") ?? "", Str(item, "orgName") ?? "",
                Str(item, "parentOrgIndexCode") ?? ""));
        return result;
    }

    public async Task<List<HcpPerson>> GetPersonsAsync(IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var result = new List<HcpPerson>();
        int total = 0;
        await foreach (var item in PagedAsync("/artemis/api/resource/v1/person/personList", 500, ct, t => total = t))
        {
            result.Add(ParsePerson(item));
            progress?.Report((result.Count, total));
        }
        return result;
    }

    public async Task<List<HcpAccessDevice>> GetAccessDevicesAsync(CancellationToken ct)
    {
        var result = new List<HcpAccessDevice>();
        await foreach (var item in PagedAsync("/artemis/api/resource/v1/acsDevice/acsDeviceList", 500, ct))
            result.Add(new HcpAccessDevice(
                Str(item, "acsDevIndexCode") ?? "",
                Str(item, "acsDevName") ?? "",
                Str(item, "acsDevIp") ?? "",
                int.TryParse(Str(item, "acsDevPort"), out int port) ? port : 8000,
                Str(item, "acsDevCode") ?? "",
                Int(item, "status") ?? 0));
        return result;
    }

    /// <summary>
    /// Foto de carnet de la persona. HikCentral la devuelve como
    /// <c>data:image/jpeg;base64,…</c>; se entrega decodificada con su tipo.
    /// </summary>
    public async Task<(byte[] Bytes, string ContentType)?> GetPersonPhotoAsync(string personId, string picUri, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(picUri)) return null;
        // Verificado contra HCP 3.1: la foto llega como texto "data:image/jpeg;base64,…"
        // con Content-Type image/jpeg, SIN el sobre {code,msg,data}; los errores sí
        // vienen con sobre JSON.
        var (text, mediaType, status) = await SendAsync("/artemis/api/resource/v1/person/picture_data", new { personId, picUri }, ct);
        string? uri;
        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) uri = text.Trim();
        else
        {
            var data = ParseEnvelope(text, mediaType, status, "/artemis/api/resource/v1/person/picture_data");
            uri = data.ValueKind == JsonValueKind.String ? data.GetString() : data.ToString();
        }
        if (string.IsNullOrWhiteSpace(uri)) return null;

        string contentType = "image/jpeg";
        string b64 = uri;
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = uri.IndexOf(',');
            if (comma < 0) return null;
            string header = uri[5..comma];                       // image/jpeg;base64
            int semi = header.IndexOf(';');
            contentType = (semi > 0 ? header[..semi] : header).Trim().ToLowerInvariant();
            b64 = uri[(comma + 1)..];
        }
        try
        {
            byte[] bytes = Convert.FromBase64String(b64.Trim());
            if (bytes.Length == 0) return null;
            // El tipo declarado no siempre es fiel: se mira la cabecera real.
            if (bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50) contentType = "image/png";
            else if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8) contentType = "image/jpeg";
            return (bytes, contentType);
        }
        catch (FormatException) { return null; }
    }

    // ------------------------------------------------------------------
    // Utilidades
    // ------------------------------------------------------------------

    private async IAsyncEnumerable<JsonElement> PagedAsync(string path, int pageSize,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, Action<int>? onTotal = null)
    {
        int page = 1;
        while (true)
        {
            var data = await CallAsync(path, new { pageNo = page, pageSize }, ct);
            int total = Int(data, "total") ?? 0;
            onTotal?.Invoke(total);
            if (!data.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) yield break;
            int count = 0;
            foreach (var item in list.EnumerateArray()) { count++; yield return item; }
            if (count < pageSize || page * pageSize >= total) yield break;
            page++;
        }
    }

    private static HcpPerson ParsePerson(JsonElement p)
    {
        var cards = new List<string>();
        if (p.TryGetProperty("cards", out var cardsEl) && cardsEl.ValueKind == JsonValueKind.Array)
            foreach (var c in cardsEl.EnumerateArray())
            {
                string? no = c.ValueKind == JsonValueKind.String ? c.GetString() : Str(c, "cardNo");
                if (!string.IsNullOrWhiteSpace(no)) cards.Add(no.Trim());
            }

        var fingers = new List<HcpFingerprint>();
        if (p.TryGetProperty("fingerPrint", out var fpEl) && fpEl.ValueKind == JsonValueKind.Array)
            foreach (var f in fpEl.EnumerateArray())
                fingers.Add(new HcpFingerprint(Str(f, "fingerPrintIndexCode") ?? "", Str(f, "fingerPrintName") ?? "",
                    Str(f, "fingerPrintData") ?? "", Str(f, "relatedCardNo") ?? ""));

        string picUri = "";
        if (p.TryGetProperty("personPhoto", out var photo) && photo.ValueKind == JsonValueKind.Object)
            picUri = Str(photo, "picUri") ?? "";

        return new HcpPerson(
            Str(p, "personId") ?? "",
            Str(p, "personCode") ?? "",
            Str(p, "orgIndexCode") ?? "",
            Str(p, "personName") ?? "",
            Str(p, "personFamilyName") ?? "",
            Str(p, "personGivenName") ?? "",
            Int(p, "gender") ?? 0,
            Str(p, "phoneNo") ?? "",
            Str(p, "email") ?? "",
            Str(p, "remark") ?? "",
            Date(p, "beginTime"),
            Date(p, "endTime"),
            picUri,
            cards,
            fingers);
    }

    private static string? Str(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
            _ => null,
        };
    }

    private static int? Int(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
        return int.TryParse(v.ToString(), out n) ? n : null;
    }

    private static DateTimeOffset? Date(JsonElement obj, string name) =>
        DateTimeOffset.TryParse(Str(obj, name), null, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
}
