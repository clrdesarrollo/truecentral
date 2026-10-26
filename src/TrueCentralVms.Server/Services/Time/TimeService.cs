using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Services.Supervisor;

namespace TrueCentralVms.Server.Services.Time;

/// <summary>
/// Hora oficial del sistema: UNA sola fuente de hora para la base de datos y
/// para los equipos.
///
/// - Consulta servidores NTP externos (por omisión la Hora Oficial de Chile del
///   SHOA y el pool mundial), elige la mediana y, si el reloj de Windows se
///   corrió, lo corrige. Todo lo que el VMS guarda lleva esa hora en UTC.
/// - Atiende a los equipos como servidor NTP (UDP 123) con esa misma hora: las
///   cámaras, grabadores, terminales y paneles quedan alineados con la base.
/// - En una red sin Internet sigue dando la hora del servidor (estrato 10):
///   aunque no sea la oficial, todos los equipos tienen LA MISMA.
///
/// NTP solo transporta UTC. El horario de verano no viaja por NTP: lo aplica
/// cada equipo según la zona horaria que tiene configurada, y esa zona (con
/// sus reglas de cambio de hora) la mantiene la supervisión de Hora y
/// mantenimiento (<see cref="DeviceClockService"/>).
///
/// Si no tiene permiso para mover el reloj de Windows (desarrollo, sin ser
/// servicio) o se le pidió no tocarlo, igual da a los equipos la hora correcta
/// sumándole el desfase medido.
/// </summary>
public sealed class TimeService(
    IServiceScopeFactory scopes,
    IConfiguration config,
    AuditService audit,
    ILogger<TimeService> logger) : SupervisedBackgroundService
{
    public const string SettingsBlob = "time.settings";
    public const string WindowsTimeName = "W32Time";

    /// <summary>A partir de este desfase se corrige el reloj de Windows.</summary>
    private static readonly TimeSpan StepThreshold = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Saltos mayores (como el umbral de pánico de ntpd) exigen que al menos dos
    /// fuentes coincidan: una sola fuente equivocada o suplantada no puede mover
    /// la hora de todo el sistema.
    /// </summary>
    private static readonly TimeSpan PanicThreshold = TimeSpan.FromSeconds(1000);

    /// <summary>Dos fuentes "coinciden" si difieren en menos de esto.</summary>
    private static readonly TimeSpan AgreementWindow = TimeSpan.FromMilliseconds(250);

    /// <summary>Después de esto sin poder consultar las fuentes, la hora deja de considerarse sincronizada.</summary>

    private static readonly TimeSpan SyncValidity = TimeSpan.FromHours(24);

    /// <summary>Estrato con que se da la hora del propio reloj (sin fuente): el de un reloj local de ntpd.</summary>
    private const int LocalStratum = 10;

    /// <summary>Pedidos por segundo que se le atienden a una misma dirección; el resto se descarta.</summary>
    private const int MaxRequestsPerSecond = 16;

    /// <summary>Tope de direcciones que se recuerdan (la lista de clientes no puede crecer sin fin).</summary>
    private const int MaxClients = 5000;

    // ------------------------------------------------------------------
    // Ajustes
    // ------------------------------------------------------------------

    private readonly object _gate = new();
    private TimeSettings? _settings;

    public TimeSettings Current
    {
        get
        {
            lock (_gate)
            {
                if (_settings is not null) return _settings;
                try
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
                    string? data = db.SystemBlobs.AsNoTracking().Where(b => b.Name == SettingsBlob).Select(b => b.Data).FirstOrDefault();
                    _settings = data is null ? new TimeSettings() : JsonSerializer.Deserialize<TimeSettings>(data) ?? new TimeSettings();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "No se pudieron leer los ajustes de la hora; rigen los de fábrica.");
                    return new TimeSettings();
                }
                return _settings;
            }
        }
    }

    public async Task SaveAsync(TimeSettings settings, CancellationToken ct)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var row = await db.SystemBlobs.FirstOrDefaultAsync(b => b.Name == SettingsBlob, ct);
            if (row is null) db.SystemBlobs.Add(row = new SystemBlob { Name = SettingsBlob });
            row.Data = JsonSerializer.Serialize(settings);
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        lock (_gate) _settings = settings;
        RestartListener();
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    /// <summary>Puerto UDP en que atiende (123; otro solo para pruebas en un mismo PC).</summary>
    public int Port => config.GetValue("Time:NtpPort", 123);

    // ------------------------------------------------------------------
    // Estado
    // ------------------------------------------------------------------

    /// <summary>Lo que respondió una fuente en la última consulta.</summary>
    public sealed record SourceResult(string Host, string? Address, double? OffsetMs, double? DelayMs, int? Stratum, string? Error);

    /// <summary>Un equipo que pidió la hora.</summary>
    public sealed record ClientInfo(string Address, DateTime FirstUtc, DateTime LastUtc, long Requests);

    private sealed class ClientStat
    {
        public DateTime FirstUtc;
        public DateTime LastUtc;
        public long Requests;
        public long Second;
        public int InSecond;
    }

    /// <summary>Corrección que se suma al reloj de Windows al dar la hora (ticks): cero si el reloj ya está bien.</summary>
    private long _correctionTicks;

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ConcurrentDictionary<string, ClientStat> _clients = new();
    private CancellationTokenSource? _listenerReset;

    private DateTime? _lastAttemptUtc, _lastSyncUtc, _lastStepUtc;
    private double? _lastOffsetMs, _lastDelayMs, _lastStepMs;
    private int _upstreamStratum;
    private string? _syncSource, _syncAddress, _syncError, _adjustError;
    private byte[] _refId = "LOCL"u8.ToArray();
    private IReadOnlyList<SourceResult> _sources = [];
    private bool _listening;
    private string? _listenerError;
    private long _served;
    private bool _syncFailingLogged;

    /// <summary>La hora que da el servidor: la de Windows más la corrección pendiente.</summary>
    public DateTime UtcNow => DateTime.UtcNow + TimeSpan.FromTicks(Interlocked.Read(ref _correctionTicks));

    /// <summary>Consultó las fuentes hace menos de un día.</summary>
    public bool Synchronized => _lastSyncUtc is { } last && DateTime.UtcNow - last < SyncValidity;

    public object Status()
    {
        var settings = Current;
        var correction = TimeSpan.FromTicks(Interlocked.Read(ref _correctionTicks));
        return new
        {
            serverUtc = UtcNow,
            windowsUtc = DateTime.UtcNow,
            correctionMs = Math.Round(correction.TotalMilliseconds, 1),
            synchronized = Synchronized,
            isolated = settings.Sources.Count == 0,
            stratum = Synchronized ? Math.Min(_upstreamStratum + 1, 15) : LocalStratum,
            lastAttemptUtc = _lastAttemptUtc,
            lastSyncUtc = _lastSyncUtc,
            lastOffsetMs = _lastOffsetMs,
            lastDelayMs = _lastDelayMs,
            syncSource = _syncSource,
            syncAddress = _syncAddress,
            syncError = _syncError,
            lastStepUtc = _lastStepUtc,
            lastStepMs = _lastStepMs,
            adjustError = _adjustError,
            sources = _sources,
            listener = new
            {
                enabled = settings.ServerEnabled,
                listening = _listening,
                port = Port,
                error = _listenerError,
                served = Interlocked.Read(ref _served),
                answering = settings.ServerEnabled && _listening && (Synchronized || settings.ServeWhenUnsynchronized),
            },
            windowsTime = WindowsTimeState(),
        };
    }

    /// <summary>Equipos que pidieron la hora en las últimas 24 horas, del más reciente al más antiguo.</summary>
    public IReadOnlyList<ClientInfo> Clients()
    {
        var since = DateTime.UtcNow - TimeSpan.FromHours(24);
        return _clients.Where(c => c.Value.LastUtc >= since)
            .Select(c => new ClientInfo(c.Key, c.Value.FirstUtc, c.Value.LastUtc, Interlocked.Read(ref c.Value.Requests)))
            .OrderByDescending(c => c.LastUtc).ToList();
    }

    // ------------------------------------------------------------------
    // Ciclo
    // ------------------------------------------------------------------

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        var listener = ListenAsync(stoppingToken);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await SyncAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogWarning(ex, "La consulta de la hora oficial falló."); }
                PruneClients();
                try { await _wake.WaitAsync(TimeSpan.FromMinutes(Math.Clamp(Current.PollMinutes, 1, 1440)), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            await listener;
        }
    }

    /// <summary>Consultar las fuentes ya (botón de la página). Devuelve cuando terminó.</summary>
    public Task SyncNowAsync(CancellationToken ct) => SyncAsync(ct);

    private async Task SyncAsync(CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var settings = Current;
            _lastAttemptUtc = DateTime.UtcNow;
            if (settings.Sources.Count == 0)
            {
                // Red aislada: no hay a quién preguntarle; manda el reloj del servidor tal cual.
                Interlocked.Exchange(ref _correctionTicks, 0);
                _sources = [];
                _syncError = null;
                _syncSource = _syncAddress = null;
                _lastSyncUtc = null;
                return;
            }

            var results = await Task.WhenAll(settings.Sources.Select(h => QuerySourceAsync(h, ct)));
            _sources = results.Select(r => r.Result).ToList();
            var samples = results.Where(r => r.Sample is not null).Select(r => (r.Result, Sample: r.Sample!.Value))
                .OrderBy(s => s.Sample.Offset).ToList();
            if (samples.Count == 0)
            {
                await SyncFailedAsync("ninguna fuente respondió (" +
                    string.Join("; ", _sources.Select(s => $"{s.Host}: {s.Error}")) + ")");
                return;
            }

            // La mediana dice cuál es la hora; de las fuentes que coinciden con
            // ella se usa la más cercana a un reloj de referencia (menor
            // estrato) y, a igual estrato, la de menor retardo.
            var median = samples[samples.Count / 2].Sample.Offset;
            var agree = samples.Where(s => (s.Sample.Offset - median).Duration() <= AgreementWindow).ToList();
            var chosen = agree.OrderBy(s => s.Sample.Stratum).ThenBy(s => s.Sample.Delay).First();
            var offset = chosen.Sample.Offset;
            int agreeing = agree.Count;
            if (offset.Duration() > PanicThreshold && settings.Sources.Count > 1 && agreeing < 2)
            {
                await SyncFailedAsync($"{chosen.Result.Host} dice que el reloj está corrido {Span(offset)} y ninguna otra fuente lo " +
                                      "confirma; no se movió la hora");
                return;
            }

            _lastSyncUtc = DateTime.UtcNow;
            _lastOffsetMs = Math.Round(offset.TotalMilliseconds, 1);
            _lastDelayMs = Math.Round(chosen.Sample.Delay.TotalMilliseconds, 1);
            _upstreamStratum = chosen.Sample.Stratum;
            _syncSource = chosen.Result.Host;
            _syncAddress = chosen.Result.Address;
            _refId = RefIdOf(chosen.Result.Address);
            _syncError = null;
            if (_syncFailingLogged)
            {
                _syncFailingLogged = false;
                await audit.LogSystemAsync("time", "time-sync-restored", targetType: "time", targetName: chosen.Result.Host,
                    detail: $"Se volvió a obtener la hora oficial desde {chosen.Result.Host} ({chosen.Result.Address}).");
            }

            if (settings.AdjustSystemClock && offset.Duration() > StepThreshold)
            {
                string? error = StepSystemClock(offset);
                if (error is null)
                {
                    Interlocked.Exchange(ref _correctionTicks, 0);
                    _lastStepUtc = DateTime.UtcNow;
                    _lastStepMs = _lastOffsetMs;
                    _adjustError = null;
                    logger.LogInformation("Reloj de Windows corregido en {Offset} ms según {Source}.", _lastOffsetMs, chosen.Result.Host);
                    await audit.LogSystemAsync("time", "time-clock-stepped", targetType: "time", targetName: chosen.Result.Host,
                        detail: $"Corrigió el reloj del servidor: estaba {(offset > TimeSpan.Zero ? "atrasado" : "adelantado")} " +
                                $"{Span(offset)} respecto de {chosen.Result.Host} ({chosen.Result.Address}, estrato {chosen.Sample.Stratum}).");
                    return;
                }
                if (_adjustError != error)
                    await audit.LogSystemAsync("time", "time-clock-stepped", targetType: "time", targetName: chosen.Result.Host,
                        detail: $"No se pudo corregir el reloj del servidor, corrido {Span(offset)}: {error}. " +
                                "Los equipos reciben igual la hora correcta.", success: false);
                _adjustError = error;
            }
            else if (!settings.AdjustSystemClock) _adjustError = null;
            Interlocked.Exchange(ref _correctionTicks, offset.Ticks);
        }
        finally { _syncLock.Release(); }
    }

    private async Task SyncFailedAsync(string reason)
    {
        _syncError = reason;
        logger.LogWarning("No se obtuvo la hora oficial: {Reason}.", reason);
        // Avisar una vez por caída, no en cada vuelta.
        if (_syncFailingLogged) return;
        _syncFailingLogged = true;
        await audit.LogSystemAsync("time", "time-sync-failed", targetType: "time",
            detail: $"No se pudo obtener la hora oficial: {reason}." +
                    (Synchronized ? "" : " Los equipos reciben la hora del reloj del servidor."), success: false);
    }

    // ------------------------------------------------------------------
    // Cliente NTP (hacia las fuentes)
    // ------------------------------------------------------------------

    private readonly record struct Sample(TimeSpan Offset, TimeSpan Delay, int Stratum);

    private async Task<(SourceResult Result, Sample? Sample)> QuerySourceAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            if (IPAddress.TryParse(host, out var literal)) addresses = [literal];
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                addresses = (await Dns.GetHostAddressesAsync(host, timeout.Token))
                    .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return (new SourceResult(host, null, null, null, null, "no se pudo resolver el nombre"), null);
        }
        if (addresses.Length == 0) return (new SourceResult(host, null, null, null, null, "el nombre no tiene direcciones"), null);

        // Hasta dos direcciones (un pool entrega varias) y tres consultas a cada
        // una: la de menor retardo es la que menos error tiene.
        Sample? best = null;
        IPAddress? bestAddress = null;
        string? error = null;
        foreach (var address in addresses.Take(2))
        {
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) await Task.Delay(250, ct);
                try
                {
                    var sample = await QueryAsync(address, ct);
                    if (best is null || sample.Delay < best.Value.Delay) (best, bestAddress) = (sample, address);
                }
                catch (NtpException ex) { error = ex.Message; }
            }
            if (best is not null) break;
        }
        return best is { } b
            ? (new SourceResult(host, bestAddress!.ToString(), Math.Round(b.Offset.TotalMilliseconds, 1),
                Math.Round(b.Delay.TotalMilliseconds, 1), b.Stratum, null), b)
            : (new SourceResult(host, addresses[0].ToString(), null, null, null, error ?? "no respondió"), null);
    }

    private sealed class NtpException(string message) : Exception(message);

    private static async Task<Sample> QueryAsync(IPAddress address, CancellationToken ct)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        var request = new byte[NtpWire.PacketSize];
        request[0] = NtpWire.Header(0, 4, NtpWire.ModeClient);
        // La marca de envío es a la vez la prueba anti-suplantación: la
        // respuesta tiene que devolverla tal cual en "origen". Se le mezclan
        // bits al azar en la parte más baja de la fracción (por debajo del
        // microsegundo) para que no se pueda adivinar.
        var t1 = DateTime.UtcNow;
        NtpWire.WriteTimestamp(request.AsSpan(NtpWire.TransmitOffset), t1);
        request[NtpWire.TransmitOffset + 7] = (byte)Random.Shared.Next(256);

        var endpoint = new IPEndPoint(address, 123);
        var buffer = new byte[256];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await socket.SendToAsync(request, SocketFlags.None, endpoint, timeout.Token);
            while (true)
            {
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None,
                    new IPEndPoint(address.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0), timeout.Token);
                var t4 = DateTime.UtcNow;
                if (!((IPEndPoint)received.RemoteEndPoint).Address.Equals(address) || received.ReceivedBytes < NtpWire.PacketSize) continue;
                var reply = buffer.AsSpan(0, NtpWire.PacketSize);
                if (!reply.Slice(NtpWire.OriginOffset, 8).SequenceEqual(request.AsSpan(NtpWire.TransmitOffset, 8))) continue;

                if (NtpWire.Mode(reply) != NtpWire.ModeServer) throw new NtpException("respuesta que no es de un servidor");
                int stratum = NtpWire.Stratum(reply);
                if (stratum == 0)
                    throw new NtpException($"el servidor pide no consultarlo ({Encoding.ASCII.GetString(reply.Slice(NtpWire.RefIdOffset, 4)).Trim('\0')})");
                if (stratum > 15 || NtpWire.Leap(reply) == NtpWire.LeapUnsynchronized)
                    throw new NtpException("el servidor no está sincronizado");
                if (NtpWire.ReadTimestamp(reply.Slice(NtpWire.ReceiveOffset), t1) is not { } t2 ||
                    NtpWire.ReadTimestamp(reply.Slice(NtpWire.TransmitOffset), t1) is not { } t3)
                    throw new NtpException("respuesta sin marcas de tiempo");

                var offset = ((t2 - t1) + (t3 - t4)) / 2;
                var delay = (t4 - t1) - (t3 - t2);
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                if (delay > TimeSpan.FromSeconds(1)) throw new NtpException("la respuesta tardó demasiado");
                return new Sample(offset, delay, stratum);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NtpException("no respondió");
        }
        catch (SocketException ex)
        {
            throw new NtpException($"error de red ({ex.SocketErrorCode})");
        }
    }

    /// <summary>Identificador de referencia para estrato 2 o más: la IPv4 de la fuente (o el comienzo de un resumen de la IPv6).</summary>
    private static byte[] RefIdOf(string? address)
    {
        if (address is null || !IPAddress.TryParse(address, out var ip)) return "LOCL"u8.ToArray();
        if (ip.AddressFamily == AddressFamily.InterNetwork) return ip.GetAddressBytes();
        return System.Security.Cryptography.MD5.HashData(ip.GetAddressBytes())[..4];
    }

    // ------------------------------------------------------------------
    // Reloj de Windows
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSystemTime(ref SystemTime time);

    /// <summary>
    /// Mueve el reloj de Windows. SetSystemTime habilita por su cuenta el
    /// privilegio de cambiar la hora, que tiene el servicio (LocalSystem) pero
    /// no un usuario común. Devuelve el error en palabras, o null.
    /// </summary>
    private static string? StepSystemClock(TimeSpan offset)
    {
        if (!OperatingSystem.IsWindows()) return "solo se puede en Windows";
        var target = DateTime.UtcNow + offset;
        var time = new SystemTime
        {
            Year = (ushort)target.Year, Month = (ushort)target.Month, DayOfWeek = (ushort)target.DayOfWeek,
            Day = (ushort)target.Day, Hour = (ushort)target.Hour, Minute = (ushort)target.Minute,
            Second = (ushort)target.Second, Milliseconds = (ushort)target.Millisecond,
        };
        if (SetSystemTime(ref time)) return null;
        int code = Marshal.GetLastWin32Error();
        return code == 1314
            ? "el servidor no tiene permiso para cambiar la hora de Windows (no está corriendo como servicio)"
            : $"Windows rechazó el cambio (error {code})";
    }

    /// <summary>Cómo está el servicio Hora de Windows, que también mueve el reloj y suele ocupar el puerto 123.</summary>
    public static object WindowsTimeState()
    {
        if (!OperatingSystem.IsWindows()) return new { installed = false };
        try
        {
            using var service = new ServiceController(WindowsTimeName);
            return new
            {
                installed = true,
                running = service.Status != ServiceControllerStatus.Stopped,
                disabled = service.StartType == ServiceStartMode.Disabled,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new { installed = false };
        }
    }

    /// <summary>
    /// Detiene y deshabilita el servicio Hora de Windows: dos programas
    /// corrigiendo el mismo reloj se pelean, y el de Windows ocupa el puerto
    /// 123. Devuelve el error en palabras, o null.
    /// </summary>
    public async Task<string?> DisableWindowsTimeAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return "solo se puede en Windows";
        try
        {
            using (var service = new ServiceController(WindowsTimeName))
            {
                if (service.Status != ServiceControllerStatus.Stopped)
                {
                    service.Stop();
                    await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)), ct);
                }
            }
            // Deshabilitado también anula sus arranques por disparador (red, dominio).
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\W32Time", "Start", 4, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                       or UnauthorizedAccessException or System.Security.SecurityException
                                       or System.ServiceProcess.TimeoutException)
        {
            return ex is System.ServiceProcess.TimeoutException
                ? "el servicio Hora de Windows no se detuvo a tiempo"
                : $"no se pudo detener o deshabilitar el servicio Hora de Windows ({ex.Message.TrimEnd('.')}); " +
                  "el servidor tiene que correr como servicio de Windows";
        }
        RestartListener();
        return null;
    }

    // ------------------------------------------------------------------
    // Servidor NTP (hacia los equipos)
    // ------------------------------------------------------------------

    private void RestartListener()
    {
        CancellationTokenSource? reset;
        lock (_gate) reset = _listenerReset;
        try { reset?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var reset = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_gate) _listenerReset = reset;
            var settings = Current;
            if (!settings.ServerEnabled)
            {
                _listening = false;
                _listenerError = null;
                await WaitAsync(Timeout.InfiniteTimeSpan, reset.Token);
                continue;
            }

            Socket socket;
            try
            {
                socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
                // Sin esto Windows corta la recepción con WSAECONNRESET cuando un
                // equipo se apaga y vuelve un ICMP "puerto inalcanzable".
                const int SioUdpConnReset = unchecked((int)0x9800000C);
                socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
                socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(IPAddress.IPv6Any, Port));
            }
            catch (SocketException ex)
            {
                _listening = false;
                bool busy = ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied;
                string error = busy
                    ? $"el puerto UDP {Port} está ocupado por otro programa" +
                      (WindowsTimeRunning() ? " (el servicio Hora de Windows)" : "")
                    : $"no se pudo abrir el puerto UDP {Port} ({ex.SocketErrorCode})";
                if (_listenerError != error)
                {
                    logger.LogWarning("Servidor NTP sin atender: {Error}.", error);
                    await audit.LogSystemAsync("time", "time-server-failed", targetType: "time",
                        detail: $"El servidor NTP no pudo empezar a atender: {error}.", success: false);
                }
                _listenerError = error;
                await WaitAsync(TimeSpan.FromMinutes(1), reset.Token);
                continue;
            }

            using (socket)
            {
                _listening = true;
                _listenerError = null;
                logger.LogInformation("Servidor NTP atendiendo en UDP {Port}.", Port);
                var buffer = new byte[512];
                var response = new byte[NtpWire.PacketSize];
                while (!reset.IsCancellationRequested)
                {
                    SocketReceiveFromResult received;
                    try
                    {
                        received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.IPv6Any, 0), reset.Token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (SocketException) { continue; }
                    var receivedAt = UtcNow;
                    try
                    {
                        if (Answer(buffer.AsSpan(0, received.ReceivedBytes), (IPEndPoint)received.RemoteEndPoint, receivedAt, response))
                        {
                            await socket.SendToAsync(response, SocketFlags.None, received.RemoteEndPoint, reset.Token);
                            Interlocked.Increment(ref _served);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (SocketException) { /* el equipo ya no está: nada que hacer */ }
                }
                _listening = false;
            }
        }
        _listening = false;
    }

    private static async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }

    private static bool WindowsTimeRunning()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var service = new ServiceController(WindowsTimeName);
            return service.Status != ServiceControllerStatus.Stopped;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>
    /// Arma la respuesta a un pedido de hora. Solo se contesta el modo
    /// cliente (3): los modos de control (6) y privado (7) son los que se usan
    /// para amplificar ataques y nunca se responden. La respuesta mide lo
    /// mismo que el pedido, así que no amplifica nada.
    /// </summary>
    private bool Answer(ReadOnlySpan<byte> request, IPEndPoint remote, DateTime receivedAt, byte[] response)
    {
        if (request.Length < NtpWire.PacketSize) return false;
        int version = NtpWire.Version(request);
        if (NtpWire.Mode(request) != NtpWire.ModeClient || version is < 1 or > 4) return false;

        var address = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
        if (!Admit(address.ToString())) return false;

        var settings = Current;
        bool synced = Synchronized;
        if (!synced && !settings.ServeWhenUnsynchronized) return false;

        var span = response.AsSpan();
        span.Clear();
        span[0] = NtpWire.Header(0, version, NtpWire.ModeServer);
        span[1] = (byte)(synced ? Math.Min(_upstreamStratum + 1, 15) : LocalStratum);
        span[2] = request[2];                     // el intervalo de consulta que pidió
        span[3] = unchecked((byte)-20);           // precisión ≈ 1 µs (2^-20 s)
        var sinceSync = synced ? DateTime.UtcNow - _lastSyncUtc!.Value : TimeSpan.Zero;
        NtpWire.WriteShort(span[4..], synced ? TimeSpan.FromMilliseconds(_lastDelayMs ?? 0) : TimeSpan.Zero);
        // Dispersión: lo que puede haberse corrido el reloj desde la última consulta (15 ppm, como NTP).
        NtpWire.WriteShort(span[8..], TimeSpan.FromMilliseconds(10) + TimeSpan.FromTicks((long)(sinceSync.Ticks * 15e-6)));
        (synced ? _refId : "LOCL"u8.ToArray()).CopyTo(span[NtpWire.RefIdOffset..]);
        NtpWire.WriteTimestamp(span[NtpWire.ReferenceOffset..], synced ? _lastSyncUtc!.Value : receivedAt.AddSeconds(-1));
        request.Slice(NtpWire.TransmitOffset, 8).CopyTo(span[NtpWire.OriginOffset..]);
        NtpWire.WriteTimestamp(span[NtpWire.ReceiveOffset..], receivedAt);
        NtpWire.WriteTimestamp(span[NtpWire.TransmitOffset..], UtcNow);
        return true;
    }

    /// <summary>Anota al equipo y decide si se le contesta (tope de pedidos por segundo).</summary>
    private bool Admit(string address)
    {
        var now = DateTime.UtcNow;
        if (!_clients.TryGetValue(address, out var stat))
        {
            if (_clients.Count >= MaxClients) return false;
            stat = _clients.GetOrAdd(address, _ => new ClientStat { FirstUtc = now });
        }
        lock (stat)
        {
            long second = Stopwatch.GetTimestamp() / Stopwatch.Frequency;
            if (stat.Second != second) (stat.Second, stat.InSecond) = (second, 0);
            if (++stat.InSecond > MaxRequestsPerSecond) return false;
            stat.LastUtc = now;
            stat.Requests++;
        }
        return true;
    }

    private void PruneClients()
    {
        var since = DateTime.UtcNow - TimeSpan.FromHours(24);
        foreach (var (key, stat) in _clients)
            if (stat.LastUtc < since) _clients.TryRemove(key, out _);
    }

    // ------------------------------------------------------------------
    // Apoyo
    // ------------------------------------------------------------------

    /// <summary>
    /// IP de este servidor por la que se llega a un equipo: un socket UDP
    /// "conectado" no envía nada pero resuelve la ruta. Con varias tarjetas de
    /// red es la dirección que hay que darle al equipo como servidor NTP.
    /// </summary>
    public static string? LocalAddressToward(string host)
    {
        try
        {
            var target = IPAddress.TryParse(host, out var ip) ? ip
                : Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (target is null || target.AddressFamily != AddressFamily.InterNetwork) return null;
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(target, 123));
            var local = (probe.LocalEndPoint as IPEndPoint)?.Address;
            return local is null || IPAddress.IsLoopback(local) && !IPAddress.IsLoopback(target) ? null : local.ToString();
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>"350 ms", "3 s", "2 min 5 s", "11 h".</summary>
    public static string Span(TimeSpan value)
    {
        var v = value.Duration();
        if (v.TotalSeconds < 1) return $"{v.TotalMilliseconds:0} ms";
        if (v.TotalSeconds < 60) return $"{v.TotalSeconds:0.#} s";
        return DeviceClockService.Span(v);
    }
}
