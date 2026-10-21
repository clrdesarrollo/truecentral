using System.Net.Http;
using System.Text.Json;

namespace TrueCentralVms.Client.Services;

/// <summary>
/// Averigua solo si el servidor atiende por https o por http: el operador
/// escribe la dirección y el puerto y el cliente decide.
///
/// Siempre se prueba https primero. Se pasa a http solo cuando el puerto
/// responde, pero sin cifrar (falla el saludo TLS, no la conexión), y además
/// /api/health contesta como un servidor TrueCentral. Y nunca para una
/// dirección que ya se conocía por https (llave confiada o cuenta guardada con
/// https): eso sería una bajada de protocolo, que también es lo que haría
/// alguien en el medio para leer la contraseña.
/// </summary>
public static class ServerProtocol
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Resultado: la URL con la que ingresar, o por qué no se puede.</summary>
    public sealed record Detection(string? Url, string? Error);

    /// <param name="knownHttps">true si esta dirección ya se usó por https en este equipo.</param>
    public static async Task<Detection> DetectAsync(string host, int port, bool knownHttps, CancellationToken ct = default)
    {
        string https = Compose(Uri.UriSchemeHttps, host, port);
        string http = Compose(Uri.UriSchemeHttp, host, port);

        using (var client = new HttpClient(ServerTrust.CreateHandler()) { Timeout = ProbeTimeout })
        {
            try
            {
                using var response = await client.GetAsync(https + "/api/health", ct);
                return new(https, null); // atiende por https (el ingreso informa cualquier otro error)
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
            {
                // Certificado que este equipo no conoce: es https; el ingreso pregunta si confiar.
                if (ServerTrust.TakeRejected(https) is not null) return new(https, null);
                // Falló el TLS: el puerto puede estar atendiendo sin cifrar. Se mira abajo.
            }
            catch (Exception ex) when (ex is TaskCanceledException && !ct.IsCancellationRequested
                                       || ex is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError })
            {
                // Sin respuesta: se avisa ya (el ingreso volvería a esperar lo mismo).
                return new(null, $"No hay respuesta del servidor en {host}:{port}. Revise la dirección, el puerto y la red.");
            }
            catch (Exception)
            {
                // Cualquier otra falla: el ingreso por https la informa con su detalle.
                return new(https, null);
            }
        }

        if (!await AnswersAsTrueCentralAsync(http, ct))
            return new(null, $"En {host}:{port} responde algo que no es un servidor TrueCentral " +
                             "(ni por https ni por http). Revise la dirección y el puerto.");
        if (knownHttps)
            return new(null, $"El servidor {host}:{port} se conocía por https y ahora responde SIN CIFRAR. " +
                             "Puede ser una configuración cambiada, pero también alguien interceptando la conexión: " +
                             "no se envió la contraseña. Consulte con el administrador del sistema.");
        return new(http, null);
    }

    /// <summary>¿http://…/api/health contesta como un servidor TrueCentral ({"ok":true,…})?</summary>
    private static async Task<bool> AnswersAsTrueCentralAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { Timeout = ProbeTimeout };
            using var response = await client.GetAsync(baseUrl + "/api/health", ct);
            if (!response.IsSuccessStatusCode) return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.ValueKind == JsonValueKind.Object
                   && json.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                   && json.RootElement.TryGetProperty("version", out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// URL con protocolo, dirección y puerto. El puerto se omite si es el propio del
    /// protocolo (80 con http, 443 con https): así queda idéntica a la que se
    /// guardaba antes y las cuentas recordadas siguen encontrándose.
    /// </summary>
    public static string Compose(string scheme, string host, int port)
    {
        // IPv6 sin corchetes (fe80::1) → [fe80::1]
        if (host.Contains(':') && !host.StartsWith('[')) host = $"[{host}]";
        bool defaultPort = (scheme == Uri.UriSchemeHttp && port == 80) || (scheme == Uri.UriSchemeHttps && port == 443);
        return defaultPort ? $"{scheme}://{host}" : $"{scheme}://{host}:{port}";
    }
}
