namespace TrueCentralVms.Server.Data.Entities;

/// <summary>Layout guardado (preset) de un muro, para aplicarlo de una vez.</summary>
public class WallLayoutPreset
{
    public int Id { get; set; }
    public int VideoWallId { get; set; }
    public VideoWall? VideoWall { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>División de cada monitor (por posición fila/columna).</summary>
    public List<WallLayoutScreen> Screens { get; set; } = [];
    /// <summary>Asignaciones de cámara por ventana (posición estable).</summary>
    public List<WallLayoutItem> Items { get; set; } = [];
}

/// <summary>División de un monitor guardada en un layout (por posición).</summary>
public class WallLayoutScreen
{
    public int Id { get; set; }
    public int WallLayoutPresetId { get; set; }
    public WallLayoutPreset? Preset { get; set; }
    public int Row { get; set; }
    public int Col { get; set; }
    public int WindowMode { get; set; } = 1;
}

/// <summary>
/// Cámara guardada en un layout. La ventana se identifica por su posición
/// estable (fila, columna, índice), que sobrevive a cambios de división y a
/// reinicios del servidor.
/// </summary>
public class WallLayoutItem
{
    public int Id { get; set; }
    public int WallLayoutPresetId { get; set; }
    public WallLayoutPreset? Preset { get; set; }
    public int Row { get; set; }
    public int Col { get; set; }
    public int WindowIndex { get; set; }
    /// <summary>Canal del inventario del VMS que ocupaba la ventana.</summary>
    public int ChannelId { get; set; }
    public Channel? Channel { get; set; }
    public int StreamType { get; set; }
    /// <summary>Span de la ventana en la grilla base (ventanas agrupadas).</summary>
    public int SpanCols { get; set; } = 1;
    public int SpanRows { get; set; } = 1;
}
