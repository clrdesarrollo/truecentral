namespace TrueCentralVms.Core.Contracts;

// ---------------------------------------------------------------------------
// Pantallas de alarma: las Raspberry Pi de las casetas de guardia que muestran
// los avisos de las automatizaciones (con sonido, foto y consignas) y dejan
// marcarlos como vistos. Se conectan DIRECTO al servidor: no hay servidor
// intermedio ni login en la pantalla — se emparejan una vez con un código de
// 6 dígitos y desde ahí arrancan solas con su credencial.
//
// Estos contratos los comparten el servidor y la app de la pantalla
// (TrueCentralVms.AlarmScreen): un nombre mal escrito en un extremo no da
// error de compilación, solo una alarma que nunca llega.
// ---------------------------------------------------------------------------

/// <summary>Rutas, cabeceras, puertos y mensajes del hub de las pantallas.</summary>
public static class AlarmScreenProtocol
{
    /// <summary>Hub de las pantallas (aparte del de los operadores: otra credencial, otros mensajes).</summary>
    public const string HubPath = "/hubs/screens";

    /// <summary>Credencial de la pantalla. En el hub también se acepta como <c>?access_token=</c>.</summary>
    public const string TokenHeader = "X-Screen-Token";

    /// <summary>
    /// Autodiscovery por UDP broadcast. Sirve dentro de una misma subred: el
    /// broadcast <b>no cruza VLANs</b>, así que la pantalla siempre permite
    /// escribir la dirección del servidor a mano.
    /// </summary>
    public const int DiscoveryPort = 5094;

    /// <summary>Lo que emite la pantalla en broadcast para encontrar el servidor.</summary>
    public const string DiscoveryProbe = "TCVMS_SCREEN_DISCOVER";

    // ---- servidor → pantalla ----

    /// <summary>Aviso nuevo. Parámetro: <see cref="AlarmScreenAlertDto"/>.</summary>
    public const string AlarmRaised = "AlarmRaised";

    /// <summary>El aviso cambió (p. ej. llegó su foto). Parámetro: <see cref="AlarmScreenAlertDto"/>.</summary>
    public const string AlarmUpdated = "AlarmUpdated";

    /// <summary>
    /// Alguien lo confirmó —otra pantalla o un operador en TrueCentral—: la
    /// pantalla deja de sonar. Parámetro: <see cref="AlarmScreenAckDto"/>.
    /// </summary>
    public const string AlarmAcknowledged = "AlarmAcknowledged";

    /// <summary>«Todas vistas». Parámetro: <see cref="AlarmScreenBulkAckDto"/>.</summary>
    public const string AlarmsBulkAcknowledged = "AlarmsBulkAcknowledged";

    /// <summary>Cambiaron sonidos, volumen o disposición: la pantalla recarga su configuración.</summary>
    public const string ConfigChanged = "ConfigChanged";

    /// <summary>Reinicio del equipo ordenado desde el panel (va solo a esa pantalla).</summary>
    public const string Reboot = "Reboot";

    // ---- pantalla → servidor ----

    /// <summary>La pantalla recibió el aviso (constancia de entrega por pantalla).</summary>
    public const string ConfirmDelivery = "ConfirmDelivery";

    /// <summary>Igual, en lote: lo que recibió al resincronizar después de una caída.</summary>
    public const string ConfirmDeliveries = "ConfirmDeliveries";

    /// <summary>El guardia lo marcó como visto: se cierra en todas partes.</summary>
    public const string Acknowledge = "Acknowledge";

    /// <summary>Marca como vistos todos los avisos pendientes de esa pantalla.</summary>
    public const string AcknowledgeAll = "AcknowledgeAll";

    /// <summary>Señal de vida (cada 30 s). Sin ella, una pantalla colgada parece una caseta tranquila.</summary>
    public const string Heartbeat = "Heartbeat";
}

/// <summary>Estados de una pantalla (y de su emparejamiento).</summary>
public static class AlarmScreenStatus
{
    /// <summary>Se anunció y espera que un administrador la apruebe con el código que muestra.</summary>
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    /// <summary>Aprobada pero apagada desde el panel: no recibe avisos ni puede conectarse.</summary>
    public const string Disabled = "disabled";
}

/// <summary>Disposición del monitor de la pantalla.</summary>
public static class AlarmScreenLayouts
{
    /// <summary>Monitor apaisado: lista a la izquierda, detalle a la derecha.</summary>
    public const string Horizontal = "horizontal";

    /// <summary>Monitor en pie: detalle arriba, lista abajo.</summary>
    public const string Vertical = "vertical";

    public static bool IsVertical(string? layout) =>
        string.Equals(layout, Vertical, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Dónde se muestra el aviso de la acción «Avisar a los operadores».</summary>
public static class NotifyDestinations
{
    /// <summary>Ventana de alarma del cliente de escritorio (lo de siempre).</summary>
    public const string Popup = "popup";
    /// <summary>Solo las pantallas de alarma de las casetas.</summary>
    public const string Screens = "screens";
    /// <summary>Las dos: confirmarlo en cualquiera lo cierra en la otra.</summary>
    public const string Both = "both";

    public static bool IsValid(string? value) => value is Popup or Screens or Both;
    public static bool ShowsPopup(string? value) => value is not Screens;
    public static bool ShowsOnScreens(string? value) => value is Screens or Both;
}

// ---------------------------------------------------------------------------
// Lo que viaja a la pantalla
// ---------------------------------------------------------------------------

/// <summary>Un aviso tal como lo muestra la pantalla. Es el contrato del hub y de la resincronización.</summary>
public sealed record AlarmScreenAlertDto
{
    /// <summary>Id de la alerta en TrueCentral (la misma que confirma el operador).</summary>
    public long Id { get; init; }

    public string Title { get; init; } = "";
    public string Message { get; init; } = "";

    /// <summary>Ubicación del recurso que lo originó ("Casa matriz › Edificio A"), si tiene ficha.</summary>
    public string? LocationPath { get; init; }

    /// <summary>Consignas de la ficha del recurso: lo que debe hacer el guardia.</summary>
    public string? Instructions { get; init; }

    /// <summary>Automatización que lo emitió.</summary>
    public string WorkflowName { get; init; } = "";

    /// <summary>1 alta (crítica) · 2 media (advertencia) · 3 baja (informativa).</summary>
    public int Priority { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>Ruta relativa de la foto principal (hay que presentar la credencial), o null.</summary>
    public string? PhotoUrl { get; init; }

    /// <summary>Sonido propio del aviso (nombre en <see cref="AlarmScreenConfigDto.Sounds"/>); null = el general.</summary>
    public string? SoundFile { get; init; }

    public bool Acknowledged { get; init; }
    public DateTimeOffset? AcknowledgedAt { get; init; }

    /// <summary>Quién lo confirmó: el nombre de la pantalla o el usuario de TrueCentral.</summary>
    public string? AcknowledgedBy { get; init; }

    /// <summary>"screen" (una pantalla), "client" (escritorio) o "web" (panel).</summary>
    public string? AcknowledgedFrom { get; init; }
}

/// <summary>Confirmación difundida a las pantallas.</summary>
public sealed record AlarmScreenAckDto(long AlertId, string? By, string? From, DateTimeOffset At);

/// <summary>Confirmación en lote («todas vistas»).</summary>
public sealed record AlarmScreenBulkAckDto(IReadOnlyList<long> AlertIds, string? By, string? From, DateTimeOffset At);

/// <summary>Un sonido de la biblioteca del servidor que la pantalla descarga y guarda por su hash.</summary>
public sealed record AlarmScreenSoundDto
{
    /// <summary>Nombre del sonido (sin extensión), como lo nombran los avisos y la configuración.</summary>
    public string Name { get; init; } = "";

    /// <summary>Extensión del archivo (".wav", ".mp3"...), para reproducirlo.</summary>
    public string Extension { get; init; } = "";

    /// <summary>Ruta relativa de descarga (hay que presentar la credencial).</summary>
    public string Url { get; init; } = "";

    public string Sha256 { get; init; } = "";
    public long Size { get; init; }
}

/// <summary>
/// Configuración de la pantalla. Los sonidos son del sitio (iguales en todas:
/// un guardia que cambia de caseta tiene que oír lo mismo); volumen, salida de
/// audio y disposición son de cada equipo.
/// </summary>
public sealed record AlarmScreenConfigDto
{
    /// <summary>Nombre de la pantalla (puede cambiarse desde el panel con la pantalla conectada).</summary>
    public string ScreenName { get; init; } = "";

    /// <summary>Nombre de la instalación.</summary>
    public string SiteName { get; init; } = "";

    /// <summary>0–100. Se aplica al mezclador del equipo.</summary>
    public int Volume { get; init; } = 80;

    /// <summary><see cref="AlarmScreenLayouts"/>.</summary>
    public string Layout { get; init; } = AlarmScreenLayouts.Horizontal;

    /// <summary>Salida de audio por nombre (HDMI, jack, USB); vacío = la del sistema.</summary>
    public string? AudioOutput { get; init; }

    /// <summary>Cada cuántos segundos recordar que hay avisos sin ver (0 = solo al llegar).</summary>
    public int RepeatBeepSeconds { get; init; } = 10;

    /// <summary>Cuántas veces insistir. 0 = sin límite: suena hasta que se marque todo.</summary>
    public int MaxReminders { get; init; }

    /// <summary>Sonido de aviso nuevo. Vacío = el tono generado por la pantalla.</summary>
    public string? AlarmSound { get; init; }

    /// <summary>Sonido para los avisos críticos. Vacío = el de aviso.</summary>
    public string? HighPrioritySound { get; init; }

    public string? ReminderSound { get; init; }

    /// <summary>Todos los sonidos disponibles (la pantalla baja los que le falten).</summary>
    public IReadOnlyList<AlarmScreenSoundDto> Sounds { get; init; } = [];
}

// ---------------------------------------------------------------------------
// Descubrimiento, emparejamiento y sesión
// ---------------------------------------------------------------------------

/// <summary>Respuesta del servidor a un <see cref="AlarmScreenProtocol.DiscoveryProbe"/>.</summary>
public sealed record AlarmScreenDiscoveryResponse
{
    /// <summary>Nombre de la instalación, para elegir si hay más de un servidor en la red.</summary>
    public string Name { get; init; } = "";

    /// <summary>URL base del servidor (<c>http://192.168.1.50:5090</c>).</summary>
    public string Url { get; init; } = "";

    public string Version { get; init; } = "";

    /// <summary>Identidad estable del servidor: evita que la pantalla salte entre instalaciones.</summary>
    public string ServerId { get; init; } = "";
}

/// <summary>Alta de la pantalla: se anuncia y recibe el código que muestra para que la aprueben.</summary>
public sealed record AlarmScreenPairStartRequest(string MachineId, string Hostname, string? Version);

public sealed record AlarmScreenPairStartResponse(int ScreenId, string PairingCode, string Status);

/// <summary>
/// Sondeo del emparejamiento. La credencial se entrega UNA vez, a quien
/// presente el MachineId y el código que muestra la pantalla; después el
/// código deja de servir.
/// </summary>
public sealed record AlarmScreenPairStatusResponse
{
    public string Status { get; init; } = AlarmScreenStatus.Pending;

    /// <summary>Solo en la primera consulta tras la aprobación. Guardar y no perder.</summary>
    public string? Token { get; init; }

    public int? ScreenId { get; init; }
    public string? ScreenName { get; init; }
    public string? SiteName { get; init; }
}

/// <summary>Lo que la pantalla recibe al presentarse con su credencial.</summary>
public sealed record AlarmScreenSessionDto
{
    public int ScreenId { get; init; }
    public string ScreenName { get; init; } = "";
    public string SiteName { get; init; } = "";
    public string ServerVersion { get; init; } = "";
}

/// <summary>Versión de la app de la pantalla publicada en el servidor (actualización remota).</summary>
public sealed record AlarmScreenClientVersionDto
{
    public string Version { get; init; } = "";

    /// <summary>Ruta relativa de descarga (hay que presentar la credencial).</summary>
    public string DownloadUrl { get; init; } = "";

    public string Sha256 { get; init; } = "";
    public long Size { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
}

// ---------------------------------------------------------------------------
// Administración (panel web)
// ---------------------------------------------------------------------------

/// <summary>Una pantalla tal como la ve el administrador.</summary>
public sealed record AlarmScreenDto(
    int Id,
    string Name,
    /// <summary><see cref="AlarmScreenStatus"/>.</summary>
    string Status,
    string MachineId,
    string Hostname,
    string? Version,
    /// <summary>Código que muestra mientras espera la aprobación (null una vez emparejada).</summary>
    string? PairingCode,
    /// <summary>Tiene su credencial (ya reclamó el token después de aprobada).</summary>
    bool Paired,
    /// <summary>Conectada al hub en este momento.</summary>
    bool Online,
    DateTime? LastSeenAt,
    string? LastIp,
    int Volume,
    string Layout,
    string? AudioOutput,
    /// <summary>Salidas de audio que informó el equipo al conectarse.</summary>
    IReadOnlyList<string> AudioOutputs,
    DateTime CreatedAt,
    DateTime? ApprovedAt,
    string? ApprovedBy,
    /// <summary>Avisos dirigidos a esta pantalla que siguen sin confirmar.</summary>
    int PendingAlerts);

/// <summary>Aprobar una pantalla que pidió emparejarse: con qué nombre queda.</summary>
public sealed record AlarmScreenApproveDto(string Name);

/// <summary>Cambios de una pantalla desde el panel.</summary>
public sealed record AlarmScreenWriteDto(string Name, int Volume, string Layout, string? AudioOutput);

/// <summary>Ajustes comunes a todas las pantallas.</summary>
public sealed record AlarmScreenSettingsDto(
    /// <summary>Nombre de la instalación que muestran las pantallas ("Planta Santiago").</summary>
    string SiteName,
    int RepeatBeepSeconds,
    int MaxReminders,
    string? AlarmSound,
    string? HighPrioritySound,
    string? ReminderSound,
    /// <summary>Horas de avisos que la pantalla trae al reconectarse.</summary>
    int ResyncHours,
    /// <summary>Versión de la app publicada para la actualización remota (null = ninguna).</summary>
    string? PublishedClientVersion = null);

/// <summary>Pantalla elegible como destino en el editor de automatizaciones.</summary>
public sealed record AlarmScreenOptionDto(int Id, string Name, bool Online, bool Enabled);
