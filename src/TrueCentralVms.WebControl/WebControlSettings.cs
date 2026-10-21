using System.IO;
using System.Text.Json;

namespace TrueCentralVms.WebControl;

/// <summary>
/// Ajustes del web control (archivo opcional
/// <c>%APPDATA%\CLRTrueCentralVMS\web-control.json</c>). Los valores por defecto
/// alcanzan para una instalación normal: el panel web prueba estos mismos
/// puertos en orden hasta encontrar el control.
/// </summary>
public sealed class WebControlSettings
{
    /// <summary>Puertos candidatos, en orden. El primero libre es el que se usa.</summary>
    public int[] Ports { get; set; } = [5081, 25471, 25472];

    /// <summary>
    /// Orígenes web (panel de un servidor TrueCentral, p. ej. "https://vms.empresa.cl") autorizados a usar el agente.
    /// Aunque el agente escucha solo en 127.0.0.1, CUALQUIER página abierta en este equipo puede llamarlo desde el
    /// navegador: sin esta lista, un sitio cualquiera leería las huellas que se capturan. Un origen que no está aquí
    /// lo aprueba (o rechaza) el usuario la primera vez, en un aviso del complemento, y queda guardado. "*" = cualquiera
    /// (solo para pruebas).
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = [];

    /// <summary>Orígenes que el usuario rechazó en el aviso: no se vuelve a preguntar por ellos.</summary>
    public List<string> DeniedOrigins { get; set; } = [];

    /// <summary>
    /// Segundos de espera del dedo antes de abandonar la captura (máximo 60 que
    /// admite el SDK). Medido con el lector real: entre que el operador lee la
    /// instrucción, acomoda el dedo de la persona y completa las tres apoyadas
    /// pasa más de medio minuto, así que un valor corto corta capturas buenas.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 45;

    /// <summary>Veces que hay que apoyar el dedo (0 = por defecto del SDK, 2 a 4).</summary>
    public int CollectTimes { get; set; } = 3;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CLRTrueCentralVMS", "web-control.json");

    /// <summary>Guarda los ajustes (las aprobaciones de orígenes). Un error queda en la actividad, no corta la captura.</summary>
    public void Save(Action<string> log)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            log($"No se pudo guardar {FilePath}: {ex.Message}.");
        }
    }

    public static WebControlSettings Load(Action<string> log)
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<WebControlSettings>(File.ReadAllText(FilePath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip });
                if (loaded is not null)
                {
                    log($"Ajustes cargados de {FilePath}");
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            log($"No se pudo leer {FilePath}: {ex.Message}. Se usan los valores por defecto.");
        }
        return new WebControlSettings();
    }
}
