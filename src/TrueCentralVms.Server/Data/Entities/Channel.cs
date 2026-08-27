namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Canal de video de un dispositivo, autopoblado al validar credenciales.
/// <see cref="ChannelNumber"/> es el número interno del SDK;
/// <see cref="RtspChannel"/> es el índice que usan las URL RTSP del fabricante.
/// </summary>
public class Channel
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public Device Device { get; set; } = null!;
    public int ChannelNumber { get; set; }
    public int RtspChannel { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Visible para los operadores (editable en el mantenedor).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Reportado por el equipo en el último sondeo.</summary>
    public bool IsOnline { get; set; }
    /// <summary>El equipo reporta PTZ en este canal (el cliente muestra el control solo si es true).</summary>
    public bool SupportsPtz { get; set; }
    /// <summary>
    /// URLs RTSP SIN credenciales resueltas por el driver en el sondeo (ONVIF:
    /// GetStreamUri). Null = el driver usa su plantilla por marca.
    /// </summary>
    public string? RtspMainUrl { get; set; }
    public string? RtspSubUrl { get; set; }
}
