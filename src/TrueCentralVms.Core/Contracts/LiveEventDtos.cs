namespace TrueCentralVms.Core.Contracts;

/// <summary>
/// Categorías de los eventos en vivo (Cuadro vigía y cinta de eventos). El
/// servidor clasifica cada evento en una; el vigía elige cuáles muestra.
/// </summary>
public static class LiveEventCategories
{
    /// <summary>Alarma de un panel: zona disparada o sensor interrumpido.</summary>
    public const string Alarm = "alarm";
    /// <summary>Armado, desarmado, falla o anulación de un panel.</summary>
    public const string AlarmSystem = "alarm-system";
    public const string AccessDenied = "access-denied";
    public const string AccessGranted = "access-granted";
    /// <summary>Puerta forzada, abierta demasiado tiempo o alarma del terminal.</summary>
    public const string DoorAlarm = "door-alarm";
    /// <summary>Analíticas de video: cruce de línea, intrusión, merodeo…</summary>
    public const string VideoAnalytics = "video-analytics";
    public const string VideoMotion = "video-motion";
    /// <summary>Pérdida de video o cámara tapada.</summary>
    public const string VideoHealth = "video-health";
    /// <summary>Patente leída que está en el padrón y autorizada.</summary>
    public const string Plate = "plate";
    /// <summary>Patente no autorizada o fuera del padrón.</summary>
    public const string PlateFlagged = "plate-flagged";
    public const string Fence = "fence";
    /// <summary>Entrada de un módulo de E/S que se activa.</summary>
    public const string IoInput = "io-input";
    /// <summary>Llamada de un frente de citofonía.</summary>
    public const string Intercom = "intercom";
    /// <summary>Aviso de una automatización (acción «Avisar»).</summary>
    public const string Alert = "alert";

    /// <summary>Todas, en el orden en que se ofrecen al configurar el vigía.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Alert, Alarm, AlarmSystem, DoorAlarm, AccessDenied, AccessGranted, VideoAnalytics, VideoMotion,
        VideoHealth, PlateFlagged, Plate, Fence, IoInput, Intercom,
    ];

    /// <summary>Las que vigila un vigía recién puesto (sin las que inundan: movimiento, accesos concedidos…).</summary>
    public static readonly IReadOnlyList<string> Default =
        [Alert, Alarm, DoorAlarm, AccessDenied, VideoAnalytics, VideoHealth, PlateFlagged, Fence, IoInput, Intercom];

    public static string Label(string category) => category switch
    {
        Alert => "Avisos de automatizaciones",
        Alarm => "Alarmas de paneles",
        AlarmSystem => "Armado, desarmado y fallas de paneles",
        DoorAlarm => "Alarmas de puertas",
        AccessDenied => "Accesos denegados",
        AccessGranted => "Accesos concedidos",
        VideoAnalytics => "Analíticas de video (cruce de línea, intrusión…)",
        VideoMotion => "Detección de movimiento",
        VideoHealth => "Pérdida de video y sabotaje",
        PlateFlagged => "Patentes no autorizadas o fuera del padrón",
        Plate => "Patentes autorizadas",
        Fence => "Alarmas de cerco eléctrico",
        IoInput => "Entradas de módulos de E/S",
        Intercom => "Llamadas de citofonía",
        _ => category,
    };
}

/// <summary>
/// Un evento en vivo, ya resuelto a su recurso y sus cámaras (las que el
/// usuario puede ver, la principal primero). Lo empuja el hub
/// (<c>LiveEvent</c>) y lo devuelve <c>/api/live-events/recent</c>.
/// </summary>
/// <param name="Id">Estable para deduplicar: un repetido llega con el mismo Id y más <paramref name="Repeats"/>.</param>
/// <param name="At">Hora del evento (UTC).</param>
/// <param name="Category"><see cref="LiveEventCategories"/>.</param>
/// <param name="ResourceKey">Recurso de origen ("Door:5"); null si no se pudo resolver.</param>
/// <param name="LocationIds">Ubicación del recurso y sus ancestros (para filtrar por ubicación).</param>
/// <param name="AlertId">Si es un aviso: su id (abre la ventana de alarma).</param>
/// <param name="Repeats">Veces que se repitió dentro de la ventana de antirrebote.</param>
public sealed record LiveEventDto(
    string Id,
    DateTime At,
    string Category,
    AlarmSeverity Severity,
    string Title,
    string? Detail,
    string? ResourceKey,
    string? ResourceName,
    string? LocationPath,
    IReadOnlyList<int> LocationIds,
    IReadOnlyList<int> ChannelIds,
    long? AlertId = null,
    int Repeats = 0);
