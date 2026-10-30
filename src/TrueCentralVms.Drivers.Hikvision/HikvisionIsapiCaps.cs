using System.Text.Json.Nodes;
using System.Xml.Linq;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Rutas de capacidades ISAPI (<c>…/capabilities</c>) en JSON o, si el firmware
/// no las tiene en JSON, en XML.
///
/// El DS-K1T805MBFWX V1.9.0 contesta 404 a <c>AccessControl/capabilities?format=json</c>
/// y a <c>RemoteControl/door/capabilities?format=json</c>, pero 200 con XML a las
/// mismas rutas sin el parámetro (medido en el corte del 2026-10-30). Las
/// operaciones (<c>UserInfo</c>…) sí las contesta en JSON. Pedirlas solo en
/// JSON lo daba por "no es un equipo de control de acceso".
///
/// El XML se convierte a la misma forma que el JSON de Hikvision —los
/// atributos como <c>"@min"</c>/<c>"@max"</c>/<c>"@opt"</c>, el texto como valor
/// y los elementos repetidos como lista—, así que quien lee la ficha no
/// distingue de dónde vino.
/// </summary>
internal static class HikvisionIsapiCaps
{
    /// <summary>
    /// GET de una ruta de capacidades (sin <c>?format=json</c>): primero en JSON
    /// y, si el equipo no la tiene así (404 o error), en XML convertido a JSON.
    /// null = el equipo no la tiene de ninguna forma. Si las dos fallan con
    /// error, se lanza el de la consulta en JSON (la de siempre).
    /// </summary>
    public static async Task<string?> GetAsync(HikvisionIsapiClient client, string path, CancellationToken ct)
    {
        DriverException? failure = null;
        try
        {
            if (await client.RequestAsync(HttpMethod.Get, path + "?format=json", ct: ct) is { } json &&
                Normalize(json) is { } answer)
                return answer;
        }
        catch (DriverException ex) { failure = ex; }

        string? xml;
        try { xml = await client.RequestAsync(HttpMethod.Get, path, ct: ct); }
        catch (DriverException) when (failure is not null) { throw failure; }
        if (xml is not null && Normalize(xml) is { } converted) return converted;
        if (failure is not null) throw failure;
        return null;
    }

    /// <summary>Lo mismo, sin errores: null si la ruta no está o no se puede leer.</summary>
    public static async Task<string?> TryGetAsync(HikvisionIsapiClient client, string path, CancellationToken ct)
    {
        try { return await GetAsync(client, path, ct); }
        catch (DriverException) { return null; }
    }

    /// <summary>JSON tal cual; XML convertido; vacío o un rechazo, como contestó (o null si no hay ficha).</summary>
    private static string? Normalize(string body)
    {
        string text = body.TrimStart();
        return text.StartsWith('<') ? ToJson(text) : body;
    }

    /// <summary>
    /// XML de capacidades en la forma del JSON de Hikvision. null si no se
    /// entiende o si es un <c>ResponseStatus</c> (un "no lo tengo", no una ficha).
    /// </summary>
    internal static string? ToJson(string xml)
    {
        XElement? root;
        try { root = XDocument.Parse(xml).Root; }
        catch (System.Xml.XmlException) { return null; }
        if (root is null || root.Name.LocalName == "ResponseStatus") return null;
        return new JsonObject { [root.Name.LocalName] = Node(root) }.ToJsonString();
    }

    private static JsonNode? Node(XElement element)
    {
        var attributes = element.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList();
        var children = element.Elements().ToList();
        if (attributes.Count == 0 && children.Count == 0) return JsonValue.Create(element.Value.Trim());

        var node = new JsonObject();
        foreach (var attribute in attributes) node["@" + attribute.Name.LocalName] = attribute.Value;
        foreach (var group in children.GroupBy(c => c.Name.LocalName))
        {
            var items = group.ToList();
            node[group.Key] = items.Count == 1 ? Node(items[0]) : new JsonArray(items.Select(Node).ToArray());
        }
        if (children.Count == 0 && element.Value.Trim() is { Length: > 0 } text) node["#text"] = text;
        return node;
    }
}
