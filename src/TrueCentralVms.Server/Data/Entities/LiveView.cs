namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Tablero del Centro de mando (antes "vista guardada"): la división de la
/// grilla y lo que hay en cada cuadro, para que el operador vuelva a armar su
/// pantalla de siempre con un clic. Vive en el servidor y no en el puesto, así
/// acompaña al usuario a cualquier estación. Compartido, lo ven todos los
/// puestos o solo los usuarios de <see cref="Roles"/>.
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
    /// <summary>Compartido: lo ven todos (o los de <see cref="Roles"/>) y lo cambia
    /// solo quien administra los tableros compartidos.</summary>
    public bool Shared { get; set; }
    /// <summary>División de la grilla: nombre estándar ("16") o a medida ("8×5").</summary>
    public string LayoutName { get; set; } = "4";
    public int Columns { get; set; } = 2;
    public int Rows { get; set; } = 2;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<LiveViewItem> Items { get; set; } = [];
    /// <summary>Compartido solo con los usuarios de <see cref="Roles"/> (false = con todos).
    /// Si se borran todos sus roles, lo ve solo quien administra los tableros.</summary>
    public bool RestrictToRoles { get; set; }
    public List<LiveViewRole> Roles { get; set; } = [];
}

/// <summary>Rol con el que se comparte un tablero.</summary>
public class LiveViewRole
{
    public int LiveViewId { get; set; }
    public int RoleId { get; set; }
}

/// <summary>Lo que hay en un cuadro del tablero: una cámara, un recurso, una
/// tarjeta de reporte o una página web (<see cref="Kind"/>).</summary>
public class LiveViewItem
{
    public int Id { get; set; }
    public int LiveViewId { get; set; }
    public LiveView? View { get; set; }
    /// <summary>Índice del cuadro en la división (0 = el primero).</summary>
    public int CellIndex { get; set; }
    /// <summary>"camera", "resource", "card" o "web" (<c>DashboardItemKinds</c>).</summary>
    public string Kind { get; set; } = "camera";
    /// <summary>Canal de la cámara (solo si Kind = camera).</summary>
    public int? ChannelId { get; set; }
    public Channel? Channel { get; set; }
    /// <summary>0 = principal, 1 = secundario.</summary>
    public int StreamType { get; set; }
    /// <summary>Id del recurso o de la página web de la lista.</summary>
    public int? RefId { get; set; }
    /// <summary>Tipo del recurso, clave de la tarjeta o dirección web libre.</summary>
    public string? Ref { get; set; }
    /// <summary>Rótulo del cuadro (el de una página web).</summary>
    public string? Title { get; set; }
}

/// <summary>
/// Página web de la lista del servidor (mapa, noticias, un tablero externo…):
/// la puede poner en un cuadro cualquiera que vea video. Escribir una dirección
/// libre pide además "Abrir cualquier página web".
/// </summary>
public class WebLink
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
