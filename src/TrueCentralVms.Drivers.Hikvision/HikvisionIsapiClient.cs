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
        /// <summary>
        /// Cliente SIN cabeceras por defecto, solo para el login de sesión.
        /// Comparte las cookies con los otros dos.
        /// </summary>
        public required HttpClient LoginHttp;
        public required CookieContainer Cookies;
        public required AlarmConnectionInfo Info;
        /// <summary>true = se autentica por sesión web (el equipo ofrece el reto o rechazó el digest).</summary>
        public bool SessionMode;
        /// <summary>Forma del POST de login que aceptó este equipo (null: todavía no se sabe).</summary>
        public SessionLoginStyle? LoginStyle;
        /// <summary>Ya se decidió cómo autenticar (una sola vez por conexión cacheada).</summary>
        public bool AuthProbed;
        public string? SessionId;
        public DateTime LastUsed = DateTime.UtcNow;
        public readonly SemaphoreSlim LoginLock = new(1, 1);
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new();

    private readonly Entry _entry;
    /// <summary>Cómo se nombra el equipo en los mensajes de error ("panel", "equipo de control de acceso").</summary>
    private readonly string _noun;

    /// <param name="digestOnly">
    /// No probar el login de sesión: autenticar siempre por Digest (lo que
    /// documenta el Hik IP Receiver Pro para su API; su sesión web no habilita
    /// la suscripción de eventos).
    /// </param>
    public HikvisionIsapiClient(AlarmConnectionInfo info, bool digestOnly = false, string deviceNoun = "panel")
    {
        string key = $"{info.UseHttps}|{info.Host}|{info.Port}|{info.Username}|{info.Password}|{digestOnly}";
        _entry = Entries.GetOrAdd(key, _ =>
        {
            var entry = Create(info);
            if (digestOnly) entry.AuthProbed = true; // sin sesión: el handler negocia Digest
            return entry;
        });
        _entry.LastUsed = DateTime.UtcNow;
        _noun = deviceNoun;
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
        // Tercer handler para el login: mismas cookies, sin cabeceras propias.
        var loginHandler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            AllowAutoRedirect = false,
        };
        if (info.UseHttps)
            loginHandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        return new Entry
        {
            Http = Configure(new HttpClient(digestHandler)),
            PlainHttp = Configure(new HttpClient(plainHandler)),
            // A propósito SIN Configure: ver LoginHttp.
            LoginHttp = new HttpClient(loginHandler) { Timeout = RequestTimeout },
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
            throw new DriverException(DescribeFailure(response.StatusCode, text, _noun));
        return text;
    }

    /// <summary>
    /// Sube un archivo con metadatos, como pide la matrícula de rostros:
    /// <c>multipart/form-data</c> con una parte JSON y otra binaria. Es la única
    /// forma que aceptan los terminales para la foto —el modelo lo arman ellos,
    /// no se les manda una plantilla— y por eso no alcanza con el envío normal.
    /// </summary>
    public async Task<string?> RequestMultipartAsync(string path, string jsonPartName, string json,
        string filePartName, string fileName, byte[] file, string fileContentType, CancellationToken ct)
    {
        HttpContent Build()
        {
            // El separador va SIN comillas. .NET escribe
            // `boundary="----xxx"` —válido según el RFC— y el terminal
            // responde `badJsonFormat`: su parser se queda con la comilla
            // pegada al separador y no encuentra ninguna parte. Por eso la
            // cabecera se arma a mano.
            string boundary = "----TrueCentral" + Guid.NewGuid().ToString("N");
            var form = new MultipartFormDataContent(boundary);
            form.Headers.Remove("Content-Type");
            form.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");

            var meta = new StringContent(json, Encoding.UTF8, "application/json");
            form.Add(meta, jsonPartName);
            var blob = new ByteArrayContent(file);
            blob.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(fileContentType);
            form.Add(blob, filePartName, fileName);
            return form;
        }

        using var response = await SendAsync(HttpMethod.Post, path, null, "application/json", ct, content: Build);
        string text = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw new DriverException(DescribeFailure(response.StatusCode, text, _noun));
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
            throw new DriverException(DescribeFailure(response.StatusCode, text, _noun));
        }
        return response;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, string contentType,
        CancellationToken ct, bool streaming = false, Func<HttpContent>? content = null)
    {
        _entry.LastUsed = DateTime.UtcNow;
        if (!_entry.AuthProbed)
            await ProbeAuthAsync(ct);
        var response = await SendOnceAsync(method, path, body, contentType, ct, streaming, content);
        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
        {
            // El AX Hybrid PRO contesta 301 → https://host:443 a todo lo que
            // entra por el puerto 80. No se sigue (cambiaría esquema y puerto
            // a espaldas de la configuración): se explica y se corrige en el
            // mantenedor.
            string target = response.Headers.Location?.ToString() ?? "otra dirección";
            response.Dispose();
            throw new DriverException($"El {_noun} redirige a {target}: active HTTPS y use ese puerto en su configuración.");
        }
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        // 401: o el digest no sirve en este equipo, o la sesión web caducó.
        string text = await response.Content.ReadAsStringAsync(ct);
        response.Dispose();
        ThrowIfLocked(text);

        await SessionLoginAsync(ct);
        response = await SendOnceAsync(method, path, body, contentType, ct, streaming, content);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        text = await response.Content.ReadAsStringAsync(ct);
        response.Dispose();
        ThrowIfLocked(text);
        throw new DriverException(BadCredentials(text));
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, string? body, string contentType,
        CancellationToken ct, bool streaming, Func<HttpContent>? content = null)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        // El contenido se FABRICA en cada intento: un HttpContent ya enviado no
        // se puede volver a mandar, y acá se reintenta al caducar la sesión.
        if (content is not null) request.Content = content();
        else if (body is not null)
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
            throw new DriverException($"No se pudo conectar con el {_noun} en {_entry.Info.Host}:{_entry.Info.Port}: {ex.Message}", ex);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DriverException($"El {_noun} en {_entry.Info.Host}:{_entry.Info.Port} no respondió a tiempo.");
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
        string? reto = null;
        await _entry.LoginLock.WaitAsync(ct);
        try
        {
            if (_entry.AuthProbed) return;
            string capUrl = $"{BaseUrl}/ISAPI/Security/sessionLogin/capabilities?username={Uri.EscapeDataString(_entry.Info.Username)}";
            string? cap;
            try
            {
                using var response = await _entry.LoginHttp.GetAsync(capUrl, ct);
                cap = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
            }
            catch (HttpRequestException ex)
            {
                throw new DriverException($"No se pudo conectar con el {_noun} en {_entry.Info.Host}:{_entry.Info.Port}: {ex.Message}", ex);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new DriverException($"El {_noun} en {_entry.Info.Host}:{_entry.Info.Port} no respondió a tiempo.");
            }
            _entry.AuthProbed = true;
            if (cap is null || !cap.Contains("<challenge>", StringComparison.OrdinalIgnoreCase))
                return; // sin login de sesión: Digest
            // Desde ya en modo sesión: cualquier petición concurrente sale sin
            // Digest (un 401 sin credenciales no cuenta como intento fallido).
            _entry.SessionMode = true;
            reto = cap;
        }
        finally
        {
            _entry.LoginLock.Release();
        }
        await SessionLoginAsync(reto, ct);
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
    private Task SessionLoginAsync(CancellationToken ct) => SessionLoginAsync(null, ct);

    private async Task SessionLoginAsync(string? capabilities, CancellationToken ct)
    {
        await _entry.LoginLock.WaitAsync(ct);
        try
        {
            // Se prueban las formas conocidas del POST de login: la primera es
            // la que manda la propia página web del equipo; la segunda, la que
            // veníamos usando. Ver SessionLoginStyle para el porqué.
            var estilos = _entry.LoginStyle is { } conocida ? new[] { conocida } : LoginStyles;
            string lastText = "";
            var lastStatus = HttpStatusCode.Unauthorized;

            bool primero = true;
            foreach (var estilo in estilos)
            {
                // El reto ya pedido sirve UNA vez; el siguiente intento pide el suyo.
                var (ok, status, text) = await TrySessionLoginAsync(estilo, ct, primero ? capabilities : null);
                primero = false;
                if (ok)
                {
                    _entry.LoginStyle = estilo;
                    return;
                }
                lastStatus = status;
                lastText = text;
                // Un bloqueo por intentos no se reintenta con otra forma: cada
                // intento extra renueva el bloqueo.
                ThrowIfLocked(text);
                if (status != HttpStatusCode.Unauthorized) break;
            }

            throw new DriverException(lastStatus == HttpStatusCode.Unauthorized
                ? BadCredentials(lastText)
                : DescribeFailure(lastStatus, lastText, _noun));
        }
        finally
        {
            _entry.LoginLock.Release();
        }
    }

    /// <summary>
    /// Formas del POST de <c>sessionLogin</c>, en orden de preferencia.
    ///
    /// No es un capricho: el DS-K1T804AMF (y la familia DS-K de control de
    /// acceso) devuelve <b>401 con la contraseña correcta</b> si el
    /// <c>Content-Type</c> lleva <c>charset</c> o si la URL trae
    /// <c>?timeStamp=</c>. Verificado contra el equipo: quitando cualquiera de
    /// las dos, el mismo hash entra. <see cref="Web"/> es lo que manda su propia
    /// página de login; <see cref="Classic"/> es lo que veníamos usando y lo que
    /// aceptan los paneles de alarma.
    ///
    /// Cuál funcionó queda anotado en la conexión, así que el rodeo se paga una
    /// sola vez: importa, porque cada rechazo le consume un intento de login a
    /// la cuenta del equipo.
    /// </summary>
    internal enum SessionLoginStyle
    {
        /// <summary>Content-Type sin charset y sin timeStamp en la URL.</summary>
        Web,
        /// <summary>Content-Type con charset y timeStamp en la URL.</summary>
        Classic,
    }

    private static readonly SessionLoginStyle[] LoginStyles = [SessionLoginStyle.Web, SessionLoginStyle.Classic];

    /// <summary>
    /// Un intento de login completo con una forma concreta. El reto se pide DE
    /// NUEVO cada vez: el equipo lo rota y reusar el anterior es un rechazo
    /// seguro (también verificado contra el equipo).
    /// </summary>
    private async Task<(bool Ok, HttpStatusCode Status, string Text)> TrySessionLoginAsync(
        SessionLoginStyle style, CancellationToken ct, string? capabilities = null)
    {
        var info = _entry.Info;
        string capUrl = $"{BaseUrl}/ISAPI/Security/sessionLogin/capabilities?username={Uri.EscapeDataString(info.Username)}";
        string cap;
        try
        {
            // Si ya se pidió el reto (lo hace ProbeAuthAsync para decidir el
            // modo), se usa ESE: pedirlo otra vez lo invalida.
            cap = capabilities ?? await _entry.LoginHttp.GetStringAsync(capUrl, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new DriverException($"El {_noun} no ofrece login de sesión y rechazó el digest: {ex.Message}", ex);
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
            throw new DriverException($"El {_noun} no entregó el reto de login de sesión (respuesta inesperada).");

        string encoded;
        if (irreversible)
        {
            encoded = Sha256Hex(info.Username + salt + info.Password);
            // Doble salt (AX Hybrid PRO DS-PHA64-LP V1.1.2 y, en general, los
            // paneles vinculados a Hik-Connect): un segundo SHA-256 con salt2
            // sobre el hash anterior, ANTES del reto. Verificado con 200 OK
            // contra el panel real; con un solo salt responde 401.
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

        string url = $"{BaseUrl}/ISAPI/Security/sessionLogin";
        if (style == SessionLoginStyle.Classic)
            url += $"?timeStamp={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (style == SessionLoginStyle.Classic)
        {
            request.Content = new StringContent(xml, Encoding.UTF8, "application/xml");
        }
        else
        {
            // StringContent agrega "; charset=utf-8" y este firmware lo rechaza:
            // el cuerpo va como bytes, con el Content-Type puesto a mano.
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(xml));
            content.Headers.TryAddWithoutValidation("Content-Type", "application/xml");
            request.Content = content;
        }

        using var response = await _entry.LoginHttp.SendAsync(request, ct);
        string text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return (false, response.StatusCode, text);

        string? newSession = null;
        try
        {
            var login = XDocument.Parse(text);
            newSession = login.Descendants().FirstOrDefault(e => e.Name.LocalName == "sessionID")?.Value?.Trim();
            string status = login.Descendants().FirstOrDefault(e => e.Name.LocalName == "statusValue")?.Value?.Trim() ?? "";
            if (status.Length > 0 && status != "200")
                throw new DriverException($"El {_noun} rechazó el login de sesión: " +
                    (login.Descendants().FirstOrDefault(e => e.Name.LocalName == "statusString")?.Value ?? status));
        }
        catch (System.Xml.XmlException)
        {
            // Algunos firmware responden vacío y solo fijan la cookie.
        }
        _entry.SessionMode = true;
        _entry.SessionId = string.IsNullOrEmpty(newSession) ? sessionId : newSession;
        return (true, response.StatusCode, text);
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

    // Los equipos contestan XML o JSON segun el endpoint (el IP Receiver Pro usa
    // JSON), asi que los patrones aceptan las dos formas: <lockStatus>lock</...>
    // y "lockStatus":"lock".
    [GeneratedRegex("(<lockStatus>\\s*lock\\s*</lockStatus>)|(\"lockStatus\"\\s*:\\s*\"lock\")", RegexOptions.IgnoreCase)]
    private static partial Regex LockedPattern();

    [GeneratedRegex("(<unlockTime>\\s*(?<v>\\d+)\\s*</unlockTime>)|(\"unlockTime\"\\s*:\\s*(?<v>\\d+))", RegexOptions.IgnoreCase)]
    private static partial Regex UnlockTimePattern();

    [GeneratedRegex("(<retryLoginTime>\\s*(?<v>\\d+)\\s*</retryLoginTime>)|(\"retryLoginTime\"\\s*:\\s*(?<v>\\d+))", RegexOptions.IgnoreCase)]
    private static partial Regex RetryLoginTimePattern();

    /// <summary>
    /// Los equipos Hikvision bloquean la cuenta/IP tras varios intentos
    /// fallidos y lo dicen en el cuerpo del 401. Hay que dejar de insistir:
    /// cada intento adicional renueva el bloqueo. Antes de bloquear avisan
    /// cuántos intentos quedan (<c>retryLoginTime</c>): con uno o ninguno no
    /// se vuelve a intentar, porque el siguiente fallo deja al panel sin
    /// monitoreo durante 30 min.
    /// </summary>
    private void ThrowIfLocked(string? body) => ThrowIfLocked(body, _noun, Address);

    /// <summary>Dónde ocurrió: va en todos los mensajes, porque el operador suele tener varios equipos.</summary>
    private string Address => $"{_entry.Info.Host}:{_entry.Info.Port}";

    private static void ThrowIfLocked(string? body, string noun = "panel", string? address = null)
    {
        if (string.IsNullOrEmpty(body)) return;
        string donde = address is null ? "" : $" en {address}";

        // Bloqueo efectivo: hay que DEJAR de intentar, cada intento lo renueva.
        if (LockedPattern().IsMatch(body))
        {
            var m = UnlockTimePattern().Match(body);
            string wait = m.Success && int.TryParse(m.Groups["v"].Value, out int seconds)
                ? $" Se libera en {Math.Max(1, seconds / 60)} minuto(s)."
                : "";
            throw new DriverException(
                $"El {noun}{donde} bloqueó el inicio de sesión por intentos fallidos.{wait} " +
                "Verifique el usuario y la contraseña: reintentar ahora solo alarga el bloqueo.");
        }

        // A un intento del bloqueo: se corta igual, para no gastarlo.
        var r = RetryLoginTimePattern().Match(body);
        if (r.Success && int.TryParse(r.Groups["v"].Value, out int left) && left <= 1)
            throw new DriverException(
                $"El {noun}{donde} rechazó las credenciales y queda UN intento antes de que bloquee el " +
                "inicio de sesión. Verifique el usuario y la contraseña antes de reintentar.");
    }

    /// <summary>
    /// Mensaje de "usuario o contraseña incorrectos" con lo que el propio equipo
    /// informa: dónde fue y cuántos intentos le quedan a la cuenta antes de que
    /// bloquee. Saberlo evita el clásico "probé tres veces más y me quedé afuera".
    /// </summary>
    private string BadCredentials(string? body)
    {
        string mensaje = $"El {_noun} en {Address} rechazó las credenciales (usuario o contraseña incorrectos).";
        if (string.IsNullOrEmpty(body)) return mensaje;
        var r = RetryLoginTimePattern().Match(body);
        if (r.Success && int.TryParse(r.Groups["v"].Value, out int left) && left > 1)
            mensaje += $" Quedan {left} intentos antes de que el equipo bloquee el inicio de sesión.";
        return mensaje;
    }

    private static string DescribeFailure(HttpStatusCode status, string body, string noun = "panel")
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
            HttpStatusCode.Forbidden => $"El {noun} denegó la operación (el usuario no tiene permiso)." + Suffix(detail),
            HttpStatusCode.NotFound => $"El {noun} no soporta esta función (recurso ISAPI inexistente)." + Suffix(detail),
            HttpStatusCode.BadRequest => $"El {noun} rechazó la solicitud." + Suffix(detail),
            HttpStatusCode.Locked => $"El {noun} rechazó la orden: el recurso está bloqueado." + Suffix(detail),
            _ => $"El {noun} respondió {(int)status}." + Suffix(detail),
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
