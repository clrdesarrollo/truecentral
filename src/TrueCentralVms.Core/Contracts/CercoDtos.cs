namespace TrueCentralVms.Core.Contracts;

// DTOs del módulo Paneles de cerco eléctrico. A diferencia de los paneles de
// alarma (el servidor se conecta al equipo), aquí el panel es un ESP8266 que se
// conecta AL servidor por WebSocket y se autentica con un secreto (PSK) que el
// servidor generó y que el instalador le asignó al equipo (modelo tipo ISUP:
// se crea un ID + clave y luego se le asignan al panel). Los enums viajan como
// texto en JSON.

/// <summary>Estado de conexión del panel de cerco con el servidor.</summary>
public enum CercoPanelStatus { Unknown, Online, Offline }

/// <summary>Naturaleza de un evento empujado por el panel de cerco.</summary>
public enum CercoEventKind
{
    Boot,
    Armed,
    Disarmed,
    /// <summary>Disparo de alarma en una zona.</summary>
    Alarm,
    /// <summary>Caída/corte del cerco (integridad perdida).</summary>
    FenceCut,
    /// <summary>Falla de alto voltaje del energizador.</summary>
    HvFault,
    /// <summary>Detección de arcos.</summary>
    Arc,
    SirenOn,
    SirenOff,
    /// <summary>Control remoto 433 MHz recibido.</summary>
    RfRemote,
    /// <summary>Sabotaje / apertura del gabinete.</summary>
    Tamper,
    /// <summary>Armado rechazado: sin retorno del cerco o zona abierta.</summary>
    ArmFailed,
    /// <summary>Una zona volvió a normal.</summary>
    ZoneRestore,
    /// <summary>Pánico disparado desde un control RF.</summary>
    Panic,
    /// <summary>Se programó un botón de control RF.</summary>
    RfLearned,
    /// <summary>La ventana de programación de control terminó sin recibir un botón.</summary>
    RfLearnTimeout,
    /// <summary>Corte de red: el panel opera con batería (deducido del pulso del energizador).</summary>
    PowerLost,
    /// <summary>Volvió la red eléctrica.</summary>
    PowerRestored,
}

/// <summary>
/// Alimentación del panel. La placa no mide la batería: el panel la deduce del ancho
/// del pulso de retorno del energizador, así que solo se conoce con el cerco armado.
/// </summary>
public enum CercoPowerSource { Unknown = 0, Mains = 1, Battery = 2 }

public enum CercoSeverity { Info, Warning, Critical }

/// <summary>Comportamiento de la entrada de llave (GPIO5). Valores = firmware.</summary>
public enum CercoKeyMode { Off = 0, Level = 1, Toggle = 2 }

/// <summary>Comportamiento de una zona cableada. Valores = firmware.</summary>
public enum CercoZoneMode { Off = 0, Instant = 1, H24 = 2 }

/// <summary>Acción de un botón de control RF 433 MHz. Valores = firmware.</summary>
public enum CercoRfAction { None = 0, Arm = 1, Disarm = 2, Toggle = 3, Panic = 4, Silence = 5 }

/// <summary>
/// Configuración del cerco. TrueCentral es la fuente de verdad: se guarda en la BD y
/// se envía al panel (comando firmado "config") al guardar y en cada reconexión.
/// </summary>
public sealed record CercoConfigDto(
    /// <summary>Nivel Voltaje 7..21 (tiempo de carga del energizador).</summary>
    int HvLevel,
    /// <summary>Duración de la sirena de alarma, 10..900 s.</summary>
    int SirenSeconds,
    /// <summary>Retardo de salida antes de energizar, 0..120 s.</summary>
    int ExitDelaySeconds,
    /// <summary>Chirp de sirena al armar/desarmar.</summary>
    bool Chirp,
    CercoKeyMode KeyMode,
    CercoZoneMode Zone0Mode,
    /// <summary>La zona 0 abierta impide armar.</summary>
    bool Zone0BlocksArm);

/// <summary>Botón de control RF programado en el panel (el panel guarda los códigos).</summary>
public sealed record CercoRemoteDto(int Slot, string Name, string Code, int Bits, CercoRfAction Action, DateTime CreatedAt);

/// <summary>Iniciar la programación de un botón: el próximo botón recibido queda con esta acción.</summary>
public sealed record CercoRfLearnDto(string Name, CercoRfAction Action);

/// <summary>Renombrar un botón programado.</summary>
public sealed record CercoRemoteRenameDto(string Name);

/// <summary>Zona del cerco (Z00..Z31) con su último estado conocido.</summary>
public sealed record CercoZoneDto(int Number, string Name, bool Enabled, bool InAlarm);

/// <summary>
/// Panel de cerco tal como lo ven el cliente y el panel web. NUNCA incluye el PSK
/// (ese solo se entrega una vez, al crear o rotar la clave).
/// </summary>
public sealed record CercoPanelDto(
    int Id,
    string Name,
    string DeviceId,
    string? Site,
    bool Enabled,
    CercoPanelStatus Status,
    /// <summary>Hay una conexión WebSocket viva y autenticada ahora mismo.</summary>
    bool Connected,
    bool Armed,
    bool Siren,
    bool HvOk,
    bool FenceOk,
    bool ArcFault,
    int? Voltage,
    int? Rssi,
    string? Model,
    string? Firmware,
    string? Mac,
    DateTime? LastSeenAt,
    DateTime? LastStateAt,
    IReadOnlyList<CercoZoneDto> Zones,
    /// <summary>Retardo de salida o verificación de retorno en curso.</summary>
    bool Arming = false,
    /// <summary>Llave cerrada.</summary>
    bool KeyOn = false,
    /// <summary>Ventana de programación de control RF abierta.</summary>
    bool RfLearning = false,
    /// <summary>Lectura cruda de la zona 0 (A0), para calibrar la resistencia de fin de línea.</summary>
    int? Zone0Adc = null,
    /// <summary>La configuración aplicada en el panel coincide con la guardada.</summary>
    bool ConfigSynced = false,
    CercoPowerSource PowerSource = CercoPowerSource.Unknown,
    /// <summary>Caída del pulso de retorno respecto de la referencia con red, en por mil.</summary>
    int? PowerDropPermille = null,
    /// <summary>Ancho medio del pulso de retorno (µs).</summary>
    int? ReturnUs = null);

/// <summary>
/// Credenciales generadas por el servidor al crear o rotar la clave de un panel.
/// Se muestran UNA sola vez; el instalador las carga en el equipo con la
/// herramienta de provisioning (SoftAP). El servidor guarda el PSK cifrado.
/// </summary>
public sealed record CercoPanelCredentialsDto(
    string DeviceId,
    /// <summary>PSK de 256 bits en base64. No se vuelve a mostrar.</summary>
    string Psk,
    /// <summary>Sugerencia de URL WebSocket a cargar en el panel.</summary>
    string WsUrlHint);

/// <summary>Alta/edición de un panel de cerco (el PSK no se envía; lo genera el servidor).</summary>
public sealed record CercoPanelUpsertDto(string Name, string? Site, bool Enabled = true);

/// <summary>Evento de un panel de cerco para el historial/monitor.</summary>
public sealed record CercoEventDto(
    long Id,
    int PanelId,
    string PanelName,
    DateTime Timestamp,
    DateTime ReceivedAt,
    CercoEventKind Kind,
    CercoSeverity Severity,
    string Description,
    int? ZoneNumber,
    string? ZoneName,
    /// <summary>true si el HMAC del evento se verificó con la clave de sesión.</summary>
    bool Verified);

/// <summary>Página del historial de eventos de cerco.</summary>
public sealed record CercoEventPageDto(long Total, int Page, int PageSize, IReadOnlyList<CercoEventDto> Items);

/// <summary>Comando del operador hacia un panel (armar/desarmar/silenciar/zona).</summary>
public sealed record CercoCommandDto(string Command, int? Zone = null, bool? On = null);
