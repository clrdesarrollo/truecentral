using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Axis;

/// <summary>
/// Cliente HTTP de VAPIX (la API abierta de Axis). Las cámaras con AXIS OS
/// piden Digest de fábrica y algunas aceptan Basic: el manejador de .NET
/// resuelve el desafío que ofrezca el equipo. Los errores salen como
/// <see cref="DriverException"/> con mensaje en español.
/// </summary>
internal sealed class AxisVapixClient : IDisposable
{
    private readonly HttpClient _http;

    public AxisVapixClient(DeviceConnectionInfo info, TimeSpan? timeout = null)
    {
        Host = info.Host;
        Port = info.Port;
        BaseUri = new Uri($"http://{info.Host}:{info.Port}/");
        var credentials = new NetworkCredential(info.Username, info.Password);
        var cache = new CredentialCache
        {
            { BaseUri, "Digest", credentials },
            { BaseUri, "Basic", credentials },
        };
        _http = new HttpClient(new HttpClientHandler { Credentials = cache, PreAuthenticate = true })
        {
            BaseAddress = BaseUri,
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };
    }

    public string Host { get; }
    public int Port { get; }
    public Uri BaseUri { get; }

    /// <summary>
    /// Parámetros de un grupo (param.cgi), sin el prefijo "root.". Un grupo
    /// que el equipo no tiene (firmware viejo, cámara sin PTZ) devuelve un
    /// diccionario vacío: VAPIX lo informa como texto "# Error", no como HTTP.
    /// </summary>
    public async Task<Dictionary<string, string>> GetParamsAsync(string group, CancellationToken ct)
    {
        string text = await GetStringAsync($"axis-cgi/param.cgi?action=list&group={Uri.EscapeDataString(group)}", ct);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq];
            if (key.StartsWith("root.", StringComparison.OrdinalIgnoreCase)) key = key[5..];
            result[key] = line[(eq + 1)..];
        }
        return result;
    }

    public async Task<string> GetStringAsync(string relative, CancellationToken ct)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, relative), ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<byte[]> GetBytesAsync(string relative, CancellationToken ct)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, relative), ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>POST con cuerpo JSON (las API nuevas de VAPIX: basicdeviceinfo, etc.).</summary>
    public async Task<string> PostJsonAsync(string relative, string json, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, relative)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var response = await SendAsync(request, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Envía y traduce los fallos a mensajes para el usuario. 404 sale como <see cref="AxisNotFoundException"/>.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DriverException($"La cámara Axis {Host}:{Port} no respondió a tiempo.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException)
        {
            throw new DriverException($"No se pudo conectar con la cámara Axis {Host}:{Port}. Revise la dirección y el puerto HTTP.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DriverException($"Error de comunicación con la cámara Axis {Host}:{Port}: {ex.Message}", ex);
        }

        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose();
        throw status switch
        {
            HttpStatusCode.Unauthorized => new DriverException("La cámara Axis rechazó el usuario o la contraseña."),
            HttpStatusCode.Forbidden => new DriverException(
                "El usuario no tiene permiso en la cámara Axis: use una cuenta de grupo Administrador u Operador."),
            HttpStatusCode.NotFound => new AxisNotFoundException(request.RequestUri?.ToString() ?? ""),
            _ => new DriverException($"La cámara Axis respondió HTTP {(int)status} a {request.RequestUri}."),
        };
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>La cámara no tiene esa API (firmware antiguo o función no disponible en el modelo).</summary>
internal sealed class AxisNotFoundException(string path)
    : DriverException($"La cámara Axis no ofrece {path} (firmware antiguo o función no disponible en el modelo).");
