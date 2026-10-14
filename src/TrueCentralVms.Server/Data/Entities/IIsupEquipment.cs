namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Equipo que puede conectarse por ISUP: en vez de que el servidor lo alcance por su IP (que detrás de NAT, 4G o MPLS no existe),
/// el equipo se registra contra el servidor con su ID ISUP y su clave, y todo lo demás viaja por esa conexión. Con
/// <see cref="IsupDeviceId"/> en null el equipo se conecta por IP como siempre. La clave va cifrada como las contraseñas
/// (<see cref="CredentialProtector"/>).
/// </summary>
public interface IIsupEquipment
{
    int Id { get; }
    string Name { get; }
    string DriverKey { get; }
    bool Enabled { get; }
    /// <summary>ID ISUP («ID del dispositivo» en el menú Acceso a plataforma del equipo). Null = conexión por IP.</summary>
    string? IsupDeviceId { get; set; }
    /// <summary>Clave ISUP («clave de cifrado» del equipo), cifrada.</summary>
    byte[]? IsupKeyCiphertext { get; set; }
}
