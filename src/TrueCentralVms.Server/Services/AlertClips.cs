using System.Collections.Concurrent;
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
/// </summary>
public sealed class AlertClips(
    DriverRegistry drivers,
    CredentialProtector protector,
    AuditService audit,
    ILogger<AlertClips> logger)
{
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
    private const int RecorderLagSeconds = 5;
    /// <summary>Tope de espera, desde el final del clip, a que la grabación lo cubra.</summary>
    private const int GiveUpSeconds = 90;
    /// <summary>Cuánto vale una consulta de tramos (varios operadores mirando el mismo aviso).</summary>
    private static readonly TimeSpan SegmentsCacheFor = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<RecordingSegment> Segments)> _segments = new();
    private readonly ConcurrentDictionary<string, byte> _missingLogged = new();

    /// <summary>Segundos antes: los de la acción (0-60) o, sin ellos (negativo), los del servidor.</summary>
    public static int Pre(int configured, IConfiguration config) => Math.Clamp(
        configured >= 0 ? configured : config.GetValue("Alerts:ClipPreSeconds", DefaultPreSeconds), 0, MaxPreSeconds);

    /// <summary>Segundos después: los de la acción (5-120) o, sin ellos (negativo), los del servidor.</summary>
    public static int Post(int configured, IConfiguration config) => Math.Clamp(
        configured >= 0 ? configured : config.GetValue("Alerts:ClipPostSeconds", DefaultPostSeconds), 5, MaxPostSeconds);

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

        // Mientras el reloj del equipo no pase el final del clip (más lo que
        // tarda en dejarlo disponible) no tiene sentido molestar al grabador.
        var deviceNow = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified) + clockOffset;
        double untilReady = (end.AddSeconds(RecorderLagSeconds) - deviceNow).TotalSeconds;
        if (untilReady > 0)
            return Result(AlertClipStates.Recording, start, end, (int)Math.Ceiling(untilReady));
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
            if (!givingUp) return Result(AlertClipStates.Recording, start, end);
            return Result(AlertClipStates.Missing, start, end, message: $"El equipo no respondió la búsqueda de grabaciones: {ex.Message}");
        }

        // Lo que la grabación cubre del clip, uniendo tramos contiguos.
        var covered = Covered(segments, start, end);
        if (covered is { } c && c.From <= start.AddSeconds(1) && c.To >= end.AddSeconds(-1))
            return Result(AlertClipStates.Ready, start, end);
        if (!givingUp)
            return Result(AlertClipStates.Recording, start, end);

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
