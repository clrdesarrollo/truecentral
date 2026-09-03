namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Bitácora de auditoría (ISO 27001): quién hizo qué, cuándo, desde dónde y
/// sobre qué objeto. Es SOLO-AGREGAR: no existe API para modificar ni borrar
/// filas (la única salida es la purga por retención configurada). Sin claves
/// foráneas a propósito: el historial sobrevive a la eliminación de usuarios,
/// dispositivos y muros (se guardan copias de los nombres).
/// </summary>
public class AuditEvent
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Null en eventos del sistema o intentos de login fallidos.</summary>
    public int? UserId { get; set; }
    /// <summary>Nombre del actor; en logins fallidos, el nombre intentado.</summary>
    public string Username { get; set; } = "";
    public string? Role { get; set; }

    /// <summary>Desde dónde se actuó: "web" (panel), "client" (escritorio) o "server".</summary>
    public string Origin { get; set; } = "";
    public string ClientIp { get; set; } = "";

    /// <summary>Categoría estable (ver <c>AuditCatalog</c>): auth, users, devices, ...</summary>
    public string Category { get; set; } = "";
    /// <summary>Subcategoría/acción estable dentro de la categoría.</summary>
    public string Action { get; set; } = "";

    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? TargetName { get; set; }

    /// <summary>Resumen legible en español (aparece tal cual en el panel).</summary>
    public string? Detail { get; set; }

    /// <summary>false = la acción se intentó y fue rechazada o falló.</summary>
    public bool Success { get; set; } = true;

    /// <summary>Datos estructurados adicionales (JSON), p. ej. rutas de archivo o rangos.</summary>
    public string? DataJson { get; set; }
}
