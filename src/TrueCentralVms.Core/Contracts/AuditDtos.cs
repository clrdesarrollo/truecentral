namespace TrueCentralVms.Core.Contracts;

// ---------------------------------------------------------------------------
// Auditoría (ISO 27001): registro inmutable de acciones. La consulta vive solo
// en el panel web; el cliente de escritorio únicamente REPORTA los eventos que
// ocurren en su máquina (capturas y grabaciones locales, con su ruta).
// ---------------------------------------------------------------------------

public sealed record AuditEventDto(
    long Id,
    DateTime Timestamp,
    string Username,
    string? Role,
    string Origin,
    string ClientIp,
    string Category,
    string Action,
    string? TargetType,
    string? TargetId,
    string? TargetName,
    string? Detail,
    bool Success,
    string? DataJson);

/// <summary>Página de resultados de la bitácora (orden: más recientes primero).</summary>
public sealed record AuditPageDto(long Total, int Page, int PageSize, IReadOnlyList<AuditEventDto> Items);

/// <summary>
/// Evento reportado por el cliente de escritorio: acciones que ocurren en la
/// máquina del operador y que el servidor no puede ver (capturas, cápsulas
/// locales, dónde quedó guardada una exportación). <paramref name="Action"/>
/// debe ser una de las claves aceptadas por el servidor; lo demás se rechaza.
/// </summary>
public sealed record ClientAuditEventDto(
    string Action,
    int? DeviceId,
    string? DeviceName,
    int? ChannelNumber,
    string? ChannelName,
    string? FilePath,
    string? Detail);
