using System.Formats.Asn1;
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
/// Por omisión es PROPIO y lo emite la CA PROPIA del servidor, que se genera la
/// primera vez. Esa CA se puede instalar como raíz de confianza en los equipos de
/// la organización (a mano o por directiva de grupo) y desde ahí los navegadores
/// y el cliente de escritorio validan el servidor sin avisos. Lleva restricción
/// de nombres: solo puede firmar para los nombres de ESTE servidor, localhost y
/// direcciones IP privadas, así que instalarla no habilita a nadie a suplantar
/// otros sitios aunque se robara su llave.
///
/// El certificado del servidor tiene todos los nombres por los que se lo llama
/// (nombre del equipo, FQDN, localhost, sus IPv4 y Streaming:PublicHost). Si
/// aparece un nombre o una IP nueva, se vuelve a emitir con la MISMA llave: los
/// clientes que fijaron la huella de la llave (SPKI) no tienen que volver a
/// confiar.
///
/// Un administrador puede cargar uno PROPIO DEL CLIENTE (.pfx de su CA o de una
/// CA pública) desde Sistema → Certificado HTTPS; ese manda mientras esté vigente.
///
/// Todo se guarda cifrado con la llave de datos (<see cref="CredentialProtector"/>)
/// en el directorio de datos, que solo leen SYSTEM y los administradores. Kestrel
/// toma <see cref="Current"/> en cada conexión: cambiarlo no exige reiniciar.
/// </summary>
public sealed class ServerCertificateManager
{
    private const string CaFile = "tcvms-https-ca.pfx.enc";
    private const string SelfSignedFile = "tcvms-https-self.pfx.enc";
    private const string CustomFile = "tcvms-https-custom.pfx.enc";
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string NameConstraintsOid = "2.5.29.30";
    // Sin EphemeralKeySet: SChannel (TLS de Windows) no acepta llaves efímeras en un servidor.
    private const X509KeyStorageFlags KeyFlags = X509KeyStorageFlags.Exportable;
    // 800 días: Apple no acepta certificados de servidor de más de 825 días ni siquiera con una CA instalada a mano.
    private static readonly TimeSpan ServerValidity = TimeSpan.FromDays(800);

    /// <summary>Redes que la CA puede firmar: privadas, CGNAT (VPN) y loopback.</summary>
    private static readonly (IPAddress Network, int Prefix)[] PermittedNetworks =
    [
        (IPAddress.Parse("10.0.0.0"), 8),
        (IPAddress.Parse("172.16.0.0"), 12),
        (IPAddress.Parse("192.168.0.0"), 16),
        (IPAddress.Parse("100.64.0.0"), 10),
        (IPAddress.Parse("127.0.0.0"), 8),
        (IPAddress.IPv6Loopback, 128),
    ];

    private readonly string _dataDir;
    private readonly CredentialProtector _protector;
    private readonly IConfiguration _config;
    private readonly Lock _sync = new();
    private X509Certificate2 _current = null!;
    private X509Certificate2? _selfSigned;
    private X509Certificate2 _ca = null!;
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

    /// <summary>La CA propia (solo la parte pública interesa afuera: para instalarla como raíz).</summary>
    public X509Certificate2 Authority { get { Initialize(); lock (_sync) return _ca; } }

    /// <summary>Nombres del equipo que la CA no puede firmar (IP pública, nombre nuevo): quedan fuera del certificado.</summary>
    public IReadOnlyList<string> ExcludedNames { get; private set; } = [];

    /// <summary>Nombres extra que deben ir en el certificado propio (la dirección pública del acceso desde Internet).</summary>
    public Func<IEnumerable<string>>? ExtraNames { get; set; }

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
    /// Agrega al propio los nombres que falten (IP nueva, cambio de nombre del equipo) o lo renueva si está por
    /// vencer. Devuelve los que se agregaron, o una lista vacía si no hizo falta.
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
            var permittedDns = PermittedDnsNames(_ca);
            var wanted = WantedNames();
            var names = wanted.Where(n => IsPermitted(n, permittedDns)).ToList();
            ExcludedNames = wanted.Except(names, StringComparer.OrdinalIgnoreCase).ToList();

            var missing = _selfSigned is null ? names : names.Except(SubjectNames(_selfSigned), StringComparer.OrdinalIgnoreCase).ToList();
            bool fromOurCa = _selfSigned is not null && _selfSigned.Issuer == _ca.Subject;
            if (fromOurCa && missing.Count == 0 && _selfSigned!.NotAfter > DateTime.Now.AddDays(30)) return [];

            using var key = _selfSigned?.GetRSAPrivateKey() is { } existing ? existing : RSA.Create(2048);
            var fresh = Issue(key, names, _ca);
            File.WriteAllBytes(Path.Combine(_dataDir, SelfSignedFile), _protector.ProtectBytes(fresh.Export(X509ContentType.Pfx)));
            bool wasCurrent = _selfSigned is null || ReferenceEquals(_current, _selfSigned);
            _selfSigned = fresh;
            if (wasCurrent) _current = fresh;
            return missing.Count > 0 ? missing : [fromOurCa ? "(renovado por vencimiento)" : "(emitido por la CA propia)"];
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
            cert = X509CertificateLoader.LoadPkcs12(pfx, password, KeyFlags);
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
        string caPath = Path.Combine(_dataDir, CaFile);
        if (File.Exists(caPath))
        {
            try { _ca = LoadProtected(caPath); }
            catch (Exception ex) { StartupWarning = $"La CA propia no se pudo leer ({ex.Message}); se generó otra: hay que volver a instalarla en los equipos."; }
        }
        if (_ca is null)
        {
            _ca = CreateAuthority(WantedNames().Where(n => !IPAddress.TryParse(n, out _)).ToList());
            File.WriteAllBytes(caPath, _protector.ProtectBytes(_ca.Export(X509ContentType.Pfx)));
        }

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
        X509CertificateLoader.LoadPkcs12(_protector.UnprotectBytes(File.ReadAllBytes(path)), null, KeyFlags);

    /// <summary>CA propia: 20 años, solo firma certificados, restringida a <paramref name="dnsNames"/> y redes privadas.</summary>
    private static X509Certificate2 CreateAuthority(IReadOnlyList<string> dnsNames)
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest($"CN=CLR TrueCentral CA ({Environment.MachineName}), O=CLR TrueCentral VMS", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(new X509Extension(NameConstraintsOid, EncodeNameConstraints(dnsNames), true));
        using var issued = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(20));
        return X509CertificateLoader.LoadPkcs12(issued.Export(X509ContentType.Pfx), null, KeyFlags);
    }

    private static X509Certificate2 Issue(RSA key, IReadOnlyList<string> names, X509Certificate2 ca)
    {
        var request = new CertificateRequest($"CN={Environment.MachineName}, O=CLR TrueCentral VMS", key,
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
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));

        var notBefore = DateTimeOffset.Now.AddDays(-1);
        var notAfter = DateTimeOffset.Now.Add(ServerValidity);
        if (notAfter > ca.NotAfter) notAfter = ca.NotAfter;
        byte[] serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // positivo
        using var issued = request.Create(ca, notBefore, notAfter, serial);
        using var withKey = issued.CopyWithPrivateKey(key);
        // Recargado desde PFX para que Kestrel (SChannel) pueda usar la llave en Windows.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, KeyFlags);
    }

    // NameConstraints ::= SEQUENCE { permittedSubtrees [0] GeneralSubtrees }
    // GeneralSubtree ::= SEQUENCE { base GeneralName }  — dNSName [2] IA5String / iPAddress [7] OCTET STRING (red + máscara)
    private static byte[] EncodeNameConstraints(IReadOnlyList<string> dnsNames)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            foreach (string name in dnsNames)
            {
                using (writer.PushSequence())
                    writer.WriteCharacterString(UniversalTagNumber.IA5String, name, new Asn1Tag(TagClass.ContextSpecific, 2));
            }
            foreach (var (network, prefix) in PermittedNetworks)
            {
                byte[] address = network.GetAddressBytes();
                byte[] mask = Mask(address.Length, prefix);
                using (writer.PushSequence())
                    writer.WriteOctetString([.. address, .. mask], new Asn1Tag(TagClass.ContextSpecific, 7));
            }
        }
        return writer.Encode();
    }

    /// <summary>Los dNSName permitidos por la CA (leídos de su restricción de nombres).</summary>
    private static List<string> PermittedDnsNames(X509Certificate2 ca)
    {
        var names = new List<string>();
        var extension = ca.Extensions[NameConstraintsOid];
        if (extension is null) return names;
        var outer = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
        if (!outer.HasData || !outer.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))) return names;
        var permitted = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        while (permitted.HasData)
        {
            var subtree = permitted.ReadSequence();
            var tag = subtree.PeekTag();
            if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 2)))
                names.Add(subtree.ReadCharacterString(UniversalTagNumber.IA5String, tag));
        }
        return names;
    }

    private static bool IsPermitted(string name, List<string> permittedDns)
    {
        if (IPAddress.TryParse(name, out var ip))
            return PermittedNetworks.Any(n => n.Network.AddressFamily == ip.AddressFamily && InNetwork(ip, n.Network, n.Prefix));
        return permittedDns.Any(d => name.Equals(d, StringComparison.OrdinalIgnoreCase)
                                     || name.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
    }

    private static bool InNetwork(IPAddress ip, IPAddress network, int prefix)
    {
        byte[] a = ip.GetAddressBytes(), n = network.GetAddressBytes(), m = Mask(a.Length, prefix);
        for (int i = 0; i < a.Length; i++)
            if ((a[i] & m[i]) != (n[i] & m[i])) return false;
        return true;
    }

    private static byte[] Mask(int length, int prefix)
    {
        byte[] mask = new byte[length];
        for (int i = 0; i < length; i++)
        {
            int bits = Math.Clamp(prefix - i * 8, 0, 8);
            mask[i] = (byte)(0xFF << (8 - bits));
        }
        return mask;
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
        foreach (string extra in ExtraNames?.Invoke() ?? []) if (extra.Trim().Length > 0) names.Add(extra.Trim());

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
