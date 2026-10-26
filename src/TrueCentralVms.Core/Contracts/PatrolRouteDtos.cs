namespace TrueCentralVms.Core.Contracts;

// ---------------------------------------------------------------------------
// Control de rondas, fase 2: rutas, programación, rondas esperadas y su
// verificación. Ver docs/rondas/PLAN_MODULO_RONDAS.md (§5.2 y §5.2.1).

/// <summary>Estricto: los puntos en secuencia y a su hora; libre: cualquier orden, basta con marcar los requeridos.</summary>
public enum PatrolOrderMode { Strict, Free }

/// <summary>Estado de una ronda esperada.</summary>
public enum PatrolRunStatus
{
    /// <summary>Todavía no llega su hora (o está dentro del margen para salir).</summary>
    Scheduled,
    /// <summary>El bastón salió de la base: la ronda está en curso.</summary>
    InProgress,
    /// <summary>El bastón volvió pero aún no se evalúa, o falta descargar otro bastón que pudo hacerla.</summary>
    AwaitingData,
    Completed,
    Partial,
    /// <summary>Pasó el margen para salir y ningún bastón salió de sus bases.</summary>
    NotStarted,
    /// <summary>Salió pero no volvió dentro de la duración máxima.</summary>
    Unfinished,
    /// <summary>Saltada por feriado o anulada a mano.</summary>
    Cancelled,
}

public enum PatrolStopStatus { Pending, OnTime, Early, Late, OutOfOrder, Done, Missed, SkippedOptional }

/// <summary>Tipos de evento de rondas para el disparador de automatizaciones "patrol-event".</summary>
public static class PatrolEventKinds
{
    public const string RoundNotStarted = "round-not-started";
    public const string RoundUnfinished = "round-unfinished";
    public const string RoundPartial = "round-partial";
    public const string RoundCompleted = "round-completed";
    public const string RoundStarted = "round-started";
    public const string ReaderMemoryHigh = "reader-memory-high";
    public const string AgentOffline = "agent-offline";

    public static readonly (string Key, string Label)[] All =
    [
        (RoundNotStarted, "Ronda no iniciada a tiempo"),
        (RoundUnfinished, "Ronda sin terminar (bastón fuera de la base)"),
        (RoundPartial, "Ronda incompleta (puntos omitidos o fuera de horario)"),
        (RoundCompleted, "Ronda cumplida"),
        (RoundStarted, "Ronda iniciada (bastón salió de la base)"),
        (ReaderMemoryHigh, "Memoria del bastón sobre el 80 %"),
        (AgentOffline, "Base de descarga sin conexión"),
    ];
}

public sealed record PatrolRouteStopDto(
    int Sequence, int CheckpointId, string? CheckpointName, bool Required, int? OffsetMinutes, int? ToleranceMinutes);

public sealed record PatrolScheduleDto(
    int Id, int DaysMask, string StartTime, bool SkipHolidays, bool Enabled);

public sealed record PatrolRouteDto(
    int Id, string Name, int? LocationId, string? Location, bool Enabled, PatrolOrderMode OrderMode,
    int ReminderMinutes, int StartWindowMinutes, int RepeatMinutes, int MaxDurationMinutes,
    IReadOnlyList<int> AgentIds, IReadOnlyList<PatrolRouteStopDto> Stops, IReadOnlyList<PatrolScheduleDto> Schedules,
    string? Notes);

/// <summary>Alta/edición completa de una ruta (paradas y horarios incluidos).</summary>
public sealed record PatrolRouteWriteDto(
    string Name, int? LocationId, bool Enabled, PatrolOrderMode OrderMode,
    int ReminderMinutes, int StartWindowMinutes, int RepeatMinutes, int MaxDurationMinutes,
    IReadOnlyList<int> AgentIds, IReadOnlyList<PatrolRouteStopDto> Stops, IReadOnlyList<PatrolScheduleWriteDto> Schedules,
    string? Notes);

/// <summary><paramref name="DaysMask"/>: bit 0 = domingo … bit 6 = sábado. <paramref name="StartTime"/>: "HH:mm" local.</summary>
public sealed record PatrolScheduleWriteDto(int DaysMask, string StartTime, bool SkipHolidays, bool Enabled);

public sealed record PatrolRunStopDto(
    int Sequence, int CheckpointId, string CheckpointName, bool Required, DateTime? ExpectedAt, int? ToleranceMinutes,
    DateTime? ActualAt, string? GuardName, PatrolStopStatus Status);

public sealed record PatrolRunDto(
    long Id, int RouteId, string RouteName, string? Location, DateTime ExpectedStart, DateTime StartDeadline, DateTime DueEnd,
    DateTime? ActualStart, DateTime? ActualEnd, PatrolRunStatus Status, string? Cause,
    int RequiredStops, int DoneRequiredStops, IReadOnlyList<string> Guards, string? ReaderName,
    IReadOnlyList<PatrolRunStopDto>? Stops);

public sealed record PatrolRunPageDto(IReadOnlyList<PatrolRunDto> Items, int Total);

public sealed record PatrolRunCancelDto(string Reason);

/// <summary>Recordatorio para el PC de una base (viaja en la respuesta del latido).</summary>
/// <param name="Kind">"upcoming" (faltan N min), "due" (es la hora), "late" (pasó el margen sin salir).</param>
public sealed record PatrolAgentReminder(long RunId, string RouteName, DateTime ExpectedStartLocal, DateTime DeadlineLocal, string Kind, int RepeatMinutes);
