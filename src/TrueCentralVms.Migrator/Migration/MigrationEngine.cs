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
    public required string PackageFolder { get; init; }
}

public sealed class ImportOptions
{
    public required TrueCentralClient TrueCentral { get; init; }
    public required string PackageFolder { get; init; }
    public EmployeeNoSource EmployeeNoSource { get; init; }
    /// <summary>Nivel de acceso que reciben las personas al crearse (null = ninguno; se asignan después).</summary>
    public int? LevelId { get; init; }
    public string? LevelName { get; init; }
    /// <summary>
    /// Si la persona ya existe (mismo identificador), completarla con lo que
    /// le falte —huellas, tarjetas, foto— en vez de omitirla. No pisa nombre,
    /// vigencia, clave ni niveles: eso ya lo administra TrueCentral.
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
            await ReadTerminalsAsync(options.Terminals, package, ct);
            NextPhase();
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
        if (withFingers > 0 && package.Persons.Sum(p => p.Fingerprints.Count) == 0)
            Info("HikCentral no entrega las plantillas de huella por OpenAPI: se van a leer desde los terminales.");
        return package;
    }

    private readonly Dictionary<string, string> _photoUris = new();

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

    private async Task ReadTerminalsAsync(IReadOnlyList<TerminalTarget> terminals, MigrationPackage package, CancellationToken ct)
    {
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
                     (reportsFingerCount ? $"{users.Count(u => u.Fingerprints > 0)}." : "el firmware no lo informa; se consultan los diez dedos de cada persona."));

                int matched = 0, fingers = 0, unmatched = 0, virtualCards = 0;
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
                Ok($"[{target.Name}] personas cruzadas: {matched} de {users.Count} · huellas recuperadas: {fingers}.");
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
        Info(options.LevelId is null
            ? "Nivel de acceso: ninguno (las personas no bajan a los equipos hasta que se les asigne uno)."
            : $"Nivel de acceso asignado al crear: {options.LevelName ?? options.LevelId.ToString()} (el sincronizador las baja a sus equipos).");

        int upscaled = 0;
        for (int i = 0; i < package.Persons.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var person = package.Persons[i];
            Report((double)i / package.Persons.Count, $"{person.FullName} ({i + 1}/{package.Persons.Count})");

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
                        if (await CompleteExistingAsync(tc, who, person, options, usedCards, result, ct)) result.Updated++;
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
                    else face = new AccessFaceWriteDto(Convert.ToBase64String(bytes), contentType, "HikCentral");
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
                PinCode: null,
                ClearPin: false,
                Cards: cards,
                LevelIds: options.LevelId is { } level ? [level] : [],
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
                Ok($"{created.FullName} → identificador {created.EmployeeNo}: {cards.Count} tarjeta(s), {fingers.Count} huella(s), {(face is null ? "sin foto" : "con foto")}.");
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
           $"Fotos: {result.Photos} · tarjetas: {result.Cards} · huellas: {result.Fingerprints}.");
        if (result.Created > 0 && options.LevelId is null)
            Info("Las personas entraron sin nivel de acceso: asígnelos en TrueCentral (Control de acceso → Niveles) para que el sincronizador las baje a los equipos.");
        if (result.Created > 0)
            Info("Las claves de teclado no se migran: hay que volver a definirlas. El estado de la bajada a cada equipo se ve en TrueCentral → Control de acceso → Personas.");
        return result;
    }

    /// <summary>
    /// Completa una persona que ya está en TrueCentral con las credenciales
    /// que trae el paquete y ella no tiene: dedos nuevos, tarjetas nuevas y
    /// la foto si no tenía rostro. Devuelve false si no había nada que agregar.
    /// </summary>
    private async Task<bool> CompleteExistingAsync(TrueCentralClient tc, ExistingPerson who, PackagePerson person,
        ImportOptions options, Dictionary<string, string> usedCards, ImportResult result, CancellationToken ct)
    {
        var current = await tc.GetPersonAsync(who.Id, ct);

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
                if (bytes.Length <= AccessFacePhoto.MaxBytes) face = new AccessFaceWriteDto(Convert.ToBase64String(bytes), contentType, "HikCentral");
            }
        }

        if (newCards.Count == 0 && newFingers.Count == 0 && face is null)
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
            PinCode: null,
            ClearPin: false,
            Cards: current.Cards.Select(c => c.Number).Concat(newCards).ToList(),
            LevelIds: current.LevelIds,
            EmployeeNo: null,
            Fingerprints: fingers,
            Face: face);

        var updated = await tc.UpdatePersonAsync(who.Id, dto, ct);
        foreach (string c in newCards) usedCards[c] = updated.FullName;
        result.Cards += newCards.Count;
        result.Fingerprints += newFingers.Count;
        if (face is not null) result.Photos++;
        Ok($"{updated.FullName} ({updated.EmployeeNo}) completada: +{newFingers.Count} huella(s), +{newCards.Count} tarjeta(s)" +
           (face is null ? "" : ", foto agregada") + $"; ahora tiene {updated.Fingerprints.Count} huella(s) y {updated.Cards.Count} tarjeta(s).");
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
