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

    /// <summary>Nivel de un sonido medido con FFmpeg (volumedetect) sobre el original.</summary>
    public sealed record AudioLevel(double? PeakDb, double? MeanDb, double? DurationSeconds);

    /// <summary>Margen bajo 0 dBFS al amplificar "al máximo": evita el recorte por redondeo del códec.</summary>
    public const double GainHeadroomDb = 0.3;

    /// <summary>
    /// Mide el pico y el nivel medio del original (dBFS, 0 = techo digital).
    /// Con eso el panel muestra cuánto se puede amplificar sin saturar.
    /// </summary>
    public async Task<AudioLevel?> MeasureAudioAsync(string name, CancellationToken ct)
    {
        if (FindAudio(name) is not { } original) return null;
        if (MediaMtxManager.LocateFfmpeg() is not { } ffmpeg) return null;
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (string argument in (string[])["-hide_banner", "-nostdin", "-i", original, "-af", "volumedetect", "-f", "null", "-"])
            psi.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(psi)!;
            string stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return new AudioLevel(ParseDb(stderr, "max_volume:"), ParseDb(stderr, "mean_volume:"), ParseDuration(stderr));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo medir el nivel del sonido '{Name}'.", name);
            return null;
        }
    }

    /// <summary>
    /// Aplica una ganancia en dB al original (queda como WAV PCM de 16 bits) y
    /// vuelve a convertirlo a G.711. Si <paramref name="gainDb"/> es null se
    /// amplifica al máximo sin saturar: el pico queda a <see cref="GainHeadroomDb"/>
    /// bajo 0 dBFS. Nunca deja el pico por encima de ese margen, aunque se
    /// pida más. Devuelve (ganancia aplicada, error).
    /// </summary>
    public async Task<(double Applied, string? Error)> ApplyGainAsync(string name, double? gainDb, CancellationToken ct)
    {
        if (FindAudio(name) is not { } original) return (0, "El sonido no existe.");
        if (MediaMtxManager.LocateFfmpeg() is not { } ffmpeg) return (0, "El servidor no tiene FFmpeg disponible.");
        var level = await MeasureAudioAsync(name, ct);
        if (level?.PeakDb is not { } peak) return (0, "No se pudo medir el nivel del sonido.");

        double max = -peak - GainHeadroomDb;            // lo más que se puede subir sin recortar
        double gain = Math.Round(Math.Min(gainDb ?? max, max), 1);
        if (gainDb is null && gain <= 0.05) return (0, "El sonido ya está al máximo sin saturar.");
        if (gain is < -30 or > 40) return (0, "La ganancia debe estar entre -30 y +40 dB.");

        string safe = Path.GetFileNameWithoutExtension(original);
        string temp = Path.Combine(AudioDirectory, $"{safe}.gain-{Guid.NewGuid():N}.wav");
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (string argument in (string[])[
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-i", original, "-af", $"volume={gain.ToString(System.Globalization.CultureInfo.InvariantCulture)}dB",
            "-c:a", "pcm_s16le", temp])
            psi.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(psi)!;
            string stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0 || new FileInfo(temp).Length == 0)
            {
                _logger.LogWarning("FFmpeg no pudo aplicar ganancia a '{Name}': {Error}", name, stderr.Trim());
                try { File.Delete(temp); } catch (IOException) { /* nada */ }
                return (0, "FFmpeg no pudo aplicar la ganancia: ¿es un audio válido?");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallo al ejecutar FFmpeg para amplificar '{Name}'.", name);
            return (0, $"No se pudo ejecutar FFmpeg: {ex.Message}");
        }

        // Reemplazar el original (y sus conversiones) por la versión amplificada.
        foreach (string old in Directory.EnumerateFiles(AudioDirectory, safe + ".*"))
        {
            if (string.Equals(old, temp, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(old); } catch (IOException) { /* en uso: se sobreescribe abajo */ }
        }
        string target = Path.Combine(AudioDirectory, safe + ".wav");
        File.Move(temp, target, overwrite: true);
        foreach (string codec in (string[])["ulaw", "alaw"])
        {
            string? error = await ConvertAsync(ffmpeg, target, PayloadPath(safe, codec), codec, ct);
            if (error is not null) return (gain, error);
        }
        return (gain, null);
    }

    private static double? ParseDb(string text, string label)
    {
        int i = text.LastIndexOf(label, StringComparison.Ordinal);
        if (i < 0) return null;
        string rest = text[(i + label.Length)..].TrimStart();
        int end = rest.IndexOf(" dB", StringComparison.Ordinal);
        if (end < 0) return null;
        return double.TryParse(rest[..end].Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : null;
    }

    private static double? ParseDuration(string text)
    {
        int i = text.IndexOf("Duration:", StringComparison.Ordinal);
        if (i < 0) return null;
        string rest = text[(i + 9)..].TrimStart();
        int end = rest.IndexOf(',');
        if (end < 0) return null;
        return TimeSpan.TryParse(rest[..end].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var span)
            ? Math.Round(span.TotalSeconds, 1) : null;
    }

    /// <summary>Deja solo caracteres seguros para un nombre de archivo.</summary>
    public static string Sanitize(string value)
    {
        var clean = new string(value.Trim().Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '-').ToArray()).Trim();
        return clean.Length > 48 ? clean[..48] : clean;
    }
}
