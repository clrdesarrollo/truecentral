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
/// apagado) igual se recupera lo que se perdió.</item>
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
    IHubContext<VmsHub> hub,
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
                logger.LogInformation("Escuchando los eventos de '{Device}' en vivo.", device.Name);
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
        var since = device.LastEventAt ?? DateTime.UtcNow - FirstReadWindow;
        IReadOnlyList<AccessEventRecord> records;
        try { records = await access.DriverOf(device).FetchEventsAsync(access.ConnectionOf(device), since, MaxPerPoll, ct); }
        catch (DriverException ex)
        {
            logger.LogDebug(ex, "El equipo {Device} no entregó su historial.", device.Name);
            return;
        }
        if (records.Count == 0) return;

        await StoreAsync(device.Id, records, ct);
    }

    /// <summary>
    /// Guarda lo que trajo cualquiera de las dos vías y lo empuja al panel.
    /// Descarta lo repetido: el sondeo y la escucha se pisan a propósito, y
    /// varios firmware reenvían el último evento al reconectar.
    /// </summary>
    private async Task StoreAsync(int deviceId, IReadOnlyList<AccessEventRecord> records, CancellationToken ct)
    {
        if (records.Count == 0) return;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var tracked = await db.AccessDevices.Include(d => d.Doors).FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (tracked is null) return;

        var since = records.Min(r => r.Timestamp).AddSeconds(-1);
        var known = await db.AccessEvents
            .Where(e => e.AccessDeviceId == deviceId && e.Timestamp >= since)
            .Select(e => new { e.Timestamp, e.EmployeeNo, e.MinorType, e.DoorNumber })
            .ToListAsync(ct);

        var stored = new List<AccessEvent>();
        foreach (var record in records)
        {
            if (known.Any(k => k.Timestamp == record.Timestamp && k.EmployeeNo == record.EmployeeNo &&
                               k.MinorType == record.MinorType && k.DoorNumber == record.DoorNumber))
                continue;

            var door = record.DoorNumber is int number
                ? tracked.Doors.FirstOrDefault(d => d.Number == number)
                : null;
            var entity = new AccessEvent
            {
                AccessDeviceId = tracked.Id,
                DeviceName = tracked.Name,
                DoorNumber = record.DoorNumber,
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
            };
            stored.Add(entity);
            db.AccessEvents.Add(entity);
        }

        if (stored.Count == 0) return;
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
        foreach (var entity in stored)
        {
            await hub.Clients.All.SendAsync(VmsHubContract.AccessEventReceived, ToDto(entity), ct);
            workflows.Publish(Workflows.WorkflowTrigger.FromAccessEvent(entity));
        }
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

    public static AccessEventDto ToDto(AccessEvent e) => new(
        e.Id, e.AccessDeviceId, e.DeviceName, e.DoorNumber, e.DoorName, e.Timestamp, e.ReceivedAt,
        e.Kind, e.Credential, e.Description, e.EmployeeNo, e.PersonName, e.AccessPersonId, e.CardNumber);
}
