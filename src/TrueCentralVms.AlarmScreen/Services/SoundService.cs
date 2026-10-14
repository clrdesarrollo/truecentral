using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.AlarmScreen.Services;

/// <summary>
/// El ruido de la caseta. Los tonos base se generan una sola vez como WAV para que un equipo
/// recién instalado suene sin depender de nada; encima de eso, el servidor puede publicar sus
/// propios audios y esta clase los usa en lugar de los generados.
/// En Windows se reproduce con winmm; en la Raspberry Pi con aplay (alsa-utils, ya viene en la
/// imagen). Todo es "best effort": un equipo sin audio no debe botar la app.
/// </summary>
public class SoundService
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);
    private const uint SND_FILENAME = 0x00020000;
    private const uint SND_ASYNC = 0x0001;

    // PlaySound solo entiende WAV; para mp3/ogg se usa MCI, que sí los reproduce.
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? buffer, int bufferSize, IntPtr hwnd);

    [DllImport("winmm.dll")]
    private static extern int waveOutSetVolume(IntPtr device, uint volume);

    private static readonly object MciLock = new();
    private static int _mciAlias;

    /// <summary>Reproductor externo en curso (Linux), para poder cortarlo al marcar la alarma.</summary>
    private static readonly object PlayLock = new();
    private static Process? _playing;

    // Alarma normal: dos notas ascendentes. Prioridad alta: triple urgente repetido.
    // Recordatorio (alarmas sin ver): toque doble corto, menos invasivo.
    private static readonly (double freq, double dur, double gap)[] AlarmNotes =
        { (659.3, 0.14, 0.03), (987.8, 0.38, 0.0) };
    private static readonly (double freq, double dur, double gap)[] HighNotes =
        { (880.0, 0.11, 0.03), (1174.7, 0.11, 0.03), (1568.0, 0.16, 0.10),
          (880.0, 0.11, 0.03), (1174.7, 0.11, 0.03), (1568.0, 0.34, 0.0) };
    private static readonly (double freq, double dur, double gap)[] ReminderNotes =
        { (987.8, 0.10, 0.07), (987.8, 0.22, 0.0) };

    private readonly string _alarmWav;
    private readonly string _highWav;
    private readonly string _reminderWav;

    /// <summary>Audios publicados por el servidor: nombre → ruta local ya descargada.</summary>
    private Dictionary<string, string> _library = new(StringComparer.OrdinalIgnoreCase);
    private AlarmScreenConfigDto _config = new();

    public SoundService()
    {
        Directory.CreateDirectory(ConfigService.AppDataDir);
        _alarmWav = Path.Combine(ConfigService.AppDataDir, "alarma.wav");
        _highWav = Path.Combine(ConfigService.AppDataDir, "alarma-alta.wav");
        _reminderWav = Path.Combine(ConfigService.AppDataDir, "recordatorio.wav");
        try
        {
            if (!File.Exists(_alarmWav)) File.WriteAllBytes(_alarmWav, BuildChime(AlarmNotes));
            if (!File.Exists(_highWav)) File.WriteAllBytes(_highWav, BuildChime(HighNotes));
            if (!File.Exists(_reminderWav)) File.WriteAllBytes(_reminderWav, BuildChime(ReminderNotes));
        }
        catch { }
    }

    /// <summary>Aplica lo que mandó el servidor, incluido el volumen del terminal.</summary>
    public void Configure(AlarmScreenConfigDto config, Dictionary<string, string> library)
    {
        _config = config;
        _library = library;
        SetVolume(config.Volume);
    }

    /// <summary>Pitido de alarma nueva. <paramref name="ruleSound"/> es el audio de la regla del tipo, si tiene.</summary>
    public void PlayNew(int priority, string? ruleSound = null)
    {
        // la regla del tipo manda sobre el sonido general: es lo más específico que configuró el admin
        if (Resolve(ruleSound) is { } fromRule) { Play(fromRule); return; }

        if (priority == 1 && Resolve(_config.HighPrioritySound) is { } high) { Play(high); return; }
        if (Resolve(_config.AlarmSound) is { } alarm) { Play(alarm); return; }

        Play(priority == 1 ? _highWav : _alarmWav);
    }

    /// <summary>Recordatorio periódico mientras haya alarmas sin ver.</summary>
    public void PlayReminder() => Play(Resolve(_config.ReminderSound) ?? _reminderWav);

    /// <summary>Reproduce un audio del servidor por nombre, para el botón de prueba.</summary>
    public void PlayNamed(string name)
    {
        if (Resolve(name) is { } path) Play(path);
    }

    private string? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return _library.TryGetValue(name, out var path) && File.Exists(path) ? path : null;
    }

    // ---------- volumen ----------

    /// <summary>
    /// Se aplica al mezclador del equipo, no a cada reproducción: en la Pi con amixer, que es lo
    /// que entiende el hardware de audio de la placa. Si no hay control «Master» se prueban los
    /// nombres habituales de la Pi.
    /// </summary>
    private static void SetVolume(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // el mismo valor en los dos canales de 16 bits
                var level = (uint)(percent / 100.0 * 0xFFFF);
                waveOutSetVolume(IntPtr.Zero, (level << 16) | level);
                return;
            }

            // en Linux lo resuelve DeviceControl: en la Pi hay que ir por PipeWire, porque la
            // salida HDMI no expone ningún control de mezclador en ALSA
            DeviceControl.SetVolume(percent);
        }
        catch { }
    }

    // ---------- reproducción ----------

    /// <summary>
    /// Corta lo que esté sonando. Se llama al marcar una alarma: el guardia acaba de decir que
    /// la vio, y que el pitido siga hasta terminar el archivo se siente como que no le hizo caso.
    /// </summary>
    public void Stop()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                PlaySound(null, IntPtr.Zero, 0);   // corta el WAV en curso
                lock (MciLock)
                    if (_mciAlias > 0)
                        mciSendString($"stop clrsnd{_mciAlias}", null, 0, IntPtr.Zero);
                return;
            }
            StopExternalPlayer();
        }
        catch { }
    }

    private static void Play(string file)
    {
        try
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (OperatingSystem.IsWindows())
            {
                if (ext == ".wav") PlaySound(file, IntPtr.Zero, SND_FILENAME | SND_ASYNC);
                else PlayViaMci(file);
                return;
            }
            // Linux / Raspberry Pi: aplay solo lee WAV; para comprimidos se busca un reproductor
            if (ext == ".wav")
            {
                PlayExternal("/usr/bin/aplay", $"-q \"{file}\"");
                return;
            }
            foreach (var (exe, args) in new[]
            {
                ("/usr/bin/mpg123", $"-q \"{file}\""),
                ("/usr/bin/ogg123", $"-q \"{file}\""),
                ("/usr/bin/ffplay", $"-nodisp -autoexit -loglevel quiet \"{file}\""),
                ("/usr/bin/paplay", $"\"{file}\"")
            })
            {
                if (File.Exists(exe))
                {
                    PlayExternal(exe, args);
                    return;
                }
            }
        }
        catch { }
    }

    /// <summary>Lanza el reproductor y guarda su proceso, para poder matarlo si hay que callar.</summary>
    private static void PlayExternal(string exe, string args)
    {
        _ = Task.Run(() =>
        {
            try
            {
                StopExternalPlayer();   // nunca dos pitidos encimados
                var process = Process.Start(new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (process is null) return;

                lock (PlayLock) _playing = process;
                process.WaitForExit(20000);
                lock (PlayLock) if (ReferenceEquals(_playing, process)) _playing = null;
            }
            catch { }
        });
    }

    private static void StopExternalPlayer()
    {
        lock (PlayLock)
        {
            try { if (_playing is { HasExited: false }) _playing.Kill(entireProcessTree: true); }
            catch { }
            _playing = null;
        }
    }

    /// <summary>Reproducción asíncrona vía MCI (soporta mp3, ogg, wav…).</summary>
    private static void PlayViaMci(string file)
    {
        lock (MciLock)
        {
            var alias = "clrsnd" + (++_mciAlias);
            // cerrar la reproducción anterior para no acumular dispositivos abiertos
            if (_mciAlias > 1) mciSendString($"close clrsnd{_mciAlias - 1}", null, 0, IntPtr.Zero);
            if (mciSendString($"open \"{file}\" alias {alias}", null, 0, IntPtr.Zero) != 0) return;
            mciSendString($"play {alias}", null, 0, IntPtr.Zero);
        }
    }

    /// <summary>true si el proceso existía y terminó bien.</summary>
    private static bool RunQuiet(string exe, string args, int timeoutMs)
    {
        try
        {
            if (!File.Exists(exe)) return false;
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p is null) return false;
            p.WaitForExit(timeoutMs);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// WAV PCM 16-bit mono 44,1 kHz con las notas indicadas, decaimiento exponencial y fundido
    /// corto de entrada para que no haga "clic".
    /// </summary>
    private static byte[] BuildChime((double freq, double dur, double gap)[] notes)
    {
        const int rate = 44100;
        var totalSamples = 0;
        foreach (var n in notes) totalSamples += (int)((n.dur + n.gap) * rate);

        var samples = new short[totalSamples];
        int pos = 0;
        foreach (var n in notes)
        {
            int len = (int)(n.dur * rate);
            for (int i = 0; i < len; i++)
            {
                double t = (double)i / rate;
                double envelope = Math.Min(1.0, i / (0.005 * rate)) * Math.Exp(-4.5 * t / n.dur);
                double v = Math.Sin(2 * Math.PI * n.freq * t) * envelope * 0.45;
                samples[pos + i] = (short)(v * short.MaxValue);
            }
            pos += len + (int)(n.gap * rate);
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        return ms.ToArray();
    }
}
