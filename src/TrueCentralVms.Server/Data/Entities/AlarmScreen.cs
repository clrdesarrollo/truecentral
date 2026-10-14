using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Pantalla de alarma: una Raspberry Pi en una caseta de guardia que muestra
/// los avisos de las automatizaciones dirigidos a las pantallas, con sonido,
/// foto y consignas, y deja marcarlos como vistos. No hay login en la
/// pantalla: se empareja una vez (muestra un código de 6 dígitos que un
/// administrador aprueba en el panel) y desde ahí se identifica con su
/// credencial, de la que el servidor solo guarda el hash.
/// </summary>
public class AlarmScreen
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Identidad estable del equipo: la genera la app la primera vez que arranca.</summary>
    public string MachineId { get; set; } = "";
    /// <summary>Nombre de red del equipo (el que informa al anunciarse).</summary>
    public string Hostname { get; set; } = "";
    /// <summary>Versión de la app instalada en la pantalla.</summary>
    public string? AppVersion { get; set; }

    /// <summary><see cref="AlarmScreenStatus"/>.</summary>
    public string Status { get; set; } = AlarmScreenStatus.Pending;

    /// <summary>
    /// Código que muestra la pantalla mientras espera: es el único secreto que
    /// protege la entrega de la credencial, y se borra al entregarla.
    /// </summary>
    public string? PairingCode { get; set; }
    /// <summary>SHA-256 de la credencial (la credencial solo la tiene la pantalla).</summary>
    public string? TokenHash { get; set; }

    /// <summary>Volumen del equipo, 0–100 (se aplica al mezclador de la Pi).</summary>
    public int Volume { get; set; } = 80;
    /// <summary><see cref="AlarmScreenLayouts"/>.</summary>
    public string Layout { get; set; } = AlarmScreenLayouts.Horizontal;
    /// <summary>Salida de audio por NOMBRE (los números cambian en cada arranque de la Pi).</summary>
    public string? AudioOutput { get; set; }
    /// <summary>Salidas que informó el equipo al conectarse (JSON), para ofrecerlas en el panel.</summary>
    public string? AudioOutputsJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public string? LastIp { get; set; }
}

/// <summary>
/// Constancia de que una pantalla recibió un aviso. Junto con la hora del aviso
/// y la de su confirmación responde "cuándo le llegó a cada caseta", que es lo
/// que delata a una pantalla caída o con mala red.
/// </summary>
public class AlarmScreenDelivery
{
    public long Id { get; set; }
    public long AlertId { get; set; }
    public int ScreenId { get; set; }
    public DateTime DeliveredAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Ajustes comunes a todas las pantallas: una sola fila (Id = 1).</summary>
public class AlarmScreenSettings
{
    public int Id { get; set; } = 1;
    /// <summary>Nombre de la instalación que muestran las pantallas; vacío = el del servidor.</summary>
    public string SiteName { get; set; } = "";
    /// <summary>Cada cuántos segundos recordar que hay avisos sin ver (0 = solo al llegar).</summary>
    public int RepeatBeepSeconds { get; set; } = 10;
    /// <summary>Cuántas veces insistir. 0 = sin límite (suena hasta que se marque todo).</summary>
    public int MaxReminders { get; set; }
    /// <summary>Sonidos de la biblioteca de automatizaciones; null = el tono propio de la pantalla.</summary>
    public string? AlarmSound { get; set; }
    public string? HighPrioritySound { get; set; }
    public string? ReminderSound { get; set; }
    /// <summary>Horas de avisos que trae una pantalla al reconectarse.</summary>
    public int ResyncHours { get; set; } = 24;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
