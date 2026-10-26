namespace TrueCentralVms.Core.Contracts;

// ---------------------------------------------------------------------------
// Control de rondas: bastones, bases de descarga, puntos de control, guardias
// y bitácora de pasos. Diseño en docs/rondas/PLAN_MODULO_RONDAS.md.

/// <summary>Un bastón aparece solo la primera vez que una base lo lee: queda pendiente hasta que se aprueba.</summary>
public enum PatrolReaderStatus { Pending, Approved, Rejected }

/// <summary>De dónde salió una descarga.</summary>
public enum PatrolDownloadOrigin { Dock, DockOnDemand, Browser }

public enum PatrolDownloadStatus { Running, Completed, Failed }

/// <summary>Cómo se marca un punto: con el bastón (tag) o en un terminal de acceso (en línea).</summary>
public enum PatrolCheckpointKind { Tag, Access }

/// <summary>Origen de un paso de la bitácora.</summary>
public enum PatrolPassSource { Reader, Access, Manual }

public enum PatrolAgentStatus { Pending, Online, Offline }

// --- Catálogo -------------------------------------------------------------

public sealed record PatrolCheckpointDto(
    int Id, string Name, PatrolCheckpointKind Kind, string? TagId, int? AccessDoorId,
    int? LocationId, string? Location, bool Enabled, bool DisabledByLicense, string? Notes, DateTime? LastPassAt);

public sealed record PatrolCheckpointWriteDto(
    string Name, PatrolCheckpointKind Kind, string? TagId, int? AccessDoorId, int? LocationId, bool Enabled, string? Notes);

public sealed record PatrolTagDto(
    int Id, string TagId, string? Label, bool Enabled, int? GuardId, string? GuardName, DateTime? LastReadAt);

public sealed record PatrolGuardDto(
    int Id, int AccessPersonId, string FullName, string EmployeeNo, bool Active, bool PersonEnabled,
    string? Notes, IReadOnlyList<PatrolTagDto> Tags, DateTime? LastPassAt,
    DateTime? DeactivatedAt, string? DeactivatedBy, string? DeactivationReason);

/// <summary>
/// Alta de guardia: con <paramref name="AccessPersonId"/> se marca como guardia
/// a una persona que ya existe; sin él se crea la persona (sin niveles de acceso,
/// así nunca se envía a los terminales).
/// </summary>
public sealed record PatrolGuardCreateDto(
    int? AccessPersonId, string? FirstName, string? LastName, string? EmployeeNo, string? Notes);

public sealed record PatrolGuardUpdateDto(string? Notes, IReadOnlyList<int>? TagIds);

public sealed record PatrolGuardDeactivateDto(string Reason);

public sealed record PatrolTagWriteDto(string TagId, string? Label, int? GuardId, bool Enabled);

public sealed record PatrolReaderDto(
    int Id, long Serial, string SerialText, string Name, string DriverKey, PatrolReaderStatus Status,
    bool Enabled, bool DisabledByLicense, int? LocationId, string? Location,
    int? AgentId, string? AgentName, bool Docked, DateTime? DockedChangedAt,
    DateTime? LastDownloadAt, DateTime? LastClockSetAt, int? LastMemoryBlocks, string? LastError, bool HasPendingCommand);

public sealed record PatrolReaderWriteDto(string Name, int? LocationId, bool Enabled);

public sealed record PatrolAgentDto(
    int Id, string Name, PatrolAgentStatus Status, string? MachineName, string? Version,
    int? LocationId, string? Location, DateTime? LastSeenAt, string? LastError,
    bool EnrollmentPending, DateTime? EnrollmentExpiresAt);

public sealed record PatrolAgentWriteDto(string Name, int? LocationId);

/// <summary>Código de enrolamiento: se muestra UNA vez en el panel y se escribe en el PC de la guardia.</summary>
public sealed record PatrolAgentEnrollmentDto(string Code, DateTime ExpiresAt);

// --- Bitácora --------------------------------------------------------------

public sealed record PatrolPassDto(
    long Id, DateTime ReadAt, int CheckpointId, string CheckpointName, string? Location,
    int? GuardId, string? GuardName, string? GuardEmployeeNo, string? GuardTagId,
    int? ReaderId, string? ReaderName, PatrolPassSource Source,
    bool Voided, string? VoidReason, string? EditedBy, DateTime? EditedAt, string? EditReason);

public sealed record PatrolPassPageDto(IReadOnlyList<PatrolPassDto> Items, int Total);

public sealed record PatrolPassEditDto(int? GuardId, string Reason);

public sealed record PatrolPassVoidDto(string Reason);

/// <summary>Lectura que no forma parte de ningún paso: tag desconocido, llavero sin punto o punto anulado.</summary>
public sealed record PatrolOrphanReadDto(long Id, DateTime ReadAt, string TagId, string Kind, int ReaderId, string ReaderName);

public sealed record PatrolOrphanPageDto(IReadOnlyList<PatrolOrphanReadDto> Items, int Total);

/// <summary>Tag leído por un bastón que no está dado de alta ni como punto ni como llavero.</summary>
public sealed record PatrolUnknownTagDto(string TagId, int Reads, DateTime FirstReadAt, DateTime LastReadAt, string? LastReaderName);

public sealed record PatrolDownloadDto(
    long Id, int ReaderId, string ReaderName, PatrolDownloadOrigin Origin, string? Actor,
    DateTime StartedAt, DateTime? FinishedAt, int Blocks, int Reads, int NewReads, int NewPasses,
    bool ClockSet, bool Erased, PatrolDownloadStatus Status, string? Error);

// --- Lectura a pedido desde el navegador (Web Serial) -----------------------

/// <summary>
/// El navegador lee el bastón y sube los bloques CRUDOS (hex); el servidor los
/// decodifica con el mismo código que la base. <paramref name="ClockSetAt"/> =
/// hora local del servidor que el navegador le escribió al bastón (o null).
/// </summary>
public sealed record PatrolBrowserUploadDto(
    long Serial, IReadOnlyList<string> BlocksHex, int DeclaredBlocks, DateTime? ClockSetAt);

public sealed record PatrolUploadResultDto(
    long DownloadId, int Reads, int NewReads, int NewPasses, bool EraseAllowed, string Message);

public sealed record PatrolServerTimeDto(DateTime Local, DateTime Utc);

/// <summary>Orden a una base desde el panel: leer (y opcionalmente vaciar) un bastón.</summary>
public sealed record PatrolReadRequestDto(bool Erase);
