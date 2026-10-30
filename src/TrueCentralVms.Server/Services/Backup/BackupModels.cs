using System.Globalization;
using System.Text.RegularExpressions;
using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services.Backup;

/// <summary>Ajustes de los respaldos (SystemBlobs "backup.settings", JSON). Los secretos van cifrados con la llave de datos.</summary>
public sealed record BackupSettings
{
    public bool Enabled { get; init; } = true;
    /// <summary>Hora local del respaldo diario, "HH:mm".</summary>
    public string DailyTime { get; init; } = "03:30";
    public int KeepDaily { get; init; } = 7;
    public int KeepWeekly { get; init; } = 4;
    public int KeepMonthly { get; init; } = 12;
    public int KeepPreUpdate { get; init; } = 5;
    /// <summary>Fotos de las lecturas de patentes: pueden pesar varios GB, por eso van solo si se piden.</summary>
    public bool IncludeAnpr { get; init; }
    /// <summary>Contraseña de respaldo cifrada (base64). Sin ella, los respaldos se cifran con la llave de este equipo.</summary>
    public string? PasswordCipher { get; init; }
    public DateTime? PasswordSetUtc { get; init; }
    /// <summary>Segundo destino (otra unidad o carpeta de red \\servidor\recurso\carpeta). Vacío = no se copia.</summary>
    public string? SecondaryPath { get; init; }
    public string? SecondaryUser { get; init; }
    public string? SecondaryPasswordCipher { get; init; }

    public TimeOnly DailyTimeOfDay =>
        TimeOnly.TryParseExact(DailyTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : new TimeOnly(3, 30);
}

/// <summary>Resultado de las últimas ejecuciones (SystemBlobs "backup.state").</summary>
public sealed record BackupState
{
    public DateTime? LastSuccessUtc { get; init; }
    public string? LastSuccessFile { get; init; }
    public DateTime? LastAttemptUtc { get; init; }
    public string? LastError { get; init; }
    public double? LastDurationSeconds { get; init; }
    public DateTime? LastSecondaryUtc { get; init; }
    public string? LastSecondaryError { get; init; }
    /// <summary>Día (local, yyyy-MM-dd) del último respaldo programado intentado: uno por día.</summary>
    public string? LastScheduledDay { get; init; }
}

/// <summary>Un respaldo en disco, tal como lo muestra el panel.</summary>
public sealed record BackupFileDto(string Name, string Kind, DateTime CreatedUtc, long Size, string KeyMode,
    string ServerVersion, string? LastMigration, bool IncludesAnpr, bool? OnSecondary);

/// <summary>
/// Un respaldo en curso o recién terminado, como lo sigue el panel. Stage, Bytes y ExpectedBytes según
/// <see cref="BackupProgress"/> ("prepare" mientras arma el trabajo). Los tiempos los mide el servidor (el reloj del
/// navegador puede estar corrido): ElapsedSeconds desde que empezó, StageSeconds en la etapa e IdleSeconds sin avanzar.
/// Success es null mientras corre.
/// </summary>
public sealed record BackupRunDto(string Kind, string? StartedBy, DateTime StartedUtc, bool Running, string Stage, long Bytes,
    long ExpectedBytes, bool HasSecondary, double ElapsedSeconds, double StageSeconds, double IdleSeconds, DateTime? FinishedUtc,
    bool? Success, string? Error, BackupFileDto? File, string? SecondaryError);

/// <summary>
/// GET /api/system/backups/progress. Busy = hay un respaldo o una prueba en curso; Testing = el respaldo que se está
/// probando; StallAfterSeconds = desde cuándo el panel avisa que no avanza; TimeoutMinutes = tope en que se detiene solo.
/// </summary>
public sealed record BackupProgressDto(bool Busy, BackupRunDto? Run, string? Testing, int StallAfterSeconds, int TimeoutMinutes);

/// <summary>
/// Quién pidió un respaldo a pedido: el resultado llega minutos después, sin el request a mano, y va a la bitácora a
/// su nombre.
/// </summary>
public sealed record BackupActor(int UserId, string Username, string? Role, string ClientIp, string Origin)
{
    public Task LogAsync(AuditService audit, string action, string? target, string detail, bool success) =>
        audit.LogSystemAsync("backup", action, targetType: "backup", targetName: target, detail: detail, success: success,
            userId: UserId, username: Username, role: Role, clientIp: ClientIp, origin: Origin);
}

/// <summary>Carpetas que entran en un respaldo: las mismas que usa el servidor (respetando su configuración).</summary>
public static class BackupPaths
{
    public static string Backups(EmbeddedPostgres pg) => Path.Combine(pg.DataRoot, "backups");
    public static string DataKey(EmbeddedPostgres pg) => Path.Combine(pg.DataDirectory, "tcvms-data.key");
    public static string License(EmbeddedPostgres pg) => Path.Combine(pg.DataRoot, "license");

    public static string Workflows(IConfiguration config, EmbeddedPostgres pg) => Configured(config["Workflows:FileDirectory"])
        ?? Path.Combine(pg.DataRoot, "workflows");

    public static string Anpr(IConfiguration config, EmbeddedPostgres pg) => Configured(config["Anpr:ImageDirectory"])
        ?? Path.Combine(pg.DataRoot, "anpr");

    /// <summary>Marca que deja la restauración por consola: el servidor la registra en la bitácora al arrancar.</summary>
    public static string RestoredMarker(EmbeddedPostgres pg) => Path.Combine(pg.DataRoot, "backups", "restaurado.json");

    private static string? Configured(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));

    private static readonly Regex NamePattern = new(
        @"^tcvms-(\d{8}-\d{6})-(diario|manual|antes-de-actualizar)\.tcbak$", RegexOptions.CultureInvariant);

    /// <summary>¿Es un nombre de respaldo válido? (lo que llega por la API nunca es una ruta).</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name);

    public static string NewName(DateTime localNow, string kind) => $"tcvms-{localNow:yyyyMMdd-HHmmss}-{kind}.tcbak";

    /// <summary>Fecha local y tipo según el nombre (sirve para la retención aunque la cabecera no se pueda leer).</summary>
    public static (DateTime Local, string Kind)? Parse(string name)
    {
        var m = NamePattern.Match(name);
        if (!m.Success) return null;
        return DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? (local, m.Groups[2].Value)
            : null;
    }

    /// <summary>Respaldos a pedido que se conservan (van aparte: uno a pedido nunca desplaza a un diario).</summary>
    public const int KeepManual = 5;

    /// <summary>
    /// Retención abuelo-padre-hijo: de los diarios se conserva el más nuevo de cada uno de los últimos
    /// <see cref="BackupSettings.KeepDaily"/> días, de las últimas semanas y de los últimos meses; de los previos a una
    /// actualización, los últimos <see cref="BackupSettings.KeepPreUpdate"/>; de los a pedido, los últimos
    /// <see cref="KeepManual"/>. Nunca se borra el diario más nuevo.
    /// </summary>
    public static IReadOnlyList<string> ToDelete(IEnumerable<string> names, BackupSettings s)
    {
        var parsed = names.Select(n => (Name: n, Info: Parse(n))).Where(x => x.Info is not null)
            .Select(x => (x.Name, Local: x.Info!.Value.Local, Kind: x.Info.Value.Kind))
            .OrderByDescending(x => x.Local).ToList();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in parsed.Where(x => x.Kind == BackupKinds.Manual).Take(KeepManual))
            keep.Add(x.Name);
        var pool = parsed.Where(x => x.Kind == BackupKinds.Daily).ToList();
        if (pool.Count > 0) keep.Add(pool[0].Name);
        void KeepNewestPer<TKey>(Func<DateTime, TKey> group, int count)
        {
            foreach (var g in pool.GroupBy(x => group(x.Local)).Take(Math.Max(0, count)))
                keep.Add(g.First().Name);
        }
        KeepNewestPer(d => d.Date, s.KeepDaily);
        KeepNewestPer(d => (ISOWeek.GetYear(d), ISOWeek.GetWeekOfYear(d)), s.KeepWeekly);
        KeepNewestPer(d => (d.Year, d.Month), s.KeepMonthly);
        foreach (var x in parsed.Where(x => x.Kind == BackupKinds.PreUpdate).Take(Math.Max(1, s.KeepPreUpdate)))
            keep.Add(x.Name);
        return parsed.Where(x => !keep.Contains(x.Name)).Select(x => x.Name).ToList();
    }
}
