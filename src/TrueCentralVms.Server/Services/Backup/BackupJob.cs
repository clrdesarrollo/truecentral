using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrueCentralVms.Server.Services.Backup;

/// <summary>
/// Todo lo que necesita un respaldo para hacerse sin el servidor: rutas, credenciales de PostgreSQL, claves y ajustes.
/// Lo arma <see cref="BackupService"/> y lo ejecuta <see cref="BackupJobRunner"/>, en el mismo proceso o en el proceso
/// aparte (<see cref="BackupWorker"/>), que lo recibe por la entrada estándar: las claves nunca van en la línea de
/// comandos.
/// </summary>
public sealed record BackupJob
{
    public required string Kind { get; init; }
    public required string Directory { get; init; }
    public required string PgDumpPath { get; init; }
    public required int PgPort { get; init; }
    public required string DumpUser { get; init; }
    public required string DumpPassword { get; init; }
    public required string DatabaseName { get; init; }
    /// <summary>Contraseña de respaldo; null = se cifra con <see cref="MachineKey"/>.</summary>
    public string? Password { get; init; }
    public byte[]? MachineKey { get; init; }
    public required string DataKeyPath { get; init; }
    public required string WorkflowsDirectory { get; init; }
    public required string LicenseDirectory { get; init; }
    public required string AnprDirectory { get; init; }
    public string? LastMigration { get; init; }
    /// <summary>Tamaño del respaldo anterior: referencia para el porcentaje de avance (0 = no se sabe).</summary>
    public long ExpectedBytes { get; init; }
    public required BackupSettings Settings { get; init; }
    public string? SecondaryPassword { get; init; }
}

/// <summary>Resultado de un respaldo: el archivo, cuántos viejos borró la retención y cómo le fue al segundo destino.</summary>
public sealed record BackupJobResult(BackupFileDto File, int Removed, string? SecondaryError, List<string> Warnings);

/// <summary>
/// Hace un respaldo completo a partir de un <see cref="BackupJob"/>: pg_dump + archivos en un .tcbak cifrado, relectura
/// completa, retención y copia al segundo destino. No usa nada del servidor (ni la base por EF, ni servicios, ni el
/// registro): así corre igual dentro del servidor que en el proceso aparte.
/// </summary>
internal static class BackupJobRunner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] NotCompressed = [".jpg", ".jpeg", ".png", ".mp3", ".ogg", ".m4a", ".zip", ".dump"];

    public static async Task<BackupJobResult> RunAsync(BackupJob job, CancellationToken ct, IProgress<BackupProgress>? progress)
    {
        var warnings = new List<string>();
        var file = await CreateAsync(job, ct, progress);
        int removed = ApplyRetention(job.Directory, job.Settings, warnings);
        string? secondaryError = CopyToSecondary(Path.Combine(job.Directory, file.Name), job, warnings);
        return new BackupJobResult(file, removed, secondaryError, warnings);
    }

    private static async Task<BackupFileDto> CreateAsync(BackupJob job, CancellationToken ct, IProgress<BackupProgress>? progress)
    {
        Directory.CreateDirectory(job.Directory);
        string name = BackupPaths.NewName(DateTime.Now, job.Kind);
        string target = Path.Combine(job.Directory, name);
        string part = target + ".part";
        long expected = job.ExpectedBytes;

        var template = new BackupHeader
        {
            CreatedUtc = DateTime.UtcNow,
            Kind = job.Kind,
            ServerVersion = BackupService.ServerVersion,
            LastMigration = job.LastMigration,
            Machine = Environment.MachineName,
            IncludesAnpr = job.Settings.IncludeAnpr,
        };
        var (header, key) = BackupFormat.NewHeader(template, job.Password, job.Password is null ? job.MachineKey : null);
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
                    await AddDumpAsync(job, zip, entries, ct,
                        progress is null ? null : bytes => progress.Report(new BackupProgress("dump", dumped = bytes, expected)));
                    progress?.Report(new BackupProgress("files", dumped, expected));
                    AddFile(zip, "secrets/tcvms-data.key", job.DataKeyPath, entries);
                    AddDirectory(zip, "files/workflows", job.WorkflowsDirectory, entries, ct);
                    AddDirectory(zip, "files/license", job.LicenseDirectory, entries, ct);
                    if (job.Settings.IncludeAnpr) AddDirectory(zip, "files/anpr", job.AnprDirectory, entries, ct);
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
        return BackupService.ToDto(target, header, onSecondary: null);
    }

    internal sealed record ManifestEntry(string Path, long Size, string Sha256);
    internal sealed record Manifest(BackupHeader Header, List<ManifestEntry> Entries);

    private static async Task AddDumpAsync(BackupJob job, ZipArchive zip, List<ManifestEntry> entries, CancellationToken ct,
        Action<long>? onBytes = null)
    {
        const string path = "db/truecentral_vms.dump";
        var psi = BackupService.PgToolStart(job.PgDumpPath, job.DumpPassword,
            ["-h", "127.0.0.1", "-p", job.PgPort.ToString(), "-U", job.DumpUser, "-d", job.DatabaseName, "-Fc", "--no-password"]);
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
        if (!Directory.Exists(source)) return;
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
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

    // ------------------------------------------------------------------ retención y segundo destino

    private static int ApplyRetention(string dir, BackupSettings settings, List<string> warnings)
    {
        int removed = 0;
        try
        {
            var names = Directory.EnumerateFiles(dir, "*.tcbak").Select(Path.GetFileName).OfType<string>();
            foreach (string name in BackupPaths.ToDelete(names, settings))
            {
                File.Delete(Path.Combine(dir, name));
                removed++;
            }
            // Restos de respaldos interrumpidos (corte de luz a mitad de uno).
            foreach (string part in Directory.EnumerateFiles(dir, "*.part"))
                if (File.GetLastWriteTimeUtc(part) < DateTime.UtcNow.AddDays(-1)) File.Delete(part);
        }
        catch (Exception ex) { warnings.Add($"No se pudo aplicar la retención en {dir}: {ex.Message}"); }
        return removed;
    }

    /// <summary>Copia el respaldo al segundo destino y aplica allí la misma retención. Devuelve el error o null.</summary>
    private static string? CopyToSecondary(string file, BackupJob job, List<string> warnings)
    {
        var settings = job.Settings;
        if (string.IsNullOrWhiteSpace(settings.SecondaryPath)) return null;
        string dir = settings.SecondaryPath.Trim();
        try
        {
            if (SecondaryShare.Connect(dir, settings.SecondaryUser, job.SecondaryPassword) is { } connectError)
                return connectError;
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, Path.GetFileName(file));
            File.Copy(file, target + ".part", overwrite: true);
            if (new FileInfo(target + ".part").Length != new FileInfo(file).Length)
                return $"La copia en {dir} quedó incompleta.";
            File.Move(target + ".part", target, overwrite: true);
            ApplyRetention(dir, settings, warnings);
            return null;
        }
        catch (Exception ex)
        {
            return $"No se pudo copiar al segundo destino ({dir}): {ex.Message}";
        }
    }
}
