using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Nodo del árbol de ubicaciones, la capa lógica del inventario: sitio,
/// edificio, piso, sector o punto. Los recursos (canales, puertas, áreas y
/// zonas de alarma, cercos, parlantes, citófonos) apuntan aquí con su propio
/// <c>LocationId</c>, así que cada recurso está en una sola ubicación. Borrar
/// una ubicación deja sus recursos "por ubicar" (FK SET NULL), nunca los borra.
/// </summary>
public class Location
{
    public int Id { get; set; }

    /// <summary>Ubicación que la contiene; null = raíz del árbol.</summary>
    public int? ParentId { get; set; }
    public Location? Parent { get; set; }

    public string Name { get; set; } = "";
    public LocationKind Kind { get; set; } = LocationKind.Sector;
    public string? Description { get; set; }

    /// <summary>Dirección postal (pensada para el sitio, admitida en cualquiera).</summary>
    public string? Address { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<Location> Children { get; set; } = [];
}

/// <summary>
/// Entidad que es un recurso ubicable (canal, puerta, área, zona, cerco,
/// parlante, citófono): permite filtrar y reubicar cualquiera de ellas con el
/// mismo código (ver <c>Services.ResourceCatalog</c>).
/// </summary>
public interface ILocatable
{
    int Id { get; }
    int? LocationId { get; set; }
}
