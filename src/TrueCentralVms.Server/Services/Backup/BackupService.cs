using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services.Backup;

/// <summary>
/// Avance de un respaldo: etapa ("dump" = base de datos, "files" = archivos,
/// "verify" = relectura completa), bytes de la base ya escritos y los que se
/// esperan (el tamaño del respaldo anterior; 0 = no se sabe).
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
    CredentialProtector protector, AuditService audit, ILogger<BackupService> logger) : BackgroundService
{
    public const string SettingsBlob = "backup.settings";
    public const string StateBlob = "backup.state";
    private const string TestDatabase = "truecentral_vms_prueba";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] NotCompressed = [".jpg", ".jpeg", ".png", ".mp3", ".ogg", ".m4a", ".zip", ".dump"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DateTime _startedUtc = DateTime.UtcNow;
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

    /// <summary>Hace un respaldo completo (uno a la vez). Lanza si falla; el estado queda guardado igual.</summary>
    public async Task<BackupFileDto> CreateAsync(string kind, CancellationToken ct, IProgress<BackupProgress>? progress = null)
    {
        if (!await _gate.WaitAsync(0, ct))
            throw new InvalidOperationException("Ya hay un respaldo o una prueba de restauración en curso.");
        var watch = Stopwatch.StartNew();
        DateTime attempt = DateTime.UtcNow;
        try
        {
            var file = await CreateCoreAsync(kind, ct, progress);
            int removed = ApplyRetention(Directory, _settings);
            string? secondaryError = CopyToSecondary(Path.Combine(Directory, file.Name), _settings);
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
            logger.LogInformation("Respaldo {Name} creado ({Size:N0} bytes, {Seconds:0.0} s; {Removed} respaldo(s) viejo(s) borrado(s)).",
                file.Name, file.Size, watch.Elapsed.TotalSeconds, removed);
            if (secondaryError is not null) logger.LogWarning("Respaldos: {Error}", secondaryError);
            return file;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SaveStateAsync(_state with { LastAttemptUtc = attempt, LastError = ex.Message });
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<BackupFileDto> CreateCoreAsync(string kind, CancellationToken ct, IProgress<BackupProgress>? progress)
    {
        System.IO.Directory.CreateDirectory(Directory);
        // Lo que se espera escribir: el último respaldo bueno es la mejor referencia (la base crece de a poco).
        long expected = 0;
        try
        {
            if (_state.LastSuccessFile is { Length: > 0 } last && new FileInfo(Path.Combine(Directory, last)) is { Exists: true } info)
                expected = info.Length;
        }
        catch (Exception) { /* sin referencia: el avance va sin porcentaje */ }
        var settings = _settings;
        string? password = BackupPassword;
        string name = BackupPaths.NewName(DateTime.Now, kind);
        string target = Path.Combine(Directory, name);
        string part = target + ".part";

        var template = new BackupHeader
        {
            CreatedUtc = DateTime.UtcNow,
            Kind = kind,
            ServerVersion = ServerVersion,
            LastMigration = await LastMigrationAsync(ct),
            Machine = Environment.MachineName,
            IncludesAnpr = settings.IncludeAnpr,
        };
        var (header, key) = BackupFormat.NewHeader(template, password, password is null ? MachineKey() : null);
        var entries = new List<ManifestEntry>();
        try
        {
            await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                byte[] raw = BackupFormat.WriteHeader(file, header);
                using var encrypted = new BackupFormat.EncryptingStream(file, key, header, raw);
                using (var zip = new ZipArchive(encrypted, ZipArchiveMode.Create, leaveOpen: true))
                {
                    long dumped = 0;
                    await AddDumpAsync(zip, entries, ct,
                        progress is null ? null : bytes => progress.Report(new BackupProgress("dump", dumped = bytes, expected)));
                    progress?.Report(new BackupProgress("files", dumped, expected));
                    AddFile(zip, "secrets/tcvms-data.key", BackupPaths.DataKey(pg), entries);
                    AddDirectory(zip, "files/workflows", BackupPaths.Workflows(config, pg), entries, ct);
                    AddDirectory(zip, "files/license", BackupPaths.License(pg), entries, ct);
                    if (settings.IncludeAnpr) AddDirectory(zip, "files/anpr", BackupPaths.Anpr(config, pg), entries, ct);
                    await using var manifest = zip.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
                    await JsonSerializer.SerializeAsync(manifest, new Manifest(header, entries), Json, ct);
                }
            }
            // Releer todo: cada bloque cifrado se valida con su etiqueta, así que un archivo que se lee entero está sano.
            progress?.Report(new BackupProgress("verify", new FileInfo(part).Length, expected));
            await using (var check = File.OpenRead(part))
            {
                var (readHeader, raw) = BackupFormat.ReadHeader(check);
                BackupFormat.Decrypt(check, key, readHeader, raw, Stream.Null);
            }
            File.Move(part, target);
        }
        catch
        {
            try { File.Delete(part); } catch { /* mejor esfuerzo */ }
            throw;
        }
        return ToDto(target, header, onSecondary: null);
    }

    private sealed record ManifestEntry(string Path, long Size, string Sha256);
    private sealed record Manifest(BackupHeader Header, List<ManifestEntry> Entries);

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

    private async Task AddDumpAsync(ZipArchive zip, List<ManifestEntry> entries, CancellationToken ct, Action<long>? onBytes = null)
    {
        const string path = "db/truecentral_vms.dump";
        var (user, password) = pg.DumpCredentials;
        var psi = PgTool("pg_dump.exe", password,
            ["-h", "127.0.0.1", "-p", pg.Port.ToString(), "-U", user, "-d", EmbeddedPostgres.DatabaseName, "-Fc", "--no-password"]);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo ejecutar pg_dump.");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        long size;
        string sha;
        await using (var target = zip.CreateEntry(path, CompressionLevel.NoCompression).Open())
            (size, sha) = await CopyHashingAsync(process.StandardOutput.BaseStream, target, ct, onBytes);
        await process.WaitForExitAsync(ct);
        string errors = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"pg_dump falló (código {process.ExitCode}): {errors.Trim()}");
        entries.Add(new ManifestEntry(path, size, sha));
    }

    private static void AddFile(ZipArchive zip, string path, string source, List<ManifestEntry> entries)
    {
        if (!File.Exists(source)) return;
        string ext = Path.GetExtension(source).ToLowerInvariant();
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = zip.CreateEntry(path, NotCompressed.Contains(ext) ? CompressionLevel.NoCompression : CompressionLevel.Fastest).Open();
        var (size, sha) = CopyHashingAsync(input, target, CancellationToken.None).GetAwaiter().GetResult();
        entries.Add(new ManifestEntry(path, size, sha));
    }

    private static void AddDirectory(ZipArchive zip, string prefix, string source, List<ManifestEntry> entries, CancellationToken ct)
    {
        if (!System.IO.Directory.Exists(source)) return;
        foreach (string file in System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            try { AddFile(zip, $"{prefix}/{relative}", file, entries); }
            catch (FileNotFoundException) { /* se purgó mientras se respaldaba */ }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static async Task<(long Size, string Sha256)> CopyHashingAsync(Stream input, Stream output, CancellationToken ct,
        Action<long>? onBytes = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1 << 16];
        long total = 0, reported = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);   // el ZIP escribe sincrónico sobre el flujo cifrado
            total += read;
            if (onBytes is not null && total - reported >= 4 << 20) onBytes(reported = total);   // cada 4 MB
        }
        onBytes?.Invoke(total);
        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    internal ProcessStartInfo PgTool(string exe, string password, IEnumerable<string> args) => PgToolStart(pg, exe, password, args);

    internal static ProcessStartInfo PgToolStart(EmbeddedPostgres pg, string exe, string password, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(pg.ToolPath(exe))
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

    // ------------------------------------------------------------------ retención y segundo destino

    private int ApplyRetention(string dir, BackupSettings settings)
    {
        int removed = 0;
        try
        {
            var names = System.IO.Directory.EnumerateFiles(dir, "*.tcbak").Select(Path.GetFileName).OfType<string>();
            foreach (string name in BackupPaths.ToDelete(names, settings))
            {
                File.Delete(Path.Combine(dir, name));
                removed++;
            }
            // Restos de respaldos interrumpidos (corte de luz a mitad de uno).
            foreach (string part in System.IO.Directory.EnumerateFiles(dir, "*.part"))
                if (File.GetLastWriteTimeUtc(part) < DateTime.UtcNow.AddDays(-1)) File.Delete(part);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Respaldos: no se pudo aplicar la retención en {Dir}.", dir); }
        return removed;
    }

    /// <summary>Copia el respaldo al segundo destino y aplica allí la misma retención. Devuelve el error o null.</summary>
    private string? CopyToSecondary(string file, BackupSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SecondaryPath)) return null;
        string dir = settings.SecondaryPath.Trim();
        try
        {
            if (SecondaryShare.Connect(dir, settings.SecondaryUser, Unprotect(settings.SecondaryPasswordCipher)) is { } connectError)
                return connectError;
            System.IO.Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, Path.GetFileName(file));
            File.Copy(file, target + ".part", overwrite: true);
            if (new FileInfo(target + ".part").Length != new FileInfo(file).Length)
                return $"La copia en {dir} quedó incompleta.";
            File.Move(target + ".part", target, overwrite: true);
            ApplyRetention(dir, settings);
            return null;
        }
        catch (Exception ex)
        {
            return $"No se pudo copiar al segundo destino ({dir}): {ex.Message}";
        }
    }

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

    private static BackupFileDto ToDto(string path, BackupHeader h, bool? onSecondary) =>
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
            throw new InvalidOperationException("Ya hay un respaldo o una prueba de restauración en curso.");
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
            _gate.Release();
        }
    }

    /// <summary>Comprueba tamaño y SHA-256 de cada archivo del ZIP contra el manifiesto. Devuelve cuántos verificó.</summary>
    internal static int VerifyManifest(ZipArchive zip)
    {
        var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("El respaldo no tiene manifiesto.");
        Manifest manifest;
        using (var stream = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<Manifest>(stream, Json) ?? throw new InvalidDataException("Manifiesto ilegible.");
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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
    /// ¿Toca respaldar? "programado": ya pasó la hora de hoy y hoy no se intentó. "atrasado": no hay un respaldo bueno
    /// de las últimas 26 h (servidor apagado a la hora, o el anterior falló) y el último intento fue hace más de una
    /// hora: reintenta solo, sin martillar. En una instalación nueva, el primero sale a los pocos minutos de arrancar.
    /// </summary>
    /// <summary>Un respaldo bueno así de reciente cubre el diario de hoy.</summary>
    private static readonly TimeSpan RecentEnough = TimeSpan.FromHours(12);

    private string? DueReason(DateTime now)
    {
        if (now.TimeOfDay >= _settings.DailyTimeOfDay.ToTimeSpan() && _state.LastScheduledDay != now.ToString("yyyy-MM-dd"))
            return "programado";
        bool stale = _state.LastSuccessUtc is not { } ok || DateTime.UtcNow - ok > TimeSpan.FromHours(26);
        bool rested = _state.LastAttemptUtc is not { } last || DateTime.UtcNow - last > TimeSpan.FromHours(1);
        return stale && rested && DateTime.UtcNow - _startedUtc > TimeSpan.FromMinutes(5) ? "atrasado" : null;
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

    /// <summary>Salud para el panel de servicios: falla si no hay un respaldo bueno reciente o si el segundo destino falla.</summary>
    public (bool Healthy, string Detail) Health()
    {
        if (Busy) return (true, "Respaldando…");
        if (_state.LastSuccessUtc is not { } ok)
            return DateTime.UtcNow - _startedUtc < TimeSpan.FromHours(26) && _state.LastError is null
                ? (true, "Todavía no hay respaldos: el primero se hace a los pocos minutos de arrancar.")
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
