using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Automatización: "cuando pase ESTO (disparador + condiciones), hacer ESTO
/// OTRO (acciones en orden)". Las condiciones viajan como JSON porque cada
/// disparador filtra por campos distintos y el conjunto crece con cada
/// disparador nuevo; el motor las lee con <c>WorkflowConditionsDto</c>.
/// </summary>
public class Workflow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Pausada = el motor la ignora (no borra el historial).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Clave estable del disparador (ver <see cref="WorkflowTriggerTypes"/>).</summary>
    public string TriggerType { get; set; } = WorkflowTriggerTypes.AlarmEvent;

    /// <summary>Filtro del disparador serializado (<see cref="WorkflowConditionsDto"/>).</summary>
    public string? ConditionsJson { get; set; }

    /// <summary>
    /// El diagrama de flujo serializado (<c>WorkflowGraphDto</c>): nodos con
    /// su posición y conexiones. La configuración de cada acción NO va aquí
    /// (vive en <see cref="WorkflowAction"/>, con su contraseña cifrada); el
    /// nodo de acción solo apunta a su fila por <see cref="WorkflowAction.NodeId"/>.
    /// Null = automatización anterior al editor visual: el motor la ejecuta
    /// como una línea recta de acciones en orden.
    /// </summary>
    public string? GraphJson { get; set; }

    /// <summary>
    /// Tiempo mínimo entre dos ejecuciones. Es la defensa contra paneles
    /// "parlanchines": una zona que rebota 40 veces no manda 40 correos.
    /// 0 = sin límite.
    /// </summary>
    public int CooldownSeconds { get; set; } = 60;

    public DateTime? LastRunAt { get; set; }
    public long RunCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Usuario que la creó (queda aunque el usuario se elimine).</summary>
    public string CreatedBy { get; set; } = "";

    public List<WorkflowAction> Actions { get; set; } = [];
}

/// <summary>
/// Acción de una automatización. La configuración es propia de cada tipo y va
/// como JSON; la contraseña (FTP, servicio HTTP, parlante) se guarda cifrada
/// con AES-256-GCM igual que la de los dispositivos y nunca sale por la API.
/// </summary>
public class WorkflowAction
{
    public int Id { get; set; }
    public int WorkflowId { get; set; }
    public Workflow Workflow { get; set; } = null!;

    /// <summary>Orden de ejecución (1..N) en las automatizaciones lineales; en las de diagrama, orden de aparición.</summary>
    public int Order { get; set; }

    /// <summary>Nodo del diagrama al que pertenece (null en las automatizaciones lineales antiguas).</summary>
    public string? NodeId { get; set; }

    /// <summary>Clave estable del tipo (ver <see cref="WorkflowActionTypes"/>).</summary>
    public string Type { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Si esta acción falla, el workflow sigue con las siguientes.</summary>
    public bool ContinueOnError { get; set; } = true;

    /// <summary>Espera antes de ejecutarla (p. ej. tomar la foto 3 s después de la alarma).</summary>
    public int DelaySeconds { get; set; }

    public string ConfigJson { get; set; } = "{}";

    /// <summary>Contraseña de la acción cifrada (null = no usa).</summary>
    public byte[]? SecretCiphertext { get; set; }
}

/// <summary>
/// Ejecución de una automatización. Es la evidencia de qué se hizo
/// automáticamente y con qué resultado; el detalle por acción va en
/// <see cref="StepsJson"/> (<c>WorkflowRunStepDto[]</c>). Sin clave foránea al
/// workflow para que el historial sobreviva a su eliminación.
/// </summary>
public class WorkflowRun
{
    public long Id { get; set; }
    public int WorkflowId { get; set; }
    public string WorkflowName { get; set; } = "";

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }

    /// <summary>false = alguna acción falló (el detalle dice cuál).</summary>
    public bool Success { get; set; }

    /// <summary>Qué la disparó, en una línea.</summary>
    public string TriggerSummary { get; set; } = "";

    /// <summary>Datos del disparador (las marcas que se reemplazaron en los textos).</summary>
    public string? TriggerJson { get; set; }

    public string? Error { get; set; }

    /// <summary>Resultado de cada acción (<c>WorkflowRunStepDto[]</c>).</summary>
    public string StepsJson { get; set; } = "[]";

    /// <summary>"automático" o el usuario que la lanzó a mano como prueba.</summary>
    public string StartedBy { get; set; } = "automático";
}

/// <summary>
/// Alerta mostrada a los operadores y su acuse de recibo. Existe para poder
/// responder <b>quién se dio por enterado y cuándo</b>: nace pendiente cuando
/// una automatización avisa, y la confirmación deja el usuario, la hora y
/// desde dónde se confirmó. Es solo-agregar salvo por esa confirmación (que
/// se escribe UNA vez: el primero que la toma queda registrado).
///
/// Sin clave foránea a la ejecución ni al workflow, para que el registro
/// sobreviva a la purga del historial y a la eliminación de la automatización.
/// </summary>
public class WorkflowAlert
{
    public long Id { get; set; }
    public long? RunId { get; set; }
    public int WorkflowId { get; set; }
    public string WorkflowName { get; set; } = "";

    public DateTime RaisedAt { get; set; } = DateTime.UtcNow;
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public AlarmSeverity Severity { get; set; } = AlarmSeverity.Warning;

    /// <summary>Foto principal (la última capturada), en ruta relativa.</summary>
    public string? ImagePath { get; set; }
    /// <summary>Todas las fotos de la ejecución, en JSON (la ventana de alarma las pasa como una tira).</summary>
    public string? ImagePathsJson { get; set; }
    /// <summary>Canales vinculados (JSON): la ventana de alarma abre su video EN VIVO.</summary>
    public string? ChannelIdsJson { get; set; }
    public string? Sound { get; set; }
    public int SoundRepeat { get; set; } = 1;

    /// <summary>Qué la disparó, para que el registro se entienda solo.</summary>
    public string TriggerSummary { get; set; } = "";

    /// <summary>Exige acuse de recibo (queda pendiente hasta que alguien la confirme).</summary>
    public bool RequiresAck { get; set; } = true;

    public DateTime? AcknowledgedAt { get; set; }
    public int? AcknowledgedByUserId { get; set; }
    public string? AcknowledgedBy { get; set; }
    /// <summary>"client" (escritorio) o "web" (panel).</summary>
    public string? AcknowledgedFrom { get; set; }
    public string? AcknowledgedIp { get; set; }
}

/// <summary>
/// Servidor de correo saliente del VMS: una sola fila (Id = 1) que comparten
/// todas las automatizaciones. La contraseña se guarda cifrada.
/// </summary>
public class SmtpSettings
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;
    public string Username { get; set; } = "";
    public byte[]? PasswordCiphertext { get; set; }
    /// <summary>Aceptar certificados no verificables (servidor de correo interno con certificado propio).</summary>
    public bool AllowInvalidCertificate { get; set; }
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "CLR TrueCentral VMS";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
