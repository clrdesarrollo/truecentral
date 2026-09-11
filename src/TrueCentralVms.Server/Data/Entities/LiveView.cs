namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Vista guardada del monitoreo en vivo (equivale a la "Custom View" de
/// iVMS-4200): la división de la grilla y el canal de cada cuadro, para que el
/// operador vuelva a armar su pantalla de siempre con un clic. Vive en el
/// servidor y no en el puesto, así la vista acompaña al usuario a cualquier
/// estación; marcada como compartida la ven todos los puestos.
/// </summary>
public class LiveView
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Dueño de la vista (quien la guardó). Null solo si la cuenta se borró.</summary>
    public int? OwnerUserId { get; set; }
    public User? OwnerUser { get; set; }
    /// <summary>Nombre del dueño congelado: la vista sobrevive a la cuenta.</summary>
    public string OwnerName { get; set; } = "";
    /// <summary>Visible para todos los puestos, no solo para su dueño.</summary>
    public bool Shared { get; set; }
    /// <summary>División de la grilla: nombre estándar ("16") o a medida ("8×5").</summary>
    public string LayoutName { get; set; } = "4";
    public int Columns { get; set; } = 2;
    public int Rows { get; set; } = 2;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<LiveViewItem> Items { get; set; } = [];
}

/// <summary>Cámara guardada en un cuadro de la vista (posición en la grilla).</summary>
public class LiveViewItem
{
    public int Id { get; set; }
    public int LiveViewId { get; set; }
    public LiveView? View { get; set; }
    /// <summary>Índice del cuadro en la división (0 = el primero).</summary>
    public int CellIndex { get; set; }
    public int ChannelId { get; set; }
    public Channel? Channel { get; set; }
    /// <summary>0 = principal, 1 = secundario.</summary>
    public int StreamType { get; set; }
}
