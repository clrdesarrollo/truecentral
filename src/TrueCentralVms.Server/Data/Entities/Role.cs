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

    public bool IsAdmin => SystemKey == AdminKey;
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
