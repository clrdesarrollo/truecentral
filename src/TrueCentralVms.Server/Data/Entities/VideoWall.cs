namespace TrueCentralVms.Server.Data.Entities;

/// <summary>Muro de video lógico: una grilla de filas × columnas alimentada por un decodificador.</summary>
public class VideoWall
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int DecoderId { get; set; }
    public Decoder? Decoder { get; set; }
    public int Rows { get; set; } = 1;
    public int Columns { get; set; } = 1;
    public List<WallScreen> Screens { get; set; } = [];
    public List<WallLayoutPreset> Layouts { get; set; } = [];
    public List<WallFloatingWindow> Floating { get; set; } = [];

    /// <summary>
    /// Ventana cuya cámara está agrandada cubriendo TODO el muro (todas las
    /// pantallas como una sola). Null = ninguna.
    /// </summary>
    public int? FullscreenWindowId { get; set; }
}

/// <summary>
/// Una pantalla física del muro: posición (fila, columna), a qué salida del
/// decodificador está conectada y en cuántas sub-ventanas está dividida.
/// </summary>
public class WallScreen
{
    public int Id { get; set; }
    public int VideoWallId { get; set; }
    public VideoWall? VideoWall { get; set; }
    public int Row { get; set; }
    public int Col { get; set; }
    /// <summary>Etiqueta de la salida física, ej. "HDMI 1".</summary>
    public string Label { get; set; } = "";
    /// <summary>Número de canal de display del decodificador (según capacidades del SDK).</summary>
    public int DisplayChannel { get; set; }
    /// <summary>Cantidad de sub-ventanas de la salida (1, 4, 6, 9, 12, 16, ...).</summary>
    public int WindowMode { get; set; } = 1;
    public List<ScreenWindow> Windows { get; set; } = [];
    /// <summary>
    /// Cuando no es null, la pantalla está en "pantalla completa": es el Id de
    /// la <see cref="ScreenWindow"/> agrandada para cubrir todo el monitor. El
    /// layout no cambia (las demás ventanas siguen decodificando por debajo);
    /// al salir la ventana vuelve a su sub-celda.
    /// </summary>
    public int? FullscreenWindowId { get; set; }
}

/// <summary>
/// Una sub-ventana dentro de una pantalla. Cada ventana consume un canal de
/// decodificación del decoder (asignado automáticamente por el servidor) y
/// puede reproducir un canal del inventario de dispositivos del VMS.
/// </summary>
public class ScreenWindow
{
    public int Id { get; set; }
    public int WallScreenId { get; set; }
    public WallScreen? WallScreen { get; set; }
    /// <summary>Índice de la ventana dentro de la pantalla (0-based, orden de lectura).
    /// Con agrupación es la casilla superior-izquierda que ocupa en la grilla base.</summary>
    public int WindowIndex { get; set; }
    /// <summary>Canal de decodificación del decoder que alimenta esta ventana.</summary>
    public int DecodeChannel { get; set; }
    /// <summary>Casillas de la grilla base que abarca en horizontal (ventanas agrupadas).</summary>
    public int SpanCols { get; set; } = 1;
    /// <summary>Casillas de la grilla base que abarca en vertical (ventanas agrupadas).</summary>
    public int SpanRows { get; set; } = 1;

    // Estado actual (persistido para sobrevivir reinicios del servidor). El
    // canal asignado es una fila de Channels: si el dispositivo se elimina, la
    // referencia queda en null (la ventana se ve vacía).
    public int? AssignedChannelId { get; set; }
    public Channel? AssignedChannel { get; set; }
    /// <summary>0 = stream principal, 1 = substream.</summary>
    public int AssignedStreamType { get; set; }

    /// <summary>
    /// Fuente externa por URL (proyección de la pantalla de un operador).
    /// Excluyente con <see cref="AssignedChannelId"/>: asignar una limpia la
    /// otra. No sale del inventario, así que se guarda la URL tal cual.
    /// </summary>
    public string? ExternalUrl { get; set; }
    /// <summary>Nombre para mostrar de la fuente externa (ej. "Pantalla - PC-OP1").</summary>
    public string? ExternalLabel { get; set; }
}

/// <summary>
/// Ventana flotante del muro: rect libre en unidades de celda (1.0 = un
/// monitor; puede cruzar monitores) dibujada ENCIMA del mosaico. Consume un
/// canal de decodificación propio y siempre tiene una cámara asignada.
/// </summary>
public class WallFloatingWindow
{
    public int Id { get; set; }
    public int VideoWallId { get; set; }
    public VideoWall? VideoWall { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 1;
    public double H { get; set; } = 1;
    public int DecodeChannel { get; set; }
    public int? AssignedChannelId { get; set; }
    public Channel? AssignedChannel { get; set; }
    public int AssignedStreamType { get; set; }

    /// <summary>Fuente externa por URL (proyección), excluyente con el canal.</summary>
    public string? ExternalUrl { get; set; }
    public string? ExternalLabel { get; set; }

    // Rect original guardado mientras la flotante está en pantalla completa
    // (doble clic). Null = tamaño normal; los cuatro se fijan y limpian juntos.
    public double? HomeX { get; set; }
    public double? HomeY { get; set; }
    public double? HomeW { get; set; }
    public double? HomeH { get; set; }
}
