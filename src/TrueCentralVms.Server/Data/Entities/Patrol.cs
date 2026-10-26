using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Data.Entities;

// ---------------------------------------------------------------------------
// Control de rondas (fase 1). Las lecturas crudas de los bastones se guardan
// siempre y nunca se editan; la bitácora (pasos) se arma a partir de ellas y
// sus correcciones dejan rastro. Diseño en docs/rondas/PLAN_MODULO_RONDAS.md.

/// <summary>
/// Base de descarga: el agente que corre en el PC de la guardia, con el bastón
/// puesto en su base por USB. Se enrola con un código y se autentica con token.
/// </summary>
public class PatrolAgent : ILocatable
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? LocationId { get; set; }
    /// <summary>SHA-256 del token que se le entregó al enrolarse (el token nunca se guarda).</summary>
    public string? TokenHash { get; set; }
    public string? EnrollmentCodeHash { get; set; }
    public DateTime? EnrollmentExpiresAt { get; set; }
    public string? MachineName { get; set; }
    public string? Version { get; set; }
    public string? RemoteAddress { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Bastón de ronda, identificado por su número de captivador.</summary>
public class PatrolReader : ILocatable
{
    public int Id { get; set; }
    /// <summary>Número de captivador que informa el propio bastón (comando 0x02).</summary>
    public long Serial { get; set; }
    public string Name { get; set; } = "";
    public string DriverKey { get; set; } = "jwm-captor4";
    public PatrolReaderStatus Status { get; set; } = PatrolReaderStatus.Pending;
    public bool Enabled { get; set; } = true;
    /// <summary>Apagado por el cupo de la licencia (ver VmsDbContext.SaveChanges).</summary>
    public bool DisabledByLicense { get; set; }
    public int? LocationId { get; set; }

    /// <summary>Base donde se vio por última vez.</summary>
    public int? AgentId { get; set; }
    public PatrolAgent? Agent { get; set; }
    /// <summary>Está puesto en la base (o conectado al PC) ahora mismo.</summary>
    public bool Docked { get; set; }
    public DateTime? DockedChangedAt { get; set; }

    public DateTime? LastDownloadAt { get; set; }
    public DateTime? LastClockSetAt { get; set; }
    /// <summary>Bloques que tenía la memoria en la última descarga (cada bloque = 5 lecturas; 12 000 = llena).</summary>
    public int? LastMemoryBlocks { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Orden del panel para una base: leer (y quizás vaciar) un bastón ahora.</summary>
public class PatrolAgentCommandRow
{
    public long Id { get; set; }
    public int AgentId { get; set; }
    public int ReaderId { get; set; }
    /// <summary>"read" o "read-erase".</summary>
    public string Kind { get; set; } = "read";
    public int? RequestedByUserId { get; set; }
    public string? RequestedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool? Success { get; set; }
    public string? Message { get; set; }
}

/// <summary>Punto de control de ronda.</summary>
public class PatrolCheckpoint : ILocatable
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public PatrolCheckpointKind Kind { get; set; } = PatrolCheckpointKind.Tag;
    /// <summary>Tag fijo que lee el bastón (10 caracteres, hex en mayúsculas).</summary>
    public string? TagId { get; set; }
    /// <summary>Punto en un terminal de acceso (fase 4: el paso llega en línea).</summary>
    public int? AccessDoorId { get; set; }
    public int? LocationId { get; set; }
    public bool Enabled { get; set; } = true;
    public bool DisabledByLicense { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Rol «guardia» de una persona del padrón. La persona es la misma del control
/// de acceso (sus huellas, tarjetas y niveles); aquí solo va lo de rondas.
/// </summary>
public class PatrolGuard
{
    public int Id { get; set; }
    public int AccessPersonId { get; set; }
    public AccessPerson? AccessPerson { get; set; }
    public bool Active { get; set; } = true;
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeactivatedAt { get; set; }
    public string? DeactivatedBy { get; set; }
    public string? DeactivationReason { get; set; }
    public List<PatrolTag> Tags { get; set; } = [];
}

/// <summary>
/// Llavero de ronda: credencial propia del módulo (no es una tarjeta de acceso:
/// no se envía a los terminales). Al desvincular al guardia queda libre.
/// </summary>
public class PatrolTag
{
    public int Id { get; set; }
    public string TagId { get; set; } = "";
    public string? Label { get; set; }
    public int? PatrolGuardId { get; set; }
    public PatrolGuard? PatrolGuard { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime? AssignedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Una descarga de un bastón (lote de lecturas).</summary>
public class PatrolDownload
{
    public long Id { get; set; }
    public int ReaderId { get; set; }
    public int? AgentId { get; set; }
    public PatrolDownloadOrigin Origin { get; set; }
    /// <summary>Usuario (lectura a pedido) o base que la hizo, para la bitácora.</summary>
    public string? Actor { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public int Blocks { get; set; }
    public int Reads { get; set; }
    public int NewReads { get; set; }
    public int NewPasses { get; set; }
    public bool ClockSet { get; set; }
    public bool Erased { get; set; }
    public PatrolDownloadStatus Status { get; set; } = PatrolDownloadStatus.Running;
    public string? Error { get; set; }
}

/// <summary>
/// Lectura cruda tal como la entregó el bastón. Nunca se edita ni se borra; la
/// clave única (bastón, tag, hora) evita duplicados entre descargas.
/// </summary>
public class PatrolRawRead
{
    public long Id { get; set; }
    public int ReaderId { get; set; }
    public string TagId { get; set; } = "";
    /// <summary>Hora de la lectura en UTC (el bastón guarda hora local del servidor, que lo puso en hora).</summary>
    public DateTime ReadAt { get; set; }
    public long DownloadId { get; set; }
}

/// <summary>Paso de un guardia por un punto: lo que muestra la bitácora.</summary>
public class PatrolPass
{
    public long Id { get; set; }
    public int CheckpointId { get; set; }
    public int? PatrolGuardId { get; set; }
    public int? PatrolTagId { get; set; }
    /// <summary>Copia del nombre al momento del paso: el historial sobrevive al borrado de la persona.</summary>
    public string? GuardName { get; set; }
    public string? GuardEmployeeNo { get; set; }
    public int? ReaderId { get; set; }
    public DateTime ReadAt { get; set; }
    public long? CheckpointReadId { get; set; }
    public long? GuardReadId { get; set; }
    public PatrolPassSource Source { get; set; } = PatrolPassSource.Reader;
    public bool Voided { get; set; }
    public string? VoidReason { get; set; }
    public string? EditedBy { get; set; }
    public DateTime? EditedAt { get; set; }
    public string? EditReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
