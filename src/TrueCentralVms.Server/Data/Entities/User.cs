namespace TrueCentralVms.Server.Data.Entities;

/// <summary>Usuario del sistema (panel web y cliente de escritorio).</summary>
public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    /// <summary>Rol según <see cref="Core.Domain.Roles"/> (Admin / Operator).</summary>
    public string Role { get; set; } = Core.Domain.Roles.Operator;
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Última vez que se estableció la contraseña (para la caducidad configurable).</summary>
    public DateTime PasswordChangedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Alcance por ubicación (solo operadores: un administrador siempre ve y
    /// opera todo). false = todas las ubicaciones; true = solo las de
    /// <see cref="Locations"/>, cada una con sus sububicaciones. Los recursos
    /// "por ubicar" quedan fuera de un alcance restringido.
    /// </summary>
    public bool RestrictToLocations { get; set; }
    /// <summary>Con alcance restringido: puede VER el resto, sin operarlo (supervisión).</summary>
    public bool ViewOutsideScope { get; set; }

    public List<PasswordHistory> PasswordHistories { get; set; } = [];
    public List<UserLocation> Locations { get; set; } = [];
}

/// <summary>Ubicación asignada al alcance de un usuario (incluye sus sububicaciones).</summary>
public class UserLocation
{
    public int UserId { get; set; }
    public int LocationId { get; set; }
}
