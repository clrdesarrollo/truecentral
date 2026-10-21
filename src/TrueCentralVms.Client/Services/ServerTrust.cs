using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace TrueCentralVms.Client.Services;

/// <summary>
/// Confianza en el certificado HTTPS de cada servidor.
///
/// Un certificado que Windows ya reconoce (emitido por la CA del cliente o una
/// pública) se acepta sin más. El propio del servidor (autofirmado) no lo
/// reconoce nadie: la primera vez el operador compara la huella con la de
/// Sistema → Certificado HTTPS y decide confiar; desde ahí este equipo exige
/// esa misma llave (huella SHA-256 de la llave pública) para esa dirección. Si
/// la llave cambia, se vuelve a preguntar con un aviso fuerte: puede ser un
/// certificado nuevo... o alguien en el medio.
///
/// Las huellas viven en %AppData%\CLRTrueCentralVMS\trusted-servers.json (aparte
/// de client.json para que ninguna copia vieja de los ajustes las pise).
/// </summary>
public static class ServerTrust
{
    /// <summary>Certificado que se rechazó por no conocerse (lo que se le muestra al operador).</summary>
    public sealed record Untrusted(string Authority, string Spki, string Subject, DateTime NotAfter, bool KeyChanged);

    private static readonly Lock Sync = new();
    private static Dictionary<string, string>? _pins;
    private static Untrusted? _lastRejected;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CLRTrueCentralVMS", "trusted-servers.json");

    /// <summary>
    /// Carpeta que deja el instalador descargado desde el panel (solo la escriben los administradores): la huella de
    /// la llave del servidor del que se bajó (trusted-keys.txt, una por línea) y su dirección (server-url.txt).
    /// </summary>
    public static string MachineDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CLRTrueCentralVMS-Client");

    private static HashSet<string>? _machineKeys;

    /// <summary>"host:puerto" de una URL (la confianza es por dirección).</summary>
    public static string Authority(Uri uri) => $"{uri.IdnHost}:{uri.Port}".ToLowerInvariant();

    /// <summary>Huella SHA-256 de la llave pública (la misma que muestra el servidor).</summary>
    public static string SpkiFingerprint(X509Certificate2 cert) =>
        Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

    /// <summary>Huella en grupos de dos (AB:CD:…), como en el panel web.</summary>
    public static string Format(string hex) =>
        string.Join(":", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2).ToUpperInvariant()));

    /// <summary>¿Se acepta este certificado para <paramref name="uri"/>?</summary>
    public static bool Validate(Uri? uri, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (uri is null || certificate is null) return false;
        using var cert = new X509Certificate2(certificate);
        string authority = Authority(uri);
        string spki = SpkiFingerprint(cert);
        lock (Sync)
        {
            // Llave que dejó el instalador (vale para cualquier nombre del servidor: la llave es solo suya).
            if (MachineKeys().Contains(spki)) return true;
            var pins = Pins();
            if (pins.TryGetValue(authority, out string? pinned) && pinned == spki) return true;
            _lastRejected = new Untrusted(authority, spki, cert.Subject, cert.NotAfter, pins.ContainsKey(authority));
        }
        return false;
    }

    /// <summary>El último rechazo para la dirección de <paramref name="serverUrl"/> (y lo olvida).</summary>
    public static Untrusted? TakeRejected(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)) return null;
        lock (Sync)
        {
            var rejected = _lastRejected;
            if (rejected is null || rejected.Authority != Authority(uri)) return null;
            _lastRejected = null;
            return rejected;
        }
    }

    /// <summary>¿Este equipo ya confió en una llave para esa dirección? (entonces se la conoce por https)</summary>
    public static bool IsPinned(Uri uri)
    {
        lock (Sync) return Pins().ContainsKey(Authority(uri));
    }

    /// <summary>Desde ahora se acepta esa llave para esa dirección.</summary>
    public static void Trust(Untrusted certificate)
    {
        lock (Sync)
        {
            var pins = Pins();
            pins[certificate.Authority] = certificate.Spki;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(pins, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* mejor esfuerzo: vale para esta ejecución */ }
        }
    }

    /// <summary>Manejador HTTP que valida con <see cref="Validate"/>.</summary>
    public static HttpClientHandler CreateHandler() => Configure(new HttpClientHandler());

    /// <summary>Aplica la validación a un manejador ya creado (el de SignalR).</summary>
    public static HttpClientHandler Configure(HttpClientHandler handler)
    {
        handler.ServerCertificateCustomValidationCallback = (request, cert, _, errors) =>
            Validate(request.RequestUri, cert, errors);
        return handler;
    }

    /// <summary>Validación para un WebSocket hacia <paramref name="uri"/>.</summary>
    public static RemoteCertificateValidationCallback ForWebSocket(Uri uri) =>
        (_, cert, _, errors) => Validate(uri, cert, errors);

    private static HashSet<string> MachineKeys()
    {
        if (_machineKeys is not null) return _machineKeys;
        _machineKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = Path.Combine(MachineDirectory, "trusted-keys.txt");
            if (File.Exists(path))
                foreach (string line in File.ReadAllLines(path))
                    if (line.Trim() is { Length: 64 } key) _machineKeys.Add(key);
        }
        catch { /* sin acceso: se pregunta como siempre */ }
        return _machineKeys;
    }

    private static Dictionary<string, string> Pins()
    {
        if (_pins is not null) return _pins;
        try
        {
            if (File.Exists(FilePath))
                _pins = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath));
        }
        catch { /* archivo dañado: se vuelve a preguntar */ }
        return _pins ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
