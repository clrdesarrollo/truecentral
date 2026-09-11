using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrueCentralVms.Migrator;

public sealed class TerminalSetting
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 80;
    public string Username { get; set; } = "admin";
    /// <summary>Cifrada con DPAPI del usuario; vacía si no se recuerda.</summary>
    public string PasswordProtected { get; set; } = "";
}

/// <summary>
/// Lo que el migrador recuerda entre ejecuciones, en el perfil del usuario.
/// Los secretos (secreto del socio, contraseñas) se guardan cifrados con
/// DPAPI —solo los lee el mismo usuario de Windows en el mismo equipo— y
/// solo si se marcó "recordar credenciales".
/// </summary>
public sealed class MigratorSettings
{
    public string HcpUrl { get; set; } = "";
    public string HcpAppKey { get; set; } = "";
    public string HcpAppSecretProtected { get; set; } = "";
    public bool ReadTerminals { get; set; } = true;
    public string TerminalUsername { get; set; } = "admin";
    public string TerminalPasswordProtected { get; set; } = "";
    public List<TerminalSetting> Terminals { get; set; } = [];
    public string TrueCentralUrl { get; set; } = "http://localhost:5080";
    public string TrueCentralUsername { get; set; } = "admin";
    public string TrueCentralPasswordProtected { get; set; } = "";
    /// <summary>0 = el que usan los terminales, 1 = código de persona de HCP, 2 = lo asigna TrueCentral.</summary>
    public int EmployeeNoSource { get; set; }
    /// <summary>Nivel de acceso a asignar al importar (null = ninguno).</summary>
    public int? TrueCentralLevelId { get; set; }
    public bool UpdateExisting { get; set; } = true;
    public bool DownloadPhotos { get; set; } = true;
    public bool ImportToTrueCentral { get; set; } = true;
    public string PackageFolder { get; set; } = "";
    public bool RememberSecrets { get; set; } = true;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CLRobotics", "TrueCentral", "migrador.json");

    public static MigratorSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<MigratorSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception) { /* archivo dañado: se arranca de cero */ }
        return new MigratorSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception) { /* sin permisos de escritura: no es motivo para frenar la migración */ }
    }

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException) { return ""; }
    }

    public static string Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception) { return ""; }   // otro usuario u otro equipo: se pide de nuevo
    }
}
