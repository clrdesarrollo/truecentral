using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Autenticación HTTP Digest propia para ISAPI (RFC 2617/7616, la que pide la
/// guía), en lugar de la de .NET.
///
/// .NET no adelanta credenciales Digest (<c>PreAuthenticate</c> solo sirve para
/// Basic): CADA petición salía sin credenciales, recibía un 401 con el desafío
/// y se repetía. Medido contra los terminales: el doble de peticiones y de
/// conexiones, en equipos que atienden una operación a la vez.
///
/// Acá se reutiliza el último desafío (<c>nonce</c>) con el contador
/// <c>nc</c> creciente, pero solo dentro de una ventana corta: los terminales
/// lo vencen a los pocos segundos (medido el 2026-10-08: ~3 s el DS-K1T323MBWX
/// V4.23, menos de 3 s el DS-K1T321MFWX V3.9.20, entre 3 y 4 s el DS-K1T804AMF
/// V1.4.0). Sirve en las ráfagas —validar un equipo, escribir una persona—, que
/// es donde se juntan las peticiones.
///
/// Ante un nonce vencido los equipos contestan distinto:
/// <list type="bullet">
/// <item><c>stale="true"</c> con el desafío nuevo (DS-K1T321MFWX): se repite
/// con ese.</item>
/// <item>401 sin desafío (DS-K1T323MBWX): se pide uno y se repite.</item>
/// <item>401 con <c>retryLoginTime</c> (DS-K1T804AMF): lo COBRA como intento
/// fallido. Ese equipo deja de reutilizar desafíos en esta conexión, y se repite
/// una sola vez con uno fresco si estas credenciales ya entraron antes y le
/// quedan al menos dos intentos (el éxito le restablece el contador).</item>
/// </list>
/// En los dos primeros la ventana del equipo se achica para no volver a
/// pasarse. Fuera de ese caso, un 401 con <c>retryLoginTime</c> o
/// <c>lockStatus</c> es un rechazo de credenciales de verdad: NO se reintenta
/// (cada intento cuenta para el bloqueo) y se devuelve tal cual para que el
/// cliente lo explique.
/// </summary>
internal sealed partial class IsapiDigestHandler(string username, string password, HttpMessageHandler inner)
    : DelegatingHandler(inner)
{
    /// <summary>Ventana inicial para reutilizar un desafío; se achica si el equipo lo vence antes.</summary>
    /// <remarks>
    /// Corta a propósito: el DS-K1T804AMF V1.4.0 cobra como intento fallido un
    /// nonce vencido y su contador NO vuelve a cero con un acceso correcto
    /// (medido: 5 → 4 → 3 con logins buenos en medio). Un segundo de margen
    /// contra vidas de 2,5 s o más.
    /// </remarks>
    private static readonly TimeSpan InitialReuseWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinReuseWindow = TimeSpan.FromMilliseconds(500);

    private readonly object _lock = new();
    private Challenge? _challenge;
    private DateTime _challengeAt;
    private TimeSpan _reuseWindow = InitialReuseWindow;
    private long _nc;
    /// <summary>Estas credenciales ya entraron alguna vez en esta conexión.</summary>
    private bool _proven;
    /// <summary>Peticiones a este equipo que todavía esperan respuesta.</summary>
    private int _inFlight;

    private sealed record Challenge(string Scheme, string Realm, string Nonce, string? Opaque, string? Qop,
        string Algorithm);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // El cuerpo se deja en memoria para poder repetir la petición.
        if (request.Content is not null) await request.Content.LoadIntoBufferAsync(ct);

        // Solo se reutiliza el desafío si esta es la única petición en curso al
        // equipo: si hay otra, la nuestra puede quedar esperando detrás de un
        // equipo ocupado y llegar con el nonce vencido.
        bool alone = Interlocked.Increment(ref _inFlight) == 1;
        try
        {
            return await SendAuthorizedAsync(request, alone, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(HttpRequestMessage request, bool mayReuse,
        CancellationToken ct)
    {
        bool reused = mayReuse && TryAuthorize(request, fresh: false);
        var response = await base.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            if (request.Headers.Authorization is not null) _proven = true;
            return response;
        }

        // 401. Se mira el cuerpo antes de decidir: si el equipo descontó un
        // intento, no se insiste (salvo el nonce reutilizado que cobra, abajo).
        string body = await PeekAsync(response, ct);
        var offered = ParseChallenge(response.Headers.WwwAuthenticate);
        if (CountsAsFailure(body))
        {
            if (!reused) return response;
            // DS-K1T804AMF: cobró un nonce vencido. No se reutilizan más, y se
            // repite UNA vez con uno fresco si estas credenciales ya entraron y
            // le quedan intentos: el éxito le restablece el contador.
            DisableReuse();
            if (!_proven || RetriesLeft(body) is not int left || left < 2) return response;
            offered ??= await FetchChallengeAsync(request, ct);
            return offered is null ? response : await RetryAsync(request, response, offered, ct);
        }

        if (offered is null)
        {
            // Sin desafío: solo tiene arreglo si fue un nonce reutilizado que el
            // equipo dio por vencido (DS-K1T323MBWX). Se pide uno nuevo.
            if (!reused) return response;
            Shrink();
            offered = await FetchChallengeAsync(request, ct);
            if (offered is null) return response;
        }
        else if (reused)
        {
            Shrink();   // vencido con stale="true": se repite con el desafío que vino
        }

        return await RetryAsync(request, response, offered, ct);
    }

    /// <summary>Repite la petición, una sola vez, con un desafío nuevo.</summary>
    private async Task<HttpResponseMessage> RetryAsync(HttpRequestMessage request, HttpResponseMessage rejected,
        Challenge offered, CancellationToken ct)
    {
        Store(offered);
        rejected.Dispose();
        var retry = Clone(request);
        TryAuthorize(retry, fresh: true);
        var response = await base.SendAsync(retry, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized) _proven = true;
        return response;
    }

    /// <summary>
    /// Un desafío nuevo: un GET sin credenciales a la misma ruta, que el equipo
    /// contesta con 401 antes de hacer nada (el nonce no depende de la ruta).
    /// </summary>
    private async Task<Challenge?> FetchChallengeAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var probe = new HttpRequestMessage(HttpMethod.Get, request.RequestUri);
        using var answer = await base.SendAsync(probe, ct);
        return ParseChallenge(answer.Headers.WwwAuthenticate);
    }

    /// <summary>Pone Authorization con el desafío guardado. false si no había uno vigente.</summary>
    private bool TryAuthorize(HttpRequestMessage request, bool fresh)
    {
        Challenge challenge;
        long nc;
        lock (_lock)
        {
            if (_challenge is null) return false;
            if (!fresh && DateTime.UtcNow - _challengeAt >= _reuseWindow) return false;
            challenge = _challenge;
            nc = ++_nc;
        }
        request.Headers.Authorization = challenge.Scheme == "Basic"
            ? new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")))
            : new AuthenticationHeaderValue("Digest", DigestParameters(challenge, request, nc));
        return true;
    }

    private void Store(Challenge challenge)
    {
        lock (_lock)
        {
            _challenge = challenge;
            _challengeAt = DateTime.UtcNow;
            _nc = 0;
        }
    }

    /// <summary>Este equipo cobra los desafíos vencidos: en esta conexión no se reutilizan más.</summary>
    private void DisableReuse()
    {
        lock (_lock) _reuseWindow = TimeSpan.Zero;
    }

    private void Shrink()
    {
        lock (_lock)
        {
            if (_reuseWindow <= TimeSpan.Zero) return;
            var age = DateTime.UtcNow - _challengeAt;
            var smaller = TimeSpan.FromTicks((long)(Math.Min(age.Ticks, _reuseWindow.Ticks) * 0.7));
            _reuseWindow = smaller < MinReuseWindow ? MinReuseWindow : smaller;
        }
    }

    private string DigestParameters(Challenge c, HttpRequestMessage request, long nc)
    {
        string uri = request.RequestUri!.PathAndQuery;
        string cnonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        string ncText = nc.ToString("x8");
        bool sha = c.Algorithm.StartsWith("SHA-256", StringComparison.OrdinalIgnoreCase);
        string H(string s) => sha ? Hex(SHA256.HashData(Encoding.UTF8.GetBytes(s))) : Hex(MD5.HashData(Encoding.UTF8.GetBytes(s)));

        string ha1 = H($"{username}:{c.Realm}:{password}");
        if (c.Algorithm.EndsWith("-sess", StringComparison.OrdinalIgnoreCase))
            ha1 = H($"{ha1}:{c.Nonce}:{cnonce}");
        string ha2 = H($"{request.Method.Method}:{uri}");
        bool qopAuth = c.Qop is not null &&
                       c.Qop.Split(',', StringSplitOptions.TrimEntries).Contains("auth", StringComparer.OrdinalIgnoreCase);
        string response = qopAuth
            ? H($"{ha1}:{c.Nonce}:{ncText}:{cnonce}:auth:{ha2}")
            : H($"{ha1}:{c.Nonce}:{ha2}");

        var sb = new StringBuilder();
        sb.Append($"username=\"{Quote(username)}\", realm=\"{Quote(c.Realm)}\", nonce=\"{c.Nonce}\", uri=\"{uri}\", ");
        sb.Append($"algorithm={c.Algorithm}, response=\"{response}\"");
        if (c.Opaque is not null) sb.Append($", opaque=\"{c.Opaque}\"");
        if (qopAuth) sb.Append($", qop=auth, nc={ncText}, cnonce=\"{cnonce}\"");
        return sb.ToString();

        static string Hex(byte[] hash) => Convert.ToHexStringLower(hash);
        static string Quote(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>
    /// El primer desafío que se sabe atender, en el orden en que los ofrece el
    /// equipo (el preferido primero): Digest MD5/SHA-256 (y sus -sess), o Basic.
    /// </summary>
    private static Challenge? ParseChallenge(HttpHeaderValueCollection<AuthenticationHeaderValue> headers)
    {
        foreach (var header in headers)
        {
            if (header.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
                return new Challenge("Basic", "", "", null, null, "");
            if (!header.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase) || header.Parameter is null) continue;
            var values = Parameters(header.Parameter);
            if (!values.TryGetValue("nonce", out string? nonce)) continue;
            string algorithm = values.GetValueOrDefault("algorithm") ?? "MD5";
            if (algorithm.ToUpperInvariant() is not ("MD5" or "MD5-SESS" or "SHA-256" or "SHA-256-SESS")) continue;
            return new Challenge("Digest", values.GetValueOrDefault("realm") ?? "", nonce,
                values.GetValueOrDefault("opaque"), values.GetValueOrDefault("qop"), algorithm);
        }
        return null;
    }

    [GeneratedRegexAttribute("""(\w+)\s*=\s*(?:"((?:[^"\\]|\\.)*)"|([^\s,]+))""")]
    private static partial Regex ParameterPattern();

    private static Dictionary<string, string> Parameters(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ParameterPattern().Matches(text))
            values[m.Groups[1].Value] = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        return values;
    }

    /// <summary>Un rechazo que el equipo cuenta para el bloqueo (lo dice en el cuerpo del 401).</summary>
    private static bool CountsAsFailure(string body) =>
        body.Contains("retryLoginTime", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("lockStatus", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegexAttribute(@"retryLoginTime\W{1,3}(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RetriesPattern();

    /// <summary>Intentos que le quedan a la cuenta según el 401 (XML o JSON); null si no lo dice.</summary>
    private static int? RetriesLeft(string body) =>
        RetriesPattern().Match(body) is { Success: true } m && int.TryParse(m.Groups[1].Value, out int n) ? n : null;

    /// <summary>Lee el cuerpo del 401 dejándolo disponible para quien reciba la respuesta.</summary>
    private static async Task<string> PeekAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.Content.LoadIntoBufferAsync(ct);
        return HikvisionIsapiClient.DecodeBody(await response.Content.ReadAsByteArrayAsync(ct));
    }

    /// <summary>La misma petición para repetirla (un HttpRequestMessage no se manda dos veces).</summary>
    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
            Content = request.Content,   // ya en memoria (LoadIntoBufferAsync)
        };
        foreach (var header in request.Headers)
            if (!header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options)
            ((IDictionary<string, object?>)copy.Options)[option.Key] = option.Value;
        return copy;
    }
}
