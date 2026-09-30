using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Xml.Linq;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Hikvision.Interop;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Dónde está dibujada una regla de analítica (la línea del cruce de línea,
/// la región de intrusión...) para marcarla sobre la foto del evento.
///
/// Las cámaras "smart" (bySmart = 1) mandan la alarma de regla SIN la
/// geometría ni el recuadro del objeto, y un DVR/NVR la reenvía igual
/// (verificado con DS-2CD4A25FWD-IZ directa y vía DVR, 2026-09-30). Por eso
/// se lee la configuración de la regla por ISAPI: primero a través de la
/// sesión del SDK (sirve para las cámaras IP de un grabador, por su canal),
/// y si el equipo no lo admite, por HTTP en el puerto 80 (firmwares viejos
/// de cámara). Coordenadas ISAPI: 0..1000 con origen ABAJO a la izquierda.
/// La configuración cambia poco: se guarda 10 minutos.
/// </summary>
internal static class HikvisionRuleGeometry
{
    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, (DateTime At, XDocument? Xml)> Cache = new();

    public static async Task<VideoEventOverlay?> GetAsync(DeviceConnectionInfo info, int rtspChannel, VideoEventKind kind,
        int? ruleId, CancellationToken ct)
    {
        var (_, closed, word) = Resource(kind);
        return await ConfigAsync(info, rtspChannel, kind, ct) is { } xml ? Parse(xml, closed, ruleId, word) : null;
    }

    /// <summary>
    /// Todas las líneas/regiones que admite la regla en el canal: la lista del
    /// equipo trae un ítem por cada una (dibujada o no) y su atributo
    /// <c>size</c> es la capacidad (4 en las cámaras y DVR probados).
    /// </summary>
    public static async Task<IReadOnlyList<AnalyticsRuleInfo>?> ListAsync(DeviceConnectionInfo info, int rtspChannel,
        VideoEventKind kind, CancellationToken ct)
    {
        if (await ConfigAsync(info, rtspChannel, kind, ct) is not { } xml) return null;
        var lists = xml.Root!.Descendants()
            .Where(e => e.Name.LocalName.EndsWith("List", StringComparison.Ordinal) &&
                        e.Name.LocalName is not ("CoordinatesList" or "RegionCoordinatesList"))
            .Select(list => (List: list, Items: list.Elements()
                .Where(i => i.Elements().Any(c => c.Name.LocalName == "id"))
                .Select(i => (
                    Id: (int?)Number(i.Elements().FirstOrDefault(c => c.Name.LocalName == "id")),
                    Enabled: string.Equals(i.Elements().FirstOrDefault(c => c.Name.LocalName == "enabled")?.Value.Trim(), "true",
                        StringComparison.OrdinalIgnoreCase),
                    // Una línea sin dibujar viene con todas sus coordenadas en 0.
                    Configured: Points(i, 1000, 1000).Distinct().Count() >= 2))
                .Where(i => i.Id is > 0)
                .ToList()))
            .Where(l => l.Items.Count > 0)
            .ToList();
        if (lists.Count == 0) return null;

        var (list, items) = lists[0];
        int size = int.TryParse(list.Attribute("size")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
        int capacity = Math.Max(size, items.Max(i => i.Id!.Value));
        return Enumerable.Range(1, Math.Min(capacity, 32))
            .Select(id => items.FirstOrDefault(i => i.Id == id) is { Id: not null } item
                ? new AnalyticsRuleInfo(id, item.Configured, item.Enabled)
                : new AnalyticsRuleInfo(id, false, false))
            .ToList();
    }

    private static (string? Resource, bool Closed, string Word) Resource(VideoEventKind kind) => kind switch
    {
        VideoEventKind.LineCrossing => ("LineDetection", false, "Línea"),
        VideoEventKind.Intrusion => ("FieldDetection", true, "Región"),
        VideoEventKind.RegionEntrance => ("regionEntrance", true, "Región"),
        VideoEventKind.RegionExit => ("regionExiting", true, "Región"),
        _ => (null, false, ""),
    };

    private static async Task<XDocument?> ConfigAsync(DeviceConnectionInfo info, int rtspChannel, VideoEventKind kind,
        CancellationToken ct)
    {
        if (rtspChannel <= 0 || Resource(kind).Resource is not { } resource) return null;

        string path = $"/ISAPI/Smart/{resource}/{rtspChannel}";
        string key = $"{info.Host}|{info.Port}|{path}";
        if (!Cache.TryGetValue(key, out var cached) || DateTime.UtcNow - cached.At > CacheTime)
        {
            var loaded = await LoadAsync(info, path, ct);
            // Un fallo (equipo caído, sin soporte) se reintenta al minuto, no a los 10.
            cached = (loaded is null ? DateTime.UtcNow - CacheTime + TimeSpan.FromMinutes(1) : DateTime.UtcNow, loaded);
            Cache[key] = cached;
        }
        return cached.Xml;
    }

    private static async Task<XDocument?> LoadAsync(DeviceConnectionInfo info, string path, CancellationToken ct)
    {
        string? text = await Task.Run(() =>
        {
            try
            {
                int userId = HikvisionSessionCache.GetOrLogin(info);
                return IsapiPassthrough.Get(userId, path);
            }
            catch { return null; }
        }, ct);

        if (text is null)
        {
            // Firmwares de cámara que no pasan ISAPI por el SDK (error 23): HTTP directo.
            try
            {
                using var handler = new SocketsHttpHandler { Credentials = new NetworkCredential(info.Username, info.Password) };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                using var response = await http.GetAsync($"http://{info.Host}{path}", ct);
                if (response.IsSuccessStatusCode) text = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { }
        }

        try { return text is { Length: > 0 } ? XDocument.Parse(text) : null; }
        catch { return null; }
    }

    /// <summary>
    /// Toma el ítem (LineItem / FieldDetectionRegion...) con el número de la
    /// regla; sin número, el único ítem con coordenadas. No se mira
    /// <c>enabled</c>: algunas cámaras lo informan false en la línea activa.
    /// </summary>
    private static VideoEventOverlay? Parse(XDocument xml, bool closed, int? ruleId, string word)
    {
        var root = xml.Root!;
        float width = Number(root.Descendants().FirstOrDefault(e => e.Name.LocalName == "normalizedScreenWidth")) ?? 1000;
        float height = Number(root.Descendants().FirstOrDefault(e => e.Name.LocalName == "normalizedScreenHeight")) ?? 1000;
        if (width <= 0 || height <= 0) return null;

        var items = root.Descendants()
            .Where(e => e.Elements().Any(c => c.Name.LocalName is "CoordinatesList" or "RegionCoordinatesList"))
            .Select(e => (
                Id: (int?)Number(e.Elements().FirstOrDefault(c => c.Name.LocalName == "id")),
                Points: Points(e, width, height)))
            .Where(i => i.Points.Distinct().Count() >= 2)
            .ToList();

        var item = ruleId is { } id ? items.FirstOrDefault(i => i.Id == id) : default;
        if (item.Points is null && items.Count == 1) item = items[0];
        if (item.Points is null) return null;

        return new VideoEventOverlay(item.Points, closed && item.Points.Count >= 3, null,
            item.Id is { } n ? $"{word} {n}" : word);
    }

    /// <summary>Puntos de un ítem (línea o región), normalizados a 0..1 con origen arriba a la izquierda.</summary>
    private static List<OverlayPoint> Points(XElement item, float width, float height) =>
        item.Descendants()
            .Where(c => c.Name.LocalName is "Coordinates" or "RegionCoordinates")
            .Select(c => (X: Number(c.Elements().FirstOrDefault(p => p.Name.LocalName == "positionX")),
                          Y: Number(c.Elements().FirstOrDefault(p => p.Name.LocalName == "positionY"))))
            .Where(p => p.X is not null && p.Y is not null)
            .Select(p => new OverlayPoint(Math.Clamp(p.X!.Value / width, 0, 1), Math.Clamp(1 - p.Y!.Value / height, 0, 1)))
            .ToList();

    private static float? Number(XElement? element) =>
        element is not null && float.TryParse(element.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : null;
}
