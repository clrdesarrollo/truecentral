using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Trae los accesos de los equipos y los deja en la base del VMS, por dos vías
/// que se complementan:
///
/// <list type="number">
/// <item><b>En vivo</b>: a los equipos que saben empujar sus eventos se les
/// mantiene abierta una escucha, y lo que pasa en la puerta aparece en el
/// monitoreo en el momento. Es la vía normal.</item>
/// <item><b>Sondeo de respaldo</b>: cada 15 s se le pregunta a cada equipo qué
/// pasó desde su MARCA DE AGUA. Cubre a los equipos que no empujan, y tapa los
/// huecos de los que sí: mientras la escucha estaba caída (o el servidor
/// apagado) igual se recupera lo que se perdió. La marca es el número de
/// evento del equipo (<c>serialNo</c>) cuando el equipo sabe buscar por él, y
/// la hora del último evento si no.</item>
/// </list>
///
/// Que las dos vías traigan el mismo evento no molesta: se descarta por equipo,
/// hora y código antes de guardarlo.
///
/// Los eventos NO van a la bitácora de auditoría: esa registra lo que hacen
/// los usuarios del VMS, y esto es lo que hace la gente frente a una puerta.
/// Su registro es la tabla del módulo, que se consulta desde
/// <c>Control de acceso → Historial</c> y se purga sola según
/// <c>Access:EventRetentionDays</c>.
/// </summary>
public sealed class AccessEventService(
    IServiceScopeFactory scopeFactory,
    AccessControlService access,
    IConfiguration config,
    ScopedHub hub,
    ILogger<AccessEventService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    /// <summary>Eventos que se traen de un equipo por vuelta (un equipo con atraso se pone al día en varias).</summary>
    private const int MaxPerPoll = 200;

    /// <summary>
    /// Cuánto hacia atrás se lee la primera vez que se consulta a un equipo.
    /// Poco a propósito: un terminal puede tener años de historial guardado y
    /// no corresponde volcarlo entero al VMS por haberlo dado de alta.
    /// </summary>
    private static readonly TimeSpan FirstReadWindow = TimeSpan.FromMinutes(15);

    /// <summary>Equipos que se consultan a la vez.</summary>
    private const int Parallelism = 4;

    /// <summary>
    /// Cada cuánto, a un equipo que se lee por número de evento, se le pregunta
    /// además por hora (<c>Access:EventTimeCheckMinutes</c>, 10 por omisión). Es
    /// lo que delata un contador que volvió a empezar (equipo restablecido)
    /// cuando no hay escucha en vivo que lo muestre.
    /// </summary>
    private TimeSpan TimeCheckInterval =>
        TimeSpan.FromMinutes(Math.Clamp(config.GetValue("Access:EventTimeCheckMinutes", 10), 1, 1440));

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> _lastTimeCheck = new();

    private DateTime _lastPurge = DateTime.MinValue;

    /// <summary>Escuchas abiertas, por equipo: para no abrir dos al mismo.</summary>
    private readonly Dictionary<int, Task> _listeners = [];

    private int RetentionDays => Math.Clamp(config.GetValue("Access:EventRetentionDays", 365), 7, 3650);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Después del primer sondeo de estado: preguntarle el historial a un
        // equipo que todavía no se sabe si está vivo es tiempo perdido.
        try { await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                EnsureListeners(stoppingToken);
                await PollAllAsync(stoppingToken);
                await PurgeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "La lectura del historial de accesos falló."); }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Abre una escucha por cada equipo que sepa empujar eventos y todavía no
    /// tenga una. Se llama en cada vuelta del sondeo, así que un equipo que se
    /// da de alta —o que vuelve después de una caída— queda escuchado sin
    /// ceremonia.
    /// </summary>
    private void EnsureListeners(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var devices = db.AccessDevices.AsNoTracking()
            .Where(d => d.Enabled && d.SupportsEvents)
            .ToList();

        foreach (var device in devices)
        {
            if (_listeners.TryGetValue(device.Id, out var running) && !running.IsCompleted) continue;
            if (!access.SupportsEventStream(device.DriverKey)) continue;
            _listeners[device.Id] = Task.Run(() => ListenAsync(device.Id, ct), ct);
        }

        // Los equipos que se dieron de baja o se pausaron dejan de escucharse
        // solos: su tarea termina al fallar la conexión y no se vuelve a crear.
        foreach (int id in _listeners.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToList())
            if (devices.All(d => d.Id != id)) _listeners.Remove(id);
    }

    /// <summary>
    /// Mantiene la escucha de UN equipo: abre, procesa lo que llega y, si se
    /// corta, espera y vuelve a abrir. Un equipo apagado no debe reintentar a
    /// toda velocidad, así que la espera crece hasta un minuto.
    /// </summary>
    private async Task ListenAsync(int deviceId, CancellationToken ct)
    {
        int fallos = 0;
        bool primera = true;
        while (!ct.IsCancellationRequested)
        {
            AccessDevice? device;
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
                device = await db.AccessDevices.AsNoTracking().Include(d => d.Doors)
                    .FirstOrDefaultAsync(d => d.Id == deviceId, ct);
            }
            if (device is null || !device.Enabled || !device.SupportsEvents) return;

            try
            {
                // El driver corta la escucha que queda en silencio (sin latido)
                // para reconectar: esas vueltas sanas no ensucian el registro.
                logger.Log(primera || fallos > 0 ? LogLevel.Information : LogLevel.Debug,
                    "Escuchando los eventos de '{Device}' en vivo.", device.Name);
                primera = false;
                await foreach (var record in access.DriverOf(device).StreamEventsAsync(access.ConnectionOf(device), ct))
                {
                    fallos = 0;   // llegó algo: la conexión está sana
                    await StoreAsync(device.Id, [record], ct);
                }
                logger.LogDebug("El equipo {Device} cerró la escucha de eventos.", device.Name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                fallos++;
                logger.LogDebug(ex, "Se cortó la escucha de eventos de {Device}.", device.Name);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(5 * Math.Max(fallos, 1), 60)), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollAllAsync(CancellationToken ct)
    {
        List<AccessDevice> devices;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            devices = await db.AccessDevices.AsNoTracking().Include(d => d.Doors)
                .Where(d => d.Enabled && d.SupportsEvents && d.Status == AccessDeviceStatus.Online)
                .ToListAsync(ct);
        }
        if (devices.Count == 0) return;

        using var limiter = new SemaphoreSlim(Parallelism);
        await Task.WhenAll(devices.Select(async device =>
        {
            await limiter.WaitAsync(ct);
            try { await PollOneAsync(device, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "No se pudo leer el historial del equipo {Device}.", device.Name);
            }
            finally { limiter.Release(); }
        }));
    }

    private async Task PollOneAsync(AccessDevice device, CancellationToken ct)
    {
        var driver = access.DriverOf(device);
        var connection = access.ConnectionOf(device);

        // Por número de evento, si ya se conoce uno y el equipo sabe buscar así
        // (el driver devuelve null si no). Lo que trae, aunque sea solo ruido
        // descartado, adelanta la marca.
        if (device.LastEventSerial is long after)
        {
            AccessEventPage? page;
            try { page = await driver.FetchEventsAfterSerialAsync(connection, after, MaxPerPoll, ct); }
            catch (DriverException ex)
            {
                logger.LogDebug(ex, "El equipo {Device} no entregó su historial.", device.Name);
                return;
            }
            if (page is not null)
            {
                await StoreAsync(device.Id, page.Events, ct, page.LastSerial, fromHistory: true);
                if (_lastTimeCheck.TryGetValue(device.Id, out var checkedAt) && DateTime.UtcNow - checkedAt < TimeCheckInterval)
                    return;
            }
        }

        // Por hora: los equipos que no buscan por número, la primera lectura
        // (de ahí sale el primer número) y el control periódico de arriba.
        var since = device.LastEventAt ?? DateTime.UtcNow - FirstReadWindow;
        IReadOnlyList<AccessEventRecord> records;
        try { records = await driver.FetchEventsAsync(connection, since, MaxPerPoll, ct); }
        catch (DriverException ex)
        {
            logger.LogDebug(ex, "El equipo {Device} no entregó su historial.", device.Name);
            return;
        }
        _lastTimeCheck[device.Id] = DateTime.UtcNow;
        if (records.Count == 0) return;

        await StoreAsync(device.Id, records, ct, fromHistory: true);
    }

    /// <summary>
    /// Guarda lo que trajo cualquiera de las dos vías y lo empuja al panel.
    /// Descarta lo repetido: el sondeo y la escucha se pisan a propósito, y
    /// varios firmware reenvían el último evento al reconectar.
    /// </summary>
    /// <param name="lastSerial">Último número que entregó la lectura, contando el ruido descartado.</param>
    /// <param name="fromHistory">Viene del historial (sondeo): solo eso adelanta la marca por número.</param>
    private async Task StoreAsync(int deviceId, IReadOnlyList<AccessEventRecord> records, CancellationToken ct,
        long? lastSerial = null, bool fromHistory = false)
    {
        if (records.Count == 0 && lastSerial is null) return;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var tracked = await db.AccessDevices.Include(d => d.Doors).FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (tracked is null) return;

        bool serialMoved = AdvanceSerial(tracked, records, lastSerial, fromHistory);
        if (records.Count == 0)
        {
            if (serialMoved) await db.SaveChangesAsync(ct);
            return;
        }

        var since = records.Min(r => r.Timestamp).AddSeconds(-1);
        var known = await db.AccessEvents
            .Where(e => e.AccessDeviceId == deviceId && e.Timestamp >= since)
            .Select(e => new { e.Timestamp, e.EmployeeNo, e.MinorType, e.DoorNumber, e.SerialNo })
            .ToListAsync(ct);

        var stored = new List<AccessEvent>();
        foreach (var record in records)
        {
            // Con número de evento, número y hora lo identifican; sin él (equipos
            // que no lo informan, eventos de antes), la hora y lo que pasó.
            if (known.Any(k => k.Timestamp == record.Timestamp &&
                               (record.SerialNo is long serial && k.SerialNo is long knownSerial
                                   ? serial == knownSerial
                                   : k.EmployeeNo == record.EmployeeNo && k.MinorType == record.MinorType &&
                                     k.DoorNumber == record.DoorNumber)))
                continue;

            // Los terminales de UNA puerta (los faciales) a menudo no informan
            // el número de puerta en sus accesos: si el evento es de paso por
            // la puerta, no puede ser de otra.
            int? doorNumber = record.DoorNumber;
            if (doorNumber is null && tracked.Doors.Count == 1 && IsDoorEvent(record.Kind))
                doorNumber = tracked.Doors[0].Number;
            var door = doorNumber is int number
                ? tracked.Doors.FirstOrDefault(d => d.Number == number)
                : null;
            var entity = new AccessEvent
            {
                AccessDeviceId = tracked.Id,
                DeviceName = tracked.Name,
                DoorNumber = doorNumber,
                DoorName = door?.Name,
                Timestamp = record.Timestamp,
                ReceivedAt = DateTime.UtcNow,
                Kind = record.Kind,
                Credential = record.Credential,
                Description = Shorten(record.Description, 512),
                EmployeeNo = record.EmployeeNo,
                PersonName = record.PersonName,
                CardNumber = record.CardNumber,
                MajorType = record.MajorType,
                MinorType = record.MinorType,
                RawJson = record.RawJson,
                SerialNo = record.SerialNo,
            };
            stored.Add(entity);
            db.AccessEvents.Add(entity);
        }

        if (stored.Count == 0)
        {
            if (serialMoved) await db.SaveChangesAsync(ct);
            return;
        }
        await LinkPersonsAsync(db, stored, ct);

        // La marca de agua solo avanza: la escucha en vivo y el sondeo llegan
        // desordenados y retrocederla haría releer lo mismo una y otra vez.
        var ultimo = records.Max(r => r.Timestamp);
        if (tracked.LastEventAt is null || ultimo > tracked.LastEventAt) tracked.LastEventAt = ultimo;
        await db.SaveChangesAsync(ct);

        // Automatizaciones ("acceso denegado fuera de horario → foto y aviso").
        // Resuelto al vuelo: el motor contiene la acción de puertas, que usa
        // el servicio de acceso del que este depende.
        var workflows = scope.ServiceProvider.GetRequiredService<Workflows.WorkflowEngine>();
        var people = await PersonInfoAsync(db, stored.Select(e => e.AccessPersonId), ct);
        foreach (var entity in stored)
        {
            await hub.SendAsync(VmsHubContract.AccessEventReceived,
                ToDto(entity, entity.AccessPersonId is int id ? people.GetValueOrDefault(id) : null),
                s => s.CanViewAccessEvent(entity.AccessDeviceId, entity.DoorNumber), ct);
            workflows.Publish(Workflows.WorkflowTrigger.FromAccessEvent(entity));
        }

        // Un paso, una puerta que se abre o se cierra, o una alarma cambian el
        // estado de la hoja y de la cerradura: se relee ya en vez de esperar
        // el sondeo de un minuto.
        if (stored.Any(e => IsDoorEvent(e.Kind))) access.RequestDoorRefresh(deviceId);
    }

    /// <summary>
    /// Mueve la marca por número de evento. Devuelve true si cambió.
    ///
    /// <list type="bullet">
    /// <item>Avanza solo con lo que trajo el historial: un evento en vivo con un
    /// número alto, después de un corte de la escucha, saltaría lo perdido. Lo
    /// en vivo se guarda igual y el sondeo lo relee (se descarta repetido).</item>
    /// <item>Retrocede si el contador del equipo volvió a empezar (se restableció o
    /// es otro equipo en la misma dirección): llega un evento MÁS NUEVO que la
    /// marca de hora con un número que ya se había pasado. Retroceder siempre
    /// es seguro: a lo sumo se relee algo y se descarta.</item>
    /// </list>
    /// </summary>
    private bool AdvanceSerial(AccessDevice device, IReadOnlyList<AccessEventRecord> records, long? lastSerial,
        bool fromHistory)
    {
        if (device.LastEventSerial is long current && device.LastEventAt is DateTime lastAt)
        {
            var restarted = records
                .Where(r => r.SerialNo is long serial && serial < current && r.Timestamp > lastAt)
                .Select(r => r.SerialNo!.Value)
                .ToList();
            if (restarted.Count > 0)
            {
                device.LastEventSerial = restarted.Min() - 1;
                logger.LogInformation(
                    "El equipo {Device} volvió a numerar sus eventos desde {Serial} (antes iba en {Previous}): " +
                    "se sigue desde ahí.", device.Name, restarted.Min(), current);
                return true;
            }
        }

        if (!fromHistory) return false;
        long? newest = new[] { lastSerial, records.Max(r => r.SerialNo) }.Max();
        if (newest is not long top || device.LastEventSerial is long known && top <= known) return false;
        device.LastEventSerial = top;
        return true;
    }

    private static bool IsDoorEvent(AccessEventKind kind) =>
        kind is AccessEventKind.Granted or AccessEventKind.Denied or AccessEventKind.DoorOpen
            or AccessEventKind.DoorClose or AccessEventKind.Alarm;

    /// <summary>Lo que el monitoreo muestra de la persona junto al evento (área, cargo, si tiene foto).</summary>
    public sealed record PersonInfo(string? Department, string? Position, bool HasFace);

    public static async Task<Dictionary<int, PersonInfo>> PersonInfoAsync(VmsDbContext db, IEnumerable<int?> ids,
        CancellationToken ct)
    {
        var wanted = ids.OfType<int>().Distinct().ToList();
        if (wanted.Count == 0) return [];
        return await db.AccessPersons.AsNoTracking()
            .Where(p => wanted.Contains(p.Id))
            .Select(p => new { p.Id, p.Department, p.Position, HasFace = p.Face != null })
            .ToDictionaryAsync(p => p.Id, p => new PersonInfo(p.Department, p.Position, p.HasFace), ct);
    }

    /// <summary>
    /// Ata los eventos a la persona del padrón que corresponda. El equipo
    /// devuelve el identificador con el que se la escribió, así que el nombre
    /// que se muestra es el del VMS y no el que quedó grabado en el equipo
    /// (que puede estar viejo o venir recortado).
    /// </summary>
    private static async Task LinkPersonsAsync(VmsDbContext db, List<AccessEvent> events, CancellationToken ct)
    {
        var employeeNos = events.Select(e => e.EmployeeNo).OfType<string>().Distinct().ToList();
        var byCard = events.Where(e => e.EmployeeNo is null).Select(e => e.CardNumber).OfType<string>().Distinct().ToList();

        var persons = employeeNos.Count == 0 ? [] : await db.AccessPersons
            .Where(p => employeeNos.Contains(p.EmployeeNo))
            .Select(p => new { p.Id, p.EmployeeNo, p.FirstName, p.LastName })
            .ToListAsync(ct);
        // Un equipo que solo informa la tarjeta (o una persona cargada a mano
        // en el equipo) igual se reconoce si esa tarjeta está en el padrón.
        var cards = byCard.Count == 0 ? [] : await db.AccessCards
            .Where(c => byCard.Contains(c.Number))
            .Select(c => new { c.Number, c.AccessPersonId, c.AccessPerson!.EmployeeNo, c.AccessPerson.FirstName, c.AccessPerson.LastName })
            .ToListAsync(ct);

        foreach (var evt in events)
        {
            if (evt.EmployeeNo is { } employeeNo &&
                persons.FirstOrDefault(p => p.EmployeeNo == employeeNo) is { } person)
            {
                evt.AccessPersonId = person.Id;
                evt.PersonName = $"{person.FirstName} {person.LastName}".Trim();
            }
            else if (evt.CardNumber is { } card && cards.FirstOrDefault(c => c.Number == card) is { } byNumber)
            {
                evt.AccessPersonId = byNumber.AccessPersonId;
                evt.EmployeeNo ??= byNumber.EmployeeNo;
                evt.PersonName = $"{byNumber.FirstName} {byNumber.LastName}".Trim();
            }
        }
    }

    /// <summary>Borra el historial más viejo que la retención configurada, una vez al día.</summary>
    private async Task PurgeAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastPurge < TimeSpan.FromHours(24)) return;
        _lastPurge = DateTime.UtcNow;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        int removed = await db.AccessEvents.Where(e => e.Timestamp < cutoff).ExecuteDeleteAsync(ct);
        if (removed > 0)
            logger.LogInformation("Historial de accesos: {Count} evento(s) más viejos que {Days} días eliminados.",
                removed, RetentionDays);
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max];

    public static AccessEventDto ToDto(AccessEvent e) => ToDto(e, null);

    public static AccessEventDto ToDto(AccessEvent e, PersonInfo? person) => new(
        e.Id, e.AccessDeviceId, e.DeviceName, e.DoorNumber, e.DoorName, e.Timestamp, e.ReceivedAt,
        e.Kind, e.Credential, e.Description, e.EmployeeNo, e.PersonName, e.AccessPersonId, e.CardNumber,
        person?.Department, person?.Position, person?.HasFace ?? false);
}
