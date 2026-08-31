using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Almacén en disco de las fotos de los reconocimientos de patentes. En la
/// base solo queda la ruta relativa: una escena JPEG pesa cientos de kB y a
/// varias por minuto haría inmanejable el respaldo del clúster.
///
/// Por defecto vive junto al directorio de datos del PostgreSQL embebido
/// (instalado como servicio, eso es %ProgramData%\CLRTrueCentralVMS\anpr), así
/// que las imágenes viajan con los datos en respaldos y migraciones de
/// servidor. Se puede reubicar con <c>Anpr:ImageDirectory</c>.
/// </summary>
public sealed class AnprStore
{
    private readonly ILogger<AnprStore> _logger;

    public AnprStore(IConfiguration config, EmbeddedPostgres postgres, ILogger<AnprStore> logger)
    {
        _logger = logger;
        string? configured = config["Anpr:ImageDirectory"];
        RootDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetDirectoryName(postgres.DataDirectory) ?? postgres.DataDirectory, "anpr")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        Directory.CreateDirectory(RootDirectory);
    }

    public string RootDirectory { get; }

    /// <summary>
    /// Guarda un JPEG y devuelve su ruta RELATIVA (con "/" como separador,
    /// para que sea la misma en la base sin importar el sistema de archivos).
    /// null si no había imagen o si el disco la rechazó: un evento sin foto
    /// sigue siendo un reconocimiento válido.
    /// </summary>
    public string? Save(byte[]? image, DateTime capturedLocal, string suffix)
    {
        if (image is null || image.Length == 0) return null;
        try
        {
            string folder = $"{capturedLocal:yyyy}/{capturedLocal:MM}/{capturedLocal:dd}";
            Directory.CreateDirectory(Path.Combine(RootDirectory, folder.Replace('/', Path.DirectorySeparatorChar)));
            string name = $"{capturedLocal:HHmmssfff}-{Guid.NewGuid():N}-{suffix}.jpg";
            string relative = $"{folder}/{name}";
            File.WriteAllBytes(FullPath(relative)!, image);
            return relative;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo guardar la imagen '{Suffix}' de un reconocimiento de patente.", suffix);
            return null;
        }
    }

    /// <summary>Ruta absoluta de una imagen guardada, o null si la ruta relativa es inválida.</summary>
    public string? FullPath(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        // Defensa contra rutas trepadoras: lo que salga de la carpeta del
        // módulo no se sirve (la ruta viene de la base, pero el archivo se
        // entrega por HTTP y no vale la pena confiar).
        string full = Path.GetFullPath(Path.Combine(RootDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(RootDirectory, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>Borra las fotos de eventos purgados (los directorios vacíos se limpian aparte).</summary>
    public void Delete(IEnumerable<string?> relativePaths)
    {
        foreach (string? relative in relativePaths)
        {
            if (FullPath(relative) is not { } full) continue;
            try { File.Delete(full); }
            catch (Exception ex) { _logger.LogDebug(ex, "No se pudo borrar la imagen {Path}.", full); }
        }
    }

    /// <summary>Elimina los directorios de días ya vacíos tras una purga.</summary>
    public void PruneEmptyDirectories()
    {
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(RootDirectory, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudieron limpiar los directorios vacíos de imágenes ANPR.");
        }
    }
}
