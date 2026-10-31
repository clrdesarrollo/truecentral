using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Hanwha;

/// <summary>
/// Grabaciones de una cámara (tarjeta SD/NAS) o grabador Wisenet por SUNAPI.
/// La búsqueda se pide en UTC y las horas que vuelven con "Z" son UTC; las
/// que vienen sin zona se toman como hora local del equipo. La diferencia la
/// da el propio equipo (system.cgi date). La reproducción lleva el rango en
/// la URL, así que arranca exactamente donde se pide.
/// </summary>
internal static partial class HanwhaRecordings
{
    /// <summary>Lo que hace falta saber del equipo para armar búsquedas y URLs (se refresca cada 30 min).</summary>
    internal sealed record DeviceShape(bool IsRecorder, int ChannelCount, TimeSpan Offset, DateTime FetchedUtc);

    private static readonly ConcurrentDictionary<string, DeviceShape> Shapes = new();

    public static async Task<IReadOnlyList<RecordingSegment>> QuerySegmentsAsync(DeviceConnectionInfo info, int channelNumber,
        DateTime localStart, DateTime localEnd, CancellationToken ct)
    {
        using var client = new SunapiClient(info, TimeSpan.FromSeconds(20));
        var shape = await ShapeAsync(client, info, ct);
        int ch = Math.Max(0, channelNumber - 1);
        var values = await SearchAsync(client, "msubmenu=timeline&action=view" +
            $"&ChannelIDList={ch}&FromDate={Utc(localStart, shape)}&ToDate={Utc(localEnd, shape)}&Type=All", ct);
        return ParseTimeline(values, ch, shape.Offset)
            .Select(s => new RecordingSegment(s.Start < localStart ? localStart : s.Start, s.End > localEnd ? localEnd : s.End, s.Kind))
            .Where(s => s.End > s.Start)
            .OrderBy(s => s.Start)
            .ToList();
    }

    public static async Task<IReadOnlyList<int>> QueryDaysAsync(DeviceConnectionInfo info, int channelNumber, int year, int month,
        CancellationToken ct)
    {
        using var client = new SunapiClient(info, TimeSpan.FromSeconds(20));
        int ch = Math.Max(0, channelNumber - 1);
        var values = await SearchAsync(client,
            $"msubmenu=calendarsearch&action=view&Month={year:0000}-{month:00}&ChannelIDList={ch}", ct);
        return ParseCalendar(values, ch);
    }

    public static async Task<string> BuildPlaybackUrlAsync(DeviceConnectionInfo info, int rtspPort, int rtspChannel,
        DateTime localStart, DateTime localEnd, CancellationToken ct)
    {
        using var client = new SunapiClient(info);
        var shape = await ShapeAsync(client, info, ct);
        return PlaybackUrl(info, rtspPort, Math.Max(0, rtspChannel - 1), shape, localStart, localEnd);
    }

    /// <summary>
    /// Cámara: rtsp://host/[canal/]recording/AAAAMMDDhhmmss-AAAAMMDDhhmmss/play.smp
    /// (UTC; el canal solo en las multisensor). Grabador:
    /// rtsp://host:558/PlaybackChannel/canal/media.smp/start=...Z&amp;end=...Z.
    /// </summary>
    internal static string PlaybackUrl(DeviceConnectionInfo info, int rtspPort, int ch, DeviceShape shape,
        DateTime localStart, DateTime localEnd)
    {
        string auth = $"{Uri.EscapeDataString(info.Username)}:{Uri.EscapeDataString(info.Password)}@{info.Host}:{rtspPort}";
        DateTime from = localStart - shape.Offset, to = localEnd - shape.Offset;
        if (shape.IsRecorder)
            return $"rtsp://{auth}/PlaybackChannel/{ch}/media.smp/" +
                   $"start={from.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}" +
                   $"&end={to.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";
        string range = $"{from.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}-{to.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
        return shape.ChannelCount > 1
            ? $"rtsp://{auth}/{ch}/recording/{range}/play.smp"
            : $"rtsp://{auth}/recording/{range}/play.smp";
    }

    private static async Task<Dictionary<string, string>> SearchAsync(SunapiClient client, string query, CancellationToken ct)
    {
        try
        {
            return await client.GetAsync("recording.cgi", query, ct);
        }
        catch (SunapiErrorException ex) when (ex.Code is "608" or "604")
        {
            throw new DriverException("El equipo Hanwha no tiene grabación propia (tarjeta SD, NAS o disco) o no permite buscarla.");
        }
    }

    private static string Utc(DateTime local, DeviceShape shape) =>
        (local - shape.Offset).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// "Channel.0.Result.3.StartTime / EndTime / Type" (texto) o el mismo
    /// arreglo en JSON ("Channel.0.Results.3...").
    /// </summary>
    internal static List<RecordingSegment> ParseTimeline(Dictionary<string, string> values, int ch, TimeSpan offset)
    {
        var rows = new Dictionary<int, (string? Start, string? End, string? Type)>();
        foreach (var (key, value) in values)
        {
            var m = TimelineKey().Match(key);
            if (!m.Success || int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) != ch) continue;
            int row = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var current = rows.GetValueOrDefault(row);
            rows[row] = m.Groups[3].Value.ToLowerInvariant() switch
            {
                "starttime" => current with { Start = value },
                "endtime" => current with { End = value },
                _ => current with { Type = value },
            };
        }

        var result = new List<RecordingSegment>();
        foreach (var (start, end, type) in rows.Values)
            if (ToLocal(start, offset) is { } s && ToLocal(end, offset) is { } e && e > s)
                result.Add(new RecordingSegment(s, e, KindOf(type)));
        return result;
    }

    [GeneratedRegex(@"^Channel\.(\d+)\.Results?\.(\d+)\.(StartTime|EndTime|Type)$", RegexOptions.IgnoreCase)]
    private static partial Regex TimelineKey();

    /// <summary>"Channel.0.Result=0011100…": un carácter por día del mes, '1' = hay grabación.</summary>
    internal static IReadOnlyList<int> ParseCalendar(Dictionary<string, string> values, int ch)
    {
        string? mask = values.GetValueOrDefault($"Channel.{ch}.Result") ?? values.GetValueOrDefault($"Channel.{ch}.Results");
        if (string.IsNullOrEmpty(mask)) return [];
        var days = new List<int>();
        for (int i = 0; i < mask.Length && i < 31; i++)
            if (mask[i] == '1') days.Add(i + 1);
        return days;
    }

    private static RecordingKind KindOf(string? type)
    {
        string t = type ?? "";
        if (t.Length == 0 || t.Equals("Normal", StringComparison.OrdinalIgnoreCase) || t.Contains("Continuous", StringComparison.OrdinalIgnoreCase))
            return RecordingKind.Continuous;
        if (t.Contains("Motion", StringComparison.OrdinalIgnoreCase) || t.Contains("VideoAnalytics", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("ObjectDetection", StringComparison.OrdinalIgnoreCase))
            return RecordingKind.Motion;
        if (t.Contains("Manual", StringComparison.OrdinalIgnoreCase)) return RecordingKind.Manual;
        if (t.Contains("Alarm", StringComparison.OrdinalIgnoreCase) || t.Contains("Event", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Tampering", StringComparison.OrdinalIgnoreCase) || t.Contains("Audio", StringComparison.OrdinalIgnoreCase))
            return RecordingKind.Alarm;
        return RecordingKind.Other;
    }

    /// <summary>"2024-01-01T12:00:00Z" → UTC + desfase; sin zona, ya es hora local del equipo.</summary>
    internal static DateTime? ToLocal(string? text, TimeSpan offset)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 19) return null;
        if (!DateTime.TryParseExact(text[..19], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
            return null;
        return text.EndsWith('Z') ? wall + offset : wall;
    }

    /// <summary>Tipo de equipo, canales y desfase horario, consultados una vez cada 30 min.</summary>
    internal static async Task<DeviceShape> ShapeAsync(SunapiClient client, DeviceConnectionInfo info, CancellationToken ct)
    {
        string key = $"{info.Host}:{info.Port}";
        if (Shapes.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.FetchedUtc < TimeSpan.FromMinutes(30))
            return cached;

        var device = await client.GetAsync("system.cgi", "msubmenu=deviceinfo&action=view", ct);
        string type = device.GetValueOrDefault("DeviceType") ?? "";
        bool recorder = type.Contains("NVR", StringComparison.OrdinalIgnoreCase) || type.Contains("DVR", StringComparison.OrdinalIgnoreCase);

        int channels = 1;
        try
        {
            var profiles = HanwhaDeviceDriver.ParseProfiles(await client.GetAsync("media.cgi", "msubmenu=videoprofile&action=view", ct));
            channels = Math.Max(1, profiles.Select(p => p.Channel).Distinct().Count());
        }
        catch (SunapiErrorException) { }

        TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        try
        {
            var date = await client.GetAsync("system.cgi", "msubmenu=date&action=view", ct);
            if (ToLocal(date.GetValueOrDefault("LocalTime"), TimeSpan.Zero) is { } local &&
                ToLocal(date.GetValueOrDefault("UTCTime"), TimeSpan.Zero) is { } utc)
                offset = TimeSpan.FromMinutes(Math.Round((local - utc).TotalMinutes / 15) * 15);
        }
        catch (SunapiErrorException) { }

        var shape = new DeviceShape(recorder, channels, offset, DateTime.UtcNow);
        Shapes[key] = shape;
        return shape;
    }
}
