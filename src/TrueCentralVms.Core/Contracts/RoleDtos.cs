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
/// <param name="RestrictScope">El rol limita DÓNDE se usan sus permisos: solo en
/// <paramref name="LocationIds"/> (cada una con sus sububicaciones) y en los
/// recursos sueltos de <paramref name="Items"/>. false = en todo el sistema.</param>
/// <param name="ViewOutsideScope">Con alcance limitado: VE el resto, sin operarlo.</param>
/// <param name="ScopeSummary">El alcance en palabras ("Todo el sistema", "Sucursal Norte, 3 recursos…").</param>
public sealed record RoleDto(int Id, string Name, string Description, string? SystemKey,
    IReadOnlyList<string> Permissions, int UserCount, IReadOnlyList<string> Users,
    bool Editable, bool Assignable, DateTime UpdatedAt,
    bool RestrictScope = false, bool ViewOutsideScope = false, IReadOnlyList<int>? LocationIds = null,
    IReadOnlyList<ScopeItemDto>? Items = null, string ScopeSummary = "");

/// <summary>Alta o modificación de un rol. Los campos del alcance en null = no
/// cambiar (un panel anterior no lo borra).</summary>
public sealed record RoleWriteDto(string Name, string? Description, IReadOnlyList<string>? Permissions,
    bool? RestrictScope = null, bool? ViewOutsideScope = null, IReadOnlyList<int>? LocationIds = null,
    IReadOnlyList<ScopeItemDto>? Items = null);

/// <summary>
/// Un recurso suelto del alcance de un rol (ver <see cref="ScopeKinds"/>): una
/// cámara, una puerta, un área… o un equipo completo (todos sus canales,
/// puertas o áreas, también los que se le agreguen después).
/// </summary>
/// <param name="Name">Solo de lectura: el nombre al momento de consultarlo.</param>
public sealed record ScopeItemDto(string Kind, int Id, string? Name = null);

/// <summary>Un recurso elegible para el alcance de un rol (catálogo del editor).</summary>
/// <param name="Equipment">Equipo al que pertenece (vacío en los equipos y en los recursos sin equipo).</param>
/// <param name="Location">Ruta de su ubicación (null = por ubicar).</param>
public sealed record ScopeCatalogItemDto(string Kind, int Id, string Name, string? Equipment, int? LocationId, string? Location);

/// <summary>
/// Tipos de recurso del alcance de un rol. Los primeros coinciden con
/// <see cref="ResourceKind"/> (claves de Recursos); los de equipo abarcan todo
/// lo que el equipo tenga, incluso lo que se le agregue después.
/// </summary>
public static class ScopeKinds
{
    public const string Camera = nameof(ResourceKind.Camera);
    public const string Door = nameof(ResourceKind.Door);
    public const string Partition = nameof(ResourceKind.Partition);
    public const string Zone = nameof(ResourceKind.Zone);
    public const string Fence = nameof(ResourceKind.Fence);
    public const string Speaker = nameof(ResourceKind.Speaker);
    public const string Intercom = nameof(ResourceKind.Intercom);
    /// <summary>Equipo de video (DVR, NVR, cámara): todos sus canales.</summary>
    public const string VideoDevice = "VideoDevice";
    /// <summary>Equipo de control de acceso: todas sus puertas.</summary>
    public const string AccessDevice = "AccessDevice";
    /// <summary>Panel de alarma: todas sus áreas y zonas.</summary>
    public const string AlarmPanel = "AlarmPanel";

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [VideoDevice] = "Equipo de video",
        [Camera] = "Cámara",
        [AccessDevice] = "Equipo de acceso",
        [Door] = "Puerta",
        [AlarmPanel] = "Panel de alarma",
        [Partition] = "Área de alarma",
        [Zone] = "Zona",
        [Fence] = "Cerco",
        [Speaker] = "Parlante",
        [Intercom] = "Citófono",
    };

    public static bool IsValid(string? kind) => kind is not null && Labels.ContainsKey(kind);
}

/// <summary>Lo que puede hacer la sesión: la interfaz oculta lo demás (el servidor valida igual).</summary>
public sealed record MyPermissionsDto(bool IsAdmin, IReadOnlyList<string> Permissions, IReadOnlyList<string> Roles);
