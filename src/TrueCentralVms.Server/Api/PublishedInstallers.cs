namespace TrueCentralVms.Server.Api;

/// <summary>
/// Instaladores que este servidor publica para descargarlos desde el panel
/// web: el del cliente de escritorio (la suite lo deja en <c>client-setup</c>)
/// y el del complemento de enrolamiento (en <c>webcontrol</c>).
/// </summary>
public static class PublishedInstallers
{
    /// <summary>
    /// El instalador más reciente que calza con <paramref name="pattern"/> en la
    /// carpeta de <paramref name="configKey"/> (por omisión, <paramref name="defaultDirectory"/>
    /// junto al servidor), o null si no hay ninguno.
    /// </summary>
    public static FileInfo? Find(IConfiguration config, IWebHostEnvironment env, string configKey,
        string defaultDirectory, string pattern)
    {
        string configured = config[configKey] ?? defaultDirectory;
        string directory = Path.IsPathRooted(configured)
            ? Environment.ExpandEnvironmentVariables(configured)
            : Path.Combine(env.ContentRootPath, Environment.ExpandEnvironmentVariables(configured));
        if (!Directory.Exists(directory)) return null;
        return new DirectoryInfo(directory)
            .GetFiles(pattern)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>Lo que el panel muestra antes de descargar: si hay, nombre, tamaño y versión.</summary>
    public static object Describe(FileInfo? file) => new
    {
        available = file is not null,
        fileName = file?.Name,
        sizeMb = file is null ? (double?)null : Math.Round(file.Length / 1024d / 1024d, 1),
        // La versión va en el nombre del instalador (…-Setup-1.2.3.exe).
        version = file is null ? null : Path.GetFileNameWithoutExtension(file.Name).Split('-').LastOrDefault(),
    };
}
