namespace TrueCentralVms.Core.Contracts;

// DTOs del módulo Muro de video (decodificadores, muros, ventanas y layouts).
//
// Diferencia clave con el producto vwcontroller del que provienen: aquí las
// ventanas NO se asignan a un registro de fuentes propio, sino a los CANALES
// del inventario de dispositivos del VMS (Devices/Channels). La identidad de
// una asignación es, por lo tanto, un único ChannelId.

// ---------------------------------------------------------------------------
// Decodificadores
// ---------------------------------------------------------------------------

/// <summary>Decodificador de muro tal como lo ven el panel y el cliente. NUNCA incluye la contraseña.</summary>
public sealed record DecoderDto(
    int Id, string Name, string DriverKey, string Host, int Port,
    string Username, bool Enabled, string? Model, string? Notes, DateTime CreatedAt);

/// <summary>Alta/edición de un decodificador. En edición, Password null o vacía = mantener la actual.</summary>
public sealed record DecoderWriteDto(
    string Name, string DriverKey, string Host, int Port,
    string Username, string? Password, bool Enabled, string? Notes);

/// <summary>Driver de decodificación disponible (combo del panel).</summary>
public sealed record DecoderDriverDto(string DriverKey, string DisplayName);

/// <summary>Salida física de video de un decodificador (HDMI/VGA/BNC/DVI).</summary>
public sealed record DisplayOutputDto(string Type, int Index, int ChannelNo, string Label, IReadOnlyList<int> WindowModes);

/// <summary>Capacidades leídas del equipo con "Probar conexión".</summary>
public sealed record DecoderCapabilitiesDto(
    int DecodeChannelStart, int DecodeChannelCount,
    IReadOnlyList<DisplayOutputDto> Displays,
    string? Model, string? SerialNumber);

/// <summary>Resultado de probar un decodificador (no persiste nada salvo modelo/serie).</summary>
public sealed record DecoderProbeResultDto(bool Success, string? Error, DecoderCapabilitiesDto? Capabilities);

// ---------------------------------------------------------------------------
// Muros
// ---------------------------------------------------------------------------

/// <summary>Configuración de una pantalla física al crear/editar un muro.</summary>
public sealed record ScreenConfigDto(int Row, int Col, string Label, int DisplayChannel, int WindowMode);

public sealed record WallWriteDto(string Name, int DecoderId, int Rows, int Columns, List<ScreenConfigDto> Screens);

/// <summary>
/// Lo que está reproduciendo una ventana del muro. Normalmente es un canal del
/// inventario del VMS (con sus datos resueltos para que el cliente no consulte
/// el árbol de dispositivos). Cuando <paramref name="ExternalUrl"/> no es null
/// se trata de una fuente externa por URL —hoy, la proyección de la pantalla
/// del operador—: ahí ChannelId/DeviceId van en 0 y ChannelName trae la
/// etiqueta para mostrar.
/// </summary>
public sealed record AssignmentDto(
    int ChannelId, int DeviceId, string DeviceName, int ChannelNumber, string ChannelName, int StreamType,
    string? ExternalUrl = null)
{
    public bool IsExternal => ExternalUrl is not null;
}

public sealed record WindowDto(
    int Id, int WindowIndex, int DecodeChannel, int SpanCols, int SpanRows, AssignmentDto? Assignment);

public sealed record ScreenDto(
    int Id, int Row, int Col, string Label, int DisplayChannel, int WindowMode,
    int? FullscreenWindowId, IReadOnlyList<WindowDto> Windows)
{
    public bool Fullscreen => FullscreenWindowId is not null;
}

/// <summary>Ventana flotante (rect libre encima del mosaico, en unidades de celda del muro).</summary>
public sealed record FloatingWindowDto(
    int Id, double X, double Y, double W, double H, int DecodeChannel, AssignmentDto? Assignment,
    bool Fullscreen = false);

public sealed record WallDto(
    int Id, string Name, int DecoderId, string DecoderName, string DriverKey,
    int Rows, int Columns, IReadOnlyList<ScreenDto> Screens,
    IReadOnlyList<FloatingWindowDto>? Floating = null,
    int? FullscreenWindowId = null)
{
    /// <summary>Una cámara está ocupando TODO el muro (todas las pantallas como una sola).</summary>
    public bool Fullscreen => FullscreenWindowId is not null;
}

// ---------------------------------------------------------------------------
// Operación
// ---------------------------------------------------------------------------

public sealed record AssignRequest(int WindowId, int ChannelId, int StreamType);

/// <summary>
/// Pone una fuente externa por URL en una ventana (proyección de la pantalla
/// del operador: su PC publica un RTSP y el decodificador lo consume).
/// </summary>
public sealed record ExternalAssignRequest(int WindowId, string Url, string Label);
public sealed record ClearRequest(int WindowId);
public sealed record SwapRequest(int WindowAId, int WindowBId);
public sealed record GroupRequest(List<int> WindowIds);

/// <summary>Subdividir UNA ventana del mosaico en 4/9/16 sub-ventanas.</summary>
public sealed record SubdivideRequest(int Parts);

/// <summary>Crear una ventana flotante dibujada sobre el muro (rect en unidades de celda).</summary>
public sealed record FloatingCreateRequest(double X, double Y, double W, double H, int ChannelId, int StreamType);

/// <summary>Mover/redimensionar una ventana flotante.</summary>
public sealed record FloatingMoveRequest(double X, double Y, double W, double H);

/// <summary>Cambiar la cámara de una ventana flotante.</summary>
public sealed record FloatingAssignRequest(int ChannelId, int StreamType);

/// <summary>Crear una ventana flotante que muestra una fuente externa por URL.</summary>
public sealed record FloatingExternalCreateRequest(double X, double Y, double W, double H, string Url, string Label);

/// <summary>Poner una fuente externa por URL en una ventana flotante existente.</summary>
public sealed record FloatingExternalAssignRequest(string Url, string Label);

/// <summary>Cambio de división (cantidad de ventanas) de un monitor.</summary>
public sealed record ScreenWindowModeRequest(int WindowMode);

/// <summary>Poner una ventana en pantalla completa (doble clic en el cliente).</summary>
public sealed record FullscreenRequest(int WindowId);

/// <summary>Resultado de operaciones contra el decoder que pueden fallar por pantalla.</summary>
public sealed record OperationResultDto(bool Success, string? Error);

// ---------------------------------------------------------------------------
// Layouts guardados
// ---------------------------------------------------------------------------

public sealed record LayoutItemDto(int Row, int Col, int WindowIndex, int ChannelId, int StreamType,
    int SpanCols = 1, int SpanRows = 1);

public sealed record LayoutScreenDto(int Row, int Col, int WindowMode);

public sealed record LayoutPresetDto(
    int Id, string Name, DateTime CreatedAt,
    IReadOnlyList<LayoutScreenDto> Screens, IReadOnlyList<LayoutItemDto> Items);

/// <summary>Guardar layout: si Screens/Items son null, se toma una foto del estado actual del muro.</summary>
public sealed record LayoutSaveRequest(string Name, List<LayoutScreenDto>? Screens, List<LayoutItemDto>? Items);
