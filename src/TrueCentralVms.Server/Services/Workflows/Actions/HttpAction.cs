using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Services.Workflows.Actions;

/// <summary>
/// Llama a un servicio externo (webhook): el clásico POST a otro sistema, a
/// una central de monitoreo, a un bot de mensajería o a un módulo de relés que
/// abre un portón. La URL, las cabeceras y el cuerpo admiten marcas; en la URL
/// los valores se escapan para no romperla.
///
/// Configuración:
/// <c>{ "method":"POST", "url":"...", "contentType":"application/json", "body":"...",
///      "headers":"X-Api-Key: 123", "auth":"None|Basic|Digest", "username":"",
///      "timeoutSeconds":15, "allowInvalidCertificate":false }</c>
/// </summary>
public sealed class HttpAction(ILogger<HttpAction> logger) : IWorkflowActionExecutor
{
    public string Type => WorkflowActionTypes.Http;
    public string Label => "Llamar a un servicio (HTTP)";
    public string Description => "Hace una petición HTTP (POST, GET...) a otro sistema con los datos del evento.";
    public bool UsesSecret => true;

    public string? Validate(JsonElement config)
    {
        string? url = config.TryGetProperty("url", out var value) ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(url)) return "Indique la URL a la que llamar.";
        // Las marcas se reemplazan al ejecutar: para validar se prueba la
        // plantilla sin ellas, que es lo que define el esquema y el host.
        string probe = url.Replace("{", "").Replace("}", "");
        if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "La URL debe ser absoluta y empezar con http:// o https://.";
        return null;
    }

    public async Task<WorkflowStepResult> ExecuteAsync(WorkflowActionContext context, CancellationToken ct)
    {
        string url = context.RenderUrl(context.Text("url"));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return WorkflowStepResult.Fail($"La URL resultante no es válida: '{url}'.");

        string method = context.Text("method", "POST").Trim().ToUpperInvariant();
        if (method.Length == 0) method = "POST";
        int timeout = Math.Clamp(context.Number("timeoutSeconds", 15), 1, 120);
        string auth = context.Text("auth", "None");
        string username = context.Text("username").Trim();

        // Un manejador por ejecución: cada acción trae sus propias
        // credenciales y su propia política de certificado, y la frecuencia
        // de las automatizaciones es baja (hay tiempo mínimo entre ejecuciones).
        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        if (context.Flag("allowInvalidCertificate", false))
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        if (username.Length > 0 && !auth.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            var credential = new NetworkCredential(username, context.Secret ?? "");
            if (auth.Equals("Digest", StringComparison.OrdinalIgnoreCase))
                handler.Credentials = new CredentialCache { { uri, "Digest", credential } };
            else
                handler.Credentials = credential;   // Basic: lo negocia el propio manejador
        }

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeout) };
        using var request = new HttpRequestMessage(new HttpMethod(method), uri);

        // Basic se envía de entrada: muchos servicios responden 401 sin
        // desafío y el manejador nunca llega a reintentar.
        if (username.Length > 0 && auth.Equals("Basic", StringComparison.OrdinalIgnoreCase))
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{context.Secret ?? ""}")));

        foreach (string line in context.Render(context.Text("headers")).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Length == 0) continue;
            if (!request.Headers.TryAddWithoutValidation(name, value))
                logger.LogDebug("Cabecera '{Name}' ignorada en una acción HTTP.", name);
        }

        string body = context.Render(context.Text("body"));
        if (body.Length > 0 && method is not ("GET" or "HEAD" or "DELETE"))
        {
            string contentType = context.Text("contentType", "application/json").Trim();
            if (contentType.Length == 0) contentType = "application/json";
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            string reply = await ReadPreviewAsync(response, ct);
            string detail = $"{method} {uri} → {(int)response.StatusCode} {response.ReasonPhrase}" +
                            (reply.Length > 0 ? $" · {reply}" : "");
            return response.IsSuccessStatusCode
                ? WorkflowStepResult.Ok(detail)
                : WorkflowStepResult.Fail(detail);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException)
        {
            return WorkflowStepResult.Fail($"El servicio {uri.Host} no respondió en {timeout} s.");
        }
        catch (HttpRequestException ex)
        {
            return WorkflowStepResult.Fail($"No se pudo llamar a {uri.Host}: {ex.Message}");
        }
    }

    /// <summary>Primeros caracteres de la respuesta, para que el historial diga qué contestó el otro sistema.</summary>
    private static async Task<string> ReadPreviewAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            string content = (await response.Content.ReadAsStringAsync(ct)).Trim().ReplaceLineEndings(" ");
            return content.Length <= 160 ? content : content[..160] + "…";
        }
        catch (Exception)
        {
            return "";
        }
    }
}
