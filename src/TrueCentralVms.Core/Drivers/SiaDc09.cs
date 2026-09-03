using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Core.Drivers;

/// <summary>
/// Trama SIA DC-09 (ANSI/SIA DC-09, "IP reporting") tal como la manda un
/// panel a un centro receptor de alarmas (ARC):
/// <c>LF CRC 0LLL "TOKEN" seq R rcvr L line #acct [datos] [extendido] _hora CR</c>.
/// </summary>
/// <param name="Token">"ADM-CID" (Contact-ID sobre IP), "SIA-DCS" o "NULL" (latido/test de enlace).</param>
/// <param name="Encrypted">true si el token venía con asterisco ("*ADM-CID"): cifrado AES, no soportado.</param>
public sealed record SiaDc09Frame(
    string Token,
    bool Encrypted,
    string Sequence,
    string Receiver,
    string Line,
    string Account,
    string Data,
    string? Extended,
    string? Timestamp,
    string Raw);

/// <summary>
/// Protocolo SIA DC-09 del lado receptor: valida la trama (CRC y largo),
/// construye las respuestas ACK/NAK/DUH y traduce el contenido —ADM-CID con
/// <see cref="ContactIdCatalog"/>, SIA-DCS con un catálogo propio— a
/// <see cref="AlarmPanelEvent"/>. Es independiente de la marca: cualquier
/// panel que reporte por DC-09 (Hikvision, Ajax, Paradox, DSC...) entra por
/// aquí. Versión sin cifrado; las tramas con asterisco se rechazan con DUH
/// para que el instalador configure el canal en claro.
/// </summary>
public static partial class SiaDc09
{
    [GeneratedRegex("^\"(\\*?)([A-Za-z0-9-]+)\"(\\d{4})(?:R([0-9A-Fa-f]{1,6}))?L([0-9A-Fa-f]{1,6})(?:#([0-9A-Za-z]{1,16}))?\\[([^\\]]*)\\](?:\\[([^\\]]*)\\])?(?:_(\\S+))?$")]
    private static partial Regex FramePattern();

    /// <summary>ADM-CID: "#acct|EEEE GG ZZZ" (calificador+código, grupo/partición, zona o usuario).</summary>
    [GeneratedRegex("\\|\\s*(\\d)\\s*(\\d{3})\\s+(\\d{1,2})\\s+(\\d{1,3})")]
    private static partial Regex AdmCidPattern();

    /// <summary>SIA-DCS: "#acct|N ri1 id02 /BA002" (modificadores opcionales y código de dos letras + número).</summary>
    [GeneratedRegex("(?:ri(\\d+))?(?:id(\\d+))?\\s*/\\s*([A-Z]{2})(\\d*)")]
    private static partial Regex SiaDcsPattern();

    /// <summary>
    /// Interpreta una trama (con o sin LF/CR y con o sin cabecera CRC+largo).
    /// Devuelve false con el motivo en español si no es DC-09 o no cuadra el
    /// CRC/largo: el receptor debe contestar NAK.
    /// </summary>
    public static bool TryParse(string raw, out SiaDc09Frame? frame, out string? error)
    {
        frame = null;
        error = null;
        string text = raw.Trim('\n', '\r', ' ', '\0');
        int quote = text.IndexOf('"');
        if (quote < 0)
        {
            error = "la trama no trae token entre comillas";
            return false;
        }
        string header = text[..quote];
        string message = text[quote..];
        if (header.Length == 8)
        {
            if (!int.TryParse(header.AsSpan(4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int length)
                || length != message.Length)
            {
                error = $"largo declarado {header[4..]} no coincide con el contenido ({message.Length})";
                return false;
            }
            string crc = Crc16Hex(message);
            if (!string.Equals(crc, header[..4], StringComparison.OrdinalIgnoreCase))
            {
                error = $"CRC inválido (recibido {header[..4]}, calculado {crc})";
                return false;
            }
        }
        else if (header.Length != 0)
        {
            error = $"cabecera inesperada «{header}»";
            return false;
        }

        var m = FramePattern().Match(message);
        if (!m.Success)
        {
            error = "formato de trama no reconocido";
            return false;
        }
        frame = new SiaDc09Frame(
            m.Groups[2].Value.ToUpperInvariant(),
            m.Groups[1].Value == "*",
            m.Groups[3].Value,
            m.Groups[4].Success ? m.Groups[4].Value : "0",
            m.Groups[5].Value,
            m.Groups[6].Success ? m.Groups[6].Value : "",
            m.Groups[7].Value,
            m.Groups[8].Success ? m.Groups[8].Value : null,
            m.Groups[9].Success ? m.Groups[9].Value : null,
            text);
        return true;
    }

    /// <summary>Confirmación positiva: mismo número de secuencia, receptor, línea y cuenta.</summary>
    public static string BuildAck(SiaDc09Frame frame) => Build("ACK", frame.Sequence, frame.Receiver, frame.Line, frame.Account, false);

    /// <summary>Rechazo (CRC/largo/formato inválidos o remitente desconocido): el panel reintenta.</summary>
    public static string BuildNak() => Build("NAK", "0000", "0", "0", "", true);

    /// <summary>"No entendido" (token cifrado o desconocido): el panel no reintenta la misma trama.</summary>
    public static string BuildDuh(SiaDc09Frame frame) => Build("DUH", frame.Sequence, frame.Receiver, frame.Line, frame.Account, true);

    private static string Build(string token, string sequence, string receiver, string line, string account, bool timestamp)
    {
        var message = new StringBuilder()
            .Append('"').Append(token).Append('"')
            .Append(sequence)
            .Append('R').Append(receiver)
            .Append('L').Append(line);
        if (account.Length > 0) message.Append('#').Append(account);
        message.Append("[]");
        if (timestamp)
            message.Append('_').Append(DateTime.UtcNow.ToString("HH:mm:ss,MM-dd-yyyy", CultureInfo.InvariantCulture));
        string body = message.ToString();
        return "\n" + Crc16Hex(body) + "0" + body.Length.ToString("X3", CultureInfo.InvariantCulture) + body + "\r";
    }

    /// <summary>CRC-16 de DC-09 (polinomio 0x8005 reflejado = 0xA001, inicial 0), en 4 hexadecimales mayúsculas.</summary>
    public static string Crc16Hex(string message)
    {
        ushort crc = 0;
        foreach (byte b in Encoding.ASCII.GetBytes(message))
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc.ToString("X4", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Traduce el contenido de la trama a un evento del panel. Devuelve null
    /// para "NULL" (latido) y para tokens que no se entienden. La hora del
    /// evento es la del servidor: la que manda el panel queda en
    /// <see cref="AlarmPanelEvent.Raw"/> (los equipos no siempre la ponen en
    /// UTC como pide la norma).
    /// </summary>
    /// <param name="zoneNumbersStartAt">Número con el que el panel reporta su
    /// primera zona (1 en Hikvision) para convertirlo al identificador que usa
    /// su API (que parte en 0).</param>
    public static AlarmPanelEvent? ToEvent(SiaDc09Frame frame, int zoneNumbersStartAt, DateTime now)
    {
        if (frame.Encrypted) return null;
        return frame.Token switch
        {
            "ADM-CID" => FromContactId(frame, zoneNumbersStartAt, now),
            "SIA-DCS" => FromSia(frame, zoneNumbersStartAt, now),
            _ => null, // NULL (latido) u otro token
        };
    }

    private static AlarmPanelEvent? FromContactId(SiaDc09Frame frame, int zoneStart, DateTime now)
    {
        var m = AdmCidPattern().Match(frame.Data);
        if (!m.Success) return null;
        string code = m.Groups[1].Value + m.Groups[2].Value;
        int group = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        int number = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
        int eventCode = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);

        AlarmEventKind kind;
        AlarmSeverity severity;
        string description;
        if (ContactIdCatalog.Translate(code) is { } cid)
            (kind, severity, description) = cid;
        else
            (kind, severity, description) = (AlarmEventKind.Info, AlarmSeverity.Info, $"Evento Contact-ID {code}");

        // En Contact-ID el tercer campo es la zona, salvo en apertura/cierre
        // (4xx) y en algunos eventos de sistema donde es el número de usuario.
        int? area = group > 0 ? group : null;
        int? zone = null;
        string? user = null;
        bool userField = eventCode is >= 400 and <= 469 || eventCode is 121 or 122 or 306 or 411 or 412 or 413 or 462 or 466;
        if (number > 0)
        {
            if (userField) user = $"usuario {number}";
            else zone = number - zoneStart;
        }
        return new AlarmPanelEvent(now, kind, severity, code, description, area, zone, user, frame.Raw);
    }

    private static AlarmPanelEvent? FromSia(SiaDc09Frame frame, int zoneStart, DateTime now)
    {
        var m = SiaDcsPattern().Match(frame.Data);
        if (!m.Success) return null;
        int? area = m.Groups[1].Success && int.TryParse(m.Groups[1].Value, out int ri) && ri > 0 ? ri : null;
        string sia = m.Groups[3].Value;
        int number = m.Groups[4].Value.Length > 0 ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
        var (kind, severity, description, userField) = SiaCodes.TryGetValue(sia, out var entry)
            ? entry
            : (AlarmEventKind.Info, AlarmSeverity.Info, $"Evento SIA {sia}", false);
        int? zone = null;
        string? user = m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out int userNo) ? $"usuario {userNo}" : null;
        if (number > 0)
        {
            if (userField) user ??= $"usuario {number}";
            else zone = number - zoneStart;
        }
        return new AlarmPanelEvent(now, kind, severity, sia, description, area, zone, user, frame.Raw);
    }

    /// <summary>Códigos SIA (DC-03) más habituales: (naturaleza, severidad, texto, el número es de usuario).</summary>
    private static readonly Dictionary<string, (AlarmEventKind, AlarmSeverity, string, bool)> SiaCodes = new(StringComparer.Ordinal)
    {
        ["BA"] = (AlarmEventKind.Alarm, AlarmSeverity.Critical, "Intrusión (robo)", false),
        ["BR"] = (AlarmEventKind.Restore, AlarmSeverity.Info, "Intrusión restaurada", false),
        ["BB"] = (AlarmEventKind.Bypass, AlarmSeverity.Warning, "Zona anulada (bypass)", false),
        ["BU"] = (AlarmEventKind.Bypass, AlarmSeverity.Warning, "Zona restituida (fin de bypass)", false),
        ["BT"] = (AlarmEventKind.Trouble, AlarmSeverity.Warning, "Falla en zona de intrusión", false),
        ["BJ"] = (AlarmEventKind.Restore, AlarmSeverity.Info, "Falla de zona resuelta", false),
        ["BV"] = (AlarmEventKind.Alarm, AlarmSeverity.Critical, "Intrusión verificada", false),
        ["BZ"] = (AlarmEventKind.Trouble, AlarmSeverity.Warning, "Zona sin supervisión", false),
        ["FA"] = (AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de incendio", false),
        ["FR"] = (AlarmEventKind.Restore, AlarmSeverity.Info, "Incendio restaurado", false),
        ["FT"] = (AlarmEventKind.Trouble, AlarmSeverity.Warning, "Falla en zona de incendio", false),
        ["GA"] = (AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de gas", false),
        ["GR"] = (AlarmEventKind.Restore, AlarmSeverity.Info, "Gas restaurado", false),
        ["HA"] = (AlarmEventKind.Alarm, AlarmSeverity.Critical, "Coacción / asalto", true),
        ["HR"] = (AlarmEventKind.Restore, AlarmSeverity.Info, "Coacción restaurada", true),
        ["MA"] = (AlarmEventKind.Alarm, AlarmSeverity.Critical, "Emergencia médica", false),
        ["MR"] = (AlarmEventKind.Restore, AlarmSeverity.Info, "Emergencia médica restaurada", false),
        ["PA"] = (AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de pánico", false),
        ["PR"] = (AlarmEventKind.Restore, AlarmSeverity.Info, "Pánico restaurado", false),
        ["QA"] = (AlarmEventKind.Alarm, AlarmSeverity.Critical, "Emergencia", false),
        ["QR"] = (AlarmEventKind.Restore, AlarmSeverity.Info, "Emergencia restaurada", false),
        ["TA"] = (AlarmEventKind.Alarm, AlarmSeverity.Critical, "Tamper (sabotaje)", false),
        ["TR"] = (AlarmEventKind.Restore, AlarmSeverity.Info, "Tamper restaurado", false),
        ["UA"] = (AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de zona no tipificada", false),
        ["UR"] = (AlarmEventKind.Restore, AlarmSeverity.Info, "Zona no tipificada restaurada", false),
        ["WA"] = (AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de agua", false),
        ["WR"] = (AlarmEventKind.Restore, AlarmSeverity.Info, "Agua restaurada", false),
        ["OP"] = (AlarmEventKind.Disarm, AlarmSeverity.Info, "Área desarmada (apertura)", true),
        ["CL"] = (AlarmEventKind.Arm, AlarmSeverity.Info, "Área armada (cierre)", true),
        ["OA"] = (AlarmEventKind.Disarm, AlarmSeverity.Info, "Desarmado automático", true),
        ["CA"] = (AlarmEventKind.Arm, AlarmSeverity.Info, "Armado automático", true),
        ["OG"] = (AlarmEventKind.Disarm, AlarmSeverity.Info, "Área desarmada", true),
        ["CG"] = (AlarmEventKind.Arm, AlarmSeverity.Info, "Área armada", true),
        ["OR"] = (AlarmEventKind.Disarm, AlarmSeverity.Info, "Desarmado tras alarma", true),
        ["CQ"] = (AlarmEventKind.Arm, AlarmSeverity.Info, "Armado remoto", true),
        ["OQ"] = (AlarmEventKind.Disarm, AlarmSeverity.Info, "Desarmado remoto", true),
        ["CF"] = (AlarmEventKind.Arm, AlarmSeverity.Info, "Armado forzado", true),
        ["CE"] = (AlarmEventKind.Info, AlarmSeverity.Info, "Cierre extendido", true),
        ["CI"] = (AlarmEventKind.Trouble, AlarmSeverity.Warning, "Armado fallido", true),
        ["OC"] = (AlarmEventKind.Info, AlarmSeverity.Info, "Cancelación", true),
        ["AT"] = (AlarmEventKind.Trouble, AlarmSeverity.Warning, "Falla de corriente (AC)", false),
        ["AR"] = (AlarmEventKind.Restore, AlarmSeverity.Info, "Corriente (AC) restaurada", false),
        ["YT"] = (AlarmEventKind.Trouble, AlarmSeverity.Warning, "Batería baja del panel", false),
        ["YR"] = (AlarmEventKind.Restore, AlarmSeverity.Info, "Batería del panel restaurada", false),
        ["YM"] = (AlarmEventKind.Trouble, AlarmSeverity.Warning, "Batería del panel ausente", false),
        ["YP"] = (AlarmEventKind.Trouble, AlarmSeverity.Warning, "Falla de fuente de poder", false),
        ["YQ"] = (AlarmEventKind.Restore, AlarmSeverity.Info, "Fuente de poder restaurada", false),
        ["YC"] = (AlarmEventKind.Trouble, AlarmSeverity.Warning, "Falla de comunicación", false),
        ["YK"] = (AlarmEventKind.Restore, AlarmSeverity.Info, "Comunicación restaurada", false),
        ["YS"] = (AlarmEventKind.Trouble, AlarmSeverity.Warning, "Falla del comunicador", false),
        ["ET"] = (AlarmEventKind.Trouble, AlarmSeverity.Warning, "Falla en módulo de expansión", false),
        ["ER"] = (AlarmEventKind.Restore, AlarmSeverity.Info, "Módulo de expansión restaurado", false),
        ["EM"] = (AlarmEventKind.Trouble, AlarmSeverity.Warning, "Módulo de expansión ausente", false),
        ["EN"] = (AlarmEventKind.Restore, AlarmSeverity.Info, "Módulo de expansión recuperado", false),
        ["XT"] = (AlarmEventKind.Trouble, AlarmSeverity.Warning, "Batería baja del detector", false),
        ["XR"] = (AlarmEventKind.Restore, AlarmSeverity.Info, "Batería del detector restaurada", false),
        ["US"] = (AlarmEventKind.Trouble, AlarmSeverity.Warning, "Supervisión de zona perdida", false),
        ["UY"] = (AlarmEventKind.Restore, AlarmSeverity.Info, "Supervisión de zona restaurada", false),
        ["LB"] = (AlarmEventKind.System, AlarmSeverity.Info, "Inicio de programación local", true),
        ["LX"] = (AlarmEventKind.System, AlarmSeverity.Info, "Fin de programación local", true),
        ["RB"] = (AlarmEventKind.System, AlarmSeverity.Info, "Inicio de programación remota", true),
        ["RS"] = (AlarmEventKind.System, AlarmSeverity.Info, "Fin de programación remota", true),
        ["RP"] = (AlarmEventKind.System, AlarmSeverity.Info, "Prueba periódica", false),
        ["RX"] = (AlarmEventKind.System, AlarmSeverity.Info, "Prueba manual", true),
        ["RY"] = (AlarmEventKind.System, AlarmSeverity.Info, "Fin de prueba", true),
        ["RR"] = (AlarmEventKind.System, AlarmSeverity.Info, "Panel reiniciado", false),
        ["TS"] = (AlarmEventKind.System, AlarmSeverity.Info, "Inicio de modo prueba", true),
        ["TE"] = (AlarmEventKind.System, AlarmSeverity.Info, "Fin de modo prueba", true),
        ["JT"] = (AlarmEventKind.System, AlarmSeverity.Info, "Hora del panel cambiada", true),
        ["JL"] = (AlarmEventKind.Trouble, AlarmSeverity.Warning, "Registro del panel lleno", false),
    };
}
