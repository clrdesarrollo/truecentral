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

    public List<PasswordHistory> PasswordHistories { get; set; } = [];
}
