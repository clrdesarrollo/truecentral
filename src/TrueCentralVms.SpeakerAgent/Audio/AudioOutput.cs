using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.Wave;

namespace TrueCentralVms.SpeakerAgent.Audio;

/// <summary>Salida de audio PCM 16 bits mono a <see cref="AudioEngine.SampleRate"/> Hz.</summary>
public interface IAudioOutput : IDisposable
{
    string DeviceName { get; }

    /// <summary>Escribe y BLOQUEA al ritmo del dispositivo (con un margen de búfer): así el bucle de reproducción no se adelanta.</summary>
    void Write(ReadOnlySpan<short> samples);
}

public sealed record AudioDeviceInfo(string Id, string Name);

public static class AudioOutputs
{
    public static IAudioOutput Open(string? device) =>
        OperatingSystem.IsWindows() ? new WaveOutOutput(device)
        : OperatingSystem.IsLinux() ? new AlsaOutput(device)
        : throw new PlatformNotSupportedException("Salida de audio solo en Windows y Linux.");

    public static IReadOnlyList<AudioDeviceInfo> List()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WaveOutOutput.ListDevices();
            if (OperatingSystem.IsLinux()) return AlsaOutput.ListDevices();
        }
        catch (Exception) { /* sin audio: lista vacía */ }
        return [];
    }
}

/// <summary>Windows: waveOut (NAudio) con un búfer intermedio; el mezclador de Windows convierte desde 8 kHz.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WaveOutOutput : IAudioOutput
{
    private static readonly TimeSpan Ahead = TimeSpan.FromMilliseconds(160);
    private readonly WaveOutEvent _out;
    private readonly BufferedWaveProvider _buffer;
    private byte[] _bytes = new byte[2048];

    public WaveOutOutput(string? device)
    {
        int number = -1;   // -1 = mapeador de Windows (dispositivo predeterminado)
        string name = "Predeterminado de Windows";
        if (!string.IsNullOrEmpty(device) && int.TryParse(device, out int wanted) && wanted >= 0 && wanted < waveOutGetNumDevs())
        {
            number = wanted;
            name = ProductName(wanted);
        }
        _buffer = new BufferedWaveProvider(new WaveFormat(AudioEngine.SampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
        };
        _out = new WaveOutEvent { DeviceNumber = number, DesiredLatency = 120, NumberOfBuffers = 3 };
        _out.Init(_buffer);
        _out.Play();
        DeviceName = name;
    }

    public string DeviceName { get; }

    public void Write(ReadOnlySpan<short> samples)
    {
        while (_buffer.BufferedDuration > Ahead) Thread.Sleep(5);
        int bytes = samples.Length * 2;
        if (_bytes.Length < bytes) _bytes = new byte[bytes];
        MemoryMarshal.AsBytes(samples).CopyTo(_bytes);
        _buffer.AddSamples(_bytes, 0, bytes);
    }

    public void Dispose()
    {
        try { _out.Stop(); } catch (Exception) { }
        _out.Dispose();
    }

    public static IReadOnlyList<AudioDeviceInfo> ListDevices()
    {
        var list = new List<AudioDeviceInfo> { new("", "Predeterminado de Windows") };
        int count = waveOutGetNumDevs();
        for (int i = 0; i < count; i++) list.Add(new(i.ToString(), ProductName(i)));
        return list;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveOutCaps
    {
        public ushort Mid;
        public ushort Pid;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ProductName;
        public uint Formats;
        public ushort Channels;
        public ushort Reserved;
        public uint Support;
    }

    [DllImport("winmm.dll")] private static extern int waveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveOutGetDevCapsW")]
    private static extern int waveOutGetDevCaps(nint deviceId, out WaveOutCaps caps, int size);

    private static string ProductName(int device) =>
        waveOutGetDevCaps(device, out var caps, Marshal.SizeOf<WaveOutCaps>()) == 0 ? caps.ProductName : $"Dispositivo {device}";
}

/// <summary>
/// Linux (Raspberry Pi OS): ALSA directo por libasound. <c>snd_pcm_set_params</c> con remuestreo por software,
/// así un DAC que solo acepta 44,1/48 kHz igual recibe el flujo de 8 kHz. <c>snd_pcm_writei</c> bloquea al ritmo
/// del dispositivo.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class AlsaOutput : IAudioOutput
{
    private const string Lib = "libasound.so.2";
    private const int StreamPlayback = 0;
    private const int FormatS16Le = 2;
    private const int AccessRwInterleaved = 3;
    /// <summary>Latencia total pedida a ALSA (µs).</summary>
    private const uint LatencyMicroseconds = 120_000;

    [DllImport(Lib)] private static extern int snd_pcm_open(out IntPtr pcm, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int stream, int mode);
    [DllImport(Lib)] private static extern int snd_pcm_set_params(IntPtr pcm, int format, int access, uint channels, uint rate, int softResample, uint latency);
    [DllImport(Lib)] private static extern unsafe nint snd_pcm_writei(IntPtr pcm, short* buffer, nuint frames);
    [DllImport(Lib)] private static extern int snd_pcm_recover(IntPtr pcm, int err, int silent);
    [DllImport(Lib)] private static extern int snd_pcm_drain(IntPtr pcm);
    [DllImport(Lib)] private static extern int snd_pcm_close(IntPtr pcm);
    [DllImport(Lib)] private static extern IntPtr snd_strerror(int errnum);

    private IntPtr _pcm;

    public AlsaOutput(string? device)
    {
        string name = string.IsNullOrEmpty(device) ? "default" : device;
        Check(snd_pcm_open(out _pcm, name, StreamPlayback, 0), $"abrir el dispositivo '{name}'");
        int rc = snd_pcm_set_params(_pcm, FormatS16Le, AccessRwInterleaved, 1, AudioEngine.SampleRate, 1, LatencyMicroseconds);
        if (rc < 0)
        {
            snd_pcm_close(_pcm);
            _pcm = IntPtr.Zero;
            Check(rc, $"configurar el dispositivo '{name}'");
        }
        DeviceName = name;
    }

    public string DeviceName { get; }

    public unsafe void Write(ReadOnlySpan<short> samples)
    {
        fixed (short* start = samples)
        {
            int offset = 0;
            while (offset < samples.Length)
            {
                nint written = snd_pcm_writei(_pcm, start + offset, (nuint)(samples.Length - offset));
                if (written < 0)
                {
                    // Subdesborde (la red se atrasó) o suspensión: se recupera y se sigue; otro error corta.
                    int rc = snd_pcm_recover(_pcm, (int)written, 1);
                    if (rc < 0) Check(rc, "escribir audio");
                    continue;
                }
                offset += (int)written;
            }
        }
    }

    public void Dispose()
    {
        if (_pcm == IntPtr.Zero) return;
        snd_pcm_drain(_pcm);
        snd_pcm_close(_pcm);
        _pcm = IntPtr.Zero;
    }

    private static void Check(int rc, string what)
    {
        if (rc >= 0) return;
        string error = Marshal.PtrToStringUTF8(snd_strerror(rc)) ?? rc.ToString();
        throw new IOException($"ALSA no pudo {what}: {error}.");
    }

    /// <summary>"default" más cada dispositivo de reproducción de /proc/asound/pcm ("00-00: bcm2835 Headphones : ... : playback 8").</summary>
    public static IReadOnlyList<AudioDeviceInfo> ListDevices()
    {
        var list = new List<AudioDeviceInfo> { new("", "Predeterminado (default)") };
        const string pcm = "/proc/asound/pcm";
        if (!File.Exists(pcm)) return list;
        foreach (string line in File.ReadLines(pcm))
        {
            if (!line.Contains("playback", StringComparison.Ordinal)) continue;
            var parts = line.Split(':');
            if (parts.Length < 2 || parts[0].Split('-') is not [var card, var dev]) continue;
            if (!int.TryParse(card, out int c) || !int.TryParse(dev, out int d)) continue;
            list.Add(new($"plughw:{c},{d}", $"{parts[1].Trim()} (plughw:{c},{d})"));
        }
        return list;
    }
}
