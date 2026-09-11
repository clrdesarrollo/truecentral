using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Módulo Paneles de alarma: por cada panel habilitado mantiene abierto el
/// canal de eventos del equipo y sondea su estado (áreas y zonas) de forma
/// periódica y también apenas llega un evento. Todo cambio de estado se
/// persiste y se empuja a los clientes por SignalR; los eventos se guardan en
/// el historial y las alarmas quedan además en la bitácora de auditoría.
///
/// Los eventos del panel llegan en un hilo del driver que no se debe
/// bloquear: el manejador solo encola y un consumidor propio persiste y
/// avisa. Además del canal de eventos, el sondeo detecta por diferencia los
/// cambios que el panel no empujó (o que llegaron en un formato que el driver
/// no entendió), de modo que el estado que ve el operador siempre es el real.
/// </summary>
public sealed class AlarmPanelService(
    IServiceScopeFactory scopeFactory,
    AlarmDriverRegistry drivers,
    CredentialProtector credentials,
    IHubContext<VmsHub> hub,
    AuditService audit,
    Workflows.WorkflowEngine workflows,
    IConfiguration config,
    ILogger<AlarmPanelService> logger) : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(20);
    /// <summary>Espera tras un fallo de red antes de reintentar.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);
    /// <summary>Espera tras credenciales rechazadas: insistir bloquea la cuenta en el panel.</summary>
    private static readonly TimeSpan AuthRetryDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(6);
    /// <summary>Ventana en la que un cambio detectado por sondeo se considera ya informado por el panel.</summary>
    private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(15);

    private sealed class Worker
    {
        public IAlarmSubscription? Subscription;
        public string Fingerprint = "";
        public string? LastError;
        public DateTime NextRetryAt = DateTime.MinValue;
        public DateTime NextPollAt = DateTime.MinValue;
        public DateTime LastActivityAt = DateTime.MinValue;
        public bool PollRequested;
        /// <summary>Huella del último estado publicado: con sondeo rápido, no se
        /// empuja por el hub una foto idéntica cada dos segundos.</summary>
        public string StateSignature = "";
        public readonly SemaphoreSlim PollLock = new(1, 1);
        /// <summary>Cambios recién informados por el panel: (clave) → momento; evita duplicarlos desde el sondeo.</summary>
        public readonly ConcurrentDictionary<string, DateTime> RecentChanges = new();
        /// <summary>Órdenes recién dadas desde el VMS: el eco que el panel empuja por su canal no se duplica en el historial.</summary>
        public readonly ConcurrentDictionary<string, DateTime> RecentVmsCommands = new();

        /// <summary>true si el cambio ya fue informado por el panel (o por una
        /// orden del VMS) dentro de la ventana de deduplicación; consume la marca.</summary>
        public bool WasRecentlyReported(string key, DateTime now)
        {
            foreach (var pair in RecentChanges)
                if (now - pair.Value > DedupWindow) RecentChanges.TryRemove(pair.Key, out _);
            return RecentChanges.TryRemove(key, out var at) && now - at <= DedupWindow;
        }
    }

    private readonly ConcurrentDictionary<int, Worker> _workers = new();

    private readonly Channel<(int PanelId, AlarmPanelEvent Event)> _incoming =
        System.Threading.Channels.Channel.CreateBounded<(int, AlarmPanelEvent)>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>
    /// Paneles que hay que sondear rápido porque alguna automatización vigila
    /// que un sensor SIGA interrumpido: con el sondeo normal (30 s) una
    /// interrupción de 5 s pasaría entera entre dos lecturas. Lo fija el motor
    /// de automatizaciones (<c>WorkflowEngine</c>) cada vez que cambia su
    /// configuración; sin automatizaciones de ese tipo nadie paga el costo.
    /// </summary>
    private volatile IReadOnlySet<int> _fastPollPanels = new HashSet<int>();
    private volatile bool _fastPollAll;

    /// <summary>Fija qué paneles se sondean rápido (all = todos los habilitados).</summary>
    public void SetFastPoll(bool all, IReadOnlySet<int> panelIds)
    {
        bool changed = _fastPollAll != all || !_fastPollPanels.SetEquals(panelIds);
        _fastPollAll = all;
        _fastPollPanels = panelIds;
        if (changed)
        {
            logger.LogInformation("Sondeo rápido de paneles de alarma: {Scope}.",
                all ? "todos" : panelIds.Count == 0 ? "ninguno" : string.Join(", ", panelIds));
            RequestReconcile();
        }
    }

    private bool IsFastPolled(int panelId) => _fastPollAll || _fastPollPanels.Contains(panelId);

    private bool AnyFastPoll => _fastPollAll || _fastPollPanels.Count > 0;

    private int FastPollSeconds => Math.Clamp(config.GetValue("Alarms:FastPollSeconds", 2), 1, 30);

    // ------------------------------------------------------------------
    // Estado consultable por la API
    // ------------------------------------------------------------------

    public bool IsLive(int panelId) =>
        _workers.TryGetValue(panelId, out var w) && w.Subscription is { IsAlive: true };

    public string? LastErrorOf(int panelId) =>
        _workers.TryGetValue(panelId, out var w) ? w.LastError : null;

    /// <summary>Reconcilia ya (tras crear/editar/eliminar un panel).</summary>
    public void RequestReconcile()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    /// <summary>
    /// El panel empujó una notificación HTTP al servidor ("HTTP Host
    /// Notification"). Si el driver la entendió, entra al historial como
    /// evento del panel (fuente "panel"); en cualquier caso se relee el estado
    /// de inmediato, que es la fuente autoritativa. Así el tiempo real no
    /// depende del alertStream, que en el AX Hybrid PRO se abre pero calla.
    /// </summary>
    public void NotifyPushed(int panelId, AlarmPanelEvent? evt)
    {
        var worker = _workers.GetOrAdd(panelId, _ => new Worker());
        worker.LastActivityAt = DateTime.UtcNow;
        if (evt is not null)
        {
            Enqueue(panelId, evt); // encola, pide el sondeo y despierta el ciclo
            return;
        }
        worker.PollRequested = true;
        RequestReconcile();
    }

    /// <summary>El panel dio señales de vida (latido del centro receptor DC-09) sin cambio de estado.</summary>
    public void NotifyAlive(int panelId)
    {
        if (_workers.TryGetValue(panelId, out var worker))
            worker.LastActivityAt = DateTime.UtcNow;
    }

    /// <summary>Suelta el canal de eventos y olvida la conexión cacheada del panel.</summary>
    public async Task DetachAsync(int panelId)
    {
        if (!_workers.TryRemove(panelId, out var worker)) return;
        await CloseAsync(panelId, worker);
    }

    // ------------------------------------------------------------------
    // Órdenes del operador (con refresco inmediato del estado)
    // ------------------------------------------------------------------

    /// <summary>
    /// Ejecuta una orden contra el panel (armar, desarmar, anular...) y
    /// vuelve a leer su estado para que la respuesta ya traiga el resultado.
    /// Lanza <see cref="DriverException"/> si el panel rechaza la orden.
    /// </summary>
    public async Task<AlarmPanelDto> ExecuteAsync(AlarmPanel panel, Func<IAlarmPanelDriver, AlarmConnectionInfo, Task> command,
        AlarmEventKind kind, string description, int? areaNumber, int? zoneNumber, string operatorName,
        CancellationToken ct)
    {
        var factory = drivers.Find(panel.DriverKey)
                      ?? throw new DriverException($"Driver '{panel.DriverKey}' no disponible.");
        var connection = ConnectionOf(panel);

        // La orden sale del VMS: queda en el historial con el operador, y se
        // marca ANTES de enviarla para que ni el sondeo ni el eco que el
        // panel empuja por su canal (que puede llegar antes de que el PUT
        // termine) la dupliquen. Si el panel la rechaza, las marcas se retiran.
        var worker = _workers.GetOrAdd(panel.Id, _ => new Worker());
        var now = DateTime.UtcNow;
        var marks = new List<string>();
        switch (kind)
        {
            case AlarmEventKind.Arm when areaNumber is { } a1 and > 0: marks.Add($"area:{a1}:arm:True"); break;
            case AlarmEventKind.Disarm when areaNumber is { } a2 and > 0: marks.Add($"area:{a2}:arm:False"); break;
            case AlarmEventKind.Arm or AlarmEventKind.Disarm:
                marks.AddRange(panel.Areas.Select(area => $"area:{area.Number}:arm:{kind == AlarmEventKind.Arm}"));
                break;
            case AlarmEventKind.Bypass when zoneNumber is { } z:
                marks.Add($"zone:{z}:bypass:{!description.Contains("restitu", StringComparison.OrdinalIgnoreCase)}");
                break;
            case AlarmEventKind.Info:
                // Borrar/silenciar la alarma: el sondeo verá las alarmas de
                // área y zona apagarse; ya queda documentado por esta orden.
                foreach (var area in panel.Areas.Where(a => areaNumber is null or 0 || a.Number == areaNumber))
                {
                    marks.Add($"area:{area.Number}:alarm:False");
                    marks.AddRange(panel.Zones.Where(z => z.AreaNumber == area.Number).Select(z => $"zone:{z.Number}:alarm:False"));
                }
                break;
        }
        foreach (var mark in marks)
        {
            worker.RecentChanges[mark] = now;
            worker.RecentVmsCommands[mark] = now;
        }
        try
        {
            await command(factory.Create(), connection);
        }
        catch
        {
            foreach (var mark in marks)
            {
                worker.RecentChanges.TryRemove(mark, out _);
                worker.RecentVmsCommands.TryRemove(mark, out _);
            }
            throw;
        }
        var area0 = areaNumber is { } an ? panel.Areas.FirstOrDefault(a => a.Number == an) : null;
        var zone0 = zoneNumber is { } zn ? panel.Zones.FirstOrDefault(z => z.Number == zn) : null;
        var entity = new AlarmEvent
        {
            AlarmPanelId = panel.Id,
            PanelName = panel.Name,
            Timestamp = now,
            ReceivedAt = now,
            Kind = kind,
            Severity = kind == AlarmEventKind.Bypass ? AlarmSeverity.Warning : AlarmSeverity.Info,
            Description = description,
            AreaNumber = areaNumber is > 0 ? areaNumber : zone0?.AreaNumber,
            AreaName = area0?.Name ?? (areaNumber is 0 ? "Todas las áreas" : null),
            ZoneNumber = zoneNumber,
            ZoneName = zone0?.Name,
            Operator = operatorName,
            Source = "vms",
        };
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            db.AlarmEvents.Add(entity);
            await db.SaveChangesAsync(ct);
        }
        await hub.Clients.All.SendAsync(VmsHubContract.AlarmEventReceived, AlarmMapper.ToDto(entity), ct);
        workflows.Publish(Workflows.WorkflowTrigger.FromAlarmEvent(entity));

        // Los paneles aplican la orden con un pequeño retardo: leer de
        // inmediato suele devolver el estado anterior.
        try { await Task.Delay(700, ct); } catch (OperationCanceledException) { }
        return await RefreshAsync(panel.Id, ct) ?? throw new DriverException("El panel ya no existe.");
    }

    /// <summary>Lee el estado del panel ahora mismo, lo persiste y lo publica.</summary>
    public async Task<AlarmPanelDto?> RefreshAsync(int panelId, CancellationToken ct)
    {
        var worker = _workers.GetOrAdd(panelId, _ => new Worker());
        return await PollAsync(panelId, worker, ct, force: true);
    }

    // ------------------------------------------------------------------
    // Ciclo de vida
    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var consumer = Task.Run(() => ConsumeAsync(ct), ct);
        var purger = Task.Run(() => PurgeLoopAsync(ct), ct);

        try { await Task.Delay(TimeSpan.FromSeconds(6), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Fallo en la reconciliación de paneles de alarma.");
            }

            // Se despierta por el intervalo, por un cambio de configuración o
            // porque un panel pidió sondeo tras un evento (cada 1 s como mucho).
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(_workers.Values.Any(w => w.PollRequested) ? TimeSpan.FromSeconds(1)
                : AnyFastPoll ? TimeSpan.FromSeconds(FastPollSeconds)
                : ReconcileInterval);
            try { await _wake.WaitAsync(wait.Token); }
            catch (OperationCanceledException) { }
        }

        foreach (var pair in _workers.ToArray())
        {
            _workers.TryRemove(pair.Key, out _);
            await CloseAsync(pair.Key, pair.Value);
        }
        await Task.WhenAll(consumer, purger).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { }, CancellationToken.None);
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        List<AlarmPanel> wanted;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            wanted = await db.AlarmPanels.AsNoTracking().Where(p => p.Enabled).ToListAsync(ct);
        }
        var wantedIds = wanted.Select(p => p.Id).ToHashSet();

        foreach (var pair in _workers.ToArray())
        {
            if (wantedIds.Contains(pair.Key)) continue;
            _workers.TryRemove(pair.Key, out _);
            await CloseAsync(pair.Key, pair.Value);
        }

        int pollSeconds = Math.Max(5, config.GetValue("Alarms:PollSeconds", 30));
        var now = DateTime.UtcNow;

        foreach (var panel in wanted)
        {
            var worker = _workers.GetOrAdd(panel.Id, _ => new Worker());
            if (drivers.Find(panel.DriverKey) is not { } factory)
            {
                await CloseAsync(panel.Id, worker);
                worker.LastError = $"Driver '{panel.DriverKey}' no disponible.";
                continue;
            }

            var connection = ConnectionOf(panel);
            string fingerprint = $"{panel.DriverKey}|{connection.UseHttps}|{connection.Host}|{connection.Port}|{connection.Username}|{connection.Password}|{connection.DeviceId}" +
                                 $"|{Convert.ToBase64String(panel.GatewayKeyCiphertext ?? [])}|{panel.GatewayProtocol}";
            if (worker.Fingerprint != fingerprint)
            {
                await CloseAsync(panel.Id, worker);
                worker.Fingerprint = fingerprint;
                worker.NextRetryAt = DateTime.MinValue;
                worker.NextPollAt = DateTime.MinValue;
            }

            // 1) Sondeo de estado (periódico o pedido por un evento).
            if (worker.PollRequested || now >= worker.NextPollAt)
            {
                if (now >= worker.NextRetryAt)
                {
                    worker.PollRequested = false;
                    var dto = await PollAsync(panel.Id, worker, ct);
                    worker.NextPollAt = DateTime.UtcNow +
                        TimeSpan.FromSeconds(IsFastPolled(panel.Id) ? FastPollSeconds : pollSeconds);
                    if (dto is null) continue; // sin conexión: el reintento lo fija PollAsync
                }
                else
                {
                    worker.PollRequested = false;
                }
            }

            // 2) Canal de eventos.
            if (worker.Subscription is { IsAlive: true }) continue;
            await CloseAsync(panel.Id, worker);
            if (DateTime.UtcNow < worker.NextRetryAt) continue;

            try
            {
                int panelId = panel.Id;
                worker.Subscription = await factory.Create().SubscribeEventsAsync(connection,
                    evt => Enqueue(panelId, evt),
                    () => OnActivity(panelId), ct);
                worker.LastActivityAt = DateTime.UtcNow;
                logger.LogInformation("Canal de eventos abierto con el panel '{Name}' ({Host}).", panel.Name, panel.Host);
            }
            catch (Exception ex)
            {
                bool auth = ex is DriverException && ex.Message.Contains("credenciales", StringComparison.OrdinalIgnoreCase)
                            || ex.Message.Contains("bloque", StringComparison.OrdinalIgnoreCase);
                worker.LastError = ex is DriverException ? ex.Message : $"No se pudo abrir el canal de eventos: {ex.Message}";
                worker.NextRetryAt = DateTime.UtcNow + (auth ? AuthRetryDelay : RetryDelay);
                logger.LogWarning("Canal de eventos del panel '{Name}' ({Host}): {Error}", panel.Name, panel.Host, worker.LastError);
            }
        }
    }

    private void OnActivity(int panelId)
    {
        if (_workers.TryGetValue(panelId, out var w))
            w.LastActivityAt = DateTime.UtcNow;
    }

    private AlarmConnectionInfo ConnectionOf(AlarmPanel panel) => new(
        panel.Host, panel.Port, panel.UseHttps, panel.Username,
        panel.PasswordCiphertext.Length == 0 ? "" : credentials.Unprotect(panel.PasswordCiphertext),
        panel.GatewayDeviceId);

    private async Task CloseAsync(int panelId, Worker worker)
    {
        var subscription = Interlocked.Exchange(ref worker.Subscription, null);
        if (subscription is null) return;
        try { await subscription.DisposeAsync(); }
        catch (Exception ex) { logger.LogDebug(ex, "Error cerrando el canal de eventos del panel {Id}.", panelId); }
    }

    // ------------------------------------------------------------------
    // Sondeo de estado y diferencia
    // ------------------------------------------------------------------

    /// <summary>
    /// Lee áreas y zonas, aplica la diferencia contra la base (generando
    /// eventos por los cambios que el panel no informó) y publica el estado.
    /// Devuelve null si el panel no respondió (queda Offline/AuthFailed).
    /// </summary>
    private async Task<AlarmPanelDto?> PollAsync(int panelId, Worker worker, CancellationToken ct, bool force = false)
    {
        if (!await worker.PollLock.WaitAsync(0, ct))
        {
            // Ya hay un sondeo en curso: esperar a que termine y devolver lo último.
            await worker.PollLock.WaitAsync(ct);
            worker.PollLock.Release();
            return await ReadDtoAsync(panelId, ct);
        }
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var panel = await db.AlarmPanels.Include(p => p.Areas).Include(p => p.Zones).AsSplitQuery()
                .FirstOrDefaultAsync(p => p.Id == panelId, ct);
            if (panel is null) return null;
            if (drivers.Find(panel.DriverKey) is not { } factory)
            {
                worker.LastError = $"Driver '{panel.DriverKey}' no disponible.";
                return AlarmMapper.ToDto(panel, false, worker.LastError);
            }

            AlarmPanelState state;
            string? gatewayMessage = null;
            try
            {
                var driver = factory.Create();
                var connection = ConnectionOf(panel);
                // Pasarela (receptora): antes de leer, asegurar que el equipo
                // siga registrado en ella. Es la verdad de lo que funciona; el
                // VMS guarda ID y clave y aquí los hace cumplir sin que nadie
                // tenga que mirar la receptora.
                if (driver is IAlarmGatewayDriver gateway && panel.GatewayDeviceId is { Length: > 0 })
                {
                    var registration = await gateway.EnsureRegisteredAsync(connection, panel.GatewayDeviceId,
                        panel.GatewayKeyCiphertext is { Length: > 0 } cipher ? credentials.Unprotect(cipher) : null,
                        panel.GatewayProtocol, replaceIfPresent: false, name: panel.Name, ct: ct);
                    if (registration.StableDeviceId is { Length: > 0 } stable &&
                        !string.Equals(stable, panel.GatewayDeviceId, StringComparison.Ordinal))
                    {
                        // El uuid de la pasarela cambia si el equipo se vuelve a
                        // registrar; el ID ISUP/OTAP no. Se guarda el estable.
                        logger.LogInformation("Panel '{Name}': identificador en la receptora '{Old}' → '{New}'.",
                            panel.Name, panel.GatewayDeviceId, stable);
                        panel.GatewayDeviceId = stable;
                        panel.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(ct);
                        connection = connection with { DeviceId = stable };
                    }
                    switch (registration.Outcome)
                    {
                        case GatewayRegistrationOutcome.ReRegistered:
                            await audit.LogSystemAsync("alarms", "receiver-device-added",
                                targetType: "alarm-receiver", targetName: $"{panel.Host}:{panel.Port}",
                                detail: $"Sincronización con la receptora: el panel '{panel.Name}' (ID {panel.GatewayDeviceId}) " +
                                        $"no estaba registrado y se registró de nuevo (uuid {registration.DevIndex}).",
                                origin: "sistema");
                            logger.LogWarning("Panel '{Name}': {Message}", panel.Name, registration.Message);
                            break;
                        case GatewayRegistrationOutcome.NotRegistered:
                            throw new DriverException(registration.Message ?? "El equipo no está registrado en la receptora.");
                        case GatewayRegistrationOutcome.RegisteredOffline:
                            gatewayMessage = registration.Message;
                            break;
                    }
                }
                state = await driver.GetStateAsync(connection, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                string message = gatewayMessage ?? (ex is DriverException ? ex.Message : $"No se pudo leer el estado: {ex.Message}");
                bool auth = message.Contains("credenciales", StringComparison.OrdinalIgnoreCase) ||
                            message.Contains("bloque", StringComparison.OrdinalIgnoreCase);
                worker.LastError = message;
                worker.NextRetryAt = DateTime.UtcNow + (auth ? AuthRetryDelay : (force ? TimeSpan.Zero : RetryDelay));
                var newStatus = auth ? AlarmPanelStatus.AuthFailed : AlarmPanelStatus.Offline;
                if (panel.Status != newStatus)
                {
                    panel.Status = newStatus;
                    panel.LastError = message;
                    panel.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    await audit.LogSystemAsync("alarms", "panel-offline",
                        targetType: "alarm-panel", targetId: panel.Id.ToString(), targetName: panel.Name,
                        detail: $"El panel '{panel.Name}' ({panel.Host}) no responde: {message}", success: false, origin: "panel");
                    await hub.Clients.All.SendAsync(VmsHubContract.AlarmPanelStateChanged,
                        AlarmMapper.ToDto(panel, false, message), ct);
                    workflows.Publish(Workflows.WorkflowTrigger.FromPanelStatus(panel, newStatus, message));
                    logger.LogWarning("Panel '{Name}' ({Host}) sin conexión: {Error}", panel.Name, panel.Host, message);
                }
                return null;
            }

            worker.LastError = null;
            worker.NextRetryAt = DateTime.MinValue;
            bool wasDown = panel.Status != AlarmPanelStatus.Online;
            var events = ApplyState(panel, state, worker);
            panel.Status = AlarmPanelStatus.Online;
            panel.LastError = null;
            panel.LastSeenAt = DateTime.UtcNow;
            panel.LastStateAt = DateTime.UtcNow;
            panel.UpdatedAt = DateTime.UtcNow;
            db.AlarmEvents.AddRange(events);
            await db.SaveChangesAsync(ct);

            if (wasDown && panel.LastSeenAt is not null)
            {
                await audit.LogSystemAsync("alarms", "panel-online",
                    targetType: "alarm-panel", targetId: panel.Id.ToString(), targetName: panel.Name,
                    detail: $"El panel '{panel.Name}' ({panel.Host}) está en línea ({panel.Areas.Count} áreas, {panel.Zones.Count} zonas).",
                    origin: "panel");
                workflows.Publish(Workflows.WorkflowTrigger.FromPanelStatus(panel, AlarmPanelStatus.Online, null));
            }

            var dto = AlarmMapper.ToDto(panel, IsLive(panelId), null);
            // Con sondeo rápido la mayoría de las lecturas son idénticas a la
            // anterior: solo se publica cuando algo cambió de verdad.
            string signature = Signature(dto);
            if (wasDown || events.Count > 0 || signature != worker.StateSignature)
            {
                worker.StateSignature = signature;
                await hub.Clients.All.SendAsync(VmsHubContract.AlarmPanelStateChanged, dto, ct);
            }
            foreach (var evt in events)
            {
                await hub.Clients.All.SendAsync(VmsHubContract.AlarmEventReceived, AlarmMapper.ToDto(evt), ct);
                await AuditAlarmAsync(evt);
                workflows.Publish(Workflows.WorkflowTrigger.FromAlarmEvent(evt));
            }
            return dto;
        }
        finally
        {
            worker.PollLock.Release();
        }
    }

    private async Task<AlarmPanelDto?> ReadDtoAsync(int panelId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var panel = await db.AlarmPanels.AsNoTracking().Include(p => p.Areas).Include(p => p.Zones).AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == panelId, ct);
        return panel is null ? null : AlarmMapper.ToDto(panel, IsLive(panelId), LastErrorOf(panelId));
    }

    /// <summary>
    /// Sincroniza áreas y zonas con lo leído y devuelve los eventos que
    /// corresponden a los cambios detectados (armado, alarma, anulación,
    /// falla) que el panel no empujó por su canal en los últimos segundos.
    /// </summary>
    private static List<AlarmEvent> ApplyState(AlarmPanel panel, AlarmPanelState state, Worker worker)
    {
        var events = new List<AlarmEvent>();
        var now = DateTime.UtcNow;
        bool first = panel.LastStateAt is null; // primera lectura: no hay "cambios", solo estado inicial

        // ---- Áreas
        var areasByNumber = panel.Areas.ToDictionary(a => a.Number);
        var seenAreas = new HashSet<int>();
        foreach (var read in state.Areas)
        {
            seenAreas.Add(read.Number);
            if (!areasByNumber.TryGetValue(read.Number, out var area))
            {
                area = new AlarmArea { Number = read.Number };
                panel.Areas.Add(area);
                areasByNumber[read.Number] = area;
                area.ArmState = read.ArmState;
                area.InAlarm = read.InAlarm;
            }
            else if (!first)
            {
                if (area.ArmState != read.ArmState && read.ArmState is not (AlarmArmState.Unknown or AlarmArmState.Arming))
                {
                    bool armed = read.ArmState is AlarmArmState.Away or AlarmArmState.Stay or AlarmArmState.Vacation;
                    string key = $"area:{read.Number}:arm:{armed}";
                    if (!worker.WasRecentlyReported(key, now))
                        events.Add(Make(panel, armed ? AlarmEventKind.Arm : AlarmEventKind.Disarm, AlarmSeverity.Info,
                            armed ? $"Área armada ({ArmLabel(read.ArmState)})" : "Área desarmada",
                            read.Number, read.Name, null, null, now));
                }
                if (area.InAlarm != read.InAlarm)
                {
                    string key = $"area:{read.Number}:alarm:{read.InAlarm}";
                    if (!worker.WasRecentlyReported(key, now))
                        events.Add(Make(panel,
                            read.InAlarm ? AlarmEventKind.Alarm : AlarmEventKind.Restore,
                            read.InAlarm ? AlarmSeverity.Critical : AlarmSeverity.Info,
                            read.InAlarm ? "Alarma en el área" : "Alarma del área restaurada",
                            read.Number, read.Name, null, null, now));
                }
            }
            area.ExitDelaySeconds = read.ExitDelaySeconds;   // transitorio, no se persiste
            if (area.Name != read.Name || area.Enabled != read.Enabled ||
                area.ArmState != read.ArmState || area.InAlarm != read.InAlarm)
            {
                area.Name = read.Name;
                area.Enabled = read.Enabled;
                area.ArmState = read.ArmState;
                area.InAlarm = read.InAlarm;
                area.UpdatedAt = now;
            }
        }
        panel.Areas.RemoveAll(a => !seenAreas.Contains(a.Number));

        // ---- Chasis del panel (tapa/sabotaje y corriente de red)
        if (state.Host is { } host)
        {
            if (!first && panel.PanelTamper != host.Tamper)
            {
                string key = $"panel:tamper:{host.Tamper}";
                if (!worker.WasRecentlyReported(key, now))
                    events.Add(Make(panel, host.Tamper ? AlarmEventKind.Trouble : AlarmEventKind.Restore,
                        host.Tamper ? AlarmSeverity.Warning : AlarmSeverity.Info,
                        host.Tamper ? "Tapa del panel abierta (sabotaje del chasis)" : "Tapa del panel cerrada (sabotaje restaurado)",
                        null, null, null, null, now));
            }
            if (!first && panel.AcLoss != host.AcLoss)
            {
                string key = $"panel:acloss:{host.AcLoss}";
                if (!worker.WasRecentlyReported(key, now))
                    events.Add(Make(panel, host.AcLoss ? AlarmEventKind.Trouble : AlarmEventKind.Restore,
                        host.AcLoss ? AlarmSeverity.Warning : AlarmSeverity.Info,
                        host.AcLoss ? "Falla de corriente de red (el panel quedó en batería)" : "Corriente de red restaurada",
                        null, null, null, null, now));
            }
            panel.PanelTamper = host.Tamper;
            panel.AcLoss = host.AcLoss;
        }

        // ---- Zonas
        var zonesByNumber = panel.Zones.ToDictionary(z => z.Number);
        var seenZones = new HashSet<int>();
        foreach (var read in state.Zones)
        {
            seenZones.Add(read.Number);
            string? areaName = read.AreaNumber is { } an && areasByNumber.TryGetValue(an, out var owner) ? owner.Name : null;
            if (!zonesByNumber.TryGetValue(read.Number, out var zone))
            {
                zone = new AlarmZone { Number = read.Number };
                panel.Zones.Add(zone);
                zonesByNumber[read.Number] = zone;
            }
            else if (!first)
            {
                if (zone.InAlarm != read.InAlarm)
                {
                    string key = $"zone:{read.Number}:alarm:{read.InAlarm}";
                    if (!worker.WasRecentlyReported(key, now))
                        events.Add(Make(panel,
                            read.InAlarm ? AlarmEventKind.Alarm : AlarmEventKind.Restore,
                            read.InAlarm ? AlarmSeverity.Critical : AlarmSeverity.Info,
                            read.InAlarm ? "Alarma en zona" : "Zona restaurada",
                            read.AreaNumber, areaName, read.Number, read.Name, now));
                }
                // Detector interrumpido y restablecido SIN alarma (puerta que
                // se abre con el área desarmada): el panel no lo informa por su
                // canal de eventos —las centrales solo reportan Contact-ID—, así
                // que sale de la diferencia de estado. Es lo que permite
                // automatizar "el sensor quedó interrumpido".
                bool wasOpen = zone.Status == AlarmZoneStatus.Triggered;
                bool isOpen = read.Status == AlarmZoneStatus.Triggered;
                if (wasOpen != isOpen && !read.InAlarm)
                {
                    string key = $"zone:{read.Number}:open:{isOpen}";
                    if (!worker.WasRecentlyReported(key, now))
                        events.Add(Make(panel,
                            isOpen ? AlarmEventKind.ZoneTriggered : AlarmEventKind.Restore,
                            AlarmSeverity.Info,
                            isOpen ? "Sensor interrumpido (detector activado)" : "Sensor restablecido",
                            read.AreaNumber, areaName, read.Number, read.Name, now));
                }
                if (zone.Bypassed != read.Bypassed)
                {
                    string key = $"zone:{read.Number}:bypass:{read.Bypassed}";
                    if (!worker.WasRecentlyReported(key, now))
                        events.Add(Make(panel, AlarmEventKind.Bypass, AlarmSeverity.Warning,
                            read.Bypassed ? "Zona anulada (bypass)" : "Zona restituida (fin de bypass)",
                            read.AreaNumber, areaName, read.Number, read.Name, now));
                }
                bool wasFault = zone.Status is AlarmZoneStatus.Fault or AlarmZoneStatus.Offline || zone.Tamper || zone.LowBattery;
                bool isFault = read.Status is AlarmZoneStatus.Fault or AlarmZoneStatus.Offline || read.Tamper || read.LowBattery;
                if (wasFault != isFault)
                {
                    string key = $"zone:{read.Number}:fault:{isFault}";
                    if (!worker.WasRecentlyReported(key, now))
                        events.Add(Make(panel,
                            isFault ? AlarmEventKind.Trouble : AlarmEventKind.Restore,
                            isFault ? AlarmSeverity.Warning : AlarmSeverity.Info,
                            isFault ? FaultLabel(read) : "Falla de zona resuelta",
                            read.AreaNumber, areaName, read.Number, read.Name, now));
                }
            }
            // Solo se escribe la fila si algo cambió: con el sondeo rápido, una
            // lectura idéntica no debe generar un UPDATE por zona cada 2 s.
            if (zone.AreaNumber != read.AreaNumber || zone.Name != read.Name || zone.ZoneType != read.ZoneType ||
                zone.DetectorType != read.DetectorType || zone.Status != read.Status || zone.Bypassed != read.Bypassed ||
                zone.Armed != read.Armed || zone.InAlarm != read.InAlarm || zone.Tamper != read.Tamper ||
                zone.LowBattery != read.LowBattery || zone.Signal != read.Signal || zone.Model != read.Model)
            {
                zone.AreaNumber = read.AreaNumber;
                zone.Name = read.Name;
                zone.ZoneType = read.ZoneType;
                zone.DetectorType = read.DetectorType;
                zone.Status = read.Status;
                zone.Bypassed = read.Bypassed;
                zone.Armed = read.Armed;
                zone.InAlarm = read.InAlarm;
                zone.Tamper = read.Tamper;
                zone.LowBattery = read.LowBattery;
                zone.Signal = read.Signal;
                zone.Model = read.Model;
                zone.UpdatedAt = now;
            }
        }
        panel.Zones.RemoveAll(z => !seenZones.Contains(z.Number));

        foreach (var evt in events) evt.Source = "poll";
        return events;
    }

    private static string FaultLabel(AlarmZoneState zone)
    {
        if (zone.Tamper) return "Tamper (sabotaje) en zona";
        if (zone.LowBattery) return "Batería baja del detector";
        if (zone.Status == AlarmZoneStatus.Offline) return "Detector sin comunicación";
        return "Falla en zona";
    }

    internal static string ArmLabel(AlarmArmState state) => state switch
    {
        AlarmArmState.Away => "total",
        AlarmArmState.Stay => "parcial / en casa",
        AlarmArmState.Vacation => "vacaciones",
        AlarmArmState.Arming => "en proceso",
        AlarmArmState.Disarmed => "desarmado",
        _ => "desconocido",
    };

    private static AlarmEvent Make(AlarmPanel panel, AlarmEventKind kind, AlarmSeverity severity, string description,
        int? areaNumber, string? areaName, int? zoneNumber, string? zoneName, DateTime timestamp) => new()
    {
        AlarmPanelId = panel.Id,
        PanelName = panel.Name,
        Timestamp = timestamp,
        ReceivedAt = timestamp,
        Kind = kind,
        Severity = severity,
        Description = description,
        AreaNumber = areaNumber,
        AreaName = areaName,
        ZoneNumber = zoneNumber,
        ZoneName = zoneName,
        Source = "poll",
    };

    // ------------------------------------------------------------------
    // Eventos empujados por el panel
    // ------------------------------------------------------------------

    /// <summary>Llamado desde el hilo del driver: solo encolar y volver.</summary>
    private void Enqueue(int panelId, AlarmPanelEvent evt)
    {
        if (_workers.TryGetValue(panelId, out var worker))
        {
            worker.LastActivityAt = DateTime.UtcNow;
            // El estado real se relee tras el evento (armado, alarma, bypass...).
            worker.PollRequested = true;
            RequestReconcile();
        }
        _incoming.Writer.TryWrite((panelId, evt));
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (panelId, evt) in _incoming.Reader.ReadAllAsync(ct))
            {
                try { await PersistEventAsync(panelId, evt, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "No se pudo registrar el evento '{Description}' del panel {Id}.", evt.Description, panelId);
                }
            }
        }
        catch (OperationCanceledException) { /* cierre del servidor */ }
    }

    private async Task PersistEventAsync(int panelId, AlarmPanelEvent evt, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var panel = await db.AlarmPanels.AsNoTracking().Include(p => p.Areas).Include(p => p.Zones).AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == panelId, ct);
        if (panel is null) return;

        var area = evt.AreaNumber is { } an ? panel.Areas.FirstOrDefault(a => a.Number == an) : null;
        var zone = evt.ZoneNumber is { } zn ? panel.Zones.FirstOrDefault(z => z.Number == zn) : null;
        int? areaNumber = evt.AreaNumber ?? zone?.AreaNumber;

        // Eco de una orden dada desde el VMS hace instantes: ya quedó en el
        // historial con el operador; el mismo armado informado por el panel
        // no se registra dos veces (el sondeo tampoco lo duplicará).
        if (_workers.TryGetValue(panelId, out var echoWorker))
        {
            string? echoKey = evt.Kind switch
            {
                AlarmEventKind.Arm when areaNumber is { } ea => $"area:{ea}:arm:True",
                AlarmEventKind.Disarm when areaNumber is { } ed => $"area:{ed}:arm:False",
                AlarmEventKind.Bypass when evt.ZoneNumber is { } ez =>
                    $"zone:{ez}:bypass:{!evt.Description.Contains("restitu", StringComparison.OrdinalIgnoreCase)}",
                _ => null,
            };
            foreach (var pair in echoWorker.RecentVmsCommands)
                if (DateTime.UtcNow - pair.Value > DedupWindow) echoWorker.RecentVmsCommands.TryRemove(pair.Key, out _);
            if (echoKey is not null && echoWorker.RecentVmsCommands.ContainsKey(echoKey))
            {
                echoWorker.RecentChanges[echoKey] = DateTime.UtcNow;
                return;
            }
        }
        string? areaName = area?.Name ?? (areaNumber is { } n2 ? panel.Areas.FirstOrDefault(a => a.Number == n2)?.Name : null);

        var entity = new AlarmEvent
        {
            AlarmPanelId = panel.Id,
            PanelName = panel.Name,
            Timestamp = evt.Timestamp,
            ReceivedAt = DateTime.UtcNow,
            Kind = evt.Kind,
            Severity = evt.Severity,
            Code = Truncate(evt.Code, 16),
            Description = Truncate(evt.Description, 256) ?? "",
            AreaNumber = areaNumber,
            AreaName = areaName,
            ZoneNumber = evt.ZoneNumber,
            ZoneName = zone?.Name ?? (evt.ZoneNumber is { } z ? $"Zona {z}" : null),
            Operator = Truncate(evt.Operator, 64),
            Source = "panel",
            RawJson = Truncate(evt.Raw, 4096),
        };
        db.AlarmEvents.Add(entity);
        await db.SaveChangesAsync(ct);

        // Marcar el cambio como ya informado para que el sondeo siguiente no
        // lo duplique.
        if (_workers.TryGetValue(panelId, out var worker))
        {
            var now = DateTime.UtcNow;
            switch (evt.Kind)
            {
                case AlarmEventKind.Arm when areaNumber is { } a1: worker.RecentChanges[$"area:{a1}:arm:True"] = now; break;
                case AlarmEventKind.Disarm when areaNumber is { } a2: worker.RecentChanges[$"area:{a2}:arm:False"] = now; break;
                case AlarmEventKind.Alarm:
                    if (evt.ZoneNumber is { } z1) worker.RecentChanges[$"zone:{z1}:alarm:True"] = now;
                    if (areaNumber is { } a3) worker.RecentChanges[$"area:{a3}:alarm:True"] = now;
                    break;
                case AlarmEventKind.Restore:
                    if (evt.ZoneNumber is { } z2)
                    {
                        worker.RecentChanges[$"zone:{z2}:alarm:False"] = now;
                        worker.RecentChanges[$"zone:{z2}:fault:False"] = now;
                    }
                    if (areaNumber is { } a4) worker.RecentChanges[$"area:{a4}:alarm:False"] = now;
                    break;
                case AlarmEventKind.Bypass when evt.ZoneNumber is { } z3:
                    bool restored = evt.Description.Contains("restitu", StringComparison.OrdinalIgnoreCase);
                    worker.RecentChanges[$"zone:{z3}:bypass:{!restored}"] = now;
                    break;
                case AlarmEventKind.Trouble when evt.ZoneNumber is { } z4:
                    worker.RecentChanges[$"zone:{z4}:fault:True"] = now;
                    break;
            }
        }

        await hub.Clients.All.SendAsync(VmsHubContract.AlarmEventReceived, AlarmMapper.ToDto(entity), ct);
        await AuditAlarmAsync(entity);
        // Automatizaciones: el evento ya está en el historial y con sus
        // nombres de área y zona resueltos, que es lo que verán los correos.
        workflows.Publish(Workflows.WorkflowTrigger.FromAlarmEvent(entity));
        logger.LogInformation("Panel '{Panel}': {Kind} — {Description}{Zone}.", panel.Name, entity.Kind, entity.Description,
            entity.ZoneName is null ? "" : $" ({entity.ZoneName})");
    }

    /// <summary>
    /// Las alarmas y fallas quedan además en la bitácora: son evidencia de
    /// seguridad física (quién armó/desarmó se audita en la API cuando la
    /// orden sale del VMS; aquí queda lo que hizo el panel o un usuario local).
    /// </summary>
    private Task AuditAlarmAsync(AlarmEvent evt)
    {
        string action = evt.Kind switch
        {
            AlarmEventKind.Alarm => "alarm-received",
            AlarmEventKind.Arm when evt.Source != "vms" => "area-armed",
            AlarmEventKind.Disarm when evt.Source != "vms" => "area-disarmed",
            _ => "",
        };
        if (action.Length == 0) return Task.CompletedTask;
        string where = evt.ZoneName is not null ? $" · zona '{evt.ZoneName}'" : evt.AreaName is not null ? $" · área '{evt.AreaName}'" : "";
        string who = evt.Operator is { Length: > 0 } ? $" por '{evt.Operator}'" : evt.Source == "panel" ? " (informado por el panel)" : " (detectado por sondeo)";
        return audit.LogSystemAsync("alarms", action,
            targetType: "alarm-panel", targetId: evt.AlarmPanelId.ToString(), targetName: evt.PanelName,
            detail: $"{evt.Description}{where}{who}." + (evt.Code is null ? "" : $" Código {evt.Code}."),
            success: evt.Kind != AlarmEventKind.Alarm,
            username: evt.Operator is { Length: > 0 } ? evt.Operator : "panel", origin: "panel",
            data: new { evt.Kind, evt.Severity, evt.Code, evt.AreaNumber, evt.ZoneNumber, evt.Timestamp });
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    /// <summary>Resumen del estado publicable de un panel (para detectar si cambió).</summary>
    private static string Signature(AlarmPanelDto panel) =>
        string.Join('|',
            [$"{panel.Status}:{panel.PanelTamper}:{panel.AcLoss}",
             .. panel.Areas.Select(a => $"a{a.Number}:{a.ArmState}:{a.InAlarm}:{a.ExitDelaySeconds}"),
             .. panel.Zones.Select(z => $"z{z.Number}:{z.Status}:{z.InAlarm}:{z.Bypassed}:{z.Tamper}:{z.LowBattery}")]);

    // ------------------------------------------------------------------
    // Purga del historial
    // ------------------------------------------------------------------

    private async Task PurgeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PurgeAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Fallo en la purga de eventos de alarma."); }
            try { await Task.Delay(PurgeInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PurgeAsync(CancellationToken ct)
    {
        int retentionDays = config.GetValue("Alarms:RetentionDays", 365);
        int maxEvents = config.GetValue("Alarms:MaxEvents", 500_000);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        int removed = 0;
        if (retentionDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            removed += await db.AlarmEvents.Where(e => e.ReceivedAt < cutoff).ExecuteDeleteAsync(ct);
        }
        if (maxEvents > 0)
        {
            int total = await db.AlarmEvents.CountAsync(ct);
            if (total > maxEvents)
            {
                var threshold = await db.AlarmEvents.OrderByDescending(e => e.ReceivedAt)
                    .Skip(maxEvents).Select(e => e.ReceivedAt).FirstAsync(ct);
                removed += await db.AlarmEvents.Where(e => e.ReceivedAt <= threshold).ExecuteDeleteAsync(ct);
            }
        }
        if (removed > 0)
            logger.LogInformation("Purga de eventos de alarma: {Count} eliminados.", removed);
    }
}

/// <summary>Conversión entidad → DTO compartida por el servicio y la API.</summary>
public static class AlarmMapper
{
    public static AlarmPanelDto ToDto(AlarmPanel p, bool live, string? lastError) => new(
        p.Id, p.Name, p.DriverKey, p.Host, p.Port, p.UseHttps, p.Username, p.GatewayDeviceId,
        p.Model, p.SerialNumber, p.FirmwareVersion, p.Enabled, p.Status, live,
        p.PanelTamper, p.AcLoss,
        lastError ?? p.LastError, p.LastSeenAt, p.LastStateAt, p.CreatedAt,
        p.Areas.OrderBy(a => a.Number).Select(a => new AlarmAreaDto(a.Number, a.Name, a.Enabled, a.ArmState, a.InAlarm,
            p.Zones.Count(z => z.AreaNumber == a.Number), a.ExitDelaySeconds)).ToList(),
        p.Zones.OrderBy(z => z.Number).Select(ToDto).ToList(),
        p.GatewayKeyCiphertext is { Length: > 0 }, p.GatewayProtocol);

    public static AlarmZoneDto ToDto(AlarmZone z) => new(
        z.Number, z.AreaNumber, z.Name, z.ZoneType, z.DetectorType, z.Status,
        z.Bypassed, z.Armed, z.InAlarm, z.Tamper, z.LowBattery, z.Signal, z.Model);

    public static AlarmEventDto ToDto(AlarmEvent e) => new(
        e.Id, e.AlarmPanelId, e.PanelName, e.Timestamp, e.ReceivedAt, e.Kind, e.Severity, e.Code,
        e.Description, e.AreaNumber, e.AreaName, e.ZoneNumber, e.ZoneName, e.Operator, e.Source);
}
