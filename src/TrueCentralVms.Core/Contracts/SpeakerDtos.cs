namespace TrueCentralVms.Core.Contracts;

// DTOs del módulo Parlantes IP (altavoces de red: mensajes pregrabados,
// texto a voz, voz en vivo del operador y reproducción sincronizada en
// varios equipos). Los enums viajan como texto en JSON.

/// <summary>Estado de conexión del servidor con el parlante.</summary>
public enum SpeakerStatus { Unknown, Online, Offline, AuthFailed }

/// <summary>Parlante tal como lo ven el panel y el cliente. NUNCA incluye la contraseña.</summary>
public sealed record SpeakerDto(
    int Id,
    string Name,
    string DriverKey,
    string Host,
    int Port,
    bool UseHttps,
    string Username,
    string? Model,
    string? SerialNumber,
    string? FirmwareVersion,
    /// <summary>Grupos de parlantes a los que pertenece, unidos por coma ("Perímetro, Todos"); null = ninguno.</summary>
    string? GroupName,
    bool Enabled,
    SpeakerStatus Status,
    string? LastError,
    DateTime? LastSeenAt,
    /// <summary>Volumen de salida 0..100 leído del equipo (null si no lo expone).</summary>
    int? Volume,
    /// <summary>El equipo guarda una biblioteca de audios propia (reproducción sin transmitir).</summary>
    bool SupportsLibrary,
    /// <summary>El equipo genera audio a partir de texto.</summary>
    bool SupportsTts,
    /// <summary>Acepta audio en vivo (voz del operador o sonidos del servidor).</summary>
    bool SupportsLiveAudio,
    /// <summary>Qué está ocupando el parlante ahora ("Voz: admin", "Sonido: sirena") o null si está libre.</summary>
    string? BusyWith,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    /// <summary>Ubicación en el árbol de Recursos (null = por ubicar).</summary>
    int? LocationId = null,
    /// <summary>Ruta legible de esa ubicación ("Casa matriz › Patio").</summary>
    string? Location = null,
    /// <summary>ID ISUP si el parlante se conecta al servidor por ISUP (entonces Host y puerto no aplican).</summary>
    string? IsupDeviceId = null,
    /// <summary>Hay clave ISUP guardada (la clave nunca viaja).</summary>
    bool HasIsupKey = false,
    /// <summary>Apagado por el cupo de la licencia: se enciende solo cuando vuelve a haber cupo.</summary>
    bool DisabledByLicense = false,
    /// <summary>Solo agentes «TrueCentral Speaker»: id del equipo enrolado (null = aún sin enrolar).</summary>
    string? AgentId = null,
    /// <summary>Solo agentes: vencimiento del código de enrolamiento vigente (null = no hay uno pendiente).</summary>
    DateTime? EnrollmentExpiresAt = null,
    /// <summary>Solo agentes: el equipo tiene el WebSocket abierto ahora.</summary>
    bool AgentConnected = false,
    /// <summary>Es el altavoz de un terminal de acceso: se administra desde Control de acceso y no ocupa parlante de la licencia.</summary>
    int? AccessDeviceId = null);

public sealed record SpeakerWriteDto(
    string Name,
    string DriverKey,
    string Host,
    int Port,
    bool UseHttps,
    string Username,
    /// <summary>Al editar, vacío = conservar la actual.</summary>
    string? Password,
    bool Enabled = true,
    /// <summary>Ya no se usa: los grupos se arman aparte (<see cref="SpeakerGroupWriteDto"/>).</summary>
    string? GroupName = null,
    /// <summary>Ubicación en el árbol de Recursos: null = conservar la actual (en el alta,
    /// por ubicar); 0 = por ubicar.</summary>
    int? LocationId = null,
    /// <summary>Solo con un driver ISUP: ID con que el parlante se registra en el servidor.</summary>
    string? IsupDeviceId = null,
    /// <summary>Solo con un driver ISUP: su clave; vacía en edición = conservar la guardada.</summary>
    string? IsupKey = null);

/// <summary>Resultado del botón "Probar conexión" (no persiste nada).</summary>
public sealed record SpeakerProbeResultDto(
    bool Success,
    string? Error,
    string? Model,
    string? SerialNumber,
    string? FirmwareVersion,
    bool SupportsLibrary,
    bool SupportsTts,
    bool SupportsLiveAudio,
    int? Volume,
    int LibraryCount);

public sealed record SpeakerDriverDto(string Key, string DisplayName, int DefaultPort, bool DefaultHttps, bool UsesIsup = false, bool UsesAgent = false);

/// <summary>Audio guardado en la biblioteca del propio parlante.</summary>
public sealed record SpeakerAudioItemDto(
    long Id,
    string Name,
    string Format,
    int DurationSeconds,
    long Bytes,
    bool BuiltIn);

/// <summary>De dónde sale el audio de una reproducción.</summary>
public static class SpeakerPlaySources
{
    /// <summary>Sonido subido al servidor (Automatizaciones → Sonidos): se transmite en vivo, sincronizado entre parlantes.</summary>
    public const string Server = "server";
    /// <summary>Audio de la biblioteca del propio parlante, elegido por nombre.</summary>
    public const string Library = "library";
    /// <summary>Texto convertido a voz por el propio parlante.</summary>
    public const string Tts = "tts";
}

/// <summary>Orden de reproducción en uno o más parlantes.</summary>
public sealed record SpeakerPlayRequestDto(
    IReadOnlyList<int> SpeakerIds,
    string Source,
    /// <summary>Nombre del sonido del servidor (Source = server).</summary>
    string? Sound = null,
    /// <summary>Nombre del audio en la biblioteca del parlante (Source = library); se busca en cada equipo.</summary>
    string? LibraryName = null,
    /// <summary>Texto a leer (Source = tts).</summary>
    string? Text = null,
    string? Language = null,
    string? Voice = null,
    /// <summary>Repeticiones de un sonido del servidor (1–5); 0 = en bucle hasta que alguien lo detenga.</summary>
    int Repeat = 1,
    /// <summary>Volumen de salida (0–100) que se fija en cada parlante antes de reproducir; null = no tocarlo.</summary>
    int? Volume = null);

public sealed record SpeakerStopRequestDto(IReadOnlyList<int> SpeakerIds);

/// <summary>Crear un audio de texto a voz en la biblioteca del parlante.</summary>
public sealed record SpeakerTtsRequestDto(string Name, string Text, string Language = "spanish", string Voice = "female");

public sealed record SpeakerVolumeRequestDto(int Volume);

/// <summary>Nombre nuevo para un audio de la biblioteca del parlante.</summary>
public sealed record SpeakerAudioRenameDto(string Name);

public sealed record SpeakerActionResultDto(int SpeakerId, string SpeakerName, bool Success, string Message);

/// <summary>Resultado de una orden que puede abarcar varios parlantes.</summary>
public sealed record SpeakerOperationResultDto(bool Success, string Message, IReadOnlyList<SpeakerActionResultDto> Results);

/// <summary>Idioma de texto a voz (clave del fabricante + etiqueta).</summary>
public sealed record SpeakerTtsLanguageDto(string Key, string Label);

/// <summary>Parlante elegible en la acción "sonar parlante" de las automatizaciones.</summary>
public sealed record WorkflowSpeakerDto(int Id, string Name, string? GroupName, bool Enabled,
    bool SupportsLibrary, bool SupportsTts, bool SupportsLiveAudio);

/// <summary>Grupo de parlantes con sus integrantes.</summary>
public sealed record SpeakerGroupDto(int Id, string Name, string? Description, IReadOnlyList<int> SpeakerIds);

/// <summary>Alta o modificación de un grupo de parlantes.</summary>
public sealed record SpeakerGroupWriteDto(string Name, string? Description, List<int> SpeakerIds);
