namespace TrueCentralVms.Core.Contracts;

// ---------------------------------------------------------------------------
// Roles y permisos
// ---------------------------------------------------------------------------

/// <summary>Un permiso del catálogo (ver <see cref="Domain.Permissions"/>).</summary>
public sealed record PermissionDto(string Key, string Group, string Label, string Description,
    IReadOnlyList<string> Requires, bool Sensitive);

/// <summary>Punto de partida para un rol nuevo (se copia al formulario; no se guarda).</summary>
public sealed record RoleTemplateDto(string Key, string Name, string Description, IReadOnlyList<string> Permissions);

/// <summary>Catálogo para el editor de roles, con lo que puede otorgar quien lo pide.</summary>
/// <param name="Grantable">Permisos que esta sesión puede otorgar (los suyos): el resto se muestra bloqueado.</param>
public sealed record PermissionCatalogDto(IReadOnlyList<string> Groups, IReadOnlyList<PermissionDto> Permissions,
    IReadOnlyList<RoleTemplateDto> Templates, IReadOnlyList<string> Grantable);

/// <param name="SystemKey">"admin" / "operator" en los de sistema; null en los creados.</param>
/// <param name="Editable">Esta sesión puede modificarlo (no es el Administrador y no tiene permisos que ella no tenga).</param>
/// <param name="Assignable">Esta sesión puede asignarlo a un usuario (mismas reglas).</param>
public sealed record RoleDto(int Id, string Name, string Description, string? SystemKey,
    IReadOnlyList<string> Permissions, int UserCount, IReadOnlyList<string> Users,
    bool Editable, bool Assignable, DateTime UpdatedAt);

public sealed record RoleWriteDto(string Name, string? Description, IReadOnlyList<string>? Permissions);

/// <summary>Lo que puede hacer la sesión: la interfaz oculta lo demás (el servidor valida igual).</summary>
public sealed record MyPermissionsDto(bool IsAdmin, IReadOnlyList<string> Permissions, IReadOnlyList<string> Roles);
