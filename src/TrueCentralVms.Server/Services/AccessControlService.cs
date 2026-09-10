using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;

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
    IHubContext<VmsHub> hub,
    AuditService audit,
    ILogger<AccessControlService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    /// <summary>Equipos que se sondean a la vez (los ISAPI son lentos y limitan sesiones).</summary>
    private const int Parallelism = 4;

    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>La configuración cambió: sondear ahora en vez de esperar el ciclo.</summary>
    public void RequestReconcile()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    public AccessConnectionInfo ConnectionOf(AccessDevice device) =>
        new(device.Host, device.Port, device.UseHttps, device.Username, credentials.Unprotect(device.PasswordCiphertext));

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
        d.Id, d.Name, d.DriverKey, d.Host, d.Port, d.UseHttps, d.Username, d.Location, d.Kind,
        d.Model, d.SerialNumber, d.FirmwareVersion, d.MacAddress, d.Doors.Count,
        d.SupportsRemoteControl, d.SupportsEvents, d.SupportsCards, d.SupportsFingerprint, d.SupportsFace,
        d.UserCapacity, d.CardCapacity, d.Enabled, d.Status, d.LastError, d.LastSeenAt, d.CreatedAt, d.UpdatedAt,
        d.Doors.OrderBy(door => door.Number)
            .Select(door => new AccessDoorDto(door.Id, door.AccessDeviceId, door.Number, door.Name, door.Enabled))
            .ToList());

    /// <summary>Puerta con su equipo, para el monitoreo en vivo y los niveles de acceso.</summary>
    public AccessDoorStateDto ToDoorDto(AccessDoor door)
    {
        var device = door.AccessDevice
                     ?? throw new InvalidOperationException("La puerta se cargó sin su equipo.");
        return new AccessDoorStateDto(
            door.Id, device.Id, device.Name, device.DriverKey, device.Location, door.Number, door.Name, door.Enabled,
            device.Status, door.Mode.ToString(), door.IsOpen,
            device.SupportsRemoteControl, SupportsDoorStatus(device.DriverKey), door.StateReadAt);
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
        await hub.Clients.All.SendAsync(VmsHubContract.AccessDoorStateChanged, ToDoorDto(door), ct);
    }

    // ------------------------------------------------------------------
    // Sondeo de estado
    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
            devices = await db.AccessDevices.AsNoTracking().Where(d => d.Enabled).ToListAsync(ct);
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
        foreach (var (snapshot, result) in results)
        {
            var device = await writeDb.AccessDevices.Include(d => d.Doors).FirstOrDefaultAsync(d => d.Id == snapshot.Id, ct);
            if (device is null) continue;
            var previous = device.Status;
            device.Status = result.Status;
            device.LastError = result.Error;
            if (result.Status == AccessDeviceStatus.Online) device.LastSeenAt = DateTime.UtcNow;
            await ApplyDoorStatusAsync(writeDb, device, result.Doors, ct);
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
            await hub.Clients.All.SendAsync(VmsHubContract.AccessDeviceStatusChanged, ToDto(device), ct);
        }
        await writeDb.SaveChangesAsync(ct);
    }

    private async Task<(AccessDeviceStatus Status, string? Error, IReadOnlyList<AccessDoorStatus> Doors)> PollOneAsync(
        AccessDevice device, CancellationToken ct)
    {
        try
        {
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
            return (AccessDeviceStatus.Online, null, doors);
        }
        catch (DriverException ex)
        {
            bool auth = ex.Message.Contains("credenciales", StringComparison.OrdinalIgnoreCase) ||
                        ex.Message.Contains("clave de comunicación", StringComparison.OrdinalIgnoreCase);
            return (auth ? AccessDeviceStatus.AuthFailed : AccessDeviceStatus.Offline, ex.Message, []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (AccessDeviceStatus.Offline, ex.Message, []);
        }
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
            if (door.Mode == status.Mode && door.IsOpen == status.Open)
            {
                door.StateReadAt = DateTime.UtcNow;
                continue;
            }
            door.Mode = status.Mode;
            door.IsOpen = status.Open;
            door.StateReadAt = DateTime.UtcNow;
            changed.Add(door);
        }
        if (changed.Count == 0) return;
        await db.SaveChangesAsync(ct);
        foreach (var door in changed)
        {
            door.AccessDevice = device;
            await hub.Clients.All.SendAsync(VmsHubContract.AccessDoorStateChanged, ToDoorDto(door), ct);
        }
    }
}
