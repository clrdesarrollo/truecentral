using System.Globalization;
using System.Text;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Migrator.HikCentral;
using TrueCentralVms.Migrator.Terminals;
using TrueCentralVms.Migrator.TrueCentral;

namespace TrueCentralVms.Migrator.Migration;

public enum LogLevel { Info, Success, Warning, Error }

public sealed record LogEntry(DateTime At, LogLevel Level, string Message);

/// <summary>Avance global (0..1) y qué se está haciendo, para la barra y el rótulo.</summary>
public sealed record ProgressState(double Fraction, string Status);

public sealed record TerminalTarget(string Name, string Host, int Port, string Username, string Password);

/// <summary>De dónde sale el identificador de empleado con el que TrueCentral va a conocer a la persona.</summary>
public enum EmployeeNoSource
{
    /// <summary>El legajo que HikCentral les bajó a los terminales (si se leyó); si no, el código de persona.</summary>
    Terminal = 0,
    /// <summary>El código de persona de HikCentral (personCode).</summary>
    PersonCode = 1,
    /// <summary>Lo asigna TrueCentral.</summary>
    Assigned = 2,
}

public sealed class ExportOptions
{
    public required HikCentralClient HikCentral { get; init; }
    /// <summary>Departamentos a exportar (orgIndexCode); null = todos.</summary>
    public HashSet<string>? Organizations { get; init; }
    public bool DownloadPhotos { get; init; } = true;
    public IReadOnlyList<TerminalTarget> Terminals { get; init; } = [];
    /// <summary>Traer la foto de rostro que guarda cada terminal y quedarse con la más grande.</summary>
    public bool FacesFromTerminals { get; init; } = true;
    public required string PackageFolder { get; init; }
}

/// <summary>Qué niveles de acceso reciben las personas al importarlas.</summary>
public enum LevelMode
{
    /// <summary>Ninguno: quedan en el padrón pero no bajan a los equipos (modo sombra).</summary>
    None = 0,
    /// <summary>El mismo nivel para todas (<see cref="ImportOptions.LevelId"/>).</summary>
    Single = 1,
    /// <summary>
    /// Uno por puerta, reconstruido de los terminales: cada persona recibe los
    /// de las puertas donde HikCentral la tenía, con el horario que tenía ahí.
    /// </summary>
    PerDoor = 2,
    /// <summary>
    /// Copia los niveles de HikCentral tal cual: mismo nombre, horario, puertas
    /// y personas (OpenAPI <c>privilege/group</c>; los tramos del horario salen
    /// de los terminales). Es lo más fiel y lo más fácil de administrar después.
    /// </summary>
    CopyHcp = 3,
}

public sealed class ImportOptions
{
    public required TrueCentralClient TrueCentral { get; init; }
    public required string PackageFolder { get; init; }
    public EmployeeNoSource EmployeeNoSource { get; init; }
    public LevelMode LevelMode { get; init; }
    /// <summary>Con <see cref="LevelMode.Single"/>: nivel de acceso que reciben las personas al crearse.</summary>
    public int? LevelId { get; init; }
    public string? LevelName { get; init; }
    /// <summary>Importar solo a quienes se encontraron en algún terminal leído (piloto de una puerta).</summary>
    public bool OnlyFoundOnTerminals { get; init; }
    /// <summary>
    /// Si la persona ya existe (mismo identificador), completarla con lo que
    /// le falte —huellas, tarjetas, foto, clave y, con niveles por puerta, los
    /// niveles de las puertas que le falten— en vez de omitirla. No pisa
    /// nombre, vigencia ni lo que ya tenga: eso ya lo administra TrueCentral.
    /// </summary>
    public bool UpdateExisting { get; init; } = true;
}

public sealed class ImportResult
{
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int Photos { get; set; }
    public int Cards { get; set; }
    public int Fingerprints { get; set; }
    public int Pins { get; set; }
    public int Levels { get; set; }
}

/// <summary>
/// Orquesta la migración en fases: (1) padrón de HikCentral, (2) fotos,
/// (3) terminales —legajos, tarjetas y plantillas de huella—, (4) alta en
/// TrueCentral. Cada fase informa avance y escribe en el registro; una
/// persona que falla no frena a las demás.
/// </summary>
public sealed class MigrationEngine(Action<LogEntry> log, IProgress<ProgressState> progress)
{
    // Se ajusta según qué fases corren: el avance global reparte el 100 % entre ellas.
    private (string Name, double Weight, double Done)[] _phases = [];
    private int _phase;

    private void Plan(params (string Name, double Weight)[] phases)
    {
        _phases = phases.Select(p => (p.Name, p.Weight, 0.0)).ToArray();
        _phase = 0;
    }

    private void Report(double fractionInPhase, string status)
    {
        if (_phase < _phases.Length) _phases[_phase].Done = Math.Clamp(fractionInPhase, 0, 1);
        double total = _phases.Sum(p => p.Weight);
        double done = _phases.Sum(p => p.Weight * p.Done);
        string prefix = _phase < _phases.Length ? $"Fase {_phase + 1} de {_phases.Length} · {_phases[_phase].Name}: " : "";
        progress.Report(new ProgressState(total <= 0 ? 0 : done / total, prefix + status));
    }

    private void NextPhase()
    {
        if (_phase < _phases.Length) _phases[_phase].Done = 1;
        _phase++;
    }

    private void Info(string m) => log(new LogEntry(DateTime.Now, LogLevel.Info, m));
    private void Ok(string m) => log(new LogEntry(DateTime.Now, LogLevel.Success, m));
    private void Warn(string m) => log(new LogEntry(DateTime.Now, LogLevel.Warning, m));
    private void Fail(string m) => log(new LogEntry(DateTime.Now, LogLevel.Error, m));

    // ==================================================================
    // Exportación
    // ==================================================================

    public async Task<MigrationPackage> ExportAsync(ExportOptions options, CancellationToken ct)
    {
        var phases = new List<(string, double)> { ("padrón de HikCentral", 10) };
        if (options.DownloadPhotos) phases.Add(("fotos", 25));
        if (options.Terminals.Count > 0) phases.Add(("terminales", 35));
        Plan(phases.ToArray());

        var package = await ReadHikCentralAsync(options, ct);
        NextPhase();

        if (options.DownloadPhotos)
        {
            await DownloadPhotosAsync(options, package, ct);
            NextPhase();
        }

        if (options.Terminals.Count > 0)
        {
            await ReadTerminalsAsync(options, package, ct);
            NextPhase();
        }

        if (package.Levels.Count > 0)
        {
            ResolveLevelSchedules(package);
            CheckLevelsAgainstTerminals(package);
        }

        await package.SaveAsync(options.PackageFolder, ct);
        Ok($"Paquete guardado en {options.PackageFolder} ({package.Persons.Count} personas).");
        progress.Report(new ProgressState(1, "Exportación terminada."));
        return package;
    }

    private async Task<MigrationPackage> ReadHikCentralAsync(ExportOptions options, CancellationToken ct)
    {
        var hcp = options.HikCentral;
        Report(0, "conectando…");
        string version = await hcp.GetVersionAsync(ct);
        Info($"Conectado a {version} en {hcp.BaseUrl}.");

        var orgs = await hcp.GetOrganizationsAsync(ct);
        var orgNames = OrganizationPaths(orgs);
        var orgLeaf = orgs.GroupBy(o => o.IndexCode).ToDictionary(g => g.Key, g => g.First().Name);
        Info($"Departamentos: {orgs.Count}.");
        Report(0.1, "leyendo personas…");

        var persons = await hcp.GetPersonsAsync(new Progress<(int Done, int Total)>(p =>
            Report(0.1 + 0.9 * (p.Total == 0 ? 1 : (double)p.Done / p.Total), $"leyendo personas ({p.Done}/{p.Total})")), ct);
        Info($"Personas en HikCentral: {persons.Count}.");

        if (options.Organizations is { } filter)
        {
            int before = persons.Count;
            persons = persons.Where(p => filter.Contains(p.OrgIndexCode)).ToList();
            Info($"Filtro por departamento: quedan {persons.Count} de {before}.");
        }

        var package = new MigrationPackage
        {
            Source = hcp.BaseUrl,
            SourceVersion = version,
            ExportedAt = DateTime.UtcNow,
            ExportedBy = $"{Environment.UserName}@{Environment.MachineName}",
        };

        int withCards = 0, withFingers = 0, withPhoto = 0;
        foreach (var p in persons)
        {
            var (first, last) = SplitName(p);
            var from = p.BeginTime?.UtcDateTime ?? DateTime.UtcNow;
            var to = p.EndTime?.UtcDateTime ?? from.AddYears(10);
            if (to <= from) to = from.AddYears(10);

            var person = new PackagePerson
            {
                HcpPersonId = p.PersonId,
                PersonCode = p.PersonCode,
                FirstName = first,
                LastName = last,
                Department = orgNames.GetValueOrDefault(p.OrgIndexCode),
                DepartmentName = orgLeaf.GetValueOrDefault(p.OrgIndexCode),
                CustomFields = new Dictionary<string, string>(p.CustomFields, StringComparer.OrdinalIgnoreCase),
                Phone = Blank(p.Phone),
                Email = Blank(p.Email),
                Notes = Blank(p.Remark),
                ValidFrom = from,
                ValidTo = to,
                Cards = p.Cards.Select(c => c.Replace(" ", "")).Where(c => c.Length > 0 && !IsVirtualCard(c)).Distinct().ToList(),
                HcpFingerprintCount = p.Fingerprints.Count,
                PhotoContentType = null,
            };
            // Por si alguna versión sí entrega la plantilla en el listado.
            int slot = 0;
            foreach (var f in p.Fingerprints)
            {
                slot++;
                if (!string.IsNullOrWhiteSpace(f.Data) && IsTemplate(f.Data))
                    person.Fingerprints.Add(new PackageFingerprint { Finger = slot, Template = f.Data.Trim(), Source = "HikCentral" });
            }
            if (person.Cards.Count > 0) withCards++;
            if (person.HcpFingerprintCount > 0) withFingers++;
            if (!string.IsNullOrWhiteSpace(p.PicUri)) withPhoto++;
            package.Persons.Add(person);
            _photoUris[p.PersonId] = p.PicUri;
        }
        Info($"Con tarjeta: {withCards} · con huellas registradas: {withFingers} · con foto: {withPhoto}.");
        await ReadLevelsAsync(hcp, package, ct);
        // Los campos personalizados no tienen todavía dónde ir en TrueCentral: quedan
        // en el paquete para no perderlos (el casino usa «Colacion»).
        var fieldNames = package.Persons.SelectMany(p => p.CustomFields.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (fieldNames.Count > 0)
            Info("Campos personalizados de HikCentral (se guardan en el paquete): " + string.Join(", ", fieldNames.Select(n =>
                $"«{n}» ({package.Persons.Count(p => p.CustomFields.TryGetValue(n, out string? v) && v.Length > 0)} con valor)")) + ".");
        if (withFingers > 0 && package.Persons.Sum(p => p.Fingerprints.Count) == 0)
            Info("HikCentral no entrega las plantillas de huella por OpenAPI: se van a leer desde los terminales.");
        return package;
    }

    // ------------------------------------------------------------------
    // Niveles de acceso de HikCentral
    // ------------------------------------------------------------------

    /// <summary>
    /// Plantillas de fábrica de HikCentral (las trae toda instalación, con el
    /// mismo código): no hace falta leerlas de un terminal. Verificado en HCP 3.1.
    /// </summary>
    private static readonly Dictionary<string, (string Name, int[] Days)> FactorySchedules = new()
    {
        ["1"] = ("Todo el día, todos los días", [0, 1, 2, 3, 4, 5, 6]),
        ["2"] = ("Días hábiles, todo el día", [1, 2, 3, 4, 5]),
        ["3"] = ("Fin de semana, todo el día", [0, 6]),
    };

    /// <summary>
    /// Niveles de acceso con sus puertas y sus personas, por la OpenAPI. Una
    /// versión de HikCentral que no tenga las rutas no frena la exportación: se
    /// avisa y queda el modo «uno por puerta», que no las necesita.
    /// </summary>
    private async Task ReadLevelsAsync(HikCentralClient hcp, MigrationPackage package, CancellationToken ct)
    {
        List<HcpAccessLevel> levels;
        List<HcpDoor> doors;
        List<HcpAccessDevice> devices;
        try
        {
            levels = await hcp.GetAccessLevelsAsync(ct);
            doors = await hcp.GetDoorsAsync(ct);
            devices = await hcp.GetAccessDevicesAsync(ct);
        }
        catch (HikCentralException ex)
        {
            Warn($"No se pudieron leer los niveles de acceso de HikCentral ({ex.Message}). Se podrá importar con «uno por puerta», que los reconstruye desde los terminales.");
            return;
        }
        if (levels.Count == 0) { Info("HikCentral no tiene niveles de acceso."); return; }

        // Número de cada puerta EN SU EQUIPO: HikCentral numera las puertas a su
        // manera (doorNo correlativo de toda la instalación), así que se cuenta su
        // posición entre las puertas del mismo equipo. La verificación contra los
        // terminales, al final, delata un orden equivocado.
        var deviceByCode = devices.GroupBy(d => d.IndexCode).ToDictionary(g => g.Key, g => g.First());
        var doorByCode = new Dictionary<string, (HcpDoor Door, int Position, int Siblings)>();
        foreach (var group in doors.GroupBy(d => d.DeviceIndexCode))
        {
            var ordered = group.OrderBy(d => d.DoorNo).ThenBy(d => long.TryParse(d.IndexCode, out long n) ? n : long.MaxValue).ToList();
            for (int i = 0; i < ordered.Count; i++) doorByCode[ordered[i].IndexCode] = (ordered[i], i + 1, ordered.Count);
        }
        int multiDoorDevices = doors.GroupBy(d => d.DeviceIndexCode).Count(g => g.Count() > 1);
        if (multiDoorDevices > 0)
            Warn($"{multiDoorDevices} equipo(s) tienen varias puertas: su número en el equipo se deduce del orden de HikCentral. Revise la verificación contra los terminales al final de la exportación.");

        var byId = package.Persons.GroupBy(p => p.HcpPersonId).ToDictionary(g => g.Key, g => g.First());
        int outside = 0;
        foreach (var level in levels)
        {
            ct.ThrowIfCancellationRequested();
            var packaged = new PackageLevel
            {
                HcpId = level.Id,
                Name = level.Name.Trim(),
                ScheduleName = level.ScheduleName.Trim(),
                ScheduleHcpId = level.ScheduleId,
                OtherElements = level.OtherElements,
            };
            if (FactorySchedules.TryGetValue(level.ScheduleId, out var factory))
            {
                packaged.Segments = factory.Days.Select(d => new PackageSegment { Day = d, StartMinutes = 0, EndMinutes = 24 * 60 - 1 }).ToList();
                packaged.SegmentsSource = $"plantilla de fábrica de HikCentral ({factory.Name})";
            }
            foreach (var d in level.Doors)
            {
                var door = new PackageLevelDoor { Name = d.Name, HcpIndexCode = d.IndexCode };
                if (doorByCode.TryGetValue(d.IndexCode, out var found))
                {
                    door.DoorNo = found.Position;
                    if (deviceByCode.TryGetValue(found.Door.DeviceIndexCode, out var device))
                    {
                        door.DeviceHost = device.Ip.Trim();
                        door.DeviceName = device.Name.Trim();
                    }
                }
                packaged.Doors.Add(door);
            }
            if (packaged.Doors.Any(d => d.DeviceHost is null))
                Warn($"Nivel «{packaged.Name}»: HikCentral no dijo en qué equipo está la puerta {string.Join(", ", packaged.Doors.Where(d => d.DeviceHost is null).Select(d => $"«{d.Name}»"))}.");
            if (level.OtherElements > 0)
                Warn($"Nivel «{packaged.Name}»: tiene {level.OtherElements} elemento(s) que no son puertas (pisos de ascensor u otros): TrueCentral no los migra.");

            List<string> personIds;
            try { personIds = await hcp.GetAccessLevelPersonIdsAsync(level.Id, ct); }
            catch (HikCentralException ex)
            {
                Warn($"No se pudieron leer las personas de los niveles de acceso ({ex.Message}). Se podrá importar con «uno por puerta».");
                package.Levels.Clear();
                foreach (var p in package.Persons) p.HcpLevelIds.Clear();
                return;
            }
            foreach (string id in personIds.Distinct())
            {
                if (!byId.TryGetValue(id, out var person)) { outside++; continue; }
                if (!person.HcpLevelIds.Contains(level.Id)) person.HcpLevelIds.Add(level.Id);
            }
            package.Levels.Add(packaged);
        }

        int withLevels = package.Persons.Count(p => p.HcpLevelIds.Count > 0);
        Ok($"Niveles de acceso en HikCentral: {package.Levels.Count} · personas con nivel: {withLevels} de {package.Persons.Count}" +
           $" · con dos o más: {package.Persons.Count(p => p.HcpLevelIds.Count > 1)}.");
        foreach (var l in package.Levels)
            Info($"Nivel «{l.Name}» · horario «{l.ScheduleName}» · {l.Doors.Count} puerta(s) · {package.Persons.Count(p => p.HcpLevelIds.Contains(l.HcpId))} persona(s).");
        if (outside > 0) Info($"{outside} asignación(es) de nivel son de personas que quedaron fuera (departamentos no elegidos): se ignoran.");
        if (withLevels < package.Persons.Count)
            Warn($"{package.Persons.Count - withLevels} persona(s) no tienen ningún nivel en HikCentral: entrarán a TrueCentral sin puertas.");
    }

    /// <summary>
    /// Tramos de cada horario. La OpenAPI no los entrega, pero los terminales
    /// sí: para cada nivel se mira qué plantilla tienen sus personas en las
    /// puertas del nivel y se toma la más repetida. Se prefieren las personas
    /// que tienen solo ese nivel, porque con dos niveles en la misma puerta
    /// HikCentral puede haberles escrito una combinación de horarios.
    /// </summary>
    private void ResolveLevelSchedules(MigrationPackage package)
    {
        var templates = new Dictionary<(string Host, int Number), PackageSchedule>();
        foreach (var t in package.Terminals)
            foreach (var s in t.Schedules)
                templates[(t.Host.Trim().ToLowerInvariant(), s.Number)] = s;

        foreach (var level in package.Levels.Where(l => l.Segments is null))
        {
            var members = package.Persons.Where(p => p.HcpLevelIds.Contains(level.HcpId)).ToList();
            var votes = new Dictionary<string, (List<PackageSegment> Segments, string Source, int Count)>();
            foreach (var pass in new[] { members.Where(p => p.HcpLevelIds.Count == 1).ToList(), members })
            {
                foreach (var person in pass)
                    foreach (var door in level.Doors.Where(d => d.DeviceHost is not null))
                        foreach (var access in person.Access.Where(a => a.Host.Equals(door.DeviceHost, StringComparison.OrdinalIgnoreCase) && a.DoorNo == door.DoorNo))
                        {
                            if (!templates.TryGetValue((access.Host.Trim().ToLowerInvariant(), access.TemplateNo), out var schedule)) continue;
                            string key = Normalize(schedule.Segments.Select(s => (s.Day, s.StartMinutes, s.EndMinutes)));
                            var current = votes.TryGetValue(key, out var seen)
                                ? seen
                                : (Segments: schedule.Segments, Source: $"terminal {access.Terminal}, plantilla {schedule.Number} «{schedule.Name}»", Count: 0);
                            votes[key] = (current.Segments, current.Source, current.Count + 1);
                        }
                if (votes.Count > 0) break;   // con las de un solo nivel alcanzó
            }
            if (votes.Count == 0)
            {
                Warn($"Nivel «{level.Name}»: no se pudo saber el horario «{level.ScheduleName}» (ninguna de sus personas apareció en sus puertas de los terminales leídos). " +
                     "Para copiar los niveles de HikCentral hay que leer los terminales de sus puertas.");
                continue;
            }
            var best = votes.Values.OrderByDescending(v => v.Count).First();
            level.Segments = best.Segments.Select(s => new PackageSegment { Day = s.Day, StartMinutes = s.StartMinutes, EndMinutes = s.EndMinutes }).ToList();
            level.SegmentsSource = best.Source;
            Info($"Nivel «{level.Name}»: horario «{level.ScheduleName}» = {DescribeSegments(level.Segments.Select(s => (s.Day, s.StartMinutes, s.EndMinutes)))} (de {best.Source}).");
            if (votes.Count > 1)
                Warn($"Nivel «{level.Name}»: no todas sus personas tienen el mismo horario en los terminales ({votes.Count} distintos); se usó el más repetido ({best.Count} coincidencia(s)).");
        }
    }

    /// <summary>
    /// Compara, por persona, las puertas que le dan sus niveles de HikCentral
    /// con las que tiene en los terminales leídos. Diferencias esperables: un
    /// cambio que HikCentral no alcanzó a aplicar, o un número de puerta mal
    /// deducido en una controladora de varias puertas. Solo informa.
    /// </summary>
    private void CheckLevelsAgainstTerminals(MigrationPackage package)
    {
        var read = package.Terminals.Select(t => t.Host.Trim().ToLowerInvariant()).ToHashSet();
        if (read.Count == 0) return;
        var levels = package.Levels.ToDictionary(l => l.HcpId);
        int checkedPeople = 0;
        var missing = new List<string>();
        var extra = new List<string>();
        foreach (var person in package.Persons)
        {
            var expected = person.HcpLevelIds.Where(levels.ContainsKey).SelectMany(id => levels[id].Doors)
                .Where(d => d.DeviceHost is not null && read.Contains(d.DeviceHost.ToLowerInvariant()))
                .Select(d => (Host: d.DeviceHost!.ToLowerInvariant(), d.DoorNo, Name: d.Name)).Distinct().ToList();
            var actual = person.Access.Where(a => read.Contains(a.Host.Trim().ToLowerInvariant()))
                .Select(a => (Host: a.Host.Trim().ToLowerInvariant(), a.DoorNo, Name: a.Terminal)).Distinct().ToList();
            if (expected.Count == 0 && actual.Count == 0) continue;
            checkedPeople++;
            foreach (var e in expected.Where(e => !actual.Any(a => a.Host == e.Host && a.DoorNo == e.DoorNo)))
                missing.Add($"{person.FullName} → «{e.Name}»");
            foreach (var a in actual.Where(a => !expected.Any(e => e.Host == a.Host && e.DoorNo == a.DoorNo)))
                extra.Add($"{person.FullName} → {a.Name} (puerta {a.DoorNo})");
        }
        if (missing.Count == 0 && extra.Count == 0)
        {
            Ok($"Verificación: los niveles de HikCentral coinciden con lo que tienen los terminales ({checkedPeople} persona(s) revisadas).");
            return;
        }
        if (missing.Count > 0)
            Warn($"{missing.Count} puerta(s) que HikCentral da por nivel NO están en el terminal (¿cambio sin aplicar?): " +
                 string.Join("; ", missing.Take(10)) + (missing.Count > 10 ? "; …" : "") + ". Con «copiar los niveles» esas personas pasarán a tenerlas.");
        if (extra.Count > 0)
            Warn($"{extra.Count} puerta(s) que el terminal da y ningún nivel de HikCentral: " +
                 string.Join("; ", extra.Take(10)) + (extra.Count > 10 ? "; …" : "") + ". Con «copiar los niveles» esas personas las perderán.");
    }

    private readonly Dictionary<string, string> _photoUris = new();

    /// <summary>Personas cuya foto ya se comparó con la de algún terminal.</summary>
    private readonly HashSet<PackagePerson> _faceChecked = [];

    private async Task DownloadPhotosAsync(ExportOptions options, MigrationPackage package, CancellationToken ct)
    {
        var pending = package.Persons.Where(p => !string.IsNullOrWhiteSpace(_photoUris.GetValueOrDefault(p.HcpPersonId))).ToList();
        if (pending.Count == 0) { Info("Ninguna persona tiene foto en HikCentral."); return; }

        string folder = Path.Combine(options.PackageFolder, MigrationPackage.PhotosFolder);
        Directory.CreateDirectory(folder);
        int done = 0, ok = 0;
        foreach (var person in pending)
        {
            ct.ThrowIfCancellationRequested();
            Report((double)done / pending.Count, $"descargando fotos ({done}/{pending.Count})");
            try
            {
                var photo = await options.HikCentral.GetPersonPhotoAsync(person.HcpPersonId, _photoUris[person.HcpPersonId], ct);
                if (photo is null) Warn($"{person.FullName}: HikCentral no entregó la foto.");
                else
                {
                    string ext = photo.Value.ContentType == "image/png" ? ".png" : ".jpg";
                    string file = Path.Combine(MigrationPackage.PhotosFolder, SafeFileName(person.HcpPersonId) + ext);
                    await File.WriteAllBytesAsync(Path.Combine(options.PackageFolder, file), photo.Value.Bytes, ct);
                    person.PhotoFile = file;
                    person.PhotoContentType = photo.Value.ContentType;
                    person.PhotoSource = "HikCentral";
                    ok++;
                }
            }
            catch (HikCentralException ex) { Warn($"{person.FullName}: no se pudo bajar la foto ({ex.Message})."); }
            done++;
        }
        Ok($"Fotos descargadas: {ok} de {pending.Count}.");
    }

    // ------------------------------------------------------------------
    // Terminales
    // ------------------------------------------------------------------

    private async Task ReadTerminalsAsync(ExportOptions options, MigrationPackage package, CancellationToken ct)
    {
        var terminals = options.Terminals;
        int index = 0;
        var byCode = package.Persons.Where(p => p.PersonCode.Length > 0).GroupBy(p => p.PersonCode)
            .ToDictionary(g => g.Key, g => g.First());
        var byId = package.Persons.ToDictionary(p => p.HcpPersonId);
        var byCard = new Dictionary<string, PackagePerson>();
        foreach (var p in package.Persons) foreach (var c in p.Cards) byCard.TryAdd(c, p);
        var byName = package.Persons.GroupBy(p => Fold(p.FullName)).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var target in terminals)
        {
            ct.ThrowIfCancellationRequested();
            double baseFraction = (double)index / terminals.Count;
            double slice = 1.0 / terminals.Count;
            Report(baseFraction, $"{target.Name}: conectando…");
            try
            {
                var reader = new TerminalReader(target.Host, target.Port, target.Username, target.Password);
                var info = await reader.ProbeAsync(ct);
                Info($"[{target.Name}] {info.Model} · serie {info.SerialNumber} · firmware {info.Firmware}.");

                var users = await reader.ReadUsersAsync(new Progress<int>(n =>
                    Report(baseFraction + slice * 0.2, $"{target.Name}: leyendo personas ({n})")), ct);
                var cards = await reader.ReadCardsAsync(ct);
                var cardsByEmployee = cards.GroupBy(c => c.EmployeeNo).ToDictionary(g => g.Key, g => g.Select(c => c.CardNo).ToList());
                bool reportsFingerCount = users.Any(u => u.Fingerprints is not null);
                Info($"[{target.Name}] personas en el equipo: {users.Count} · tarjetas: {cards.Count} · con huella: " +
                     (reportsFingerCount ? $"{users.Count(u => u.Fingerprints > 0)}" : "el firmware no lo informa (se consultan los diez dedos de cada persona)") +
                     $" · con clave: {users.Count(u => u.Pin is not null)} · con rostro: {users.Count(u => u.Faces > 0)}.");
                if (users.Count > 0 && users.All(u => u.Pin is null))
                    Info($"[{target.Name}] el equipo no informó ninguna clave: o nadie usa clave en él, o su firmware no la entrega.");

                var terminal = new PackageTerminal
                {
                    Name = target.Name, Host = target.Host, Model = Blank(info.Model), SerialNumber = Blank(info.SerialNumber),
                    Firmware = Blank(info.Firmware), Users = users.Count, Cards = cards.Count,
                };
                package.Terminals.RemoveAll(t => t.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase));
                package.Terminals.Add(terminal);
                await ReadSchedulesAsync(reader, target, terminal, users, ct);

                int matched = 0, fingers = 0, unmatched = 0, virtualCards = 0, pins = 0, faces = 0, noRights = 0;
                var unmatchedNames = new List<string>();
                for (int i = 0; i < users.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var user = users[i];
                    Report(baseFraction + slice * (0.3 + 0.7 * i / Math.Max(1, users.Count)),
                        $"{target.Name}: cruzando y leyendo huellas ({i + 1}/{users.Count})");

                    var terminalCards = cardsByEmployee.GetValueOrDefault(user.EmployeeNo) ?? [];
                    var (person, how) = Match(user, terminalCards, byCode, byId, byCard, byName);
                    if (person is null)
                    {
                        unmatched++;
                        if (unmatchedNames.Count < 15) unmatchedNames.Add($"{user.Name} ({user.EmployeeNo})");
                        continue;
                    }
                    matched++;

                    if (person.TerminalEmployeeNo is null) person.TerminalEmployeeNo = user.EmployeeNo;
                    else if (person.TerminalEmployeeNo != user.EmployeeNo)
                        Warn($"{person.FullName}: {target.Name} la conoce como '{user.EmployeeNo}' pero otro terminal como '{person.TerminalEmployeeNo}'. Se conserva el primero.");
                    if (!person.FoundOnTerminals.Contains(target.Name)) person.FoundOnTerminals.Add(target.Name);
                    if (how != "legajo") Info($"{person.FullName}: identificada en {target.Name} por {how} (legajo {user.EmployeeNo}).");

                    // Clave: HikCentral escribe la misma en todos sus equipos; si dos
                    // terminales discrepan, vale la primera y se avisa.
                    if (user.Pin is { } pin)
                    {
                        if (person.Pin is null) { person.Pin = pin; pins++; }
                        else if (person.Pin != pin)
                            Warn($"{person.FullName}: {target.Name} tiene otra clave que el terminal anterior. Se conserva la primera; revise cuál usa.");
                    }

                    // Puertas y horario: de acá salen sus niveles de acceso en TrueCentral.
                    if (user.Rights.Count == 0) noRights++;
                    foreach (var right in user.Rights)
                        if (!person.Access.Any(a => a.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase) && a.DoorNo == right.DoorNo && a.TemplateNo == right.PlanTemplateNo))
                            person.Access.Add(new PackageAccess { Terminal = target.Name, Host = target.Host, DoorNo = right.DoorNo, TemplateNo = right.PlanTemplateNo });

                    // Lo que TrueCentral no conserva al reescribir a la persona: se informa
                    // para volver a aplicarlo (el kiosco del casino lo hace en sus equipos).
                    if (!user.Enabled && !person.DisabledOn.Contains(target.Name)) person.DisabledOn.Add(target.Name);
                    if (user.VerifyMode is { } mode && mode.Equals("card", StringComparison.OrdinalIgnoreCase) && !person.CardOnlyOn.Contains(target.Name))
                        person.CardOnlyOn.Add(target.Name);
                    if (user.LocalAdmin && !person.LocalAdminOn.Contains(target.Name)) person.LocalAdminOn.Add(target.Name);
                    if (user.UserType.Length > 0 && !user.UserType.Equals("normal", StringComparison.OrdinalIgnoreCase))
                        person.UserTypeOn[target.Name] = user.UserType;

                    if (options.FacesFromTerminals && user.Faces > 0 && await TakeTerminalFaceAsync(reader, target, options.PackageFolder, user, person, ct))
                        faces++;

                    foreach (string card in terminalCards)
                    {
                        if (IsVirtualCard(card)) { virtualCards++; continue; }
                        if (!person.Cards.Contains(card))
                        {
                            person.Cards.Add(card);
                            byCard.TryAdd(card, person);
                            Info($"{person.FullName}: tarjeta {card} estaba en {target.Name} y no en HikCentral; se agrega.");
                        }
                    }

                    // Sin conteo (firmware viejo) se pregunta igual: los dedos que no
                    // existan contestan "NoFP" y no cuestan más que una consulta.
                    if (user.Fingerprints is null or > 0 && person.Fingerprints.Count < (user.Fingerprints ?? AccessFingers.Count))
                    {
                        List<TerminalFingerprint>? read;
                        try { read = await reader.ReadFingerprintsAsync(user.EmployeeNo, user.Fingerprints, ct); }
                        catch (DriverException ex) { Warn($"{person.FullName}: {target.Name} no entregó las huellas ({ex.Message})."); continue; }
                        if (read is null)
                        {
                            Warn($"[{target.Name}] el firmware no permite leer plantillas de huella (FingerPrintUpload). Se omite el resto del equipo.");
                            break;
                        }
                        foreach (var f in read)
                        {
                            if (!AccessFingers.IsValid(f.Finger)) { Warn($"{person.FullName}: dedo {f.Finger} fuera de rango; se ignora."); continue; }
                            if (!IsTemplate(f.Template)) { Warn($"{person.FullName}: la plantilla del dedo {f.Finger} no tiene un tamaño válido; se ignora."); continue; }
                            if (person.Fingerprints.Any(x => x.Finger == f.Finger)) continue;
                            person.Fingerprints.Add(new PackageFingerprint { Finger = f.Finger, Template = f.Template, Source = $"HikCentral · {target.Name}" });
                            fingers++;
                        }
                        if (read.Count == 0 && user.Fingerprints is > 0)
                            Warn($"{person.FullName}: {target.Name} dice que tiene {user.Fingerprints} huella(s) pero no entregó ninguna.");
                    }
                }
                Ok($"[{target.Name}] personas cruzadas: {matched} de {users.Count} · huellas recuperadas: {fingers} · claves: {pins}" +
                   (options.FacesFromTerminals ? $" · rostros tomados del equipo: {faces}" : "") + ".");
                if (noRights > 0)
                    Info($"[{target.Name}] {noRights} persona(s) están en el equipo sin ninguna puerta habilitada: en TrueCentral no recibirán el nivel de esta puerta.");
                if (virtualCards > 0)
                    Info($"[{target.Name}] se ignoraron {virtualCards} tarjeta(s) virtuales: HikCentral las inventa (números cercanos a 18446744073709551615) para vincular huellas de personas sin tarjeta; no abren ninguna puerta.");
                if (unmatched > 0)
                    Warn($"[{target.Name}] {unmatched} persona(s) del equipo no están en HikCentral (o en los departamentos elegidos): " +
                         string.Join(", ", unmatchedNames) + (unmatched > unmatchedNames.Count ? ", …" : "") + ".");
            }
            catch (DriverException ex)
            {
                Fail($"[{target.Name}] {ex.Message}");
            }
            index++;
        }

        int total = package.Persons.Sum(p => p.Fingerprints.Count);
        int missing = package.Persons.Count(p => p.HcpFingerprintCount > 0 && p.Fingerprints.Count == 0);
        Ok($"Huellas en el paquete: {total}. Personas con huella en HikCentral que quedaron sin plantilla: {missing}.");
        int noId = package.Persons.Count(p => p.TerminalEmployeeNo is null);
        if (noId > 0) Info($"{noId} persona(s) no se encontraron en ningún terminal: usarán su código de persona como identificador.");
        Ok($"Claves en el paquete: {package.Persons.Count(p => p.Pin is not null)} · personas con puertas: {package.Persons.Count(p => p.Access.Count > 0)} · " +
           $"puertas distintas: {package.Persons.SelectMany(p => p.Access).Select(a => (a.Host, a.DoorNo)).Distinct().Count()}.");
        await WriteFollowUpAsync(options.PackageFolder, package, ct);
    }

    /// <summary>
    /// Lo que TrueCentral NO conserva al escribir a la persona en sus equipos
    /// (siempre la deja habilitada, con verificación según el lector y sin
    /// acceso al menú): se avisa en el registro y se deja la lista en
    /// <c>pendientes-despues-del-corte.csv</c> para volver a aplicarlo.
    /// </summary>
    private async Task WriteFollowUpAsync(string folder, MigrationPackage package, CancellationToken ct)
    {
        var rows = new List<string>();
        foreach (var p in package.Persons)
        {
            string id = p.TerminalEmployeeNo ?? p.PersonCode;
            rows.AddRange(p.DisabledOn.Select(t => $"{id};{p.FullName};{t};deshabilitada en el equipo"));
            rows.AddRange(p.CardOnlyOn.Select(t => $"{id};{p.FullName};{t};solo tarjeta"));
            rows.AddRange(p.LocalAdminOn.Select(t => $"{id};{p.FullName};{t};administrador del menú del equipo"));
            rows.AddRange(p.UserTypeOn.Select(t => $"{id};{p.FullName};{t.Key};tipo de usuario «{t.Value}»"));
        }
        int disabled = package.Persons.Count(p => p.DisabledOn.Count > 0);
        int cardOnly = package.Persons.Count(p => p.CardOnlyOn.Count > 0);
        int admins = package.Persons.Count(p => p.LocalAdminOn.Count > 0);
        int typed = package.Persons.Count(p => p.UserTypeOn.Count > 0);
        string file = Path.Combine(folder, "pendientes-despues-del-corte.csv");
        if (rows.Count == 0)
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }
        await File.WriteAllTextAsync(file, "legajo;nombre;terminal;estado" + Environment.NewLine + string.Join(Environment.NewLine, rows) + Environment.NewLine,
            new UTF8Encoding(true), ct);
        if (disabled > 0)
            Warn($"{disabled} persona(s) están DESHABILITADAS en algún terminal (lo hace el kiosco del casino). TrueCentral las va a dejar habilitadas al escribirlas: vuelva a deshabilitarlas desde el kiosco después del corte.");
        if (cardOnly > 0)
            Warn($"{cardOnly} persona(s) tienen «solo tarjeta» en algún terminal (visitas del casino): vuelva a aplicarlo desde el kiosco después del corte.");
        if (admins > 0)
            Warn($"{admins} persona(s) pueden entrar al menú de algún terminal: TrueCentral les quita ese permiso.");
        if (typed > 0)
            Warn($"{typed} persona(s) tienen en algún terminal un tipo de usuario distinto de «normal» (visita, lista negra…): TrueCentral las escribe como «normal». " +
                 "Si otro sistema usa ese tipo (el kiosco del casino distingue así a las visitas), vuelva a aplicarlo después del corte.");
        Info($"La lista quedó en {file}.");
    }

    /// <summary>
    /// Lee las plantillas de horario que usan las personas del terminal. Una
    /// plantilla que no se pudo leer queda fuera del paquete: al importar con
    /// niveles por puerta eso detiene la importación, porque reemplazarla por
    /// «todo el día» daría más acceso del que había.
    /// </summary>
    private async Task ReadSchedulesAsync(TerminalReader reader, TerminalTarget target, PackageTerminal terminal,
        List<TerminalUser> users, CancellationToken ct)
    {
        foreach (int number in users.SelectMany(u => u.Rights).Select(r => r.PlanTemplateNo).Distinct().Order())
        {
            TerminalSchedule? schedule;
            try { schedule = await reader.ReadScheduleAsync(number, ct); }
            catch (DriverException ex) { Warn($"[{target.Name}] no se pudo leer la plantilla de horario {number}: {ex.Message}"); continue; }
            catch (System.Text.Json.JsonException) { Warn($"[{target.Name}] la plantilla de horario {number} vino en un formato inesperado."); continue; }
            if (schedule is null) { Warn($"[{target.Name}] el equipo no entregó la plantilla de horario {number}."); continue; }
            terminal.Schedules.Add(new PackageSchedule
            {
                Number = schedule.Number,
                Name = schedule.Name,
                HolidayGroup = schedule.HolidayGroup,
                Segments = schedule.Segments.Select(s => new PackageSegment { Day = s.Day, StartMinutes = s.StartMinutes, EndMinutes = s.EndMinutes }).ToList(),
            });
            int people = users.Count(u => u.Rights.Any(r => r.PlanTemplateNo == number));
            Info($"[{target.Name}] horario {number} «{schedule.Name}»: {DescribeSegments(schedule.Segments.Select(s => (s.Day, s.StartMinutes, s.EndMinutes)))} ({people} persona(s)).");
            if (schedule.HolidayGroup is not null)
                Warn($"[{target.Name}] el horario «{schedule.Name}» tiene feriados en HikCentral (grupo {schedule.HolidayGroup}): cargue los feriados en TrueCentral y revise el horario después de importar.");
        }
    }

    /// <summary>
    /// Toma la foto del rostro que guarda el terminal si es más grande que la
    /// que ya hay en el paquete (la de HikCentral es una miniatura). Devuelve
    /// true si la reemplazó.
    /// </summary>
    private async Task<bool> TakeTerminalFaceAsync(TerminalReader reader, TerminalTarget target, string folder,
        TerminalUser user, PackagePerson person, CancellationToken ct)
    {
        int? current = person.PhotoFile is { } existing && File.Exists(Path.Combine(folder, existing))
            ? ShortSide(await File.ReadAllBytesAsync(Path.Combine(folder, existing), ct))
            : null;
        // Ya se comparó con otro terminal y la foto alcanza para modelar: no se
        // le pregunta a cada equipo (son 12 consultas por persona).
        if (!_faceChecked.Add(person) && current >= MinAcceptedSide) return false;

        var face = await reader.ReadFaceAsync(user.EmployeeNo, ct);
        if (face is null || ShortSide(face.Value.Bytes) is not { } side || side <= (current ?? 0)) return false;
        if (face.Value.Bytes.Length > AccessFacePhoto.MaxBytes) return false;

        Directory.CreateDirectory(Path.Combine(folder, MigrationPackage.PhotosFolder));
        string ext = face.Value.ContentType == "image/png" ? ".png" : ".jpg";
        string file = Path.Combine(MigrationPackage.PhotosFolder, SafeFileName(person.HcpPersonId) + "-terminal" + ext);
        await File.WriteAllBytesAsync(Path.Combine(folder, file), face.Value.Bytes, ct);
        person.PhotoFile = file;
        person.PhotoContentType = face.Value.ContentType;
        person.PhotoSource = $"terminal {target.Name}";
        return true;
    }

    /// <summary>Lado corto de la imagen en píxeles, o null si no se puede leer.</summary>
    private static int? ShortSide(byte[] bytes)
    {
        try
        {
            using var input = new MemoryStream(bytes);
            using var image = System.Drawing.Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: false);
            return Math.Min(image.Width, image.Height);
        }
        catch (Exception) { return null; }
    }

    private static readonly string[] DayNames = ["dom", "lun", "mar", "mié", "jue", "vie", "sáb"];

    /// <summary>"todos los días 00:00-23:59" o "lun 08:00-18:00; mar 08:00-18:00…", para el registro.</summary>
    private static string DescribeSegments(IEnumerable<(int Day, int Start, int End)> segments)
    {
        var list = segments.ToList();
        if (list.Count == 0) return "sin tramos (no deja pasar)";
        string Hhmm(int m) => $"{m / 60:00}:{m % 60:00}";
        var byDay = list.GroupBy(s => s.Day).ToDictionary(g => g.Key, g => string.Join(", ", g.OrderBy(s => s.Start).Select(s => $"{Hhmm(s.Start)}-{Hhmm(s.End)}")));
        if (byDay.Count == 7 && byDay.Values.Distinct().Count() == 1) return $"todos los días {byDay[0]}";
        return string.Join("; ", byDay.OrderBy(d => d.Key).Select(d => $"{DayNames[d.Key]} {d.Value}"));
    }

    private static (PackagePerson? Person, string How) Match(TerminalUser user, List<string> terminalCards,
        Dictionary<string, PackagePerson> byCode, Dictionary<string, PackagePerson> byId,
        Dictionary<string, PackagePerson> byCard, Dictionary<string, PackagePerson> byName)
    {
        if (byCode.TryGetValue(user.EmployeeNo, out var p) || byId.TryGetValue(user.EmployeeNo, out p)) return (p, "legajo");
        foreach (string card in terminalCards)
            if (byCard.TryGetValue(card, out p)) return (p, $"la tarjeta {card}");
        if (user.Name.Length > 0 && byName.TryGetValue(Fold(user.Name), out p)) return (p, "el nombre");
        return (null, "");
    }

    // ==================================================================
    // Importación
    // ==================================================================

    public async Task<ImportResult> ImportAsync(MigrationPackage package, ImportOptions options, CancellationToken ct)
    {
        Plan(("alta en TrueCentral", 1));
        var result = new ImportResult();
        var tc = options.TrueCentral;

        Report(0, "leyendo el padrón actual…");
        var existing = await tc.GetPersonsAsync(ct);
        var usedIds = new HashSet<string>(existing.Select(e => e.EmployeeNo), StringComparer.OrdinalIgnoreCase);
        var usedCards = new Dictionary<string, string>();
        foreach (var e in existing) foreach (var c in e.Cards) usedCards.TryAdd(c, e.FullName);
        Info($"TrueCentral ({tc.BaseUrl}) ya tiene {existing.Count} persona(s) en el padrón.");
        Info("Identificador de empleado: " + options.EmployeeNoSource switch
        {
            EmployeeNoSource.Terminal => "el legajo que usan los terminales (o el código de persona si no se encontró).",
            EmployeeNoSource.PersonCode => "el código de persona de HikCentral.",
            _ => "lo asigna TrueCentral.",
        });
        var mode = options.LevelMode == LevelMode.Single && options.LevelId is null ? LevelMode.None : options.LevelMode;
        Info(mode switch
        {
            LevelMode.PerDoor => "Niveles de acceso: uno por puerta, con el horario que cada persona tenía en cada terminal.",
            LevelMode.CopyHcp => "Niveles de acceso: los mismos de HikCentral (nombre, horario, puertas y personas).",
            LevelMode.Single => $"Nivel de acceso asignado al crear: {options.LevelName ?? options.LevelId.ToString()} (el sincronizador las baja a sus equipos).",
            _ => "Nivel de acceso: ninguno (las personas quedan en el padrón y NO bajan a los equipos hasta que se les asigne uno).",
        });

        var persons = options.OnlyFoundOnTerminals ? package.Persons.Where(p => p.FoundOnTerminals.Count > 0).ToList() : package.Persons;
        if (persons.Count < package.Persons.Count)
            Info($"Solo las personas encontradas en los terminales leídos: {persons.Count} de {package.Persons.Count}.");

        Dictionary<(string Host, int Door, int Template), int> doorLevels = [];
        Dictionary<string, int> hcpLevels = [];
        if (mode == LevelMode.PerDoor)
        {
            Report(0, "preparando horarios y niveles…");
            doorLevels = await PrepareDoorLevelsAsync(tc, package, persons, result, ct);
        }
        else if (mode == LevelMode.CopyHcp)
        {
            Report(0, "copiando los niveles de HikCentral…");
            hcpLevels = await PrepareHcpLevelsAsync(tc, package, persons, result, ct);
        }
        List<int> LevelsOf(PackagePerson p) => mode switch
        {
            // PrepareDoorLevelsAsync deja resuelta cada puerta del paquete (o se detiene antes).
            LevelMode.PerDoor => p.Access.Select(a => doorLevels[AccessKey(a)]).Distinct().ToList(),
            LevelMode.CopyHcp => p.HcpLevelIds.Where(hcpLevels.ContainsKey).Select(id => hcpLevels[id]).Distinct().ToList(),
            LevelMode.Single => [options.LevelId!.Value],
            _ => [],
        };

        int upscaled = 0;
        for (int i = 0; i < persons.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var person = persons[i];
            Report((double)i / persons.Count, $"{person.FullName} ({i + 1}/{persons.Count})");
            var levelIds = LevelsOf(person);
            string? pinCode = ValidPin(person);

            string? employeeNo = options.EmployeeNoSource switch
            {
                EmployeeNoSource.Terminal => Blank(person.TerminalEmployeeNo) ?? Blank(person.PersonCode),
                EmployeeNoSource.PersonCode => Blank(person.PersonCode),
                _ => null,
            };
            if (employeeNo is { Length: > 32 })
            {
                Warn($"{person.FullName}: el identificador '{employeeNo}' supera 32 caracteres; TrueCentral asignará uno.");
                employeeNo = null;
            }
            if (employeeNo is not null && usedIds.Contains(employeeNo))
            {
                var who = existing.FirstOrDefault(e => e.EmployeeNo.Equals(employeeNo, StringComparison.OrdinalIgnoreCase));
                if (options.UpdateExisting && who is not null)
                {
                    try
                    {
                        if (await CompleteExistingAsync(tc, who, person, options, usedCards, result,
                                mode is LevelMode.PerDoor or LevelMode.CopyHcp ? levelIds : [], pinCode, ct)) result.Updated++;
                        else result.Skipped++;
                    }
                    catch (TrueCentralException ex)
                    {
                        result.Failed++;
                        Fail($"{person.FullName}: no se pudo completar ({ex.Message})");
                    }
                    continue;
                }
                Warn($"{person.FullName}: ya existe en TrueCentral con el identificador {employeeNo}" +
                     (who is null ? "" : $" ({who.FullName})") + "; se omite.");
                result.Skipped++;
                continue;
            }

            var cards = new List<string>();
            foreach (string card in person.Cards.Distinct())
            {
                if (card.Length > 32) { Warn($"{person.FullName}: la tarjeta {card} supera 32 caracteres; se omite."); continue; }
                if (usedCards.TryGetValue(card, out string? owner))
                {
                    Warn($"{person.FullName}: la tarjeta {card} ya es de {owner} en TrueCentral; se omite esa tarjeta.");
                    continue;
                }
                cards.Add(card);
            }

            AccessFaceWriteDto? face = null;
            if (person.PhotoFile is not null)
            {
                string path = Path.Combine(options.PackageFolder, person.PhotoFile);
                if (!File.Exists(path)) Warn($"{person.FullName}: falta el archivo de la foto ({person.PhotoFile}).");
                else
                {
                    byte[] bytes = await File.ReadAllBytesAsync(path, ct);
                    string contentType = person.PhotoContentType ?? "image/jpeg";
                    if (UpscaleIfSmall(bytes) is { } bigger) { bytes = bigger; contentType = "image/jpeg"; upscaled++; }
                    if (bytes.Length > AccessFacePhoto.MaxBytes)
                        Warn($"{person.FullName}: la foto pesa {bytes.Length / 1024} KB (máximo {AccessFacePhoto.MaxBytes / 1024} KB); se omite.");
                    else face = new AccessFaceWriteDto(Convert.ToBase64String(bytes), contentType, person.PhotoSource ?? "HikCentral");
                }
            }

            var fingers = person.Fingerprints
                .Where(f => AccessFingers.IsValid(f.Finger) && IsTemplate(f.Template))
                .GroupBy(f => f.Finger).Select(g => g.First())
                .Select(f => new AccessFingerprintWriteDto(f.Finger, f.Template, null, f.Source))
                .ToList();

            var dto = new AccessPersonWriteDto(
                FirstName: Clip(person.FirstName.Length > 0 ? person.FirstName : person.LastName, 64),
                LastName: Clip(person.LastName.Length > 0 ? person.LastName : person.FirstName, 64),
                Department: ClipOrNull(person.Department, 64),
                Position: null,
                Email: ClipOrNull(person.Email, 128),
                Phone: ClipOrNull(person.Phone, 32),
                Notes: ClipOrNull(person.Notes, 256),
                ValidFrom: person.ValidFrom,
                ValidTo: person.ValidTo > person.ValidFrom ? person.ValidTo : person.ValidFrom.AddYears(10),
                Enabled: true,
                PinCode: pinCode,
                ClearPin: false,
                Cards: cards,
                LevelIds: levelIds,
                EmployeeNo: employeeNo,
                Fingerprints: fingers.Count > 0 ? fingers : null,
                Face: face);

            try
            {
                var created = await tc.CreatePersonAsync(dto, ct);
                usedIds.Add(created.EmployeeNo);
                foreach (string c in cards) usedCards[c] = created.FullName;
                result.Created++;
                result.Cards += cards.Count;
                result.Fingerprints += fingers.Count;
                if (face is not null) result.Photos++;
                if (pinCode is not null) result.Pins++;
                Ok($"{created.FullName} → identificador {created.EmployeeNo}: {cards.Count} tarjeta(s), {fingers.Count} huella(s), " +
                   $"{(face is null ? "sin foto" : "con foto")}, {(pinCode is null ? "sin clave" : "con clave")}" +
                   (mode == LevelMode.PerDoor ? $", {levelIds.Count} puerta(s)" : "") +
                   (mode == LevelMode.CopyHcp ? $", {levelIds.Count} nivel(es)" : "") + ".");
            }
            catch (TrueCentralException ex)
            {
                result.Failed++;
                Fail($"{person.FullName}: {ex.Message}");
            }
        }

        Report(1, "listo");
        if (upscaled > 0)
            Info($"{upscaled} foto(s) venían chicas (HikCentral entrega miniaturas de ~135 px) y se ampliaron a {UpscaleShortSide} px de lado corto: es lo mínimo que piden los terminales para modelar el rostro.");
        Ok($"Importación terminada: {result.Created} creada(s), {result.Updated} completada(s), {result.Skipped} omitida(s) o sin cambios, {result.Failed} con error. " +
           $"Fotos: {result.Photos} · tarjetas: {result.Cards} · huellas: {result.Fingerprints} · claves: {result.Pins}" +
           (mode is LevelMode.PerDoor or LevelMode.CopyHcp ? $" · niveles creados: {result.Levels}" : "") + ".");
        if (result.Created > 0 && mode == LevelMode.None)
            Info("Las personas entraron sin nivel de acceso: asígnelos en TrueCentral (Control de acceso → Niveles) para que el sincronizador las baje a los equipos.");
        int withoutPin = persons.Count(p => !string.IsNullOrWhiteSpace(p.Pin) && !IsValidPin(p.Pin));
        if (withoutPin > 0)
            Warn($"{withoutPin} clave(s) no se migraron porque TrueCentral acepta claves de 4 a 8 dígitos: hay que definírselas a mano (están en el registro de arriba).");
        if (result.Created + result.Updated > 0)
            Info("El estado de la bajada a cada equipo se ve en TrueCentral → Control de acceso → Personas.");
        return result;
    }

    /// <summary>La clave del paquete si TrueCentral la acepta (4 a 8 dígitos); si no, null y se avisa.</summary>
    private string? ValidPin(PackagePerson person)
    {
        string? pin = person.Pin?.Trim();
        if (string.IsNullOrEmpty(pin)) return null;
        if (IsValidPin(pin)) return pin;
        Warn($"{person.FullName}: su clave en los terminales tiene {pin.Length} caracteres y TrueCentral acepta de 4 a 8 dígitos; no se migró " +
             "y, al escribirla, TrueCentral le BORRA la clave en sus equipos: defínale una nueva antes del corte.");
        return null;
    }

    private static bool IsValidPin(string pin) => pin.Trim() is { Length: >= 4 and <= 8 } p && p.All(char.IsAsciiDigit);

    private static (string Host, int Door, int Template) AccessKey(PackageAccess a) =>
        (a.Host.Trim().ToLowerInvariant(), a.DoorNo, a.TemplateNo);

    /// <summary>
    /// Arma en TrueCentral lo necesario para replicar quién entraba por dónde:
    /// por cada plantilla de horario distinta, un horario (el 24/7 de fábrica
    /// si es «todo el día», o uno igual que ya exista); y por cada puerta y
    /// horario, un nivel «Acceso · puerta». Antes de crear nada verifica que
    /// todos los terminales estén dados de alta en TrueCentral y que todas las
    /// plantillas se hayan leído: con una puerta o un horario de menos alguien
    /// perdería acceso, y reemplazar un horario por «todo el día» le daría más.
    /// Es idempotente: una segunda pasada reutiliza lo que creó la primera.
    /// </summary>
    private async Task<Dictionary<(string Host, int Door, int Template), int>> PrepareDoorLevelsAsync(
        TrueCentralClient tc, MigrationPackage package, IReadOnlyList<PackagePerson> persons, ImportResult result, CancellationToken ct)
    {
        var needed = persons.SelectMany(p => p.Access).GroupBy(AccessKey).Select(g => g.First()).ToList();
        if (needed.Count == 0)
        {
            Warn("El paquete no trae puertas por persona (¿se exportó sin leer los terminales, o con una versión anterior del migrador?): nadie recibirá niveles.");
            return [];
        }

        // 1. Terminales y puertas en TrueCentral, por IP.
        var devices = await tc.GetDevicesAsync(ct);
        var byHost = devices.Where(d => d.Host.Length > 0).GroupBy(d => d.Host.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var problems = new List<string>();
        foreach (var terminal in needed.GroupBy(a => a.Host.Trim().ToLowerInvariant()))
        {
            string name = terminal.First().Terminal;
            if (!byHost.TryGetValue(terminal.Key, out var device))
            {
                problems.Add($"el terminal «{name}» ({terminal.First().Host}) no está dado de alta en TrueCentral");
                continue;
            }
            foreach (int door in terminal.Select(a => a.DoorNo).Distinct())
                if (device.Doors.All(d => d.Number != door))
                    problems.Add($"«{device.Name}» no tiene la puerta {door} en TrueCentral");
        }

        // 2. Plantillas de horario leídas de cada terminal.
        var templates = new Dictionary<(string Host, int Number), PackageSchedule>();
        foreach (var t in package.Terminals)
            foreach (var s in t.Schedules)
                templates[(t.Host.Trim().ToLowerInvariant(), s.Number)] = s;
        foreach (var missing in needed.Where(a => !templates.ContainsKey((a.Host.Trim().ToLowerInvariant(), a.TemplateNo)))
                     .GroupBy(a => (a.Host, a.TemplateNo)))
        {
            // Sin la plantilla 1 leída, se sabe igual qué es: la de fábrica.
            if (missing.Key.TemplateNo == 1) continue;
            problems.Add($"no se leyó el horario {missing.Key.TemplateNo} de «{missing.First().Terminal}»");
        }
        if (problems.Count > 0)
            throw new TrueCentralException("No se importó nada para no dejar a nadie con más o menos acceso del que tenía: " +
                                           string.Join("; ", problems) + ". Corríjalo y vuelva a importar el paquete.");

        // 3. Horarios: el de fábrica para «todo el día», uno igual que ya exista, o uno nuevo.
        var schedules = await tc.GetSchedulesAsync(ct);
        var scheduleOf = new Dictionary<(string Host, int Number), AccessScheduleDto>();
        foreach (var key in needed.Select(a => (Host: a.Host.Trim().ToLowerInvariant(), a.TemplateNo)).Distinct())
        {
            var segments = templates.TryGetValue(key, out var template)
                ? Normalize(template.Segments.Select(s => (s.Day, s.StartMinutes, s.EndMinutes)))
                : Normalize(Enumerable.Range(0, 7).Select(d => (d, 0, 24 * 60 - 1)));
            var match = schedules
                .Where(s => s.HolidayMode == AccessHolidayMode.Normal || s.IsBuiltIn)
                .OrderByDescending(s => s.IsBuiltIn)
                .FirstOrDefault(s => Normalize(s.Segments.Select(x => (x.Day, x.StartMinutes, x.EndMinutes))) == segments);
            if (match is null)
            {
                if (template is null)
                    throw new TrueCentralException("TrueCentral no tiene su horario de fábrica «Todo el día, todos los días»: abra Control de acceso → Horarios una vez y vuelva a importar.");
                if (segments.Length == 0)
                    throw new TrueCentralException($"El horario «{template.Name}» de un terminal no tiene ningún tramo (no deja pasar a nadie): revíselo en HikCentral antes de migrar.");
                string baseName = Clip($"Migrado · {template.Name}", 120), name = baseName;
                for (int n = 2; schedules.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++) name = $"{baseName} ({n})";
                match = await tc.CreateScheduleAsync(new AccessScheduleWriteDto(name,
                    $"Creado por el migrador desde la plantilla {template.Number} de HikCentral el {DateTime.Now:dd-MM-yyyy}.",
                    template.Segments.Select(s => new AccessScheduleSegmentDto(s.Day, s.StartMinutes, s.EndMinutes)).ToList()), ct);
                schedules.Add(match);
                Ok($"Horario creado: «{match.Name}» ({DescribeSegments(template.Segments.Select(s => (s.Day, s.StartMinutes, s.EndMinutes)))}).");
            }
            scheduleOf[key] = match;
        }

        // 4. Un nivel por puerta y horario.
        var levels = await tc.GetLevelsAsync(ct);
        var map = new Dictionary<(string Host, int Door, int Template), int>();
        foreach (var access in needed)
        {
            var device = byHost[access.Host.Trim().ToLowerInvariant()];
            var door = device.Doors.First(d => d.Number == access.DoorNo);
            var schedule = scheduleOf[(access.Host.Trim().ToLowerInvariant(), access.TemplateNo)];
            string place = device.Doors.Count > 1 ? $"{device.Name} · {door.Name}" : device.Name;
            string name = Clip(schedule.IsBuiltIn ? $"Puerta · {place}" : $"Puerta · {place} ({schedule.Name})", 120);

            var level = levels.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (level is not null && !(level.ScheduleId == schedule.Id && level.DoorIds.Count == 1 && level.DoorIds[0] == door.Id))
                throw new TrueCentralException($"Ya existe un nivel «{level.Name}» con otras puertas u otro horario: renómbrelo antes de importar (el migrador usa ese nombre).");
            if (level is null)
            {
                level = await tc.CreateLevelAsync(new AccessLevelWriteDto(name,
                    $"Creado por el migrador el {DateTime.Now:dd-MM-yyyy}: quienes entraban por esta puerta en HikCentral.",
                    schedule.Id, [door.Id]), ct);
                levels.Add(level);
                result.Levels++;
                Ok($"Nivel creado: «{level.Name}» ({schedule.Name}).");
            }
            map[AccessKey(access)] = level.Id;
        }
        int people = persons.Count(p => p.Access.Count > 0);
        Info($"Niveles por puerta listos: {map.Values.Distinct().Count()} nivel(es) para {people} persona(s) con puertas.");
        int none = persons.Count(p => p.Access.Count == 0);
        if (none > 0)
            Warn($"{none} persona(s) no estaban en ningún terminal leído (o sin puertas): entran sin niveles y no bajan a los equipos.");
        return map;
    }

    /// <summary>
    /// Crea en TrueCentral los niveles de HikCentral que usan las personas que
    /// se importan: mismo nombre, mismas puertas (ubicadas por la IP de su
    /// terminal) y un horario con el mismo nombre y tramos (el 24/7 de fábrica
    /// si es «todo el día»). Antes de crear nada verifica que estén todos los
    /// terminales, sus puertas y los tramos de cada horario: con una puerta o
    /// un horario de menos alguien perdería acceso. Una segunda pasada
    /// reutiliza lo que creó la primera. Devuelve id de HCP → id en TrueCentral.
    /// </summary>
    private async Task<Dictionary<string, int>> PrepareHcpLevelsAsync(
        TrueCentralClient tc, MigrationPackage package, IReadOnlyList<PackagePerson> persons, ImportResult result, CancellationToken ct)
    {
        if (package.Levels.Count == 0)
            throw new TrueCentralException("El paquete no trae los niveles de HikCentral (la versión de HCP no los entregó, o se exportó con un migrador anterior). " +
                                           "Importe con «uno por puerta».");
        var used = persons.SelectMany(p => p.HcpLevelIds).ToHashSet();
        var levels = package.Levels.Where(l => used.Contains(l.HcpId)).ToList();
        foreach (var skipped in package.Levels.Where(l => !used.Contains(l.HcpId)))
            Info($"Nivel «{skipped.Name}»: ninguna de las personas que se importan lo tiene; no se crea.");

        // 1. Terminales, puertas y horarios: todo o nada.
        var devices = await tc.GetDevicesAsync(ct);
        var byHost = devices.Where(d => d.Host.Length > 0).GroupBy(d => d.Host.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var problems = new List<string>();
        foreach (var level in levels)
        {
            if (level.Segments is null)
                problems.Add($"no se sabe el horario «{level.ScheduleName}» del nivel «{level.Name}» (lea los terminales de sus puertas)");
            else if (level.Segments.Count == 0)
                problems.Add($"el horario «{level.ScheduleName}» del nivel «{level.Name}» no tiene ningún tramo");
            if (level.Doors.Count == 0)
                problems.Add($"el nivel «{level.Name}» no tiene puertas");
            foreach (var door in level.Doors)
            {
                if (door.DeviceHost is null) { problems.Add($"no se sabe en qué terminal está la puerta «{door.Name}» del nivel «{level.Name}»"); continue; }
                if (!byHost.TryGetValue(door.DeviceHost.ToLowerInvariant(), out var device))
                    problems.Add($"el terminal «{door.DeviceName ?? door.Name}» ({door.DeviceHost}) no está dado de alta en TrueCentral");
                else if (device.Doors.All(d => d.Number != door.DoorNo))
                    problems.Add($"«{device.Name}» no tiene la puerta {door.DoorNo} en TrueCentral");
            }
        }
        if (problems.Count > 0)
            throw new TrueCentralException("No se importó nada para no dejar a nadie con más o menos acceso del que tenía: " +
                                           string.Join("; ", problems.Distinct()) + ". Corríjalo y vuelva a importar el paquete.");

        // 2. Horarios: el de fábrica si es «todo el día»; si no, uno con el nombre de HikCentral.
        var schedules = await tc.GetSchedulesAsync(ct);
        var scheduleOf = new Dictionary<string, AccessScheduleDto>();
        foreach (var level in levels)
        {
            var segments = level.Segments!;
            string key = Normalize(segments.Select(s => (s.Day, s.StartMinutes, s.EndMinutes)));
            string cacheKey = $"{level.ScheduleHcpId}|{key}";
            if (scheduleOf.ContainsKey(cacheKey)) continue;

            var builtIn = schedules.FirstOrDefault(s => s.IsBuiltIn && Normalize(s.Segments.Select(x => (x.Day, x.StartMinutes, x.EndMinutes))) == key);
            var match = builtIn ?? schedules.FirstOrDefault(s => s.Name.Equals(level.ScheduleName, StringComparison.OrdinalIgnoreCase) &&
                                                                 s.HolidayMode == AccessHolidayMode.Normal &&
                                                                 Normalize(s.Segments.Select(x => (x.Day, x.StartMinutes, x.EndMinutes))) == key);
            if (match is null)
            {
                string baseName = Clip(level.ScheduleName.Length > 0 ? level.ScheduleName : $"Horario de {level.Name}", 120), name = baseName;
                // Mismo nombre con otros tramos = otro horario: se le pone un sufijo en vez de pisarlo.
                for (int n = 2; schedules.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++) name = $"{baseName} ({n})";
                match = await tc.CreateScheduleAsync(new AccessScheduleWriteDto(name,
                    $"Copiado del horario «{level.ScheduleName}» de HikCentral por el migrador el {DateTime.Now:dd-MM-yyyy} (tramos tomados de {level.SegmentsSource}).",
                    segments.Select(s => new AccessScheduleSegmentDto(s.Day, s.StartMinutes, s.EndMinutes)).ToList()), ct);
                schedules.Add(match);
                Ok($"Horario creado: «{match.Name}» ({DescribeSegments(segments.Select(s => (s.Day, s.StartMinutes, s.EndMinutes)))}).");
            }
            scheduleOf[cacheKey] = match;
        }

        // 3. Niveles con el nombre de HikCentral.
        var existing = await tc.GetLevelsAsync(ct);
        var map = new Dictionary<string, int>();
        foreach (var level in levels)
        {
            var schedule = scheduleOf[$"{level.ScheduleHcpId}|{Normalize(level.Segments!.Select(s => (s.Day, s.StartMinutes, s.EndMinutes)))}"];
            var doorIds = level.Doors.Select(d => byHost[d.DeviceHost!.ToLowerInvariant()].Doors.First(x => x.Number == d.DoorNo).Id)
                .Distinct().OrderBy(id => id).ToList();
            string name = Clip(level.Name.Length > 0 ? level.Name : $"Nivel {level.HcpId} de HikCentral", 120);
            var same = existing.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (same is not null)
            {
                if (same.ScheduleId != schedule.Id || !same.DoorIds.OrderBy(id => id).SequenceEqual(doorIds))
                    throw new TrueCentralException($"Ya existe en TrueCentral un nivel «{same.Name}» con otras puertas u otro horario: renómbrelo antes de importar " +
                                                   "(el migrador copia los niveles de HikCentral con su mismo nombre).");
                map[level.HcpId] = same.Id;
                continue;
            }
            var created = await tc.CreateLevelAsync(new AccessLevelWriteDto(name,
                $"Copiado del nivel «{level.Name}» de HikCentral por el migrador el {DateTime.Now:dd-MM-yyyy}.", schedule.Id, doorIds), ct);
            existing.Add(created);
            map[level.HcpId] = created.Id;
            result.Levels++;
            Ok($"Nivel creado: «{created.Name}» · horario «{schedule.Name}» · {doorIds.Count} puerta(s).");
        }
        int none = persons.Count(p => !p.HcpLevelIds.Any(map.ContainsKey));
        if (none > 0)
            Warn($"{none} persona(s) no tienen niveles en HikCentral: entran a TrueCentral sin puertas y no bajan a los equipos.");
        return map;
    }

    /// <summary>Tramos ordenados en texto, para comparar horarios sin importar el orden.</summary>
    private static string Normalize(IEnumerable<(int Day, int Start, int End)> segments)
    {
        // El VMS escribe el fin de día como 23:59 y algunos equipos lo leen como 24:00.
        var list = segments.Select(s => (s.Day, s.Start, End: Math.Min(s.End, 24 * 60 - 1)))
            .OrderBy(s => s.Day).ThenBy(s => s.Start).Select(s => $"{s.Day}:{s.Start}-{s.End}");
        return string.Join(",", list);
    }

    /// <summary>
    /// Completa una persona que ya está en TrueCentral con las credenciales
    /// que trae el paquete y ella no tiene: dedos nuevos, tarjetas nuevas, la
    /// foto si no tenía rostro, la clave si no tenía y los niveles por puerta
    /// que le falten (<paramref name="doorLevels"/>; nunca le quita uno).
    /// Devuelve false si no había nada que agregar.
    /// </summary>
    private async Task<bool> CompleteExistingAsync(TrueCentralClient tc, ExistingPerson who, PackagePerson person,
        ImportOptions options, Dictionary<string, string> usedCards, ImportResult result,
        IReadOnlyList<int> doorLevels, string? pinCode, CancellationToken ct)
    {
        var current = await tc.GetPersonAsync(who.Id, ct);
        var newLevels = doorLevels.Where(l => !current.LevelIds.Contains(l)).Distinct().ToList();
        string? newPin = current.HasPin ? null : pinCode;

        var newCards = new List<string>();
        foreach (string card in person.Cards.Distinct())
        {
            if (card.Length > 32 || IsVirtualCard(card) || current.Cards.Any(c => c.Number == card)) continue;
            if (usedCards.TryGetValue(card, out string? owner) && owner != current.FullName)
            {
                Warn($"{person.FullName}: la tarjeta {card} ya es de {owner} en TrueCentral; se omite esa tarjeta.");
                continue;
            }
            newCards.Add(card);
        }

        var taken = current.Fingerprints.Select(f => f.Number).ToHashSet();
        var newFingers = person.Fingerprints
            .Where(f => AccessFingers.IsValid(f.Finger) && IsTemplate(f.Template) && !taken.Contains(f.Finger))
            .GroupBy(f => f.Finger).Select(g => g.First()).ToList();

        AccessFaceWriteDto? face = null;
        if (current.Face is null && person.PhotoFile is not null)
        {
            string path = Path.Combine(options.PackageFolder, person.PhotoFile);
            if (File.Exists(path))
            {
                byte[] bytes = await File.ReadAllBytesAsync(path, ct);
                string contentType = person.PhotoContentType ?? "image/jpeg";
                if (UpscaleIfSmall(bytes) is { } bigger) { bytes = bigger; contentType = "image/jpeg"; }
                if (bytes.Length <= AccessFacePhoto.MaxBytes) face = new AccessFaceWriteDto(Convert.ToBase64String(bytes), contentType, person.PhotoSource ?? "HikCentral");
            }
        }

        if (newCards.Count == 0 && newFingers.Count == 0 && face is null && newPin is null && newLevels.Count == 0)
        {
            Info($"{current.FullName} ({current.EmployeeNo}): ya tenía todo; sin cambios.");
            return false;
        }

        // Los dedos que ya están van con plantilla null ("dejar la que ya está");
        // un dedo que no venga en la lista se borraría, por eso se mandan todos.
        var fingers = current.Fingerprints.Select(f => new AccessFingerprintWriteDto(f.Number, null, f.Quality, f.Source))
            .Concat(newFingers.Select(f => new AccessFingerprintWriteDto(f.Finger, f.Template, null, f.Source)))
            .OrderBy(f => f.Number).ToList();

        var dto = new AccessPersonWriteDto(
            FirstName: current.FirstName,
            LastName: current.LastName,
            Department: current.Department,
            Position: current.Position,
            Email: current.Email,
            Phone: current.Phone,
            Notes: current.Notes,
            ValidFrom: current.ValidFrom,
            ValidTo: current.ValidTo,
            Enabled: current.Enabled,
            PinCode: newPin,
            ClearPin: false,
            Cards: current.Cards.Select(c => c.Number).Concat(newCards).ToList(),
            LevelIds: current.LevelIds.Concat(newLevels).ToList(),
            EmployeeNo: null,
            Fingerprints: fingers,
            Face: face);

        var updated = await tc.UpdatePersonAsync(who.Id, dto, ct);
        foreach (string c in newCards) usedCards[c] = updated.FullName;
        result.Cards += newCards.Count;
        result.Fingerprints += newFingers.Count;
        if (face is not null) result.Photos++;
        if (newPin is not null) result.Pins++;
        Ok($"{updated.FullName} ({updated.EmployeeNo}) completada: +{newFingers.Count} huella(s), +{newCards.Count} tarjeta(s)" +
           (face is null ? "" : ", foto agregada") + (newPin is null ? "" : ", clave agregada") +
           (newLevels.Count == 0 ? "" : $", +{newLevels.Count} puerta(s)") +
           $"; ahora tiene {updated.Fingerprints.Count} huella(s), {updated.Cards.Count} tarjeta(s) y {updated.LevelIds.Count} nivel(es).");
        return true;
    }

    // ==================================================================
    // Utilidades
    // ==================================================================

    /// <summary>Lado corto al que se llevan las fotos chicas. Los DS-K1T rechazan rostros por debajo de ~300 px.</summary>
    private const int UpscaleShortSide = 400;
    private const int MinAcceptedSide = 300;

    /// <summary>
    /// Verificado contra un DS-K1T321MFWX: con las miniaturas de 135x189 que
    /// entrega la OpenAPI de HikCentral el terminal contesta "no pudo
    /// reconocer una cara" en buena parte de las personas. Ampliarlas con
    /// interpolación bicúbica no inventa rasgos, pero pasa el umbral de
    /// tamaño y el detector vuelve a encontrar la cara. Devuelve null si la
    /// foto ya es suficientemente grande o no se pudo leer.
    /// </summary>
    private static byte[]? UpscaleIfSmall(byte[] bytes)
    {
        try
        {
            using var input = new MemoryStream(bytes);
            using var image = System.Drawing.Image.FromStream(input);
            int shortSide = Math.Min(image.Width, image.Height);
            if (shortSide >= MinAcceptedSide) return null;
            double factor = (double)UpscaleShortSide / shortSide;
            int w = (int)Math.Round(image.Width * factor), h = (int)Math.Round(image.Height * factor);
            using var bitmap = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(image, 0, 0, w, h);
            }
            var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
            parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
            using var output = new MemoryStream();
            bitmap.Save(output, codec, parameters);
            return output.ToArray();
        }
        catch (Exception) { return null; }   // formato raro: se manda tal cual y que el equipo decida
    }

    private static Dictionary<string, string> OrganizationPaths(List<HcpOrganization> orgs)
    {
        var byCode = orgs.ToDictionary(o => o.IndexCode);
        var result = new Dictionary<string, string>();
        foreach (var org in orgs)
        {
            var parts = new List<string>();
            var current = org;
            int guard = 0;
            while (current is not null && guard++ < 20)
            {
                // La raíz "All Departments" no aporta: se omite.
                if (current.ParentIndexCode != "0") parts.Insert(0, current.Name);
                current = byCode.GetValueOrDefault(current.ParentIndexCode);
            }
            result[org.IndexCode] = parts.Count == 0 ? org.Name : string.Join(" / ", parts);
        }
        return result;
    }

    private static (string First, string Last) SplitName(HcpPerson p)
    {
        string first = p.GivenName.Trim(), last = p.FamilyName.Trim();
        if (first.Length > 0 || last.Length > 0) return (first, last);
        var words = p.FullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => ("Sin nombre", "Sin apellido"),
            1 => (words[0], words[0]),
            _ => (words[0], string.Join(' ', words.Skip(1))),
        };
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    private static string? ClipOrNull(string? s, int max) => s is null ? null : Clip(s.Trim(), max);

    /// <summary>
    /// Tarjeta "virtual" de HikCentral: cuando una persona sin tarjeta recibe
    /// huellas, HCP le baja al terminal un número inventado en el tope de los
    /// 64 bits (18446744073609551876, …) solo para enlazar la plantilla. No
    /// existe físicamente y en TrueCentral sería basura: se descarta.
    /// </summary>
    public static bool IsVirtualCard(string card) =>
        card.Length >= 19 && ulong.TryParse(card, out ulong n) && n >= 18_000_000_000_000_000_000UL;

    /// <summary>Base64 de 64 a 4096 bytes: lo que acepta el servidor como plantilla.</summary>
    private static bool IsTemplate(string base64)
    {
        Span<byte> buffer = stackalloc byte[4096 + 8];
        if (!Convert.TryFromBase64String(base64.Trim(), buffer, out int written)) return false;
        return written is >= 64 and <= 4096;
    }

    private static string SafeFileName(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        return sb.Length == 0 ? "persona" : sb.ToString();
    }

    /// <summary>Sin tildes, en minúsculas, un solo espacio: para comparar nombres.</summary>
    private static string Fold(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
