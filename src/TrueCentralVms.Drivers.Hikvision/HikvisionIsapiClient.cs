using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Cliente HTTP para la API ISAPI de Hikvision (paneles AX PRO / AX Hybrid y
/// cualquier equipo que la hable). Cómo autentica, en este orden:
/// <list type="number">
/// <item>Si el equipo ofrece login de sesión (responde el reto en
/// <c>/ISAPI/Security/sessionLogin/capabilities</c>) entra por ahí de
/// entrada: reto SHA-256 iterado —con <b>doble salt</b> cuando el panel
/// entrega <c>salt2</c>— y cookie WebSession. Es el caso de los AX
/// vinculados a Hik-Connect, que rechazan el Digest y <b>descuentan cada
/// rechazo como intento fallido</b> hasta bloquear la IP.</item>
/// <item>Si no hay reto, HTTP Digest, que es lo que ISAPI documenta.</item>
/// </list>
/// En modo sesión las peticiones salen SOLO con la cookie (sin cabecera
/// Authorization), que es lo que el panel acepta.
///
/// Las conexiones se cachean por panel+usuario para no repetir el login en
/// cada sondeo (los paneles bloquean la cuenta tras pocos intentos fallidos
/// y limitan las sesiones simultáneas). Todo esto está verificado el
/// 2026-09-01 contra un AX Hybrid PRO real (DS-PHA64-LP, firmware V1.1.2).
/// </summary>
public sealed partial class HikvisionIsapiClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private sealed class Entry
    {
        /// <summary>Con credenciales Digest (las negocia el handler ante el 401 con desafío).</summary>
        public required HttpClient Http;
        /// <summary>Mismo CookieContainer, SIN credenciales: en modo sesión el panel recibe solo la cookie.</summary>
        public required HttpClient PlainHttp;
        public required CookieContainer Cookies;
        public required AlarmConnectionInfo Info;
        /// <summary>true = se autentica por sesión web (el equipo ofrece el reto o rechazó el digest).</summary>
        public bool SessionMode;
        /// <summary>Ya se decidió cómo autenticar (una sola vez por conexión cacheada).</summary>
        public bool AuthProbed;
        public string? SessionId;
        public DateTime LastUsed = DateTime.UtcNow;
        public readonly SemaphoreSlim LoginLock = new(1, 1);
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new();

    private readonly Entry _entry;

    /// <param name="digestOnly">
    /// No probar el login de sesión: autenticar siempre por Digest (lo que
    /// documenta el Hik IP Receiver Pro para su API; su sesión web no habilita
    /// la suscripción de eventos).
    /// </param>
    public HikvisionIsapiClient(AlarmConnectionInfo info, bool digestOnly = false)
    {
        string key = $"{info.UseHttps}|{info.Host}|{info.Port}|{info.Username}|{info.Password}|{digestOnly}";
        _entry = Entries.GetOrAdd(key, _ =>
        {
            var entry = Create(info);
            if (digestOnly) entry.AuthProbed = true; // sin sesión: el handler negocia Digest
            return entry;
        });
        _entry.LastUsed = DateTime.UtcNow;
    }

    public string BaseUrl => $"{(_entry.Info.UseHttps ? "https" : "http")}://{_entry.Info.Host}:{_entry.Info.Port}";

    private static Entry Create(AlarmConnectionInfo info)
    {
        var cookies = new CookieContainer();
        var digestHandler = new HttpClientHandler
        {
            // Digest lo negocia el propio handler ante el 401 con desafío.
            Credentials = new NetworkCredential(info.Username, info.Password),
            PreAuthenticate = true,
            CookieContainer = cookies,
            UseCookies = true,
            AllowAutoRedirect = false,
        };
        // Sin credenciales: comparte la cookie de sesión y nunca manda
        // Authorization. Un Digest que el panel no acepta le cuesta un
        // intento a la cuenta aunque la cookie sea válida.
        var plainHandler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            AllowAutoRedirect = false,
        };
        if (info.UseHttps)
        {
            // Los paneles traen certificado autofirmado: la confianza aquí es
            // por dirección/credenciales, no por la cadena de certificación.
            digestHandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            plainHandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        return new Entry
        {
            Http = Configure(new HttpClient(digestHandler)),
            PlainHttp = Configure(new HttpClient(plainHandler)),
            Cookies = cookies,
            Info = info,
        };

        static HttpClient Configure(HttpClient http)
        {
            http.Timeout = RequestTimeout;
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json, application/xml, text/xml;q=0.9, */*;q=0.5");
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CLRTrueCentralVMS/1.0");
            return http;
        }
    }

    /// <summary>
    /// Ejecuta una solicitud ISAPI y devuelve el cuerpo. Reintenta una vez
    /// con login de sesión si el digest fue rechazado, y una vez más si la
    /// sesión caducó. Lanza <see cref="DriverException"/> con mensaje en
    /// español ante credenciales inválidas, bloqueo por intentos o panel
    /// inalcanzable. Un 404 devuelve null (recurso no soportado).
    /// </summary>
    public async Task<string?> RequestAsync(HttpMethod method, string path, string? body = null,
        string contentType = "application/json", CancellationToken ct = default, bool allowNotFound = true)
    {
        using var response = await SendAsync(method, path, body, contentType, ct);
        if (response.StatusCode == HttpStatusCode.NotFound && allowNotFound)
            return null;
        string text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new DriverException(DescribeFailure(response.StatusCode, text));
        return text;
    }

    /// <summary>Solicitud de flujo largo (alertStream, suscripción de eventos): devuelve la respuesta abierta con las cabeceras leídas.</summary>
    public async Task<HttpResponseMessage> OpenStreamAsync(string path, CancellationToken ct, HttpMethod? method = null, string? body = null)
    {
        var response = await SendAsync(method ?? HttpMethod.Get, path, body, "application/json", ct, streaming: true);
        if (!response.IsSuccessStatusCode)
        {
            string text = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            throw new DriverException(DescribeFailure(response.StatusCode, text));
        }
        return response;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, string contentType,
        CancellationToken ct, bool streaming = false)
    {
        _entry.LastUsed = DateTime.UtcNow;
        if (!_entry.AuthProbed)
            await ProbeAuthAsync(ct);
        var response = await SendOnceAsync(method, path, body, contentType, ct, streaming);
        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
        {
            // El AX Hybrid PRO contesta 301 → https://host:443 a todo lo que
            // entra por el puerto 80. No se sigue (cambiaría esquema y puerto
            // a espaldas de la configuración): se explica y se corrige en el
            // mantenedor.
            string target = response.Headers.Location?.ToString() ?? "otra dirección";
            response.Dispose();
            throw new DriverException($"El panel redirige a {target}: active HTTPS y use ese puerto en la configuración del panel.");
        }
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        // 401: o el digest no sirve en este equipo, o la sesión web caducó.
        string text = await response.Content.ReadAsStringAsync(ct);
        response.Dispose();
        ThrowIfLocked(text);

        await SessionLoginAsync(ct);
        response = await SendOnceAsync(method, path, body, contentType, ct, streaming);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        text = await response.Content.ReadAsStringAsync(ct);
        response.Dispose();
        ThrowIfLocked(text);
        throw new DriverException("El panel rechazó las credenciales (usuario o contraseña incorrectos).");
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, string? body, string contentType,
        CancellationToken ct, bool streaming)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        if (_entry.SessionMode && _entry.SessionId is { } sid && _entry.Cookies.GetCookies(new Uri(BaseUrl)).Count == 0)
            request.Headers.TryAddWithoutValidation("Cookie", $"WebSession={sid}");
        // En modo sesión la petición sale sin credenciales Digest: basta la
        // cookie, y el panel contabiliza un Authorization rechazado como
        // intento de login fallido (el DS-PHA64-LP devuelve retryLoginTime
        // al rechazar un Digest).
        var http = _entry.SessionMode ? _entry.PlainHttp : _entry.Http;
        try
        {
            return await http.SendAsync(request,
                streaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new DriverException($"No se pudo conectar con el panel en {_entry.Info.Host}:{_entry.Info.Port}: {ex.Message}", ex);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DriverException($"El panel en {_entry.Info.Host}:{_entry.Info.Port} no respondió a tiempo.");
        }
    }

    // ------------------------------------------------------------------
    // Login de sesión web (SHA-256 iterado)
    // ------------------------------------------------------------------

    /// <summary>
    /// Decide una sola vez cómo autenticar esta conexión. Si el equipo
    /// entrega el reto de login de sesión, se entra por sesión de inmediato y
    /// no se prueba Digest: los paneles AX vinculados a Hik-Connect lo
    /// rechazan y cada rechazo consume un intento (bloqueo por IP a los
    /// pocos). Sin reto, queda el Digest que negocia el handler.
    /// Un fallo de red no fija la decisión: se vuelve a intentar después.
    /// </summary>
    private async Task ProbeAuthAsync(CancellationToken ct)
    {
        await _entry.LoginLock.WaitAsync(ct);
        try
        {
            if (_entry.AuthProbed) return;
            string capUrl = $"{BaseUrl}/ISAPI/Security/sessionLogin/capabilities?username={Uri.EscapeDataString(_entry.Info.Username)}";
            string? cap;
            try
            {
                using var response = await _entry.PlainHttp.GetAsync(capUrl, ct);
                cap = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
            }
            catch (HttpRequestException ex)
            {
                throw new DriverException($"No se pudo conectar con el panel en {_entry.Info.Host}:{_entry.Info.Port}: {ex.Message}", ex);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new DriverException($"El panel en {_entry.Info.Host}:{_entry.Info.Port} no respondió a tiempo.");
            }
            _entry.AuthProbed = true;
            if (cap is null || !cap.Contains("<challenge>", StringComparison.OrdinalIgnoreCase))
                return; // sin login de sesión: Digest
            // Desde ya en modo sesión: cualquier petición concurrente sale sin
            // Digest (un 401 sin credenciales no cuenta como intento fallido).
            _entry.SessionMode = true;
        }
        finally
        {
            _entry.LoginLock.Release();
        }
        await SessionLoginAsync(ct);
    }

    /// <summary>
    /// Reproduce el login de la interfaz web de Hikvision: se pide un reto
    /// (sessionID, challenge, salt, salt2, iteraciones) y se envía la
    /// contraseña transformada. Con isIrreversible=true (paneles y firmware
    /// modernos) la cadena es SHA256(usuario+salt+clave) →
    /// [SHA256(usuario+salt2+x) si el panel entrega salt2] → SHA256(x+challenge)
    /// → SHA256^n. El paso con salt2 se leyó del propio firmware (función
    /// <c>encodePwd</c>): sin él, el panel responde 401 aunque la clave sea
    /// correcta.
    /// </summary>
    private async Task SessionLoginAsync(CancellationToken ct)
    {
        await _entry.LoginLock.WaitAsync(ct);
        try
        {
            var info = _entry.Info;
            string capUrl = $"{BaseUrl}/ISAPI/Security/sessionLogin/capabilities?username={Uri.EscapeDataString(info.Username)}";
            string cap;
            try
            {
                cap = await _entry.PlainHttp.GetStringAsync(capUrl, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new DriverException($"El panel no ofrece login de sesión y rechazó el digest: {ex.Message}", ex);
            }

            var doc = XDocument.Parse(cap);
            string Value(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim() ?? "";
            string sessionId = Value("sessionID");
            string challenge = Value("challenge");
            string salt = Value("salt");
            string salt2 = Value("salt2");
            int iterations = int.TryParse(Value("iterations"), out int it) ? it : 100;
            bool irreversible = string.Equals(Value("isIrreversible"), "true", StringComparison.OrdinalIgnoreCase);
            string version = Value("sessionIDVersion");
            if (sessionId.Length == 0 || challenge.Length == 0)
                throw new DriverException("El panel no entregó el reto de login de sesión (respuesta inesperada).");

            string encoded;
            if (irreversible)
            {
                encoded = Sha256Hex(info.Username + salt + info.Password);
                // Doble salt (AX Hybrid PRO DS-PHA64-LP V1.1.2 y, en general,
                // los paneles vinculados a Hik-Connect): un segundo SHA-256 con
                // salt2 sobre el hash anterior, ANTES del reto. Verificado con
                // 200 OK contra el panel real; con un solo salt responde 401.
                if (salt2.Length > 0)
                    encoded = Sha256Hex(info.Username + salt2 + encoded);
                encoded = Sha256Hex(encoded + challenge);
                for (int i = 2; i < iterations; i++)
                    encoded = Sha256Hex(encoded);
            }
            else
            {
                encoded = Sha256Hex(info.Password + challenge);
                for (int i = 1; i < iterations; i++)
                    encoded = Sha256Hex(encoded);
            }

            string xml =
                "<SessionLogin>" +
                $"<userName>{Escape(info.Username)}</userName>" +
                $"<password>{encoded}</password>" +
                $"<sessionID>{sessionId}</sessionID>" +
                "<isSessionIDValidLongTerm>false</isSessionIDValidLongTerm>" +
                $"<sessionIDVersion>{(version.Length > 0 ? version : "2.1")}</sessionIDVersion>" +
                "</SessionLogin>";

            long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/ISAPI/Security/sessionLogin?timeStamp={stamp}")
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
            };
            using var response = await _entry.PlainHttp.SendAsync(request, ct);
            string text = await response.Content.ReadAsStringAsync(ct);
            ThrowIfLocked(text);
            if (!response.IsSuccessStatusCode)
                throw new DriverException(response.StatusCode == HttpStatusCode.Unauthorized
                    ? "El panel rechazó las credenciales (usuario o contraseña incorrectos)."
                    : DescribeFailure(response.StatusCode, text));

            string? newSession = null;
            try
            {
                var login = XDocument.Parse(text);
                newSession = login.Descendants().FirstOrDefault(e => e.Name.LocalName == "sessionID")?.Value?.Trim();
                string status = login.Descendants().FirstOrDefault(e => e.Name.LocalName == "statusValue")?.Value?.Trim() ?? "";
                if (status.Length > 0 && status != "200")
                    throw new DriverException("El panel rechazó el login de sesión: " +
                        (login.Descendants().FirstOrDefault(e => e.Name.LocalName == "statusString")?.Value ?? status));
            }
            catch (System.Xml.XmlException)
            {
                // Algunos firmware responden vacío y solo fijan la cookie.
            }
            _entry.SessionMode = true;
            _entry.SessionId = string.IsNullOrEmpty(newSession) ? sessionId : newSession;
        }
        finally
        {
            _entry.LoginLock.Release();
        }
    }

    private static string Sha256Hex(string value)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return Convert.ToHexStringLower(hash);
    }

    private static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? value;

    // ------------------------------------------------------------------
    // Errores
    // ------------------------------------------------------------------

    [GeneratedRegex("<lockStatus>\\s*lock\\s*</lockStatus>", RegexOptions.IgnoreCase)]
    private static partial Regex LockedPattern();

    [GeneratedRegex("<unlockTime>\\s*(\\d+)\\s*</unlockTime>", RegexOptions.IgnoreCase)]
    private static partial Regex UnlockTimePattern();

    [GeneratedRegex("<retryLoginTime>\\s*(\\d+)\\s*</retryLoginTime>", RegexOptions.IgnoreCase)]
    private static partial Regex RetryLoginTimePattern();

    /// <summary>
    /// Los equipos Hikvision bloquean la cuenta/IP tras varios intentos
    /// fallidos y lo dicen en el cuerpo del 401. Hay que dejar de insistir:
    /// cada intento adicional renueva el bloqueo. Antes de bloquear avisan
    /// cuántos intentos quedan (<c>retryLoginTime</c>): con uno o ninguno no
    /// se vuelve a intentar, porque el siguiente fallo deja al panel sin
    /// monitoreo durante 30 min.
    /// </summary>
    private static void ThrowIfLocked(string? body)
    {
        if (string.IsNullOrEmpty(body)) return;
        var r = RetryLoginTimePattern().Match(body);
        if (r.Success && int.TryParse(r.Groups[1].Value, out int left) && left <= 1)
            throw new DriverException("El panel rechazó las credenciales y está a un intento de bloquear el acceso. " +
                                      "Verifique el usuario y la contraseña antes de reintentar.");
        if (!LockedPattern().IsMatch(body)) return;
        var m = UnlockTimePattern().Match(body);
        string wait = m.Success && int.TryParse(m.Groups[1].Value, out int seconds)
            ? $" Se libera en {seconds / 60} min."
            : "";
        throw new DriverException("El panel bloqueó el acceso por intentos de login fallidos." + wait +
                                  " Verifique el usuario y la contraseña antes de reintentar.");
    }

    private static string DescribeFailure(HttpStatusCode status, string body)
    {
        string? statusString = null, subStatus = null, errorMsg = null;
        if (body.TrimStart().StartsWith('<'))
        {
            try
            {
                var doc = XDocument.Parse(body);
                statusString = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "statusString")?.Value;
                subStatus = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "subStatusCode")?.Value;
                errorMsg = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorMsg")?.Value;
            }
            catch { /* no era XML válido */ }
        }
        else if (body.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("statusString", out var s)) statusString = s.GetString();
                if (doc.RootElement.TryGetProperty("subStatusCode", out var sub)) subStatus = sub.GetString();
                if (doc.RootElement.TryGetProperty("errorMsg", out var em)) errorMsg = em.GetString();
            }
            catch { /* no era JSON válido */ }
        }

        string detail = string.Join(" · ", new[] { statusString, subStatus, errorMsg }.Where(s => !string.IsNullOrWhiteSpace(s))!);
        return status switch
        {
            HttpStatusCode.Forbidden => "El panel denegó la operación (el usuario no tiene permiso)." + Suffix(detail),
            HttpStatusCode.NotFound => "El panel no soporta esta función (recurso ISAPI inexistente)." + Suffix(detail),
            HttpStatusCode.BadRequest => "El panel rechazó la solicitud." + Suffix(detail),
            HttpStatusCode.Locked => "El panel rechazó la orden: el recurso está bloqueado." + Suffix(detail),
            _ => $"El panel respondió {(int)status}." + Suffix(detail),
        };

        static string Suffix(string detail) => detail.Length > 0 ? $" ({detail})" : "";
    }

    /// <summary>Cierra y olvida la conexión cacheada (credenciales cambiadas o panel eliminado).</summary>
    public static void Forget(AlarmConnectionInfo info)
    {
        foreach (bool digestOnly in new[] { false, true })
        {
            string key = $"{info.UseHttps}|{info.Host}|{info.Port}|{info.Username}|{info.Password}|{digestOnly}";
            if (Entries.TryRemove(key, out var entry))
            {
                entry.Http.Dispose();
                entry.PlainHttp.Dispose();
            }
        }
    }
}
