using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrueCentralVms.Client.Services;

/// <summary>
/// Cuenta recordada en este equipo. La contraseña (si el usuario pidió
/// recordarla) se guarda cifrada con DPAPI: solo este usuario de Windows en
/// esta máquina puede descifrarla — el archivo de ajustes nunca contiene
/// contraseñas en texto plano.
/// </summary>
public sealed class SavedAccount
{
    public string ServerUrl { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>Contraseña cifrada (Base64 de DPAPI); null = no se guardó.</summary>
    public string? ProtectedPassword { get; set; }
    public DateTime LastLoginUtc { get; set; }

    public string DisplayLabel => $"{Username} — {ServerUrl.Replace("http://", "").Replace("https://", "")}";
}

/// <summary>Preferencias locales del cliente (%AppData%\CLRTrueCentralVMS\client.json).</summary>
public sealed class ClientSettings
{
    private const int MaxAccounts = 8;

    public string ServerUrl { get; set; } = "http://localhost:5090";
    public string Username { get; set; } = "";
    public bool RememberPassword { get; set; }
    public bool AutoLogin { get; set; }
    /// <summary>Última división de pantalla usada en Vista en Vivo (nombre del
    /// layout, ej. "4", "6", "13"); null = la por defecto.</summary>
    public string? LastLayout { get; set; }
    /// <summary>Últimos inicios de sesión, el más reciente primero.</summary>
    public List<SavedAccount> Accounts { get; set; } = [];

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CLRTrueCentralVMS", "client.json");

    public static ClientSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* preferencias corruptas: se parte de cero */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* mejor esfuerzo */ }
    }

    public SavedAccount? FindAccount(string serverUrl, string username) =>
        Accounts.FirstOrDefault(a =>
            a.ServerUrl.Equals(serverUrl, StringComparison.OrdinalIgnoreCase) &&
            a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Registra un inicio de sesión exitoso: actualiza o crea la cuenta, cifra
    /// la contraseña solo si se pidió recordarla y conserva las más recientes.
    /// </summary>
    public void RecordLogin(string serverUrl, string username, string? passwordToRemember)
    {
        var account = FindAccount(serverUrl, username);
        if (account is null)
        {
            account = new SavedAccount();
            Accounts.Add(account);
        }
        account.ServerUrl = serverUrl;
        account.Username = username;
        account.LastLoginUtc = DateTime.UtcNow;
        account.ProtectedPassword = passwordToRemember is null ? null : CredentialVault.Protect(passwordToRemember);

        Accounts = Accounts.OrderByDescending(a => a.LastLoginUtc).Take(MaxAccounts).ToList();
        ServerUrl = serverUrl;
        Username = username;
        Save();
    }

    public void RemoveAccount(SavedAccount account)
    {
        Accounts.Remove(account);
        Save();
    }
}

/// <summary>
/// Cifrado local de credenciales con DPAPI (CurrentUser): la llave la
/// administra Windows y está ligada al perfil del usuario, así que el blob no
/// sirve copiado a otra máquina ni a otro usuario del mismo equipo.
/// </summary>
internal static class CredentialVault
{
    // Entropía propia de la aplicación: un blob DPAPI de otro programa no es
    // intercambiable con el nuestro aunque corra bajo el mismo usuario.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CLRTrueCentralVMS.credential.v1");

    public static string? Protect(string plaintext)
    {
        try
        {
            byte[] blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(blob);
        }
        catch
        {
            return null; // sin DPAPI disponible es mejor no recordar que guardar en claro
        }
    }

    public static string? Unprotect(string? base64Blob)
    {
        if (string.IsNullOrEmpty(base64Blob)) return null;
        try
        {
            byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(base64Blob), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null; // blob de otra máquina/usuario o corrupto: se pide la clave de nuevo
        }
    }
}
