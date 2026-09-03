using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace TrueCentralVms.Core.Drivers;

/// <summary>
/// Autenticación HTTP Digest/Basic hecha a mano. El manejador de .NET sabe
/// resolver el desafío solo, pero para hacerlo REENVÍA la petición: con un
/// cuerpo que se transmite en vivo (el audio hacia el parlante) eso no es
/// posible. Guardando el desafío de una petición previa, la que lleva el audio
/// sale ya autenticada al primer intento.
/// </summary>
public sealed class DigestAuthenticator(string username, string password)
{
    private string? _realm;
    private string? _nonce;
    private string? _qop;
    private string? _opaque;
    private string? _algorithm;
    private int _counter;

    /// <summary>El equipo pidió Basic en vez de Digest.</summary>
    public bool IsBasic { get; private set; }

    /// <summary>Ya se conoce el desafío: las peticiones siguientes pueden ir firmadas de entrada.</summary>
    public bool Ready => _nonce is not null || IsBasic;

    /// <summary>Lee la cabecera WWW-Authenticate de una respuesta 401.</summary>
    public bool ReadChallenge(HttpResponseMessage response)
    {
        foreach (var header in response.Headers.WwwAuthenticate)
        {
            if (header.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
            {
                IsBasic = true;
                continue;   // se prefiere Digest si el equipo ofrece ambos
            }
            if (!header.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase) || header.Parameter is null)
                continue;

            IsBasic = false;
            _realm = Parameter(header.Parameter, "realm");
            _nonce = Parameter(header.Parameter, "nonce");
            _opaque = Parameter(header.Parameter, "opaque");
            _algorithm = Parameter(header.Parameter, "algorithm");
            string? qop = Parameter(header.Parameter, "qop");
            // qop puede venir como "auth,auth-int": solo se soporta "auth".
            _qop = qop?.Split(',').Select(q => q.Trim()).FirstOrDefault(q => q.Equals("auth", StringComparison.OrdinalIgnoreCase));
            _counter = 0;
            return true;
        }
        return IsBasic;
    }

    /// <summary>Cabecera Authorization para una petición, o null si aún no hay desafío.</summary>
    public AuthenticationHeaderValue? Build(string method, Uri uri)
    {
        if (IsBasic)
            return new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        if (_nonce is null || _realm is null) return null;

        string path = uri.PathAndQuery;
        string ha1 = Md5($"{username}:{_realm}:{password}");
        string ha2 = Md5($"{method}:{path}");
        string response;
        var parameters = new StringBuilder(
            $"username=\"{username}\", realm=\"{_realm}\", nonce=\"{_nonce}\", uri=\"{path}\"");

        if (_qop is not null)
        {
            string nc = (++_counter).ToString("x8");
            string cnonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            response = Md5($"{ha1}:{_nonce}:{nc}:{cnonce}:{_qop}:{ha2}");
            parameters.Append($", qop={_qop}, nc={nc}, cnonce=\"{cnonce}\"");
        }
        else
        {
            response = Md5($"{ha1}:{_nonce}:{ha2}");
        }
        parameters.Append($", response=\"{response}\"");
        if (_algorithm is { Length: > 0 }) parameters.Append($", algorithm={_algorithm}");
        if (_opaque is { Length: > 0 }) parameters.Append($", opaque=\"{_opaque}\"");
        return new AuthenticationHeaderValue("Digest", parameters.ToString());
    }

    private static string Md5(string value) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Valor de un parámetro del desafío (entre comillas o sin ellas).</summary>
    private static string? Parameter(string source, string name)
    {
        int start = source.IndexOf(name + "=", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += name.Length + 1;
        if (start >= source.Length) return null;
        if (source[start] == '"')
        {
            int end = source.IndexOf('"', start + 1);
            return end < 0 ? null : source[(start + 1)..end];
        }
        int comma = source.IndexOf(',', start);
        return (comma < 0 ? source[start..] : source[start..comma]).Trim();
    }
}
