namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Rol: un conjunto de permisos (<see cref="Core.Domain.Permissions"/>) con
/// nombre. Un usuario puede tener varios y obtiene la unión de sus permisos.
/// Los de sistema (<see cref="SystemKey"/>) no se borran; el Administrador
/// además no se edita (tiene todos los permisos, también los futuros).
/// </summary>
public class Role
{
    public const string AdminKey = "admin";
    public const string OperatorKey = "operator";

    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>"admin" / "operator" para los de sistema; null para los creados por un usuario.</summary>
    public string? SystemKey { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<RolePermission> Permissions { get; set; } = [];

    /// <summary>
    /// DÓNDE valen sus permisos. false = en todo el sistema (como siempre);
    /// true = solo en <see cref="Locations"/> (cada una con sus sububicaciones)
    /// y en los recursos sueltos de <see cref="Resources"/>. Un usuario suma lo
    /// de todos sus roles: basta un rol sin límite para que vea todo.
    /// </summary>
    public bool RestrictScope { get; set; }
    /// <summary>Con alcance limitado: VE el resto, sin operarlo (supervisión).</summary>
    public bool ViewOutsideScope { get; set; }
    public List<RoleLocation> Locations { get; set; } = [];
    public List<RoleResource> Resources { get; set; } = [];

    public bool IsAdmin => SystemKey == AdminKey;
}

/// <summary>Ubicación del alcance de un rol (incluye sus sububicaciones).</summary>
public class RoleLocation
{
    public int RoleId { get; set; }
    public int LocationId { get; set; }
}

/// <summary>
/// Recurso suelto del alcance de un rol (Core.Contracts.ScopeKinds): una
/// cámara, puerta, área, zona, cerco, parlante o citófono por su id de fila, o
/// un equipo completo. Sin clave foránea (apunta a tablas distintas): lo que
/// ya no existe simplemente no cuenta.
/// </summary>
public class RoleResource
{
    public int RoleId { get; set; }
    public string Kind { get; set; } = "";
    public int ResourceId { get; set; }
}

/// <summary>Un permiso otorgado por un rol (clave del catálogo).</summary>
public class RolePermission
{
    public int RoleId { get; set; }
    public string Permission { get; set; } = "";
}

/// <summary>Rol asignado a un usuario.</summary>
public class UserRole
{
    public int UserId { get; set; }
    public int RoleId { get; set; }
}
