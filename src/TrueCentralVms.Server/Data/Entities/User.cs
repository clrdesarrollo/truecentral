namespace TrueCentralVms.Server.Data.Entities;

/// <summary>Usuario del sistema (panel web y cliente de escritorio).</summary>
public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    /// <summary>
    /// Nivel según <see cref="Core.Domain.Roles"/>: Admin si tiene el rol de
    /// sistema Administrador, si no Operator. Se deriva de <see cref="Roles"/>
    /// al guardarlos (lo usan la sesión y la bitácora); los permisos salen de los roles.
    /// </summary>
    public string Role { get; set; } = Core.Domain.Roles.Operator;
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Superadministrador: el administrador que se crea al activar la plataforma.
    /// Ve y opera todo sin importar sus roles ni su alcance, solo él modifica su
    /// usuario (nombre y contraseña), no se deshabilita y no se elimina.
    /// </summary>
    public bool IsSuperAdmin { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Última vez que se estableció la contraseña (para la caducidad configurable).</summary>
    public DateTime PasswordChangedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Límite PROPIO por ubicación, además del alcance de sus roles (solo
    /// operadores: un administrador siempre ve y opera todo). false = sin límite
    /// propio (manda lo de sus roles); true = de lo que le dan sus roles, solo lo
    /// que esté en <see cref="Locations"/>, cada una con sus sububicaciones. Los
    /// recursos "por ubicar" quedan fuera de un límite propio.
    /// </summary>
    public bool RestrictToLocations { get; set; }
    /// <summary>Con alcance restringido: puede VER el resto, sin operarlo (supervisión).</summary>
    public bool ViewOutsideScope { get; set; }

    public List<PasswordHistory> PasswordHistories { get; set; } = [];
    public List<UserLocation> Locations { get; set; } = [];
    public List<UserRole> Roles { get; set; } = [];
}

/// <summary>Ubicación asignada al alcance de un usuario (incluye sus sububicaciones).</summary>
public class UserLocation
{
    public int UserId { get; set; }
    public int LocationId { get; set; }
}
