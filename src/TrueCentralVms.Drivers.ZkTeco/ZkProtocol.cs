using System.Buffers.Binary;

namespace TrueCentralVms.Drivers.ZkTeco;

/// <summary>
/// Protocolo propio de ZKTeco (el del "Standalone SDK", puerto 4370). Es
/// binario y pequeño: cada mensaje es
/// <c>[comando:2][checksum:2][sesión:2][respuesta:2][datos…]</c> en
/// little-endian, y sobre TCP va envuelto en una cabecera de 8 bytes
/// (<c>50 50 82 7d</c> + largo). Por UDP viaja el mensaje pelado.
///
/// El equipo no tiene usuario ni contraseña: la credencial es la <b>clave de
/// comunicación</b> (Comm Key, 0 de fábrica), que se responde cifrada con
/// <see cref="MakeCommKey"/> cuando el equipo contesta
/// <see cref="CmdAckUnauth"/> al conectar.
///
/// Las particularidades raras de aquí (el checksum que se envuelve en 65535 en
/// vez de 65536, el orden de los XOR de la clave) son del protocolo, no
/// errores: así lo implementan el SDK del fabricante y las bibliotecas libres
/// que lo replican.
/// </summary>
public static class ZkProtocol
{
    public const int DefaultPort = 4370;

    // Comandos usados por el administrador de dispositivos.
    public const ushort CmdConnect = 1000;
    public const ushort CmdExit = 1001;
    public const ushort CmdAuth = 1102;
    public const ushort CmdGetVersion = 1100;
    /// <summary>Lee un parámetro por nombre ("~DeviceName", "~SerialNumber"…).</summary>
    public const ushort CmdOptionsRead = 11;
    /// <summary>Cupos y ocupación del equipo (usuarios, huellas, registros).</summary>
    public const ushort CmdGetFreeSizes = 50;

    // Comandos de operación.
    /// <summary>Abre la cerradura; los datos son la duración del pulso en décimas de segundo.</summary>
    public const ushort CmdUnlock = 31;
    /// <summary>Pide el historial de marcas/pasadas (llega como bloque largo).</summary>
    public const ushort CmdAttLogRead = 13;

    // Respuestas.
    public const ushort CmdAckOk = 2000;
    public const ushort CmdAckError = 2001;
    public const ushort CmdAckData = 2002;
    public const ushort CmdAckUnauth = 2005;

    // Transferencia de bloques largos.
    /// <summary>"Vienen N bytes": los datos llegan después en mensajes <see cref="CmdData"/>.</summary>
    public const ushort CmdPrepareData = 1500;
    /// <summary>Un pedazo de los datos anunciados.</summary>
    public const ushort CmdData = 1501;

    /// <summary>
    /// Las marcas de tiempo del equipo son un solo entero de 32 bits que
    /// cuenta desde el año 2000 tratando todos los meses como de 31 días. Se
    /// deshace en el mismo orden en que se armó.
    /// </summary>
    public static DateTime DecodeTime(uint value)
    {
        int second = (int)(value % 60); value /= 60;
        int minute = (int)(value % 60); value /= 60;
        int hour = (int)(value % 24); value /= 24;
        int day = (int)(value % 31) + 1; value /= 31;
        int month = (int)(value % 12) + 1; value /= 12;
        int year = (int)value + 2000;
        // Un equipo con la hora sin configurar manda cualquier cosa: mejor
        // devolver algo válido que reventar el sondeo del historial.
        if (year is < 2000 or > 2099 || day > DateTime.DaysInMonth(year, month)) return DateTime.MinValue;
        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
    }

    /// <summary>Cabecera de los mensajes sobre TCP (por UDP no se usa).</summary>
    public static ReadOnlySpan<byte> TcpPrefix => [0x50, 0x50, 0x82, 0x7d];

    public const int HeaderSize = 8;

    /// <summary>Arma un mensaje con su checksum ya calculado.</summary>
    public static byte[] BuildPacket(ushort command, ushort sessionId, ushort replyId, ReadOnlySpan<byte> data)
    {
        var packet = new byte[HeaderSize + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0), command);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), 0);   // checksum: se calcula sobre el resto
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), sessionId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), replyId);
        data.CopyTo(packet.AsSpan(HeaderSize));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), Checksum(packet));
        return packet;
    }

    /// <summary>Envuelve un mensaje para mandarlo por TCP.</summary>
    public static byte[] WrapTcp(ReadOnlySpan<byte> packet)
    {
        var framed = new byte[HeaderSize + packet.Length];
        TcpPrefix.CopyTo(framed);
        BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(4), packet.Length);
        packet.CopyTo(framed.AsSpan(HeaderSize));
        return framed;
    }

    /// <summary>
    /// Checksum del protocolo: suma de palabras de 16 bits y complemento,
    /// envolviendo en 65535 (no en 65536, como sería lo habitual).
    /// </summary>
    public static ushort Checksum(ReadOnlySpan<byte> packet)
    {
        int checksum = 0;
        int i = 0;
        for (; i + 1 < packet.Length; i += 2)
        {
            checksum += packet[i] | (packet[i + 1] << 8);
            if (checksum > ushort.MaxValue) checksum -= ushort.MaxValue;
        }
        if (i < packet.Length) checksum += packet[i];
        while (checksum > ushort.MaxValue) checksum -= ushort.MaxValue;
        checksum = ~checksum;
        while (checksum < 0) checksum += ushort.MaxValue;
        return (ushort)checksum;
    }

    /// <summary>Comando, sesión y respuesta de un mensaje recibido.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> packet, out ushort command, out ushort sessionId, out ushort replyId)
    {
        command = sessionId = replyId = 0;
        if (packet.Length < HeaderSize) return false;
        command = BinaryPrimitives.ReadUInt16LittleEndian(packet);
        sessionId = BinaryPrimitives.ReadUInt16LittleEndian(packet[4..]);
        replyId = BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]);
        return true;
    }

    /// <summary>
    /// Clave de comunicación cifrada con la sesión, tal como la espera
    /// <see cref="CmdAuth"/>: los 32 bits de la clave se rotan bit a bit, se
    /// les suma el número de sesión y el resultado se mezcla con "ZKSO" y con
    /// un contador fijo.
    /// </summary>
    public static byte[] MakeCommKey(int key, ushort sessionId, byte ticks = 50)
    {
        uint rotated = 0;
        for (int i = 0; i < 32; i++)
            rotated = (key & (1 << i)) != 0 ? (rotated << 1) | 1 : rotated << 1;
        rotated += sessionId;

        var bytes = BitConverter.GetBytes(rotated);
        byte[] mixed =
        [
            (byte)(bytes[0] ^ 'Z'),
            (byte)(bytes[1] ^ 'K'),
            (byte)(bytes[2] ^ 'S'),
            (byte)(bytes[3] ^ 'O'),
        ];
        // Las dos mitades de 16 bits se intercambian antes del último XOR.
        ushort low = (ushort)(mixed[0] | (mixed[1] << 8));
        ushort high = (ushort)(mixed[2] | (mixed[3] << 8));
        return
        [
            (byte)((high & 0xFF) ^ ticks),
            (byte)((high >> 8) ^ ticks),
            (byte)(low & 0xFF),
            (byte)((low >> 8) ^ ticks),
        ];
    }
}
