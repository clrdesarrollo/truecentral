using System.Globalization;
using TrueCentralVms.Core.Drivers;
using TransitionTime = System.TimeZoneInfo.TransitionTime;

namespace TrueCentralVms.Drivers.Dahua;

/// <summary>
/// Zona horaria y horario de verano en el formato de la API HTTP de Dahua
/// (cámaras, grabadores y control de acceso hablan lo mismo):
/// <c>NTP.TimeZone</c> es un ÍNDICE de una tabla fija de desfases y el horario
/// de verano va en <c>Locales.DSTEnable</c> / <c>Locales.DSTStart.*</c> /
/// <c>Locales.DSTEnd.*</c>.
/// </summary>
public static class DahuaTime
{
    /// <summary>La tabla de NTP.TimeZone: GMT+00:00 = 0 … GMT-12:00 = 32.</summary>
    private static readonly string[] Table =
    [
        "+00:00", "+01:00", "+02:00", "+03:00", "+03:30", "+04:00", "+04:30", "+05:00", "+05:30", "+05:45",
        "+06:00", "+06:30", "+07:00", "+08:00", "+09:00", "+09:30", "+10:00", "+11:00", "+12:00", "+13:00",
        "-01:00", "-02:00", "-03:00", "-03:30", "-04:00", "-05:00", "-06:00", "-07:00", "-08:00", "-09:00",
        "-10:00", "-11:00", "-12:00",
    ];

    /// <summary>Índice de NTP.TimeZone para ese desfase, o null si la tabla no lo tiene.</summary>
    public static int? IndexOf(TimeSpan offset)
    {
        string key = $"{(offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";
        int i = Array.IndexOf(Table, key);
        return i < 0 ? null : i;
    }

    /// <summary>Desfase de un índice de NTP.TimeZone, o null si no está en la tabla.</summary>
    public static TimeSpan? OffsetOf(int index)
    {
        if (index < 0 || index >= Table.Length) return null;
        string text = Table[index];
        var value = TimeSpan.ParseExact(text[1..], @"hh\:mm", CultureInfo.InvariantCulture);
        return text[0] == '-' ? -value : value;
    }

    /// <summary>
    /// Ajustes de <c>configManager.cgi?action=setConfig</c> para dejar la zona
    /// y su horario de verano (sin la hora: esa va aparte, con
    /// <c>global.cgi?action=setCurrentTime</c>).
    /// </summary>
    public static List<string> ZoneSettings(TimeZoneInfo tz)
    {
        var settings = new List<string>();
        if (IndexOf(tz.BaseUtcOffset) is int index) settings.Add($"NTP.TimeZone={index}");
        var rule = DeviceTimeZones.CurrentRule(tz);
        if (rule is not null && !rule.DaylightTransitionStart.IsFixedDateRule && !rule.DaylightTransitionEnd.IsFixedDateRule)
        {
            settings.Add("Locales.DSTEnable=true");
            void Add(string prefix, TransitionTime t)
            {
                var time = t.TimeOfDay.TimeOfDay;
                bool midnight = time >= new TimeSpan(23, 59, 59);
                settings.Add($"Locales.{prefix}.Month={t.Month}");
                // Semana: 1..4 y -1 = la última del mes (en .NET es la 5).
                settings.Add($"Locales.{prefix}.Week={(t.Week == 5 ? -1 : t.Week)}");
                settings.Add($"Locales.{prefix}.Day={(int)t.DayOfWeek}");
                settings.Add($"Locales.{prefix}.Hour={(midnight ? 24 : time.Hours)}");
                settings.Add($"Locales.{prefix}.Minute={(midnight ? 0 : time.Minutes)}");
            }
            Add("DSTStart", rule.DaylightTransitionStart);
            Add("DSTEnd", rule.DaylightTransitionEnd);
        }
        else
        {
            settings.Add("Locales.DSTEnable=false");
        }
        return settings;
    }

    /// <summary>"yyyy-MM-dd HH:mm:ss" ya escapado para la URL de setCurrentTime.</summary>
    public static string TimeArgument(DateTime local) =>
        Uri.EscapeDataString(local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
}
