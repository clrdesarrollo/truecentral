namespace TrueCentralVms.Core.Drivers;

/// <summary>Datos de conexión a un parlante IP (API HTTP del fabricante).</summary>
public sealed record SpeakerConnectionInfo(string Host, int Port, bool UseHttps, string Username, string Password);

/// <summary>Qué sabe hacer el parlante, leído de sus capacidades al validarlo.</summary>
public sealed record SpeakerCapabilities(
    /// <summary>Guarda audios propios y los reproduce por id (sin transmitir).</summary>
    bool SupportsLibrary,
    /// <summary>Genera audio desde texto en el propio equipo.</summary>
    bool SupportsTts,
    /// <summary>Acepta un flujo de audio en vivo (voz del operador, sonidos del servidor).</summary>
    bool SupportsLiveAudio,
    bool SupportsVolume,
    /// <summary>Códecs que acepta el canal en vivo, en el orden de preferencia del equipo ("G.711alaw", ...).</summary>
    IReadOnlyList<string> LiveCodecs,
    /// <summary>Idiomas de texto a voz que ofrece el equipo (claves del fabricante).</summary>
    IReadOnlyList<string> TtsLanguages,
    /// <summary>Formatos admitidos al subir un archivo a la biblioteca ("mp3", "wav"...).</summary>
    IReadOnlyList<string> UploadFormats,
    long MaxUploadBytes);

/// <summary>Identificación del parlante obtenida al validar las credenciales.</summary>
public sealed record SpeakerInfo(string? Model, string? SerialNumber, string? FirmwareVersion, string? DeviceType,
    SpeakerCapabilities Capabilities, int? Volume);

/// <summary>Audio de la biblioteca del parlante.</summary>
public sealed record SpeakerAudioItem(long Id, string Name, string Format, int DurationSeconds, long Bytes, bool BuiltIn);

public sealed record SpeakerPlaybackState(bool IsPlaying, string? CurrentName);

/// <summary>
/// Canal de audio en vivo abierto contra el parlante. Quien escribe debe
/// hacerlo al ritmo real de reproducción (los equipos descartan lo que
/// llega de golpe); liberarlo cierra el canal en el equipo.
/// </summary>
public interface ISpeakerAudioSession : IAsyncDisposable
{
    /// <summary>Formato que espera <see cref="WriteAsync"/>: "alaw", "ulaw" o "pcm16" (8 kHz mono).</summary>
    string Codec { get; }

    /// <summary>false cuando el equipo cerró el canal (hay que reabrirlo).</summary>
    bool IsAlive { get; }

    Task WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default);
}

/// <summary>
/// Driver de una marca de parlantes IP. Los métodos lanzan
/// <see cref="DriverException"/> con mensaje en español cuando el equipo es
/// inalcanzable, rechaza las credenciales o rechaza la orden. Las funciones
/// que el equipo no soporta (según <see cref="SpeakerCapabilities"/>) lanzan
/// <see cref="NotSupportedException"/>.
/// </summary>
public interface ISpeakerDriver
{
    /// <summary>Valida credenciales y lee identificación y capacidades.</summary>
    Task<SpeakerInfo> ProbeAsync(SpeakerConnectionInfo info, CancellationToken ct = default);

    Task<IReadOnlyList<SpeakerAudioItem>> GetLibraryAsync(SpeakerConnectionInfo info, CancellationToken ct = default);

    /// <summary>Sube un archivo a la biblioteca del equipo y devuelve el registro resultante.</summary>
    Task<SpeakerAudioItem> UploadAudioAsync(SpeakerConnectionInfo info, string name, string format, byte[] content,
        CancellationToken ct = default);

    Task DeleteAudioAsync(SpeakerConnectionInfo info, long audioId, CancellationToken ct = default);

    /// <summary>Cambia el nombre con que el equipo muestra un audio de su biblioteca.</summary>
    Task<SpeakerAudioItem> RenameAudioAsync(SpeakerConnectionInfo info, long audioId, string newName, CancellationToken ct = default);

    /// <summary>Descarga el archivo de un audio de la biblioteca (bytes y tipo MIME), para escucharlo localmente.</summary>
    Task<(byte[] Content, string ContentType, string FileName)> DownloadAudioAsync(SpeakerConnectionInfo info, long audioId, CancellationToken ct = default);

    /// <summary>Genera un audio de texto a voz en la biblioteca del equipo.</summary>
    Task<SpeakerAudioItem> CreateTtsAsync(SpeakerConnectionInfo info, string name, string text, string language, string voice,
        CancellationToken ct = default);

    Task PlayLibraryAsync(SpeakerConnectionInfo info, long audioId, CancellationToken ct = default);

    /// <summary>Detiene lo que esté sonando (biblioteca).</summary>
    Task StopAsync(SpeakerConnectionInfo info, CancellationToken ct = default);

    Task<SpeakerPlaybackState> GetPlaybackStateAsync(SpeakerConnectionInfo info, CancellationToken ct = default);

    Task<int?> GetVolumeAsync(SpeakerConnectionInfo info, CancellationToken ct = default);

    Task SetVolumeAsync(SpeakerConnectionInfo info, int volume, CancellationToken ct = default);

    /// <summary>Abre el canal de audio en vivo. El códec resultante lo dice la sesión.</summary>
    Task<ISpeakerAudioSession> OpenLiveAudioAsync(SpeakerConnectionInfo info, CancellationToken ct = default);
}

public interface ISpeakerDriverFactory
{
    /// <summary>Clave estable ("hikvision-isapi").</summary>
    string DriverKey { get; }

    string DisplayName { get; }

    int DefaultPort { get; }

    bool DefaultHttps { get; }

    ISpeakerDriver Create();
}

/// <summary>Registro de drivers de parlantes, poblado por inyección de dependencias.</summary>
public sealed class SpeakerDriverRegistry
{
    private readonly Dictionary<string, ISpeakerDriverFactory> _factories;

    public SpeakerDriverRegistry(IEnumerable<ISpeakerDriverFactory> factories)
    {
        _factories = factories.ToDictionary(f => f.DriverKey, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<ISpeakerDriverFactory> All => _factories.Values;

    public ISpeakerDriverFactory? Find(string driverKey) =>
        _factories.TryGetValue(driverKey, out var factory) ? factory : null;
}
