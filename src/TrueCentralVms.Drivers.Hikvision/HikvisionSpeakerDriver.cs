using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Xml.Linq;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Hikvision;

public sealed class HikvisionSpeakerDriverFactory : ISpeakerDriverFactory
{
    public string DriverKey => "hikvision-isapi";
    public string DisplayName => "Hikvision (parlante IP por ISAPI)";
    public int DefaultPort => 80;
    public bool DefaultHttps => false;
    public ISpeakerDriver Create() => new HikvisionSpeakerDriver();
}

/// <summary>
/// Parlantes IP Hikvision (familia DS-QAZ / DS-PA) por ISAPI. Verificado el
/// 2026-09-02 contra un DS-QAZ1325G1T (firmware V1.6.0). Tres familias de
/// llamadas, porque el equipo las expone así:
/// <list type="bullet">
/// <item><b>Audio en vivo</b>: <c>/ISAPI/System/TwoWayAudio/channels/{id}/open</c>,
/// luego un PUT largo a <c>audioData</c> con el flujo G.711 al ritmo real y
/// <c>close</c> al terminar. Sirve para la voz del operador y para los sonidos
/// del servidor (que así suenan sincronizados en varios parlantes).</item>
/// <item><b>Biblioteca del equipo</b> (JSON con <c>?format=json</c>, rutas
/// heredadas del control de acceso): carpetas, listado, subida multipart,
/// borrado, texto a voz y <c>CustomAudio/{id}/play|stop</c>.</item>
/// <item><b>Salida</b>: volumen en <c>/ISAPI/System/Audio/AudioOut/channels/1</c>
/// y estado de reproducción en <c>SearchAduioOutStatus</c> (sic, así lo escribe
/// el fabricante).</item>
/// </list>
/// La autenticación Digest se firma a mano (<see cref="DigestAuthenticator"/>):
/// el manejador de .NET resuelve el desafío REENVIANDO la petición, imposible
/// con el cuerpo del audio que se transmite en vivo. El desafío se cachea por
/// equipo y usuario para que las órdenes salgan autenticadas de entrada.
/// </summary>
public sealed class HikvisionSpeakerDriver : ISpeakerDriver
{
    private const string LibraryBase = "/ISAPI/AccessControl/EventCardLinkageCfg/CustomAudio";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TtsTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(3);

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        // Los equipos de terreno traen certificado autofirmado: la confianza es
        // por dirección y credenciales (mismo criterio que los otros drivers).
        SslOptions = { RemoteCertificateValidationCallback = static (_, _, _, _) => true },
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,   // el tope lo pone cada llamada (el audio en vivo no tiene)
    };

    private static readonly ConcurrentDictionary<string, DigestAuthenticator> Auths = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string BaseUrl(SpeakerConnectionInfo info) =>
        $"{(info.UseHttps ? "https" : "http")}://{info.Host}:{info.Port}";

    private static DigestAuthenticator AuthFor(SpeakerConnectionInfo info) =>
        Auths.GetOrAdd($"{info.Host}:{info.Port}|{info.Username}|{info.Password}",
            _ => new DigestAuthenticator(info.Username, info.Password));

    /// <summary>Olvida el desafío cacheado (credenciales cambiadas o equipo eliminado).</summary>
    public static void Forget(SpeakerConnectionInfo info) =>
        Auths.TryRemove($"{info.Host}:{info.Port}|{info.Username}|{info.Password}", out _);

    // ------------------------------------------------------------------
    // Transporte
    // ------------------------------------------------------------------

    /// <summary>
    /// Envía firmando con el desafío conocido; ante un 401 lee el desafío
    /// nuevo y reintenta UNA vez si el cuerpo se puede reenviar.
    /// </summary>
    private static async Task<HttpResponseMessage> SendAsync(SpeakerConnectionInfo info, HttpMethod method, string path,
        HttpContent? content, TimeSpan? timeout, CancellationToken ct)
    {
        var auth = AuthFor(info);
        var uri = new Uri(BaseUrl(info) + path);
        bool replayable = content is null or ByteArrayContent or StringContent or MultipartFormDataContent;
        for (int attempt = 0; ; attempt++)
        {
            var request = new HttpRequestMessage(method, uri) { Content = content };
            request.Headers.Accept.ParseAdd("application/json, application/xml, text/xml;q=0.9, */*;q=0.5");
            request.Headers.UserAgent.ParseAdd("CLRTrueCentralVMS/1.0");
            if (auth.Ready) request.Headers.Authorization = auth.Build(method.Method, uri);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout is { } limit) cts.CancelAfter(limit);
            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new DriverException($"El parlante en {info.Host}:{info.Port} no respondió a tiempo.");
            }
            catch (HttpRequestException ex)
            {
                throw new DriverException($"No se pudo conectar con el parlante en {info.Host}:{info.Port}: {ex.Message}", ex);
            }
            finally
            {
                // El cuerpo lo administra quien llama: desprenderlo evita que
                // Dispose lo cierre y deje inservible el reintento.
                request.Content = null;
                request.Dispose();
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && replayable && auth.ReadChallenge(response))
            {
                response.Dispose();
                continue;
            }
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.PermanentRedirect)
            {
                string? target = response.Headers.Location?.ToString();
                response.Dispose();
                throw new DriverException($"El parlante redirige a {target}: active HTTPS y use ese puerto en la configuración.");
            }
            return response;
        }
    }

    /// <summary>Solicitud corta: devuelve (estado, cuerpo). Lanza solo por conexión/credenciales.</summary>
    private static async Task<(HttpStatusCode Status, string Body)> CallAsync(SpeakerConnectionInfo info, HttpMethod method,
        string path, HttpContent? content, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var response = await SendAsync(info, method, path, content, timeout ?? RequestTimeout, ct);
        string body;
        try { body = await HikvisionIsapiClient.ReadTextAsync(response, ct); }
        catch (Exception) { body = ""; }
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new DriverException("El parlante rechazó las credenciales (usuario o contraseña incorrectos).");
        return (response.StatusCode, body);
    }

    /// <summary>Solicitud corta que DEBE ser 2xx; si no, lanza con el motivo que informa el equipo.</summary>
    private static async Task<string> RequireAsync(SpeakerConnectionInfo info, HttpMethod method, string path,
        HttpContent? content, string what, CancellationToken ct, TimeSpan? timeout = null)
    {
        var (status, body) = await CallAsync(info, method, path, content, ct, timeout);
        if ((int)status is >= 200 and < 300) return body;
        throw new DriverException($"{what}: {Describe(status, body)}.");
    }

    private static string Describe(HttpStatusCode status, string body)
    {
        string detail = "";
        try
        {
            if (body.TrimStart().StartsWith('{'))
            {
                using var json = JsonDocument.Parse(body);
                detail = Str(json.RootElement, "errorMsg") ?? Str(json.RootElement, "subStatusCode") ?? Str(json.RootElement, "statusString") ?? "";
            }
            else if (body.TrimStart().StartsWith('<'))
            {
                var doc = XDocument.Parse(body);
                detail = Value(doc.Root, "subStatusCode") ?? Value(doc.Root, "statusString") ?? "";
            }
        }
        catch (Exception) { /* el detalle es opcional */ }
        return detail.Length > 0 ? $"{(int)status} {detail}" : $"{(int)status} {status}";
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static StringContent Xml(string xml) => new(xml, Encoding.UTF8, "application/xml");

    private static ByteArrayContent Empty() => new([]);

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            }
            : null;

    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long l) ? l : 0;

    private static string? Value(XElement? parent, string name) =>
        parent?.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

    private static IReadOnlyList<string> Options(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var field)) return [];
        if (field.ValueKind == JsonValueKind.Object && field.TryGetProperty("@opt", out var opt) && opt.ValueKind == JsonValueKind.Array)
            return opt.EnumerateArray().Select(o => o.ToString()).Where(o => o.Length > 0).ToList();
        return [];
    }

    // ------------------------------------------------------------------
    // Identificación y capacidades
    // ------------------------------------------------------------------

    public async Task<SpeakerInfo> ProbeAsync(SpeakerConnectionInfo info, CancellationToken ct = default)
    {
        string xml = await RequireAsync(info, HttpMethod.Get, "/ISAPI/System/deviceInfo", null,
            "El parlante no entregó su identificación", ct);
        XDocument device;
        try { device = XDocument.Parse(xml); }
        catch (System.Xml.XmlException)
        {
            throw new DriverException("El equipo respondió algo que no es ISAPI: verifique la dirección y el puerto (¿es un parlante Hikvision?).");
        }
        string? model = Value(device.Root, "model");
        string? serial = Value(device.Root, "serialNumber");
        string? firmware = Value(device.Root, "firmwareVersion");
        string? build = Value(device.Root, "firmwareReleasedDate");
        if (firmware is not null && build is { Length: > 0 }) firmware = $"{firmware} {build}";
        string? deviceType = Value(device.Root, "deviceType");

        // Canal de audio en vivo y sus códecs.
        var liveCodecs = new List<string>();
        bool supportsLive = false;
        var (liveStatus, liveBody) = await CallAsync(info, HttpMethod.Get, "/ISAPI/System/TwoWayAudio/channels/capabilities", null, ct);
        if (liveStatus == HttpStatusCode.OK)
        {
            try
            {
                var caps = XDocument.Parse(liveBody);
                var channel = caps.Descendants().FirstOrDefault(e => e.Name.LocalName == "TwoWayAudioChannel");
                if (channel is not null)
                {
                    supportsLive = true;
                    var codec = channel.Elements().FirstOrDefault(e => e.Name.LocalName == "audioCompressionType");
                    string opt = codec?.Attribute("opt")?.Value ?? codec?.Value ?? "";
                    liveCodecs.AddRange(opt.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }
            catch (System.Xml.XmlException) { /* sin canal de audio en vivo */ }
        }

        // Biblioteca de audios y texto a voz (JSON).
        bool supportsLibrary = false;
        var formats = new List<string>();
        long maxUpload = 0;
        var (libStatus, libBody) = await CallAsync(info, HttpMethod.Get, LibraryBase + "/capabilities?format=json", null, ct);
        if (libStatus == HttpStatusCode.OK)
        {
            try
            {
                using var json = JsonDocument.Parse(libBody);
                if (json.RootElement.TryGetProperty("CustomAudioInfoCap", out var cap))
                {
                    supportsLibrary = true;
                    formats.AddRange(Options(cap, "audioFileFormat").Select(f => f.ToLowerInvariant()).Distinct());
                    if (cap.TryGetProperty("audioFileSize", out var size) && size.TryGetProperty("@max", out var max))
                        maxUpload = max.GetInt64();
                }
            }
            catch (JsonException) { /* sin biblioteca */ }
        }

        bool supportsTts = false;
        var languages = new List<string>();
        var (ttsStatus, ttsBody) = await CallAsync(info, HttpMethod.Get, LibraryBase + "/CreateTTSAudioFile/capabilities?format=json", null, ct);
        if (ttsStatus == HttpStatusCode.OK)
        {
            try
            {
                using var json = JsonDocument.Parse(ttsBody);
                if (json.RootElement.TryGetProperty("CreateTTSAudioFileCap", out var cap))
                {
                    supportsTts = true;
                    languages.AddRange(Options(cap, "TTSLanguageType"));
                }
            }
            catch (JsonException) { /* sin TTS */ }
        }

        int? volume = null;
        bool supportsVolume = false;
        try
        {
            volume = await GetVolumeAsync(info, ct);
            supportsVolume = volume is not null;
        }
        catch (DriverException) { /* el volumen es accesorio */ }

        return new SpeakerInfo(model, serial, firmware, deviceType,
            new SpeakerCapabilities(supportsLibrary, supportsTts, supportsLive, supportsVolume, liveCodecs, languages,
                formats, maxUpload), volume);
    }

    // ------------------------------------------------------------------
    // Biblioteca del equipo
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<SpeakerAudioItem>> GetLibraryAsync(SpeakerConnectionInfo info, CancellationToken ct = default)
    {
        string foldersBody = await RequireAsync(info, HttpMethod.Get, LibraryBase + "/GetCustomAudioFolder?format=json", null,
            "El parlante no entregó sus carpetas de audio", ct);
        var folders = new List<string>();
        try
        {
            using var json = JsonDocument.Parse(foldersBody);
            if (json.RootElement.TryGetProperty("folderList", out var list))
                foreach (var folder in list.EnumerateArray())
                    if (Str(folder, "folderID") is { Length: > 0 } id) folders.Add(id);
        }
        catch (JsonException) { /* se intenta la carpeta por defecto */ }
        if (folders.Count == 0) folders.Add("1");

        var items = new Dictionary<long, SpeakerAudioItem>();
        foreach (string folder in folders)
        {
            var (status, body) = await CallAsync(info, HttpMethod.Post, LibraryBase + "/SearchFolderCustomAudio?format=json",
                Json($"{{\"folderID\":\"{folder}\"}}"), ct);
            if (status != HttpStatusCode.OK) continue;
            foreach (var item in ParseAudioList(body))
                items.TryAdd(item.Id, item);
        }
        return items.Values.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static IEnumerable<SpeakerAudioItem> ParseAudioList(string body)
    {
        var result = new List<SpeakerAudioItem>();
        try
        {
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("customAudioInfoList", out var list) || list.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var item in list.EnumerateArray())
            {
                long id = Long(item, "customAudioID");
                if (id <= 0) continue;
                result.Add(new SpeakerAudioItem(id,
                    Str(item, "customAudioName") ?? $"audio {id}",
                    (Str(item, "audioFileFormat") ?? "").ToLowerInvariant(),
                    (int)Long(item, "audioFileDuration"),
                    Long(item, "audioFileSize"),
                    Str(item, "isBuiltIn") == "true"));
            }
        }
        catch (JsonException) { /* lista vacía */ }
        return result;
    }

    public async Task<SpeakerAudioItem> UploadAudioAsync(SpeakerConnectionInfo info, string name, string format, byte[] content,
        CancellationToken ct = default)
    {
        await EnsureAuthAsync(info, ct);
        string safeName = name.Trim();
        string ext = format.Trim().TrimStart('.').ToLowerInvariant();
        string meta = JsonSerializer.Serialize(new
        {
            CustomAudioInfo = new { customAudioName = safeName, audioFileFormat = ext, audioFileSize = content.Length },
        });
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(meta, Encoding.UTF8, "application/json"), "CustomAudioInfo");
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(ext switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "aac" => "audio/aac",
            _ => "application/octet-stream",
        });
        form.Add(file, "audioData", safeName.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase) ? safeName : $"{safeName}.{ext}");

        await RequireAsync(info, HttpMethod.Post, LibraryBase + "?format=json", form,
            "El parlante rechazó el archivo de audio", ct, UploadTimeout);

        return await FindByNameAsync(info, safeName, ct)
               ?? throw new DriverException("El parlante aceptó el archivo pero no aparece en su biblioteca (revise el formato).");
    }

    public async Task DeleteAudioAsync(SpeakerConnectionInfo info, long audioId, CancellationToken ct = default)
    {
        // Verificado con el DS-QAZ1325G1T: deleteCustomAudio y batchDelete
        // responden invalidOperation; lo que borra es DeleteFolderCustomAudio
        // sobre cada carpeta que contenga el audio. Sacarlo de una subcarpeta
        // (vinculación de alarma, TTS) no lo elimina del equipo: hay que
        // quitarlo de todas, la raíz ("1") incluida.
        string foldersBody = await RequireAsync(info, HttpMethod.Get, LibraryBase + "/GetCustomAudioFolder?format=json", null,
            "El parlante no entregó sus carpetas de audio", ct);
        var folders = new List<string>();
        try
        {
            using var json = JsonDocument.Parse(foldersBody);
            if (json.RootElement.TryGetProperty("folderList", out var list))
                foreach (var folder in list.EnumerateArray())
                    if (Str(folder, "folderID") is { Length: > 0 } id) folders.Add(id);
        }
        catch (JsonException) { /* se intenta la raíz */ }
        if (!folders.Contains("1")) folders.Insert(0, "1");

        bool found = false;
        string? lastError = null;
        // Subcarpetas primero: la raíz suele rechazar el borrado mientras el
        // audio siga vinculado a una de ellas.
        foreach (string folder in folders.OrderByDescending(f => f == "1" ? 0 : 1))
        {
            var (searchStatus, searchBody) = await CallAsync(info, HttpMethod.Post, LibraryBase + "/SearchFolderCustomAudio?format=json",
                Json($"{{\"folderID\":\"{folder}\"}}"), ct);
            if (searchStatus != HttpStatusCode.OK || ParseAudioList(searchBody).All(i => i.Id != audioId)) continue;
            found = true;
            string body = $"{{\"folderID\":\"{folder}\",\"customAudioInfoList\":[{{\"customAudioID\":{audioId}}}]}}";
            var (status, reply) = await CallAsync(info, HttpMethod.Put, LibraryBase + "/DeleteFolderCustomAudio?format=json", Json(body), ct);
            if ((int)status is < 200 or >= 300) lastError = Describe(status, reply);
        }
        if (!found) return;   // ya no está
        var remaining = await GetLibraryAsync(info, ct);
        if (remaining.Any(i => i.Id == audioId))
            throw new DriverException($"El parlante no borró el audio {audioId}{(lastError is null ? "" : $": {lastError}")}.");
    }

    public async Task<SpeakerAudioItem> RenameAudioAsync(SpeakerConnectionInfo info, long audioId, string newName, CancellationToken ct = default)
    {
        await EnsureAuthAsync(info, ct);
        string name = newName.Trim();
        if (name.Length is 0 or > 255) throw new DriverException("El nombre debe tener entre 1 y 255 caracteres.");
        // VERIFICADO con el DS-QAZ1325G1T: PUT CustomAudio/{id} con el cuerpo
        // envuelto en CustomAudioInfo (la forma plana responde badJsonFormat).
        string body = JsonSerializer.Serialize(new { CustomAudioInfo = new { customAudioName = name } });
        await RequireAsync(info, HttpMethod.Put, $"{LibraryBase}/{audioId}?format=json", Json(body),
            $"El parlante no renombró el audio {audioId}", ct);
        return (await GetLibraryAsync(info, ct)).FirstOrDefault(i => i.Id == audioId)
               ?? throw new DriverException("El parlante aceptó el nombre nuevo pero el audio ya no aparece en su biblioteca.");
    }

    public async Task<(byte[] Content, string ContentType, string FileName)> DownloadAudioAsync(SpeakerConnectionInfo info, long audioId,
        CancellationToken ct = default)
    {
        await EnsureAuthAsync(info, ct);
        var item = (await GetLibraryAsync(info, ct)).FirstOrDefault(i => i.Id == audioId)
                   ?? throw new DriverException($"El audio {audioId} no está en la biblioteca del parlante.");
        // Ruta que informa el propio listado (customAudioFile.filePath).
        using var response = await SendAsync(info, HttpMethod.Get, $"{LibraryBase}/download?CustomAudioId={audioId}&format=json", null,
            UploadTimeout, ct);
        if (!response.IsSuccessStatusCode)
        {
            string body;
            try { body = await HikvisionIsapiClient.ReadTextAsync(response, ct); } catch (Exception) { body = ""; }
            throw new DriverException($"El parlante no entregó el audio '{item.Name}': {Describe(response.StatusCode, body)}.");
        }
        byte[] content = await response.Content.ReadAsByteArrayAsync(ct);
        string contentType = response.Content.Headers.ContentType?.MediaType is { Length: > 0 } declared && declared != "application/json"
            ? declared
            : item.Format switch
            {
                "mp3" => "audio/mpeg",
                "wav" => "audio/wav",
                "aac" => "audio/aac",
                "mp2" => "audio/mpeg",
                _ => "application/octet-stream",
            };
        string fileName = item.Name.EndsWith("." + item.Format, StringComparison.OrdinalIgnoreCase) ? item.Name : $"{item.Name}.{item.Format}";
        return (content, contentType, fileName);
    }

    public async Task<SpeakerAudioItem> CreateTtsAsync(SpeakerConnectionInfo info, string name, string text, string language, string voice,
        CancellationToken ct = default)
    {
        await EnsureAuthAsync(info, ct);
        string safeName = name.Trim();
        var payload = new
        {
            customAudioName = safeName,
            TTSContent = text.Trim(),
            TTSLanguageType = language,
            voiceType = voice,
        };
        // El esquema documentado envuelve los campos; algunos firmwares los
        // aceptan planos. Se prueba primero la forma envuelta.
        string wrapped = JsonSerializer.Serialize(new { CreateTTSAudioFile = payload });
        var (status, body) = await CallAsync(info, HttpMethod.Post, LibraryBase + "/CreateTTSAudioFile?format=json", Json(wrapped), ct, TtsTimeout);
        if (status == HttpStatusCode.BadRequest)
            (status, body) = await CallAsync(info, HttpMethod.Post, LibraryBase + "/CreateTTSAudioFile?format=json",
                Json(JsonSerializer.Serialize(payload)), ct, TtsTimeout);
        if ((int)status is < 200 or >= 300)
            throw new DriverException($"El parlante no generó el audio de texto a voz: {Describe(status, body)}.");

        return await FindByNameAsync(info, safeName, ct)
               ?? throw new DriverException("El parlante generó el audio pero no aparece en su biblioteca.");
    }

    private async Task<SpeakerAudioItem?> FindByNameAsync(SpeakerConnectionInfo info, string name, CancellationToken ct)
    {
        var library = await GetLibraryAsync(info, ct);
        return library.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? library.FirstOrDefault(i => string.Equals(Path.GetFileNameWithoutExtension(i.Name), name, StringComparison.OrdinalIgnoreCase))
               ?? library.FirstOrDefault(i => string.Equals(i.Name, Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase));
    }

    public async Task PlayLibraryAsync(SpeakerConnectionInfo info, long audioId, CancellationToken ct = default)
    {
        await EnsureAuthAsync(info, ct);
        await RequireAsync(info, HttpMethod.Put, $"{LibraryBase}/{audioId}/play?format=json", Json("{}"),
            $"El parlante no reprodujo el audio {audioId}", ct);
    }

    public async Task StopAsync(SpeakerConnectionInfo info, CancellationToken ct = default)
    {
        await EnsureAuthAsync(info, ct);
        var state = await GetPlaybackStateAsync(info, ct);
        if (!state.IsPlaying || state.CurrentName is null) return;
        var item = await FindByNameAsync(info, state.CurrentName, ct);
        if (item is null) return;
        await RequireAsync(info, HttpMethod.Put, $"{LibraryBase}/{item.Id}/stop?format=json", Json("{}"),
            "El parlante no detuvo la reproducción", ct);
    }

    public async Task<SpeakerPlaybackState> GetPlaybackStateAsync(SpeakerConnectionInfo info, CancellationToken ct = default)
    {
        var (status, body) = await CallAsync(info, HttpMethod.Post, "/ISAPI/System/Audio/AudioOut/SearchAduioOutStatus?format=json",
            Json("{\"audioOutID\":[1]}"), ct);
        if (status != HttpStatusCode.OK) return new SpeakerPlaybackState(false, null);
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Array) return new SpeakerPlaybackState(false, null);
            foreach (var output in json.RootElement.EnumerateArray())
            {
                if (!output.TryGetProperty("audioOutStatusList", out var list) || list.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in list.EnumerateArray())
                {
                    // broadcastType 4 = archivo de la biblioteca; el resto son
                    // fuentes permanentes (línea, mezcla) que siempre figuran "playing".
                    if (Long(entry, "broadcastType") != 4) continue;
                    if (Str(entry, "audioOutStatus") != "playing") continue;
                    string? path = Str(entry, "customAudioName");
                    string? name = path is null ? null : path[(path.LastIndexOf('/') + 1)..];
                    return new SpeakerPlaybackState(true, name);
                }
            }
        }
        catch (JsonException) { /* sin estado */ }
        return new SpeakerPlaybackState(false, null);
    }

    // ------------------------------------------------------------------
    // Volumen de salida
    // ------------------------------------------------------------------

    public async Task<int?> GetVolumeAsync(SpeakerConnectionInfo info, CancellationToken ct = default)
    {
        var (status, body) = await CallAsync(info, HttpMethod.Get, "/ISAPI/System/Audio/AudioOut/channels/1", null, ct);
        if (status != HttpStatusCode.OK) return null;
        try
        {
            var doc = XDocument.Parse(body);
            return int.TryParse(Value(doc.Root, "volume"), out int volume) ? volume : null;
        }
        catch (System.Xml.XmlException) { return null; }
    }

    public async Task SetVolumeAsync(SpeakerConnectionInfo info, int volume, CancellationToken ct = default)
    {
        volume = Math.Clamp(volume, 0, 100);
        string current = await RequireAsync(info, HttpMethod.Get, "/ISAPI/System/Audio/AudioOut/channels/1", null,
            "El parlante no entregó su configuración de salida", ct);
        XDocument doc;
        try { doc = XDocument.Parse(current); }
        catch (System.Xml.XmlException) { throw new DriverException("El parlante entregó una configuración de salida ilegible."); }
        var node = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "volume")
                   ?? throw new DriverException("El parlante no expone el volumen de salida.");
        node.Value = volume.ToString();
        await RequireAsync(info, HttpMethod.Put, "/ISAPI/System/Audio/AudioOut/channels/1",
            Xml(doc.Declaration is null ? doc.ToString(SaveOptions.DisableFormatting) : doc.Declaration + doc.ToString(SaveOptions.DisableFormatting)),
            "El parlante rechazó el cambio de volumen", ct);
    }

    // ------------------------------------------------------------------
    // Audio en vivo
    // ------------------------------------------------------------------

    /// <summary>Con el desafío Digest ya conocido las órdenes (y el flujo de audio) salen firmadas de entrada.</summary>
    private static async Task EnsureAuthAsync(SpeakerConnectionInfo info, CancellationToken ct)
    {
        if (AuthFor(info).Ready) return;
        await CallAsync(info, HttpMethod.Get, "/ISAPI/System/deviceInfo", null, ct);
    }

    public async Task<ISpeakerAudioSession> OpenLiveAudioAsync(SpeakerConnectionInfo info, CancellationToken ct = default)
    {
        await EnsureAuthAsync(info, ct);
        string list = await RequireAsync(info, HttpMethod.Get, "/ISAPI/System/TwoWayAudio/channels", null,
            "El parlante no entregó sus canales de audio", ct);
        int channel = 1;
        string codecName = "G.711ulaw";
        try
        {
            var doc = XDocument.Parse(list);
            var node = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "TwoWayAudioChannel");
            if (node is not null)
            {
                if (int.TryParse(node.Elements().FirstOrDefault(e => e.Name.LocalName == "id")?.Value, out int id)) channel = id;
                codecName = node.Elements().FirstOrDefault(e => e.Name.LocalName == "audioCompressionType")?.Value.Trim() ?? codecName;
            }
        }
        catch (System.Xml.XmlException) { /* se usa el canal 1 */ }

        string codec = codecName.Contains("alaw", StringComparison.OrdinalIgnoreCase) ? "alaw"
            : codecName.Contains("ulaw", StringComparison.OrdinalIgnoreCase) ? "ulaw"
            : codecName.Equals("PCM", StringComparison.OrdinalIgnoreCase) ? "pcm16"
            : throw new DriverException($"El parlante pide audio {codecName} en el canal en vivo; el servidor solo genera G.711 o PCM. " +
                                        "Cambie el códec del canal de audio bidireccional en el equipo.");

        await RequireAsync(info, HttpMethod.Put, $"/ISAPI/System/TwoWayAudio/channels/{channel}/open", Empty(),
            $"El parlante no abrió el canal de audio {channel}", ct);
        return new LiveSession(info, channel, codec);
    }

    /// <summary>
    /// PUT largo a audioData: el cuerpo es un canal en memoria que se va
    /// llenando con lo que escribe el operador o el reproductor del
    /// servidor; cerrar la sesión completa el cuerpo y manda el close.
    /// </summary>
    private sealed class LiveSession : ISpeakerAudioSession
    {
        private readonly SpeakerConnectionInfo _info;
        private readonly int _channel;
        private readonly Channel<ReadOnlyMemory<byte>> _frames = System.Threading.Channels.Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        private readonly Task<HttpResponseMessage> _upload;
        private readonly CancellationTokenSource _cts = new();
        private int _disposed;

        public LiveSession(SpeakerConnectionInfo info, int channel, string codec)
        {
            _info = info;
            _channel = channel;
            Codec = codec;
            _upload = SendAsync(info, HttpMethod.Put, $"/ISAPI/System/TwoWayAudio/channels/{channel}/audioData",
                new StreamingContent(_frames.Reader), null, _cts.Token);
        }

        public string Codec { get; }

        public bool IsAlive => !_upload.IsCompleted && _disposed == 0;

        public async Task WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
        {
            if (!IsAlive)
            {
                string reason = "el parlante cerró el canal de audio";
                if (_upload.IsFaulted && _upload.Exception?.GetBaseException() is { } ex) reason = ex.Message;
                throw new DriverException($"No se pudo enviar audio al parlante {_info.Host}: {reason}.");
            }
            await _frames.Writer.WriteAsync(frame, ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _frames.Writer.TryComplete();
            // El equipo no contesta el PUT de audioData hasta que se cierra la
            // sesión: primero el close (con el cuerpo ya completo) y recién
            // entonces se espera la respuesta del flujo, un instante.
            try
            {
                using var response = await SendAsync(_info, HttpMethod.Put, $"/ISAPI/System/TwoWayAudio/channels/{_channel}/close",
                    Empty(), TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (Exception) { /* el equipo cierra solo la sesión huérfana */ }
            // El largo declarado nunca se completa: abortar la conexión es el
            // final normal del flujo (el equipo ya recibió el close).
            try { _cts.Cancel(); } catch (ObjectDisposedException) { /* ya liberado */ }
            try { (await _upload).Dispose(); }
            catch (Exception) { /* cancelado a propósito */ }
            _cts.Dispose();
        }
    }

    /// <summary>Cuerpo HTTP sin largo (chunked) alimentado por un canal en memoria.</summary>
    private sealed class StreamingContent : HttpContent
    {
        private readonly ChannelReader<ReadOnlyMemory<byte>> _frames;

        public StreamingContent(ChannelReader<ReadOnlyMemory<byte>> frames)
        {
            _frames = frames;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken ct)
        {
            await foreach (var frame in _frames.ReadAllAsync(ct))
            {
                await stream.WriteAsync(frame, ct);
                await stream.FlushAsync(ct);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        /// <summary>
        /// VERIFICADO con el DS-QAZ1325G1T: el equipo acepta un cuerpo "chunked"
        /// (responde 200 al cerrar) pero NO lo reproduce; con Content-Length sí
        /// suena. Para un flujo en vivo se declara un largo enorme y la sesión
        /// se corta abortando la conexión cuando el operador termina.
        /// </summary>
        protected override bool TryComputeLength(out long length)
        {
            length = LiveContentLength;
            return true;
        }
    }

    /// <summary>~69 h de G.711 a 8 kB/s: más que el tope de cualquier sesión de voz o sonido.</summary>
    private const long LiveContentLength = 2_000_000_000L;
}
