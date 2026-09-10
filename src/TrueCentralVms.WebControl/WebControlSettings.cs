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
    /// Orígenes web autorizados a usar el agente ("*" = cualquiera). El agente
    /// solo escucha en 127.0.0.1, así que el riesgo se limita a páginas
    /// abiertas en este mismo equipo; restrinja aquí si quiere fijar el panel.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = ["*"];

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
