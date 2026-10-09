using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Core.Contracts;

// DTOs del módulo Citofonía (frentes de videoportero: el visitante toca el
// timbre, la llamada suena en los clientes del VMS y el guardia contesta con
// video, voz bidireccional y apertura de puerta). Los enums viajan como texto.

/// <summary>Estado de conexión del servidor con el frente.</summary>
public enum IntercomStatus { Unknown, Online, Offline, AuthFailed }

/// <summary>Etapa de una llamada de citofonía.</summary>
public enum IntercomCallState
{
    /// <summary>Sonando en la central, nadie contestó todavía.</summary>
    Ringing,
    /// <summary>Un operador contestó y está hablando.</summary>
    InCall,
    /// <summary>Contestada y terminada.</summary>
    Completed,
    /// <summary>Nadie contestó (el visitante cortó o se cumplió el tiempo de timbre).</summary>
    Missed,
    /// <summary>Un operador la rechazó.</summary>
    Rejected,
}

/// <summary>Frente de citofonía tal como lo ven el panel y el cliente. NUNCA incluye la contraseña.</summary>
public sealed record IntercomDto(
    int Id,
    string Name,
    string DriverKey,
    string Host,
    /// <summary>Puerto del SDK (señalización y voz).</summary>
    int Port,
    /// <summary>Puerto HTTP (ISAPI).</summary>
    int HttpPort,
    string Username,
    string? Model,
    string? SerialNumber,
    string? FirmwareVersion,
    /// <summary>Grupo lógico (portería, acceso norte...) para ordenar la lista; libre.</summary>
    string? GroupName,
    bool Enabled,
    IntercomStatus Status,
    string? LastError,
    DateTime? LastSeenAt,
    /// <summary>Otra cámara elegida para el frente (canal de video); null = la cámara propia del frente.</summary>
    int? ChannelId,
    string? ChannelName,
    int DoorCount,
    /// <summary>El botón del frente llama a la central (sin esto las llamadas no llegan al VMS).</summary>
    bool CallCenterEnabled,
    /// <summary>La llamada en curso (sonando o en conversación), si hay.</summary>
    IntercomCallDto? ActiveCall,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    /// <summary>Ubicación en el árbol de Recursos (null = por ubicar).</summary>
    int? LocationId = null,
    /// <summary>Ruta legible de esa ubicación ("Casa matriz › Portería").</summary>
    string? Location = null,
    /// <summary>Puerto RTSP de la cámara propia del frente.</summary>
    int RtspPort = 554);

public sealed record IntercomWriteDto(
    string Name,
    string DriverKey,
    string Host,
    int Port,
    int HttpPort,
    string Username,
    /// <summary>Al editar, vacío = conservar la actual.</summary>
    string? Password,
    bool Enabled = true,
    string? GroupName = null,
    int? ChannelId = null,
    /// <summary>Configurar el botón del frente para que llame a la central al guardar.</summary>
    bool ConfigureCallCenter = true,
    /// <summary>Si el stream principal manda cuadros completos cada más de 2 s, dejarlo en 1 s al guardar.</summary>
    bool OptimizeVideo = true,
    /// <summary>Ubicación en el árbol de Recursos: null = conservar la actual (en el alta,
    /// por ubicar); 0 = por ubicar.</summary>
    int? LocationId = null,
    /// <summary>Puerto RTSP de la cámara propia del frente (554 de fábrica).</summary>
    int RtspPort = 554);

/// <summary>Concesión del video de la cámara propia de un frente (POST /api/intercoms/{id}/stream).</summary>
public sealed record IntercomStreamRequestDto(StreamProfile Profile);

/// <summary>Resultado del botón "Probar conexión" (no persiste nada).</summary>
public sealed record IntercomProbeResultDto(
    bool Success,
    string? Error,
    string? Model,
    string? SerialNumber,
    string? FirmwareVersion,
    string? DeviceName,
    int DoorCount,
    bool CallCenterEnabled,
    string? AudioCodec,
    /// <summary>Canal de un dispositivo de Fuentes de video con la misma IP (el frente registrado además como cámara: ocupa un canal que no hace falta).</summary>
    int? SuggestedChannelId,
    /// <summary>Segundos entre cuadros completos del stream principal (null = no se pudo leer).</summary>
    double? KeyFrameSeconds = null);

public sealed record IntercomDriverDto(string Key, string DisplayName, int DefaultPort, int DefaultHttpPort);

/// <summary>Una llamada de citofonía (en curso o del historial).</summary>
public sealed record IntercomCallDto(
    long Id,
    int IntercomId,
    string IntercomName,
    IntercomCallState State,
    DateTime StartedAt,
    DateTime? AnsweredAt,
    DateTime? EndedAt,
    /// <summary>Operador que contestó (o rechazó).</summary>
    string? AnsweredBy,
    int? AnsweredByUserId,
    /// <summary>Quién llama según el frente (edificio/piso/depto), si lo informa.</summary>
    string? Origin,
    /// <summary>Por qué terminó ("colgó el operador", "el visitante cortó"...).</summary>
    string? EndReason,
    bool DoorOpened,
    string? DoorOpenedBy,
    /// <summary>Otra cámara elegida para el frente; null = la propia (POST /api/intercoms/{id}/stream).</summary>
    int? ChannelId,
    int DoorCount);

/// <summary>Página del historial de llamadas.</summary>
public sealed record IntercomCallPageDto(IReadOnlyList<IntercomCallDto> Items, int Total);

/// <summary>Resultado de una orden sobre una llamada o una puerta.</summary>
public sealed record IntercomActionResultDto(bool Success, string Message, IntercomCallDto? Call = null);
