namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Historial de contraseñas por usuario. Toda clave nueva se compara contra
/// TODAS las filas del usuario: ninguna contraseña se puede reutilizar.
/// </summary>
public class PasswordHistory
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
