using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services.Https;

/// <summary>
/// Certificado con que el servidor atiende HTTPS.
///
/// Por omisión es PROPIO: el servidor genera una llave RSA la primera vez y un
/// certificado autofirmado con todos los nombres por los que se lo puede llamar
/// (nombre del equipo, FQDN, localhost, sus IP y Streaming:PublicHost). Si
/// aparece un nombre o una IP nueva, se vuelve a emitir con la MISMA llave: los
/// clientes fijan la huella de la llave pública (SPKI), así que no tienen que
/// volver a confiar.
///
/// Un administrador puede cargar uno PROPIO DEL CLIENTE (.pfx de su CA o de una
/// CA pública) desde Sistema → Certificado HTTPS; ese manda mientras esté vigente.
///
/// Ambos se guardan cifrados con la llave de datos (<see cref="CredentialProtector"/>)
/// en el directorio de datos, que solo leen SYSTEM y los administradores. Kestrel
/// toma <see cref="Current"/> en cada conexión: cambiarlo no exige reiniciar.
/// </summary>
public sealed class ServerCertificateManager
{
    private const string SelfSignedFile = "tcvms-https-self.pfx.enc";
    private const string CustomFile = "tcvms-https-custom.pfx.enc";
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    // Sin EphemeralKeySet: SChannel (TLS de Windows) no acepta llaves efímeras en un servidor.
    private const X509KeyStorageFlags KeyFlags = X509KeyStorageFlags.Exportable;

    private readonly string _dataDir;
    private readonly CredentialProtector _protector;
    private readonly IConfiguration _config;
    private readonly Lock _sync = new();
    private X509Certificate2 _current = null!;
    private X509Certificate2? _selfSigned;
    private bool _loaded;

    public ServerCertificateManager(EmbeddedPostgres postgres, CredentialProtector protector, IConfiguration config)
    {
        _dataDir = postgres.DataDirectory;
        _protector = protector;
        _config = config;
    }

    /// <summary>
    /// Lee o genera los certificados. Va DESPUÉS de iniciar PostgreSQL: en una instalación nueva el directorio de
    /// datos tiene que estar vacío para initdb, y los archivos del certificado (y la llave de datos) viven ahí.
    /// </summary>
    public void Initialize()
    {
        lock (_sync)
        {
            if (_loaded) return;
            Load();
            _loaded = true;
        }
    }

    /// <summary>El certificado que se presenta ahora.</summary>
    public X509Certificate2 Current { get { Initialize(); lock (_sync) return _current; } }

    /// <summary>¿Es uno cargado por el cliente (y no el propio)?</summary>
    public bool IsCustom { get { Initialize(); lock (_sync) return !ReferenceEquals(_current, _selfSigned); } }

    /// <summary>Lo que se dejó en el log al arrancar (si el cargado venció, etc.).</summary>
    public string? StartupWarning { get; private set; }

    /// <summary>Huella SHA-256 de la llave pública (lo que fijan el cliente, las pantallas y los agentes).</summary>
    public static string SpkiFingerprint(X509Certificate2 cert) =>
        Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

    /// <summary>Huella SHA-256 del certificado completo (la que muestran los navegadores).</summary>
    public static string CertificateFingerprint(X509Certificate2 cert) =>
        Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant();

    /// <summary>Nombres (DNS e IP) del certificado.</summary>
    public static IReadOnlyList<string> SubjectNames(X509Certificate2 cert)
    {
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is null) return [cert.GetNameInfo(X509NameType.DnsName, false)];
        return [.. san.EnumerateDnsNames(), .. san.EnumerateIPAddresses().Select(ip => ip.ToString())];
    }

    /// <summary>
    /// Agrega al propio los nombres que falten (IP nueva, cambio de nombre del equipo). Devuelve los que se
    /// agregaron, o una lista vacía si no hizo falta.
    /// </summary>
    public IReadOnlyList<string> RefreshSelfSigned()
    {
        Initialize();
        return Reissue();
    }

    private IReadOnlyList<string> Reissue()
    {
        lock (_sync)
        {
            var wanted = WantedNames();
            var missing = _selfSigned is null ? wanted : wanted.Except(SubjectNames(_selfSigned), StringComparer.OrdinalIgnoreCase).ToList();
            if (_selfSigned is not null && missing.Count == 0 && _selfSigned.NotAfter > DateTime.Now.AddDays(30)) return [];

            using var key = _selfSigned?.GetRSAPrivateKey() is { } existing ? existing : RSA.Create(2048);
            var fresh = Issue(key, wanted);
            File.WriteAllBytes(Path.Combine(_dataDir, SelfSignedFile), _protector.ProtectBytes(fresh.Export(X509ContentType.Pfx)));
            bool wasCurrent = _selfSigned is null || ReferenceEquals(_current, _selfSigned);
            _selfSigned = fresh;
            if (wasCurrent) _current = fresh;
            return missing.Count > 0 ? missing : ["(renovado por vencimiento)"];
        }
    }

    /// <summary>
    /// Valida y deja en uso un .pfx del cliente. Lanza <see cref="InvalidOperationException"/> con el motivo en
    /// palabras del operador si no sirve.
    /// </summary>
    public X509Certificate2 InstallCustom(byte[] pfx, string? password)
    {
        X509Certificate2 cert;
        try
        {
            cert = X509CertificateLoader.LoadPkcs12(pfx, password,
                KeyFlags);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("No se pudo abrir el archivo: no es un .pfx/.p12 válido o la contraseña no es la correcta.");
        }
        if (!cert.HasPrivateKey)
            throw new InvalidOperationException("El archivo no trae la llave privada del certificado (exporte el .pfx CON la llave privada).");
        if (cert.NotAfter <= DateTime.Now)
            throw new InvalidOperationException($"El certificado venció el {cert.NotAfter:dd-MM-yyyy}.");
        if (cert.NotBefore > DateTime.Now)
            throw new InvalidOperationException($"El certificado recién es válido desde el {cert.NotBefore:dd-MM-yyyy}.");
        var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (eku is not null && !eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == ServerAuthOid))
            throw new InvalidOperationException("El certificado no sirve para un servidor web (le falta el uso «Autenticación del servidor»).");

        Initialize();
        lock (_sync)
        {
            // Se guarda el .pfx exportado (sin contraseña: lo protege el cifrado con la llave de datos).
            File.WriteAllBytes(Path.Combine(_dataDir, CustomFile), _protector.ProtectBytes(cert.Export(X509ContentType.Pfx)));
            _current = cert;
        }
        return cert;
    }

    /// <summary>Quita el certificado del cliente y vuelve al propio.</summary>
    public void RemoveCustom()
    {
        Initialize();
        lock (_sync)
        {
            File.Delete(Path.Combine(_dataDir, CustomFile));
            _current = _selfSigned!;
        }
    }

    private void Load()
    {
        string selfPath = Path.Combine(_dataDir, SelfSignedFile);
        if (File.Exists(selfPath))
        {
            try { _selfSigned = LoadProtected(selfPath); }
            catch (Exception ex) { StartupWarning = $"El certificado propio no se pudo leer ({ex.Message}); se generó otro: los clientes deberán volver a confiar en el servidor."; }
        }
        _current = _selfSigned!;
        Reissue();

        string customPath = Path.Combine(_dataDir, CustomFile);
        if (!File.Exists(customPath)) return;
        try
        {
            var custom = LoadProtected(customPath);
            if (custom.NotAfter <= DateTime.Now)
                StartupWarning = $"El certificado cargado venció el {custom.NotAfter:dd-MM-yyyy}: se usa el propio del servidor hasta que se cargue uno vigente.";
            else
                _current = custom;
        }
        catch (Exception ex)
        {
            StartupWarning = $"El certificado cargado no se pudo leer ({ex.Message}): se usa el propio del servidor.";
        }
    }

    private X509Certificate2 LoadProtected(string path) =>
        X509CertificateLoader.LoadPkcs12(_protector.UnprotectBytes(File.ReadAllBytes(path)), null,
            KeyFlags);

    private static X509Certificate2 Issue(RSA key, IReadOnlyList<string> names)
    {
        string host = Environment.MachineName;
        var request = new CertificateRequest($"CN={host}, O=CLR TrueCentral VMS", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (string name in names)
        {
            if (IPAddress.TryParse(name, out var ip)) san.AddIpAddress(ip);
            else san.AddDnsName(name);
        }
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthOid)], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        // Cinco años: es un certificado privado (los navegadores solo acotan los de CA públicas).
        using var issued = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
        // Recargado desde PFX para que Kestrel (SChannel) pueda usar la llave en Windows.
        return X509CertificateLoader.LoadPkcs12(issued.Export(X509ContentType.Pfx), null,
            KeyFlags);
    }

    /// <summary>Nombres por los que hoy se puede llamar al servidor.</summary>
    private List<string> WantedNames()
    {
        var names = new List<string> { "localhost", Environment.MachineName };
        try
        {
            string fqdn = Dns.GetHostEntry(Environment.MachineName).HostName;
            if (fqdn.Contains('.')) names.Add(fqdn);
        }
        catch (SocketException) { /* sin DNS: alcanza con el nombre corto */ }
        if (_config["Streaming:PublicHost"] is { Length: > 0 } publicHost) names.Add(publicHost.Trim());

        // Solo IPv4 (y el loopback IPv6): las IPv6 globales cambian solas (direcciones temporales) y nadie
        // escribe una IPv6 en la barra del navegador.
        names.Add(IPAddress.Loopback.ToString());
        names.Add(IPAddress.IPv6Loopback.ToString());
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var address in nic.GetIPProperties().UnicastAddresses.Select(u => u.Address))
            {
                if (address.AddressFamily == AddressFamily.InterNetwork) names.Add(address.ToString());
            }
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
