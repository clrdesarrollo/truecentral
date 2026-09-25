using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Panel de cerco eléctrico (ESP8266 con firmware CLRobotics). A diferencia de
/// <see cref="AlarmPanel"/>, el servidor NO se conecta al equipo: el panel se
/// conecta al servidor por WebSocket y se autentica con un PSK que el servidor
/// generó (modelo tipo ISUP: se crea ID + clave y se le asignan al panel). El
/// PSK se guarda cifrado con AES-256-GCM (ver <see cref="CredentialProtector"/>).
/// </summary>
public class CercoPanel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Identificador estable del panel ("CERCO-xxxxxxxxxxxx"), generado al crear.</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>Secreto compartido de 256 bits, cifrado. Base de la autenticación mutua.</summary>
    public byte[] PskCiphertext { get; set; } = [];

    /// <summary>Ubicación / sitio (texto libre para el operador).</summary>
    public string? Site { get; set; }

    /// <summary>El servidor acepta la conexión de este panel.</summary>
    public bool Enabled { get; set; } = true;

    // ---- estado reportado por el panel ----
    public CercoPanelStatus Status { get; set; } = CercoPanelStatus.Unknown;
    public bool Armed { get; set; }
    public bool Siren { get; set; }
    public bool HvOk { get; set; } = true;
    public bool FenceOk { get; set; } = true;
    public bool ArcFault { get; set; }
    public int? Voltage { get; set; }
    public int? GroundMs { get; set; }
    public int? Equipo { get; set; }
    public int? Rssi { get; set; }
    public bool Arming { get; set; }
    public bool KeyOn { get; set; }
    public bool RfLearning { get; set; }
    public int? Zone0Adc { get; set; }
    /// <summary>La configuración reportada por el panel coincide con la guardada aquí.</summary>
    public bool ConfigSynced { get; set; }
    public CercoPowerSource PowerSource { get; set; } = CercoPowerSource.Unknown;
    public int? PowerDropPermille { get; set; }
    public int? ReturnUs { get; set; }

    // ---- configuración deseada (fuente de verdad; se envía al panel) ----
    public int HvLevel { get; set; } = 21;
    public int SirenSeconds { get; set; } = 180;
    public int ExitDelaySeconds { get; set; }
    public bool Chirp { get; set; } = true;
    public CercoKeyMode KeyMode { get; set; } = CercoKeyMode.Off;
    public CercoZoneMode Zone0Mode { get; set; } = CercoZoneMode.Off;
    public bool Zone0BlocksArm { get; set; } = true;

    // ---- identificación reportada en el handshake ----
    public string? Model { get; set; }
    public string? Firmware { get; set; }
    public string? Mac { get; set; }

    public string? LastError { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? LastStateAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Contador de comandos salientes firmados (anti-replay en el equipo).</summary>
    public long CmdSeq { get; set; }
    /// <summary>Último seq de evento entrante aceptado (anti-replay de eventos).</summary>
    public long LastEventSeq { get; set; }

    public List<CercoZone> Zones { get; set; } = [];
    public List<CercoRemote> Remotes { get; set; } = [];
}

/// <summary>
/// Botón de control RF 433 MHz programado en el panel. El panel guarda los códigos
/// (fuente de verdad) y reporta la lista; aquí se sincroniza y se le agrega el
/// nombre que el operador le dio.
/// </summary>
public class CercoRemote
{
    public int Id { get; set; }
    public int CercoPanelId { get; set; }
    public CercoPanel CercoPanel { get; set; } = null!;
    /// <summary>Posición en la memoria del panel (0..31).</summary>
    public int Slot { get; set; }
    /// <summary>Código recibido, en hexadecimal.</summary>
    public string Code { get; set; } = "";
    public int Bits { get; set; }
    public CercoRfAction Action { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Zona (Z00..Z31) de un panel de cerco, con su último estado conocido.</summary>
public class CercoZone
{
    public int Id { get; set; }
    public int CercoPanelId { get; set; }
    public CercoPanel CercoPanel { get; set; } = null!;
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool InAlarm { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Evento de un panel de cerco. Sin FK al panel para que el historial sobreviva a
/// su eliminación (el nombre queda copiado), igual que <see cref="AlarmEvent"/>.
/// </summary>
public class CercoEvent
{
    public long Id { get; set; }
    public int CercoPanelId { get; set; }
    public string PanelName { get; set; } = "";
    /// <summary>Hora del evento según el panel, en UTC.</summary>
    public DateTime Timestamp { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public CercoEventKind Kind { get; set; }
    public CercoSeverity Severity { get; set; }
    public string Description { get; set; } = "";
    public int? ZoneNumber { get; set; }
    public string? ZoneName { get; set; }
    /// <summary>true si el HMAC del evento se verificó con la clave de sesión.</summary>
    public bool Verified { get; set; }
    /// <summary>Notificación cruda del panel (diagnóstico).</summary>
    public string? RawJson { get; set; }
}
