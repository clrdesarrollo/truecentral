using System.Diagnostics;

namespace TrueCentralVms.AlarmScreen.Services;

/// <summary>Una salida de audio del equipo, tal como la ve el sistema.</summary>
public sealed record AudioOutput(string Id, string Name);

/// <summary>
/// Lo que el panel puede hacerle al equipo: reiniciarlo y elegir por dónde suena.
///
/// Todo se resuelve llamando a las herramientas del sistema en vez de hablar con PipeWire o
/// systemd por sus APIs. En una caseta lo que importa es que funcione con lo que trae la imagen
/// de Raspberry Pi OS y que se pueda depurar escribiendo el mismo comando por SSH.
/// </summary>
public static class DeviceControl
{
    /// <summary>
    /// Reinicia el equipo. Se prefiere <c>systemctl reboot</c> a <c>reboot</c> a secas porque es
    /// el que respeta el apagado ordenado de los servicios; si no está, se cae al otro.
    /// </summary>
    public static bool Reboot()
    {
        if (!OperatingSystem.IsLinux()) return false;
        return Run("/usr/bin/sudo", "-n /usr/bin/systemctl reboot")
            || Run("/usr/bin/sudo", "-n /sbin/reboot");
    }

    /// <summary>
    /// Salidas de audio disponibles. El identificador que se devuelve es el <b>nombre</b> del
    /// sink y no su número: los números de PipeWire cambian en cada arranque, así que guardar
    /// un 35 significa que tras reiniciar el sonido sale por donde toque el azar.
    /// </summary>
    public static IReadOnlyList<AudioOutput> AudioOutputs()
    {
        if (!OperatingSystem.IsLinux()) return [];

        var salida = Capture("/usr/bin/wpctl", "status");
        if (salida is null) return [];

        var resultado = new List<AudioOutput>();
        var dentroDeSinks = false;

        foreach (var linea in salida.Split('\n'))
        {
            if (linea.Contains("Sinks:")) { dentroDeSinks = true; continue; }
            if (dentroDeSinks && (linea.Contains("Sources:") || linea.Contains("Filters:"))) break;
            if (!dentroDeSinks) continue;

            // │  *   35. Built-in Audio Digital Stereo (HDMI) [vol: 1.00]
            var limpia = linea.Trim(' ', '│', '├', '└', '─', '*').Trim();
            var punto = limpia.IndexOf(". ", StringComparison.Ordinal);
            if (punto <= 0 || !int.TryParse(limpia[..punto], out _)) continue;

            var nombre = limpia[(punto + 2)..].Trim();
            var corchete = nombre.IndexOf(" [", StringComparison.Ordinal);
            if (corchete > 0) nombre = nombre[..corchete].Trim();

            if (nombre.Length > 0) resultado.Add(new AudioOutput(nombre, nombre));
        }
        return resultado;
    }

    /// <summary>
    /// Fija la salida por la que suena el equipo. Se busca el sink por nombre y se traduce al
    /// número que pide wpctl en ese momento; si ya no existe —cambió el cable de HDMI, se quitó
    /// un USB— no se toca nada y sigue sonando por donde estaba, que es mejor que quedarse mudo.
    /// </summary>
    public static bool UseAudioOutput(string? name)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(name)) return false;

        var salida = Capture("/usr/bin/wpctl", "status");
        if (salida is null) return false;

        foreach (var linea in salida.Split('\n'))
        {
            if (!linea.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;

            var limpia = linea.Trim(' ', '│', '├', '└', '─', '*').Trim();
            var punto = limpia.IndexOf(". ", StringComparison.Ordinal);
            if (punto <= 0 || !int.TryParse(limpia[..punto], out var id)) continue;

            return Run("/usr/bin/wpctl", $"set-default {id}");
        }
        return false;
    }

    /// <summary>
    /// Volumen 0–100 sobre la salida activa. Va por PipeWire y no por <c>amixer</c>: en una
    /// Raspberry con salida HDMI <b>no existe ningún control de mezclador en ALSA</b> —
    /// comprobado, las dos tarjetas vc4hdmi no exponen ninguno— y el único que hay vive en la
    /// tarjeta del jack, que no es la que suena. Con amixer el deslizador del panel no movía nada.
    ///
    /// Se aplica sobre la salida por omisión, así que sigue automáticamente a la que se haya
    /// elegido en el panel.
    /// </summary>
    public static bool SetVolume(int percent)
    {
        if (!OperatingSystem.IsLinux()) return false;
        percent = Math.Clamp(percent, 0, 100);

        // InvariantCulture a propósito: la Pi está en español y "0,8" no lo entiende wpctl,
        // así que sin esto el volumen falla justo en las máquinas del cliente y no en las de prueba.
        var nivel = (percent / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        if (Run("/usr/bin/wpctl", $"set-volume @DEFAULT_AUDIO_SINK@ {nivel}")) return true;

        // imágenes viejas sin PipeWire: el camino de antes, ahora probando también por tarjeta
        foreach (var control in new[] { "Master", "PCM", "Headphone", "Speaker" })
        {
            if (Run("/usr/bin/amixer", $"-q sset {control} {percent}%")) return true;
            for (var card = 0; card <= 3; card++)
                if (Run("/usr/bin/amixer", $"-q -c {card} sset {control} {percent}%")) return true;
        }
        return false;
    }

    // ---------- apoyo ----------

    private static ProcessStartInfo Info(string exe, string args) => new(exe, args)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        // el servicio de usuario no siempre hereda estas dos, y sin ellas wpctl no encuentra
        // el servidor de audio de la sesión gráfica
        Environment =
        {
            ["XDG_RUNTIME_DIR"] = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
                                  ?? $"/run/user/{Environment.GetEnvironmentVariable("UID") ?? "1000"}"
        }
    };

    private static bool Run(string exe, string args)
    {
        try
        {
            if (!File.Exists(exe)) return false;
            using var proceso = Process.Start(Info(exe, args));
            if (proceso is null) return false;
            proceso.WaitForExit(8000);
            return proceso.HasExited && proceso.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string? Capture(string exe, string args)
    {
        try
        {
            if (!File.Exists(exe)) return null;
            using var proceso = Process.Start(Info(exe, args));
            if (proceso is null) return null;

            var texto = proceso.StandardOutput.ReadToEnd();
            proceso.WaitForExit(8000);
            return proceso.ExitCode == 0 ? texto : null;
        }
        catch { return null; }
    }
}
