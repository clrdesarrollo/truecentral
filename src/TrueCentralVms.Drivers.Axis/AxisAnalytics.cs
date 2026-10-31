using System.Collections.Concurrent;
using System.Text.Json;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Axis;

/// <summary>
/// Reglas de las analíticas de la cámara Axis, leídas de su configuración:
/// los escenarios de AXIS Object Analytics (cerco, conteo por línea, objeto
/// en el área, ocupación) y los perfiles de AXIS Video Motion Detection 4.
/// Sirven para tres cosas: saber de qué tipo es el escenario que dispara un
/// evento (el tema solo dice "Device1Scenario3"), listar las reglas en el
/// editor de automatizaciones y dibujar la línea o el área sobre la foto.
/// Coordenadas de VAPIX: −1..1 con (−1, −1) abajo a la izquierda. Se guarda
/// 10 minutos.
/// </summary>
internal static class AxisAnalytics
{
    /// <summary>Una regla: escenario de AOA (Source "aoa") o perfil de VMD4 (Source "vmd").</summary>
    internal sealed record Rule(string Source, int Id, string Name, int Camera, VideoEventKind Kind,
        IReadOnlyList<OverlayPoint> Points, bool Closed);

    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<Rule> Rules)> Cache = new();

    public static async Task<IReadOnlyList<Rule>> RulesAsync(DeviceConnectionInfo info, CancellationToken ct)
    {
        string key = $"{info.Host}:{info.Port}";
        if (Cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < CacheTime)
            return cached.Rules;

        var rules = new List<Rule>();
        using var client = new AxisVapixClient(info, TimeSpan.FromSeconds(8));
        rules.AddRange(await ReadAsync(client, "local/objectanalytics/control.cgi", ParseObjectAnalytics, ct));
        rules.AddRange(await ReadAsync(client, "local/vmd/control.cgi", ParseVmd, ct));
        Cache[key] = (DateTime.UtcNow, rules);
        return rules;
    }

    /// <summary>Sin la aplicación instalada (404) o detenida, simplemente no hay reglas suyas.</summary>
    private static async Task<IEnumerable<Rule>> ReadAsync(AxisVapixClient client, string path,
        Func<JsonElement, IEnumerable<Rule>> parse, CancellationToken ct)
    {
        try
        {
            string json = await client.PostJsonAsync(path,
                """{"apiVersion":"1.0","context":"truecentral","method":"getConfiguration"}""", ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("data", out var data) ? parse(data).ToList() : [];
        }
        catch (Exception ex) when (ex is DriverException or JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// {"scenarios":[{"id":2,"name":"Portón","type":"fence","devices":[{"id":1}],
    /// "triggers":[{"type":"fence","vertices":[[x,y],...]}]}]}
    /// </summary>
    internal static IEnumerable<Rule> ParseObjectAnalytics(JsonElement data)
    {
        if (!data.TryGetProperty("scenarios", out var scenarios) || scenarios.ValueKind != JsonValueKind.Array) yield break;
        foreach (var s in scenarios.EnumerateArray())
        {
            if (!s.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out int id)) continue;
            string type = s.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            var kind = type.ToLowerInvariant() switch
            {
                "fence" or "tailgating" => VideoEventKind.LineCrossing,
                "crosslinecounting" => VideoEventKind.PeopleCounting,
                "occupancyinarea" => VideoEventKind.Crowd,
                _ => VideoEventKind.Intrusion,   // "motion": objeto en el área
            };
            int camera = s.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array &&
                         devices.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } d &&
                         d.TryGetProperty("id", out var dev) && dev.TryGetInt32(out int c) ? c : 1;
            var points = new List<OverlayPoint>();
            if (s.TryGetProperty("triggers", out var triggers) && triggers.ValueKind == JsonValueKind.Array &&
                triggers.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } trigger)
                points.AddRange(Vertices(trigger, "vertices"));
            string name = s.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } nm ? nm : $"Escenario {id}";
            bool line = kind is VideoEventKind.LineCrossing or VideoEventKind.PeopleCounting;
            yield return new Rule("aoa", id, name, camera, kind, points, Closed: !line);
        }
    }

    /// <summary>{"profiles":[{"id":1,"name":"Profile 1","camera":1,"triggers":[{"type":"includeArea","data":[[x,y],...]}]}]}</summary>
    internal static IEnumerable<Rule> ParseVmd(JsonElement data)
    {
        if (!data.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array) yield break;
        foreach (var p in profiles.EnumerateArray())
        {
            if (!p.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out int id)) continue;
            int camera = p.TryGetProperty("camera", out var cam) && cam.TryGetInt32(out int c) ? c : 1;
            var points = new List<OverlayPoint>();
            if (p.TryGetProperty("triggers", out var triggers) && triggers.ValueKind == JsonValueKind.Array &&
                triggers.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } trigger)
                points.AddRange(Vertices(trigger, "data").Concat(Vertices(trigger, "vertices")));
            string name = p.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } nm ? nm : $"Perfil {id}";
            yield return new Rule("vmd", id, name, camera, VideoEventKind.Motion, points, Closed: true);
        }
    }

    /// <summary>[[x, y], ...] en −1..1 (y hacia arriba) → 0..1 con origen arriba a la izquierda.</summary>
    private static IEnumerable<OverlayPoint> Vertices(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var v in list.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() < 2) continue;
            double x = v[0].GetDouble(), y = v[1].GetDouble();
            yield return new OverlayPoint((float)Math.Clamp((x + 1) / 2, 0, 1), (float)Math.Clamp((1 - y) / 2, 0, 1));
        }
    }

    /// <summary>La regla que disparó un evento, por el tema ("…ObjectAnalytics/Device1Scenario3", "…VMD/Camera1Profile2").</summary>
    public static Rule? ForTopic(IReadOnlyList<Rule> rules, string source, int camera, int id) =>
        rules.FirstOrDefault(r => r.Source == source && r.Id == id && r.Camera == camera) ??
        rules.FirstOrDefault(r => r.Source == source && r.Id == id);

    public static async Task<IReadOnlyList<AnalyticsRuleInfo>?> ListAsync(DeviceConnectionInfo info, int camera, VideoEventKind kind,
        CancellationToken ct)
    {
        var rules = (await RulesAsync(info, ct)).Where(r => r.Kind == kind && r.Camera == camera).ToList();
        if (rules.Count == 0) return null;
        return rules.OrderBy(r => r.Id).Select(r => new AnalyticsRuleInfo(r.Id, r.Points.Count >= 2, true)).ToList();
    }

    public static async Task<VideoEventOverlay?> OverlayAsync(DeviceConnectionInfo info, int camera, VideoEventKind kind, int? ruleId,
        CancellationToken ct)
    {
        var rules = (await RulesAsync(info, ct)).Where(r => r.Camera == camera && r.Kind == kind && r.Points.Count >= 2).ToList();
        var rule = ruleId is { } id ? rules.FirstOrDefault(r => r.Id == id) : rules.Count == 1 ? rules[0] : null;
        return rule is null ? null : new VideoEventOverlay(rule.Points, rule.Closed, Target: null, Label: rule.Name);
    }
}
