using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services.Licensing;

/// <summary>
/// Sobre del estado local del licenciamiento: AES-256-GCM (cifra Y autentica)
/// con una llave derivada (HKDF) del secreto compilado
/// <see cref="LicensingConstants.StateSealSecret"/> y del identificador de ESTE
/// equipo. Un sobre editado, truncado o traído de otro equipo no abre y se
/// trata como si no existiera. Formato: <c>tcv1:</c> + base64(nonce 12 | tag 16 | cifrado).
/// </summary>
public static class LicenseStateVault
{
    private const string Prefix = "tcv1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static byte[] Key(string purpose) => HKDF.DeriveKey(HashAlgorithmName.SHA256,
        Convert.FromBase64String(LicensingConstants.StateSealSecret), 32,
        salt: Encoding.UTF8.GetBytes(HardwareId.Value),
        info: Encoding.UTF8.GetBytes("tcvms-license-" + purpose));

    public static string Seal(byte[] plain, string purpose)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] tag = new byte[TagSize];
        byte[] cipher = new byte[plain.Length];
        using (var aes = new AesGcm(Key(purpose), TagSize))
            aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(purpose));
        return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>Contenido del sobre, o null si no es auténtico (editado, de otro equipo o de otro propósito).</summary>
    public static byte[]? Open(string? sealedText, string purpose)
    {
        if (sealedText is null || !sealedText.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            byte[] blob = Convert.FromBase64String(sealedText[Prefix.Length..]);
            if (blob.Length < NonceSize + TagSize) return null;
            byte[] plain = new byte[blob.Length - NonceSize - TagSize];
            using var aes = new AesGcm(Key(purpose), TagSize);
            aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize),
                plain, Encoding.UTF8.GetBytes(purpose));
            return plain;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>Estado local del licenciamiento (lo que no viene firmado en el .lic).</summary>
public sealed class PersistedLicenseState
{
    /// <summary>Contador de escrituras: entre copias válidas manda la más nueva.</summary>
    public long Revision { get; set; }
    public DateTime? TrialStartedUtc { get; set; }
    public string? ActivationCode { get; set; }
    public DateTime? LastValidatedUtc { get; set; }
    public DateTime? LastHeartbeatAttemptUtc { get; set; }
    public string? LastHeartbeatError { get; set; }
    public string? ServerRejectionStatus { get; set; }
    public string? ServerRejectionMessage { get; set; }
    public DateTime? ServerRejectionAtUtc { get; set; }
    public DateTime LastSeenClockUtc { get; set; }

    /// <summary>
    /// Fecha de generación (firmada) del .lic más nuevo que este equipo ha
    /// aceptado. No se acepta uno más antiguo: así no se puede volver a un
    /// archivo de antes de una revocación o con otro vencimiento.
    /// </summary>
    public DateTime? HighestLicenseGeneratedUtc { get; set; }

    /// <summary>
    /// Memoria de la última invalidación del servidor de licencias. A
    /// diferencia de <see cref="ServerRejectionStatus"/>, sobrevive a desactivar
    /// la licencia: el mismo archivo no se puede volver a importar, hace falta
    /// uno emitido después.
    /// </summary>
    public string? RejectedLicenseKey { get; set; }
    public DateTime? RejectedAtUtc { get; set; }
}

/// <summary>
/// Marca mínima que sobrevive a la desinstalación: cuándo empezó el período de
/// prueba en este equipo y la hora más alta vista. Reinstalar no regala otra prueba.
/// </summary>
public sealed class TrialMarker
{
    public DateTime? TrialStartedUtc { get; set; }
    public DateTime LastSeenClockUtc { get; set; }
}

/// <summary>
/// Guarda el estado del licenciamiento en TRES lugares, cada uno en un sobre de
/// <see cref="LicenseStateVault"/>: un archivo en la carpeta de licencia, el
/// registro de Windows (HKLM; HKCU si el proceso no puede escribir ahí, como al
/// correr en desarrollo) y una fila de la base de datos. Al leer se combinan las
/// copias válidas (ver <see cref="LicenseService"/>), así que borrar o editar
/// una no sirve: se reconstruye desde las otras. Aparte, la
/// <see cref="TrialMarker"/> va solo al registro y el desinstalador la conserva.
/// </summary>
public sealed class LicenseStateStore
{
    public const string RegistryPath = @"SOFTWARE\CLRobotics\TrueCentralVMS\Runtime";
    /// <summary>
    /// La marca de prueba es UNA por equipo (es lo que se quiere: una sola
    /// prueba por máquina). El estado, en cambio, es de cada instalación: en un
    /// mismo PC pueden convivir varias (desarrollo, demo, un servidor de prueba
    /// aislado) y ninguna debe tomar el estado de otra.
    /// </summary>
    private const string MarkerValue = "Profile";
    private readonly string _stateValue;
    private const string StateFileName = "state.dat";
    private const string DbBlobName = "license-state";
    private const string StatePurpose = "state";
    private const string MarkerPurpose = "marker";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _directory;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger _logger;

    public LicenseStateStore(string directory, IServiceScopeFactory scopes, ILogger logger)
    {
        _directory = directory;
        _scopes = scopes;
        _logger = logger;
        // "Data-" + huella de la carpeta de datos de esta instalación.
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).TrimEnd('\\', '/').ToLowerInvariant()));
        _stateValue = "Data-" + Convert.ToHexString(hash)[..8];
    }

    /// <summary>Copias válidas del estado, con el lugar de donde salió cada una (para el registro).</summary>
    public List<(string Source, PersistedLicenseState State)> ReadStates()
    {
        var copies = new List<(string, PersistedLicenseState)>();
        void Add(string source, string? sealedText)
        {
            if (sealedText is null) return;
            if (Unseal<PersistedLicenseState>(sealedText, StatePurpose) is { } state) copies.Add((source, state));
            else _logger.LogWarning("Copia del estado de licencia descartada ({Source}): no es auténtica.", source);
        }
        Add("archivo", ReadFile());
        Add("registro (HKLM)", ReadRegistry(RegistryHive.LocalMachine, _stateValue));
        Add("registro (HKCU)", ReadRegistry(RegistryHive.CurrentUser, _stateValue));
        Add("base de datos", ReadDb());
        return copies;
    }

    public TrialMarker? ReadMarker()
    {
        // Gana la más restrictiva de HKLM y HKCU (la de desarrollo puede estar en cualquiera).
        TrialMarker? result = null;
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            if (Unseal<TrialMarker>(ReadRegistry(hive, MarkerValue), MarkerPurpose) is not { } m) continue;
            result ??= new TrialMarker();
            result.TrialStartedUtc = Earliest(result.TrialStartedUtc, m.TrialStartedUtc);
            if (m.LastSeenClockUtc > result.LastSeenClockUtc) result.LastSeenClockUtc = m.LastSeenClockUtc;
        }
        return result;
    }

    /// <summary>Escribe el estado en los tres lugares y la marca de prueba; un lugar que falla no frena a los demás.</summary>
    public void Write(PersistedLicenseState state)
    {
        string sealedState = Seal(state, StatePurpose);
        Try("archivo", () =>
        {
            string path = Path.Combine(_directory, StateFileName);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sealedState);
            File.Move(tmp, path, overwrite: true);
        });
        WriteRegistry(_stateValue, sealedState);
        Try("base de datos", () => WriteDb(sealedState));
        WriteRegistry(MarkerValue, Seal(new TrialMarker
        {
            TrialStartedUtc = state.TrialStartedUtc,
            LastSeenClockUtc = state.LastSeenClockUtc,
        }, MarkerPurpose));
    }

    /// <summary>
    /// Estado del formato anterior (state.json en claro), solo para la primera
    /// vez después de actualizar. Se borra al migrar.
    /// </summary>
    public PersistedLicenseState? ReadLegacy()
    {
        string path = Path.Combine(_directory, "state.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<PersistedLicenseState>(File.ReadAllText(path), Json); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer el estado de licencia anterior (state.json).");
            return null;
        }
    }

    public void DeleteLegacy()
    {
        try { File.Delete(Path.Combine(_directory, "state.json")); }
        catch (IOException) { /* se reintenta en la próxima escritura */ }
    }

    /// <summary>
    /// Pista en claro para el desinstalador (qué código está activado, para
    /// avisar antes de borrar). No la lee el servidor: no da ni quita nada.
    /// </summary>
    public void WriteInstallerHint(string? activationCode) => Try("pista del instalador", () =>
    {
        string path = Path.Combine(_directory, "activation.json");
        if (activationCode is null) File.Delete(path);
        // Indentado: el instalador lo lee línea por línea (JsonStringValue de suite.iss).
        else File.WriteAllText(path, JsonSerializer.Serialize(new { activationCode }, new JsonSerializerOptions(Json) { WriteIndented = true }));
    });

    // ------------------------------------------------------------------

    private static string Seal<T>(T value, string purpose) =>
        LicenseStateVault.Seal(JsonSerializer.SerializeToUtf8Bytes(value, Json), purpose);

    private static T? Unseal<T>(string? sealedText, string purpose) where T : class
    {
        if (LicenseStateVault.Open(sealedText, purpose) is not { } plain) return null;
        try { return JsonSerializer.Deserialize<T>(plain, Json); }
        catch (JsonException) { return null; }
    }

    private string? ReadFile()
    {
        string path = Path.Combine(_directory, StateFileName);
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (IOException) { return null; }
    }

    private static string? ReadRegistry(RegistryHive hive, string value)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(RegistryPath);
            return key?.GetValue(value) as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    /// <summary>HKLM (el servicio corre como LocalSystem); si no se puede, HKCU.</summary>
    private void WriteRegistry(string value, string data)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.CreateSubKey(RegistryPath, writable: true);
                key.SetValue(value, data, RegistryValueKind.String);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // sin permiso en HKLM (desarrollo): se prueba HKCU
            }
        }
        _logger.LogWarning("No se pudo guardar el estado de licencia en el registro.");
    }

    private string? ReadDb()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            return db.SystemBlobs.Where(b => b.Name == DbBlobName).Select(b => b.Data).FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo leer la copia del estado de licencia en la base de datos.");
            return null;
        }
    }

    private void WriteDb(string data)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var row = db.SystemBlobs.FirstOrDefault(b => b.Name == DbBlobName);
        if (row is null) db.SystemBlobs.Add(row = new SystemBlob { Name = DbBlobName });
        row.Data = data;
        row.UpdatedAt = DateTime.UtcNow;
        db.SaveChanges();
    }

    private void Try(string where, Action action)
    {
        try { action(); }
        catch (Exception ex) { _logger.LogWarning(ex, "No se pudo guardar el estado de licencia ({Where}).", where); }
    }

    public static DateTime? Earliest(DateTime? a, DateTime? b) =>
        a is null ? b : b is null ? a : (a < b ? a : b);
}
