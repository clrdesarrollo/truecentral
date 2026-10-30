using System.Text;

namespace TrueCentralVms.Server.Services.Supervisor;

/// <summary>
/// Etapa en que va el arranque del servidor, para quien espera afuera (el
/// instalador): mientras inicia la base, respalda y migra, el servidor todavía
/// no atiende /api/health, así que avisa por un archivo junto a sus datos
/// (<c>startup-status.ini</c>, en %ProgramData%\CLRTrueCentralVMS instalado).
///
/// Formato INI en UTF-16 con BOM: el instalador (Inno Setup) lo lee con
/// GetIniString, que entiende ese formato con tildes. Claves de [status]:
/// <c>phase</c> (database, certificate, backup, migrate, services, ready,
/// error), <c>title</c> y <c>detail</c> (texto para la persona),
/// <c>percent</c> (0..100, o -1 si no se puede medir), <c>step</c>/<c>steps</c>
/// (etapa n de m), <c>started</c> y <c>updated</c> (UTC, ISO).
/// </summary>
public sealed class StartupStatus(string directory, ILogger logger)
{
    private const int Steps = 5;
    private readonly string _path = Path.Combine(directory, "startup-status.ini");
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly Lock _sync = new();
    private string _phase = "";
    private long _lastWriteTicks;
    private bool _failedOnce;

    /// <summary>Informa una etapa. Las actualizaciones de la misma etapa se escriben a lo más dos veces por segundo.</summary>
    public void Report(string phase, int step, string title, string? detail = null, int percent = -1)
    {
        lock (_sync)
        {
            long now = Environment.TickCount64;
            string key = phase + "|" + title;
            if (key == _phase && now - _lastWriteTicks < 500) return;
            _phase = key;
            _lastWriteTicks = now;
            Write(phase, step, title, detail, percent);
        }
    }

    /// <summary>Avance del respaldo previo a la actualización (MB escritos contra los del respaldo anterior).</summary>
    public IProgress<Backup.BackupProgress> BackupProgress() => new Callback<Backup.BackupProgress>(p =>
    {
        string written = Mb(p.Bytes);
        switch (p.Stage)
        {
            case "dump" when p.ExpectedBytes > 0:
                int percent = (int)Math.Min(99, p.Bytes * 100 / p.ExpectedBytes);
                Report("backup", 2, "Respaldando la base de datos antes de actualizar",
                    $"{written} de unos {Mb(p.ExpectedBytes)} (según el respaldo anterior)", percent);
                break;
            case "dump":
                Report("backup", 2, "Respaldando la base de datos antes de actualizar", $"{written} escritos");
                break;
            case "files":
                Report("backup", 2, "Respaldando la base de datos antes de actualizar",
                    "Agregando la configuración, las licencias y los archivos de las automatizaciones");
                break;
            case "verify":
                Report("backup", 2, "Verificando el respaldo",
                    $"Se relee completo ({written} de {Mb(p.ExpectedBytes)}) para asegurar que se pueda restaurar", Percent(p));
                break;
            case "secondary":
                Report("backup", 2, "Copiando el respaldo al segundo destino", $"{written} de {Mb(p.ExpectedBytes)}", Percent(p));
                break;
        }
    });

    private static int Percent(Backup.BackupProgress p) =>
        p.ExpectedBytes > 0 ? (int)Math.Min(100, p.Bytes * 100 / p.ExpectedBytes) : -1;

    /// <summary>Arranque terminado: el servidor ya atiende.</summary>
    public void Ready() => Report("ready", Steps, "El servidor está listo", null, 100);

    /// <summary>El arranque falló: lo que se le muestra a quien instala.</summary>
    public void Failed(Exception ex) => Report("error", 0, "El servidor no pudo iniciar", ex.Message);

    private void Write(string phase, int step, string title, string? detail, int percent)
    {
        var text = new StringBuilder()
            .AppendLine("[status]")
            .AppendLine($"phase={phase}")
            .AppendLine($"step={step}")
            .AppendLine($"steps={Steps}")
            .AppendLine($"title={OneLine(title)}")
            .AppendLine($"detail={OneLine(detail)}")
            .AppendLine($"percent={percent}")
            .AppendLine($"started={_started:O}")
            .AppendLine($"updated={DateTime.UtcNow:O}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, text.ToString(), new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Es solo un aviso para el instalador: nunca frena el arranque.
            if (!_failedOnce) logger.LogDebug(ex, "No se pudo escribir el estado del arranque en {Path}.", _path);
            _failedOnce = true;
        }
    }

    private static string OneLine(string? text) => (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string Mb(long bytes) => bytes < 10L << 20
        ? $"{bytes / 1024d / 1024d:N1} MB"
        : $"{bytes / 1024d / 1024d:N0} MB";

    /// <summary>IProgress que informa en el mismo hilo (Progress&lt;T&gt; lo encola y podría llegar desordenado).</summary>
    private sealed class Callback<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
