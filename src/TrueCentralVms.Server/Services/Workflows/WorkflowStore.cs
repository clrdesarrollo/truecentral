using System.Diagnostics;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services.Workflows;

/// <summary>
/// Almacén en disco del módulo Automatizaciones: las fotos que capturan las
/// acciones (la base solo guarda la ruta relativa, igual que en ANPR) y los
/// sonidos que se reproducen en los parlantes IP.
///
/// Vive junto al directorio de datos de PostgreSQL para que viaje con él en
/// respaldos y migraciones de servidor; se puede reubicar con
/// <c>Workflows:FileDirectory</c>. Las fotos se purgan por retención; los
/// sonidos NO (son configuración, no historial).
/// </summary>
public sealed class WorkflowStore
{
    private readonly ILogger<WorkflowStore> _logger;

    public WorkflowStore(IConfiguration config, EmbeddedPostgres postgres, ILogger<WorkflowStore> logger)
    {
        _logger = logger;
        string? configured = config["Workflows:FileDirectory"];
        RootDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetDirectoryName(postgres.DataDirectory) ?? postgres.DataDirectory, "workflows")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        AudioDirectory = Path.Combine(RootDirectory, AudioFolder);
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(AudioDirectory);
    }

    private const string AudioFolder = "audio";

    public string RootDirectory { get; }
    public string AudioDirectory { get; }

    // ------------------------------------------------------------------
    // Archivos que producen las acciones (fotos)
    // ------------------------------------------------------------------

    /// <summary>
    /// Guarda un archivo de la ejecución y devuelve su ruta RELATIVA (con "/"
    /// como separador, igual en cualquier sistema de archivos). null si el
    /// disco lo rechazó: una acción sin foto igual deja constancia.
    /// </summary>
    public string? Save(byte[] content, DateTime localTime, string suffix, string extension = "jpg")
    {
        if (content.Length == 0) return null;
        try
        {
            string folder = $"{localTime:yyyy}/{localTime:MM}/{localTime:dd}";
            Directory.CreateDirectory(Path.Combine(RootDirectory, folder.Replace('/', Path.DirectorySeparatorChar)));
            string relative = $"{folder}/{localTime:HHmmssfff}-{Guid.NewGuid():N}-{Sanitize(suffix)}.{extension}";
            File.WriteAllBytes(FullPath(relative)!, content);
            return relative;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo guardar el archivo '{Suffix}' de una automatización.", suffix);
            return null;
        }
    }

    /// <summary>Ruta absoluta de un archivo guardado, o null si la ruta relativa es inválida.</summary>
    public string? FullPath(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        // Defensa contra rutas trepadoras: el archivo se entrega por HTTP.
        string full = Path.GetFullPath(Path.Combine(RootDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(RootDirectory, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>Borra las carpetas de días anteriores a la retención (las fotos de ejecuciones purgadas).</summary>
    public int PurgeOlderThan(DateTime cutoffLocal)
    {
        int removed = 0;
        try
        {
            foreach (string yearDir in Directory.EnumerateDirectories(RootDirectory))
            {
                if (string.Equals(Path.GetFileName(yearDir), AudioFolder, StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(Path.GetFileName(yearDir), out int year)) continue;
                foreach (string monthDir in Directory.EnumerateDirectories(yearDir))
                {
                    if (!int.TryParse(Path.GetFileName(monthDir), out int month)) continue;
                    foreach (string dayDir in Directory.EnumerateDirectories(monthDir))
                    {
                        if (!int.TryParse(Path.GetFileName(dayDir), out int day)) continue;
                        DateTime date;
                        try { date = new DateTime(year, month, day); }
                        catch (ArgumentOutOfRangeException) { continue; }
                        if (date >= cutoffLocal.Date) continue;
                        removed += Directory.EnumerateFiles(dayDir).Count();
                        Directory.Delete(dayDir, recursive: true);
                    }
                    if (!Directory.EnumerateFileSystemEntries(monthDir).Any()) Directory.Delete(monthDir);
                }
                if (!Directory.EnumerateFileSystemEntries(yearDir).Any()) Directory.Delete(yearDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudieron purgar los archivos antiguos de automatizaciones.");
        }
        return removed;
    }

    // ------------------------------------------------------------------
    // Sonidos para los parlantes IP
    // ------------------------------------------------------------------

    /// <summary>Sonidos disponibles, con su estado de conversión.</summary>
    public IReadOnlyList<WorkflowAudioDto> ListAudio()
    {
        var result = new List<WorkflowAudioDto>();
        foreach (string file in Directory.EnumerateFiles(AudioDirectory))
        {
            string extension = Path.GetExtension(file);
            if (extension is ".ulaw" or ".alaw") continue;   // son las conversiones, no el original
            string name = Path.GetFileNameWithoutExtension(file);
            var info = new FileInfo(file);
            result.Add(new WorkflowAudioDto(Path.GetFileName(file), name, info.Length,
                File.GetLastWriteTime(file), File.Exists(PayloadPath(name, "ulaw"))));
        }
        return result.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Ruta del original de un sonido (cualquier extensión), o null si no existe.</summary>
    public string? FindAudio(string name)
    {
        string safe = Sanitize(name);
        if (safe.Length == 0) return null;
        return Directory.EnumerateFiles(AudioDirectory, safe + ".*")
            .FirstOrDefault(f => Path.GetExtension(f) is not (".ulaw" or ".alaw"));
    }

    /// <summary>Ruta del audio ya convertido a G.711 (payload crudo, sin cabecera), o null si falta.</summary>
    public string? FindPayload(string name, string codec)
    {
        string path = PayloadPath(Sanitize(name), codec);
        return File.Exists(path) ? path : null;
    }

    private string PayloadPath(string safeName, string codec) => Path.Combine(AudioDirectory, $"{safeName}.{codec}");

    /// <summary>
    /// Guarda un sonido subido por el administrador y lo convierte a los dos
    /// formatos que aceptan los parlantes (G.711 µ-law y A-law, 8 kHz mono).
    /// Devuelve el mensaje de error de la conversión, o null si todo salió bien.
    /// </summary>
    public async Task<string?> SaveAudioAsync(string fileName, Stream content, CancellationToken ct)
    {
        string safe = Sanitize(Path.GetFileNameWithoutExtension(fileName));
        if (safe.Length == 0) safe = "sonido";
        string extension = Path.GetExtension(fileName);
        if (extension.Length is 0 or > 8) extension = ".wav";

        // Un nombre reemplaza al anterior: los .ulaw/.alaw viejos quedarían huérfanos.
        foreach (string old in Directory.EnumerateFiles(AudioDirectory, safe + ".*"))
        {
            try { File.Delete(old); } catch (IOException) { /* en uso: se sobreescribe abajo */ }
        }

        string original = Path.Combine(AudioDirectory, safe + extension);
        await using (var file = File.Create(original))
            await content.CopyToAsync(file, ct);

        if (MediaMtxManager.LocateFfmpeg() is not { } ffmpeg)
            return "El servidor no tiene FFmpeg disponible: el sonido quedó guardado pero no se pudo convertir al formato del parlante.";

        foreach (string codec in (string[])["ulaw", "alaw"])
        {
            string? error = await ConvertAsync(ffmpeg, original, PayloadPath(safe, codec), codec, ct);
            if (error is not null) return error;
        }
        return null;
    }

    public bool DeleteAudio(string name)
    {
        string safe = Sanitize(name);
        if (safe.Length == 0) return false;
        bool any = false;
        foreach (string file in Directory.EnumerateFiles(AudioDirectory, safe + ".*"))
        {
            try { File.Delete(file); any = true; }
            catch (Exception ex) { _logger.LogWarning(ex, "No se pudo borrar el sonido {File}.", file); }
        }
        return any;
    }

    /// <summary>
    /// Convierte a G.711 crudo (sin cabecera de archivo): es exactamente el
    /// flujo que espera el canal de audio bidireccional de los equipos.
    /// OJO: el multiplexor de FFmpeg para µ-law se llama <c>mulaw</c>, no
    /// "ulaw" (el nombre que usamos para el archivo); con el nombre errado
    /// contesta "Requested output format is not known" y no convierte nada.
    /// </summary>
    private async Task<string?> ConvertAsync(string ffmpeg, string input, string output, string codec, CancellationToken ct)
    {
        string muxer = codec == "ulaw" ? "mulaw" : codec;
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string argument in (string[])[
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-i", input,
            "-ar", "8000", "-ac", "1",
            "-f", muxer, output])
        {
            psi.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(psi)!;
            string stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode == 0 && new FileInfo(output).Length > 0) return null;
            _logger.LogWarning("FFmpeg no pudo convertir '{Input}' a {Codec}: {Error}", input, codec, stderr.Trim());
            return $"El archivo no se pudo convertir a G.711 ({codec}): ¿es un audio válido?";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallo al ejecutar FFmpeg para convertir '{Input}'.", input);
            return $"No se pudo ejecutar FFmpeg para convertir el sonido: {ex.Message}";
        }
    }

    /// <summary>Deja solo caracteres seguros para un nombre de archivo.</summary>
    public static string Sanitize(string value)
    {
        var clean = new string(value.Trim().Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '-').ToArray()).Trim();
        return clean.Length > 48 ? clean[..48] : clean;
    }
}
