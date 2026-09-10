using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Data.Entities;

// Segunda etapa del módulo Control de acceso. La cadena de decisión es la
// misma que usa cualquier control de acceso serio, y acá está entera:
//
//   HORARIO (cuándo)  +  PUERTAS (por dónde)   =  NIVEL DE ACCESO
//   NIVEL DE ACCESO   →  PERSONAS                 (quién puede)
//   PERSONA + NIVELES →  lo que se escribe en cada equipo (sincronización)
//
// El VMS es la fuente de verdad: los equipos son una caché de lo que acá se
// decidió. Por eso cada persona lleva, por equipo, en qué estado quedó lo que
// se le escribió (AccessPersonDevice), y cualquier cambio en horarios, niveles
// o personas vuelve a marcar como pendientes a los afectados.

/// <summary>
/// Horario semanal: los tramos en que un nivel de acceso deja pasar. Ocupa una
/// ranura numerada EN LOS EQUIPOS (<see cref="PlanNumber"/>), porque así los
/// guardan ellos y así los referencian las personas.
/// </summary>
public class AccessSchedule
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>
    /// Ranura del horario en los equipos (1..n). La 1 queda reservada para el
    /// horario 24/7 de fábrica, que es el que traen los equipos de serie.
    /// </summary>
    public int PlanNumber { get; set; }

    /// <summary>El horario 24/7 que crea el servidor: no se edita ni se borra.</summary>
    public bool IsBuiltIn { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<AccessScheduleSegment> Segments { get; set; } = [];
    public List<AccessLevel> Levels { get; set; } = [];
}

/// <summary>
/// Ranura de horario EN LOS EQUIPOS. Los equipos guardan los horarios en una
/// tabla numerada y cada persona referencia, por cada puerta, UN número de esa
/// tabla. Esta tabla del VMS es la que reparte esos números.
///
/// Hay una ranura por cada conjunto DISTINTO de tramos que haya que escribir.
/// Normalmente es el de un horario del VMS, pero cuando una persona llega a la
/// misma puerta por dos niveles de acceso con horarios distintos, lo que
/// corresponde escribir es la UNIÓN de ambos —si un nivel la deja pasar el
/// martes y el otro el jueves, pasa los dos días— y esa unión también necesita
/// su ranura. Se reparten por huella del contenido, así que dos veces el mismo
/// horario reutilizan la misma ranura en vez de gastar dos.
/// </summary>
public class AccessPlanSlot
{
    public int Id { get; set; }
    /// <summary>Número de la ranura en los equipos (1..n).</summary>
    public int Number { get; set; }
    /// <summary>Huella del conjunto de tramos: es la llave real de la tabla.</summary>
    public string Hash { get; set; } = "";
    /// <summary>Los tramos, para poder reescribir la ranura en un equipo nuevo.</summary>
    public string SegmentsJson { get; set; } = "[]";
    /// <summary>Nombre que se le escribe al equipo ("Oficinas", "Oficinas + Bodega").</summary>
    public string Label { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Un tramo de un día del horario. Día 0 = domingo, como <see cref="DayOfWeek"/>.</summary>
public class AccessScheduleSegment
{
    public int Id { get; set; }
    public int AccessScheduleId { get; set; }
    public AccessSchedule? AccessSchedule { get; set; }
    public int Day { get; set; }
    /// <summary>Minutos desde medianoche (0..1439).</summary>
    public int StartMinutes { get; set; }
    /// <summary>Minutos desde medianoche (1..1439); 1439 = "hasta el final del día".</summary>
    public int EndMinutes { get; set; }
}

/// <summary>
/// Nivel de acceso: por qué puertas se pasa y en qué horario. Es lo que se le
/// asigna a las personas; una persona puede tener varios (portería de lunes a
/// viernes + bodega solo los sábados, por ejemplo).
/// </summary>
public class AccessLevel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int AccessScheduleId { get; set; }
    public AccessSchedule? AccessSchedule { get; set; }
    /// <summary>Desactivado = no da permiso a nadie (se quita de los equipos sin borrar la configuración).</summary>
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<AccessLevelDoor> Doors { get; set; } = [];
    public List<AccessLevelPerson> Persons { get; set; } = [];
}

/// <summary>Puerta incluida en un nivel de acceso.</summary>
public class AccessLevelDoor
{
    public int AccessLevelId { get; set; }
    public AccessLevel? AccessLevel { get; set; }
    public int AccessDoorId { get; set; }
    public AccessDoor? AccessDoor { get; set; }
}

/// <summary>Persona que tiene un nivel de acceso.</summary>
public class AccessLevelPerson
{
    public int AccessLevelId { get; set; }
    public AccessLevel? AccessLevel { get; set; }
    public int AccessPersonId { get; set; }
    public AccessPerson? AccessPerson { get; set; }
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Persona del padrón. <see cref="EmployeeNo"/> es su identificador EN LOS
/// EQUIPOS: se asigna al crearla y no cambia nunca, porque es la llave con la
/// que los equipos devuelven sus eventos y con la que se la borra de ellos.
/// La clave de teclado se guarda cifrada con AES-256-GCM, igual que las
/// contraseñas de los equipos.
/// </summary>
public class AccessPerson
{
    public int Id { get; set; }
    public string EmployeeNo { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Department { get; set; }
    public string? Position { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Notes { get; set; }

    /// <summary>Vigencia (UTC). Los equipos la respetan solos: fuera de ella rechazan a la persona.</summary>
    public DateTime ValidFrom { get; set; } = DateTime.UtcNow;
    public DateTime ValidTo { get; set; } = DateTime.UtcNow.AddYears(10);

    /// <summary>Desactivada = se borra de los equipos, pero se conserva su historial.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Clave de teclado cifrada; vacío = la persona no usa clave.</summary>
    public byte[] PinCiphertext { get; set; } = [];

    /// <summary>Estado de lo escrito en los equipos, resumido de <see cref="Devices"/>.</summary>
    public AccessSyncState SyncState { get; set; } = AccessSyncState.NotApplicable;
    public string? SyncError { get; set; }
    public DateTime? LastSyncedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<AccessCard> Cards { get; set; } = [];
    public List<AccessFingerprint> Fingerprints { get; set; } = [];

    /// <summary>Rostro enrolado, o null. Uno solo: los terminales guardan un modelo por persona.</summary>
    public AccessFace? Face { get; set; }
    public List<AccessLevelPerson> Levels { get; set; } = [];
    public List<AccessPersonDevice> Devices { get; set; } = [];

    public string FullName => $"{FirstName} {LastName}".Trim();
}

/// <summary>Tarjeta de una persona. El número es el que leen los equipos.</summary>
public class AccessCard
{
    public int Id { get; set; }
    public int AccessPersonId { get; set; }
    public AccessPerson? AccessPerson { get; set; }
    public string Number { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Huella de una persona, capturada con el lector USB del puesto (el
/// complemento de enrolamiento) y bajada después a los equipos.
///
/// La plantilla va CIFRADA con AES-256-GCM, igual que las contraseñas de los
/// equipos y la clave de teclado: es un dato biométrico, no un número de
/// tarjeta. Nunca sale del servidor en claro — la API devuelve que la persona
/// tiene tal dedo enrolado y con qué calidad, no la plantilla.
/// </summary>
/// <summary>
/// Rostro de una persona. Se guarda la FOTO, no una plantilla: el modelo lo
/// arma cada terminal a partir de la imagen, así que hay que conservarla para
/// poder mandársela a un equipo nuevo o volver a mandarla si se perdió.
/// Va cifrada, como las huellas: es un dato biométrico.
/// </summary>
public class AccessFace
{
    public int Id { get; set; }
    public int AccessPersonId { get; set; }
    public AccessPerson? AccessPerson { get; set; }

    /// <summary>La foto tal cual (JPEG o PNG), cifrada.</summary>
    public byte[] ImageCiphertext { get; set; } = [];

    /// <summary>"image/jpeg" o "image/png".</summary>
    public string ContentType { get; set; } = "image/jpeg";

    /// <summary>Tamaño de la foto en claro, para mostrarlo sin descifrarla.</summary>
    public int Bytes { get; set; }

    /// <summary>De dónde salió ("Archivo subido por admin"), para saber quién la cargó.</summary>
    public string? Source { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AccessFingerprint
{
    public int Id { get; set; }
    public int AccessPersonId { get; set; }
    public AccessPerson? AccessPerson { get; set; }

    /// <summary>Número de dedo EN LOS EQUIPOS (1 a 10): es el que usan sus órdenes.</summary>
    public int Number { get; set; }

    /// <summary>Plantilla del lector (512 bytes en base64), cifrada.</summary>
    public byte[] TemplateCiphertext { get; set; } = [];

    /// <summary>Calidad que informó el lector (0 a 100); mayor es mejor.</summary>
    public int? Quality { get; set; }

    /// <summary>De dónde salió ("Lector USB PUESTO-01"), para saber quién la tomó.</summary>
    public string? Source { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Qué pasó al escribir una persona en UN equipo. Existe una fila por cada
/// equipo donde la persona DEBE estar (según sus niveles); cuando deja de
/// corresponder, el sincronizador la borra del equipo y quita la fila.
/// </summary>
public class AccessPersonDevice
{
    public int Id { get; set; }
    public int AccessPersonId { get; set; }
    public AccessPerson? AccessPerson { get; set; }
    public int AccessDeviceId { get; set; }
    public AccessDevice? AccessDevice { get; set; }

    public AccessSyncState State { get; set; } = AccessSyncState.Pending;
    public string? Error { get; set; }
    public DateTime? SyncedAt { get; set; }

    /// <summary>
    /// Huella de lo último que se escribió con éxito (persona + credenciales +
    /// puertas + horarios). Si no cambió, el sincronizador no vuelve a
    /// molestar al equipo.
    /// </summary>
    public string? AppliedHash { get; set; }

    /// <summary>La persona ya no corresponde en este equipo: hay que borrarla de él.</summary>
    public bool PendingRemoval { get; set; }
}

/// <summary>
/// Un renglón del historial de accesos, leído del equipo. Se guarda el nombre
/// del equipo y de la puerta tal como estaban al momento del evento: el
/// historial no debe cambiar porque después se renombre una puerta.
/// </summary>
public class AccessEvent
{
    public long Id { get; set; }
    public int AccessDeviceId { get; set; }
    public AccessDevice? AccessDevice { get; set; }
    public string DeviceName { get; set; } = "";
    public int? DoorNumber { get; set; }
    public string? DoorName { get; set; }

    /// <summary>Hora del evento según el equipo, en UTC.</summary>
    public DateTime Timestamp { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    public AccessEventKind Kind { get; set; }
    public AccessCredentialKind Credential { get; set; }
    public string Description { get; set; } = "";

    public string? EmployeeNo { get; set; }
    public string? PersonName { get; set; }
    /// <summary>Persona del padrón, si el <see cref="EmployeeNo"/> coincide con una.</summary>
    public int? AccessPersonId { get; set; }
    public string? CardNumber { get; set; }

    /// <summary>Códigos del fabricante (Hikvision: major/minor), para diagnóstico.</summary>
    public int? MajorType { get; set; }
    public int? MinorType { get; set; }
    public string? RawJson { get; set; }
}
