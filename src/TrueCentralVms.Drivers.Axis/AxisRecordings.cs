using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Axis;

/// <summary>
/// Grabaciones en el almacenamiento propio de la cámara (Edge Storage).
/// list.cgi responde en UTC; el VMS trabaja en la hora local del equipo, así
/// que se usa la diferencia que informa la propia cámara (time.cgi). Cada
/// grabación de Axis es un archivo lógico entero (la continua dura hasta que
/// se reinicia o se llena el disco, una por evento en las disparadas): se
/// reproduce por su recordingid y el instante pedido viaja como desplazamiento
/// dentro de ella (<see cref="PlaybackSource"/>).
/// </summary>
internal static class AxisRecordings
{
    internal sealed record Recording(string Id, DateTime Start, DateTime End, RecordingKind Kind, int? Source);

    /// <summary>Diferencia hora local − UTC por equipo (cambia rara vez: horario de verano).</summary>
    private static readonly ConcurrentDictionary<string, (TimeSpan Offset, DateTime FetchedUtc)> Offsets = new();

    public static async Task<IReadOnlyList<RecordingSegment>> QuerySegmentsAsync(DeviceConnectionInfo info, int channel,
        DateTime localStart, DateTime localEnd, CancellationToken ct)
    {
        using var client = new AxisVapixClient(info, TimeSpan.FromSeconds(20));
        var recordings = await ListAsync(client, info, channel, localStart, localEnd, ct);
        return recordings
            .Select(r => new RecordingSegment(r.Start < localStart ? localStart : r.Start, r.End > localEnd ? localEnd : r.End, r.Kind))
            .Where(s => s.End > s.Start)
            .OrderBy(s => s.Start)
            .ToList();
    }

    public static async Task<IReadOnlyList<int>> QueryDaysAsync(DeviceConnectionInfo info, int channel, int year, int month,
        CancellationToken ct)
    {
        var first = new DateTime(year, month, 1);
        var last = first.AddMonths(1);
        using var client = new AxisVapixClient(info, TimeSpan.FromSeconds(20));
        var recordings = await ListAsync(client, info, channel, first, last, ct);
        var days = new SortedSet<int>();
        foreach (var r in recordings)
            for (var day = (r.Start < first ? first : r.Start).Date; day < last && day < r.End; day = day.AddDays(1))
                days.Add(day.Day);
        return days.ToList();
    }

    public static async Task<string?> ResolvePlaybackUrlAsync(DeviceConnectionInfo info, int rtspPort, int channel,
        DateTime localStart, CancellationToken ct)
    {
        using var client = new AxisVapixClient(info, TimeSpan.FromSeconds(20));
        // Una grabación continua puede haber empezado días antes: se busca hacia atrás con margen.
        var recordings = await ListAsync(client, info, channel, localStart.AddDays(-7), localStart.AddDays(1), ct);
        if (Pick(recordings, localStart) is not { } pick)
            throw new DriverException("La cámara Axis no tiene grabaciones desde el instante pedido.");

        string url = $"rtsp://{Uri.EscapeDataString(info.Username)}:{Uri.EscapeDataString(info.Password)}@{info.Host}:{rtspPort}" +
                     $"/axis-media/media.amp?recordingid={Uri.EscapeDataString(pick.Recording.Id)}";
        return PlaybackSource.WithStartOffset(url, pick.Offset);
    }

    /// <summary>
    /// La grabación que cubre el instante (la continua antes que la de un
    /// evento: sigue de largo) y el desplazamiento dentro de ella; si cae en
    /// un hueco, la siguiente desde su comienzo.
    /// </summary>
    internal static (Recording Recording, TimeSpan Offset)? Pick(IReadOnlyList<Recording> recordings, DateTime localStart)
    {
        var covering = recordings
            .Where(r => r.Start <= localStart && localStart < r.End)
            .OrderBy(r => r.Kind == RecordingKind.Continuous ? 0 : 1)
            .ThenByDescending(r => r.End)
            .FirstOrDefault();
        if (covering is not null) return (covering, localStart - covering.Start);
        var next = recordings.Where(r => r.Start > localStart).MinBy(r => r.Start);
        return next is null ? null : (next, TimeSpan.Zero);
    }

    private static async Task<List<Recording>> ListAsync(AxisVapixClient client, DeviceConnectionInfo info, int channel,
        DateTime localStart, DateTime localEnd, CancellationToken ct)
    {
        var offset = await OffsetAsync(client, info, ct);
        string from = (localStart - offset).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        string to = (localEnd - offset).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        string xml;
        try
        {
            xml = await client.GetStringAsync(
                $"axis-cgi/record/list.cgi?recordingid=all&starttime={from}&stoptime={to}&maxnumberofresults=5000", ct);
        }
        catch (AxisNotFoundException)
        {
            throw new DriverException("La cámara Axis no tiene grabación en el equipo (Edge Storage) o el firmware no la ofrece.");
        }
        return Parse(xml, offset, DateTime.UtcNow + offset)
            .Where(r => r.Source is null || r.Source == channel)
            .ToList();
    }

    /// <summary>
    /// &lt;recording recordingid="..." starttime="...Z" stoptime="...Z"
    /// recordingtype="continuous|triggered" eventtrigger="..." source="1"/&gt;.
    /// Una grabación en curso no trae stoptime: llega hasta ahora.
    /// </summary>
    internal static List<Recording> Parse(string xml, TimeSpan offset, DateTime localNow)
    {
        var result = new List<Recording>();
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { throw new DriverException("La cámara Axis devolvió una lista de grabaciones ilegible."); }

        foreach (var e in doc.Descendants().Where(e => e.Name.LocalName == "recording"))
        {
            string? id = (string?)e.Attribute("recordingid");
            if (string.IsNullOrEmpty(id) || !TryUtc((string?)e.Attribute("starttime"), out var startUtc)) continue;
            DateTime end = TryUtc((string?)e.Attribute("stoptime"), out var stopUtc) ? stopUtc + offset : localNow;
            int? source = int.TryParse((string?)e.Attribute("source"), out int s) ? s : null;
            result.Add(new Recording(id, startUtc + offset, end, KindOf(e), source));
        }
        return result;
    }

    private static RecordingKind KindOf(XElement e)
    {
        string type = (string?)e.Attribute("recordingtype") ?? "";
        if (type.Equals("continuous", StringComparison.OrdinalIgnoreCase)) return RecordingKind.Continuous;
        string trigger = $"{(string?)e.Attribute("eventtrigger")} {(string?)e.Attribute("eventid")}";
        if (trigger.Contains("motion", StringComparison.OrdinalIgnoreCase) || trigger.Contains("vmd", StringComparison.OrdinalIgnoreCase) ||
            trigger.Contains("objectanalytics", StringComparison.OrdinalIgnoreCase))
            return RecordingKind.Motion;
        if (trigger.Contains("manual", StringComparison.OrdinalIgnoreCase)) return RecordingKind.Manual;
        return type.Equals("triggered", StringComparison.OrdinalIgnoreCase) ? RecordingKind.Alarm : RecordingKind.Other;
    }

    private static bool TryUtc(string? text, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return false;
        utc = parsed.UtcDateTime;
        return true;
    }

    /// <summary>Hora local − UTC según la cámara (time.cgi, AXIS OS 7.10+); sin él, la del servidor.</summary>
    private static async Task<TimeSpan> OffsetAsync(AxisVapixClient client, DeviceConnectionInfo info, CancellationToken ct)
    {
        string key = $"{info.Host}:{info.Port}";
        if (Offsets.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.FetchedUtc < TimeSpan.FromMinutes(30))
            return cached.Offset;

        TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        try
        {
            string json = await client.PostJsonAsync("axis-cgi/time.cgi",
                """{"apiVersion":"1.0","context":"truecentral","method":"getDateTimeInfo"}""", ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("dateTime", out var utcText) && data.TryGetProperty("localDateTime", out var localText) &&
                DateTimeOffset.TryParse(utcText.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc) &&
                WallClock(localText.GetString()) is { } wall)
                offset = TimeSpan.FromMinutes(Math.Round((wall - utc.UtcDateTime).TotalMinutes / 15) * 15);
        }
        catch (Exception ex) when (ex is DriverException or JsonException) { }
        Offsets[key] = (offset, DateTime.UtcNow);
        return offset;
    }

    /// <summary>
    /// El reloj de pared de "2024-05-01T12:00:00+02:00" (o con una Z que
    /// algunos firmware ponen igual a la hora local): la fecha y hora tal
    /// como se leen, sin aplicar el desfase.
    /// </summary>
    internal static DateTime? WallClock(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 19) return null;
        return DateTime.TryParseExact(text[..19], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall)
            ? wall : null;
    }
}
