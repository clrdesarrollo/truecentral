using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Hanwha;

/// <summary>
/// Líneas y áreas de la analítica de video de un canal Wisenet
/// (eventsources.cgi videoanalysis2), para listarlas en el editor de
/// automatizaciones y dibujarlas sobre la foto del evento. SUNAPI da las
/// coordenadas en píxeles de la resolución máxima del canal: se normalizan
/// con el perfil de video más grande. Se guarda 10 minutos.
/// </summary>
internal static partial class HanwhaAnalytics
{
    /// <summary>Una línea ("Line") o un área definida ("DefinedArea") de la analítica, con sus modos (Intrusion, Entering...).</summary>
    internal sealed record Shape(string Type, int Id, IReadOnlyList<OverlayPoint> Points, IReadOnlyList<string> Modes, bool Enabled);

    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<Shape> Shapes)> Cache = new();

    public static async Task<IReadOnlyList<Shape>> ShapesAsync(DeviceConnectionInfo info, int ch, CancellationToken ct)
    {
        string key = $"{info.Host}:{info.Port}:{ch}";
        if (Cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < CacheTime)
            return cached.Shapes;

        using var client = new SunapiClient(info, TimeSpan.FromSeconds(8));
        IReadOnlyList<Shape> shapes;
        try
        {
            var values = await client.GetAsync("eventsources.cgi", $"msubmenu=videoanalysis2&action=view&Channel={ch}", ct);
            var profiles = HanwhaDeviceDriver.ParseProfiles(await client.GetAsync("media.cgi", "msubmenu=videoprofile&action=view", ct));
            var max = profiles.Where(p => p.Channel == ch && p.Area > 0).MaxBy(p => p.Area);
            shapes = Parse(values, ch, max?.Width ?? 1920, max?.Height ?? 1080);
        }
        catch (SunapiErrorException)
        {
            shapes = [];   // modelo sin analítica de video
        }
        Cache[key] = (DateTime.UtcNow, shapes);
        return shapes;
    }

    /// <summary>
    /// Texto: "Channel.0.Line.1.Coordinate=100,200,500,600", "Channel.0.DefinedArea.2.Mode=Intrusion,Entering".
    /// JSON aplanado: "Channel.0.Line.1.Coordinates.0.x" / ".y".
    /// </summary>
    internal static List<Shape> Parse(Dictionary<string, string> values, int ch, int width, int height)
    {
        var points = new Dictionary<(string, int), SortedDictionary<int, (double? X, double? Y)>>();
        var flat = new Dictionary<(string, int), string>();
        var modes = new Dictionary<(string, int), List<string>>();
        var enabled = new Dictionary<(string, int), bool>();

        foreach (var (key, value) in values)
        {
            var m = ShapeKey().Match(key);
            if (!m.Success || int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) != ch) continue;
            var id = (m.Groups[2].Value, int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
            string rest = m.Groups[4].Value;

            var coord = CoordinateKey().Match(rest);
            if (coord.Success)
            {
                if (!points.TryGetValue(id, out var list)) points[id] = list = new();
                int index = int.Parse(coord.Groups[1].Value, CultureInfo.InvariantCulture);
                var current = list.GetValueOrDefault(index);
                double v = double.Parse(value, CultureInfo.InvariantCulture);
                list[index] = coord.Groups[2].Value.Equals("x", StringComparison.OrdinalIgnoreCase) ? current with { X = v } : current with { Y = v };
            }
            else if (rest.StartsWith("Coordinate", StringComparison.OrdinalIgnoreCase))
                flat[id] = value;
            else if (rest.StartsWith("Mode", StringComparison.OrdinalIgnoreCase))
            {
                if (!modes.TryGetValue(id, out var list)) modes[id] = list = [];
                if (value.Equals("Off", StringComparison.OrdinalIgnoreCase) || value.Equals("False", StringComparison.OrdinalIgnoreCase))
                    enabled[id] = false;
                else if (value.Equals("True", StringComparison.OrdinalIgnoreCase))
                    list.Add(rest[(rest.LastIndexOf('.') + 1)..]);   // JSON: "Mode.Intrusion=True"
                else
                    list.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            else if (rest.Equals("Enable", StringComparison.OrdinalIgnoreCase))
                enabled[id] = value.Equals("True", StringComparison.OrdinalIgnoreCase);
        }

        var result = new List<Shape>();
        foreach (var id in points.Keys.Concat(flat.Keys).Concat(modes.Keys).Distinct())
        {
            var raw = new List<(double X, double Y)>();
            if (points.TryGetValue(id, out var list))
                raw.AddRange(list.Values.Where(p => p.X is not null && p.Y is not null).Select(p => (p.X!.Value, p.Y!.Value)));
            else if (flat.TryGetValue(id, out string? text))
            {
                var numbers = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN).ToList();
                for (int i = 0; i + 1 < numbers.Count; i += 2) raw.Add((numbers[i], numbers[i + 1]));
            }
            var normalized = raw.Where(p => !double.IsNaN(p.X) && !double.IsNaN(p.Y))
                .Select(p => new OverlayPoint((float)Math.Clamp(p.X / width, 0, 1), (float)Math.Clamp(p.Y / height, 0, 1)))
                .ToList();
            result.Add(new Shape(id.Item1, id.Item2, normalized, modes.GetValueOrDefault(id) ?? [], enabled.GetValueOrDefault(id, true)));
        }
        return result.OrderBy(s => s.Type).ThenBy(s => s.Id).ToList();
    }

    [GeneratedRegex(@"^Channel\.(\d+)\.(Line|DefinedArea)\.(\d+)\.(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ShapeKey();

    [GeneratedRegex(@"^Coordinates?\.(\d+)\.(x|y)$", RegexOptions.IgnoreCase)]
    private static partial Regex CoordinateKey();

    /// <summary>Las formas que corresponden a un tipo de evento: líneas para el cruce, áreas (con ese modo) para el resto.</summary>
    internal static IEnumerable<Shape> For(IEnumerable<Shape> shapes, VideoEventKind kind)
    {
        string? mode = kind switch
        {
            VideoEventKind.Intrusion => "Intrusion",
            VideoEventKind.RegionEntrance => "Enter",
            VideoEventKind.RegionExit => "Exit",
            VideoEventKind.Loitering => "Loitering",
            VideoEventKind.ObjectLeftOrTaken => "ppear",   // Appearing / Disappearing
            _ => null,
        };
        if (kind == VideoEventKind.LineCrossing) return shapes.Where(s => s.Type.Equals("Line", StringComparison.OrdinalIgnoreCase));
        if (mode is null) return [];
        return shapes.Where(s => s.Type.Equals("DefinedArea", StringComparison.OrdinalIgnoreCase) &&
                                 (s.Modes.Count == 0 || s.Modes.Any(m => m.Contains(mode, StringComparison.OrdinalIgnoreCase))));
    }

    public static async Task<IReadOnlyList<AnalyticsRuleInfo>?> ListAsync(DeviceConnectionInfo info, int ch, VideoEventKind kind,
        CancellationToken ct)
    {
        var shapes = For(await ShapesAsync(info, ch, ct), kind).ToList();
        if (shapes.Count == 0) return null;
        return shapes.Select(s => new AnalyticsRuleInfo(s.Id, s.Points.Count >= 2, s.Enabled)).ToList();
    }

    public static async Task<VideoEventOverlay?> OverlayAsync(DeviceConnectionInfo info, int ch, VideoEventKind kind, int? ruleId,
        CancellationToken ct)
    {
        var shapes = For(await ShapesAsync(info, ch, ct), kind).Where(s => s.Points.Count >= 2 && s.Enabled).ToList();
        var shape = ruleId is { } id ? shapes.FirstOrDefault(s => s.Id == id) : shapes.Count == 1 ? shapes[0] : null;
        if (shape is null) return null;
        bool line = shape.Type.Equals("Line", StringComparison.OrdinalIgnoreCase);
        return new VideoEventOverlay(shape.Points, Closed: !line, Target: null, Label: line ? $"Línea {shape.Id}" : $"Área {shape.Id}");
    }
}
