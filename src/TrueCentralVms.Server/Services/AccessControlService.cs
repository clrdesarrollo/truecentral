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
/// Módulo Control de acceso: mantiene vivo el estado de los equipos y de sus
/// puertas, y ejecuta las órdenes del operador sobre ellas. Cada 60 s sondea a
/// los equipos habilitados con UNA llamada liviana (deviceInfo) y, a los que
/// saben informarlo, les pregunta además en qué modo está cada puerta; los
/// cambios se auditan y se empujan por el hub, igual que en parlantes y
/// paneles.
///
/// El padrón (personas, credenciales y horarios) lo escribe
/// <see cref="AccessSyncService"/> y el historial lo trae
/// <see cref="AccessEventService"/>; los tres comparten desde acá la forma de
/// llegar a los equipos.
/// </summary>
public sealed class AccessControlService(
    IServiceScopeFactory scopeFactory,
    AccessDriverRegistry drivers,
    CredentialProtector credentials,
    ScopedHub hub,
    AuditService audit,
    Isup.IsupService isup,
    Isup.IsupHostManager isupHost,
    Licensing.LicenseService license,
    IConfiguration config,
    ILogger<AccessControlService> logger) : SupervisedBackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    /// <summary>Equipos que se sondean a la vez (los ISAPI son lentos y limitan sesiones).</summary>
    private const int Parallelism = 4;

    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>
    /// Si se le pregunta a los terminales por su cámara, su altavoz y sus
    /// entradas y salidas (<c>Access:ReadTerminalParts</c>). Apagado, los
    /// terminales que comparten el equipo con otro sistema (un kiosco que
    /// también entra por el SDK) no reciben más consultas que las de siempre;
    /// las partes que ya tienen se conservan.
    /// </summary>
    public bool ReadTerminalParts { get; } = config.GetValue("Access:ReadTerminalParts", true);

    /// <summary>La configuración cambió: sondear ahora en vez de esperar el ciclo.</summary>
    public void RequestReconcile()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    public AccessConnectionInfo ConnectionOf(AccessDevice device) =>
        new(device.Host, device.Port, device.UseHttps, device.Username, credentials.Unprotect(device.PasswordCiphertext), device.IsupDeviceId,
            ReadTerminalParts);

    public IAccessControlDriver DriverOf(AccessDevice device) =>
        drivers.Find(device.DriverKey)?.Create()
        ?? throw new DriverException($"El equipo '{device.Name}' usa un driver desconocido ('{device.DriverKey}').");

    /// <summary>Olvida lo que el driver tenga cacheado del equipo (credenciales cambiadas o equipo eliminado).</summary>
    public void ForgetSessionOf(AccessDevice device)
    {
        // Un driver desconocido o una contraseña que ya no se puede descifrar
        // no deben impedir el borrado del equipo.
        try { drivers.Find(device.DriverKey)?.Create().ForgetCachedSession(ConnectionOf(device)); }
        catch (Exception ex) { logger.LogDebug(ex, "No se pudo limpiar la sesión cacheada del equipo {Device}.", device.Name); }
    }

    public AccessDeviceDto ToDto(AccessDevice d) => new(
        d.Id, d.Name, d.DriverKey, d.Host, d.Port, d.UseHttps, d.Username, Auth.LocationPaths.Of(d.LocationId), d.Kind,
        d.Model, d.SerialNumber, d.FirmwareVersion, d.MacAddress, d.Doors.Count,
        d.SupportsRemoteControl, d.SupportsEvents, d.SupportsCards, d.SupportsFingerprint, d.SupportsFace,
        d.UserCapacity, d.CardCapacity, d.Enabled, d.Status, d.LastError, d.LastSeenAt, d.CreatedAt, d.UpdatedAt,
        d.Doors.OrderBy(door => door.Number)
            .Select(door => new AccessDoorDto(door.Id, door.AccessDeviceId, door.Number, door.Name, door.Enabled))
            .ToList(),
        d.LocationId,
        SupportsCardCapture(d.DriverKey),
        d.IsupDeviceId,
        d.IsupKeyCiphertext is { Length: > 0 }, DisabledByLicense: d.DisabledByLicense, HolidayNote: d.HolidayNote,
        LiveEvents: d.LiveEvents, HasCamera: d.CameraRtspPort is not null, UseCamera: d.UseCamera,
        HasSpeaker: d.HasSpeaker, UseSpeaker: d.UseSpeaker, IoInputs: d.IoInputs ?? 0, IoOutputs: d.IoOutputs ?? 0, UseIo: d.UseIo,
        PartsNote: d.PartsNote);

    /// <summary>Puerta con su equipo, para el monitoreo en vivo y los niveles de acceso.</summary>
    public AccessDoorStateDto ToDoorDto(AccessDoor door)
    {
        var device = door.AccessDevice
                     ?? throw new InvalidOperationException("La puerta se cargó sin su equipo.");
        return new AccessDoorStateDto(
            door.Id, device.Id, device.Name, device.DriverKey,
            Auth.LocationPaths.Of(door.LocationId ?? device.LocationId), door.Number, door.Name, door.Enabled,
            device.Status, door.Mode.ToString(), door.IsOpen,
            device.SupportsRemoteControl, SupportsDoorStatus(device.DriverKey), door.StateReadAt, door.IsLocked);
    }

    /// <summary>¿El driver de esa marca sabe leer el modo de las puertas?</summary>
    public bool SupportsDoorStatus(string driverKey)
    {
        try { return drivers.Find(driverKey)?.Create().SupportsDoorStatus ?? false; }
        catch (DriverException) { return false; }
    }

    /// <summary>¿El driver de esa marca acepta el padrón del VMS?</summary>
    public bool SupportsPersonSync(string driverKey) => drivers.Find(driverKey)?.SupportsPersonSync ?? false;

    /// <summary>¿El driver de esa marca escucha los eventos que empuja el equipo?</summary>
    public bool SupportsEventStream(string driverKey)
    {
        try { return drivers.Find(driverKey)?.Create().SupportsEventStream ?? false; }
        catch (DriverException) { return false; }
    }

    /// <summary>¿El driver de esa marca sabe bajar huellas al equipo?</summary>
    /// <summary>Formato de huella de los equipos de ese driver.</summary>
    public string FingerprintFormatOf(string driverKey)
    {
        try { return drivers.Find(driverKey)?.Create().FingerprintFormat ?? AccessFingerprintFormats.Hikvision; }
        catch (DriverException) { return AccessFingerprintFormats.Hikvision; }
    }

    public bool SupportsFingerprintImport(string driverKey)
    {
        try { return drivers.Find(driverKey)?.Create().SupportsFingerprintImport ?? false; }
        catch (DriverException) { return false; }
    }

    public bool SupportsFingerprintSync(string driverKey)
    {
        try { return drivers.Find(driverKey)?.Create().SupportsFingerprintSync ?? false; }
        catch (DriverException) { return false; }
    }

    /// <summary>¿El driver de esa marca sabe leer una tarjeta en el lector?</summary>
    public bool SupportsCardCapture(string driverKey)
    {
        try { return drivers.Find(driverKey)?.Create().SupportsCardCapture ?? false; }
        catch (DriverException) { return false; }
    }

    /// <summary>
    /// Deja el equipo esperando una tarjeta y devuelve su número, o null si
    /// nadie la pasó en el rato que el equipo espera. Que vuelva vacío es lo
    /// normal: el panel vuelve a pedirlo mientras el diálogo esté abierto.
    /// </summary>
    public async Task<string?> CaptureCardAsync(AccessDevice device, int? cardReaderNo, CancellationToken ct) =>
        await DriverOf(device).CaptureCardAsync(ConnectionOf(device), cardReaderNo, ct);

    /// <summary>¿El driver de esa marca sabe bajar el rostro al equipo?</summary>
    public bool SupportsFaceSync(string driverKey)
    {
        try { return drivers.Find(driverKey)?.Create().SupportsFaceSync ?? false; }
        catch (DriverException) { return false; }
    }

    // ------------------------------------------------------------------
    // Órdenes sobre puertas
    // ------------------------------------------------------------------

    /// <summary>
    /// Le da una orden a una puerta y deja anotado el modo en que queda. Los
    /// equipos que no saben informar su estado igual muestran algo coherente
    /// en el monitoreo: lo que el VMS les ordenó por última vez.
    /// </summary>
    public async Task CommandDoorAsync(VmsDbContext db, AccessDoor door, AccessDoorCommand command, CancellationToken ct)
    {
        var device = door.AccessDevice
                     ?? throw new InvalidOperationException("La puerta se cargó sin su equipo.");
        await DriverOf(device).ControlDoorAsync(ConnectionOf(device), door.Number, command, ct);

        // Un pulso de apertura no cambia el modo (la puerta sigue siendo
        // "normal", solo se abrió un momento); los otros tres sí.
        door.Mode = command switch
        {
            AccessDoorCommand.RemainOpen => AccessDoorMode.RemainOpen,
            AccessDoorCommand.RemainLocked => AccessDoorMode.RemainLocked,
            AccessDoorCommand.Close => AccessDoorMode.Normal,
            _ => door.Mode == AccessDoorMode.Unknown ? AccessDoorMode.Normal : door.Mode,
        };
        door.StateReadAt = DateTime.UtcNow;
        door.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await hub.SendAsync(VmsHubContract.AccessDoorStateChanged, ToDoorDto(door), s => s.CanViewDoor(door.Id), ct);

        // Lo anotado es lo que se ORDENÓ; el estado real (sensor, cerradura)
        // se relee enseguida para que el monitoreo no espere al próximo sondeo.
        RequestDoorRefresh(device.Id);
    }

    // ------------------------------------------------------------------
    // Relectura inmediata del estado de las puertas
    // ------------------------------------------------------------------

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _doorRefreshPending = new();

    /// <summary>
    /// Pide releer YA el estado de las puertas de un equipo: tras una orden o
    /// cuando llega un evento de puerta (abierta, cerrada, forzada). Las
    /// peticiones que llegan juntas se agrupan en una sola lectura, un segundo
    /// después (lo que tarda el equipo en actualizar su propio estado).
    /// </summary>
    public void RequestDoorRefresh(int deviceId)
    {
        if (!_doorRefreshPending.TryAdd(deviceId, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                _doorRefreshPending.TryRemove(deviceId, out _);
                await RefreshDoorsAsync(deviceId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _doorRefreshPending.TryRemove(deviceId, out _);
                logger.LogDebug(ex, "No se pudo releer el estado de las puertas del equipo {DeviceId}.", deviceId);
            }
        });
    }

    private async Task RefreshDoorsAsync(int deviceId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var device = await db.AccessDevices.Include(d => d.Doors).FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null || !device.Enabled || device.Doors.Count == 0) return;
        var driver = DriverOf(device);
        if (!driver.SupportsDoorStatus) return;
        var statuses = await driver.ReadDoorStatusAsync(ConnectionOf(device), device.Doors.Count, ct);
        await ApplyDoorStatusAsync(db, device, statuses, ct);
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------
    // Sondeo de estado
    // ------------------------------------------------------------------

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        // Un equipo ISUP que se conecta se sondea de inmediato y, si se agregó sin conexión, se completa (puertas y capacidades).
        isup.DeviceOnlineChanged += (isupId, online) =>
        {
            if (online) _ = Task.Run(() => ProvisionIsupAsync(isupId, stoppingToken), stoppingToken);
        };
        if (!ReadTerminalParts)
            logger.LogInformation("Access:ReadTerminalParts = false: no se busca la cámara, el altavoz ni las E/S de los terminales de acceso.");
        // Dar tiempo a que la API y el hub estén arriba antes del primer sondeo.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollAllAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "El sondeo de equipos de control de acceso falló."); }

            try { await _wake.WaitAsync(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollAllAsync(CancellationToken ct)
    {
        List<AccessDevice> devices;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            // Con sus puertas: sin ellas PollOneAsync ve cero puertas y nunca
            // le pregunta al equipo en qué estado están (el monitoreo quedaba en "—").
            devices = await db.AccessDevices.AsNoTracking().Include(d => d.Doors)
                .Where(d => d.Enabled).ToListAsync(ct);
        }
        if (devices.Count == 0) return;

        using var limiter = new SemaphoreSlim(Parallelism);
        var results = await Task.WhenAll(devices.Select(async device =>
        {
            await limiter.WaitAsync(ct);
            try { return (Device: device, Result: await PollOneAsync(device, ct)); }
            finally { limiter.Release(); }
        }));

        using var writeScope = scopeFactory.CreateScope();
        var writeDb = writeScope.ServiceProvider.GetRequiredService<VmsDbContext>();
        bool partsChanged = false;
        foreach (var (snapshot, result) in results)
        {
            var device = await writeDb.AccessDevices.Include(d => d.Doors).FirstOrDefaultAsync(d => d.Id == snapshot.Id, ct);
            if (device is null) continue;
            var previous = device.Status;
            device.Status = result.Status;
            device.LastError = result.Error;
            if (result.Status == AccessDeviceStatus.Online) device.LastSeenAt = DateTime.UtcNow;
            await ApplyDoorStatusAsync(writeDb, device, result.Doors, ct);
            if (result.PartsRead && !device.PartsChecked)
                partsChanged |= await ApplyPartsAsync(writeDb, writeScope.ServiceProvider, device, result.Parts, ct);
            if (previous == result.Status) continue;
            await writeDb.SaveChangesAsync(ct);

            if (result.Status == AccessDeviceStatus.Online && previous != AccessDeviceStatus.Unknown)
                await audit.LogSystemAsync("access", "device-online",
                    targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                    detail: $"El equipo de control de acceso '{device.Name}' ({device.Host}) recuperó la conexión.");
            else if (result.Status != AccessDeviceStatus.Online)
                await audit.LogSystemAsync("access", "device-offline",
                    targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                    detail: $"El equipo de control de acceso '{device.Name}' ({device.Host}) no responde: {result.Error}",
                    success: false);
            await hub.SendTrimmedAsync(VmsHubContract.AccessDeviceStatusChanged, ToDto(device), (s, d) => s.Visible(d), ct);

            // Automatizaciones ("terminal sin conexión → avisar"). Resuelto al
            // vuelo: el motor contiene la acción de puertas, que usa este servicio.
            if (previous != AccessDeviceStatus.Unknown || result.Status != AccessDeviceStatus.Online)
                writeScope.ServiceProvider.GetRequiredService<Workflows.WorkflowEngine>().Publish(
                    Workflows.WorkflowTrigger.FromDeviceStatus("access", device.Id, device.Name, device.Host, device.Model,
                        result.Status.ToString(), result.Error));
        }
        await writeDb.SaveChangesAsync(ct);
        if (partsChanged)
        {
            writeScope.ServiceProvider.GetRequiredService<Auth.UserScopeService>().Invalidate();
            await Api.AccessApi.PartsChangedAsync(writeScope.ServiceProvider, ct);
        }
    }

    /// <summary>
    /// Primera lectura de las partes (cámara, altavoz, entradas y salidas) de un
    /// terminal agregado antes de que el VMS las supiera ver: las que tenga se
    /// agregan como recursos, igual que al darlo de alta hoy. Devuelve true si
    /// cambió alguna.
    /// </summary>
    private async Task<bool> ApplyPartsAsync(VmsDbContext db, IServiceProvider services, AccessDevice device,
        DeviceParts? parts, CancellationToken ct)
    {
        Api.AccessApi.ApplyParts(device, device.Model, parts);
        var changes = await EquipmentParts.SyncAsync(db, device, device.LocationId, services, ct);
        bool any = db.ChangeTracker.HasChanges();
        await db.SaveChangesAsync(ct);
        foreach (var change in changes)
        {
            logger.LogInformation("Terminal '{Device}' ({Host}): {Change}.", device.Name, device.Host, change);
            await audit.LogSystemAsync("access", "device-parts-added",
                targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                detail: $"El terminal de acceso '{device.Name}': el servidor {change}.");
        }
        return any;
    }

    private sealed record PollResult(AccessDeviceStatus Status, string? Error, IReadOnlyList<AccessDoorStatus> Doors,
        bool PartsRead = false, DeviceParts? Parts = null);

    private async Task<PollResult> PollOneAsync(AccessDevice device, CancellationToken ct)
    {
        try
        {
            // Por ISUP: si el equipo no está conectado al servidor, se dice por qué (clave rechazada, sin registrarse…).
            if (device.IsupDeviceId is { } isupId)
            {
                if (!isup.IsOnline(isupId))
                    return new(AccessDeviceStatus.Offline, await Isup.IsupEquipmentRules.NotConnectedMessageAsync(isup, isupHost, isupId, "equipo", ct), []);
            }
            var driver = DriverOf(device);
            var connection = ConnectionOf(device);
            await driver.PingAsync(connection, ct);

            IReadOnlyList<AccessDoorStatus> doors = [];
            if (driver.SupportsDoorStatus && device.Doors.Count > 0)
            {
                // Que el equipo no sepa contestar el estado de sus puertas no
                // lo deja fuera de línea: contestó el ping, está vivo.
                try { doors = await driver.ReadDoorStatusAsync(connection, device.Doors.Count, ct); }
                catch (DriverException ex) { logger.LogDebug(ex, "El equipo {Device} no entregó el estado de sus puertas.", device.Name); }
            }

            // Los agregados antes de que el VMS supiera ver sus partes: se le pregunta una sola vez.
            if (ReadTerminalParts && !device.PartsChecked && device.IsupDeviceId is null && EquipmentParts.Supports(device.DriverKey))
            {
                try
                {
                    if (await driver.ReadPartsAsync(connection, ct) is { } parts)
                        return new(AccessDeviceStatus.Online, null, doors, true, parts);
                }
                catch (DriverException ex) { logger.LogDebug(ex, "El equipo {Device} no dijo qué partes tiene.", device.Name); }
            }
            return new(AccessDeviceStatus.Online, null, doors);
        }
        catch (DriverException ex)
        {
            bool auth = ex.Message.Contains("credenciales", StringComparison.OrdinalIgnoreCase) ||
                        ex.Message.Contains("clave de comunicación", StringComparison.OrdinalIgnoreCase);
            return new(auth ? AccessDeviceStatus.AuthFailed : AccessDeviceStatus.Offline, ex.Message, []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(AccessDeviceStatus.Offline, ex.Message, []);
        }
    }

    /// <summary>
    /// Primer contacto de un equipo ISUP agregado sin conexión: lee su identificación, capacidades y puertas (lo que en un equipo por
    /// IP se hace al darlo de alta). Si sus puertas no caben en la licencia, el equipo queda desactivado y se avisa.
    /// </summary>
    private async Task ProvisionIsupAsync(string isupId, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);   // que termine el intercambio de registro del equipo
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var device = await db.AccessDevices.Include(d => d.Doors).FirstOrDefaultAsync(d => d.IsupDeviceId == isupId, ct);
            if (device is null) return;
            if (device.Model is null || device.Doors.Count == 0)
            {
                var info = await DriverOf(device).ProbeAsync(ConnectionOf(device), ct);
                Api.AccessApi.ApplyProbe(device, info);
                string doors = Api.AccessApi.SyncDoors(device, info.Doors);
                if (device.Enabled && license.Deny(LicenseFeatures.ModuleAccess, LicenseFeatures.AccessDoors,
                        await Api.AccessApi.DoorsInUseAsync(db, device.Id, ct), info.Doors.Count) is not null)
                {
                    device.Enabled = false;
                    device.LastError = $"Se conectó por ISUP pero sus {info.Doors.Count} puerta(s) no caben en la licencia: quedó desactivado.";
                    await audit.LogSystemAsync("license", "license-denied", "access-device", device.Id.ToString(), device.Name,
                        device.LastError, success: false);
                }
                await db.SaveChangesAsync(ct);
                await audit.LogSystemAsync("access", "device-revalidated", "access-device", device.Id.ToString(), device.Name,
                    $"El equipo de control de acceso '{device.Name}' se conectó por ISUP (ID {isupId}): {device.Model ?? "modelo desconocido"}, {doors}.",
                    origin: "server");
                var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<VmsHub>>();
                await hubContext.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "access-devices", cancellationToken: ct);
            }
            RequestReconcile();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogWarning("No se pudo completar el equipo ISUP {Id} en su primer contacto: {Error}", isupId, ex.Message); }
    }

    /// <summary>
    /// Guarda el modo leído de cada puerta y avisa por el hub solo de las que
    /// cambiaron: el monitoreo de un edificio con decenas de puertas no debe
    /// repintarse entero cada minuto.
    /// </summary>
    private async Task ApplyDoorStatusAsync(VmsDbContext db, AccessDevice device,
        IReadOnlyList<AccessDoorStatus> statuses, CancellationToken ct)
    {
        if (statuses.Count == 0) return;
        var changed = new List<AccessDoor>();
        foreach (var status in statuses)
        {
            var door = device.Doors.FirstOrDefault(d => d.Number == status.Number);
            if (door is null) continue;
            // Un equipo que no informa el modo no debe borrar el que dejó
            // anotado la última orden del operador.
            var mode = status.Mode == AccessDoorMode.Unknown ? door.Mode : status.Mode;
            if (door.Mode == mode && door.IsOpen == status.Open && door.IsLocked == status.Locked)
            {
                door.StateReadAt = DateTime.UtcNow;
                continue;
            }
            door.Mode = mode;
            door.IsOpen = status.Open;
            door.IsLocked = status.Locked;
            door.StateReadAt = DateTime.UtcNow;
            changed.Add(door);
        }
        if (changed.Count == 0) return;
        await db.SaveChangesAsync(ct);
        foreach (var door in changed)
        {
            door.AccessDevice = device;
            await hub.SendAsync(VmsHubContract.AccessDoorStateChanged, ToDoorDto(door), s => s.CanViewDoor(door.Id), ct);
        }
    }
}
