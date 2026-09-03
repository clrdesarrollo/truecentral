using System.Text.Json;

namespace TrueCentralVms.Core.Contracts;

// DTOs del módulo Automatizaciones (workflows): "cuando pase ESTO, hacer
// ESTO OTRO". Un workflow = un disparador + condiciones + una lista ordenada
// de acciones. Los tipos de disparador y de acción son CLAVES ESTABLES (van a
// la base): se agregan claves nuevas, no se renombran las existentes.

/// <summary>Disparadores disponibles. La clave se guarda en la base.</summary>
public static class WorkflowTriggerTypes
{
    /// <summary>Evento de un panel de alarma (alarma, armado, falla...).</summary>
    public const string AlarmEvent = "alarm-event";
    /// <summary>Cambio de conexión del servidor con un panel de alarma.</summary>
    public const string PanelStatus = "panel-status";

    public static readonly string[] All = [AlarmEvent, PanelStatus];
}

/// <summary>Acciones disponibles. La clave se guarda en la base.</summary>
public static class WorkflowActionTypes
{
    /// <summary>Capturar una foto (snapshot) de una o más cámaras.</summary>
    public const string Snapshot = "snapshot";
    /// <summary>Enviar un correo (opcionalmente con las fotos capturadas antes).</summary>
    public const string Email = "email";
    /// <summary>Subir los archivos generados a un servidor FTP/FTPS.</summary>
    public const string Ftp = "ftp";
    /// <summary>Llamar a un servicio externo (GET/POST/PUT...).</summary>
    public const string Http = "http";
    /// <summary>Reproducir un sonido en un parlante IP.</summary>
    public const string Speaker = "speaker";
    /// <summary>Avisar a los operadores conectados (panel web y clientes).</summary>
    public const string Notify = "notify";

    public static readonly string[] All = [Snapshot, Email, Ftp, Http, Speaker, Notify];
}

/// <summary>
/// Filtro del disparador: TODAS las listas vacías o nulas significan "sin
/// filtrar" (cualquier valor sirve). Una lista con elementos exige que el
/// evento coincida con alguno de ellos.
/// </summary>
public sealed record WorkflowConditionsDto(
    /// <summary>Paneles que disparan (vacío = todos).</summary>
    IReadOnlyList<int>? PanelIds = null,
    /// <summary>Naturaleza del evento: Alarm, Trouble, Arm, Disarm, Bypass...</summary>
    IReadOnlyList<AlarmEventKind>? Kinds = null,
    IReadOnlyList<AlarmSeverity>? Severities = null,
    /// <summary>Números de área del panel.</summary>
    IReadOnlyList<int>? AreaNumbers = null,
    /// <summary>Identificadores de zona tal como los usa la API del panel.</summary>
    IReadOnlyList<int>? ZoneNumbers = null,
    /// <summary>Códigos del evento (Contact-ID o del fabricante), ej. "1130".</summary>
    IReadOnlyList<string>? Codes = null,
    /// <summary>Origen del evento: "panel" | "poll" | "vms".</summary>
    IReadOnlyList<string>? Sources = null,
    /// <summary>Estados de conexión que disparan (solo en el disparador de conexión).</summary>
    IReadOnlyList<AlarmPanelStatus>? Statuses = null,
    /// <summary>La descripción del evento debe contener este texto.</summary>
    string? TextContains = null,
    /// <summary>
    /// Segundos que la condición debe SOSTENERSE para que la automatización
    /// se ejecute: al recibir el evento el servidor espera este tiempo y
    /// vuelve a leer el panel; si el sensor ya se restableció, no hace nada.
    /// Es lo que distingue "alguien pasó frente al detector" de "la puerta
    /// quedó abierta". 0 o null = ejecutar de inmediato.
    /// </summary>
    int? SustainedSeconds = null,
    /// <summary>Días de la semana en que la automatización está activa (0 = domingo).</summary>
    IReadOnlyList<int>? DaysOfWeek = null,
    /// <summary>Hora local de inicio de la ventana horaria ("22:00"); null = sin restricción.</summary>
    string? FromTime = null,
    /// <summary>Hora local de fin ("06:00"); si es menor que la de inicio, la ventana cruza la medianoche.</summary>
    string? ToTime = null);

/// <summary>
/// Acción de un workflow. <paramref name="Config"/> es la configuración
/// propia del tipo (objeto JSON libre); la contraseña, si la acción usa una,
/// va cifrada en la base y NUNCA se devuelve.
/// </summary>
public sealed record WorkflowActionDto(
    int Id,
    int Order,
    string Type,
    bool Enabled,
    /// <summary>Si falla, el workflow sigue con las acciones siguientes.</summary>
    bool ContinueOnError,
    /// <summary>Espera (s) antes de ejecutar esta acción.</summary>
    int DelaySeconds,
    JsonElement Config,
    /// <summary>La acción tiene una contraseña guardada.</summary>
    bool HasSecret);

/// <summary>
/// Alta/edición de una acción. <paramref name="Secret"/> null o vacío =
/// mantener la contraseña actual, que se identifica por <paramref name="Id"/>
/// (0 en las acciones nuevas).
/// </summary>
public sealed record WorkflowActionWriteDto(
    string Type,
    bool Enabled,
    bool ContinueOnError,
    int DelaySeconds,
    JsonElement Config,
    string? Secret = null,
    int Id = 0);

public sealed record WorkflowDto(
    int Id,
    string Name,
    string? Description,
    bool Enabled,
    string TriggerType,
    WorkflowConditionsDto Conditions,
    /// <summary>Tiempo mínimo (s) entre dos ejecuciones; 0 = sin límite.</summary>
    int CooldownSeconds,
    DateTime? LastRunAt,
    long RunCount,
    DateTime CreatedAt,
    string CreatedBy,
    IReadOnlyList<WorkflowActionDto> Actions);

public sealed record WorkflowWriteDto(
    string Name,
    string? Description,
    bool Enabled,
    string TriggerType,
    WorkflowConditionsDto? Conditions,
    int CooldownSeconds,
    IReadOnlyList<WorkflowActionWriteDto> Actions);

/// <summary>Resultado de una acción dentro de una ejecución.</summary>
public sealed record WorkflowRunStepDto(
    int Order,
    string Type,
    string Label,
    bool Success,
    string? Detail,
    int ElapsedMs,
    /// <summary>Rutas relativas de los archivos que produjo (fotos), servibles por /api/workflows/files.</summary>
    IReadOnlyList<string>? Files);

/// <summary>Ejecución de un workflow (historial).</summary>
public sealed record WorkflowRunDto(
    long Id,
    int WorkflowId,
    string WorkflowName,
    DateTime StartedAt,
    DateTime? FinishedAt,
    bool Success,
    /// <summary>Qué lo disparó, en una línea ("Panel 'Casa' · Alarma en zona 'Living'").</summary>
    string TriggerSummary,
    string? Error,
    /// <summary>"automático" o el usuario que la lanzó a mano (prueba).</summary>
    string StartedBy,
    IReadOnlyList<WorkflowRunStepDto> Steps);

public sealed record WorkflowTriggerInfoDto(string Key, string Label, string Description);

public sealed record WorkflowActionInfoDto(string Key, string Label, string Description,
    /// <summary>La acción puede llevar contraseña (se guarda cifrada).</summary>
    bool UsesSecret);

/// <summary>Marca reemplazable en los textos de las acciones ({panel}, {zona}...).</summary>
public sealed record WorkflowPlaceholderDto(string Key, string Label);

/// <summary>Todo lo que el panel necesita para armar el editor sin quemar listas en el JavaScript.</summary>
public sealed record WorkflowCatalogDto(
    IReadOnlyList<WorkflowTriggerInfoDto> Triggers,
    IReadOnlyList<WorkflowActionInfoDto> Actions,
    IReadOnlyList<WorkflowPlaceholderDto> Placeholders,
    /// <summary>Hay un servidor de correo configurado y habilitado.</summary>
    bool SmtpConfigured,
    /// <summary>El servidor tiene FFmpeg (necesario para convertir sonidos de parlante).</summary>
    bool FfmpegAvailable);

/// <summary>Seguridad del canal SMTP.</summary>
public enum SmtpSecurity
{
    /// <summary>Sin cifrado (puerto 25 interno).</summary>
    None,
    /// <summary>STARTTLS sobre el puerto normal (587).</summary>
    StartTls,
    /// <summary>TLS implícito desde la conexión (465).</summary>
    Ssl,
}

/// <summary>Servidor de correo saliente del VMS (uno solo para todo el sistema).</summary>
public sealed record SmtpSettingsDto(
    bool Enabled,
    string Host,
    int Port,
    SmtpSecurity Security,
    string Username,
    bool HasPassword,
    /// <summary>Se aceptan certificados no verificables (servidor interno).</summary>
    bool AllowInvalidCertificate,
    string FromAddress,
    string FromName,
    DateTime? UpdatedAt);

/// <summary>Password null o vacía = mantener la actual.</summary>
public sealed record SmtpSettingsWriteDto(
    bool Enabled,
    string Host,
    int Port,
    SmtpSecurity Security,
    string Username,
    string? Password,
    bool AllowInvalidCertificate,
    string FromAddress,
    string FromName);

public sealed record SmtpTestRequestDto(string To);

/// <summary>Cámara elegible en la acción "capturar foto" (lista plana de canales).</summary>
public sealed record WorkflowCameraDto(
    int ChannelId,
    int DeviceId,
    string DeviceName,
    string ChannelName,
    /// <summary>El driver del equipo sabe capturar imágenes.</summary>
    bool SupportsSnapshot);

/// <summary>Sonido disponible para la acción "parlante IP".</summary>
public sealed record WorkflowAudioDto(
    string FileName,
    string DisplayName,
    long Bytes,
    DateTime UploadedAt,
    /// <summary>Tiene la versión convertida a G.711 que exige el parlante.</summary>
    bool Ready);

/// <summary>
/// Alerta generada por una automatización: el aviso que se le muestra al
/// operador Y su acuse de recibo. Existe para poder responder "quién se dio
/// por enterado y cuándo": mientras nadie la confirme queda pendiente, y la
/// confirmación registra usuario, hora y origen.
/// </summary>
public sealed record WorkflowAlertDto(
    long Id,
    /// <summary>Ejecución que la generó (null si esa ejecución ya se purgó del historial).</summary>
    long? RunId,
    int WorkflowId,
    string WorkflowName,
    DateTime RaisedAt,
    string Title,
    string Message,
    AlarmSeverity Severity,
    /// <summary>Foto principal (ruta de /api/workflows/files/...).</summary>
    string? ImagePath,
    /// <summary>Todas las fotos capturadas en esa ejecución, en orden.</summary>
    IReadOnlyList<string> ImagePaths,
    /// <summary>Cámaras vinculadas: el cliente abre su video en vivo en la ventana de alarma.</summary>
    IReadOnlyList<int> ChannelIds,
    string? Sound,
    int SoundRepeat,
    /// <summary>Qué la disparó, en una línea.</summary>
    string TriggerSummary,
    /// <summary>Exige acuse de recibo: sigue pendiente hasta que alguien la confirme.</summary>
    bool RequiresAck,
    DateTime? AcknowledgedAt,
    string? AcknowledgedBy,
    /// <summary>"client" (escritorio) o "web" (panel).</summary>
    string? AcknowledgedFrom,
    /// <summary>Segundos entre el aviso y la confirmación.</summary>
    int? ResponseSeconds)
{
    public bool Pending => RequiresAck && AcknowledgedAt is null;
}

/// <summary>Listado de alertas con el total y cuántas siguen sin confirmar.</summary>
public sealed record WorkflowAlertListDto(int Total, int Pending, IReadOnlyList<WorkflowAlertDto> Items);

/// <summary>Aviso empujado por la acción "notificar a los operadores".</summary>
public sealed record WorkflowNotificationDto(
    int WorkflowId,
    string WorkflowName,
    string Title,
    string Message,
    AlarmSeverity Severity,
    DateTime At,
    /// <summary>
    /// Foto capturada en la misma ejecución (ruta relativa que sirve
    /// /api/workflows/files/...); null si la automatización no capturó
    /// ninguna. El cliente la descarga y la muestra en el aviso.
    /// </summary>
    string? ImagePath = null,
    /// <summary>
    /// Alarma sonora en el equipo del operador: nombre de un sonido cargado
    /// en el servidor (los mismos de los parlantes IP),
    /// <see cref="SystemSoundName"/> para el pitido del sistema, o null para
    /// un aviso silencioso.
    /// </summary>
    string? Sound = null,
    /// <summary>Cuántas veces se repite el sonido; 0 = hasta que alguien confirme la alerta.</summary>
    int SoundRepeat = 1,
    /// <summary>
    /// Alerta a la que corresponde este aviso: con ella el operador confirma
    /// que la vio (POST /api/workflows/alerts/{id}/ack). 0 = aviso sin
    /// registro (no debería ocurrir; se deja por compatibilidad).
    /// </summary>
    long AlertId = 0,
    /// <summary>El aviso queda en pantalla hasta que alguien lo confirme.</summary>
    bool RequiresAck = true)
{
    /// <summary>Valor de <see cref="Sound"/> que significa "pitido del sistema operativo".</summary>
    public const string SystemSoundName = "sistema";
}
