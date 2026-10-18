namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Dato interno del sistema guardado como texto opaco por nombre. Hoy lo usa el
/// licenciamiento para una de las tres copias de su estado (cifrada y
/// autenticada, ver <see cref="Services.Licensing.LicenseStateVault"/>): no se
/// edita ni se muestra en ninguna pantalla.
/// </summary>
public class SystemBlob
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Data { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
