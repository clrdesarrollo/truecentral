using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Escribe el padrón del VMS en los equipos. El VMS es la fuente de verdad y
/// los equipos son una copia suya: acá se calcula qué le corresponde a cada
/// persona en cada equipo y se deja el equipo igual a eso.
///
/// La cuenta es siempre la misma:
/// <list type="number">
/// <item>los NIVELES DE ACCESO de la persona dan sus puertas, y cada nivel
/// aporta el HORARIO con que se pasa por ellas;</item>
/// <item>las puertas se agrupan POR EQUIPO, que es a quién hay que hablarle;</item>
/// <item>si dos niveles dan la misma puerta con horarios distintos, se escribe
/// la UNIÓN de los dos (uno deja pasar el martes y el otro el jueves: la
/// persona pasa los dos días);</item>
/// <item>de todo eso sale una huella. Si es la misma que la última vez que el
/// equipo aceptó a esa persona, no se lo vuelve a molestar.</item>
/// </list>
///
/// Lo que no se puede escribir no se pierde: queda pendiente con su motivo a
/// la vista (equipo caído, credencial rechazada, marca que no admite padrón) y
/// se reintenta en la pasada siguiente.
/// </summary>
public sealed class AccessSyncService(
    IServiceScopeFactory scopeFactory,
    AccessControlService access,
    CredentialProtector credentials,
    IHubContext<VmsHub> hub,
    AuditService audit,
    ILogger<AccessSyncService> logger) : BackgroundService
{
    /// <summary>Cada cuánto se reintenta sola la escritura pendiente (equipos que estaban caídos).</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

    /// <summary>Personas que se escriben en un mismo lote (un equipo lento no debe trabar la pasada entera).</summary>
    private const int BatchSize = 50;

    /// <summary>
    /// Ranuras de horario que admiten los equipos. Es el número más bajo de la
    /// familia (los Hikvision de acceso guardan 128 plantillas), así que el
    /// tope vale para todos.
    /// </summary>
    private const int MaxPlanSlots = 128;

    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>
    /// Una pasada a la vez. El lazo de fondo y los botones del panel pueden
    /// pedirla al mismo tiempo, y dos pasadas en paralelo se pisarían las filas
    /// de estado y le escribirían dos veces lo mismo a cada equipo.
    /// </summary>
    private readonly SemaphoreSlim _pass = new(1, 1);

    /// <summary>Hay cambios que escribir: hacerlo ahora en vez de esperar el reintento.</summary>
    public void RequestSync()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // El sondeo de estado necesita una vuelta para saber qué equipos están
        // vivos; escribir antes de eso sería escribirle a equipos "desconocidos".
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SyncPendingAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "La escritura del padrón en los equipos falló."); }

            try { await _wake.WaitAsync(RetryInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ==================================================================
    // Marcar pendientes
    // ==================================================================

    /// <summary>
    /// Deja pendientes de escritura a las personas indicadas. Lo llaman la API
    /// (alta o edición de personas, niveles y horarios) y la propia
    /// reconciliación. No guarda: el llamador decide cuándo confirmar.
    /// </summary>
    public static async Task MarkPendingAsync(VmsDbContext db, IEnumerable<int> personIds, CancellationToken ct)
    {
        var ids = personIds.Distinct().ToList();
        if (ids.Count == 0) return;
        var persons = await db.AccessPersons.Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        foreach (var person in persons)
        {
            person.SyncState = AccessSyncState.Pending;
            person.UpdatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>Personas alcanzadas por un nivel de acceso (cambió sus puertas, su horario o se apagó).</summary>
    public static async Task MarkLevelPendingAsync(VmsDbContext db, int levelId, CancellationToken ct)
    {
        var personIds = await db.AccessLevelPersons.Where(x => x.AccessLevelId == levelId)
            .Select(x => x.AccessPersonId).ToListAsync(ct);
        await MarkPendingAsync(db, personIds, ct);
    }

    /// <summary>Personas alcanzadas por un horario (a través de los niveles que lo usan).</summary>
    public static async Task MarkSchedulePendingAsync(VmsDbContext db, int scheduleId, CancellationToken ct)
    {
        var levelIds = await db.AccessLevels.Where(l => l.AccessScheduleId == scheduleId).Select(l => l.Id).ToListAsync(ct);
        var personIds = await db.AccessLevelPersons.Where(x => levelIds.Contains(x.AccessLevelId))
            .Select(x => x.AccessPersonId).ToListAsync(ct);
        await MarkPendingAsync(db, personIds, ct);
    }

    /// <summary>Personas alcanzadas por una puerta (se apagó, se renombró el equipo, cambió de nivel).</summary>
    public static async Task MarkDoorPendingAsync(VmsDbContext db, int doorId, CancellationToken ct)
    {
        var levelIds = await db.AccessLevelDoors.Where(x => x.AccessDoorId == doorId)
            .Select(x => x.AccessLevelId).ToListAsync(ct);
        var personIds = await db.AccessLevelPersons.Where(x => levelIds.Contains(x.AccessLevelId))
            .Select(x => x.AccessPersonId).ToListAsync(ct);
        await MarkPendingAsync(db, personIds, ct);
    }

    /// <summary>
    /// Deja todo listo para REESCRIBIR lo que ya estaba escrito. Marcar
    /// pendiente no alcanza: el sincronizador compara la huella de lo último
    /// que el equipo aceptó y, si no cambió, no lo vuelve a molestar. Acá se
    /// borra esa huella a propósito.
    ///
    /// Es lo que hace falta cuando el equipo perdió el padrón sin que el VMS se
    /// entere: se reemplazó el terminal, se lo volvió a fábrica, o alguien
    /// borró personas desde su pantalla.
    ///
    /// Con <paramref name="deviceId"/> se limita a ese equipo; sin él, a todos.
    /// Devuelve a cuántas personas alcanza. No guarda: decide el llamador.
    /// </summary>
    public static async Task<int> MarkForResendAsync(VmsDbContext db, int? deviceId, CancellationToken ct)
    {
        var rows = await db.AccessPersonDevices
            .Where(r => deviceId == null || r.AccessDeviceId == deviceId)
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            row.AppliedHash = null;
            row.State = AccessSyncState.Pending;
        }

        var ids = rows.Select(r => r.AccessPersonId).ToHashSet();

        // Además de las ya escritas, las que DEBERÍAN estarlo: una persona con
        // niveles pero sin fila (nunca se pudo escribir) también entra. Con un
        // equipo puntual no hace falta: sus filas ya la traen.
        if (deviceId is null)
            foreach (int id in await db.AccessLevelPersons.Select(x => x.AccessPersonId).Distinct().ToListAsync(ct))
                ids.Add(id);

        var persons = await db.AccessPersons.Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        foreach (var person in persons)
        {
            person.SyncState = AccessSyncState.Pending;
            person.UpdatedAt = DateTime.UtcNow;
        }
        return persons.Count;
    }

    // ==================================================================
    // Ranuras de horario
    // ==================================================================

    /// <summary>
    /// Ranura que le corresponde a un conjunto de tramos, creándola si es la
    /// primera vez que se ve. Dos horarios con los mismos tramos comparten
    /// ranura: lo que identifica a una ranura es su contenido, no quién la pidió.
    /// </summary>
    public static async Task<AccessPlanSlot> SlotForAsync(VmsDbContext db, IReadOnlyList<AccessTimeSegment> segments,
        string label, CancellationToken ct)
    {
        var canonical = Canonical(segments);
        string hash = HashOf(JsonSerializer.Serialize(canonical));

        // Se mira primero lo que ya se pidió en esta misma tanda y todavía no se
        // guardó: dos puertas con el mismo horario comparten ranura, y sin esto
        // la segunda pediría una nueva y chocaría contra el índice único.
        var pending = db.AccessPlanSlots.Local.FirstOrDefault(s => s.Hash == hash);
        if (pending is not null) return pending;
        var existing = await db.AccessPlanSlots.FirstOrDefaultAsync(s => s.Hash == hash, ct);
        if (existing is not null) return existing;

        // El número más bajo que esté libre: así las ranuras se mantienen
        // juntas y es más fácil mirarlas en el propio equipo.
        var taken = await db.AccessPlanSlots.Select(s => s.Number).ToListAsync(ct);
        taken.AddRange(db.AccessPlanSlots.Local.Select(s => s.Number));
        int number = Enumerable.Range(1, MaxPlanSlots).Except(taken).FirstOrDefault();
        if (number == 0)
            throw new InvalidOperationException(
                $"Se agotaron las {MaxPlanSlots} ranuras de horario de los equipos. Junte niveles de acceso que " +
                "usen el mismo horario o borre horarios que ya no se usan.");

        var slot = new AccessPlanSlot
        {
            Number = number,
            Hash = hash,
            Label = label.Length <= 64 ? label : label[..64],
            SegmentsJson = JsonSerializer.Serialize(canonical),
        };
        db.AccessPlanSlots.Add(slot);
        return slot;
    }

    /// <summary>
    /// Tramos en forma canónica: ordenados y con los que se pisan fusionados.
    /// Es lo que hace que la huella de "8-12 y 10-18" sea la misma que la de
    /// "8-18", y que la unión de dos horarios no dependa del orden en que se
    /// sumaron.
    /// </summary>
    public static List<AccessTimeSegment> Canonical(IEnumerable<AccessTimeSegment> segments)
    {
        var result = new List<AccessTimeSegment>();
        foreach (var group in segments.GroupBy(s => s.Day).OrderBy(g => g.Key))
        {
            AccessTimeSegment? open = null;
            foreach (var segment in group.OrderBy(s => s.StartMinutes).ThenBy(s => s.EndMinutes))
            {
                if (open is null) { open = segment; continue; }
                // Se tocan o se solapan: se funden en uno solo.
                if (segment.StartMinutes <= open.EndMinutes)
                    open = open with { EndMinutes = Math.Max(open.EndMinutes, segment.EndMinutes) };
                else { result.Add(open); open = segment; }
            }
            if (open is not null) result.Add(open);
        }
        return result;
    }

    private static string HashOf(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant();

    // ==================================================================
    // Escritura
    // ==================================================================

    /// <summary>Escribe en los equipos todo lo que esté pendiente. Devuelve cuántas personas quedaron al día.</summary>
    public async Task<int> SyncPendingAsync(CancellationToken ct)
    {
        await _pass.WaitAsync(ct);
        try { return await RunPassAsync(ct); }
        finally { _pass.Release(); }
    }

    private async Task<int> RunPassAsync(CancellationToken ct)
    {
        int done = 0;
        while (!ct.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();

            var pending = await PendingQuery(db).OrderBy(p => p.Id).Take(BatchSize).ToListAsync(ct);
            if (pending.Count == 0) break;

            // Los horarios ya escritos en esta pasada no se reescriben: un
            // equipo con cien personas del mismo nivel recibe su horario una vez.
            var writtenPlans = new HashSet<(int Device, int Slot)>();
            foreach (var person in pending)
            {
                await SyncPersonAsync(db, person, writtenPlans, ct);
                done++;
            }
            await db.SaveChangesAsync(ct);

            foreach (var person in pending)
                await hub.Clients.All.SendAsync(VmsHubContract.AccessPersonSyncChanged, ToPersonDto(person), ct);

            if (pending.Count < BatchSize) break;
        }
        return done;
    }

    /// <summary>Personas con algo que escribir, con todo lo que hace falta para calcular su plan.</summary>
    private static IQueryable<AccessPerson> PendingQuery(VmsDbContext db) =>
        db.AccessPersons
            .Include(p => p.Cards)
            .Include(p => p.Fingerprints)
            .Include(p => p.Face)
            .Include(p => p.Devices).ThenInclude(d => d.AccessDevice)
            .Include(p => p.Levels).ThenInclude(x => x.AccessLevel).ThenInclude(l => l!.AccessSchedule)
                .ThenInclude(s => s!.Segments)
            .Include(p => p.Levels).ThenInclude(x => x.AccessLevel).ThenInclude(l => l!.Doors)
                .ThenInclude(d => d.AccessDoor).ThenInclude(d => d!.AccessDevice)
            .Where(p => p.SyncState == AccessSyncState.Pending || p.SyncState == AccessSyncState.Failed);

    /// <summary>
    /// Deja a una persona escrita donde corresponde y borrada donde ya no. Un
    /// equipo caído o que rechaza no detiene a los demás: cada equipo lleva su
    /// propio resultado.
    /// </summary>
    private async Task SyncPersonAsync(VmsDbContext db, AccessPerson person, HashSet<(int, int)> writtenPlans,
        CancellationToken ct)
    {
        var target = await TargetsOfAsync(db, person, ct);

        // Equipos donde ya no corresponde: primero borrar, para que una persona
        // a la que se le quitó el permiso deje de entrar cuanto antes.
        foreach (var row in person.Devices.Where(d => !target.ContainsKey(d.AccessDeviceId)).ToList())
        {
            var device = row.AccessDevice;
            if (device is null) { person.Devices.Remove(row); continue; }
            row.PendingRemoval = true;
            try
            {
                if (access.SupportsPersonSync(device.DriverKey))
                    await access.DriverOf(device).RemovePersonAsync(access.ConnectionOf(device), person.EmployeeNo, ct);
                person.Devices.Remove(row);
                db.Remove(row);
                await audit.LogSystemAsync("access", "person-removed-from-device",
                    targetType: "access-person", targetId: person.Id.ToString(), targetName: person.FullName,
                    detail: $"Quitó a '{person.FullName}' del equipo '{device.Name}': ya no tiene permiso en sus puertas.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                row.State = AccessSyncState.Failed;
                row.Error = Shorten(ex.Message);
            }
        }

        // Equipos donde corresponde: crear o actualizar.
        foreach (var (deviceId, plan) in target)
        {
            var row = person.Devices.FirstOrDefault(d => d.AccessDeviceId == deviceId);
            if (row is null)
            {
                row = new AccessPersonDevice { AccessPersonId = person.Id, AccessDeviceId = deviceId };
                person.Devices.Add(row);
            }
            row.AccessDevice ??= await db.AccessDevices.FindAsync([deviceId], ct);
            if (row.AccessDevice is not { } device)
            {
                // El equipo se borró mientras se calculaba el plan: la fila
                // sobra, y la cascada del borrado ya se está encargando.
                person.Devices.Remove(row);
                continue;
            }

            string hash = HashOf(JsonSerializer.Serialize(plan.Plan));
            if (row.State == AccessSyncState.Synced && row.AppliedHash == hash) continue;

            if (!access.SupportsPersonSync(device.DriverKey))
            {
                row.State = AccessSyncState.Failed;
                row.Error = $"Los equipos {device.DriverKey} no aceptan el padrón desde el VMS: " +
                            "las personas se cargan en el propio equipo.";
                continue;
            }
            if (!device.Enabled || device.Status != AccessDeviceStatus.Online)
            {
                // No es un error de configuración: el equipo está caído o
                // pausado. Queda pendiente y se reintenta solo.
                row.State = AccessSyncState.Pending;
                row.Error = device.Enabled
                    ? $"El equipo '{device.Name}' no está en línea; se reintenta solo."
                    : $"El equipo '{device.Name}' está pausado.";
                continue;
            }

            try
            {
                var driver = access.DriverOf(device);
                var connection = access.ConnectionOf(device);

                // Los horarios primero: una persona que referencia una ranura
                // vacía la rechaza el equipo.
                foreach (var slot in plan.Slots)
                {
                    if (!writtenPlans.Add((deviceId, slot.Number))) continue;
                    await driver.ApplyWeekPlanAsync(connection, slot, ct);
                }

                await driver.ApplyPersonAsync(connection, plan.Plan, ct);
                row.State = AccessSyncState.Synced;
                row.Error = null;
                row.AppliedHash = hash;
                row.SyncedAt = DateTime.UtcNow;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                row.State = AccessSyncState.Failed;
                row.Error = Shorten(ex.Message);
                row.AppliedHash = null;
                logger.LogWarning(ex, "No se pudo escribir a {Person} en el equipo {Device}.", person.FullName, device.Name);
            }
        }

        Summarize(person);
    }

    /// <summary>El estado de la persona es el peor de sus equipos: lo que falta en uno le falta a ella.</summary>
    private static void Summarize(AccessPerson person)
    {
        var rows = person.Devices;
        person.SyncState = rows.Count == 0 ? AccessSyncState.NotApplicable
            : rows.Any(r => r.State == AccessSyncState.Failed) ? AccessSyncState.Failed
            : rows.Any(r => r.State != AccessSyncState.Synced) ? AccessSyncState.Pending
            : AccessSyncState.Synced;
        person.SyncError = rows.FirstOrDefault(r => r.Error is not null)?.Error;
        person.LastSyncedAt = rows.Count > 0 && rows.All(r => r.SyncedAt is not null)
            ? rows.Max(r => r.SyncedAt)
            : person.LastSyncedAt;
    }

    private static string Shorten(string message) => message.Length <= 480 ? message : message[..480] + "…";

    /// <summary>Lo que hay que escribirle a UN equipo por UNA persona.</summary>
    private sealed record DeviceTarget(AccessPersonPlan Plan, IReadOnlyList<AccessWeekPlan> Slots);

    /// <summary>
    /// Qué le corresponde a la persona en cada equipo. Una persona apagada o
    /// vencida no corresponde en ninguno: el equipo la respetaría igual por su
    /// vigencia, pero borrarla es más seguro que confiar en que la respete.
    /// </summary>
    private async Task<Dictionary<int, DeviceTarget>> TargetsOfAsync(VmsDbContext db, AccessPerson person, CancellationToken ct)
    {
        var result = new Dictionary<int, DeviceTarget>();
        if (!person.Enabled || person.ValidTo <= DateTime.UtcNow) return result;

        // Puerta → tramos que la persona tiene sobre ella, sumando todos los
        // niveles que se la dan.
        var byDoor = new Dictionary<int, (AccessDoor Door, List<AccessTimeSegment> Segments, List<string> Sources)>();
        foreach (var assignment in person.Levels)
        {
            var level = assignment.AccessLevel;
            if (level is null || !level.Enabled || level.AccessSchedule is null) continue;
            var segments = level.AccessSchedule.Segments
                .Select(s => new AccessTimeSegment(s.Day, s.StartMinutes, s.EndMinutes)).ToList();
            if (segments.Count == 0) continue;   // un horario sin tramos no deja pasar a nadie

            foreach (var levelDoor in level.Doors)
            {
                var door = levelDoor.AccessDoor;
                if (door is null || !door.Enabled || door.AccessDevice is null || !door.AccessDevice.Enabled) continue;
                if (!byDoor.TryGetValue(door.Id, out var entry))
                    byDoor[door.Id] = entry = (door, [], []);
                entry.Segments.AddRange(segments);
                if (!entry.Sources.Contains(level.AccessSchedule.Name)) entry.Sources.Add(level.AccessSchedule.Name);
            }
        }
        if (byDoor.Count == 0) return result;

        // Cada conjunto de tramos necesita su ranura en los equipos.
        var rights = new Dictionary<int, List<AccessDoorRight>>();
        var slotsByDevice = new Dictionary<int, Dictionary<int, AccessWeekPlan>>();
        foreach (var (door, segments, sources) in byDoor.Values)
        {
            var canonical = Canonical(segments);
            var slot = await SlotForAsync(db, canonical, string.Join(" + ", sources), ct);
            var plan = new AccessWeekPlan(slot.Number, slot.Label, canonical);

            int deviceId = door.AccessDeviceId;
            if (!rights.TryGetValue(deviceId, out var doorRights)) rights[deviceId] = doorRights = [];
            doorRights.Add(new AccessDoorRight(door.Number, plan));

            if (!slotsByDevice.TryGetValue(deviceId, out var slots)) slotsByDevice[deviceId] = slots = [];
            slots[slot.Number] = plan;
        }
        // Las ranuras nuevas tienen que existir antes de escribir a nadie.
        await db.SaveChangesAsync(ct);

        string? pin = person.PinCiphertext.Length > 0 ? credentials.Unprotect(person.PinCiphertext) : null;
        var cards = person.Cards.Where(c => c.Enabled).Select(c => c.Number).OrderBy(n => n).ToList();
        // Las plantillas se descifran una sola vez, no una por equipo.
        var fingerprints = person.Fingerprints.OrderBy(f => f.Number)
            .Select(f => new AccessFingerprintData(f.Number, credentials.Unprotect(f.TemplateCiphertext)))
            .ToList();
        // La foto también se descifra una sola vez: pesa bastante más que una
        // plantilla y va a los equipos con cámara tal cual está.
        var face = person.Face is { } stored
            ? new AccessFaceData(credentials.UnprotectBytes(stored.ImageCiphertext), stored.ContentType)
            : null;

        foreach (var (deviceId, doorRights) in rights)
        {
            // A un equipo que no sabe de biometría no se le mandan huellas: se
            // le escribiría algo que va a rechazar y quedaría la persona entera
            // como fallida por una credencial que ese equipo ni usa.
            var device = await db.AccessDevices.FindAsync([deviceId], ct);
            bool biometric = device is not null && access.SupportsFingerprintSync(device.DriverKey);
            // Lo mismo con el rostro, que además es cosa de otro terminal: hay
            // equipos con lector de huella y sin cámara, y al revés.
            bool camera = device is not null && device.SupportsFace && access.SupportsFaceSync(device.DriverKey);

            var plan = new AccessPersonPlan(
                person.EmployeeNo,
                person.FullName,
                person.ValidFrom,
                person.ValidTo,
                string.IsNullOrWhiteSpace(pin) ? null : pin,
                cards,
                biometric ? fingerprints : [],
                camera ? face : null,
                doorRights.OrderBy(d => d.DoorNumber).ToList());
            result[deviceId] = new DeviceTarget(plan, slotsByDevice[deviceId].Values.ToList());
        }
        return result;
    }

    // ==================================================================
    // Mapeo a DTO (lo comparten la API y el hub)
    // ==================================================================

    public static AccessPersonDto ToPersonDto(AccessPerson person) => new(
        person.Id, person.EmployeeNo, person.FirstName, person.LastName, person.FullName,
        person.Department, person.Position, person.Email, person.Phone, person.Notes,
        person.ValidFrom, person.ValidTo, person.Enabled, person.PinCiphertext.Length > 0,
        person.Cards.OrderBy(c => c.Id).Select(c => new AccessCardDto(c.Id, c.Number, c.Enabled)).ToList(),
        person.Fingerprints.OrderBy(f => f.Number)
            .Select(f => new AccessFingerprintDto(f.Number, AccessFingers.NameOf(f.Number), f.Quality, f.Source, f.CreatedAt))
            .ToList(),
        person.Face is { } face
            ? new AccessFaceDto(face.Bytes, face.ContentType, face.Source, face.CreatedAt)
            : null,
        person.Levels.Select(x => x.AccessLevelId).ToList(),
        person.Levels.Select(x => x.AccessLevel?.Name ?? "").Where(n => n.Length > 0).OrderBy(n => n).ToList(),
        person.SyncState, person.SyncError, person.LastSyncedAt,
        person.Devices.Select(d => new AccessPersonDeviceDto(
            d.AccessDeviceId, d.AccessDevice?.Name ?? $"Equipo {d.AccessDeviceId}", d.State, d.Error, d.SyncedAt)).ToList(),
        person.CreatedAt, person.UpdatedAt);
}
