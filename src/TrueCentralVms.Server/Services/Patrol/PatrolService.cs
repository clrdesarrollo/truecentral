using Microsoft.EntityFrameworkCore;
using Npgsql;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Drivers.Patrol;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;

namespace TrueCentralVms.Server.Services.Patrol;

/// <summary>Quién entrega una descarga, para la bitácora y la fila de la descarga.</summary>
public sealed record PatrolUploadSource(
    PatrolDownloadOrigin Origin, string Actor, int? AgentId, int? UserId, string? ClientIp, long? CommandId);

/// <summary>
/// Ingesta de descargas y armado de la bitácora. Lo usan la base (agente), la
/// lectura a pedido del navegador y el reproceso al dar de alta tags.
/// Las lecturas crudas se insertan con ON CONFLICT DO NOTHING sobre (bastón,
/// tag, hora): descargar dos veces lo mismo no duplica nada.
/// </summary>
public sealed class PatrolService(
    IServiceScopeFactory scopeFactory,
    ScopedHub hub,
    AuditService audit,
    ILogger<PatrolService> logger)
{
    /// <summary>Evento del hub cuando cambia algo de rondas (estado de bastones, bitácora...).</summary>
    public const string HubMethod = "PatrolChanged";

    /// <summary>Segundos máximos entre la lectura del punto y la del llavero para formar un paso (mediana real: 2 s).</summary>
    public static readonly TimeSpan PairWindow = TimeSpan.FromSeconds(30);

    /// <summary>Memoria del bastón: 12 000 bloques de 5 lecturas.</summary>
    public const int MemoryBlocks = 12000;

    /// <summary>
    /// Registra una descarga: crea el bastón si es nuevo (pendiente), guarda las
    /// lecturas y arma los pasos. Devuelve si la base puede vaciar el bastón:
    /// solo si quedó todo guardado y el bastón está aprobado y habilitado.
    /// </summary>
    public async Task<PatrolUploadResultDto> IngestAsync(
        long serial, IReadOnlyList<string> blocksHex, int declaredBlocks, DateTime? clockSetLocal,
        PatrolUploadSource source, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();

        var reader = await EnsureReaderAsync(db, serial, source, ct);
        var download = new PatrolDownload
        {
            ReaderId = reader.Id,
            AgentId = source.AgentId,
            Origin = source.Origin,
            Actor = source.Actor,
            Blocks = blocksHex.Count,
            ClockSet = clockSetLocal is not null,
        };
        db.PatrolDownloads.Add(download);
        await db.SaveChangesAsync(ct);

        try
        {
            var blocks = blocksHex.Select(Convert.FromHexString).ToList();
            var reads = blocks.SelectMany(PatrolBlockDecoder.Decode).ToList();
            var tags = reads.Select(r => r.TagId).ToArray();
            var times = reads.Select(r => ToUtc(r.ReadAt)).ToArray();

            int inserted = await InsertRawReadsAsync(db, reader.Id, download.Id, tags, times, ct);
            int newPasses = 0;
            if (inserted > 0)
            {
                var from = await db.PatrolRawReads.Where(r => r.DownloadId == download.Id).MinAsync(r => (DateTime?)r.ReadAt, ct);
                if (from is not null) newPasses = await PairAsync(db, reader.Id, from.Value, ct);
            }

            var now = DateTime.UtcNow;
            download.Reads = reads.Count;
            download.NewReads = inserted;
            download.NewPasses = newPasses;
            download.Status = PatrolDownloadStatus.Completed;
            download.FinishedAt = now;
            reader.LastDownloadAt = now;
            reader.LastMemoryBlocks = declaredBlocks;
            reader.LastError = null;
            if (clockSetLocal is not null) reader.LastClockSetAt = ToUtc(clockSetLocal.Value);
            reader.UpdatedAt = now;
            await db.SaveChangesAsync(ct);

            bool eraseAllowed = reader.Status == PatrolReaderStatus.Approved && reader.Enabled;
            string message = $"{reads.Count} lecturas ({inserted} nuevas), {newPasses} pasos nuevos.";
            if (!eraseAllowed)
                message += reader.Status == PatrolReaderStatus.Approved
                    ? " El bastón está deshabilitado: no se vacía."
                    : " El bastón está pendiente de aprobación: no se vacía hasta aprobarlo.";
            if (declaredBlocks >= MemoryBlocks * 8 / 10)
                message += $" Atención: la memoria del bastón estaba al {declaredBlocks * 100 / MemoryBlocks} %.";

            await audit.LogSystemAsync("patrol", "download", "patrol-reader", reader.Id.ToString(), reader.Name,
                $"{OriginText(source.Origin)} de '{reader.Name}' por {source.Actor}: {message}",
                userId: source.UserId, username: source.Actor, clientIp: source.ClientIp,
                origin: source.Origin == PatrolDownloadOrigin.Browser ? "web" : "agent");
            if (clockSetLocal is not null)
                await audit.LogSystemAsync("patrol", "reader-clock-set", "patrol-reader", reader.Id.ToString(), reader.Name,
                    $"'{reader.Name}' puesto en hora ({clockSetLocal:dd-MM-yyyy HH:mm:ss}) por {source.Actor}.",
                    userId: source.UserId, username: source.Actor, clientIp: source.ClientIp);
            await NotifyAsync("downloads", ct);
            return new PatrolUploadResultDto(download.Id, reads.Count, inserted, newPasses, eraseAllowed, message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Descarga {Id} del bastón {Serial} fallida", download.Id, serial);
            download.Status = PatrolDownloadStatus.Failed;
            download.Error = Truncate(ex.Message, 500);
            download.FinishedAt = DateTime.UtcNow;
            reader.LastError = download.Error;
            await db.SaveChangesAsync(CancellationToken.None);
            await audit.LogSystemAsync("patrol", "download-failed", "patrol-reader", reader.Id.ToString(), reader.Name,
                $"No se pudo registrar la descarga de '{reader.Name}': {ex.Message}", success: false,
                userId: source.UserId, username: source.Actor, clientIp: source.ClientIp);
            throw;
        }
    }

    /// <summary>La base informa si vació (o no pudo vaciar) el bastón tras subir la descarga.</summary>
    public async Task ReportEraseAsync(long downloadId, bool erased, string? error, PatrolUploadSource source, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var download = await db.PatrolDownloads.FirstOrDefaultAsync(d => d.Id == downloadId, ct);
        if (download is null) return;
        var reader = await db.PatrolReaders.FirstAsync(r => r.Id == download.ReaderId, ct);
        download.Erased = erased;
        if (erased) reader.LastMemoryBlocks = 0;
        else if (error is not null) reader.LastError = Truncate(error, 500);
        await db.SaveChangesAsync(ct);
        if (erased || error is not null)
            await audit.LogSystemAsync("patrol", erased ? "reader-erased" : "reader-erase-failed",
                "patrol-reader", reader.Id.ToString(), reader.Name,
                erased ? $"'{reader.Name}' vaciado por {source.Actor} tras guardar la descarga {downloadId}."
                       : $"No se pudo vaciar '{reader.Name}': {error}",
                success: erased, userId: source.UserId, username: source.Actor, clientIp: source.ClientIp);
        await NotifyAsync("readers", ct);
    }

    /// <summary>
    /// Arma los pasos de un bastón desde <paramref name="fromUtc"/>: cada lectura
    /// de un punto con el primer llavero leído dentro de <see cref="PairWindow"/>
    /// (antes del siguiente punto). Sin llavero, el paso queda sin guardia en vez
    /// de perderse. También completa el guardia de pasos que lo tenían vacío
    /// (llavero dado de alta después), salvo que alguien los haya corregido a mano.
    /// </summary>
    public async Task<int> PairAsync(VmsDbContext db, int readerId, DateTime fromUtc, CancellationToken ct)
    {
        var start = fromUtc - PairWindow;
        var checkpoints = await db.PatrolCheckpoints.Where(c => c.Kind == PatrolCheckpointKind.Tag && c.TagId != null)
            .Select(c => new { c.Id, c.TagId }).ToDictionaryAsync(c => c.TagId!, c => c.Id, ct);
        var tags = await db.PatrolTags.Include(t => t.PatrolGuard!).ThenInclude(g => g.AccessPerson)
            .ToDictionaryAsync(t => t.TagId, ct);
        var reads = await db.PatrolRawReads.Where(r => r.ReaderId == readerId && r.ReadAt >= start)
            .OrderBy(r => r.ReadAt).ThenBy(r => r.Id).AsNoTracking().ToListAsync(ct);
        var existing = await db.PatrolPasses.Where(p => p.ReaderId == readerId && p.ReadAt >= start && p.CheckpointReadId != null)
            .ToDictionaryAsync(p => p.CheckpointReadId!.Value, ct);
        var usedGuardReads = existing.Values.Where(p => p.GuardReadId != null).Select(p => p.GuardReadId!.Value).ToHashSet();

        int created = 0;
        for (int i = 0; i < reads.Count; i++)
        {
            var read = reads[i];
            if (!checkpoints.TryGetValue(read.TagId, out int checkpointId)) continue;
            existing.TryGetValue(read.Id, out var pass);
            if (pass is not null && (pass.GuardReadId is not null || pass.EditedAt is not null)) continue;

            PatrolRawRead? guardRead = null;
            PatrolTag? tag = null;
            for (int j = i + 1; j < reads.Count && reads[j].ReadAt - read.ReadAt <= PairWindow; j++)
            {
                if (checkpoints.ContainsKey(reads[j].TagId)) break;
                if (tags.TryGetValue(reads[j].TagId, out var t) && !usedGuardReads.Contains(reads[j].Id))
                {
                    guardRead = reads[j];
                    tag = t;
                    break;
                }
            }

            if (pass is null)
            {
                pass = new PatrolPass
                {
                    CheckpointId = checkpointId, ReaderId = readerId, ReadAt = read.ReadAt,
                    CheckpointReadId = read.Id, Source = PatrolPassSource.Reader,
                };
                db.PatrolPasses.Add(pass);
                created++;
            }
            if (guardRead is not null && tag is not null)
            {
                usedGuardReads.Add(guardRead.Id);
                pass.GuardReadId = guardRead.Id;
                pass.PatrolTagId = tag.Id;
                pass.PatrolGuardId = tag.PatrolGuardId;
                pass.GuardName = tag.PatrolGuard?.AccessPerson?.FullName;
                pass.GuardEmployeeNo = tag.PatrolGuard?.AccessPerson?.EmployeeNo;
            }
        }
        await db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>Vuelve a armar los pasos donde aparece un tag recién dado de alta (punto o llavero).</summary>
    public async Task<int> ReprocessTagAsync(VmsDbContext db, string tagId, CancellationToken ct)
    {
        var firstByReader = await db.PatrolRawReads.Where(r => r.TagId == tagId)
            .GroupBy(r => r.ReaderId).Select(g => new { ReaderId = g.Key, From = g.Min(r => r.ReadAt) })
            .ToListAsync(ct);
        int created = 0;
        foreach (var x in firstByReader) created += await PairAsync(db, x.ReaderId, x.From, ct);
        if (firstByReader.Count > 0) await NotifyAsync("passes", ct);
        return created;
    }

    public Task NotifyAsync(string what, CancellationToken ct) =>
        hub.ToPermittedAsync(Permissions.PatrolView, HubMethod, what, ct);

    private async Task<PatrolReader> EnsureReaderAsync(VmsDbContext db, long serial, PatrolUploadSource source, CancellationToken ct)
    {
        var reader = await db.PatrolReaders.FirstOrDefaultAsync(r => r.Serial == serial, ct);
        if (reader is not null)
        {
            if (source.AgentId is not null) reader.AgentId = source.AgentId;
            return reader;
        }
        reader = new PatrolReader { Serial = serial, Name = $"Bastón {serial:D6}", AgentId = source.AgentId };
        db.PatrolReaders.Add(reader);
        await db.SaveChangesAsync(ct);
        await audit.LogSystemAsync("patrol", "reader-discovered", "patrol-reader", reader.Id.ToString(), reader.Name,
            $"Bastón nuevo {serial:D6} detectado por {source.Actor}; queda pendiente de aprobación.",
            userId: source.UserId, username: source.Actor, clientIp: source.ClientIp);
        await NotifyAsync("readers", ct);
        return reader;
    }

    private static async Task<int> InsertRawReadsAsync(
        VmsDbContext db, int readerId, long downloadId, string[] tags, DateTime[] times, CancellationToken ct)
    {
        if (tags.Length == 0) return 0;
        return await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "PatrolRawReads" ("ReaderId", "TagId", "ReadAt", "DownloadId")
            SELECT @reader, t.tag, t.at, @download FROM unnest(@tags, @times) AS t(tag, at)
            ON CONFLICT ("ReaderId", "TagId", "ReadAt") DO NOTHING
            """,
            [
                new NpgsqlParameter("reader", readerId),
                new NpgsqlParameter("download", downloadId),
                new NpgsqlParameter("tags", tags),
                new NpgsqlParameter("times", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = times },
            ], ct);
    }

    /// <summary>
    /// El bastón guarda la hora local del servidor (que es quien lo pone en hora).
    /// Su reloj no sabe de horario de verano: una lectura puede caer en la hora
    /// que se salta al adelantar (en Chile, de 00:00 a 00:59 el día del cambio).
    /// Se toma como ya adelantada, que es lo que marcaría un reloj al día.
    /// </summary>
    public static DateTime ToUtc(DateTime local)
    {
        var tz = TimeZoneInfo.Local;
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(unspecified))
        {
            var rule = tz.GetAdjustmentRules().FirstOrDefault(r => r.DateStart <= unspecified && unspecified <= r.DateEnd);
            unspecified = unspecified + (rule?.DaylightDelta ?? TimeSpan.FromHours(1));
        }
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
    }

    public static DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);

    private static string OriginText(PatrolDownloadOrigin o) => o switch
    {
        PatrolDownloadOrigin.Dock => "Descarga automática",
        PatrolDownloadOrigin.DockOnDemand => "Lectura a pedido en la base",
        _ => "Lectura desde el navegador",
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
