using System.Runtime.InteropServices;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Hikvision.Interop;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Voz en modo reenvío por el SDK (<c>NET_DVR_StartVoiceCom_MR_V30</c>):
/// G.711 en tramas de 160 bytes (20 ms) en ambos sentidos, sin tocar la tarjeta
/// de sonido del servidor. La usan los citófonos y, como parlante, los equipos
/// que no tienen audio bidireccional por ISAPI (terminales faciales).
/// </summary>
internal static class HikvisionSdkVoice
{
    /// <summary>Trama de voz: 160 bytes G.711 = 20 ms a 8 kHz.</summary>
    public const int FrameBytes = 160;

    /// <summary>Códec del canal de voz del equipo ("ulaw"/"alaw"), o null si no tiene uno que el VMS sepa generar.</summary>
    public static string? CodecOf(int userId)
    {
        var compress = new IntercomInterop.NET_DVR_COMPRESSION_AUDIO { byres = new byte[4] };
        if (!IntercomInterop.NET_DVR_GetCurrentAudioCompress(userId, ref compress)) return null;
        return compress.byAudioEncType switch { 1 => "ulaw", 2 => "alaw", _ => null };
    }

    /// <summary>
    /// ¿Tiene el equipo voz por el SDK? Inicia sesión, mira el códec del canal
    /// de voz y cierra. Sin SDK (puerto cerrado) o sin un códec G.711, no.
    /// </summary>
    public static Task<bool> HasVoiceAsync(string host, int port, string username, string password, CancellationToken ct) => Task.Run(() =>
    {
        int userId;
        try { userId = Login(host, port, username, password, "equipo"); }
        catch (DriverException) { return false; }
        try { return CodecOf(userId) is not null; }
        finally { CHCNetSDK.NET_DVR_Logout(userId); }
    }, ct);

    /// <summary>Inicia sesión SDK; lanza <see cref="DriverException"/> en español si el equipo la rechaza.</summary>
    public static int Login(string host, int port, string username, string password, string noun)
    {
        HikvisionSdk.EnsureInitialized();
        var deviceInfo = new CHCNetSDK.NET_DVR_DEVICEINFO_V30();
        int userId = CHCNetSDK.NET_DVR_Login_V30(host, port, username, password, ref deviceInfo);
        if (userId < 0)
        {
            uint code = CHCNetSDK.NET_DVR_GetLastError();
            throw new DriverException($"No se pudo iniciar sesión en el {noun} {host}:{port}: {HikvisionException.DescribeForUser(code)} (error {code}).");
        }
        return userId;
    }
}

internal sealed class HikvisionSdkVoiceSession(int userId, string codec, Action<ReadOnlyMemory<byte>>? onAudio)
: IIntercomVoiceSession, ISpeakerAudioSession
{
    private readonly object _sync = new();
    private readonly byte[] _pending = new byte[HikvisionSdkVoice.FrameBytes];
    private int _pendingLength;
    private int _handle = -1;
    private int _disposed;
    private int _consecutiveFailures;
    private IntercomInterop.VoiceDataCallback? _callback;

    public string Codec => codec;

    public bool IsAlive => Volatile.Read(ref _disposed) == 0 && _handle >= 0 && _consecutiveFailures < 25;

    public void Start(string host, string noun)
    {
        _callback = OnVoice;
        int handle = IntercomInterop.NET_DVR_StartVoiceCom_MR_V30(userId, 1, _callback, IntPtr.Zero);
        if (handle < 0)
        {
            uint code = CHCNetSDK.NET_DVR_GetLastError();
            throw new DriverException(
                $"El {noun} {host} no abrió el canal de voz: {HikvisionException.DescribeForUser(code)} (error {code}).");
        }
        _handle = handle;
    }

    private void OnVoice(int handle, IntPtr buffer, uint size, byte flag, IntPtr user)
    {
        // flag 1 = audio que viene del equipo; 0 = eco de lo que enviamos. Como parlante, no se escucha.
        if (onAudio is null || flag != 1 || size == 0 || size > 64 * 1024) return;
        try
        {
            var copy = new byte[size];
            Marshal.Copy(buffer, copy, 0, (int)size);
            onAudio(copy);
        }
        catch { /* nunca propagar al hilo del SDK */ }
    }

    public Task WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        // Llamada nativa corta y sin bloqueo de red: se hace en línea.
        lock (_sync)
        {
            if (!IsAlive) return Task.CompletedTask;
            var span = frame.Span;
            while (span.Length > 0)
            {
                int take = Math.Min(HikvisionSdkVoice.FrameBytes - _pendingLength, span.Length);
                span[..take].CopyTo(_pending.AsSpan(_pendingLength));
                _pendingLength += take;
                span = span[take..];
                if (_pendingLength < HikvisionSdkVoice.FrameBytes) break;
                _consecutiveFailures = IntercomInterop.NET_DVR_VoiceComSendData(_handle, _pending, HikvisionSdkVoice.FrameBytes)
                    ? 0
                    : _consecutiveFailures + 1;
                _pendingLength = 0;
            }
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        return new ValueTask(Task.Run(() =>
        {
            lock (_sync)
            {
                if (_handle >= 0) IntercomInterop.NET_DVR_StopVoiceCom(_handle);
                _handle = -1;
                CHCNetSDK.NET_DVR_Logout(userId);
            }
            GC.KeepAlive(_callback);
        }));
    }
}
