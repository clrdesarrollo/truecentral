using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TrueCentralVms.Server.Services.Backup;

/// <summary>
/// Respaldo en un proceso aparte: el servidor se lanza a sí mismo con <c>--backup</c> y le pasa el
/// <see cref="BackupJob"/> por la entrada estándar. Así, nada de lo que le pase al respaldo (memoria, un cuelgue en la
/// carpeta de red, una excepción) alcanza al servidor que atiende a los guardias: en el peor caso el proceso se mata y
/// queda registrado como un respaldo fallido.
///
/// Protocolo por la salida estándar, una línea por mensaje: <c>progress {etapa} {bytes} {esperados}</c>,
/// <c>result {json}</c> al terminar bien y <c>error {mensaje}</c> si falla. El proceso va en el Job Object del servidor:
/// si el servidor muere, el respaldo muere con él (y el .part se limpia en la próxima retención).
/// </summary>
public static class BackupWorker
{
    public const string Argument = "--backup";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool IsRequested(string[] args) => args.Length > 0 && args[0] == Argument;

    // ------------------------------------------------------------------ lado del proceso aparte

    /// <summary>Punto de entrada de <c>--backup</c>: lee el trabajo, respalda y avisa el resultado. Devuelve el código de salida.</summary>
    public static async Task<int> RunChildAsync()
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        try
        {
            using var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            var job = JsonSerializer.Deserialize<BackupJob>(await stdin.ReadToEndAsync(), Json)
                ?? throw new InvalidDataException("No llegó el trabajo de respaldo.");
            var progress = new SyncProgress<BackupProgress>(p =>
                stdout.WriteLine(FormattableString.Invariant($"progress {p.Stage} {p.Bytes} {p.ExpectedBytes}")));
            var result = await BackupJobRunner.RunAsync(job, CancellationToken.None, progress);
            stdout.WriteLine("result " + JsonSerializer.Serialize(result, Json));
            return 0;
        }
        catch (Exception ex)
        {
            stdout.WriteLine("error " + ex.Message.ReplaceLineEndings(" "));
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    /// <summary>IProgress que informa en el mismo hilo (Progress&lt;T&gt; lo haría en el ThreadPool, desordenado).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    // ------------------------------------------------------------------ lado del servidor

    /// <summary>
    /// Lanza el proceso aparte, le pasa el trabajo y espera el resultado. Cancelar o pasar <paramref name="timeout"/>
    /// mata el proceso con sus hijos (pg_dump). Lanza si el respaldo falló.
    /// </summary>
    public static async Task<BackupJobResult> RunAsync(BackupJob job, TimeSpan timeout, CancellationToken ct,
        IProgress<BackupProgress>? progress, ILogger logger)
    {
        var psi = SelfStart();
        psi.ArgumentList.Add(Argument);
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        psi.StandardInputEncoding = new UTF8Encoding(false);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo lanzar el proceso de respaldo.");
        ChildProcessJob.Attach(process);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            var stderr = process.StandardError.ReadToEndAsync(limit.Token);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(job, Json).AsMemory(), limit.Token);
            process.StandardInput.Close();

            BackupJobResult? result = null;
            string? error = null;
            while (await process.StandardOutput.ReadLineAsync(limit.Token) is { } line)
            {
                if (line.StartsWith("progress ", StringComparison.Ordinal))
                {
                    string[] p = line.Split(' ');
                    if (p.Length == 4 && long.TryParse(p[2], CultureInfo.InvariantCulture, out long bytes)
                        && long.TryParse(p[3], CultureInfo.InvariantCulture, out long expected))
                        progress?.Report(new BackupProgress(p[1], bytes, expected));
                }
                else if (line.StartsWith("result ", StringComparison.Ordinal))
                    result = JsonSerializer.Deserialize<BackupJobResult>(line["result ".Length..], Json);
                else if (line.StartsWith("error ", StringComparison.Ordinal))
                    error = line["error ".Length..];
            }
            await process.WaitForExitAsync(limit.Token);
            string details = await stderr;
            if (result is not null && process.ExitCode == 0) return result;
            if (!string.IsNullOrWhiteSpace(details))
                logger.LogDebug("Proceso de respaldo (código {Code}): {Details}", process.ExitCode, details.Trim());
            throw new InvalidOperationException(error
                ?? $"El proceso de respaldo terminó sin resultado (código {process.ExitCode}). {LastLine(details)}".Trim());
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"El respaldo no terminó en {timeout.TotalMinutes:0} min y se detuvo.");
        }
        catch
        {
            Kill(process);
            throw;
        }
    }

    /// <summary>El mismo ejecutable del servidor (o dotnet + la dll cuando se corre así en desarrollo).</summary>
    private static ProcessStartInfo SelfStart()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("No se conoce la ruta del servidor.");
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = AppContext.BaseDirectory };
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(typeof(BackupWorker).Assembly.Location);
        return psi;
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* ya terminó */ }
    }

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
}
