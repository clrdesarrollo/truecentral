using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services.Supervisor;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Trae los accesos de los equipos y los deja en la base del VMS, por dos vías
/// que se complementan:
///
/// <list type="number">
/// <item><b>En vivo</b>: a los equipos que saben empujar sus eventos se les
/// mantiene abierta una escucha, y lo que pasa en la puerta aparece en el
/// monitoreo en el momento. Es la vía normal.</item>
/// <item><b>Sondeo de respaldo</b>: se le pregunta a cada equipo qué pasó desde
/// su MARCA DE AGUA. Cada 15 s a los que no tienen una escucha sana; a los que
/// sí, cada <c>Access:LivePollMinutes</c> (5) y además enseguida al reconectar
/// la escucha o al faltar un número de evento entre dos que llegaron. Cubre a
/// los equipos que no empujan, y tapa los huecos de los que sí: mientras la
/// escucha estaba caída (o el servidor apagado) igual se recupera lo que se
/// perdió, sin cargar con consultas a los equipos que ya avisan solos. La marca es el número de
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
    ILogger<AccessEventService> logger) : SupervisedBackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    /// <summary>Eventos que se traen de un equipo por vuelta (un equipo con atraso se pone al día en varias).</summary>
    private const int MaxPerPoll = 200;

    /// <summary>
    /// Cuánto hacia atrás se lee, como mucho, la primera vez que se consulta a
    /// un equipo (se parte de su alta, ver <see cref="FirstReadSince"/>). Con
    /// tope a propósito: un terminal puede tener años de historial guardado y
    /// no corresponde volcarlo entero al VMS por haberlo dado de alta.
    /// </summary>
    private static readonly TimeSpan FirstReadWindow = TimeSpan.FromHours(24);

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

    // ------------------------------------------------------------------
    // Ritmo del sondeo según la salud de la escucha en vivo
    // ------------------------------------------------------------------

    /// <summary>Cuándo llegó algo por última vez de la escucha de cada equipo (evento o latido).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> _liveAlive = new();

    /// <summary>Cuándo se leyó por última vez el historial de cada equipo.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> _lastPoll = new();

    /// <summary>Equipos cuyo historial se pidió leer cuanto antes (reconexión o hueco de numeración).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> _pollRequested = new();

    /// <summary>Despierta la vuelta del sondeo antes de tiempo.</summary>
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>
    /// Una escucha está sana si llegó algo en este lapso: el driver corta la que
    /// calla 120 s, así que pasado esto ya no se le puede creer.
    /// </summary>
    private static readonly TimeSpan LiveFreshness = TimeSpan.FromSeconds(150);

    /// <summary>Mínimo entre dos lecturas del mismo equipo pedidas por la escucha (un equipo con muchos huecos no lo satura).</summary>
    private static readonly TimeSpan MinRequestSpacing = TimeSpan.FromSeconds(10);

    /// <summary>Mínimo entre dos vueltas del sondeo, aunque lo despierten seguido.</summary>
    private static readonly TimeSpan MinLoopGap = TimeSpan.FromSeconds(2);

    /// <summary>Respaldo de los equipos con escucha sana (<c>Access:LivePollMinutes</c>, 5 por omisión).</summary>
    private TimeSpan LivePollInterval =>
        TimeSpan.FromMinutes(Math.Clamp(config.GetValue("Access:LivePollMinutes", 5), 1, 60));

    private bool IsLive(int deviceId, DateTime now) =>
        _liveAlive.TryGetValue(deviceId, out var alive) && now - alive < LiveFreshness;

    /// <summary>¿Le toca leer el historial a este equipo en esta vuelta?</summary>
    private bool IsDue(int deviceId, DateTime now)
    {
        var since = _lastPoll.TryGetValue(deviceId, out var last) ? now - last : TimeSpan.MaxValue;
        if (_pollRequested.ContainsKey(deviceId)) return since >= MinRequestSpacing;
        // Las vueltas pueden adelantarse (las despierta otro equipo): sin escucha
        // sana se sigue leyendo cada 15 s, no más seguido.
        return since >= (IsLive(deviceId, now) ? LivePollInterval : PollInterval - MinLoopGap);
    }

    /// <summary>Pide leer el historial del equipo en la próxima vuelta, que se adelanta.</summary>
    private void RequestPoll(int deviceId)
    {
        _pollRequested[deviceId] = DateTime.UtcNow;
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { /* ya estaba despierto */ }
    }

    private void OnStreamSignal(int deviceId, string deviceName, AccessStreamSignal signal)
    {
        switch (signal)
        {
            case AccessStreamSignal.Connected:
                _liveAlive[deviceId] = DateTime.UtcNow;
                // Lo que pasó mientras la escucha estaba caída se trae ya.
                RequestPoll(deviceId);
                break;
            case AccessStreamSignal.Alive:
                _liveAlive[deviceId] = DateTime.UtcNow;
                break;
            case AccessStreamSignal.Gap:
                logger.LogDebug("Faltó un número de evento en la escucha de {Device}: se lee el historial.", deviceName);
                RequestPoll(deviceId);
                break;
        }
    }

    private DateTime _lastPurge = DateTime.MinValue;

    /// <summary>
    /// Escuchas abiertas, por equipo: para no abrir dos al mismo, y con su
    /// propio corte para soltar al instante la de un equipo al que se le apaga
    /// la escucha en vivo (una escucha sana no se cierra nunca sola).
    /// </summary>
    private readonly Dictionary<int, (Task Task, CancellationTokenSource Stop)> _listeners = [];

    private int RetentionDays => Math.Clamp(config.GetValue("Access:EventRetentionDays", 365), 7, 3650);

    protected override async Task RunAsync(CancellationToken stoppingToken)
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

            // La vuelta siguiente, o antes si la escucha pidió leer un equipo.
            try
            {
                await Task.Delay(MinLoopGap, stoppingToken);
                await _wake.WaitAsync(PollInterval - MinLoopGap, stoppingToken);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Abre una escucha por cada equipo que sepa empujar eventos y todavía no
    /// tenga una. Se llama en cada vuelta del sondeo, así que un equipo que se
    /// da de alta —o que vuelve después de una caída— queda escuchado sin
    /// ceremonia. A los que tienen la escucha en vivo apagada se les corta la
    /// que tuvieran: el equipo queda libre para otro sistema y sigue entrando
    /// por el sondeo de cada 15 s.
    /// </summary>
    private void EnsureListeners(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var devices = db.AccessDevices.AsNoTracking()
            .Where(d => d.Enabled && d.SupportsEvents)
            .Select(d => new { d.Id, d.Name, d.DriverKey, d.LiveEvents })
            .ToList();

        foreach (var device in devices)
        {
            bool running = _listeners.TryGetValue(device.Id, out var listener) && !listener.Task.IsCompleted;
            if (!device.LiveEvents)
            {
                if (running)
                {
                    listener.Stop.Cancel();
                    logger.LogInformation("Se suelta la escucha en vivo de '{Device}': queda solo con la lectura del historial.", device.Name);
                }
                continue;
            }
            if (running || !access.SupportsEventStream(device.DriverKey)) continue;
            listener.Stop?.Dispose();
            var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _listeners[device.Id] = (Task.Run(() => ListenAsync(device.Id, stop.Token), stop.Token), stop);
        }

        // Los equipos que se dieron de baja o se pausaron dejan de escucharse
        // solos: su tarea termina al fallar la conexión y no se vuelve a crear.
        foreach (var (id, listener) in _listeners.Where(p => p.Value.Task.IsCompleted).ToList())
            if (devices.All(d => d.Id != id || !d.LiveEvents))
            {
                listener.Stop.Dispose();
                _listeners.Remove(id);
            }
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
            if (device is null || !device.Enabled || !device.SupportsEvents || !device.LiveEvents) return;

            try
            {
                // El driver corta la escucha que queda en silencio (sin latido)
                // para reconectar: esas vueltas sanas no ensucian el registro.
                logger.Log(primera || fallos > 0 ? LogLevel.Information : LogLevel.Debug,
                    "Escuchando los eventos de '{Device}' en vivo.", device.Name);
                primera = false;
                string name = device.Name;
                await foreach (var record in access.DriverOf(device).StreamEventsAsync(access.ConnectionOf(device),
                                   s => OnStreamSignal(deviceId, name, s), ct))
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
            finally
            {
                // Sin escucha, el equipo vuelve al sondeo de cada 15 s.
                _liveAlive.TryRemove(deviceId, out _);
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
        var now = DateTime.UtcNow;
        devices = devices.Where(d => IsDue(d.Id, now)).ToList();
        if (devices.Count == 0) return;
        foreach (var device in devices)
        {
            _lastPoll[device.Id] = now;
            _pollRequested.TryRemove(device.Id, out _);
        }

        using var limiter = new SemaphoreSlim(Parallelism);
        await Task.WhenAll(devices.Select(async device =>
        {
            await limiter.WaitAsync(ct);
            var gate = GateOf(device.Id);
            await gate.WaitAsync(ct);
            try { await PollOneAsync(device, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "No se pudo leer el historial del equipo {Device}.", device.Name);
            }
            finally
            {
                gate.Release();
                limiter.Release();
            }
        }));
    }

    /// <summary>
    /// Una lectura del historial por equipo a la vez: la relectura que pide un
    /// administrador (<see cref="RereadAsync"/>) retrocede la marca, y una
    /// lectura en curso con la marca vieja la volvería a adelantar.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> _gates = new();

    private SemaphoreSlim GateOf(int deviceId) => _gates.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));

    private async Task PollOneAsync(AccessDevice device, CancellationToken ct)
    {
        var driver = access.DriverOf(device);
        var connection = access.ConnectionOf(device);

        // Las marcas se releen ya con el equipo tomado: la lista de la vuelta
        // pudo leerse antes de una relectura.
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var marks = await db.AccessDevices.AsNoTracking().Where(d => d.Id == device.Id)
                .Select(d => new { d.LastEventSerial, d.LastEventAt }).FirstOrDefaultAsync(ct);
            if (marks is null) return;
            device.LastEventSerial = marks.LastEventSerial;
            device.LastEventAt = marks.LastEventAt;
        }

        // Por número de evento, si ya se conoce uno y el equipo sabe buscar así
        // (el driver devuelve null si no). La marca avanza hasta donde el driver
        // leyó sin huecos, contando el ruido descartado.
        bool bySerial = false;
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
                bySerial = true;
                await StoreAsync(device.Id, page.Events, ct, page.LastSerial, fromHistory: true);
                if (_lastTimeCheck.TryGetValue(device.Id, out var checkedAt) && DateTime.UtcNow - checkedAt < TimeCheckInterval)
                    return;
            }
        }

        // Por hora: los equipos que no buscan por número, la primera lectura
        // (de ahí sale el primer número) y el control periódico de arriba.
        var since = device.LastEventAt ?? FirstReadSince(device);
        AccessEventPage history;
        try { history = await driver.FetchEventsSinceAsync(connection, since, MaxPerPoll, ct); }
        catch (DriverException ex)
        {
            logger.LogDebug(ex, "El equipo {Device} no entregó su historial.", device.Name);
            return;
        }
        _lastTimeCheck[device.Id] = DateTime.UtcNow;

        // La primera vez, el número más alto de lo anterior a la marca fija la
        // marca por número aunque no haya llegado nada nuevo: desde ahí sigue la
        // lectura por número, que no depende de la hora del equipo. Después, en
        // un equipo que se lee por número, esta lectura NO mueve esa marca: la
        // dejaría por encima de eventos del mismo segundo, o escritos tarde, que
        // la lectura por número todavía no entregó.
        long? newest = history.Events.Max(e => e.SerialNo);
        long? advanceTo = device.LastEventSerial is null ? new[] { history.LastSerial, newest }.Max()
            : bySerial ? null : newest;
        if (history.Events.Count == 0 && advanceTo is null) return;

        await StoreAsync(device.Id, history.Events, ct, advanceTo, fromHistory: true);
    }

    /// <summary>
    /// Desde cuándo se lee la primera vez: desde el alta del equipo, para que
    /// no se pierda lo que pasó entre el alta y la primera lectura que funcionó
    /// (un equipo que no entregaba su historial, o un servidor actualizado
    /// después). Con un tope, para no volcar años de historial a un equipo que
    /// se dio de alta hace mucho.
    /// </summary>
    private static DateTime FirstReadSince(AccessDevice device)
    {
        var floor = DateTime.UtcNow - FirstReadWindow;
        var created = DateTime.SpecifyKind(device.CreatedAt, DateTimeKind.Utc).AddMinutes(-1);
        return created > floor ? created : floor;
    }

    /// <summary>
    /// Guarda lo que trajo cualquiera de las dos vías y lo empuja al panel.
    /// Descarta lo repetido: el sondeo y la escucha se pisan a propósito, y
    /// varios firmware reenvían el último evento al reconectar.
    /// </summary>
    /// <param name="advanceSerialTo">Hasta dónde avanza la marca por número (nunca retrocede por esto); null = no se mueve.</param>
    /// <param name="fromHistory">Viene del historial (sondeo o relectura), no de la escucha en vivo.</param>
    private async Task StoreAsync(int deviceId, IReadOnlyList<AccessEventRecord> records, CancellationToken ct,
        long? advanceSerialTo = null, bool fromHistory = false)
    {
        if (records.Count == 0 && advanceSerialTo is null) return;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var tracked = await db.AccessDevices.Include(d => d.Doors).FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (tracked is null) return;

        bool serialMoved = AdvanceSerial(tracked, records, advanceSerialTo);
        if (records.Count == 0)
        {
            if (serialMoved) await db.SaveChangesAsync(ct);
            return;
        }

        var since = records.Min(r => r.Timestamp).AddSeconds(-1);
        var known = await db.AccessEvents
            .Where(e => e.AccessDeviceId == deviceId && e.Timestamp >= since)
            .Select(e => new { e.Id, e.Timestamp, e.EmployeeNo, e.MinorType, e.DoorNumber, e.SerialNo, e.Kind })
            .ToListAsync(ct);

        var stored = new List<AccessEvent>();
        var relabel = new Dictionary<long, AccessEventRecord>();
        foreach (var record in records)
        {
            // Con número de evento, número y hora lo identifican; sin él (equipos
            // que no lo informan, eventos de antes), la hora y lo que pasó.
            var repeated = known.FirstOrDefault(k => k.Timestamp == record.Timestamp &&
                               (record.SerialNo is long serial && k.SerialNo is long knownSerial
                                   ? serial == knownSerial
                                   : k.EmployeeNo == record.EmployeeNo && k.MinorType == record.MinorType &&
                                     k.DoorNumber == record.DoorNumber));
            if (repeated is not null)
            {
                // Un código que el driver no entendía cuando se guardó (quedó
                // como "Otro") y ahora sí: la relectura lo corrige.
                if (fromHistory && repeated.Kind == AccessEventKind.Other && record.Kind != AccessEventKind.Other)
                    relabel[repeated.Id] = record;
                continue;
            }

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

        if (relabel.Count > 0)
        {
            foreach (var row in await db.AccessEvents.Where(e => relabel.Keys.Contains(e.Id)).ToListAsync(ct))
            {
                var record = relabel[row.Id];
                row.Kind = record.Kind;
                row.Credential = record.Credential;
                row.Description = Shorten(record.Description, 512);
            }
            logger.LogInformation("{Device}: {Count} evento(s) guardados como «Otro» quedaron con su descripción.",
                tracked.Name, relabel.Count);
        }

        // La marca de agua solo avanza: la escucha en vivo y el sondeo llegan
        // desordenados y retrocederla haría releer lo mismo una y otra vez. Lo
        // leído del historial la mueve aunque sea todo repetido (una relectura
        // por hora no puede quedarse leyendo lo mismo).
        var ultimo = records.Max(r => r.Timestamp);
        bool timeMoved = (stored.Count > 0 || fromHistory) && (tracked.LastEventAt is null || ultimo > tracked.LastEventAt);
        if (timeMoved) tracked.LastEventAt = ultimo;

        if (stored.Count == 0)
        {
            if (serialMoved || timeMoved || relabel.Count > 0) await db.SaveChangesAsync(ct);
            return;
        }
        await LinkPersonsAsync(db, stored, ct);
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
    /// <item>Avanza solo hasta donde diga quien leyó el historial
    /// (<paramref name="advanceTo"/>), no hasta el número más alto que llegó: un
    /// evento en vivo con un número alto, después de un corte de la escucha, o
    /// uno del historial que llegó después de un hueco, saltarían lo que falta.
    /// Esos se guardan igual y se releen (se descartan repetidos).</item>
    /// <item>Retrocede si el contador del equipo volvió a empezar (se restableció o
    /// es otro equipo en la misma dirección): llega un evento MÁS NUEVO que la
    /// marca de hora con un número que ya se había pasado. Retroceder siempre
    /// es seguro: a lo sumo se relee algo y se descarta.</item>
    /// </list>
    /// </summary>
    private bool AdvanceSerial(AccessDevice device, IReadOnlyList<AccessEventRecord> records, long? advanceTo)
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

        if (advanceTo is not long top || device.LastEventSerial is long known && top <= known) return false;
        device.LastEventSerial = top;
        return true;
    }

    // ------------------------------------------------------------------
    // Relectura a pedido
    // ------------------------------------------------------------------

    /// <summary>Resultado de <see cref="RereadAsync"/>, para la respuesta y la bitácora.</summary>
    public sealed record RereadResult(bool Started, string Message, long? PreviousSerial, long? FromSerial,
        DateTime? FromTime);

    /// <summary>
    /// Vuelve a leer el historial de un equipo desde el evento número
    /// <paramref name="fromSerial"/> o desde <paramref name="fromUtc"/>: sirve
    /// para recuperar lo que una versión anterior se saltó. Solo retrocede la
    /// marca; la lectura la hace el sondeo de siempre, que lo trae a su ritmo
    /// (200 eventos por vuelta) y descarta lo que ya estaba.
    ///
    /// Desde una fecha se busca primero, por hora, el primer evento desde ahí, y
    /// se sigue por número desde él (no pierde nada del mismo segundo ni lo
    /// escrito tarde). Un equipo que no numera sus eventos se relee por hora.
    /// </summary>
    public async Task<RereadResult> RereadAsync(int deviceId, long? fromSerial, DateTime? fromUtc, CancellationToken ct)
    {
        var gate = GateOf(deviceId);
        await gate.WaitAsync(ct);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var device = await db.AccessDevices.Include(d => d.Doors).FirstOrDefaultAsync(d => d.Id == deviceId, ct)
                         ?? throw new InvalidOperationException("El equipo ya no existe.");
            long? previous = device.LastEventSerial;

            if (fromSerial is long serial)
            {
                if (previous is not long current)
                    return new(false, "Este equipo todavía no se lee por número de evento: indique una fecha.", previous, null, null);
                if (serial - 1 >= current)
                    return new(false, $"El historial ya se leyó hasta el evento {current}: no hay nada que releer desde el {serial}.",
                        previous, null, null);
                device.LastEventSerial = serial - 1;
                await db.SaveChangesAsync(ct);
                RequestPoll(deviceId);
                return new(true, $"Se vuelve a leer desde el evento {serial} (iba en el {current}).", previous, serial, null);
            }

            var since = DateTime.SpecifyKind(fromUtc!.Value, DateTimeKind.Utc);
            AccessEventPage first;
            try
            {
                first = await access.DriverOf(device).FetchEventsSinceAsync(access.ConnectionOf(device), since.AddSeconds(-1), 1, ct);
            }
            catch (DriverException ex)
            {
                return new(false, $"El equipo no entregó su historial: {ex.Message}", previous, null, since);
            }

            if (first.Events.Count == 0)
                return new(false, "El equipo no tiene eventos desde esa fecha.", previous, null, since);
            if (first.Events[0].SerialNo is long found && previous is not null)
            {
                if (found - 1 >= previous)
                    return new(false, $"El primer evento desde esa fecha es el {found} y el historial ya se leyó hasta el {previous}: " +
                                      "no hay nada que releer.", previous, null, since);
                device.LastEventSerial = found - 1;
                await db.SaveChangesAsync(ct);
                RequestPoll(deviceId);
                return new(true, $"Se vuelve a leer desde el evento {found}, el primero desde esa fecha (iba en el {previous}).",
                    previous, found, since);
            }

            // Sin número de evento: por hora, desde esa fecha. Sin marca por número
            // la primera lectura vuelve a fijarla, y no se toma lo releído por un
            // contador que volvió a empezar.
            device.LastEventSerial = null;
            device.LastEventAt = since.AddSeconds(-1);
            await db.SaveChangesAsync(ct);
            RequestPoll(deviceId);
            return new(true, "Se vuelve a leer por hora desde esa fecha.", previous, null, since);
        }
        finally { gate.Release(); }
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
