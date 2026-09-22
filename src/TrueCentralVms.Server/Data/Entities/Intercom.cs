using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Frente de citofonía (videoportero de calle, ej. Hikvision DS-KB8113). El
/// servidor mantiene abierto su enlace de llamadas: cuando alguien toca el
/// timbre la llamada suena en los clientes y un operador la contesta con voz
/// bidireccional y apertura de puerta. El video sale de un canal normal del
/// VMS (<see cref="ChannelId"/>): el frente se da de alta también como cámara.
/// La contraseña se guarda cifrada con AES-256-GCM (ver <see cref="CredentialProtector"/>).
/// </summary>
public class Intercom
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Clave del driver ("hikvision-intercom").</summary>
    public string DriverKey { get; set; } = "hikvision-intercom";
    public string Host { get; set; } = "";
    /// <summary>Puerto del SDK (señalización y voz).</summary>
    public int Port { get; set; } = 8000;
    /// <summary>Puerto HTTP (ISAPI).</summary>
    public int HttpPort { get; set; } = 80;
    public string Username { get; set; } = "";
    public byte[] PasswordCiphertext { get; set; } = [];
    /// <summary>Grupo lógico (portería, acceso norte...); libre.</summary>
    public string? GroupName { get; set; }

    /// <summary>Canal de video que muestra la cámara del frente (null = sin video).</summary>
    public int? ChannelId { get; set; }
    public Channel? Channel { get; set; }

    // Datos obtenidos del frente al validar credenciales.
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? FirmwareVersion { get; set; }
    public int DoorCount { get; set; } = 1;
    /// <summary>El botón del frente llama a la central (sin esto las llamadas no llegan al VMS).</summary>
    public bool CallCenterEnabled { get; set; }

    /// <summary>El servidor mantiene su enlace de llamadas y lo ofrece a los operadores.</summary>
    public bool Enabled { get; set; } = true;

    public IntercomStatus Status { get; set; } = IntercomStatus.Unknown;
    public string? LastError { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Llamada de citofonía (historial). Se crea al sonar el timbre y se cierra
/// al colgar, al rechazarla o cuando nadie contesta.
/// </summary>
public class IntercomCall
{
    public long Id { get; set; }
    public int IntercomId { get; set; }
    /// <summary>Nombre del frente al momento de la llamada (sobrevive a su borrado).</summary>
    public string IntercomName { get; set; } = "";
    public IntercomCallState State { get; set; } = IntercomCallState.Ringing;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AnsweredAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int? AnsweredByUserId { get; set; }
    public string? AnsweredBy { get; set; }
    public string? Origin { get; set; }
    public string? EndReason { get; set; }
    public bool DoorOpened { get; set; }
    public string? DoorOpenedBy { get; set; }
}
