namespace TrueCentralVms.Core.Contracts;

// DTOs del módulo de entradas y salidas (E/S): módulos de E/S digital por red
// (Advantech ADAM-6000) cuyas salidas mueven barreras, portones, balizas o
// sirenas, y cuyas entradas leen pulsadores, lazos detectores o sensores.
// Los enums viajan como texto en JSON.

/// <summary>Estado de conexión del servidor con el módulo.</summary>
public enum IoModuleStatus { Unknown, Online, Offline }

/// <summary>Entrada digital (DI) o salida (DO / relé).</summary>
public enum IoPointKind { Input, Output }

/// <summary>Orden a una salida.</summary>
public enum IoOutputCommand
{
    /// <summary>Energizar y dejarla así.</summary>
    On,
    /// <summary>Soltar y dejarla así.</summary>
    Off,
    /// <summary>Invertir su estado.</summary>
    Toggle,
    /// <summary>Energizar unos segundos y soltar (barrera, puerta).</summary>
    Pulse,
    /// <summary>Parpadear (baliza) y quedar suelta.</summary>
    Blink,
}

/// <summary>
/// Una entrada o salida de un módulo con su nombre y su estado actual.
/// <paramref name="Code"/> es como la rotula el equipo ("DI0", "DO3").
/// </summary>
/// <param name="Inverted">Entradas: el nivel 0 es "activa" (contacto seco que
/// cierra a GND: pulsador, lazo, sensor normal abierto).</param>
/// <param name="Active">Entradas: activa (ya aplicada la inversión); salidas:
/// energizada. null = sin lectura todavía.</param>
/// <param name="Raw">Nivel leído (true = 1). null = sin lectura.</param>
public sealed record IoPointDto(IoPointKind Kind, int Number, string Code, string Name, bool Inverted,
    bool? Active, bool? Raw);

/// <summary>Módulo de E/S tal como lo ven el panel y las automatizaciones.</summary>
/// <param name="ModelKey">Modelo elegido a mano; null = el que informa el equipo.</param>
/// <param name="Model">Modelo con que se trabaja (fija cuántas entradas y salidas hay).</param>
/// <param name="ReportedModel">Lo que informó el propio equipo la última vez que se probó.</param>
public sealed record IoModuleDto(
    int Id,
    string Name,
    string DriverKey,
    string Host,
    int Port,
    string? ModelKey,
    string? Model,
    string? ReportedModel,
    int PollIntervalMs,
    bool Enabled,
    IoModuleStatus Status,
    string? LastError,
    DateTime? LastSeenAt,
    IReadOnlyList<IoPointDto> Inputs,
    IReadOnlyList<IoPointDto> Outputs,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    /// <summary>Ubicación en el árbol de Recursos (null = por ubicar).</summary>
    int? LocationId = null,
    /// <summary>Ruta legible de esa ubicación ("Casa matriz › Acceso vehicular").</summary>
    string? Location = null);

/// <summary>Nombre (y lógica, en las entradas) de una entrada o salida al guardar el módulo.</summary>
public sealed record IoPointWriteDto(IoPointKind Kind, int Number, string? Name, bool Inverted = true);

public sealed record IoModuleWriteDto(
    string Name,
    string DriverKey,
    string Host,
    int Port,
    /// <summary>Modelo elegido a mano; null o vacío = el que informa el equipo.</summary>
    string? ModelKey = null,
    /// <summary>Cada cuánto se leen las entradas (100–5000 ms).</summary>
    int PollIntervalMs = 250,
    bool Enabled = true,
    /// <summary>Ubicación en el árbol de Recursos: null = conservar la actual (en el alta,
    /// por ubicar); 0 = por ubicar.</summary>
    int? LocationId = null,
    /// <summary>Nombres de las entradas y salidas; las que no vienen conservan el suyo.</summary>
    IReadOnlyList<IoPointWriteDto>? Points = null);

/// <summary>Resultado del botón "Probar conexión" (no persiste nada).</summary>
public sealed record IoProbeResultDto(bool Success, string? Error, string? ReportedModel, string? Model,
    int Inputs, int Outputs, string? Warning);

public sealed record IoModelDto(string Key, string Name, int Inputs, int Outputs, string Description);

public sealed record IoDriverDto(string Key, string DisplayName, int DefaultPort, IReadOnlyList<IoModelDto> Models,
    int MinBlinkIntervalMs);

/// <summary>
/// Orden a una salida desde el panel. <paramref name="Seconds"/>: duración del
/// pulso (por omisión 1 s) o del parpadeo (0 = hasta otra orden);
/// <paramref name="IntervalMs"/>: período del parpadeo (por omisión 500 ms).
/// </summary>
public sealed record IoOutputCommandDto(IoOutputCommand Command, double? Seconds = null, int? IntervalMs = null);

/// <summary>
/// Resultado de una orden a una salida. No trae el estado del módulo: el que
/// había al responder todavía no refleja la orden; el estado vivo llega por el
/// hub (IoModuleChanged) en cuanto el servidor vuelve a leer el módulo.
/// </summary>
public sealed record IoCommandResultDto(bool Success, string Message);

/// <summary>Módulo elegible en la acción "operar relé" y en el disparador de entradas.</summary>
public sealed record WorkflowIoModuleDto(int Id, string Name, bool Enabled,
    IReadOnlyList<WorkflowIoPointDto> Inputs, IReadOnlyList<WorkflowIoPointDto> Outputs);

public sealed record WorkflowIoPointDto(int Number, string Code, string Name);
