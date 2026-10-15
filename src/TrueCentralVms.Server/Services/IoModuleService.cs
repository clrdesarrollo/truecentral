using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Módulos de entradas y salidas (E/S). El servidor es el ÚNICO que habla con
/// cada módulo —estos equipos admiten pocas conexiones Modbus a la vez—, así
/// que aquí vive una conexión por módulo habilitado:
/// <list type="bullet">
/// <item><b>Sondeo</b>: lee entradas y salidas cada pocos cientos de ms; los
/// cambios se empujan por el hub y las entradas que cambian disparan las
/// automatizaciones («entrada de módulo de E/S»).</item>
/// <item><b>Conexión</b>: la pérdida y recuperación se guardan, se auditan y
/// disparan «conexión de un equipo».</item>
/// <item><b>Órdenes</b>: encender, apagar, alternar, pulso y parpadeo de las
/// salidas, desde el panel o desde la acción «Operar relé».</item>
/// </list>
/// Los pulsos y parpadeos los temporiza este proceso: al detenerse el servicio
/// se sueltan las salidas que estaban en uno. Si el proceso muere de golpe, la
/// salida queda como esté (para eso está el watchdog del propio módulo).
/// </summary>
public sealed class IoModuleService(
    IServiceScopeFactory scopeFactory,
    IoModuleDriverRegistry drivers,
    ScopedHub hub,
    AuditService audit,
    ILogger<IoModuleService> logger) : BackgroundService
{
    /// <summary>Repaso de la configuración por si algo cambió sin aviso (y reintento de los que no abrieron).</summary>
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);
    public const int MinPollMs = 100;
    public const int MaxPollMs = 5000;

    private readonly ConcurrentDictionary<int, Runtime> _runtimes = new();
    private readonly SemaphoreSlim _wake = new(0);
    /// <summary>Los avisos de los monitores llegan desde sus hilos: se procesan aquí, de a uno y en orden.</summary>
    private readonly Channel<Func<CancellationToken, Task>> _events =
        System.Threading.Channels.Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Conexión viva con un módulo y la configuración con que se abrió.</summary>
    private sealed class Runtime(IIoModuleSession session, string signature, IoModule module)
    {
        public IIoModuleSession Session { get; } = session;
        public string Signature { get; } = signature;
        /// <summary>Copia de la fila con sus entradas y salidas (nombres y lógica); se refresca al repasar.</summary>
        public IoModule Module { get; set; } = module;
    }

    /// <summary>La configuración cambió: repasar ahora en vez de esperar el ciclo.</summary>
    public void RequestReconcile()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    /// <summary>Modelo conocido por su driver y su clave, o null.</summary>
    public IoModuleModel? ModelOf(string driverKey, string? modelKey) =>
        modelKey is null ? null
            : drivers.Find(driverKey)?.Create().Models
                .FirstOrDefault(m => string.Equals(m.Key, modelKey, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------
    // Estado para el panel
    // ------------------------------------------------------------------

    /// <summary>
    /// El módulo con el estado vivo de sus entradas y salidas. Lo que no tiene
    /// lectura (pausado, sin conexión, recién creado) va con el estado en null.
    /// </summary>
    public IoModuleDto ToDto(IoModule m)
    {
        var runtime = _runtimes.GetValueOrDefault(m.Id);
        var session = runtime?.Session;
        var state = session?.IsOnline == true ? session.LastState : null;
        var status = !m.Enabled ? m.Status
            : session?.IsOnline is { } online ? (online ? IoModuleStatus.Online : IoModuleStatus.Offline)
            : m.Status;
        var model = session?.Model ?? ModelOf(m.DriverKey, m.Model);
        int inputCount = model?.Inputs ?? m.Points.Count(p => p.Kind == IoPointKind.Input);
        int outputCount = model?.Outputs ?? m.Points.Count(p => p.Kind == IoPointKind.Output);

        IoPointDto Point(IoPointKind kind, int number, IReadOnlyList<bool>? levels)
        {
            var point = m.Points.FirstOrDefault(p => p.Kind == kind && p.Number == number)
                        ?? new IoPoint { Kind = kind, Number = number };
            bool? raw = levels is not null && number < levels.Count ? levels[number] : null;
            bool? active = raw is { } level ? (kind == IoPointKind.Input ? level ^ point.Inverted : level) : null;
            string name = string.IsNullOrWhiteSpace(point.Name) ? point.Code : point.Name;
            return new IoPointDto(kind, number, point.Code, name, kind == IoPointKind.Input && point.Inverted, active, raw);
        }

        return new IoModuleDto(m.Id, m.Name, m.DriverKey, m.Host, m.Port, m.ModelKey, m.Model, m.ReportedModel,
            m.PollIntervalMs, m.Enabled, status, status == IoModuleStatus.Online ? null : m.LastError,
            state?.TimestampUtc ?? m.LastSeenAt,
            Enumerable.Range(0, inputCount).Select(n => Point(IoPointKind.Input, n, state?.Inputs)).ToList(),
            Enumerable.Range(0, outputCount).Select(n => Point(IoPointKind.Output, n, state?.Outputs)).ToList(),
            m.CreatedAt, m.UpdatedAt, m.LocationId, Auth.LocationPaths.Of(m.LocationId));
    }

    // ------------------------------------------------------------------
    // Órdenes a las salidas
    // ------------------------------------------------------------------

    /// <summary>Resultado de una orden: si llegó al módulo y qué se hizo, en palabras.</summary>
    public sealed record CommandOutcome(bool Success, string Message);

    /// <summary>
    /// Da una orden a una salida. <paramref name="seconds"/>: duración del pulso
    /// (por omisión 1 s) o del parpadeo (0 o null = hasta otra orden);
    /// <paramref name="intervalMs"/>: período del parpadeo (por omisión 500 ms).
    /// No audita: lo hace quien la pidió (operador o automatización), con su nombre.
    /// </summary>
    public async Task<CommandOutcome> CommandAsync(int moduleId, int output, IoOutputCommand command,
        double? seconds, int? intervalMs, CancellationToken ct)
    {
        if (!_runtimes.TryGetValue(moduleId, out var runtime))
            return new(false, "El módulo está pausado o el servidor todavía no abre su conexión.");
        var session = runtime.Session;
        string outputLabel = OutputLabel(runtime.Module, output);
        string target = $"{outputLabel} de '{runtime.Module.Name}'";
        if (output < 0 || output >= session.Model.Outputs)
            return new(false, session.Model.Outputs == 0
                ? $"El {session.Model.Name} no tiene salidas."
                : $"El {session.Model.Name} tiene salidas DO0 a DO{session.Model.Outputs - 1}.");

        var driver = drivers.Find(runtime.Module.DriverKey)?.Create();
        try
        {
            string done;
            switch (command)
            {
                case IoOutputCommand.On:
                    await session.SetOutputAsync(output, true, ct);
                    done = $"Encendió {target}";
                    break;
                case IoOutputCommand.Off:
                    await session.SetOutputAsync(output, false, ct);
                    done = $"Apagó {target}";
                    break;
                case IoOutputCommand.Toggle:
                    bool now = await session.ToggleOutputAsync(output, ct);
                    done = $"Alternó {target}: quedó {(now ? "encendida" : "apagada")}";
                    break;
                case IoOutputCommand.Pulse:
                {
                    double s = seconds is > 0 ? seconds.Value : 1;
                    if (s is < 0.1 or > 3600) return new(false, "El pulso debe durar entre 0,1 y 3600 segundos.");
                    await session.PulseOutputAsync(output, TimeSpan.FromSeconds(s), ct);
                    done = $"Dio un pulso de {Seconds(s)} a {target}";
                    break;
                }
                case IoOutputCommand.Blink:
                {
                    int period = intervalMs is > 0 ? intervalMs.Value : 500;
                    int min = (int)(driver?.MinBlinkInterval.TotalMilliseconds ?? 100);
                    if (period < min || period > 60_000)
                        return new(false, $"El parpadeo debe alternar cada {min} a 60000 ms.");
                    double s = seconds ?? 0;
                    if (s is < 0 or > 3600) return new(false, "El parpadeo dura entre 0 (hasta otra orden) y 3600 segundos.");
                    await session.BlinkOutputAsync(output, TimeSpan.FromMilliseconds(period),
                        s == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(s), ct);
                    done = $"Hizo parpadear {target} cada {period} ms " + (s == 0 ? "hasta nueva orden" : $"durante {Seconds(s)}");
                    break;
                }
                default:
                    return new(false, $"Orden desconocida: {command}.");
            }
            session.RefreshNow();
            return new(true, done + ".");
        }
        catch (DriverException ex)
        {
            return new(false, $"El módulo '{runtime.Module.Name}' no recibió la orden sobre {outputLabel}: {ex.Message}");
        }
    }

    private static string Seconds(double s) => s == Math.Floor(s) ? $"{s:0} s" : $"{s:0.0} s";

    /// <summary>"'Barrera' (DO0)", o solo "DO2" si la salida no tiene nombre propio.</summary>
    private static string OutputLabel(IoModule module, int number) =>
        module.Points.FirstOrDefault(p => p.Kind == IoPointKind.Output && p.Number == number) is { Name.Length: > 0 } point
        && point.Name != point.Code
            ? $"'{point.Name}' (DO{number})" : $"DO{number}";

    // ------------------------------------------------------------------
    // Conexiones
    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processing = Task.Run(() => ProcessEventsAsync(stoppingToken), CancellationToken.None);
        try
        {
            // Dar tiempo a que la API y el hub estén arriba.
            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try { await ReconcileAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogWarning(ex, "No se pudo repasar la configuración de los módulos de E/S."); }

                try { await _wake.WaitAsync(ReconcileInterval, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
        finally
        {
            // Se sueltan los pulsos y parpadeos en curso: ninguna barrera queda arriba
            // porque el servidor se detuvo.
            foreach (int id in _runtimes.Keys.ToList())
                await CloseAsync(id);
            try { await processing; } catch (OperationCanceledException) { }
        }
    }

    private static string SignatureOf(IoModule m) => $"{m.DriverKey}|{m.Host}|{m.Port}|{m.Model}|{m.PollIntervalMs}";

    private async Task ReconcileAsync(CancellationToken ct)
    {
        List<IoModule> modules;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            modules = await db.IoModules.AsNoTracking().Include(m => m.Points).Where(m => m.Enabled).ToListAsync(ct);
        }

        foreach (int id in _runtimes.Keys.Where(id => modules.All(m => m.Id != id)).ToList())
            await CloseAsync(id);

        foreach (var module in modules)
        {
            string signature = SignatureOf(module);
            if (_runtimes.TryGetValue(module.Id, out var existing))
            {
                if (existing.Signature == signature)
                {
                    // Nombres y lógica de las entradas: sin reconectar.
                    existing.Module = module;
                    continue;
                }
                await CloseAsync(module.Id);
            }
            await OpenAsync(module, signature, ct);
        }
    }

    private async Task OpenAsync(IoModule module, string signature, CancellationToken ct)
    {
        string? problem = null;
        IIoModuleSession? session = null;
        if (drivers.Find(module.DriverKey) is not { } factory)
            problem = $"Driver desconocido: '{module.DriverKey}'.";
        else if (ModelOf(module.DriverKey, module.Model) is not { } model)
            problem = "No se sabe qué modelo es: pruebe la conexión o elíjalo a mano en el mantenedor.";
        else
            session = factory.Create().Open(new IoConnectionInfo(module.Host, module.Port), model,
                TimeSpan.FromMilliseconds(Math.Clamp(module.PollIntervalMs, MinPollMs, MaxPollMs)));

        if (session is null)
        {
            await SaveStatusAsync(module.Id, IoModuleStatus.Offline, problem, ct);
            return;
        }

        var runtime = new Runtime(session, signature, module);
        int id = module.Id;
        bool Current() => _runtimes.TryGetValue(id, out var now) && ReferenceEquals(now, runtime);
        session.ConnectionChanged += (online, error) =>
            Enqueue(async token => { if (Current()) await OnConnectionAsync(runtime, online, error, token); });
        session.StateChanged += _ =>
            Enqueue(async token => { if (Current()) await PushAsync(runtime, token); });
        session.ChannelChanged += change =>
        {
            if (change.Kind == IoChannelKind.Input)
                Enqueue(token => { if (Current()) OnInputChanged(runtime, change); return Task.CompletedTask; });
        };
        session.OutputEffectFailed += (output, error) =>
            Enqueue(async _ => await OnEffectFailedAsync(runtime, output, error));

        _runtimes[id] = runtime;
        session.Start();
        logger.LogInformation("Módulo de E/S '{Name}' ({Host}:{Port}, {Model}): conexión abierta.",
            module.Name, module.Host, module.Port, session.Model.Name);
    }

    private async Task CloseAsync(int id)
    {
        if (!_runtimes.TryRemove(id, out var runtime)) return;
        try { await runtime.Session.DisposeAsync(); }
        catch (Exception ex) { logger.LogDebug(ex, "Cierre del módulo de E/S {Id}.", id); }
    }

    /// <summary>Cierra ya la conexión (al borrar o pausar el módulo), sin esperar el repaso.</summary>
    public Task DetachAsync(int id) => CloseAsync(id);

    private void Enqueue(Func<CancellationToken, Task> work) => _events.Writer.TryWrite(work);

    private async Task ProcessEventsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var work in _events.Reader.ReadAllAsync(ct))
            {
                try { await work(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogWarning(ex, "Fallo procesando un aviso de un módulo de E/S."); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task OnConnectionAsync(Runtime runtime, bool online, string? error, CancellationToken ct)
    {
        var status = online ? IoModuleStatus.Online : IoModuleStatus.Offline;
        var (module, previous) = await SaveStatusAsync(runtime.Module.Id, status, error, ct);
        if (module is null) return;

        if (previous != status)
        {
            if (online && previous != IoModuleStatus.Unknown)
                await audit.LogSystemAsync("io", "io-module-online",
                    targetType: "io-module", targetId: module.Id.ToString(), targetName: module.Name,
                    detail: $"El módulo de E/S '{module.Name}' ({module.Host}) recuperó la conexión.");
            else if (!online)
                await audit.LogSystemAsync("io", "io-module-offline",
                    targetType: "io-module", targetId: module.Id.ToString(), targetName: module.Name,
                    detail: $"El módulo de E/S '{module.Name}' ({module.Host}) no responde: {error}", success: false);

            // Automatizaciones ("módulo sin conexión → avisar"). Resuelto al vuelo:
            // el motor contiene la acción «Operar relé», que usa este servicio.
            if (previous != IoModuleStatus.Unknown || !online)
            {
                using var scope = scopeFactory.CreateScope();
                scope.ServiceProvider.GetRequiredService<Workflows.WorkflowEngine>().Publish(
                    Workflows.WorkflowTrigger.FromDeviceStatus("io", module.Id, module.Name, module.Host, module.Model,
                        online ? "Online" : "Offline", error));
            }
        }
        await PushAsync(runtime, ct);
    }

    private void OnInputChanged(Runtime runtime, IoChannelChange change)
    {
        var module = runtime.Module;
        var input = module.Points.FirstOrDefault(p => p.Kind == IoPointKind.Input && p.Number == change.Number)
                    ?? new IoPoint { IoModuleId = module.Id, Kind = IoPointKind.Input, Number = change.Number, Name = $"DI{change.Number}" };
        if (string.IsNullOrWhiteSpace(input.Name)) input.Name = input.Code;
        bool active = change.Value ^ input.Inverted;
        using var scope = scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<Workflows.WorkflowEngine>().Publish(
            Workflows.WorkflowTrigger.FromIoInput(module, input, active, change.TimestampUtc));
    }

    private async Task OnEffectFailedAsync(Runtime runtime, int output, string error)
    {
        var module = runtime.Module;
        logger.LogWarning("Módulo de E/S '{Name}': un pulso o parpadeo de DO{Output} no pudo terminar: {Error}",
            module.Name, output, error);
        await audit.LogSystemAsync("io", "output-effect-failed",
            targetType: "io-module", targetId: module.Id.ToString(), targetName: module.Name,
            detail: $"No se pudo soltar {OutputLabel(module, output)} del módulo '{module.Name}' al terminar " +
                    $"su pulso o parpadeo: {error}. Puede haber quedado encendida; el watchdog del módulo la suelta si está configurado.",
            success: false);
    }

    /// <summary>Guarda el estado de conexión; devuelve la fila y el estado anterior (fila null si ya no existe).</summary>
    private async Task<(IoModule? Module, IoModuleStatus Previous)> SaveStatusAsync(int id, IoModuleStatus status,
        string? error, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var module = await db.IoModules.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (module is null) return (null, IoModuleStatus.Unknown);
        var previous = module.Status;
        module.Status = status;
        module.LastError = status == IoModuleStatus.Online ? null : error;
        if (status == IoModuleStatus.Online) module.LastSeenAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return (module, previous);
    }

    private async Task PushAsync(Runtime runtime, CancellationToken ct)
    {
        var dto = ToDto(runtime.Module);
        await hub.SendAsync(VmsHubContract.IoModuleChanged, dto, sc => sc.CanViewIoModule(dto.Id), ct);
    }

    /// <summary>Empuja el módulo por el hub (después de editarlo, para que las pantallas lo vean sin esperar un cambio).</summary>
    public async Task PushAsync(int id, CancellationToken ct)
    {
        if (_runtimes.TryGetValue(id, out var runtime)) await PushAsync(runtime, ct);
    }
}
