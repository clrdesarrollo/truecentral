namespace TrueCentralVms.Core.Contracts;

// DTOs del módulo Control de acceso. Primera etapa: el ADMINISTRADOR DE
// DISPOSITIVOS (alta, edición, sondeo y revalidación de terminales y
// controladoras, con las puertas que declara cada equipo). Las órdenes sobre
// puertas, el padrón de personas y el historial de accesos vienen después.
// Los enums viajan como texto en JSON.

/// <summary>Estado de la conexión del servidor con el equipo de control de acceso.</summary>
public enum AccessDeviceStatus { Unknown, Online, Offline, AuthFailed }

/// <summary>
/// Familia del equipo, tal como se muestra en el administrador. La deduce el
/// driver a partir de lo que informa el propio equipo (modelo y tipo).
/// </summary>
public enum AccessDeviceKind
{
    /// <summary>No se pudo clasificar (se muestra igual, con lo que informó el equipo).</summary>
    Unknown,
    /// <summary>Terminal de acceso: lector con teclado, tarjeta, huella o rostro (DS-K1T…).</summary>
    Terminal,
    /// <summary>Controladora de puertas: los lectores cuelgan de ella (DS-K2…).</summary>
    Controller,
    /// <summary>Torniquete, barrera o molinete con controladora integrada (DS-K3…).</summary>
    Turnstile,
}

/// <summary>Puerta declarada por un equipo de control de acceso.</summary>
public sealed record AccessDoorDto(
    int Id,
    int DeviceId,
    /// <summary>Número de puerta EN EL EQUIPO (1..n); es el que usan sus órdenes ISAPI.</summary>
    int Number,
    /// <summary>Nombre para el operador (el del equipo si lo entrega, o "Puerta n").</summary>
    string Name,
    bool Enabled);

/// <summary>Equipo de control de acceso tal como lo ve el panel. NUNCA incluye la contraseña.</summary>
public sealed record AccessDeviceDto(
    int Id,
    string Name,
    string DriverKey,
    string Host,
    int Port,
    bool UseHttps,
    string Username,
    /// <summary>Ubicación libre para el operador ("Portería", "Bodega 2"); opcional.</summary>
    string? Location,
    AccessDeviceKind Kind,
    string? Model,
    string? SerialNumber,
    string? FirmwareVersion,
    string? MacAddress,
    int DoorCount,
    bool SupportsRemoteControl,
    bool SupportsEvents,
    bool SupportsCards,
    bool SupportsFingerprint,
    bool SupportsFace,
    /// <summary>Cupo de personas del equipo (null si no lo informa).</summary>
    int? UserCapacity,
    /// <summary>Cupo de tarjetas del equipo (null si no lo informa).</summary>
    int? CardCapacity,
    bool Enabled,
    AccessDeviceStatus Status,
    string? LastError,
    DateTime? LastSeenAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<AccessDoorDto> Doors);

public sealed record AccessDeviceWriteDto(
    string Name,
    string DriverKey,
    string Host,
    int Port,
    bool UseHttps,
    string Username,
    /// <summary>Al editar, vacío = conservar la actual.</summary>
    string? Password,
    bool Enabled = true,
    string? Location = null);

/// <summary>Resultado del botón "Probar conexión" (no persiste nada).</summary>
public sealed record AccessProbeResultDto(
    bool Success,
    string? Error,
    string? Model,
    string? SerialNumber,
    string? FirmwareVersion,
    string? MacAddress,
    AccessDeviceKind Kind,
    int DoorCount,
    bool SupportsRemoteControl,
    bool SupportsEvents,
    bool SupportsCards,
    bool SupportsFingerprint,
    bool SupportsFace,
    int? UserCapacity,
    int? CardCapacity,
    IReadOnlyList<string> DoorNames);

/// <summary>Con qué se autentica el driver contra el equipo (cambia el formulario del mantenedor).</summary>
public enum AccessAuthMode
{
    /// <summary>Usuario y contraseña del equipo (Hikvision ISAPI, Dahua CGI).</summary>
    UserPassword,
    /// <summary>Clave numérica de comunicación del SDK, sin usuario (ZKTeco; 0 = sin clave).</summary>
    CommKey,
}

public sealed record AccessDriverDto(
    string Key,
    string DisplayName,
    int DefaultPort,
    bool DefaultHttps,
    AccessAuthMode AuthMode,
    /// <summary>Ayuda para el formulario: qué credencial espera este equipo.</summary>
    string? Hint,
    /// <summary>Los equipos de esta marca aceptan el padrón del VMS (personas, credenciales y horarios).</summary>
    bool SupportsPersonSync);

// ---------------------------------------------------------------------------
// Segunda etapa del módulo: puertas, horarios, personas, niveles de acceso e
// historial. La cadena es: un HORARIO dice cuándo; un NIVEL DE ACCESO junta
// puertas + horario; una PERSONA recibe niveles; el servidor traduce todo eso
// a lo que entiende cada equipo y lo escribe (sincronización).
// ---------------------------------------------------------------------------

/// <summary>Orden que se le da a una puerta desde el VMS.</summary>
public enum AccessDoorCommand
{
    /// <summary>Pulso de apertura: abre y vuelve a cerrarse sola (lo habitual).</summary>
    Open,
    /// <summary>Cierra ahora y vuelve al modo normal (cancela un "mantener abierta").</summary>
    Close,
    /// <summary>Mantener abierta hasta nueva orden (puerta libre).</summary>
    RemainOpen,
    /// <summary>Mantener cerrada: no entra nadie, ni con credencial válida (bloqueo).</summary>
    RemainLocked,
}

/// <summary>En qué modo está una puerta.</summary>
public enum AccessDoorMode { Unknown, Normal, RemainOpen, RemainLocked }

/// <summary>Qué le pasó a una persona en una puerta.</summary>
public enum AccessEventKind
{
    Other,
    /// <summary>Acceso concedido: la credencial era válida y tenía permiso.</summary>
    Granted,
    /// <summary>Acceso denegado (sin permiso, fuera de horario, credencial desconocida o vencida).</summary>
    Denied,
    /// <summary>La puerta se abrió (sensor, botón de salida u orden remota).</summary>
    DoorOpen,
    /// <summary>La puerta se cerró.</summary>
    DoorClose,
    /// <summary>Alarma de la puerta: forzada, mantenida abierta, sabotaje o coacción.</summary>
    Alarm,
}

/// <summary>Con qué se identificó la persona.</summary>
public enum AccessCredentialKind { Unknown, Card, Fingerprint, Face, Pin, Remote, ExitButton, Qr, Plate }

/// <summary>Puerta con el equipo al que pertenece y su estado en vivo, para el monitoreo y los niveles de acceso.</summary>
public sealed record AccessDoorStateDto(
    int Id,
    int DeviceId,
    string DeviceName,
    string DriverKey,
    string? Location,
    int Number,
    string Name,
    bool Enabled,
    /// <summary>Estado de conexión del EQUIPO: una puerta de un equipo caído no se puede operar.</summary>
    AccessDeviceStatus DeviceStatus,
    /// <summary>Modo de la puerta ("Normal", "RemainOpen", "RemainLocked", "Unknown").</summary>
    string Mode,
    /// <summary>Hoja abierta según el sensor; null si el equipo no lo informa.</summary>
    bool? Open,
    bool SupportsRemoteControl,
    bool SupportsDoorStatus,
    DateTime? StateReadAt);

/// <summary>Orden sobre una puerta: "Open", "Close", "RemainOpen", "RemainLocked".</summary>
public sealed record AccessDoorCommandDto(string Command);

/// <summary>Renombrar o pausar una puerta (lo demás lo define el equipo).</summary>
public sealed record AccessDoorWriteDto(string Name, bool Enabled);

// --- Horarios --------------------------------------------------------------

/// <summary>Tramo de un horario: día (0 = domingo … 6 = sábado) y minutos desde medianoche.</summary>
public sealed record AccessScheduleSegmentDto(int Day, int StartMinutes, int EndMinutes);

public sealed record AccessScheduleDto(
    int Id,
    string Name,
    string? Description,
    /// <summary>Ranura que ocupa el horario EN LOS EQUIPOS (las personas lo referencian por este número).</summary>
    int PlanNumber,
    /// <summary>El horario de fábrica 24/7: no se puede borrar ni editar.</summary>
    bool IsBuiltIn,
    IReadOnlyList<AccessScheduleSegmentDto> Segments,
    /// <summary>Niveles de acceso que lo usan (no se puede borrar si hay alguno).</summary>
    int LevelCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record AccessScheduleWriteDto(
    string Name,
    string? Description,
    IReadOnlyList<AccessScheduleSegmentDto> Segments);

// --- Niveles de acceso -----------------------------------------------------

public sealed record AccessLevelDto(
    int Id,
    string Name,
    string? Description,
    int ScheduleId,
    string ScheduleName,
    bool Enabled,
    IReadOnlyList<int> DoorIds,
    /// <summary>"Equipo · Puerta", para mostrar sin pedir la lista de puertas.</summary>
    IReadOnlyList<string> DoorNames,
    int PersonCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record AccessLevelWriteDto(
    string Name,
    string? Description,
    int ScheduleId,
    IReadOnlyList<int> DoorIds,
    bool Enabled = true);

// --- Personas --------------------------------------------------------------

/// <summary>En qué estado está el padrón de una persona respecto de los equipos.</summary>
public enum AccessSyncState
{
    /// <summary>Nada que escribir: la persona no tiene niveles de acceso.</summary>
    NotApplicable,
    /// <summary>Hay cambios sin escribir en los equipos.</summary>
    Pending,
    /// <summary>Escrita en todos los equipos que le corresponden.</summary>
    Synced,
    /// <summary>Algún equipo la rechazó o no respondió (el detalle va en el error).</summary>
    Failed,
}

/// <summary>Tarjeta de una persona.</summary>
public sealed record AccessCardDto(int Id, string Number, bool Enabled);

/// <summary>
/// Huella enrolada. NUNCA lleva la plantilla: el dato biométrico no sale del
/// servidor, solo se informa qué dedo está tomado y con qué calidad.
/// </summary>
public sealed record AccessFingerprintDto(
    int Number,
    /// <summary>Nombre del dedo ("Índice derecho").</summary>
    string Name,
    int? Quality,
    string? Source,
    DateTime CreatedAt);

/// <summary>
/// Huella que manda el panel al guardar una persona. <paramref name="Template"/>
/// null significa "dejar la que ya está" (el panel no la recibe nunca, así que
/// no puede reenviarla); un dedo que no viene en la lista se borra.
/// </summary>
public sealed record AccessFingerprintWriteDto(
    int Number,
    string? Template,
    int? Quality,
    string? Source);

/// <summary>
/// Rostro enrolado. A diferencia de la huella, la foto SÍ se puede mirar —para
/// eso está: para que quien administra vea a quién le está dando acceso— pero
/// no viaja en las listas: se pide aparte, por su propia dirección.
/// </summary>
public sealed record AccessFaceDto(
    /// <summary>Tamaño de la foto en bytes, para mostrarlo sin descargarla.</summary>
    int Bytes,
    /// <summary>"image/jpeg" o "image/png": es lo que aceptan los terminales.</summary>
    string ContentType,
    string? Source,
    DateTime CreatedAt);

/// <summary>
/// Rostro que manda el panel al guardar una persona. <paramref name="Image"/>
/// es la foto en base64 (sin el prefijo <c>data:</c>); null significa "dejar la
/// que ya está". Para quitarlo está el borrado, no una foto vacía.
/// </summary>
public sealed record AccessFaceWriteDto(
    string? Image,
    string? ContentType,
    string? Source);

/// <summary>Lo que aceptan los terminales para el rostro, y hasta cuánto.</summary>
public static class AccessFacePhoto
{
    /// <summary>Formatos que declaran los equipos (<c>facePicFormat</c>).</summary>
    public static readonly string[] ContentTypes = ["image/jpeg", "image/png"];

    /// <summary>
    /// Tope de la foto. Los terminales rechazan las imágenes grandes y el
    /// modelado se hace EN el equipo, así que una foto de carnet alcanza y
    /// sobra; el tope evita que alguien mande una de 12 MP y espere.
    /// </summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    public static bool IsAllowed(string? contentType) =>
        contentType is not null &&
        ContentTypes.Contains(contentType.Trim().ToLowerInvariant());
}

/// <summary>Los diez dedos, como los numeran los equipos.</summary>
public static class AccessFingers
{
    public const int Count = 10;

    private static readonly string[] Names =
    [
        "Pulgar derecho", "Índice derecho", "Medio derecho", "Anular derecho", "Meñique derecho",
        "Pulgar izquierdo", "Índice izquierdo", "Medio izquierdo", "Anular izquierdo", "Meñique izquierdo",
    ];

    public static bool IsValid(int number) => number is >= 1 and <= Count;

    public static string NameOf(int number) => IsValid(number) ? Names[number - 1] : $"Dedo {number}";
}

/// <summary>Resultado de escribir una persona en UN equipo.</summary>
public sealed record AccessPersonDeviceDto(
    int DeviceId,
    string DeviceName,
    AccessSyncState State,
    string? Error,
    DateTime? SyncedAt);

public sealed record AccessPersonDto(
    int Id,
    /// <summary>Identificador de la persona en los equipos; se asigna al crearla y no cambia.</summary>
    string EmployeeNo,
    string FirstName,
    string LastName,
    string FullName,
    string? Department,
    string? Position,
    string? Email,
    string? Phone,
    string? Notes,
    DateTime ValidFrom,
    DateTime ValidTo,
    bool Enabled,
    /// <summary>La persona tiene clave de teclado (el valor NUNCA se devuelve).</summary>
    bool HasPin,
    IReadOnlyList<AccessCardDto> Cards,
    IReadOnlyList<AccessFingerprintDto> Fingerprints,
    /// <summary>Rostro enrolado, o null si la persona no tiene.</summary>
    AccessFaceDto? Face,
    IReadOnlyList<int> LevelIds,
    IReadOnlyList<string> LevelNames,
    AccessSyncState SyncState,
    string? SyncError,
    DateTime? LastSyncedAt,
    IReadOnlyList<AccessPersonDeviceDto> Devices,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record AccessPersonWriteDto(
    string FirstName,
    string LastName,
    string? Department,
    string? Position,
    string? Email,
    string? Phone,
    string? Notes,
    DateTime ValidFrom,
    DateTime ValidTo,
    bool Enabled,
    /// <summary>Vacío = no cambiar; "" explícito no se distingue, por eso null quita la clave solo con <see cref="ClearPin"/>.</summary>
    string? PinCode,
    bool ClearPin,
    IReadOnlyList<string> Cards,
    IReadOnlyList<int> LevelIds,
    /// <summary>Solo al crear: si se omite, lo asigna el servidor.</summary>
    string? EmployeeNo = null,
    /// <summary>Huellas de la persona; null = no tocarlas (formularios que no las administran).</summary>
    IReadOnlyList<AccessFingerprintWriteDto>? Fingerprints = null,
    /// <summary>Rostro de la persona; null = no tocarlo. Para quitarlo, <see cref="ClearFace"/>.</summary>
    AccessFaceWriteDto? Face = null,
    bool ClearFace = false);

/// <summary>Asignación masiva de un nivel de acceso a varias personas (y a la inversa).</summary>
public sealed record AccessAssignDto(IReadOnlyList<int> PersonIds);

// --- Historial de accesos --------------------------------------------------

public sealed record AccessEventDto(
    long Id,
    int DeviceId,
    string DeviceName,
    int? DoorNumber,
    string? DoorName,
    DateTime Timestamp,
    DateTime ReceivedAt,
    AccessEventKind Kind,
    AccessCredentialKind Credential,
    string Description,
    string? EmployeeNo,
    string? PersonName,
    int? PersonId,
    string? CardNumber);

public sealed record AccessEventPageDto(int Total, int Page, int PageSize, IReadOnlyList<AccessEventDto> Items);

/// <summary>Resumen del módulo para la portada de Control de acceso.</summary>
public sealed record AccessOverviewDto(
    int Devices,
    int DevicesOnline,
    int Doors,
    int DoorsRemainOpen,
    int DoorsRemainLocked,
    int Persons,
    int PersonsWithoutLevel,
    int PersonsPendingSync,
    int PersonsFailedSync,
    int Levels,
    int Schedules,
    int EventsToday,
    int DeniedToday,
    /// <summary>Cupo de puertas de la licencia (null = sin límite declarado).</summary>
    int? DoorLimit,
    IReadOnlyList<AccessEventDto> RecentEvents);
