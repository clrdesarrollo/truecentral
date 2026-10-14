using System.Text.Json;
using TrueCentralVms.AlarmScreen.Models;

namespace TrueCentralVms.AlarmScreen.Services;

/// <summary>Carga y guarda <see cref="ClientConfig"/>. En Linux queda en <c>~/.config/TrueCentralPantalla</c>.</summary>
public static class ConfigService
{
    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrueCentralPantalla");

    private static readonly string FilePath = Path.Combine(AppDataDir, "config.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static ClientConfig Load()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(AppDataDir);
            ClientConfig config;
            try
            {
                config = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(FilePath)) ?? new ClientConfig()
                    : new ClientConfig();
            }
            catch
            {
                // config corrupta: mejor partir de cero que no arrancar
                config = new ClientConfig();
            }

            if (string.IsNullOrWhiteSpace(config.MachineId))
            {
                config.MachineId = Guid.NewGuid().ToString("N");
                SaveCore(config);
            }
            return config;
        }
    }

    public static void Save(ClientConfig config)
    {
        lock (Gate) SaveCore(config);
    }

    private static void SaveCore(ClientConfig config)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(config, Json));
        }
        catch
        {
            // disco lleno o solo lectura: la caseta debe seguir mostrando alarmas igual
        }
    }
}
