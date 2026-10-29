using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Módulo de entradas y salidas digitales por red (Advantech ADAM-6000). Sus
/// salidas mueven barreras, portones, balizas o sirenas; sus entradas leen
/// pulsadores, lazos detectores o sensores. Es un recurso por sí mismo (como
/// un parlante): tiene UNA ubicación y el alcance de los usuarios se mide por
/// módulo. Modbus no tiene usuario ni contraseña.
/// </summary>
public class IoModule : ILocatable
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Clave del driver ("adam-6000").</summary>
    public string DriverKey { get; set; } = "adam-6000";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 502;

    /// <summary>Credenciales de los drivers que las piden (ISAPI); Modbus no tiene.</summary>
    public string Username { get; set; } = "";
    public byte[] PasswordCiphertext { get; set; } = [];
    public bool UseHttps { get; set; }

    /// <summary>
    /// Entradas y salidas de alarma de un terminal de acceso: el VMS crea el
    /// módulo a partir del terminal (dirección, credenciales, ubicación) y se
    /// borra con él. No ocupa módulo de E/S de la licencia.
    /// </summary>
    public int? AccessDeviceId { get; set; }
    public AccessDevice? AccessDevice { get; set; }

    /// <summary>Modelo elegido a mano ("ADAM-6066"); null = el que informa el equipo.</summary>
    public string? ModelKey { get; set; }
    /// <summary>Modelo con que se trabaja: el elegido o, si no, el detectado al probarlo.</summary>
    public string? Model { get; set; }
    /// <summary>Lo que informó el equipo la última vez que se probó (para avisar si no calza).</summary>
    public string? ReportedModel { get; set; }

    /// <summary>Cada cuánto se leen las entradas (ms).</summary>
    public int PollIntervalMs { get; set; } = 250;

    /// <summary>Ubicación en el árbol de Recursos; null = por ubicar.</summary>
    public int? LocationId { get; set; }

    /// <summary>El servidor mantiene la conexión y lo ofrece a operadores y automatizaciones.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Apagado por el cupo (o el módulo) de la licencia, no por decisión del
    /// administrador. Cuando vuelve a haber cupo el servidor lo enciende solo
    /// (LicenseQuotaReconciler); cualquier cambio manual de <see cref="Enabled"/>
    /// limpia la marca (VmsDbContext.SaveChanges).
    /// </summary>
    public bool DisabledByLicense { get; set; }

    public IoModuleStatus Status { get; set; } = IoModuleStatus.Unknown;
    public string? LastError { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Entradas y salidas con su nombre (una fila por cada una que tiene el modelo).</summary>
    public List<IoPoint> Points { get; set; } = [];
}

/// <summary>Entrada o salida de un módulo, con el nombre que le puso el usuario.</summary>
public class IoPoint
{
    public int Id { get; set; }
    public int IoModuleId { get; set; }
    public IoModule? IoModule { get; set; }
    public IoPointKind Kind { get; set; }
    /// <summary>Número en el equipo, desde 0 (DI0, DO0), como lo rotula el ADAM.</summary>
    public int Number { get; set; }
    public string Name { get; set; } = "";
    /// <summary>
    /// Entradas: el nivel 0 es "activa". Es lo normal en contacto seco (el ADAM
    /// lee 1 con el contacto abierto y 0 cerrado a GND): un pulsador o un lazo
    /// que cierra contacto queda "activo" al cerrarse.
    /// </summary>
    public bool Inverted { get; set; } = true;

    /// <summary>Rótulo del equipo: "DI0", "DO3".</summary>
    public string Code => (Kind == IoPointKind.Input ? "DI" : "DO") + Number;
}
