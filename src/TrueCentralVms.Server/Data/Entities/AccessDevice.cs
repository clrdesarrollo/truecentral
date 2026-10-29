using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Equipo de control de acceso (terminal DS-K1T, controladora DS-K2,
/// torniquete DS-K3). Entidad APARTE de <see cref="Device"/>: no tiene
/// canales de video ni rutas de streaming, sino puertas. La contraseña se
/// guarda cifrada con AES-256-GCM (ver <see cref="CredentialProtector"/>) y
/// el modelo, la serie, el firmware y las capacidades vienen del propio
/// equipo al validar las credenciales.
/// </summary>
public class AccessDevice : IIsupEquipment
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Clave del driver ("hikvision-isapi").</summary>
    public string DriverKey { get; set; } = "hikvision-isapi";
    public string Host { get; set; } = "";
    /// <summary>Puerto HTTP del equipo (ISAPI): 80 de fábrica.</summary>
    public int Port { get; set; } = 80;
    public bool UseHttps { get; set; }
    public string Username { get; set; } = "";
    public byte[] PasswordCiphertext { get; set; } = [];
    /// <summary>
    /// ID ISUP del equipo cuando se conecta por ISUP (driver hikvision-isup): el equipo se registra contra el servidor y no hace falta
    /// alcanzarlo por IP (detrás de NAT, 4G o MPLS). Null = conexión directa por IP.
    /// </summary>
    public string? IsupDeviceId { get; set; }
    /// <summary>Clave ISUP del equipo (la «clave de cifrado» de su menú de plataforma), cifrada como la contraseña.</summary>
    public byte[]? IsupKeyCiphertext { get; set; }
    /// <summary>
    /// Ubicación del equipo en el árbol de Recursos (null = por ubicar; antes
    /// era un texto libre). La reciben sus puertas nuevas; al cambiarla se
    /// mueven las puertas que estaban con el equipo.
    /// </summary>
    public int? LocationId { get; set; }

    // Datos obtenidos del equipo al validar credenciales.
    public AccessDeviceKind Kind { get; set; } = AccessDeviceKind.Unknown;
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? MacAddress { get; set; }
    public bool SupportsRemoteControl { get; set; }
    public bool SupportsEvents { get; set; }
    public bool SupportsCards { get; set; }
    public bool SupportsFingerprint { get; set; }

    /// <summary>
    /// Huella del calendario de feriados escrito en el equipo, en los que llevan
    /// uno propio (ZKTeco C3); null = hay que escribirlo.
    /// </summary>
    public string? AppliedHolidayHash { get; set; }

    /// <summary>Por qué el equipo quedó sin feriados (C3 con un horario que los ignora), o null.</summary>
    public string? HolidayNote { get; set; }
    public bool SupportsFace { get; set; }
    /// <summary>Cupo de personas del equipo (null si no lo informa).</summary>
    public int? UserCapacity { get; set; }
    /// <summary>Cupo de tarjetas del equipo (null si no lo informa).</summary>
    public int? CardCapacity { get; set; }

    /// <summary>
    /// Ficha de capacidades que DECLARA el equipo (<see cref="TrueCentralVms.Core.Drivers.AccessCapabilityProfile"/>
    /// en JSON), leída al validarlo o revalidarlo. Es lo que muestra la pestaña
    /// Capacidades de la página del equipo; null si el driver no la sabe leer
    /// o el equipo se validó antes de que existiera.
    /// </summary>
    public string? CapabilityProfileJson { get; set; }

    /// <summary>El servidor lo sondea y lo ofrece a operadores y automatizaciones.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Apagado por el cupo (o el módulo) de la licencia, no por decisión del
    /// administrador. Cuando vuelve a haber cupo el servidor lo enciende solo
    /// (LicenseQuotaReconciler); cualquier cambio manual de <see cref="Enabled"/>
    /// limpia la marca (VmsDbContext.SaveChanges).
    /// </summary>
    public bool DisabledByLicense { get; set; }

    public AccessDeviceStatus Status { get; set; } = AccessDeviceStatus.Unknown;
    public string? LastError { get; set; }
    public DateTime? LastSeenAt { get; set; }

    /// <summary>
    /// Hasta dónde se leyó el historial de este equipo. Es la marca de agua del
    /// sondeo de eventos: sobrevive a un reinicio del servidor, así que lo que
    /// pasó mientras estaba apagado se recupera igual.
    /// </summary>
    public DateTime? LastEventAt { get; set; }

    /// <summary>
    /// Último número de evento (<c>serialNo</c>) que se leyó del historial del
    /// equipo. Es la marca de agua preferida: el sondeo pide lo que vino
    /// DESPUÉS de este número, sin depender de la hora. La adelanta solo la
    /// lectura del historial (no la escucha en vivo, que tras un corte saltaría
    /// lo perdido); null mientras no se conozca, y entonces se lee por hora.
    /// </summary>
    public long? LastEventSerial { get; set; }

    /// <summary>
    /// El VMS lo pone en hora solo cuando se desfasa o tiene otra zona (si la
    /// política lo hace). Se apaga para un equipo cuya hora la maneja otro
    /// sistema y no conviene pelearle.
    /// </summary>
    public bool ClockAutoCorrect { get; set; } = true;

    /// <summary>
    /// Mantener una escucha de eventos en vivo con el equipo. Se apaga cuando
    /// otro sistema necesita la suya y el equipo admite pocas (los faciales con
    /// llamada de Hikvision, una sola: <c>deployExceedMax</c>), como el kiosco
    /// del casino. Apagada, el historial se sigue leyendo cada 15 s: no se
    /// pierde nada, solo llega con unos segundos de atraso.
    /// </summary>
    public bool LiveEvents { get; set; } = true;

    /// <summary>
    /// Su cámara propia (terminal facial): puerto RTSP que declaró, o null si
    /// no tiene. El VMS la muestra como una cámara más
    /// (<see cref="Device.AccessDeviceId"/>), incluida con el equipo: no ocupa
    /// canal de video de la licencia.
    /// </summary>
    public int? CameraRtspPort { get; set; }

    /// <summary>
    /// Ya se le preguntó qué partes tiene —cámara, altavoz, entradas y salidas—
    /// (al validarlo; a los agregados antes, en su primer sondeo en línea).
    /// </summary>
    public bool PartsChecked { get; set; }

    /// <summary>
    /// Mostrar su cámara como una cámara más. Si el administrador la quita, no
    /// vuelve sola al revalidar el equipo.
    /// </summary>
    public bool UseCamera { get; set; } = true;

    /// <summary>
    /// Su altavoz (el audio bidireccional de los faciales): se ofrece como un
    /// parlante más (<see cref="Speaker.AccessDeviceId"/>), incluido con el equipo.
    /// </summary>
    public bool HasSpeaker { get; set; }

    /// <summary>Ofrecer su altavoz como parlante. Si se quita, no vuelve solo al revalidar.</summary>
    public bool UseSpeaker { get; set; } = true;

    /// <summary>Su altavoz va por el audio bidireccional del SDK (puerto 8000), no por ISAPI.</summary>
    public bool SpeakerViaSdk { get; set; }

    /// <summary>
    /// Sus entradas y salidas de alarma (null = no tiene): se ofrecen como un
    /// módulo de E/S (<see cref="IoModule.AccessDeviceId"/>), incluido con el equipo.
    /// </summary>
    public int? IoInputs { get; set; }
    public int? IoOutputs { get; set; }

    /// <summary>Ofrecer sus entradas y salidas como módulo de E/S. Si se quita, no vuelve solo.</summary>
    public bool UseIo { get; set; } = true;

    /// <summary>Por qué alguna de sus partes no está en uso aunque la tenga, o null.</summary>
    public string? PartsNote { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<AccessDoor> Doors { get; set; } = [];
}

/// <summary>
/// Puerta administrada por un equipo de control de acceso. Se crea al validar
/// el equipo (tantas como declare) y se identifica por su número EN EL EQUIPO,
/// que es el que usan sus órdenes. Es la unidad que cuenta la licencia.
/// </summary>
public class AccessDoor : ILocatable
{
    public int Id { get; set; }
    public int AccessDeviceId { get; set; }
    public AccessDevice? AccessDevice { get; set; }
    /// <summary>Número de puerta en el equipo (1..n).</summary>
    public int Number { get; set; }
    /// <summary>Nombre para el operador (el que trae el equipo, editable después).</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// El nombre lo sigue mandando el equipo. Se apaga en cuanto un operador
    /// renombra la puerta en el VMS: a partir de ahí revalidar el equipo ya no
    /// le pisa el nombre que eligió (el del equipo suele ser "Door1" y el del
    /// VMS, "Portería casino").
    /// </summary>
    public bool NameFromDevice { get; set; } = true;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Último modo conocido de la puerta (normal, mantenida abierta, bloqueada).
    /// Lo escribe el sondeo en los equipos que informan estado, y las órdenes
    /// del operador en los que no: así el monitoreo muestra algo útil incluso
    /// cuando el equipo no sabe contar en qué modo está.
    /// </summary>
    public AccessDoorMode Mode { get; set; } = AccessDoorMode.Unknown;
    /// <summary>Hoja abierta según el sensor de puerta; null si el equipo no lo informa.</summary>
    public bool? IsOpen { get; set; }
    /// <summary>Cerradura trabada según el equipo; null si no lo informa.</summary>
    public bool? IsLocked { get; set; }
    public DateTime? StateReadAt { get; set; }

    /// <summary>Ubicación en el árbol de Recursos; null = por ubicar.</summary>
    public int? LocationId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<AccessLevelDoor> Levels { get; set; } = [];
}
