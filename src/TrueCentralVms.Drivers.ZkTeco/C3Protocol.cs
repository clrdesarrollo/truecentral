using System.Buffers.Binary;
using System.Text;

namespace TrueCentralVms.Drivers.ZkTeco;

/// <summary>
/// Protocolo de las controladoras ZKTeco (inBio y C3), el que usan ZKAccess y
/// el PullSDK (<c>plcommpro.dll</c>). Es otro protocolo que el de los
/// terminales autónomos (<see cref="ZkProtocol"/>), aunque también escuche en
/// el puerto TCP 4370: la controladora no contesta los mensajes de terminal ni
/// los terminales los de controladora.
///
/// Cada trama es
/// <c>AA [versión 01][comando][largo:2 LE][datos…][crc:2 LE] 55</c>, con
/// CRC-16/ARC (polinomio 0xA001 reflejado, valor inicial 0) sobre todo lo que
/// va entre el <c>AA</c> y el CRC. La controladora responde con el comando
/// <see cref="ReplyOk"/> o <see cref="ReplyError"/>.
///
/// Validado contra una inBio480 real (firmware «AC Ver 5.0.9 Sep 25 2013»).
/// </summary>
public static class C3Protocol
{
    public const int DefaultPort = 4370;
    /// <summary>Puerto UDP en que las controladoras escuchan la búsqueda.</summary>
    public const int DiscoveryPort = 65535;

    public const byte Start = 0xAA;
    public const byte End = 0x55;
    public const byte Version = 0x01;

    /// <summary>Conexión de los firmware que no manejan sesiones.</summary>
    public const byte CmdConnectSessionLess = 0x01;
    public const byte CmdDisconnect = 0x02;
    /// <summary>Fija parámetros (texto <c>nombre=valor,…</c>); la hora es <c>DateTime=&lt;<see cref="EncodeTime"/>&gt;</c>.</summary>
    public const byte CmdSetTime = 0x03;
    /// <summary>Lee parámetros: nombres separados por coma; responde <c>nombre=valor</c>.</summary>
    public const byte CmdGetParam = 0x04;
    /// <summary>Orden de control: 5 bytes (operación y tres parámetros, más uno reservado).</summary>
    public const byte CmdControl = 0x05;
    // Tablas de datos (usuarios, derechos, horarios, historial…). Capturados
    // del PullSDK oficial (plcommpro.dll de ZKAccess 3.5) contra una inBio480.
    /// <summary>Estructura de las tablas: texto <c>tabla=n,Campo=tipoN,…</c>, una tabla por línea.</summary>
    public const byte CmdTableConfig = 0x06;
    /// <summary>Escribe filas (alta o actualización por clave): [tabla][n][campos…] y por fila [tamaño][valor]….</summary>
    public const byte CmdSetData = 0x07;
    /// <summary>Lee filas: [tabla][n][campos…][opción: 1 = solo nuevas][n condiciones][campo][tamaño][valor]….</summary>
    public const byte CmdGetData = 0x08;
    /// <summary>Borra filas: [tabla][n condiciones][campo][tamaño][valor]….</summary>
    public const byte CmdDeleteData = 0x09;
    /// <summary>Cuenta filas: [tabla:2] → entero de 4 bytes.</summary>
    public const byte CmdCountData = 0x0A;
    /// <summary>
    /// Respuesta a una lectura que no cabe en una trama: anuncia el tamaño total
    /// y el de cada parte; el cliente pide las partes con <see cref="CmdReadChunk"/>
    /// y al final libera el búfer con <see cref="CmdFreeData"/>.
    /// </summary>
    public const byte ReplyLargeData = 0x0D;
    /// <summary>Pide una parte de la lectura grande: [desplazamiento:4]; responde [desplazamiento:4][datos].</summary>
    public const byte CmdReadChunk = 0x0E;
    public const byte CmdFreeData = 0x0F;
    /// <summary>Confirma una lectura "solo nuevas": [tabla]. Mueve el marcador de lo ya entregado.</summary>
    public const byte CmdConfirmNew = 0x19;
    /// <summary>Eventos en tiempo real, en registros binarios de <see cref="RtLogRecordSize"/> bytes.</summary>
    public const byte CmdRtLog = 0x0B;
    /// <summary>Búsqueda en la red; los datos son el texto <see cref="DiscoveryMessage"/>.</summary>
    public const byte CmdDiscover = 0x14;
    /// <summary>
    /// Cambio de red por broadcast al <see cref="DiscoveryPort"/>, sin sesión ni clave:
    /// texto <c>MAC=00:17:61:…,IPAddress=…,NetMask=…,GATEIPAddress=…</c>.
    /// </summary>
    public const byte CmdModifyIp = 0x15;
    /// <summary>Abre una sesión TCP; la respuesta trae el número de sesión.</summary>
    public const byte CmdConnect = 0x76;
    /// <summary>Eventos en tiempo real en texto (firmware que no tienen el binario).</summary>
    public const byte CmdRtLogText = 0x79;

    // Operaciones de CmdControl.
    /// <summary>Salida: [puerta o auxiliar, 1 = puerta / 2 = auxiliar, segundos (0 cierra, 255 = abierta)].</summary>
    public const byte ControlOutput = 1;
    public const byte ControlCancelAlarm = 2;
    public const byte ControlRestart = 3;
    /// <summary>Normal abierta del día: [puerta, 0 = deshabilitar / 1 = habilitar].</summary>
    public const byte ControlNormalOpen = 4;
    public const byte OutputDoor = 1;
    /// <summary>Duración de salida que la deja abierta hasta nueva orden.</summary>
    public const byte OutputHoldOpen = 255;

    /// <summary>Largo de un registro del evento en tiempo real.</summary>
    public const int RtLogRecordSize = 16;
    /// <summary>Tipo de evento que marca un registro de ESTADO de puertas y alarmas, no un evento.</summary>
    public const byte EventDoorAlarmStatus = 255;

    public const byte ReplyOk = 0xC8;
    public const byte ReplyError = 0xC9;

    public const string DiscoveryMessage = "CallSecurityDevice";

    /// <summary>Tramas mínimas: AA + versión + comando + largo(2) + crc(2) + 55.</summary>
    public const int Overhead = 8;

    /// <summary>Arma una trama con su CRC.</summary>
    public static byte[] BuildFrame(byte command, ReadOnlySpan<byte> data)
    {
        var frame = new byte[Overhead + data.Length];
        frame[0] = Start;
        frame[1] = Version;
        frame[2] = command;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3), (ushort)data.Length);
        data.CopyTo(frame.AsSpan(5));
        ushort crc = Crc16(frame.AsSpan(1, 4 + data.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(5 + data.Length), crc);
        frame[^1] = End;
        return frame;
    }

    /// <summary>Trama de búsqueda que se manda por broadcast al <see cref="DiscoveryPort"/>.</summary>
    public static byte[] BuildDiscovery() => BuildFrame(CmdDiscover, Encoding.ASCII.GetBytes(DiscoveryMessage));

    /// <summary>
    /// Lee una trama completa: comando y datos. Falso si no es una trama C3
    /// (marcas, largo o CRC no cuadran).
    /// </summary>
    public static bool TryReadFrame(ReadOnlySpan<byte> frame, out byte command, out ReadOnlySpan<byte> data)
    {
        command = 0;
        data = default;
        // El byte de versión no se exige: la inBio480 contesta 01 y el
        // terminal MA500 contesta 00 a la misma trama. El CRC lo cubre igual.
        if (frame.Length < Overhead || frame[0] != Start) return false;
        int length = BinaryPrimitives.ReadUInt16LittleEndian(frame[3..]);
        if (frame.Length < Overhead + length || frame[Overhead + length - 1] != End) return false;
        ushort crc = BinaryPrimitives.ReadUInt16LittleEndian(frame[(5 + length)..]);
        if (crc != Crc16(frame.Slice(1, 4 + length))) return false;
        command = frame[2];
        data = frame.Slice(5, length);
        return true;
    }

    /// <summary>
    /// Datos de la respuesta a la búsqueda: texto
    /// <c>MAC=…,IP=…,NetMask=…,GATEIPAddress=…,SN=…,Device=…,Ver=…</c>.
    /// </summary>
    public static Dictionary<string, string> ParseKeyValues(ReadOnlySpan<byte> data)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in Encoding.ASCII.GetString(data).TrimEnd('\0').Split(','))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0) values[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
        }
        return values;
    }

    /// <summary>
    /// Hora como la cuenta la controladora: segundos de un calendario de meses
    /// de 31 días desde el año 2000, en su hora LOCAL (no tiene zona horaria).
    /// </summary>
    public static uint EncodeTime(DateTime local) =>
        (uint)((((local.Year - 2000) * 12 * 31 + (local.Month - 1) * 31 + (local.Day - 1)) * 86400) +
               local.Hour * 3600 + local.Minute * 60 + local.Second);

    /// <summary>Lo inverso de <see cref="EncodeTime"/>; <see cref="DateTime.MinValue"/> si el valor no es una fecha.</summary>
    public static DateTime DecodeTime(uint value)
    {
        int second = (int)(value % 60), minute = (int)(value / 60 % 60), hour = (int)(value / 3600 % 24);
        int day = (int)(value / 86400 % 31) + 1, month = (int)(value / 2678400 % 12) + 1;
        int year = (int)(value / 32140800) + 2000;
        if (day > DateTime.DaysInMonth(year, month)) return DateTime.MinValue;
        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
    }

    /// <summary>
    /// Entero de un campo de tabla: little-endian con los bytes justos (el
    /// cero va sin bytes), como lo escribe el PullSDK.
    /// </summary>
    public static byte[] EncodeInt(long value)
    {
        if (value == 0) return [];
        var bytes = new List<byte>();
        ulong v = (ulong)value;
        while (v != 0) { bytes.Add((byte)v); v >>= 8; }
        return bytes.ToArray();
    }

    public static long DecodeInt(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        for (int i = bytes.Length - 1; i >= 0; i--) value = (value << 8) | bytes[i];
        return value;
    }

    /// <summary>
    /// Texto de los campos de tabla (nombres de usuario): GB18030, que es lo
    /// que escriben ZKAccess y el firmware (una Ñ viaja como 81 30 88 35).
    /// </summary>
    public static Encoding TextEncoding
    {
        get
        {
            if (_textEncoding is null)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _textEncoding = Encoding.GetEncoding(54936);
            }
            return _textEncoding;
        }
    }
    private static Encoding? _textEncoding;

    /// <summary>Texto para un campo de tabla, cortado a <paramref name="maxBytes"/> sin partir un carácter.</summary>
    public static byte[] EncodeText(string text, int maxBytes)
    {
        var bytes = TextEncoding.GetBytes(text);
        if (bytes.Length <= maxBytes) return bytes;
        string cut = text;
        while (cut.Length > 0 && TextEncoding.GetByteCount(cut) > maxBytes) cut = cut[..^1];
        return TextEncoding.GetBytes(cut);
    }

    /// <summary>Fecha de vigencia de un usuario: entero AAAAMMDD (0 = sin límite).</summary>
    public static long EncodeDate(DateTime date) => date.Year * 10000L + date.Month * 100 + date.Day;

    /// <summary>Tramo de un horario: inicio y fin en HHMM, el inicio en los 16 bits altos.</summary>
    public static long EncodeSegment(int startMinutes, int endMinutes)
    {
        int start = startMinutes / 60 * 100 + startMinutes % 60;
        int end = Math.Min(endMinutes, 24 * 60 - 1);
        end = end / 60 * 100 + end % 60;
        return ((long)start << 16) | (uint)end;
    }

    /// <summary>CRC-16/ARC: polinomio 0xA001 reflejado, valor inicial 0.</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
        }
        return (ushort)crc;
    }
}
