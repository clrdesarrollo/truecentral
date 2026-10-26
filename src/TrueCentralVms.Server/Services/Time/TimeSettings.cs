namespace TrueCentralVms.Server.Services.Time;

/// <summary>Ajustes de Sistema → Red → Hora (NTP) (SystemBlobs "time.settings").</summary>
public sealed class TimeSettings
{
    /// <summary>Servidores NTP de los que el servidor toma la hora. Vacío = red aislada: manda el reloj del servidor.</summary>
    public List<string> Sources { get; set; } = [.. DefaultSources];

    /// <summary>Corregir el reloj de Windows con la hora de las fuentes (lo normal: la base guarda esa hora).</summary>
    public bool AdjustSystemClock { get; set; } = true;

    /// <summary>Atender a los equipos por NTP (UDP 123).</summary>
    public bool ServerEnabled { get; set; } = true;

    /// <summary>
    /// Dar la hora aunque no se haya podido consultar ninguna fuente (red sin
    /// Internet): mejor que todos los equipos tengan LA MISMA hora, la del
    /// servidor, a que cada uno corra con la suya.
    /// </summary>
    public bool ServeWhenUnsynchronized { get; set; } = true;

    /// <summary>Cada cuánto se consultan las fuentes.</summary>
    public int PollMinutes { get; set; } = 16;

    public DateTime? UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>
    /// Hora Oficial de Chile (SHOA, la Armada) y el pool mundial de respaldo.
    /// No se mezclan servidores con "leap smear" (Google, AWS): reparten el
    /// segundo intercalar y discrepan de los demás durante horas.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSources = ["ntp.shoa.cl", "pool.ntp.org"];
}
