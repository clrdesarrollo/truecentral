using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services.Workflows.Actions;

/// <summary>
/// Reproduce un sonido en un parlante IP (o en una cámara con altavoz). Cuatro
/// formas, porque cada instalación lo resuelve distinto:
///
/// · <b>inventory</b> (recomendada): parlantes del módulo Parlantes IP,
///   elegidos por id o por grupo. El audio puede ser un sonido del servidor
///   (se transmite sincronizado a todos), un audio de la biblioteca de cada
///   equipo (por nombre) o texto a voz generado por el propio parlante; el
///   texto admite las marcas {zona}, {panel}, etc. Lo ejecuta
///   <see cref="SpeakerService"/>, el mismo camino que usa el operador.
/// · <b>hikvision</b>: un equipo suelto por dirección, canal de audio
///   bidireccional por ISAPI. El servidor abre el canal
///   (<c>TwoWayAudio/channels/{id}/open</c>), envía el audio ya convertido a
///   G.711 al ritmo real (8 kHz) y cierra.
/// · <b>axis</b>: <c>playclip.cgi</c> de VAPIX, que reproduce un clip ya
///   cargado en el parlante.
/// · <b>http</b>: cualquier otra marca que ofrezca una URL para gatillar un
///   mensaje pregrabado.
///
/// Configuración: <c>{ "mode":"inventory|hikvision|axis|http", "speakerIds":[1,2],
/// "group":"", "source":"server|library|tts", "audio":"sirena", "libraryName":"",
/// "text":"", "language":"spanish", "voice":"female", "host":"", "port":80,
/// "useHttps":false, "username":"", "channel":1, "repeat":1, "clip":1,
/// "volume":50, "url":"", "method":"GET" }</c>
/// </summary>
public sealed class SpeakerAction(WorkflowStore store, SpeakerService speakers, IServiceScopeFactory scopeFactory,
    ILogger<SpeakerAction> logger) : IWorkflowActionExecutor
{
    /// <summary>G.711 a 8 kHz: 8000 bytes por segundo, 640 bytes = 80 ms.</summary>
    private const int ChunkSize = 640;
    private const int ChunkMilliseconds = 80;
    /// <summary>Tope de audio por reproducción (el parlante no es una radio).</summary>
    private const int MaxSeconds = 60;

    public string Type => WorkflowActionTypes.Speaker;
    public string Label => "Sonar parlante IP";
    public string Description => "Reproduce un sonido, un audio del propio parlante o un texto leído en voz alta en uno o más parlantes IP.";
    public bool UsesSecret => true;

    private static string Mode(JsonElement config) =>
        (config.TryGetProperty("mode", out var m) ? m.GetString() : null)?.Trim().ToLowerInvariant() is { Length: > 0 } mode ? mode : "inventory";

    /// <summary>Orden para los parlantes del inventario: "play" (por defecto) o "stop".</summary>
    private static string Command(JsonElement config) =>
        (config.TryGetProperty("command", out var c) ? c.GetString() : null)?.Trim().ToLowerInvariant() == "stop" ? "stop" : "play";

    public string? Validate(JsonElement config)
    {
        string mode = Mode(config);
        string? host = config.TryGetProperty("host", out var h) ? h.GetString() : null;
        string? url = config.TryGetProperty("url", out var u) ? u.GetString() : null;

        if (mode == "inventory")
        {
            bool anySpeaker = config.TryGetProperty("speakerIds", out var ids) && ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() > 0;
            bool anyGroup = config.TryGetProperty("group", out var g) && !string.IsNullOrWhiteSpace(g.GetString());
            if (!anySpeaker && !anyGroup) return "Elija al menos un parlante o un grupo.";
            if (Command(config) == "stop") return null;
            string source = (config.TryGetProperty("source", out var s) ? s.GetString() : null)?.ToLowerInvariant() ?? SpeakerPlaySources.Server;
            bool loop = config.TryGetProperty("repeat", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out int rep) && rep == 0;
            if (loop && source != SpeakerPlaySources.Server)
                return "El bucle hasta detener (0 repeticiones) solo está disponible con un sonido del servidor.";
            return source switch
            {
                SpeakerPlaySources.Server when string.IsNullOrWhiteSpace(config.TryGetProperty("audio", out var a) ? a.GetString() : null)
                    => "Elija el sonido del servidor que debe reproducir el parlante.",
                SpeakerPlaySources.Library when string.IsNullOrWhiteSpace(config.TryGetProperty("libraryName", out var l) ? l.GetString() : null)
                    => "Indique el nombre del audio en la biblioteca del parlante.",
                SpeakerPlaySources.Tts when string.IsNullOrWhiteSpace(config.TryGetProperty("text", out var t) ? t.GetString() : null)
                    => "Escriba el texto que debe leer el parlante.",
                SpeakerPlaySources.Server or SpeakerPlaySources.Library or SpeakerPlaySources.Tts => null,
                _ => $"Origen de audio desconocido: '{source}'.",
            };
        }
        if (mode == "http")
            return string.IsNullOrWhiteSpace(url) ? "Indique la URL que hace sonar el parlante." : null;
        if (string.IsNullOrWhiteSpace(host))
            return "Indique la dirección del parlante.";
        if (mode == "hikvision")
        {
            string? audio = config.TryGetProperty("audio", out var a) ? a.GetString() : null;
            if (string.IsNullOrWhiteSpace(audio)) return "Elija el sonido que debe reproducir el parlante.";
        }
        return null;
    }

    public async Task<WorkflowStepResult> ExecuteAsync(WorkflowActionContext context, CancellationToken ct)
    {
        string mode = Mode(context.Config);
        if (mode == "inventory") return await PlayInventoryAsync(context, ct);

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            // Los equipos de terreno traen certificado autofirmado: la
            // confianza es por dirección y credenciales (mismo criterio que
            // los drivers de dispositivos).
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(MaxSeconds + 20) };
        var auth = new DigestAuthenticator(context.Text("username").Trim(), context.Secret ?? "");

        return mode switch
        {
            "axis" => await PlayAxisAsync(context, client, auth, ct),
            "http" => await PlayHttpAsync(context, client, auth, ct),
            _ => await PlayHikvisionAsync(context, client, auth, ct),
        };
    }

    // ------------------------------------------------------------------
    // Parlantes del inventario (módulo Parlantes IP)
    // ------------------------------------------------------------------

    private async Task<WorkflowStepResult> PlayInventoryAsync(WorkflowActionContext context, CancellationToken ct)
    {
        var ids = new List<int>();
        if (context.Config.TryGetProperty("speakerIds", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var element in array.EnumerateArray())
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int id)) ids.Add(id);
                else if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out int parsed)) ids.Add(parsed);

        string group = context.Text("group").Trim();
        if (group.Length > 0)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            ids.AddRange(await db.Speakers.AsNoTracking()
                .Where(s => s.Enabled && s.GroupName != null && s.GroupName.ToLower() == group.ToLower())
                .Select(s => s.Id).ToListAsync(ct));
        }
        ids = ids.Distinct().ToList();
        if (ids.Count == 0)
            return WorkflowStepResult.Fail(group.Length > 0 ? $"El grupo '{group}' no tiene parlantes activos." : "La acción no tiene parlantes elegidos.");

        // "Detener": corta lo que esté sonando (sonido del servidor en curso,
        // audio de la biblioteca o voz). Es la pareja de un sonido en bucle:
        // "sensor interrumpido" → sonar en bucle, "sensor restablecido" → detener.
        if (Command(context.Config) == "stop")
        {
            var stopped = await speakers.StopAsync(ids, ct);
            string stopDetail = string.Join(" · ", stopped.Results.Select(r => $"{r.SpeakerName}: {r.Message}"));
            if (stopDetail.Length == 0) stopDetail = stopped.Message;
            return stopped.Success ? WorkflowStepResult.Ok(stopDetail) : WorkflowStepResult.Fail(stopDetail);
        }

        string source = context.Text("source", SpeakerPlaySources.Server).Trim().ToLowerInvariant();
        // 0 = en bucle hasta que una acción "Detener" o el operador lo corte.
        int repeat = Math.Clamp(context.Number("repeat", 1), 0, 5);
        if (repeat == 0 && source != SpeakerPlaySources.Server) repeat = 1;
        int? volume = context.Flag("setVolume") ? Math.Clamp(context.Number("volume", 80), 0, 100) : null;
        var request = new SpeakerPlayRequestDto(ids, source,
            Sound: context.Text("audio"),
            LibraryName: context.Text("libraryName"),
            Text: context.Render(context.Text("text")),
            Language: context.Text("language", "spanish"),
            Voice: context.Text("voice", "female"),
            Repeat: repeat,
            Volume: volume);

        var result = await speakers.PlayAsync(request, $"automatización '{context.Workflow.Name}'", ct);
        string detail = string.Join(" · ", result.Results.Select(r => $"{r.SpeakerName}: {r.Message}"));
        if (detail.Length == 0) detail = result.Message;
        return result.Success ? WorkflowStepResult.Ok(detail) : WorkflowStepResult.Fail(detail);
    }

    // ------------------------------------------------------------------
    // Hikvision: audio bidireccional por ISAPI
    // ------------------------------------------------------------------

    private async Task<WorkflowStepResult> PlayHikvisionAsync(WorkflowActionContext context, HttpClient client,
        DigestAuthenticator auth, CancellationToken ct)
    {
        if (BaseUri(context) is not { } baseUri) return WorkflowStepResult.Fail("La acción no tiene dirección de parlante.");
        string audio = context.Text("audio");
        if (audio.Length == 0) return WorkflowStepResult.Fail("La acción no tiene sonido configurado.");

        // 1) Capacidades del canal de audio: id y códec que habla el equipo.
        var (listResponse, listBody) = await SendAsync(client, HttpMethod.Get,
            new Uri(baseUri, "/ISAPI/System/TwoWayAudio/channels"), null, auth, ct);
        if (!listResponse.IsSuccessStatusCode)
            return WorkflowStepResult.Fail(Describe(listResponse, $"El parlante {baseUri.Host} no entregó sus canales de audio"));

        int channel = context.Number("channel", 0);
        string codec = "G.711ulaw";
        try
        {
            var document = XDocument.Parse(listBody);
            var node = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "TwoWayAudioChannel");
            if (node is not null)
            {
                if (channel <= 0 && int.TryParse(Value(node, "id"), out int id)) channel = id;
                codec = Value(node, "audioCompressionType") ?? codec;
            }
        }
        catch (System.Xml.XmlException)
        {
            logger.LogDebug("El parlante {Host} respondió los canales de audio en un formato inesperado.", baseUri.Host);
        }
        if (channel <= 0) channel = 1;

        string payloadCodec = codec.Contains("alaw", StringComparison.OrdinalIgnoreCase) ? "alaw" : "ulaw";
        if (!codec.Contains("711", StringComparison.OrdinalIgnoreCase))
            return WorkflowStepResult.Fail($"El parlante pide audio {codec}, que el servidor no sabe generar (solo G.711).");

        if (store.FindPayload(audio, payloadCodec) is not { } payloadPath)
            return WorkflowStepResult.Fail($"El sonido '{audio}' no está convertido a G.711 {payloadCodec}: vuelva a subirlo en Automatizaciones → Sonidos.");

        byte[] payload = await File.ReadAllBytesAsync(payloadPath, ct);
        if (payload.Length > MaxSeconds * 8000) payload = payload[..(MaxSeconds * 8000)];
        int repeat = Math.Clamp(context.Number("repeat", 1), 1, 5);

        // 2) Abrir el canal, 3) enviar el audio, 4) cerrarlo siempre.
        var openUri = new Uri(baseUri, $"/ISAPI/System/TwoWayAudio/channels/{channel}/open");
        var (openResponse, openBody) = await SendAsync(client, HttpMethod.Put, openUri, new ByteArrayContent([]), auth, ct);
        if (!openResponse.IsSuccessStatusCode)
            return WorkflowStepResult.Fail(Describe(openResponse, $"El parlante no abrió el canal de audio {channel}") +
                                           (openBody.Length > 0 ? $" · {Preview(openBody)}" : ""));
        try
        {
            var dataUri = new Uri(baseUri, $"/ISAPI/System/TwoWayAudio/channels/{channel}/audioData");
            var (dataResponse, dataBody) = await SendAsync(client, HttpMethod.Put, dataUri,
                new PacedAudioContent(payload, repeat), auth, ct);
            if (!dataResponse.IsSuccessStatusCode)
                return WorkflowStepResult.Fail(Describe(dataResponse, "El parlante rechazó el audio") +
                                               (dataBody.Length > 0 ? $" · {Preview(dataBody)}" : ""));
        }
        finally
        {
            var closeUri = new Uri(baseUri, $"/ISAPI/System/TwoWayAudio/channels/{channel}/close");
            try { await SendAsync(client, HttpMethod.Put, closeUri, new ByteArrayContent([]), auth, CancellationToken.None); }
            catch (Exception ex) { logger.LogDebug(ex, "No se pudo cerrar el canal de audio del parlante."); }
        }

        double seconds = Math.Round(payload.Length * repeat / 8000.0, 1);
        return WorkflowStepResult.Ok(
            $"Sonido '{audio}' reproducido en {baseUri.Host} (canal {channel}, {codec}, {seconds} s" +
            (repeat > 1 ? $", {repeat} repeticiones" : "") + ").");
    }

    // ------------------------------------------------------------------
    // Axis (VAPIX) y URL genérica
    // ------------------------------------------------------------------

    private async Task<WorkflowStepResult> PlayAxisAsync(WorkflowActionContext context, HttpClient client,
        DigestAuthenticator auth, CancellationToken ct)
    {
        if (BaseUri(context) is not { } baseUri) return WorkflowStepResult.Fail("La acción no tiene dirección de parlante.");
        int clip = Math.Max(context.Number("clip", 1), 0);
        int repeat = Math.Clamp(context.Number("repeat", 1), 1, 10) - 1;   // VAPIX cuenta las REPETICIONES extra
        int volume = Math.Clamp(context.Number("volume", 50), 0, 100);

        var uri = new Uri(baseUri, $"/axis-cgi/playclip.cgi?clip={clip}&repeat={repeat}&volume={volume}");
        var (response, body) = await SendAsync(client, HttpMethod.Get, uri, null, auth, ct);
        return response.IsSuccessStatusCode
            ? WorkflowStepResult.Ok($"Clip {clip} reproducido en {baseUri.Host} (volumen {volume}%).")
            : WorkflowStepResult.Fail(Describe(response, $"El parlante {baseUri.Host} rechazó la reproducción") +
                                      (body.Length > 0 ? $" · {Preview(body)}" : ""));
    }

    private async Task<WorkflowStepResult> PlayHttpAsync(WorkflowActionContext context, HttpClient client,
        DigestAuthenticator auth, CancellationToken ct)
    {
        string url = context.RenderUrl(context.Text("url"));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return WorkflowStepResult.Fail($"La URL del parlante no es válida: '{url}'.");

        var method = new HttpMethod(context.Text("method", "GET").Trim().ToUpperInvariant() is { Length: > 0 } m ? m : "GET");
        HttpContent? content = null;
        string body = context.Render(context.Text("body"));
        if (body.Length > 0 && method != HttpMethod.Get)
            content = new StringContent(body, System.Text.Encoding.UTF8, context.Text("contentType", "application/json"));

        var (response, reply) = await SendAsync(client, method, uri, content, auth, ct);
        return response.IsSuccessStatusCode
            ? WorkflowStepResult.Ok($"Parlante gatillado: {method} {uri} → {(int)response.StatusCode}.")
            : WorkflowStepResult.Fail(Describe(response, $"El parlante {uri.Host} rechazó la orden") +
                                      (reply.Length > 0 ? $" · {Preview(reply)}" : ""));
    }

    // ------------------------------------------------------------------
    // Utilitarios
    // ------------------------------------------------------------------

    private static Uri? BaseUri(WorkflowActionContext context)
    {
        string host = context.Text("host").Trim();
        if (host.Length == 0) return null;
        int port = context.Number("port", context.Flag("useHttps") ? 443 : 80);
        return new Uri($"{(context.Flag("useHttps") ? "https" : "http")}://{host}:{port}");
    }

    /// <summary>
    /// Envía firmando con el desafío ya conocido; si el equipo contesta 401 y
    /// el cuerpo se puede reenviar, lee el desafío nuevo y reintenta una vez.
    /// </summary>
    private static async Task<(HttpResponseMessage Response, string Body)> SendAsync(HttpClient client,
        HttpMethod method, Uri uri, HttpContent? content, DigestAuthenticator auth, CancellationToken ct)
    {
        bool replayable = content is null or ByteArrayContent or StringContent;
        for (int attempt = 0; ; attempt++)
        {
            var request = new HttpRequestMessage(method, uri) { Content = content };
            request.Headers.Authorization = auth.Build(method.Method, uri);
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct); }
            finally
            {
                // El cuerpo lo administra quien llama: desprenderlo evita que
                // Dispose lo cierre y deje inservible el reintento.
                request.Content = null;
                request.Dispose();
            }
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0 || !replayable)
                return (response, await SafeBodyAsync(response, ct));

            if (!auth.ReadChallenge(response))
                return (response, await SafeBodyAsync(response, ct));
            response.Dispose();
        }
    }

    private static async Task<string> SafeBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct); }
        catch (Exception) { return ""; }
    }

    private static string Describe(HttpResponseMessage response, string prefix) =>
        response.StatusCode == HttpStatusCode.Unauthorized
            ? $"{prefix}: usuario o contraseña rechazados."
            : $"{prefix}: {(int)response.StatusCode} {response.ReasonPhrase}.";

    private static string Preview(string body)
    {
        string clean = body.Trim().ReplaceLineEndings(" ");
        return clean.Length <= 120 ? clean : clean[..120] + "…";
    }

    private static string? Value(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

    /// <summary>
    /// Audio enviado al ritmo real de reproducción: los equipos esperan un
    /// flujo en vivo y descartan (o cortan) lo que llegue de golpe.
    /// </summary>
    private sealed class PacedAudioContent : HttpContent
    {
        private readonly byte[] _payload;
        private readonly int _repeat;

        public PacedAudioContent(byte[] payload, int repeat)
        {
            _payload = payload;
            _repeat = repeat;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            for (int pass = 0; pass < _repeat; pass++)
            {
                for (int offset = 0; offset < _payload.Length; offset += ChunkSize)
                {
                    int size = Math.Min(ChunkSize, _payload.Length - offset);
                    await stream.WriteAsync(_payload.AsMemory(offset, size), ct);
                    await stream.FlushAsync(ct);
                    await Task.Delay(ChunkMilliseconds, ct);
                }
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        /// <summary>
        /// Largo exacto: los parlantes Hikvision aceptan un cuerpo "chunked" pero
        /// NO lo reproducen (verificado con un DS-QAZ1325G1T); con Content-Length sí.
        /// </summary>
        protected override bool TryComputeLength(out long length)
        {
            length = (long)_payload.Length * _repeat;
            return true;
        }
    }
}
