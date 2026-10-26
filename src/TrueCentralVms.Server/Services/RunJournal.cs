using System.Diagnostics.Eventing.Reader;
using System.Text.Json;
using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services;

/// <summary>Quién o qué pidió detener el servidor (lo informa <see cref="Supervisor.ScmAwareServiceLifetime"/>).</summary>
public enum StopCause
{
    /// <summary>Nadie lo informó: consola (Ctrl+C) o el propio servidor.</summary>
    Unknown,
    /// <summary>Windows detuvo el servicio (services.msc, sc stop, Watchdog, reinicio desde el panel, instalador).</summary>
    ServiceStop,
    /// <summary>Windows avisó que se apaga o reinicia el equipo.</summary>
    SystemShutdown,
    /// <summary>El servidor se detuvo solo, sin pedido de Windows (falla de un servicio interno).</summary>
    SelfStop,
}

/// <summary>
/// Bitácora de vida del proceso, para saber al arrancar CÓMO terminó la ejecución anterior. Mientras corre, el servidor
/// mantiene <c>server-run.json</c> (en la carpeta de datos) con su arranque y un latido cada 30 s; al detenerse en orden
/// anota el motivo. Si al arrancar el archivo no tiene motivo, la ejecución anterior terminó de mala forma (corte de luz,
/// equipo colgado o reiniciado a la fuerza, proceso matado o caído): queda en la bitácora de auditoría
/// (<c>server-unexpected-stop</c>) y en el registro de eventos, con la última señal de vida y lo que anotó Windows en
/// ese lapso (eventos 41/6008 de apagado inesperado, 1074 de quién pidió reiniciar, 1001 de pantallazo azul).
/// </summary>
public sealed class RunJournal(EmbeddedPostgres pg, ILogger<RunJournal> logger) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
    private static readonly TimeSpan BeatEvery = TimeSpan.FromSeconds(30);

    private static volatile StopCause _cause = StopCause.Unknown;
    private readonly Lock _sync = new();
    private Record _current = new();
    private Timer? _beat;

    /// <summary>Motivo de la detención en curso (el primero que se informa gana).</summary>
    public static void ReportStop(StopCause cause)
    {
        if (_cause == StopCause.Unknown) _cause = cause;
    }

    private string FilePath => Path.Combine(pg.DataRoot, "server-run.json");

    private sealed record Record
    {
        public int Pid { get; init; }
        public string? Version { get; init; }
        public DateTime StartedUtc { get; init; }
        public DateTime LastBeatUtc { get; init; }
        /// <summary>Arranque de Windows (UTC, aproximado): distingue un reinicio del equipo de una caída del proceso.</summary>
        public DateTime BootUtc { get; init; }
        public DateTime? StoppedUtc { get; init; }
        public StopCause? Cause { get; init; }
    }

    /// <summary>Resultado de revisar la ejecución anterior.</summary>
    public sealed record PreviousRun(bool Unexpected, string Detail, object Data);

    /// <summary>
    /// Lee cómo terminó la ejecución anterior y empieza a anotar la actual. Devuelve null en la primera ejecución.
    /// </summary>
    public PreviousRun? Start(string version)
    {
        Record? previous = null;
        try
        {
            if (File.Exists(FilePath))
                previous = JsonSerializer.Deserialize<Record>(File.ReadAllText(FilePath), Json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo leer {File}: no se sabe cómo terminó la ejecución anterior.", FilePath);
        }

        var now = DateTime.UtcNow;
        _current = new Record
        {
            Pid = Environment.ProcessId,
            Version = version,
            StartedUtc = now,
            LastBeatUtc = now,
            BootUtc = now - TimeSpan.FromMilliseconds(Environment.TickCount64),
        };
        Write(_current);
        _beat = new Timer(_ => Beat(), null, BeatEvery, BeatEvery);
        return previous is null ? null : Describe(previous, _current.BootUtc);
    }

    private void Beat()
    {
        lock (_sync)
        {
            if (_current.StoppedUtc is not null) return;
            _current = _current with { LastBeatUtc = DateTime.UtcNow };
            Write(_current);
        }
    }

    /// <summary>Anota el apagado ordenado. Devuelve el motivo en palabras, para la bitácora.</summary>
    public string Stop()
    {
        _beat?.Dispose();
        // Como servicio, un pedido de Windows llega por OnStop/OnShutdown ANTES de que empiece el apagado; si no
        // llegó ninguno, se detuvo solo (el aviso de ScmAwareServiceLifetime corre después que este).
        var cause = _cause == StopCause.Unknown && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()
            ? StopCause.SelfStop : _cause;
        lock (_sync)
        {
            _current = _current with { LastBeatUtc = DateTime.UtcNow, StoppedUtc = DateTime.UtcNow, Cause = cause };
            Write(_current);
        }
        return CauseText(cause);
    }

    public void Dispose() => _beat?.Dispose();

    private static string CauseText(StopCause cause) => cause switch
    {
        StopCause.ServiceStop => "Windows detuvo el servicio (services.msc, sc stop, Watchdog, reinicio desde el panel o instalador)",
        StopCause.SystemShutdown => "se apagó o reinició el equipo",
        StopCause.SelfStop => "el propio servidor se detuvo por una falla (ver el registro de eventos); Windows lo vuelve a iniciar",
        _ => "detenido desde la consola",
    };

    private PreviousRun Describe(Record previous, DateTime bootUtc)
    {
        string when(DateTime utc) => utc.ToLocalTime().ToString("dd-MM-yyyy HH:mm:ss");
        if (previous.StoppedUtc is { } stopped)
        {
            string text = $"La ejecución anterior (v{previous.Version}, desde el {when(previous.StartedUtc)}) terminó en orden el {when(stopped)}: " +
                          CauseText(previous.Cause ?? StopCause.Unknown) + ".";
            if (previous.Cause == StopCause.SystemShutdown && WindowsEvents(previous.LastBeatUtc.AddMinutes(-15), stopped.AddMinutes(1), [1074])
                    is { Length: > 0 } who)
                text += " Windows: " + who;
            return new PreviousRun(previous.Cause == StopCause.SelfStop, text,
                new { previous.Version, previous.StartedUtc, stopped, cause = previous.Cause?.ToString() });
        }

        bool rebooted = (bootUtc - previous.BootUtc).Duration() > TimeSpan.FromMinutes(2);
        string how = rebooted
            ? "el EQUIPO se apagó o reinició sin un apagado ordenado del servidor (corte de luz, equipo colgado, reinicio forzado o pantallazo azul)"
            : "el PROCESO del servidor terminó de golpe con el equipo encendido (matado desde el Administrador de tareas o caído por una falla grave)";
        string detail = $"Apagado inesperado: la ejecución anterior (v{previous.Version}, PID {previous.Pid}, desde el {when(previous.StartedUtc)}) " +
                        $"no se detuvo en orden; última señal de vida el {when(previous.LastBeatUtc)}. Causa probable: {how}.";
        if (WindowsEvents(previous.LastBeatUtc.AddMinutes(-2), DateTime.UtcNow, [41, 6008, 1074, 1001]) is { Length: > 0 } windows)
            detail += " Windows: " + windows;
        return new PreviousRun(true, detail,
            new { previous.Version, previous.Pid, previous.StartedUtc, lastSignOfLifeUtc = previous.LastBeatUtc, machineRebooted = rebooted });
    }

    /// <summary>Eventos del registro Sistema de Windows en el lapso, resumidos (vacío si no hay o no se pueden leer).</summary>
    private string WindowsEvents(DateTime fromUtc, DateTime toUtc, int[] ids)
    {
        try
        {
            string idFilter = string.Join(" or ", ids.Select(i => $"EventID={i}"));
            string xpath = $"*[System[({idFilter}) and TimeCreated[@SystemTime>='{fromUtc:yyyy-MM-ddTHH:mm:ss.fffZ}' " +
                           $"and @SystemTime<='{toUtc:yyyy-MM-ddTHH:mm:ss.fffZ}']]]";
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath));
            var lines = new List<string>();
            for (EventRecord? e = reader.ReadEvent(); e is not null && lines.Count < 5; e = reader.ReadEvent())
                using (e)
                {
                    string text;
                    try { text = e.FormatDescription() ?? ""; } catch { text = ""; }
                    text = text.ReplaceLineEndings(" ").Trim();
                    if (text.Length > 300) text = text[..300] + "…";
                    lines.Add($"[{e.TimeCreated:dd-MM HH:mm:ss} id {e.Id}] {(text.Length > 0 ? text : e.ProviderName)}");
                }
            return string.Join(" ", lines);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No se pudo leer el registro Sistema de Windows.");
            return "";
        }
    }

    private void Write(Record record)
    {
        try
        {
            Directory.CreateDirectory(pg.DataRoot);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(record, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No se pudo escribir {File}.", FilePath);
        }
    }
}
