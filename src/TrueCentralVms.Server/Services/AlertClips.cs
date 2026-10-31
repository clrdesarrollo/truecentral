using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Repetición del evento de un aviso: el video de sus cámaras desde unos
/// segundos antes del evento hasta unos segundos después, sacado de la
/// grabación del propio equipo (DVR/NVR o tarjeta de la cámara).
///
/// Lo difícil es el DESPUÉS: cuando la ventana de alarma se abre, esos
/// segundos todavía no existen, y el grabador además tarda en dejar
/// disponible lo recién grabado. Por eso el clip no se pide a ciegas: se
/// espera a que el reloj del equipo pase el final del clip y luego se
/// consultan los tramos grabados hasta que lo cubran (o se agote la espera).
///
/// La hora sale del reloj del equipo que informó el evento cuando es el mismo
/// que graba la cámara; si no, de la hora del servidor (con un segundo de
/// margen por lado), y la supervisión de hora de equipos mantiene los relojes
/// alineados.
///
/// Los segundos antes y después salen de la acción «Avisar», de los valores del
/// servidor (Automatizaciones → Repetición del evento; SystemBlobs
/// "alerts.clip") o del propio equipo («Según el equipo»: su pregrabación y
/// posgrabación, leídas por el driver). Lo que tarda cada grabador en dejar
/// disponible lo recién grabado se aprende (SystemBlobs "alerts.clip.lag") y
/// sirve para la cuenta regresiva que ve el operador.
/// </summary>
public sealed class AlertClips(
    DriverRegistry drivers,
    CredentialProtector protector,
    AuditService audit,
    IServiceScopeFactory scopes,
    IConfiguration config,
    ILogger<AlertClips> logger)
{
    /// <summary>Valor de <c>clipPre</c>/<c>clipPost</c> en la acción «Avisar»: los del propio equipo.</summary>
    public const string FromDevice = "device";
    /// <summary>Lo que se guarda en el aviso cuando los segundos salen del equipo.</summary>
    public const int FromDeviceMark = -1;
    private const string SettingsBlob = "alerts.clip";
    private const string LagBlob = "alerts.clip.lag";
    /// <summary>Segundos antes del evento si ni la acción ni el servidor dicen otra cosa.</summary>
    public const int DefaultPreSeconds = 5;
    /// <summary>Segundos después del evento si ni la acción ni el servidor dicen otra cosa.</summary>
    public const int DefaultPostSeconds = 15;
    public const int MaxPreSeconds = 60;
    public const int MaxPostSeconds = 120;

    /// <summary>
    /// Espera mínima después del final del clip antes de preguntarle al
    /// grabador: casi ninguno tiene disponible lo de los últimos segundos.
    /// </summary>
    private const int MinLagSeconds = 3;
    /// <summary>Lo que se supone que tarda un grabador del que todavía no se aprendió nada.</summary>
    private const double DefaultLagSeconds = 5;
    /// <summary>Cuánto vale la pregrabación/posgrabación leída de un equipo.</summary>
    private static readonly TimeSpan WindowCacheFor = TimeSpan.FromHours(1);
    /// <summary>Tope de espera, desde el final del clip, a que la grabación lo cubra.</summary>
    private const int GiveUpSeconds = 90;
    /// <summary>Cuánto vale una consulta de tramos (varios operadores mirando el mismo aviso).</summary>
    private static readonly TimeSpan SegmentsCacheFor = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<RecordingSegment> Segments)> _segments = new();
    private readonly ConcurrentDictionary<string, byte> _missingLogged = new();
    private readonly ConcurrentDictionary<(int Device, int Channel), (DateTime At, RecordWindow? Window)> _windows = new();
    /// <summary>Clips (aviso:canal) que se consultaron antes de estar listos: su espera sirve para aprender.</summary>
    private readonly ConcurrentDictionary<string, byte> _waiting = new();
    private readonly ConcurrentDictionary<string, byte> _learned = new();
    private readonly Lock _sync = new();
    private AlertClipSettingsDto? _settings;
    private Dictionary<int, double>? _lags;
    private DateTime _lagsSavedAt = DateTime.MinValue;

    // ------------------------------------------------------------------
    // Valores del servidor
    // ------------------------------------------------------------------

    /// <summary>Los segundos que rigen cuando la acción «Avisar» no dice otros.</summary>
    public AlertClipSettingsDto Settings
    {
        get
        {
            lock (_sync)
            {
                if (_settings is not null) return _settings;
                try
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
                    string? data = db.SystemBlobs.AsNoTracking().Where(b => b.Name == SettingsBlob).Select(b => b.Data).FirstOrDefault();
                    _settings = data is not null && JsonSerializer.Deserialize<AlertClipSettingsDto>(data) is { } saved
                        ? Clean(saved)
                        // Sin nada guardado: lo de appsettings (o los de fábrica).
                        : Clean(new AlertClipSettingsDto(config.GetValue("Alerts:ClipPreSeconds", DefaultPreSeconds),
                            config.GetValue("Alerts:ClipPostSeconds", DefaultPostSeconds)));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "No se pudieron leer los valores de la repetición del evento; rigen los de fábrica.");
                    return new AlertClipSettingsDto(DefaultPreSeconds, DefaultPostSeconds);
                }
                return _settings;
            }
        }
    }

    public static AlertClipSettingsDto Clean(AlertClipSettingsDto s) =>
        new(Math.Clamp(s.PreSeconds, 0, MaxPreSeconds), Math.Clamp(s.PostSeconds, 5, MaxPostSeconds));

    public async Task SaveSettingsAsync(AlertClipSettingsDto settings, CancellationToken ct)
    {
        var clean = Clean(settings);
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var row = await db.SystemBlobs.FirstOrDefaultAsync(b => b.Name == SettingsBlob, ct);
            if (row is null) db.SystemBlobs.Add(row = new SystemBlob { Name = SettingsBlob });
            row.Data = JsonSerializer.Serialize(clean);
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        lock (_sync) _settings = clean;
    }

    /// <summary>
    /// Segundos antes para un aviso nuevo, según la acción: un número (0-60),
    /// «device» (los del equipo, se resuelven por cámara al pedir el clip) o
    /// nada (los del servidor).
    /// </summary>
    public int Pre(string? configured) =>
        configured == FromDevice ? FromDeviceMark
        : int.TryParse(configured, out int seconds) && seconds >= 0 ? Math.Clamp(seconds, 0, MaxPreSeconds)
        : Settings.PreSeconds;

    /// <summary>Segundos después para un aviso nuevo (5-120), con las mismas reglas que <see cref="Pre"/>.</summary>
    public int Post(string? configured) =>
        configured == FromDevice ? FromDeviceMark
        : int.TryParse(configured, out int seconds) && seconds >= 0 ? Math.Clamp(seconds, 5, MaxPostSeconds)
        : Settings.PostSeconds;

    /// <summary>
    /// Hora de pared sin zona para las columnas <c>timestamp without time
    /// zone</c>: una UTC se pasa a la local del servidor (<paramref name="toLocal"/>);
    /// la hora de un equipo ya es la de su reloj y se guarda tal cual.
    /// </summary>
    public static DateTime WallTime(DateTime value, bool toLocal = true) => DateTime.SpecifyKind(
        toLocal && value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value, DateTimeKind.Unspecified);

    /// <summary>
    /// Estado del clip de una cámara del aviso. <paramref name="channel"/> ya
    /// viene validado (es del aviso y el usuario lo ve).
    /// </summary>
    public async Task<AlertClipDto> StatusAsync(WorkflowAlert alert, Channel channel, CancellationToken ct)
    {
        var device = channel.Device;
        int pre = alert.ClipPreSeconds ?? DefaultPreSeconds;
        int post = alert.ClipPostSeconds ?? DefaultPostSeconds;
        if (pre < 0 || post < 0)
        {
            // «Según el equipo»: su pregrabación y posgrabación; si no las informa, las del servidor.
            var window = await DeviceWindowAsync(device, channel.ChannelNumber, ct);
            if (pre < 0) pre = window is { } w ? Math.Clamp(w.PreSeconds, 0, MaxPreSeconds) : Settings.PreSeconds;
            if (post < 0) post = window is { } w2 ? Math.Clamp(w2.PostSeconds, 5, MaxPostSeconds) : Settings.PostSeconds;
        }
        string key = $"{alert.Id}:{channel.Id}";

        // Hora del evento en el reloj del equipo que GRABA. Si fue él quien
        // informó el evento, su propia hora; si no, la del servidor, y el
        // reloj del equipo se estima igual al del servidor.
        bool exact = alert.EventDeviceAt is not null && alert.EventDeviceId == device.Id;
        DateTime serverEvent = alert.EventAt ?? WallTime(alert.RaisedAt);
        DateTime eventTime = exact ? alert.EventDeviceAt!.Value : serverEvent;
        TimeSpan clockOffset = exact ? eventTime - serverEvent : TimeSpan.Zero;
        int margin = exact ? 0 : 1;
        var start = eventTime.AddSeconds(-(pre + margin));
        var end = eventTime.AddSeconds(post + margin);

        AlertClipDto Result(string state, DateTime from, DateTime to, int remaining = 0, string? message = null) =>
            new(state, device.Id, channel.RtspChannel, from, to, eventTime, remaining, exact, message);

        // Mientras el reloj del equipo no pase el final del clip (más un mínimo)
        // no tiene sentido molestar al grabador. La cuenta regresiva que ve el
        // operador usa lo que este grabador suele tardar (aprendido).
        var deviceNow = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified) + clockOffset;
        double lag = LagOf(device.Id);
        int Remaining() => (int)Math.Max(0, Math.Ceiling((end.AddSeconds(lag) - deviceNow).TotalSeconds));
        if ((end.AddSeconds(MinLagSeconds) - deviceNow).TotalSeconds > 0)
        {
            _waiting.TryAdd(key, 0);
            return Result(AlertClipStates.Recording, start, end, Math.Max(1, Remaining()));
        }
        bool givingUp = (deviceNow - end).TotalSeconds >= GiveUpSeconds;

        if (drivers.Find(device.DriverKey)?.Create() is not { } driver)
            return Result(AlertClipStates.Missing, start, end, message: "El driver del equipo no está disponible.");

        IReadOnlyList<RecordingSegment> segments;
        try
        {
            segments = await SegmentsAsync(driver, device, channel.ChannelNumber, start, end, ct);
        }
        catch (DriverException ex)
        {
            // Sin respuesta del grabador todavía se puede reintentar; pasado el
            // tope ya no: se dice por qué.
            if (!givingUp) { _waiting.TryAdd(key, 0); return Result(AlertClipStates.Recording, start, end, Remaining()); }
            return Result(AlertClipStates.Missing, start, end, message: $"El equipo no respondió la búsqueda de grabaciones: {ex.Message}");
        }

        // Lo que la grabación cubre del clip, uniendo tramos contiguos.
        var covered = Covered(segments, start, end);
        if (covered is { } c && c.From <= start.AddSeconds(1) && c.To >= end.AddSeconds(-1))
        {
            // Se esperó por él: lo que tardó desde el final del clip es lo que tarda este grabador.
            if (_waiting.TryRemove(key, out _) && _learned.TryAdd(key, 0))
                Learn(device.Id, (deviceNow - end).TotalSeconds);
            return Result(AlertClipStates.Ready, start, end);
        }
        if (!givingUp)
        {
            _waiting.TryAdd(key, 0);
            return Result(AlertClipStates.Recording, start, end, Remaining());
        }

        if (covered is { } part && part.To > part.From.AddSeconds(1))
        {
            var missing = new List<string>();
            int before = (int)Math.Round((part.From - start).TotalSeconds);
            int after = (int)Math.Round((end - part.To).TotalSeconds);
            if (before > 0) missing.Add($"faltan {before} s del antes");
            if (after > 0) missing.Add($"faltan {after} s del después");
            return Result(AlertClipStates.Partial, part.From, part.To,
                message: $"El equipo no grabó el clip completo ({string.Join(" y ", missing)}).");
        }

        await LogMissingAsync(alert, channel, start, ct);
        return Result(AlertClipStates.Missing, start, end, message:
            "El equipo no tiene grabación de ese momento. Revise que la cámara grabe en forma continua o por " +
            "evento con pregrabación, que el disco no esté lleno y que el horario de grabación cubra esa hora.");
    }

    /// <summary>Pregrabación y posgrabación del equipo para el canal (una lectura por hora); null = no la informa.</summary>
    private async Task<RecordWindow?> DeviceWindowAsync(Device device, int channelNumber, CancellationToken ct)
    {
        var key = (device.Id, channelNumber);
        if (_windows.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < WindowCacheFor) return cached.Window;
        RecordWindow? window = null;
        if (drivers.Find(device.DriverKey)?.Create() is { } driver)
        {
            try { window = await driver.ReadRecordWindowAsync(DeviceConnections.Of(device, protector), channelNumber, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "No se pudo leer la pregrabación de '{Device}' canal {Channel}.", device.Name, channelNumber);
            }
        }
        _windows[key] = (DateTime.UtcNow, window);
        return window;
    }

    /// <summary>Lo que suele tardar el grabador en dejar disponible lo recién grabado (aprendido).</summary>
    private double LagOf(int deviceId)
    {
        lock (_sync)
        {
            _lags ??= LoadLags();
            return _lags.TryGetValue(deviceId, out double lag) ? lag : DefaultLagSeconds;
        }
    }

    /// <summary>Suma una medición (promedio móvil) y la guarda, a lo más una vez por minuto.</summary>
    private void Learn(int deviceId, double seconds)
    {
        string? toSave = null;
        lock (_sync)
        {
            _lags ??= LoadLags();
            double measured = Math.Clamp(seconds, 0, GiveUpSeconds);
            _lags[deviceId] = _lags.TryGetValue(deviceId, out double old) ? Math.Round(old * 0.7 + measured * 0.3, 1) : Math.Round(measured, 1);
            if (DateTime.UtcNow - _lagsSavedAt > TimeSpan.FromMinutes(1))
            {
                _lagsSavedAt = DateTime.UtcNow;
                toSave = JsonSerializer.Serialize(_lags);
            }
        }
        if (toSave is not null) _ = SaveLagsAsync(toSave);
    }

    private Dictionary<int, double> LoadLags()
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            string? data = db.SystemBlobs.AsNoTracking().Where(b => b.Name == LagBlob).Select(b => b.Data).FirstOrDefault();
            return data is null ? [] : JsonSerializer.Deserialize<Dictionary<int, double>>(data) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No se pudo leer lo aprendido de los grabadores.");
            return [];
        }
    }

    private async Task SaveLagsAsync(string json)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var row = await db.SystemBlobs.FirstOrDefaultAsync(b => b.Name == LagBlob);
            if (row is null) db.SystemBlobs.Add(row = new SystemBlob { Name = LagBlob });
            row.Data = json;
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { logger.LogDebug(ex, "No se pudo guardar lo aprendido de los grabadores."); }
    }

    private async Task<IReadOnlyList<RecordingSegment>> SegmentsAsync(IDeviceDriver driver, Device device,
        int channelNumber, DateTime start, DateTime end, CancellationToken ct)
    {
        string key = $"{device.Id}:{channelNumber}:{start:O}:{end:O}";
        if (_segments.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < SegmentsCacheFor)
            return cached.Segments;
        // Un poco más ancho que el clip: hay firmwares que corren el rango de
        // la búsqueda (ya visto con el horario de verano) y el tramo que
        // empieza justo antes del clip es el que lo cubre.
        var segments = await driver.QueryRecordingsAsync(DeviceConnections.Of(device, protector), channelNumber,
            start.AddMinutes(-5), end.AddMinutes(5), ct);
        _segments[key] = (DateTime.UtcNow, segments);
        if (_segments.Count > 500)
            foreach (var old in _segments.Where(p => DateTime.UtcNow - p.Value.At > TimeSpan.FromMinutes(1)).ToList())
                _segments.TryRemove(old.Key, out _);
        return segments;
    }

    /// <summary>El tramo continuo de grabación que más cubre el clip (null = nada).</summary>
    private static (DateTime From, DateTime To)? Covered(IReadOnlyList<RecordingSegment> segments, DateTime start, DateTime end)
    {
        (DateTime From, DateTime To)? best = null;
        DateTime? runFrom = null, runTo = null;
        foreach (var s in segments.OrderBy(s => s.Start))
        {
            if (runTo is { } to && s.Start <= to.AddSeconds(3))
            {
                if (s.End > to) runTo = s.End;
            }
            else
            {
                Consider();
                runFrom = s.Start;
                runTo = s.End;
            }
        }
        Consider();
        return best;

        void Consider()
        {
            if (runFrom is not { } f || runTo is not { } t) return;
            var from = f < start ? start : f;
            var to = t > end ? end : t;
            if (to <= from) return;
            if (best is not { } b || (to - from) > (b.To - b.From)) best = (from, to);
        }
    }

    /// <summary>
    /// Una vez por aviso y cámara: deja en la bitácora que el equipo no tenía
    /// grabación del momento (sirve para descubrir cámaras que no graban).
    /// </summary>
    private async Task LogMissingAsync(WorkflowAlert alert, Channel channel, DateTime start, CancellationToken ct)
    {
        if (!_missingLogged.TryAdd($"{alert.Id}:{channel.Id}", 0)) return;
        try
        {
            await audit.LogSystemAsync("workflows", "alert-clip-missing",
                targetType: "channel", targetId: $"{channel.DeviceId}/{channel.RtspChannel}",
                targetName: $"{channel.Device.Name} · {channel.Name}",
                detail: $"La repetición del aviso {alert.Id} («{alert.Title}») no encontró grabación en " +
                        $"'{channel.Device.Name}' · {channel.Name} desde el {start:dd-MM-yyyy HH:mm:ss}.",
                success: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "No se pudo registrar la falta de grabación del aviso {Id}.", alert.Id);
        }
    }
}
