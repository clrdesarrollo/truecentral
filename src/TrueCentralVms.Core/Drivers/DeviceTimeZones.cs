using System.Globalization;
using System.Text.RegularExpressions;
using TransitionTime = System.TimeZoneInfo.TransitionTime;

namespace TrueCentralVms.Core.Drivers;

/// <summary>
/// Zonas horarias de los equipos: armar la zona en el formato que entienden
/// (el POSIX de Hikvision), leer la que traen, explicarla en palabras y decidir
/// si coincide con la que deberían tener.
///
/// Hikvision la escribe como POSIX con dos particularidades: el signo va
/// INVERTIDO respecto de UTC (UTC−4 es "CST+4:00:00") y lo que sigue a "DST"
/// es lo que se ADELANTA el reloj, no la zona de verano completa:
/// <c>CST+4:00:00DST01:00:00,M9.1.6/24:00:00,M4.1.6/24:00:00</c> = UTC−4, en
/// verano una hora más, del primer sábado de septiembre a medianoche al primer
/// sábado de abril a medianoche (Chile).
/// </summary>
public static partial class DeviceTimeZones
{
    /// <summary>La regla de horario de verano vigente en <paramref name="at"/> (hoy si no se indica).</summary>
    public static TimeZoneInfo.AdjustmentRule? CurrentRule(TimeZoneInfo tz, DateTime? at = null)
    {
        if (!tz.SupportsDaylightSavingTime) return null;
        var day = (at ?? DateTime.Today).Date;
        return tz.GetAdjustmentRules().FirstOrDefault(r => r.DateStart <= day && day <= r.DateEnd);
    }

    /// <summary>
    /// Zona en formato POSIX de Hikvision, con su horario de verano si la zona
    /// lo tiene y la regla es de "n-ésimo día del mes" (las de fecha fija no
    /// tienen forma de escribirse: quedan sin horario de verano).
    /// </summary>
    public static string Posix(TimeZoneInfo tz)
    {
        string text = "CST" + PosixOffset(tz.BaseUtcOffset);
        var rule = CurrentRule(tz);
        if (rule is null || rule.DaylightTransitionStart.IsFixedDateRule || rule.DaylightTransitionEnd.IsFixedDateRule)
            return text;
        var delta = rule.DaylightDelta;
        return $"{text}DST{delta.Hours:00}:{delta.Minutes:00}:00," +
               $"{PosixTransition(rule.DaylightTransitionStart)},{PosixTransition(rule.DaylightTransitionEnd)}";
    }

    /// <summary>
    /// Las formas de escribir la zona, de la más completa a la más pobre, para
    /// ir probando: no todos los firmware aceptan lo mismo. Los terminales de
    /// acceso (DS-K1T) rechazan la hora 24:00:00 de las transiciones con
    /// <c>badXmlContent</c>, así que la segunda la cambia por 23:59:59; la
    /// última es solo el desfase, sin horario de verano.
    /// </summary>
    public static IReadOnlyList<string> PosixCandidates(TimeZoneInfo tz)
    {
        string full = Posix(tz);
        var list = new List<string> { full };
        if (full.Contains("/24:00:00")) list.Add(full.Replace("/24:00:00", "/23:59:59"));
        int dst = full.IndexOf("DST", StringComparison.Ordinal);
        if (dst > 0) list.Add(full[..dst]);
        return list;
    }

    /// <summary>"+4:00:00" para UTC−4: POSIX invierte el signo.</summary>
    private static string PosixOffset(TimeSpan utcOffset)
    {
        var o = -utcOffset;
        return $"{(o < TimeSpan.Zero ? "-" : "+")}{Math.Abs(o.Hours)}:{Math.Abs(o.Minutes):00}:00";
    }

    private static string PosixTransition(TransitionTime t)
    {
        // TimeOfDay 23:59:59.999 (las reglas "a medianoche") se escribe 24:00:00.
        var time = t.TimeOfDay.TimeOfDay;
        string at = time >= new TimeSpan(23, 59, 59) ? "24:00:00" : $"{time.Hours:00}:{time.Minutes:00}:{time.Seconds:00}";
        return $"M{t.Month}.{t.Week}.{(int)t.DayOfWeek}/{at}";
    }

    // ------------------------------------------------------------------
    // Leer la zona que trae el equipo
    // ------------------------------------------------------------------

    /// <summary>Una zona POSIX leída: desfase base (con el signo de UTC) y su horario de verano.</summary>
    /// <param name="UtcOffset">Desfase base respecto de UTC (UTC−4 = −4 h).</param>
    /// <param name="DstDelta">Cuánto adelanta en verano; null = sin horario de verano.</param>
    /// <param name="DstStart">Regla de inicio ("M9.1.6/24:00:00"), si la trae.</param>
    /// <param name="DstEnd">Regla de término, si la trae.</param>
    public sealed record PosixZone(TimeSpan UtcOffset, TimeSpan? DstDelta, string? DstStart, string? DstEnd);

    [GeneratedRegex(@"^\s*[A-Za-z]{3,}(?<sign>[+-]?)(?<h>\d{1,2})(?::(?<m>\d{2}))?(?::\d{2})?" +
                    @"(?:[A-Za-z]{3,}(?:(?<dh>\d{1,2})(?::(?<dm>\d{2}))?(?::\d{2})?)?" +
                    @"(?:,(?<start>[^,]+),(?<end>[^,]+))?)?\s*$")]
    private static partial Regex PosixPattern();

    /// <summary>Lee una zona POSIX de Hikvision; null si no tiene esa forma.</summary>
    public static PosixZone? ParsePosix(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = PosixPattern().Match(text);
        if (!m.Success) return null;
        var offset = new TimeSpan(int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture),
            m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0, 0);
        // POSIX invierte el signo: "+4" es UTC−4; sin signo también es "+".
        if (m.Groups["sign"].Value != "-") offset = -offset;

        bool hasDst = text.IndexOf("DST", StringComparison.OrdinalIgnoreCase) > 0;
        TimeSpan? delta = null;
        if (hasDst)
            delta = m.Groups["dh"].Success
                ? new TimeSpan(int.Parse(m.Groups["dh"].Value, CultureInfo.InvariantCulture),
                    m.Groups["dm"].Success ? int.Parse(m.Groups["dm"].Value, CultureInfo.InvariantCulture) : 0, 0)
                : TimeSpan.FromHours(1);
        return new PosixZone(offset, delta,
            m.Groups["start"].Success ? m.Groups["start"].Value.Trim() : null,
            m.Groups["end"].Success ? m.Groups["end"].Value.Trim() : null);
    }

    // ------------------------------------------------------------------
    // En palabras
    // ------------------------------------------------------------------

    /// <summary>"UTC−4", "UTC+5:30", "UTC".</summary>
    public static string OffsetLabel(TimeSpan offset)
    {
        if (offset == TimeSpan.Zero) return "UTC";
        string sign = offset < TimeSpan.Zero ? "−" : "+";
        var abs = offset.Duration();
        return abs.Minutes == 0 ? $"UTC{sign}{abs.Hours}" : $"UTC{sign}{abs.Hours}:{abs.Minutes:00}";
    }

    private static readonly string[] Months =
        ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];
    private static readonly string[] Days = ["domingo", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado"];
    private static readonly string[] Ordinals = ["", "primer", "segundo", "tercer", "cuarto", "último"];

    [GeneratedRegex(@"^M(?<month>\d{1,2})\.(?<week>\d)\.(?<day>\d)(?:/(?<time>[\d:]+))?$")]
    private static partial Regex RulePattern();

    /// <summary>"M9.1.6/24:00:00" → "el primer sábado de septiembre a medianoche".</summary>
    public static string DescribeRule(string rule)
    {
        var m = RulePattern().Match(rule.Trim());
        if (!m.Success) return rule;
        int month = int.Parse(m.Groups["month"].Value, CultureInfo.InvariantCulture);
        int week = int.Parse(m.Groups["week"].Value, CultureInfo.InvariantCulture);
        int day = int.Parse(m.Groups["day"].Value, CultureInfo.InvariantCulture);
        if (month is < 1 or > 12 || week is < 1 or > 5 || day is < 0 or > 6) return rule;
        string time = m.Groups["time"].Success ? m.Groups["time"].Value : "02:00:00";
        string at = time is "24:00:00" or "23:59:59" or "0:00:00" or "00:00:00" ? "a medianoche" : $"a las {time[..5]}";
        return $"el {Ordinals[week]} {Days[day]} de {Months[month - 1]} {at}";
    }

    /// <summary>La zona de un equipo en una línea.</summary>
    public static string Describe(PosixZone zone)
    {
        string text = OffsetLabel(zone.UtcOffset);
        if (zone.DstDelta is not { } delta) return $"{text} sin horario de verano";
        string amount = delta.TotalMinutes % 60 == 0 ? $"{delta.TotalHours:0} h" : $"{delta.TotalMinutes:0} min";
        return zone.DstStart is not null && zone.DstEnd is not null
            ? $"{text} con horario de verano (+{amount} desde {DescribeRule(zone.DstStart)} hasta {DescribeRule(zone.DstEnd)})"
            : $"{text} con horario de verano (+{amount})";
    }

    /// <summary>Una zona de Windows en una línea, con el mismo vocabulario que <see cref="Describe(PosixZone)"/>.</summary>
    public static string Describe(TimeZoneInfo tz)
    {
        var zone = ParsePosix(Posix(tz))!;
        return Describe(zone);
    }

    // ------------------------------------------------------------------
    // ¿Está bien?
    // ------------------------------------------------------------------

    /// <summary>
    /// Desfase del reloj del equipo: positivo si está adelantado. Con el
    /// desfase de su zona se compara la hora ABSOLUTA (un equipo con la hora
    /// bien pero en otra zona no está "desfasado": está en otra zona, y eso lo
    /// dice <see cref="ZoneMismatch"/>); sin él, contra la hora local esperada.
    /// </summary>
    public static TimeSpan Drift(DeviceClock clock, TimeZoneInfo expected)
    {
        if (clock.UtcOffset is { } offset)
            return clock.LocalTime - offset - clock.ReadAtUtc;
        return clock.LocalTime - TimeZoneInfo.ConvertTimeFromUtc(clock.ReadAtUtc, expected);
    }

    /// <summary>Por qué la zona del equipo no es la esperada.</summary>
    /// <param name="Reason">En palabras, para la lista y la bitácora.</param>
    /// <param name="OnlyDaylight">
    /// El desfase base está bien y lo único distinto es el horario de verano.
    /// Hay firmware que no acepta reglas de verano: a esos se los deja en el
    /// desfase base y no tiene sentido insistir cada vez.
    /// </param>
    public sealed record ZoneProblem(string Reason, bool OnlyDaylight);

    /// <summary>
    /// Qué tiene de distinto la zona del equipo respecto de la esperada; null
    /// si coincide (o si el equipo no informa lo suficiente para saberlo).
    /// </summary>
    public static ZoneProblem? ZoneMismatch(DeviceClock clock, TimeZoneInfo expected)
    {
        var want = ParsePosix(Posix(expected))!;
        var have = ParsePosix(clock.TimeZone);
        TimeSpan? haveBase = have?.UtcOffset ?? clock.ZoneOffset;
        bool? haveDst = have is not null ? have.DstDelta is not null : clock.DaylightSaving;

        // Corto: las fechas del horario de verano solo importan cuando son ellas lo distinto.
        string Expected() => $"{OffsetLabel(want.UtcOffset)} {(want.DstDelta is null ? "sin" : "con")} horario de verano";
        if (haveBase is { } b && b != want.UtcOffset)
            return new($"está en {OffsetLabel(b)} y debería estar en {Expected()}", false);
        if (haveBase is null && clock.UtcOffset is { } now &&
            now != expected.GetUtcOffset(clock.ReadAtUtc))
            return new($"está en {OffsetLabel(now)} y debería estar en {Expected()}", false);
        if (haveDst is { } dst && dst != (want.DstDelta is not null))
            return new(dst ? $"tiene horario de verano y debería estar en {Expected()}"
                           : $"no tiene horario de verano y debería ({Describe(want)})", true);
        if (have is { DstStart: not null, DstEnd: not null } && want.DstStart is not null &&
            (!SameRule(have.DstStart, want.DstStart) || !SameRule(have.DstEnd, want.DstEnd!)))
            return new($"su horario de verano cambia en otras fechas ({Describe(have)}); debería ser {Describe(want)}", true);
        return null;
    }

    /// <summary>Misma regla, sin distinguir medianoche escrita como 24:00:00 o 23:59:59.</summary>
    private static bool SameRule(string a, string b)
    {
        static string Normal(string r) => r.Trim().Replace("/23:59:59", "/24:00:00");
        return string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase);
    }
}
