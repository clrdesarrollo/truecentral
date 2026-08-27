namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Auditoría de streaming: quién vio qué canal y cuándo. Sin claves foráneas a
/// propósito: el historial sobrevive a la eliminación de usuarios y
/// dispositivos (se guardan copias de los nombres).
/// </summary>
public class StreamSession
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Username { get; set; } = "";
    public int DeviceId { get; set; }
    public string DeviceName { get; set; } = "";
    public int RtspChannel { get; set; }
    /// <summary>"main" o "sub".</summary>
    public string Profile { get; set; } = "";
    public string ClientIp { get; set; } = "";
    /// <summary>Ruta de MediaMTX (ch/{deviceId}/{rtspChannel}/{perfil}).</summary>
    public string Path { get; set; } = "";
    /// <summary>Identificador de la sesión/conexión en MediaMTX (para detectar el cierre).</summary>
    public string MtxSessionId { get; set; } = "";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
}
