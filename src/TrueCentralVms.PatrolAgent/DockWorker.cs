using System.IO.Ports;
using Microsoft.Win32;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Drivers.Patrol;

namespace TrueCentralVms.PatrolAgent;

/// <summary>Puertos COM del puente CP210x que usa el bastón (VID 10C4, PID EA60).</summary>
public static class Cp210xPorts
{
    public static IReadOnlyList<string> Find(IReadOnlyList<string>? forced)
    {
        var present = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (forced is { Count: > 0 }) return forced.Where(present.Contains).ToList();
        var result = new List<string>();
        if (!OperatingSystem.IsWindows()) return result;
        string key = $@"SYSTEM\CurrentControlSet\Enum\USB\VID_{JwmCaptorReader.UsbVendorId:X4}&PID_{JwmCaptorReader.UsbProductId:X4}";
        using var usb = Registry.LocalMachine.OpenSubKey(key);
        if (usb is null) return result;
        foreach (var instance in usb.GetSubKeyNames())
        {
            using var parameters = usb.OpenSubKey($@"{instance}\Device Parameters");
            if (parameters?.GetValue("PortName") is string port && present.Contains(port) && !result.Contains(port))
                result.Add(port);
        }
        return result;
    }
}

/// <summary>Orden pendiente del panel para un bastón.</summary>
public sealed record PendingCommand(long Id, string Kind);

/// <summary>
/// Atiende UN puerto: detecta si el bastón está puesto (despertar + identificar
/// cada ~5 s), lo mantiene despierto mientras está (identificar cada 3 s, que
/// además detecta el retiro) y descarga al ponerlo o cuando el panel lo pide.
/// Corre en un hilo propio: el puerto serie es bloqueante.
/// </summary>
public sealed class DockWorker(string port, PatrolAgentService service, ILogger logger)
{
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;

    public string Port { get; } = port;
    public long? Serial { get; private set; }
    public bool Docked { get; private set; }
    public string? Error { get; private set; }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = $"base {Port}" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(10));
    }

    private void Run()
    {
        var ct = _stop.Token;
        JwmCaptorReader? reader = null;
        while (!ct.IsCancellationRequested)
        {
            bool opening = reader is null;
            try
            {
                reader ??= JwmCaptorReader.Open(Port);
                opening = false;
                if (!Docked)
                {
                    if (reader.Probe())
                    {
                        Serial = (long)reader.GetSerial();
                        Docked = true;
                        Error = null;
                        logger.LogInformation("Bastón {Serial:D6} puesto en {Port}.", Serial, Port);
                        service.Kick();
                        Progress("detected");
                        Download(reader, null, ct);
                    }
                    else Sleep(TimeSpan.FromSeconds(4), ct);
                    continue;
                }

                Sleep(JwmCaptorReader.KeepAwakeInterval, ct);
                if (Serial is { } serial && service.TakeCommand(serial) is { } command)
                {
                    Download(reader, command, ct);
                    continue;
                }
                if (!reader.Ping() && !reader.Ping())
                {
                    logger.LogInformation("Bastón {Serial:D6} retirado de {Port}.", Serial, Port);
                    Docked = false;
                    service.Kick();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                bool wasDocked = Docked;
                if (Docked) { Docked = false; service.Kick(); }
                if (_downloading) Progress("removed", message: "Se desconectó el bastón antes de terminar. Vuelva a ponerlo en la base: no se perdió nada.");
                _downloading = false;
                reader?.Dispose();
                reader = null;
                if (opening && ex is UnauthorizedAccessException)
                {
                    // Al abrir: otro programa tiene el puerto (la app Ronda antigua, por ejemplo).
                    Error = $"El puerto {Port} está ocupado por otro programa (¿la app Ronda antigua abierta?).";
                    service.ReportError(Serial, Error);
                }
                else if (!wasDocked && Serial is null)
                {
                    // El puerto recién apareció y el bastón aún no contesta: se reintenta en silencio.
                    logger.LogDebug("Puerto {Port} todavía no listo ({Message}).", Port, ex.Message);
                }
                else if (wasDocked || ex is UnauthorizedAccessException or IOException)
                {
                    // El chip USB va en el bastón: sacarlo (aun a mitad de una descarga) hace
                    // desaparecer el puerto. No es un error: lo descargado y no confirmado
                    // sigue en la cola y nada se vació sin confirmación.
                    logger.LogInformation("Bastón {Serial} retirado de {Port} ({Message}).", Serial?.ToString("D6"), Port, ex.Message);
                }
                else
                {
                    Error = ex.Message;
                    service.ReportError(Serial, Error);
                }
                Sleep(TimeSpan.FromSeconds(3), ct);
            }
        }
        reader?.Dispose();
    }

    /// <summary>
    /// Poner en hora → leer → guardar en la cola local → subir → (si el servidor
    /// lo permite) vaciar y comprobar que quedó vacío. Nunca se vacía sin el
    /// visto bueno del servidor, salvo memoria casi llena con el servidor caído
    /// y la descarga ya guardada en la cola (ver PatrolAgentService.SpoolEraseThreshold).
    /// </summary>
    private void Download(JwmCaptorReader reader, PendingCommand? command, CancellationToken ct)
    {
        long serial = Serial!.Value;
        _downloading = true;
        DateTime? clockSet = null;
        if (service.ServerNow() is { } now)
        {
            Progress("clock");
            reader.SetClock(now);
            clockSet = now;
        }
        var started = DateTime.UtcNow;
        Progress("reading");
        var dump = reader.ReadMemory(new InlineProgress(p => Progress("reading", p.Block, p.Blocks)), ct);
        var upload = new PatrolAgentUpload(serial, dump.Blocks.Select(Convert.ToHexString).ToList(), dump.DeclaredBlocks, clockSet, command?.Id);
        logger.LogInformation("Bastón {Serial:D6}: {Blocks} bloques leídos en {Ms} ms.", serial, dump.Blocks.Count,
            (int)(DateTime.UtcNow - started).TotalMilliseconds);

        Progress("uploading", dump.Blocks.Count, dump.DeclaredBlocks);
        var outcome = service.SubmitAsync(upload, ct).GetAwaiter().GetResult();
        bool wantsErase = command?.Kind != "read" && dump.Blocks.Count > 0;
        bool erase = wantsErase && (outcome.Result?.EraseAllowed == true
            || (outcome.Result is null && outcome.Queued && command is null && dump.DeclaredBlocks >= service.SpoolEraseThreshold));
        string? eraseError = null;
        bool erased = false;
        if (erase)
        {
            try
            {
                Progress("erasing");
                reader.EraseMemory();
                erased = reader.ReadMemory(ct: ct).DeclaredBlocks == 0;
                if (!erased) eraseError = "El bastón no quedó vacío después de borrarlo.";
            }
            catch (PatrolReaderException ex) { eraseError = ex.Message; }
            logger.LogInformation("Bastón {Serial:D6}: {Result}.", serial, erased ? "vaciado" : $"no se pudo vaciar ({eraseError})");
        }
        service.AfterDownload(serial, outcome, erased, erase ? eraseError : null, command, ct);
        _downloading = false;
        if (outcome.Result is { } result)
            Progress(eraseError is null ? "done" : "error", message: result.Message +
                (erased ? " Bastón vaciado." : eraseError is not null ? $" No se pudo vaciar: {eraseError}" : ""));
        else if (outcome.Queued)
            Progress("queued", message: "Sin conexión con el servidor: las lecturas quedaron guardadas en este PC y se enviarán solas." +
                (erased ? " Bastón vaciado." : " El bastón no se vació."));
        else Progress("error", message: outcome.Error);
    }

    private bool _downloading;

    private void Progress(string stage, int block = 0, int blocks = 0, string? message = null) =>
        service.Publish(new PatrolDockProgress(Port, Serial, stage, block, blocks, message, DateTime.Now));

    /// <summary>IProgress que corre en el mismo hilo (Progress&lt;T&gt; lo mandaría al pool).</summary>
    private sealed class InlineProgress(Action<(int Block, int Blocks)> report) : IProgress<(int Block, int Blocks)>
    {
        public void Report((int Block, int Blocks) value) => report(value);
    }

    private static void Sleep(TimeSpan delay, CancellationToken ct) => ct.WaitHandle.WaitOne(delay);
}
