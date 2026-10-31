using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Hanwha;

/// <summary>
/// Cliente HTTP de SUNAPI (la API abierta de Hanwha Vision / Wisenet,
/// /stw-cgi). Según el firmware responde en texto "Clave=Valor" o en JSON; las
/// dos formas se aplanan al mismo diccionario de claves con puntos
/// ("Channel.0.Profile.2.Resolution") para que el driver no dependa del
/// formato. Un rechazo de SUNAPI llega con HTTP 200 y "NG" / "Response":"Fail"
/// en el cuerpo: también sale como <see cref="DriverException"/>.
/// </summary>
internal sealed class SunapiClient : IDisposable
{
    private readonly HttpClient _http;

    public SunapiClient(DeviceConnectionInfo info, TimeSpan? timeout = null)
    {
        Host = info.Host;
        Port = info.Port;
        var baseUri = new Uri($"http://{info.Host}:{info.Port}/");
        var credentials = new NetworkCredential(info.Username, info.Password);
        var cache = new CredentialCache
        {
            { baseUri, "Digest", credentials },
            { baseUri, "Basic", credentials },
        };
        _http = new HttpClient(new HttpClientHandler { Credentials = cache, PreAuthenticate = true })
        {
            BaseAddress = baseUri,
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };
    }

    public string Host { get; }
    public int Port { get; }

    /// <summary>GET a un CGI de SUNAPI, aplanado. Lanza <see cref="SunapiErrorException"/> si el equipo responde NG.</summary>
    public async Task<Dictionary<string, string>> GetAsync(string cgi, string query, CancellationToken ct)
    {
        string body = await GetStringAsync($"stw-cgi/{cgi}?{query}", ct);
        return Parse(body);
    }

    public async Task<byte[]> GetBytesAsync(string relative, CancellationToken ct)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, relative), HttpCompletionOption.ResponseContentRead, ct);
        byte[] data = await response.Content.ReadAsByteArrayAsync(ct);
        // Un rechazo llega como texto aunque se haya pedido una imagen.
        if (data.Length < 512 && response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
            Parse(System.Text.Encoding.UTF8.GetString(data));
        return data;
    }

    public async Task<string> GetStringAsync(string relative, CancellationToken ct)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, relative), HttpCompletionOption.ResponseContentRead, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Respuesta en streaming (eventos): quien llama la libera.</summary>
    public Task<HttpResponseMessage> OpenStreamAsync(string relative, CancellationToken ct) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, relative), HttpCompletionOption.ResponseHeadersRead, ct);

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, completion, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DriverException($"El equipo Hanwha {Host}:{Port} no respondió a tiempo.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException)
        {
            throw new DriverException($"No se pudo conectar con el equipo Hanwha {Host}:{Port}. Revise la dirección y el puerto HTTP.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DriverException($"Error de comunicación con el equipo Hanwha {Host}:{Port}: {ex.Message}", ex);
        }

        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        string detail = "";
        try { detail = (await response.Content.ReadAsStringAsync(ct)).Trim(); } catch (Exception) { }
        response.Dispose();
        if (status == HttpStatusCode.Unauthorized)
            throw new DriverException(
                "El equipo Hanwha rechazó el usuario o la contraseña. Ojo: tras varios intentos fallidos el equipo bloquea el acceso unos minutos.");
        // Algunos firmware contestan el NG de SUNAPI con un HTTP de error.
        if (detail.Length > 0 && TryReadError(detail) is { } error) throw error;
        throw new DriverException($"El equipo Hanwha respondió HTTP {(int)status} a {request.RequestUri}.");
    }

    /// <summary>Aplana una respuesta de SUNAPI (texto "Clave=Valor" o JSON).</summary>
    internal static Dictionary<string, string> Parse(string body)
    {
        if (TryReadError(body) is { } error) throw error;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string trimmed = body.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                Flatten(doc.RootElement, "", result);
                return result;
            }
            catch (JsonException)
            {
                // no era JSON completo: se intenta como texto
            }
        }
        foreach (string raw in body.Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            result[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return result;
    }

    /// <summary>
    /// JSON → claves con puntos, como las del formato de texto: un elemento de
    /// arreglo que trae "Channel" (o "Profile", "Preset"...) se nombra por ese
    /// número, así "VideoProfiles":[{"Channel":0,"Profiles":[{"Profile":2,...}]}]
    /// queda "Channel.0.Profile.2.X", igual que en texto.
    /// </summary>
    internal static void Flatten(JsonElement element, string prefix, Dictionary<string, string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    // El número que identifica al elemento ya va en el prefijo.
                    if (prefix.Length > 0 && IsIdentity(prop.Name) && prefix.EndsWith("." + prop.Value.ToString(), StringComparison.Ordinal))
                        continue;
                    Flatten(prop.Value, Join(prefix, prop.Name), into);
                }
                break;
            case JsonValueKind.Array:
                int index = 0;
                // El nombre del arreglo ("VideoProfiles", "ChannelEvent") se reemplaza por el del identificador.
                string parent = prefix.Contains('.') ? prefix[..prefix.LastIndexOf('.')] : "";
                foreach (var item in element.EnumerateArray())
                {
                    string itemPrefix = Join(prefix, index.ToString(CultureInfo.InvariantCulture));
                    if (item.ValueKind == JsonValueKind.Object)
                        foreach (var prop in item.EnumerateObject())
                            if (IsIdentity(prop.Name) && prop.Value.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                            {
                                itemPrefix = Join(parent, $"{prop.Name}.{prop.Value}");
                                break;
                            }
                    Flatten(item, itemPrefix, into);
                    index++;
                }
                break;
            case JsonValueKind.True:
                into[prefix] = "True";
                break;
            case JsonValueKind.False:
                into[prefix] = "False";
                break;
            case JsonValueKind.Null:
                break;
            default:
                into[prefix] = element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ToString();
                break;
        }
    }

    private static bool IsIdentity(string name) =>
        name is "Channel" or "Profile" or "Preset" or "AlarmInput" or "AlarmOutput" or "Line" or "Area" or "Region";

    private static string Join(string prefix, string name) => prefix.Length == 0 ? name : $"{prefix}.{name}";

    /// <summary>"NG / Error Code: 600 / Error Details: ..." (texto) o {"Response":"Fail","Error":{...}} (JSON).</summary>
    private static SunapiErrorException? TryReadError(string body)
    {
        string trimmed = body.TrimStart();
        if (trimmed.StartsWith("NG", StringComparison.Ordinal))
        {
            string code = "", details = "";
            foreach (string raw in trimmed.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("Error Code", StringComparison.OrdinalIgnoreCase)) code = line[(line.IndexOf(':') + 1)..].Trim();
                if (line.StartsWith("Error Details", StringComparison.OrdinalIgnoreCase)) details = line[(line.IndexOf(':') + 1)..].Trim();
            }
            return new SunapiErrorException(code, details);
        }
        if (trimmed.StartsWith('{') && trimmed.Contains("\"Fail\"", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.TryGetProperty("Response", out var r) && r.GetString() == "Fail")
                {
                    string code = "", details = "";
                    if (doc.RootElement.TryGetProperty("Error", out var e))
                    {
                        if (e.TryGetProperty("Code", out var c)) code = c.ToString();
                        if (e.TryGetProperty("Details", out var d)) details = d.ToString();
                    }
                    return new SunapiErrorException(code, details);
                }
            }
            catch (JsonException) { }
        }
        return null;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>El equipo rechazó el comando SUNAPI (código 600 = parámetro inválido, 608 = no soportado...).</summary>
internal sealed class SunapiErrorException(string code, string details)
    : DriverException($"El equipo Hanwha rechazó el comando (código {code}{(details.Length > 0 ? ": " + details : "")}).")
{
    public string Code { get; } = code;
}
