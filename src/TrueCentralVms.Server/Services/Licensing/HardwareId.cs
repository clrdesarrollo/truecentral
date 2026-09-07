using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace TrueCentralVms.Server.Services.Licensing;

/// <summary>
/// Identificador estable del equipo al que se vincula la licencia. Se deriva
/// (SHA-256) del MachineGuid de Windows más los datos de placa/BIOS que el
/// sistema expone en el registro: sobrevive a cambios de nombre de máquina,
/// de IP o de tarjeta de red, y cambia si se reinstala Windows o se migra a
/// otro hardware (en ese caso se libera el cupo en el servidor de licencias
/// y se activa de nuevo). Nunca se envía el MachineGuid en claro.
/// </summary>
public static class HardwareId
{
    private static readonly Lazy<string> Cached = new(Compute);

    /// <summary>Formato XXXXX-XXXXX-XXXXX-XXXXX (hex, 20 caracteres).</summary>
    public static string Value => Cached.Value;

    private static string Compute()
    {
        var parts = new List<string> { "tcvms-v1" };
        if (OperatingSystem.IsWindows())
        {
            parts.Add(ReadRegistry(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid", RegistryView.Registry64));
            parts.Add(ReadRegistry(@"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardManufacturer", RegistryView.Default));
            parts.Add(ReadRegistry(@"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardProduct", RegistryView.Default));
            parts.Add(ReadRegistry(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer", RegistryView.Default));
            parts.Add(ReadRegistry(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName", RegistryView.Default));
        }
        else
        {
            try { parts.Add(File.ReadAllText("/etc/machine-id").Trim()); }
            catch { parts.Add(Environment.MachineName); }
        }

        // Si nada del hardware fue legible (virtualización rara, permisos), el
        // nombre de máquina evita un identificador vacío igual para todos.
        if (parts.Count(p => p.Length > 0) <= 1)
            parts.Add(Environment.MachineName);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)));
        string hex = Convert.ToHexString(hash)[..20];
        return string.Join('-', Enumerable.Range(0, 4).Select(i => hex.Substring(i * 5, 5)));
    }

    private static string ReadRegistry(string path, string name, RegistryView view)
    {
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.OpenSubKey(path);
            return key?.GetValue(name)?.ToString()?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }
}
