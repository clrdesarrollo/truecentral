using System.Diagnostics;
using System.IO.Ports;

namespace TrueCentralVms.Drivers.Patrol;

public sealed class PatrolReaderException(string message) : Exception(message);

/// <summary>Resultado de leer la memoria del bastón.</summary>
/// <param name="Blocks">Bloques crudos 0x93, en el orden en que llegaron (lo que se sube al servidor).</param>
/// <param name="DeclaredBlocks">Bloques que anunció el bastón en la cabecera.</param>
public sealed record PatrolMemoryDump(IReadOnlyList<byte[]> Blocks, int DeclaredBlocks)
{
    public IEnumerable<PatrolTagRead> Reads => Blocks.SelectMany(PatrolBlockDecoder.Decode);
}

/// <summary>
/// Bastón de rondas JWM (Captor 4) por el puerto serie virtual del CP210x
/// (VID 10C4, PID EA60), 57600 8N1. Protocolo sacado de WmWebUsb.dll y
/// verificado con un bastón real (ver docs/rondas/PROTOCOLO.md). La clase NO es
/// segura entre hilos: una instancia por puerto, atendida desde un solo hilo.
/// </summary>
public sealed class JwmCaptorReader : IDisposable
{
    public const string DriverKey = "jwm-captor4";
    public const int BaudRate = 57600;
    public const int UsbVendorId = 0x10C4;
    public const int UsbProductId = 0xEA60;

    /// <summary>
    /// Bytes 0xFE para despertarlo. Medido: 4096 no alcanza, 6144 sí; la DLL usa
    /// 20480 (3,6 s). 8192 (1,4 s) deja margen.
    /// </summary>
    public const int WakeBytes = 8192;

    /// <summary>Despierto, el bastón se duerme entre 5 y 15 s sin tráfico.</summary>
    public static readonly TimeSpan KeepAwakeInterval = TimeSpan.FromSeconds(3);

    private const byte CmdSetTermNo = 0x01;
    private const byte CmdGetTermNo = 0x02;
    private const byte CmdGetRecords = 0x03;
    private const byte CmdEraseRecords = 0x04;
    private const byte CmdSetDateTime = 0x05;
    private const byte CmdIdentify = 0x0A;
    private const byte CmdAck = 0x00;
    private const byte CmdNak = 0xFF;

    private readonly SerialPort _port;

    public string PortName => _port.PortName;

    private JwmCaptorReader(SerialPort port) => _port = port;

    /// <summary>Abre el puerto sin hablarle al bastón (puede no estar conectado).</summary>
    public static JwmCaptorReader Open(string portName)
    {
        var port = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 50,
            WriteTimeout = 5000,
            ReadBufferSize = 4096,
            WriteBufferSize = WakeBytes + 1024,
        };
        port.Open();
        return new JwmCaptorReader(port);
    }

    /// <summary>
    /// ¿Hay un bastón respondiendo? Primero pregunta sin despertar (si ya está
    /// despierto contesta en ~60 ms) y, si no, lo despierta y vuelve a preguntar.
    /// </summary>
    public bool Probe()
    {
        if (Ping()) return true;
        WakeUp();
        return Ping();
    }

    /// <summary>Identificación sin despertar: mantiene despierto a un bastón que ya lo está.</summary>
    public bool Ping() => TryTransact(PatrolFrame.Build(CmdIdentify), CmdIdentify, 400) is not null;

    /// <summary>Número de captivador (Ronda lo muestra con 6 dígitos).</summary>
    public uint GetSerial()
    {
        var p = PatrolFrame.Payload(Transact(PatrolFrame.Build(CmdGetTermNo), CmdGetTermNo, 1000, 3));
        if (p.Length < 4) throw new PatrolReaderException("El bastón respondió un número de captivador incompleto.");
        return (uint)(p[0] << 24 | p[1] << 16 | p[2] << 8 | p[3]);
    }

    public void SetSerial(uint serial)
    {
        byte[] data = [(byte)(serial >> 24), (byte)(serial >> 16), (byte)(serial >> 8), (byte)serial];
        Transact(PatrolFrame.Build(CmdSetTermNo, data), CmdSetTermNo, 1000, 3);
    }

    /// <summary>Pone el reloj del bastón (hora local, BCD: ss mm hh DD 00 MM YY).</summary>
    public void SetClock(DateTime local)
    {
        PrepareCommand();
        byte[] data =
        [
            PatrolBlockDecoder.ToBcd(local.Second), PatrolBlockDecoder.ToBcd(local.Minute), PatrolBlockDecoder.ToBcd(local.Hour),
            PatrolBlockDecoder.ToBcd(local.Day), 0, PatrolBlockDecoder.ToBcd(local.Month), PatrolBlockDecoder.ToBcd(local.Year % 100),
        ];
        Transact(PatrolFrame.Build(CmdSetDateTime, data), CmdSetDateTime, 1000, 3);
    }

    /// <summary>
    /// Lee toda la memoria (no borra): 0x03 anuncia los bloques y se pide cada
    /// uno con ACK (0x00), repitiendo con NAK (0xFF). Al final, ACK de cierre.
    /// </summary>
    public PatrolMemoryDump ReadMemory(IProgress<(int Block, int Blocks)>? progress = null, CancellationToken ct = default)
    {
        PrepareCommand();
        var head = Transact(PatrolFrame.Build(CmdGetRecords), CmdGetRecords, 6000, 3);
        var blocks = new List<byte[]>();
        if ((head[2] | head[3] << 8) == 9) return new PatrolMemoryDump(blocks, 0); // misma condición que WmWebUsb: vacío

        var p = PatrolFrame.Payload(head);
        int declared = p.Length >= 2 ? p[0] | p[1] << 8 : 0;
        var ack = PatrolFrame.Build(CmdAck);
        var nak = PatrolFrame.Build(CmdNak);
        try
        {
            for (int i = 0; i < declared; i++)
            {
                ct.ThrowIfCancellationRequested();
                byte[]? block = null;
                var request = ack;
                for (int attempt = 0; attempt < 3 && block is null; attempt++)
                {
                    Write(request);
                    var b = ReadFrame(2000);
                    if (b is not null && PatrolFrame.IsValid(b) && b[4] == PatrolBlockDecoder.BlockResponse) block = b;
                    request = nak;
                }
                if (block is null) throw new PatrolReaderException($"No se pudo leer el bloque {i + 1} de {declared}.");
                blocks.Add(block);
                progress?.Report((i + 1, declared));
            }
        }
        finally
        {
            Write(ack);
        }
        return new PatrolMemoryDump(blocks, declared);
    }

    /// <summary>Vacía la memoria. La DLL espera 4 s antes y hasta 40 s la confirmación.</summary>
    public void EraseMemory()
    {
        PrepareCommand();
        Thread.Sleep(4000);
        Transact(PatrolFrame.Build(CmdEraseRecords), CmdEraseRecords, 40000, 1);
    }

    /// <summary>
    /// "SetModel(2, 1)" de WmWebUsb (comando 0x39 01). Sin él el bastón no
    /// contesta la lectura de registros; Ronda lo manda antes de leer, borrar y
    /// poner la hora.
    /// </summary>
    private void PrepareCommand() => Transact(PatrolFrame.Build(0x39, [1]), 0x39, 1000, 3);

    private void WakeUp()
    {
        var fe = new byte[WakeBytes];
        Array.Fill(fe, (byte)0xFE);
        _port.Write(fe, 0, fe.Length);
        _port.BaseStream.Flush();
        _port.DiscardInBuffer();
    }

    private byte[] Transact(byte[] request, byte cmd, int timeoutMs, int tries)
    {
        for (int i = 0; i < tries; i++)
            if (TryTransact(request, cmd, timeoutMs) is { } r) return r;
        throw new PatrolReaderException($"El bastón no respondió al comando 0x{cmd:X2} en {_port.PortName}.");
    }

    private byte[]? TryTransact(byte[] request, byte cmd, int timeoutMs)
    {
        _port.DiscardInBuffer();
        Write(request);
        var r = ReadFrame(timeoutMs);
        return r is not null && PatrolFrame.IsValid(r) && r[4] == (byte)(cmd | 0x80) ? r : null;
    }

    private void Write(byte[] data) => _port.Write(data, 0, data.Length);

    private byte[]? ReadFrame(int timeoutMs)
    {
        var buf = new List<byte>(80);
        var tmp = new byte[256];
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            int n;
            try { n = _port.Read(tmp, 0, tmp.Length); }
            catch (TimeoutException) { continue; }
            buf.AddRange(tmp.AsSpan(0, n));
            while (buf.Count > 0 && buf[0] != PatrolFrame.Header) buf.RemoveAt(0);
            var declared = PatrolFrame.DeclaredLength(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buf));
            if (declared <= 0 || buf.Count < declared) continue;
            // Termina en el primer CR LF desde el largo declarado (0x39 trae un byte de más).
            for (int end = declared; end <= Math.Min(buf.Count, declared + 4); end++)
                if (buf[end - 2] == 0x0D && buf[end - 1] == 0x0A) return buf.GetRange(0, end).ToArray();
            if (buf.Count > declared + 4) return buf.GetRange(0, declared).ToArray();
        }
        return null;
    }

    /// <summary>
    /// El chip USB está en el bastón: al sacarlo, Windows elimina el puerto y
    /// cerrarlo lanza UnauthorizedAccessException/IOException. No hay nada que
    /// cerrar ya; se ignora.
    /// </summary>
    public void Dispose()
    {
        try { _port.Dispose(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }
}
