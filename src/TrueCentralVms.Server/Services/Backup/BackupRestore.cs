using System.Diagnostics;
using System.IO.Compression;
using System.ServiceProcess;
using System.Text.Json;
using Npgsql;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Services.Supervisor;

namespace TrueCentralVms.Server.Services.Backup;

/// <summary>
/// Restauración por consola, con el servicio DETENIDO (nunca desde la web: un botón así sería un arma):
///
///   TrueCentralVms.Server.exe --restore "D:\respaldos\tcvms-20261020-033000-diario.tcbak"
///   (opcional: --password-file archivo.txt  y  --yes para no pedir confirmación)
///
/// Descifra y verifica el respaldo completo (etiquetas y manifiesto) ANTES de tocar nada; restaura la base en una base
/// aparte y solo si salió bien la intercambia con la actual, que queda guardada como truecentral_vms_antes_{fecha}
/// (se puede volver a ella). La llave de datos y las carpetas reemplazadas quedan igual, con el sufijo .antes-{fecha}.
/// Al arrancar, el servidor ajusta los dueños de la base restaurada (rol propio), aplica las migraciones que falten y
/// registra la restauración en la bitácora.
/// </summary>
public static class BackupRestore
{
    public const string Argument = "--restore";
    private const string RestoringDatabase = "truecentral_vms_restaurando";

    public static bool IsRequested(string[] args) => args.Length > 0 && args[0] == Argument;

    public static async Task<int> RunAsync(string[] args, EmbeddedPostgres pg, IConfiguration config)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string? file = args.Length > 1 ? args[1] : null;
        if (file is null || !File.Exists(file))
        {
            Console.Error.WriteLine("Uso: TrueCentralVms.Server.exe --restore <respaldo.tcbak> [--password-file <archivo>] [--yes]");
            return 2;
        }
        string? passwordFile = Option(args, "--password-file");
        bool assumeYes = args.Contains("--yes");

        if (RunningServer() is { } running)
        {
            // Con «sc stop» el Watchdog lo vuelve a iniciar a los 15 s: hay que pedirlo desde el Watchdog.
            Console.Error.WriteLine($"El servidor está en ejecución ({running}). Deténgalo antes de restaurar con");
            Console.Error.WriteLine("«Detener servicio» en el Watchdog (ícono junto al reloj); si lo detiene con");
            Console.Error.WriteLine($"sc stop {ServiceSupervisor.WindowsServiceName}, el Watchdog lo vuelve a iniciar.");
            return 3;
        }

        using var loggers = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        var logger = loggers.CreateLogger("Restauración");
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string work = Path.Combine(BackupPaths.Backups(pg), $".restauracion-{stamp}");
        try
        {
            // 1. Abrir y verificar el respaldo completo, sin tocar nada todavía.
            BackupHeader header;
            byte[] raw;
            await using (var input = File.OpenRead(file))
                (header, raw) = BackupFormat.ReadHeader(input);
            Console.WriteLine($"Respaldo: {Path.GetFileName(file)}");
            Console.WriteLine($"  {BackupFormat.Describe(header)}");
            Console.WriteLine($"  hecho en el equipo {header.Machine}{(header.IncludesAnpr ? ", con fotos de patentes" : "")}.");
            if (Version.TryParse(header.ServerVersion, out var backupVersion) && Version.TryParse(BackupService.ServerVersion, out var current)
                && backupVersion > current)
            {
                Console.Error.WriteLine($"El respaldo es de una versión más nueva ({header.ServerVersion}) que este servidor ({BackupService.ServerVersion}). " +
                                        "Actualice el sistema primero.");
                return 4;
            }

            byte[]? key;
            if (header.KeyMode == "password")
            {
                string password = passwordFile is not null ? File.ReadAllText(passwordFile).Trim() : ReadSecret("Contraseña de respaldo: ");
                key = BackupFormat.KeyFor(header, password, null);
                if (key is null) { Console.Error.WriteLine("Contraseña incorrecta."); return 5; }
            }
            else
            {
                string keyPath = BackupPaths.DataKey(pg);
                key = File.Exists(keyPath) ? BackupFormat.KeyFor(header, null, Convert.FromBase64String(File.ReadAllText(keyPath).Trim())) : null;
                if (key is null)
                {
                    Console.Error.WriteLine("Este respaldo se cifró con la llave de datos de otro equipo (no tenía contraseña de respaldo): " +
                                            "solo se puede restaurar en ese equipo.");
                    return 5;
                }
            }

            Directory.CreateDirectory(work);
            string zipPath = Path.Combine(work, "contenido.zip");
            Console.WriteLine("Descifrando y verificando…");
            await using (var input = File.OpenRead(file))
            {
                BackupFormat.ReadHeader(input);
                await using var output = File.Create(zipPath);
                BackupFormat.Decrypt(input, key, header, raw, output);
            }
            using var zip = ZipFile.OpenRead(zipPath);
            int verified = BackupService.VerifyManifest(zip);
            Console.WriteLine($"  Íntegro: {verified} archivo(s) verificados.");

            if (!assumeYes)
            {
                Console.WriteLine();
                Console.WriteLine("Se reemplazará la base de datos y los archivos del sistema por los del respaldo.");
                Console.WriteLine("Lo actual NO se borra: queda guardado con el sufijo «antes» para poder volver.");
                Console.Write("Escriba RESTAURAR para continuar: ");
                if (Console.ReadLine()?.Trim() != "RESTAURAR") { Console.WriteLine("Cancelado: no se cambió nada."); return 1; }
            }

            // 2. Base: se restaura aparte y, si salió bien, se intercambia.
            Console.WriteLine("Iniciando PostgreSQL…");
            await pg.StartAsync(logger);
            string dump = Path.Combine(work, "base.dump");
            zip.GetEntry("db/truecentral_vms.dump")!.ExtractToFile(dump);
            Console.WriteLine("Restaurando la base (puede tardar)…");
            var (tables, migrations) = await BackupService.RestoreIntoAsync(pg, RestoringDatabase, dump, CancellationToken.None, dropAfter: false);
            Console.WriteLine($"  Base restaurada: {tables} tablas, {migrations} migraciones.");

            string previousDb = $"{EmbeddedPostgres.DatabaseName}_antes_{stamp.Replace("-", "_")}";
            await BackupService.AdminExecAsync(pg,
                $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{EmbeddedPostgres.DatabaseName}' AND pid <> pg_backend_pid()",
                CancellationToken.None);
            await BackupService.AdminExecAsync(pg, $"ALTER DATABASE \"{EmbeddedPostgres.DatabaseName}\" RENAME TO \"{previousDb}\"", CancellationToken.None);
            await BackupService.AdminExecAsync(pg, $"ALTER DATABASE \"{RestoringDatabase}\" RENAME TO \"{EmbeddedPostgres.DatabaseName}\"", CancellationToken.None);
            Console.WriteLine($"  La base anterior quedó como {previousDb}.");

            // 3. Llave de datos y archivos.
            var replaced = new List<string>();
            if (zip.GetEntry("secrets/tcvms-data.key") is { } keyEntry)
            {
                string keyPath = BackupPaths.DataKey(pg);
                if (File.Exists(keyPath)) { File.Move(keyPath, $"{keyPath}.antes-{stamp}"); replaced.Add(Path.GetFileName(keyPath)); }
                keyEntry.ExtractToFile(keyPath);
            }
            RestoreDirectory(zip, "files/workflows/", BackupPaths.Workflows(config, pg), stamp, replaced);
            if (header.IncludesAnpr) RestoreDirectory(zip, "files/anpr/", BackupPaths.Anpr(config, pg), stamp, replaced);
            // La licencia está atada al equipo: solo se repone si aquí no hay ninguna (equipo nuevo).
            string license = BackupPaths.License(pg);
            if (!Directory.Exists(license) || !Directory.EnumerateFileSystemEntries(license).Any())
                RestoreDirectory(zip, "files/license/", license, stamp, replaced);

            await File.WriteAllTextAsync(BackupPaths.RestoredMarker(pg),
                $"{Path.GetFileName(file)} ({BackupFormat.Describe(header)}), por {Environment.UserName} en {Environment.MachineName}; " +
                $"la base anterior quedó como {previousDb}" + (replaced.Count > 0 ? $" y se apartaron {string.Join(", ", replaced)} (.antes-{stamp})." : "."));

            pg.Stop(logger);
            Console.WriteLine();
            Console.WriteLine("Restauración terminada. Inicie el servicio:");
            Console.WriteLine($"  sc start {ServiceSupervisor.WindowsServiceName}");
            Console.WriteLine("Al arrancar se ajustan los permisos de la base, se aplican las migraciones que falten y la restauración queda en la bitácora.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"No se pudo restaurar: {ex.Message}");
            Console.Error.WriteLine("La base y los archivos actuales no se reemplazaron si el error ocurrió antes del intercambio.");
            try { pg.Stop(logger); } catch { /* mejor esfuerzo */ }
            return 10;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* mejor esfuerzo */ }
        }
    }

    private static void RestoreDirectory(ZipArchive zip, string prefix, string target, string stamp, List<string> replaced)
    {
        var entries = zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length > 0).ToList();
        if (entries.Count == 0) return;
        if (Directory.Exists(target))
        {
            Directory.Move(target, $"{target}.antes-{stamp}");
            replaced.Add(Path.GetFileName(target));
        }
        string root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        foreach (var entry in entries)
        {
            string destination = Path.GetFullPath(Path.Combine(target, entry.FullName[prefix.Length..]));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // nunca fuera de la carpeta
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    /// <summary>Otro servidor corriendo (servicio o consola) usaría la base mientras se reemplaza.</summary>
    private static string? RunningServer()
    {
        try
        {
            using var service = new ServiceController(ServiceSupervisor.WindowsServiceName);
            if (service.Status != ServiceControllerStatus.Stopped) return $"servicio {ServiceSupervisor.WindowsServiceName}";
        }
        catch (InvalidOperationException) { /* no instalado como servicio */ }
        int self = Environment.ProcessId;
        var other = Process.GetProcessesByName("TrueCentralVms.Server").FirstOrDefault(p => p.Id != self);
        return other is null ? null : $"proceso {other.Id}";
    }

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.WriteLine();
        return text.ToString();
    }
}
