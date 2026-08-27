using System.IO;
using System.Text.Json;

namespace TrueCentralVms.Client.Services;

/// <summary>Preferencias locales del cliente (%AppData%\CLRTrueCentralVMS\client.json).</summary>
public sealed class ClientSettings
{
    public string ServerUrl { get; set; } = "http://localhost:5090";
    public string Username { get; set; } = "";

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
}
