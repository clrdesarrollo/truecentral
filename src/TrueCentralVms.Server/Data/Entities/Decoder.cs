namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Decodificador de muro de video (ej. Hikvision DS-6908UDI). Igual que los
/// dispositivos, la contraseña se guarda cifrada con AES-256-GCM (ver
/// <see cref="CredentialProtector"/>); el modelo lo reporta el propio equipo
/// al probar la conexión.
/// </summary>
public class Decoder
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Clave del driver de decodificación que lo controla (ej. "hikvision-netsdk").</summary>
    public string DriverKey { get; set; } = "hikvision-netsdk";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 8000;
    public string Username { get; set; } = "";
    public byte[] PasswordCiphertext { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public string? Model { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<VideoWall> Walls { get; set; } = [];
}
