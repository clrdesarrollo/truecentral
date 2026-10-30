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
        var meter = new ProgressMeter(progress);
        var file = await CreateAsync(job, ct, meter);
        meter.Begin("retention", 0);
        int removed = ApplyRetention(job.Directory, job.Settings, warnings);
        string? secondaryError = CopyToSecondary(Path.Combine(job.Directory, file.Name), job, warnings, meter);
        return new BackupJobResult(file, removed, secondaryError, warnings);
    }

    private static async Task<BackupFileDto> CreateAsync(BackupJob job, CancellationToken ct, ProgressMeter meter)
    {
        Directory.CreateDirectory(job.Directory);
        string name = BackupPaths.NewName(DateTime.Now, job.Kind);
        string target = Path.Combine(job.Directory, name);
        string part = target + ".part";

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
                    // La base y los archivos van al mismo .tcbak: se cuentan juntos contra el tamaño del respaldo anterior.
                    meter.Begin("dump", job.ExpectedBytes);
                    await AddDumpAsync(job, zip, entries, ct, meter);
                    meter.Continue("files");
                    AddFile(zip, "secrets/tcvms-data.key", job.DataKeyPath, entries, meter);
                    AddDirectory(zip, "files/workflows", job.WorkflowsDirectory, entries, meter, ct);
                    AddDirectory(zip, "files/license", job.LicenseDirectory, entries, meter, ct);
                    if (job.Settings.IncludeAnpr) AddDirectory(zip, "files/anpr", job.AnprDirectory, entries, meter, ct);
                    await using var manifest = zip.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
                    await JsonSerializer.SerializeAsync(manifest, new Manifest(header, entries), Json, ct);
                }
            }
            // Releer todo: cada bloque cifrado se valida con su etiqueta, así que un archivo que se lee entero está sano.
            meter.Begin("verify", new FileInfo(part).Length);
            await using (var check = new MeteredReadStream(File.OpenRead(part), meter))
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
        ProgressMeter meter)
    {
        const string path = "db/truecentral_vms.dump";
        var psi = BackupService.PgToolStart(job.PgDumpPath, job.DumpPassword,
            ["-h", "127.0.0.1", "-p", job.PgPort.ToString(), "-U", job.DumpUser, "-d", job.DatabaseName, "-Fc", "--no-password"]);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo ejecutar pg_dump.");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        long size;
        string sha;
        await using (var target = zip.CreateEntry(path, CompressionLevel.NoCompression).Open())
            (size, sha) = await CopyHashingAsync(process.StandardOutput.BaseStream, target, ct, meter);
        await process.WaitForExitAsync(ct);
        string errors = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"pg_dump falló (código {process.ExitCode}): {errors.Trim()}");
        entries.Add(new ManifestEntry(path, size, sha));
    }

    private static void AddFile(ZipArchive zip, string path, string source, List<ManifestEntry> entries, ProgressMeter meter)
    {
        if (!File.Exists(source)) return;
        string ext = Path.GetExtension(source).ToLowerInvariant();
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = zip.CreateEntry(path, NotCompressed.Contains(ext) ? CompressionLevel.NoCompression : CompressionLevel.Fastest).Open();
        var (size, sha) = CopyHashingAsync(input, target, CancellationToken.None, meter).GetAwaiter().GetResult();
        entries.Add(new ManifestEntry(path, size, sha));
    }

    private static void AddDirectory(ZipArchive zip, string prefix, string source, List<ManifestEntry> entries, ProgressMeter meter,
        CancellationToken ct)
    {
        if (!Directory.Exists(source)) return;
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            try { AddFile(zip, $"{prefix}/{relative}", file, entries, meter); }
            catch (FileNotFoundException) { /* se purgó mientras se respaldaba */ }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static async Task<(long Size, string Sha256)> CopyHashingAsync(Stream input, Stream output, CancellationToken ct,
        ProgressMeter meter)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1 << 16];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);   // el ZIP escribe sincrónico sobre el flujo cifrado
            total += read;
            meter.Add(read);
        }
        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>
    /// Avance de la etapa en curso: suma los bytes y los informa cada 4 MB y al cambiar de etapa. Desde el proceso aparte
    /// cada informe es una línea por la salida estándar: uno por archivo chico (fotos de patentes) sería puro ruido.
    /// </summary>
    private sealed class ProgressMeter(IProgress<BackupProgress>? progress)
    {
        private const long Every = 4 << 20;
        private string _stage = "";
        private long _expected, _bytes, _reported;

        /// <summary>Etapa nueva, contada desde cero contra <paramref name="expected"/> bytes (0 = no se sabe).</summary>
        public void Begin(string stage, long expected)
        {
            _stage = stage;
            _expected = expected;
            _bytes = 0;
            Report();
        }

        /// <summary>Etapa nueva que sigue sumando sobre la anterior (los archivos van al mismo respaldo que la base).</summary>
        public void Continue(string stage)
        {
            _stage = stage;
            Report();
        }

        public void Add(long bytes)
        {
            _bytes += bytes;
            if (_bytes - _reported >= Every) Report();
        }

        private void Report()
        {
            _reported = _bytes;
            progress?.Report(new BackupProgress(_stage, _bytes, _expected));
        }
    }

    /// <summary>Lectura que va contando lo leído: el avance de la relectura completa.</summary>
    private sealed class MeteredReadStream(Stream inner, ProgressMeter meter) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        private int Count(int read)
        {
            meter.Add(read);
            return read;
        }
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
    private static string? CopyToSecondary(string file, BackupJob job, List<string> warnings, ProgressMeter meter)
    {
        var settings = job.Settings;
        if (string.IsNullOrWhiteSpace(settings.SecondaryPath)) return null;
        string dir = settings.SecondaryPath.Trim();
        try
        {
            meter.Begin("secondary", new FileInfo(file).Length);
            if (SecondaryShare.Connect(dir, settings.SecondaryUser, job.SecondaryPassword) is { } connectError)
                return connectError;
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, Path.GetFileName(file));
            // A mano y no con File.Copy: así se ve cuánto lleva (a una carpeta de red, 1,7 GB pueden tardar minutos).
            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            using (var output = new FileStream(target + ".part", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                byte[] buffer = new byte[1 << 20];
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    output.Write(buffer, 0, read);
                    meter.Add(read);
                }
            }
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
