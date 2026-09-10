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
public class AccessDevice
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
    /// <summary>Ubicación libre para el operador ("Portería", "Bodega 2").</summary>
    public string? Location { get; set; }

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
    public bool SupportsFace { get; set; }
    /// <summary>Cupo de personas del equipo (null si no lo informa).</summary>
    public int? UserCapacity { get; set; }
    /// <summary>Cupo de tarjetas del equipo (null si no lo informa).</summary>
    public int? CardCapacity { get; set; }

    /// <summary>El servidor lo sondea y lo ofrece a operadores y automatizaciones.</summary>
    public bool Enabled { get; set; } = true;

    public AccessDeviceStatus Status { get; set; } = AccessDeviceStatus.Unknown;
    public string? LastError { get; set; }
    public DateTime? LastSeenAt { get; set; }

    /// <summary>
    /// Hasta dónde se leyó el historial de este equipo. Es la marca de agua del
    /// sondeo de eventos: sobrevive a un reinicio del servidor, así que lo que
    /// pasó mientras estaba apagado se recupera igual.
    /// </summary>
    public DateTime? LastEventAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<AccessDoor> Doors { get; set; } = [];
}

/// <summary>
/// Puerta administrada por un equipo de control de acceso. Se crea al validar
/// el equipo (tantas como declare) y se identifica por su número EN EL EQUIPO,
/// que es el que usan sus órdenes. Es la unidad que cuenta la licencia.
/// </summary>
public class AccessDoor
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
    public DateTime? StateReadAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<AccessLevelDoor> Levels { get; set; } = [];
}
