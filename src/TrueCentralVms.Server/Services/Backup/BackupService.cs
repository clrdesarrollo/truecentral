using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Services.Supervisor;

namespace TrueCentralVms.Server.Services.Backup;

/// <summary>
/// Avance de un respaldo: etapa y bytes de esa etapa contra los que se esperan (0 = no se sabe).
/// "dump" (base de datos) y "files" (llave, automatizaciones, licencia, patentes) suman juntos lo escrito en el
/// respaldo, contra el tamaño del respaldo anterior (una estimación); "verify" (relectura completa) y "secondary"
/// (copia al segundo destino) cuentan contra el tamaño exacto del archivo; "retention" no tiene bytes.
/// </summary>
public sealed record BackupProgress(string Stage, long Bytes, long ExpectedBytes);

/// <summary>
/// Respaldos automáticos de la instalación (Sistema → Respaldos). Cada respaldo es un archivo .tcbak cifrado
/// (<see cref="BackupFormat"/>) con la base completa (pg_dump), la llave de datos (sin ella, las claves guardadas de los
/// equipos no se recuperan), los archivos de las automatizaciones, la licencia y, si se pide, las fotos de patentes.
///
/// Cuándo: todos los días a la hora configurada (y, si el servidor estuvo apagado o falló, apenas puede); antes de
/// aplicar las migraciones de una versión nueva; y a pedido. Retención abuelo-padre-hijo
/// (<see cref="BackupPaths.ToDelete"/>), en la carpeta local y en el segundo destino opcional.
///
/// Sin contraseña de respaldo configurada se cifra con la llave de este equipo: protege contra una base dañada o una
/// actualización fallida, pero NO contra la pérdida del equipo. El panel lo advierte hasta que se defina la contraseña.
/// La restauración es por consola con el servicio detenido (<see cref="BackupRestore"/>): nunca desde la web.
/// </summary>
public sealed class BackupService(EmbeddedPostgres pg, IConfiguration config, IServiceScopeFactory scopes,
    CredentialProtector protector, AuditService audit, ILogger<BackupService> logger) : SupervisedBackgroundService
{
    public const string SettingsBlob = "backup.settings";
    public const string StateBlob = "backup.state";
    private const string TestDatabase = "truecentral_vms_prueba";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private RunTracker? _run;        // el respaldo en curso o el último (para el panel)
    private string? _testing;        // el respaldo que se está probando, si hay uno
    // Lo que pasa ANTES de migrar (respaldo previo a la actualización) se registra en la bitácora después: la tabla
    // de auditoría todavía puede no tener las columnas que espera el modelo nuevo.
    private readonly List<(string Action, string? Target, string Detail, bool Success)> _pendingAudit = [];
    private BackupSettings _settings = new();
    private BackupState _state = new();

    public string Directory => BackupPaths.Backups(pg);
    public BackupSettings Settings => _settings;
    public BackupState State => _state;
    public bool Busy => _gate.CurrentCount == 0;

    public static string ServerVersion =>
        (typeof(BackupService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    // ------------------------------------------------------------------ ajustes y estado

    public async Task<BackupSettings> LoadSettingsAsync(CancellationToken ct)
    {
        _settings = await ReadBlobAsync<BackupSettings>(SettingsBlob, ct) ?? new BackupSettings();
        return _settings;
    }

    public async Task SaveSettingsAsync(BackupSettings settings, CancellationToken ct)
    {
        await WriteBlobAsync(SettingsBlob, settings, ct);
        _settings = settings;
    }

    private async Task SaveStateAsync(BackupState state)
    {
        _state = state;
        try { await WriteBlobAsync(StateBlob, state, CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Respaldos: no se pudo guardar el estado."); }
    }

    private async Task<T?> ReadBlobAsync<T>(string name, CancellationToken ct) where T : class
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        string? data = await db.SystemBlobs.Where(b => b.Name == name).Select(b => b.Data).FirstOrDefaultAsync(ct);
        return data is null ? null : JsonSerializer.Deserialize<T>(data, Json);
    }

    private async Task WriteBlobAsync<T>(string name, T value, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var row = await db.SystemBlobs.FirstOrDefaultAsync(b => b.Name == name, ct);
        if (row is null) db.SystemBlobs.Add(row = new SystemBlob { Name = name });
        row.Data = JsonSerializer.Serialize(value, Json);
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public string Protect(string secret) => Convert.ToBase64String(protector.Protect(secret));
    private string? Unprotect(string? cipher) => string.IsNullOrEmpty(cipher) ? null : protector.Unprotect(Convert.FromBase64String(cipher));

    /// <summary>Contraseña de respaldo vigente (null = se cifra con la llave del equipo).</summary>
    private string? BackupPassword => Unprotect(_settings.PasswordCipher);

    private byte[] MachineKey()
    {
        protector.Protect("");   // crea la llave de datos si todavía no existe
        return Convert.FromBase64String(File.ReadAllText(BackupPaths.DataKey(pg)).Trim());
    }

    // ------------------------------------------------------------------ crear

    private const string BusyMessage = "Ya hay un respaldo o una prueba de restauración en curso.";

    /// <summary>
    /// Hace un respaldo completo (uno a la vez). Lanza si falla; el estado queda guardado igual. El diario y el a pedido
    /// corren en un proceso aparte (<see cref="BackupWorker"/>): el servidor sigue atendiendo pase lo que pase con el
    /// respaldo. El previo a una actualización corre aquí mismo: el servidor todavía no atiende a nadie.
    /// </summary>
    public async Task<BackupFileDto> CreateAsync(string kind, CancellationToken ct, IProgress<BackupProgress>? progress = null)
    {
        if (!await _gate.WaitAsync(0, ct))
            throw new InvalidOperationException(BusyMessage);
        return await CreateHeldAsync(BeginRun(kind, startedBy: null), ct, progress);
    }

    /// <summary>
    /// Respaldo a pedido: lo lanza y vuelve enseguida (false si ya hay un respaldo o una prueba en curso). El panel sigue
    /// el avance con <see cref="Progress"/>, también si se recarga, y el resultado queda en la bitácora a nombre de quien
    /// lo pidió.
    /// </summary>
    public bool StartManual(BackupActor actor)
    {
        if (!_gate.Wait(0)) return false;
        var run = BeginRun(BackupKinds.Manual, actor.Username);
        _ = Task.Run(async () =>
        {
            try
            {
                var file = await CreateHeldAsync(run, CancellationToken.None, progress: null);
                string? secondary = _state.LastSecondaryError;
                await actor.LogAsync(audit, "backup-created", file.Name,
                    $"Respaldo a pedido: {Size(file.Size)} en {Duration(run.ToDto(DateTime.UtcNow).ElapsedSeconds)}" +
                    (secondary is null ? "." : $". OJO: {secondary}"), true);
                if (secondary is not null)
                    await actor.LogAsync(audit, "backup-secondary-failed", file.Name, secondary, false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Respaldo a pedido fallido.");
                await actor.LogAsync(audit, "backup-failed", null, $"Falló el respaldo a pedido: {ex.Message}", false);
            }
        });
        return true;
    }

    /// <summary>Con el turno ya tomado (<see cref="_gate"/>): respalda, anota el resultado en el seguimiento y lo suelta.</summary>
    private async Task<BackupFileDto> CreateHeldAsync(RunTracker run, CancellationToken ct, IProgress<BackupProgress>? progress)
    {
        var watch = Stopwatch.StartNew();
        DateTime attempt = DateTime.UtcNow;
        string kind = run.Kind;
        try
        {
            var job = await NewJobAsync(kind, ct);
            run.Prepared(job.ExpectedBytes, !string.IsNullOrWhiteSpace(job.Settings.SecondaryPath));
            var tracking = new RunProgress(run, progress);
            var result = kind == BackupKinds.PreUpdate
                ? await BackupJobRunner.RunAsync(job, ct, tracking)
                : await BackupWorker.RunAsync(job, WorkerTimeout, ct, tracking, logger);
            foreach (string warning in result.Warnings) logger.LogWarning("Respaldos: {Warning}", warning);
            var file = result.File;
            string? secondaryError = result.SecondaryError;
            await SaveStateAsync(_state with
            {
                LastSuccessUtc = DateTime.UtcNow,
                LastSuccessFile = file.Name,
                LastAttemptUtc = attempt,
                LastError = null,
                LastDurationSeconds = Math.Round(watch.Elapsed.TotalSeconds, 1),
                LastSecondaryError = secondaryError,
                LastSecondaryUtc = secondaryError is null && !string.IsNullOrWhiteSpace(_settings.SecondaryPath)
                    ? DateTime.UtcNow : _state.LastSecondaryUtc,
            });
            // Recién con el estado guardado: el panel, al verlo terminado, vuelve a pedir la lista y la salud.
            run.Finish(file, secondaryError, error: null);
            logger.LogInformation("Respaldo {Name} creado ({Size:N0} bytes, {Seconds:0.0} s: {Stages}; {Removed} respaldo(s) viejo(s) borrado(s)).",
                file.Name, file.Size, watch.Elapsed.TotalSeconds, run.StageTimes(), result.Removed);
            if (secondaryError is not null) logger.LogWarning("Respaldos: {Error}", secondaryError);
            return file;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SaveStateAsync(_state with { LastAttemptUtc = attempt, LastError = ex.Message });
            run.Finish(null, null, ex.Message);
            throw;
        }
        catch (OperationCanceledException)
        {
            run.Finish(null, null, "Se interrumpió: se detuvo el servidor o el servicio de respaldos.");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ avance (Sistema → Respaldos)

    /// <summary>Sin avance (ni bytes ni cambio de etapa) por este lapso, el panel avisa que el respaldo parece detenido.</summary>
    public static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(2);

    /// <summary>Lo que el panel muestra de un respaldo ya terminado: después de esto queda solo la lista y la salud.</summary>
    private static readonly TimeSpan ShowFinishedFor = TimeSpan.FromHours(1);

    /// <summary>
    /// Avance para el panel, sin tocar la base: el respaldo en curso (o el último, si terminó hace menos de una hora),
    /// la prueba de restauración que esté corriendo y los umbrales con que el panel avisa.
    /// </summary>
    public BackupProgressDto Progress()
    {
        var now = DateTime.UtcNow;
        var run = Volatile.Read(ref _run)?.ToDto(now);
        if (run is { Running: false, FinishedUtc: { } done } && now - done > ShowFinishedFor) run = null;
        return new BackupProgressDto(Busy, run, Volatile.Read(ref _testing), (int)StallAfter.TotalSeconds, (int)WorkerTimeout.TotalMinutes);
    }

    private RunTracker BeginRun(string kind, string? startedBy)
    {
        var run = new RunTracker(kind, startedBy);
        Volatile.Write(ref _run, run);
        return run;
    }

    /// <summary>
    /// Seguimiento de un respaldo, de principio a fin. Lo escribe el progreso (el hilo que lee al proceso aparte, o el
    /// propio respaldo previo a una actualización) y lo lee el panel. Vive en memoria: si el servidor se reinicia a mitad
    /// de camino, el proceso del respaldo muere con él y queda lo que guarda <see cref="BackupState"/>.
    /// </summary>
    private sealed class RunTracker(string kind, string? startedBy)
    {
        private readonly Lock _sync = new();
        private readonly List<(string Stage, double Seconds)> _stages = [];
        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private string _stage = "prepare";
        private long _bytes, _expected;
        private bool _hasSecondary;
        private DateTime _stageUtc = DateTime.UtcNow, _progressUtc = DateTime.UtcNow;
        private DateTime? _finishedUtc;
        private string? _error, _secondaryError;
        private BackupFileDto? _file;

        public string Kind => kind;

        /// <summary>Lo que se sabe al armar el trabajo: cuánto pesó el anterior y si hay segundo destino.</summary>
        public void Prepared(long expected, bool hasSecondary)
        {
            lock (_sync)
            {
                _expected = expected;
                _hasSecondary = hasSecondary;
            }
        }

        public void Report(BackupProgress p)
        {
            lock (_sync)
            {
                var now = DateTime.UtcNow;
                if (p.Stage != _stage)
                {
                    _stages.Add((_stage, (now - _stageUtc).TotalSeconds));
                    _stage = p.Stage;
                    _stageUtc = now;
                    _progressUtc = now;
                }
                if (p.Bytes != _bytes) _progressUtc = now;
                _bytes = p.Bytes;
                _expected = p.ExpectedBytes;
            }
        }

        public void Finish(BackupFileDto? file, string? secondaryError, string? error)
        {
            lock (_sync)
            {
                var now = DateTime.UtcNow;
                _stages.Add((_stage, (now - _stageUtc).TotalSeconds));
                _finishedUtc = now;
                _file = file;
                _secondaryError = secondaryError;
                _error = error;
            }
        }

        public BackupRunDto ToDto(DateTime now)
        {
            lock (_sync)
            {
                var end = _finishedUtc ?? now;
                return new BackupRunDto(kind, startedBy, _startedUtc, _finishedUtc is null, _stage, _bytes, _expected, _hasSecondary,
                    Math.Round((end - _startedUtc).TotalSeconds, 1), Math.Round((end - _stageUtc).TotalSeconds, 1),
                    Math.Round((end - _progressUtc).TotalSeconds, 1),
                    _finishedUtc, _finishedUtc is null ? null : _error is null, _error, _file, _secondaryError);
            }
        }

        /// <summary>Cuánto tardó cada etapa, para el log: "prepare 1.2 s, dump 151.0 s, …".</summary>
        public string StageTimes()
        {
            lock (_sync)
                return string.Join(", ", _stages.Select(s => FormattableString.Invariant($"{s.Stage} {s.Seconds:0.0} s")));
        }
    }

    private static string StageLabel(string stage) => stage switch
    {
        "dump" => "base de datos",
        "files" => "archivos",
        "verify" => "verificación",
        "retention" => "retención",
        "secondary" => "segundo destino",
        _ => "preparando",
    };

    /// <summary>Progreso que anota en el seguimiento y sigue de largo al de afuera (el estado del arranque, en una actualización).</summary>
    private sealed class RunProgress(RunTracker run, IProgress<BackupProgress>? next) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value)
        {
            run.Report(value);
            next?.Report(value);
        }
    }

    /// <summary>"45 s", "3 min 12 s", "1 h 05 min".</summary>
    public static string Duration(double seconds) => seconds switch
    {
        < 60 => $"{Math.Max(0, seconds):0} s",
        < 3600 => $"{(int)seconds / 60} min {(int)seconds % 60:00} s",
        _ => $"{(int)seconds / 3600} h {(int)seconds % 3600 / 60:00} min",
    };

    /// <summary>Tope del proceso aparte: si se cuelga (p. ej. una carpeta de red que no responde), se mata.</summary>
    private TimeSpan WorkerTimeout => TimeSpan.FromMinutes(Math.Clamp(config.GetValue("Backup:TimeoutMinutes", 180), 5, 1440));

    private async Task<BackupJob> NewJobAsync(string kind, CancellationToken ct)
    {
        // Lo que se espera escribir: el último respaldo bueno es la mejor referencia (la base crece de a poco).
        long expected = 0;
        try
        {
            if (_state.LastSuccessFile is { Length: > 0 } last && new FileInfo(Path.Combine(Directory, last)) is { Exists: true } info)
                expected = info.Length;
        }
        catch (Exception) { /* sin referencia: el avance va sin porcentaje */ }
        string? password = BackupPassword;
        var (user, dumpPassword) = pg.DumpCredentials;
        return new BackupJob
        {
            Kind = kind,
            Directory = Directory,
            PgDumpPath = pg.ToolPath("pg_dump.exe"),
            PgPort = pg.Port,
            DumpUser = user,
            DumpPassword = dumpPassword,
            DatabaseName = EmbeddedPostgres.DatabaseName,
            Password = password,
            MachineKey = password is null ? MachineKey() : null,
            DataKeyPath = BackupPaths.DataKey(pg),
            WorkflowsDirectory = BackupPaths.Workflows(config, pg),
            LicenseDirectory = BackupPaths.License(pg),
            AnprDirectory = BackupPaths.Anpr(config, pg),
            LastMigration = await LastMigrationAsync(ct),
            ExpectedBytes = expected,
            Settings = _settings,
            SecondaryPassword = Unprotect(_settings.SecondaryPasswordCipher),
        };
    }

    private async Task<string?> LastMigrationAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            return (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault();
        }
        catch (Exception) { return null; }
    }

    internal static ProcessStartInfo PgToolStart(EmbeddedPostgres pg, string exe, string password, IEnumerable<string> args) =>
        PgToolStart(pg.ToolPath(exe), password, args);

    internal static ProcessStartInfo PgToolStart(string toolPath, string password, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(toolPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        foreach (string v in new[] { "PGHOST", "PGPORT", "PGUSER", "PGDATABASE", "PGOPTIONS", "PGSERVICE", "PGSSLMODE", "PGPASSFILE" })
            psi.Environment.Remove(v);
        psi.Environment["PGPASSWORD"] = password;
        return psi;
    }

    // ------------------------------------------------------------------ segundo destino
    /// <summary>Prueba el segundo destino escribiendo y borrando un archivo. Devuelve el error o null.</summary>
    public string? TestSecondary(BackupSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SecondaryPath)) return "No hay un segundo destino configurado.";
        string dir = settings.SecondaryPath.Trim();
        try
        {
            if (SecondaryShare.Connect(dir, settings.SecondaryUser, Unprotect(settings.SecondaryPasswordCipher)) is { } connectError)
                return connectError;
            System.IO.Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, $".tcvms-prueba-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "prueba");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) { return $"No se pudo escribir en {dir}: {ex.Message}"; }
    }

    // ------------------------------------------------------------------ listar, abrir, probar

    public IReadOnlyList<BackupFileDto> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        HashSet<string>? secondary = null;
        if (!string.IsNullOrWhiteSpace(_settings.SecondaryPath))
        {
            // Una carpeta de red caída no debe colgar el panel: se consulta con tope.
            var listing = Task.Run(() => System.IO.Directory.EnumerateFiles(_settings.SecondaryPath.Trim(), "*.tcbak")
                .Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase));
            try { if (listing.Wait(TimeSpan.FromSeconds(3))) secondary = listing.Result; } catch { /* inaccesible */ }
        }
        var result = new List<BackupFileDto>();
        foreach (string path in System.IO.Directory.EnumerateFiles(Directory, "*.tcbak"))
        {
            string name = Path.GetFileName(path);
            if (!BackupPaths.IsValidName(name)) continue;
            try { result.Add(ToDto(path, BackupFormat.ReadHeader(path), secondary?.Contains(name))); }
            catch (Exception ex) { logger.LogWarning("Respaldos: {Name} no se pudo leer: {Error}", name, ex.Message); }
        }
        return result.OrderByDescending(f => f.CreatedUtc).ToList();
    }

    internal static BackupFileDto ToDto(string path, BackupHeader h, bool? onSecondary) =>
        new(Path.GetFileName(path), h.Kind, h.CreatedUtc, new FileInfo(path).Length, h.KeyMode, h.ServerVersion,
            h.LastMigration, h.IncludesAnpr, onSecondary);

    /// <summary>Ruta de un respaldo por su nombre (validado: nunca una ruta que llega de afuera), o null si no existe.</summary>
    public string? PathOf(string name)
    {
        if (!BackupPaths.IsValidName(name)) return null;
        string path = Path.Combine(Directory, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Prueba completa de un respaldo: lo descifra, comprueba el SHA-256 de cada archivo contra su manifiesto y restaura
    /// la base en una base de PRUEBA (truecentral_vms_prueba) que después se borra. La base real no se toca.
    /// </summary>
    public async Task<string> VerifyAsync(string name, CancellationToken ct)
    {
        string path = PathOf(name) ?? throw new FileNotFoundException("No existe ese respaldo.");
        if (!await _gate.WaitAsync(0, ct))
            throw new InvalidOperationException(BusyMessage);
        Volatile.Write(ref _testing, name);
        string work = Path.Combine(Directory, $".prueba-{Guid.NewGuid():N}");
        try
        {
            System.IO.Directory.CreateDirectory(work);
            string zipPath = Path.Combine(work, "contenido.zip");
            await using (var input = File.OpenRead(path))
            {
                var (header, raw) = BackupFormat.ReadHeader(input);
                byte[] key = BackupFormat.KeyFor(header, BackupPassword, MachineKey())
                    ?? throw new InvalidOperationException(header.KeyMode == "password"
                        ? "La contraseña de respaldo actual no abre este respaldo: se hizo con una contraseña anterior."
                        : "Este respaldo se cifró con la llave de otro equipo: no se puede abrir aquí.");
                await using var output = File.Create(zipPath);
                BackupFormat.Decrypt(input, key, header, raw, output);
            }
            int files;
            string dump = Path.Combine(work, "base.dump");
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                files = VerifyManifest(zip);
                zip.GetEntry("db/truecentral_vms.dump")!.ExtractToFile(dump);
            }
            var (tables, migrations) = await RestoreIntoAsync(pg, TestDatabase, dump, ct, dropAfter: true);
            return $"Respaldo íntegro: {files} archivo(s) verificados contra su manifiesto, y la base se restauró sin errores en una " +
                   $"base de prueba ({tables} tablas, {migrations} migraciones) que luego se borró.";
        }
        finally
        {
            try { System.IO.Directory.Delete(work, recursive: true); } catch { /* mejor esfuerzo */ }
            Volatile.Write(ref _testing, null);
            _gate.Release();
        }
    }

    /// <summary>Comprueba tamaño y SHA-256 de cada archivo del ZIP contra el manifiesto. Devuelve cuántos verificó.</summary>
    internal static int VerifyManifest(ZipArchive zip)
    {
        var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("El respaldo no tiene manifiesto.");
        BackupJobRunner.Manifest manifest;
        using (var stream = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<BackupJobRunner.Manifest>(stream, Json) ?? throw new InvalidDataException("Manifiesto ilegible.");
        foreach (var e in manifest.Entries)
        {
            var entry = zip.GetEntry(e.Path) ?? throw new InvalidDataException($"Falta {e.Path} en el respaldo.");
            using var stream = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[1 << 16];
            long total = 0;
            int read;
            while ((read = stream.Read(buffer)) > 0) { hash.AppendData(buffer, 0, read); total += read; }
            if (total != e.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != e.Sha256)
                throw new InvalidDataException($"{e.Path} no coincide con su manifiesto (dañado).");
        }
        if (manifest.Entries.All(e => e.Path != "db/truecentral_vms.dump"))
            throw new InvalidDataException("El respaldo no contiene la base de datos.");
        return manifest.Entries.Count;
    }

    /// <summary>
    /// Restaura un pg_dump en <paramref name="database"/> (que se recrea) con el superusuario. Devuelve tablas y
    /// migraciones de la base restaurada. Lo usan la prueba (que la borra después) y la restauración por consola.
    /// </summary>
    internal static async Task<(long Tables, long Migrations)> RestoreIntoAsync(EmbeddedPostgres pg, string database, string dumpFile,
        CancellationToken ct, bool dropAfter)
    {
        await AdminExecAsync(pg, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", ct);
        await AdminExecAsync(pg, $"CREATE DATABASE \"{database}\"", ct);
        try
        {
            var (user, password) = pg.AdminCredentials;
            var psi = PgToolStart(pg, "pg_restore.exe", password,
                ["-h", "127.0.0.1", "-p", pg.Port.ToString(), "-U", user, "-d", database,
                 "--no-owner", "--no-privileges", "--exit-on-error", "--no-password", dumpFile]);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo ejecutar pg_restore.");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"pg_restore falló (código {process.ExitCode}): {(await stderr).Trim()}");
            await stdout;

            await using var conn = new NpgsqlConnection(pg.AdminConnectionString(database));
            await conn.OpenAsync(ct);
            await using var tables = new NpgsqlCommand(
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'", conn);
            long tableCount = (long)(await tables.ExecuteScalarAsync(ct) ?? 0L);
            await using var migrations = new NpgsqlCommand("SELECT count(*) FROM \"__EFMigrationsHistory\"", conn);
            long migrationCount = (long)(await migrations.ExecuteScalarAsync(ct) ?? 0L);
            return (tableCount, migrationCount);
        }
        finally
        {
            if (dropAfter)
                try { await AdminExecAsync(pg, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", CancellationToken.None); }
                catch { /* mejor esfuerzo */ }
        }
    }

    internal static async Task AdminExecAsync(EmbeddedPostgres pg, string sql, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(pg.AdminConnectionString("postgres"));
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------------ antes de una actualización

    /// <summary>
    /// Respaldo previo a aplicar las migraciones de una versión nueva (lo llama Program antes de MigrateAsync). Si
    /// falla, el servidor sigue arrancando (no dejar el sistema caído por un respaldo), pero queda en el log y en la
    /// bitácora apenas termine la migración.
    /// </summary>
    public async Task BackupBeforeUpdateAsync(IReadOnlyList<string> pending, CancellationToken ct,
        IProgress<BackupProgress>? progress = null)
    {
        try { await LoadSettingsAsync(ct); } catch (Exception) { _settings = new BackupSettings(); }
        try { _state = await ReadBlobAsync<BackupState>(StateBlob, ct) ?? new BackupState(); } catch (Exception) { /* base antigua */ }
        logger.LogInformation("Hay {Count} migración(es) pendiente(s): respaldando la base antes de actualizar...", pending.Count);
        try
        {
            var file = await CreateAsync(BackupKinds.PreUpdate, ct, progress);
            _pendingAudit.Add(("backup-created", file.Name,
                $"Respaldo automático antes de actualizar a la versión {ServerVersion} ({pending.Count} migración(es) pendiente(s): " +
                $"{string.Join(", ", pending.Take(5))}{(pending.Count > 5 ? "…" : "")}). {Size(file.Size)}.", true));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "No se pudo respaldar la base antes de actualizar; la actualización sigue igual.");
            _pendingAudit.Add(("backup-failed", null, $"Falló el respaldo previo a la actualización a {ServerVersion}: {ex.Message}", false));
        }
    }

    // ------------------------------------------------------------------ programador

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            await LoadSettingsAsync(stoppingToken);
            _state = await ReadBlobAsync<BackupState>(StateBlob, stoppingToken) ?? _state;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Respaldos: no se pudieron leer los ajustes; se usan los de fábrica.");
        }
        foreach (var (action, target, detail, success) in _pendingAudit)
            await audit.LogSystemAsync("backup", action, targetType: "backup", targetName: target, detail: detail, success: success);
        _pendingAudit.Clear();
        await AuditRestoreMarkerAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_settings.Enabled && !Busy && DueReason(DateTime.Now) is { } reason)
                {
                    if (reason == "programado")
                        await SaveStateAsync(_state with { LastScheduledDay = DateTime.Now.ToString("yyyy-MM-dd") });
                    // Ya hay uno fresco (p. ej. el de antes de actualizar, minutos atrás): el diario de hoy queda cubierto.
                    if (reason == "programado" && _state.LastSuccessUtc is { } recent && DateTime.UtcNow - recent < RecentEnough)
                    {
                        logger.LogInformation("Respaldo diario omitido: ya hay uno de hace {Minutes:0} min ({File}).",
                            (DateTime.UtcNow - recent).TotalMinutes, _state.LastSuccessFile);
                        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                        continue;
                    }
                    try
                    {
                        var file = await CreateAsync(BackupKinds.Daily, stoppingToken);
                        await audit.LogSystemAsync("backup", "backup-created", targetType: "backup", targetName: file.Name,
                            detail: $"Respaldo {(reason == "programado" ? "diario" : "atrasado (no había uno reciente)")}: {Size(file.Size)}" +
                                    (_state.LastSecondaryError is { } err ? $". OJO: {err}" : "."));
                        if (_state.LastSecondaryError is { } secondaryError)
                            await audit.LogSystemAsync("backup", "backup-secondary-failed", targetType: "backup", targetName: file.Name,
                                detail: secondaryError, success: false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "Respaldo automático fallido.");
                        await audit.LogSystemAsync("backup", "backup-failed", targetType: "backup",
                            detail: $"Falló el respaldo automático: {ex.Message}", success: false);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Respaldos: error en el programador.");
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    /// <summary>
    /// Un respaldo bueno así de reciente (el de antes de actualizar, minutos atrás) cubre el diario de hoy. Corto a
    /// propósito: si se omite el diario, el siguiente sale 24 h después, y el hueco no debe pasar las 26 h con que
    /// <see cref="Health"/> da los respaldos por atrasados (con 12 h, un respaldo hecho a media tarde dejaba al día
    /// siguiente sin respaldo hasta pasadas las 26 h).
    /// </summary>
    private static readonly TimeSpan RecentEnough = TimeSpan.FromHours(1);

    /// <summary>
    /// ¿Toca respaldar? "programado": ya pasó la hora de hoy y hoy no se intentó. "atrasado": no hay un respaldo bueno
    /// de las últimas 26 h (servidor apagado a la hora, o el anterior falló) y el último intento fue hace más de una
    /// hora: reintenta solo, sin martillar. Al arrancar se espera un minuto, para no respaldar en medio del arranque;
    /// en una instalación nueva el primero sale entonces.
    /// </summary>
    private string? DueReason(DateTime now)
    {
        if (now.TimeOfDay >= _settings.DailyTimeOfDay.ToTimeSpan() && _state.LastScheduledDay != now.ToString("yyyy-MM-dd"))
            return "programado";
        bool stale = _state.LastSuccessUtc is not { } ok || DateTime.UtcNow - ok > TimeSpan.FromHours(26);
        bool rested = _state.LastAttemptUtc is not { } last || DateTime.UtcNow - last > TimeSpan.FromHours(1);
        return stale && rested && DateTime.UtcNow - _startedUtc > TimeSpan.FromMinutes(1) ? "atrasado" : null;
    }

    private async Task AuditRestoreMarkerAsync()
    {
        string marker = BackupPaths.RestoredMarker(pg);
        if (!File.Exists(marker)) return;
        try
        {
            string detail = await File.ReadAllTextAsync(marker);
            await audit.LogSystemAsync("backup", "backup-restored", targetType: "backup",
                detail: $"La instalación se restauró desde un respaldo por consola: {detail}");
            File.Delete(marker);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Respaldos: no se pudo registrar la restauración."); }
    }

    /// <summary>
    /// Salud para el panel de servicios: false si no hay un respaldo bueno reciente, si el segundo destino falla o si el
    /// respaldo en curso dejó de avanzar. Es una advertencia (<see cref="Supervisor.BackupManagedService"/>), no una
    /// caída: reiniciar el programador no la arregla. Con <paramref name="current"/> = false, solo lo guardado (la
    /// página de Respaldos muestra el respaldo en curso en su propio cuadro, con <see cref="Progress"/>).
    /// </summary>
    public (bool Healthy, string Detail) Health(bool current = true)
    {
        if (current && Busy && Volatile.Read(ref _run)?.ToDto(DateTime.UtcNow) is { Running: true } run)
            return run.IdleSeconds >= StallAfter.TotalSeconds
                ? (false, $"El respaldo en curso lleva {Duration(run.IdleSeconds)} sin avanzar ({StageLabel(run.Stage)}).")
                : (true, $"Respaldando… ({StageLabel(run.Stage)}, {Duration(run.ElapsedSeconds)}).");
        if (current && Busy) return (true, "Probando un respaldo…");
        if (_state.LastSuccessUtc is not { } ok)
            return DateTime.UtcNow - _startedUtc < TimeSpan.FromHours(26) && _state.LastError is null
                ? (true, "Todavía no hay respaldos: el primero se hace al minuto de arrancar.")
                : (false, $"No hay ningún respaldo bueno. {_state.LastError}".Trim());
        if (_state.LastError is { } error && _state.LastAttemptUtc > ok)
            return (false, $"El último respaldo falló: {error}");
        if (DateTime.UtcNow - ok > TimeSpan.FromHours(26))
            return (false, $"El último respaldo bueno es del {ok.ToLocalTime():dd-MM-yyyy HH:mm}.");
        if (_state.LastSecondaryError is { } secondary)
            return (false, secondary);
        return (true, $"Último respaldo: {ok.ToLocalTime():dd-MM-yyyy HH:mm}{(BackupPassword is null ? " (sin contraseña de respaldo)" : "")}.");
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };
}
