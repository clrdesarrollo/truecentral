using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Core.Drivers;

/// <summary>Datos de conexión a un equipo de control de acceso (API HTTP del fabricante).</summary>
public sealed record AccessConnectionInfo(string Host, int Port, bool UseHttps, string Username, string Password);

/// <summary>Puerta que declara el equipo. <paramref name="Number"/> es el número con el que la nombran sus propias órdenes.</summary>
public sealed record AccessDoorInfo(int Number, string Name);

/// <summary>Qué sabe hacer el equipo, leído de sus capacidades al validarlo.</summary>
public sealed record AccessCapabilities(
    /// <summary>Puertas que administra (1 en un terminal típico, 2 o 4 en una controladora).</summary>
    int DoorCount,
    /// <summary>Acepta órdenes remotas de apertura/cierre de puerta.</summary>
    bool SupportsRemoteControl,
    /// <summary>Entrega el historial de accesos y/o empuja eventos en vivo.</summary>
    bool SupportsEvents,
    /// <summary>Administra tarjetas.</summary>
    bool SupportsCards,
    bool SupportsFingerprint,
    bool SupportsFace,
    /// <summary>Cupo de personas del equipo (null si no lo informa).</summary>
    int? UserCapacity,
    /// <summary>Cupo de tarjetas del equipo (null si no lo informa).</summary>
    int? CardCapacity);

/// <summary>Identificación del equipo obtenida al validar las credenciales.</summary>
public sealed record AccessDeviceInfo(
    string? Model,
    string? SerialNumber,
    string? FirmwareVersion,
    string? DeviceType,
    string? MacAddress,
    AccessDeviceKind Kind,
    AccessCapabilities Capabilities,
    IReadOnlyList<AccessDoorInfo> Doors);

/// <summary>Lo que el equipo informa de una puerta en el monitoreo en vivo.</summary>
public sealed record AccessDoorStatus(
    int Number,
    AccessDoorMode Mode,
    /// <summary>Hoja abierta según el sensor de puerta; null si el equipo no lo informa.</summary>
    bool? Open);

/// <summary>Un renglón del historial de accesos, tal como lo entrega el equipo.</summary>
public sealed record AccessEventRecord(
    /// <summary>Hora del evento SEGÚN EL EQUIPO, ya convertida a UTC.</summary>
    DateTime Timestamp,
    int? DoorNumber,
    AccessEventKind Kind,
    AccessCredentialKind Credential,
    string Description,
    /// <summary>Identificador de la persona en el equipo (el del padrón del VMS).</summary>
    string? EmployeeNo,
    string? PersonName,
    string? CardNumber,
    /// <summary>Códigos del fabricante (Hikvision: major/minor), para diagnóstico.</summary>
    int? MajorType,
    int? MinorType,
    string? RawJson);

/// <summary>Un tramo horario de un día de la semana, en minutos desde medianoche.</summary>
public sealed record AccessTimeSegment(int Day, int StartMinutes, int EndMinutes)
{
    /// <summary>El tramo como lo escriben los equipos ("08:30:00").</summary>
    public static string Hhmmss(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}:00";
}

/// <summary>
/// Horario semanal tal como se sube al equipo. <paramref name="Number"/> es la
/// ranura que ocupa EN EL EQUIPO: los equipos guardan los horarios en una
/// tabla numerada y las personas los referencian por ese número.
/// </summary>
public sealed record AccessWeekPlan(int Number, string Name, IReadOnlyList<AccessTimeSegment> Segments)
{
    /// <summary>Cubre los siete días completos: no hace falta restringir nada en el equipo.</summary>
    public bool IsAllDay => Enumerable.Range(0, 7).All(day =>
        Segments.Any(s => s.Day == day && s.StartMinutes <= 0 && s.EndMinutes >= 24 * 60 - 1));
}

/// <summary>Derecho de una persona sobre UNA puerta de UN equipo: por dónde pasa y en qué horario.</summary>
public sealed record AccessDoorRight(int DoorNumber, AccessWeekPlan Plan);

/// <summary>
/// Una huella lista para bajar a un equipo: la plantilla que entregó el lector
/// de enrolamiento, en base64, y el número de dedo (1 a 10), que es como las
/// numeran los equipos.
/// </summary>
public sealed record AccessFingerprintData(int Number, string Template);

/// <summary>
/// El rostro de una persona listo para bajar a un equipo: la FOTO tal cual, no
/// una plantilla. A diferencia de la huella, el modelo lo arma el propio
/// terminal a partir de la imagen —por eso puede rechazarla si no encuentra una
/// cara— y por eso se guarda la foto y no un vector.
/// </summary>
public sealed record AccessFaceData(byte[] Image, string ContentType);

/// <summary>
/// Todo lo que hay que dejar escrito en un equipo para que una persona pueda
/// entrar: quién es, hasta cuándo vale, con qué se identifica y por qué
/// puertas y en qué horario pasa. El driver la crea o la actualiza según ya
/// exista o no en el equipo.
/// </summary>
public sealed record AccessPersonPlan(
    /// <summary>Identificador de la persona en los equipos (estable, lo asigna el VMS).</summary>
    string EmployeeNo,
    string Name,
    /// <summary>Vigencia de la persona (UTC): fuera de ella el equipo la rechaza solo.</summary>
    DateTime ValidFrom,
    DateTime ValidTo,
    /// <summary>Clave de teclado, si la persona tiene; null si no usa.</summary>
    string? PinCode,
    IReadOnlyList<string> Cards,
    /// <summary>Huellas enroladas; vacía si la persona no tiene o el equipo no las admite.</summary>
    IReadOnlyList<AccessFingerprintData> Fingerprints,
    /// <summary>Rostro enrolado; null si la persona no tiene o el equipo no lo admite.</summary>
    AccessFaceData? Face,
    /// <summary>Puertas de ESTE equipo a las que tiene derecho, con su horario.</summary>
    IReadOnlyList<AccessDoorRight> Doors);

/// <summary>
/// Driver de una marca de control de acceso. Los métodos lanzan
/// <see cref="DriverException"/> con mensaje en español cuando el equipo es
/// inalcanzable, rechaza las credenciales o no es un equipo de control de
/// acceso.
/// </summary>
public interface IAccessControlDriver
{
    /// <summary>Valida credenciales y lee identificación, capacidades y puertas.</summary>
    Task<AccessDeviceInfo> ProbeAsync(AccessConnectionInfo info, CancellationToken ct = default);

    /// <summary>
    /// Comprobación liviana para el sondeo periódico de estado: una sola
    /// llamada al equipo. Lanza <see cref="DriverException"/> si no responde
    /// o rechaza las credenciales.
    /// </summary>
    Task PingAsync(AccessConnectionInfo info, CancellationToken ct = default);

    /// <summary>
    /// Olvida lo que el driver tenga cacheado de ese equipo (credenciales
    /// cambiadas o equipo eliminado). Por omisión no hay nada que olvidar:
    /// solo los drivers que reutilizan sesiones lo implementan.
    /// </summary>
    void ForgetCachedSession(AccessConnectionInfo info) { }

    // ------------------------------------------------------------------
    // Operación. Lo que un driver no sabe hacer lo declara acá y no lo
    // implementa: el servidor deja de ofrecerle esa función al operador en
    // vez de fallar recién cuando la pide.
    // ------------------------------------------------------------------

    /// <summary>Sabe escribir el padrón (personas, credenciales y horarios) en el equipo.</summary>
    bool SupportsPersonSync => false;

    /// <summary>
    /// Sabe bajar huellas al equipo. Va aparte de
    /// <see cref="SupportsPersonSync"/> porque escribir personas y tarjetas es
    /// una cosa y la biometría es otra: hay equipos que aceptan lo primero y
    /// no lo segundo.
    /// </summary>
    bool SupportsFingerprintSync => false;

    /// <summary>
    /// Sabe bajar el rostro al equipo. Va aparte de
    /// <see cref="SupportsFingerprintSync"/> porque son terminales distintos:
    /// hay equipos con lector de huella y sin cámara, y al revés.
    /// </summary>
    bool SupportsFaceSync => false;

    /// <summary>
    /// Sabe poner un lector del equipo a la espera de una tarjeta para leer su
    /// número. Es lo que evita tener que tipear el número a mano —o buscarlo en
    /// el historial— al dar de alta a alguien.
    /// </summary>
    bool SupportsCardCapture => false;

    /// <summary>
    /// Deja el equipo esperando que pasen una tarjeta y devuelve su número, o
    /// <c>null</c> si nadie la pasó en el rato que el equipo espera (unos diez
    /// segundos). Que no venga nadie NO es un error: el llamador vuelve a
    /// pedirlo mientras el operador tenga el diálogo abierto.
    /// </summary>
    Task<string?> CaptureCardAsync(AccessConnectionInfo info, int? cardReaderNo = null,
        CancellationToken ct = default) =>
        throw new DriverException("Este equipo no sabe leer una tarjeta a pedido.");

    /// <summary>Sabe leer en qué modo está cada puerta.</summary>
    bool SupportsDoorStatus => false;

    /// <summary>
    /// El equipo EMPUJA sus eventos y el driver sabe escucharlos. Es lo que
    /// distingue "me entero cuando pasa" de "me entero en el próximo sondeo":
    /// para un guardia mirando el monitoreo, quince segundos de atraso es una
    /// eternidad.
    /// </summary>
    bool SupportsEventStream => false;

    /// <summary>Da una orden a una puerta del equipo.</summary>
    Task ControlDoorAsync(AccessConnectionInfo info, int doorNumber, AccessDoorCommand command, CancellationToken ct = default) =>
        throw new DriverException("Este equipo no acepta órdenes remotas de puerta.");

    /// <summary>En qué modo está cada puerta (solo si <see cref="SupportsDoorStatus"/>).</summary>
    Task<IReadOnlyList<AccessDoorStatus>> ReadDoorStatusAsync(AccessConnectionInfo info, int doorCount, CancellationToken ct = default) =>
        throw new DriverException("Este equipo no informa el estado de sus puertas.");

    /// <summary>
    /// Eventos de acceso posteriores a <paramref name="sinceUtc"/>, del más
    /// antiguo al más nuevo y a lo sumo <paramref name="max"/>. El servidor
    /// llama a esto en cada vuelta del sondeo y se queda con lo que no tenía.
    /// </summary>
    Task<IReadOnlyList<AccessEventRecord>> FetchEventsAsync(AccessConnectionInfo info, DateTime sinceUtc, int max,
        CancellationToken ct = default) =>
        throw new DriverException("Este equipo no entrega el historial de accesos.");

    /// <summary>
    /// Escucha los eventos que empuja el equipo, hasta que se cancele o se
    /// corte la conexión (solo si <see cref="SupportsEventStream"/>). El
    /// llamador se encarga de reconectar: acá una desconexión termina la
    /// secuencia, no se disimula.
    /// </summary>
    IAsyncEnumerable<AccessEventRecord> StreamEventsAsync(AccessConnectionInfo info, CancellationToken ct = default) =>
        throw new DriverException("Este equipo no empuja eventos.");

    /// <summary>Deja la persona escrita en el equipo (la crea o la actualiza) con sus credenciales y permisos.</summary>
    Task ApplyPersonAsync(AccessConnectionInfo info, AccessPersonPlan plan, CancellationToken ct = default) =>
        throw new DriverException("Este equipo no admite administrar personas desde el VMS.");

    /// <summary>Borra la persona del equipo (perdió el permiso o se dio de baja). Que no esté no es error.</summary>
    Task RemovePersonAsync(AccessConnectionInfo info, string employeeNo, CancellationToken ct = default) =>
        throw new DriverException("Este equipo no admite administrar personas desde el VMS.");

    /// <summary>Sube un horario semanal a su ranura del equipo, antes de que las personas lo referencien.</summary>
    Task ApplyWeekPlanAsync(AccessConnectionInfo info, AccessWeekPlan plan, CancellationToken ct = default) =>
        throw new DriverException("Este equipo no admite administrar horarios desde el VMS.");
}

public interface IAccessControlDriverFactory
{
    /// <summary>Clave estable ("hikvision-isapi").</summary>
    string DriverKey { get; }

    string DisplayName { get; }

    int DefaultPort { get; }

    bool DefaultHttps { get; }

    /// <summary>Con qué se autentica: usuario+contraseña o clave de comunicación.</summary>
    AccessAuthMode AuthMode { get; }

    /// <summary>Ayuda que el mantenedor muestra bajo el formulario.</summary>
    string? Hint { get; }

    /// <summary>
    /// Los equipos de esta marca aceptan el padrón del VMS. Los que no, se
    /// administran en el propio equipo y el VMS solo lee sus eventos: la
    /// interfaz lo advierte en vez de ofrecer algo que va a fallar.
    /// </summary>
    bool SupportsPersonSync => false;

    IAccessControlDriver Create();
}

/// <summary>Registro de drivers de control de acceso, poblado por inyección de dependencias.</summary>
public sealed class AccessDriverRegistry
{
    private readonly Dictionary<string, IAccessControlDriverFactory> _factories;

    public AccessDriverRegistry(IEnumerable<IAccessControlDriverFactory> factories)
    {
        _factories = factories.ToDictionary(f => f.DriverKey, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IAccessControlDriverFactory> All => _factories.Values;

    public IAccessControlDriverFactory? Find(string driverKey) =>
        _factories.TryGetValue(driverKey, out var factory) ? factory : null;
}
