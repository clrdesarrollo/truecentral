using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services.Workflows;

/// <summary>
/// Qué recursos toca cada acción de una automatización, para que quien la crea o
/// la edita no use una automatización para operar lo que su alcance no le deja
/// operar a mano (abrir una puerta de otra sede, desarmar un panel ajeno, mandar
/// las fotos de una cámara que no puede ver).
///
/// Se revisa el recurso concreto (REGLA del alcance: no su ubicación) al crear,
/// al editar (solo lo que se AGREGA: quien edita una automatización de un
/// administrador puede conservar lo que ya tenía), al duplicar y al probarla a
/// mano. La automatización ya guardada se ejecuta igual con los permisos del
/// sistema: es un control de quién la arma, no de cuándo corre.
/// </summary>
public static class WorkflowResourceScope
{
    /// <summary>Claves "tipo:id" de los recursos que usa una acción (ej. "door:5", "area:3:2", "snapshot:12").</summary>
    public static IEnumerable<string> References(string type, JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object) yield break;
        switch (type)
        {
            case WorkflowActionTypes.Door:
                foreach (int id in Ints(config, "doorIds")) yield return $"door:{id}";
                break;
            case WorkflowActionTypes.Panel:
                int panel = Int(config, "panelId");
                if (panel <= 0) break;
                int area = Int(config, "areaNumber");
                yield return area > 0 ? $"area:{panel}:{area}" : $"panel:{panel}";
                break;
            case WorkflowActionTypes.PtzPreset:
                if (Int(config, "channelId") is > 0 and var channel) yield return $"ptz:{channel}";
                break;
            case WorkflowActionTypes.Snapshot:
                foreach (int id in Ints(config, "channelIds")) yield return $"snapshot:{id}";
                break;
            case WorkflowActionTypes.Relay:
                if (config.TryGetProperty("relayKeys", out var keys) && keys.ValueKind == JsonValueKind.Array)
                    foreach (var key in keys.EnumerateArray())
                        if (key.ValueKind == JsonValueKind.String && key.GetString()?.Split(':') is [var module, _]
                            && int.TryParse(module, out int moduleId))
                            yield return $"io:{moduleId}";
                break;
            case WorkflowActionTypes.Speaker:
                string mode = config.TryGetProperty("mode", out var m) ? (m.GetString() ?? "").Trim().ToLowerInvariant() : "";
                if (mode is not ("" or "inventory")) break;   // por URL: no es un recurso del inventario
                foreach (int id in Ints(config, "speakerIds")) yield return $"speaker:{id}";
                if (config.TryGetProperty("group", out var g) && g.GetString() is { Length: > 0 } group)
                    yield return $"speaker-group:{group.Trim()}";
                break;
        }
    }

    /// <summary>Todas las claves de un alta o edición (del diagrama si viene, si no de la lista lineal).</summary>
    public static HashSet<string> References(WorkflowWriteDto request)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (request.Graph is not null)
        {
            foreach (var node in request.Graph.Nodes)
                if (node.Type is { } type && node.Config is { } config)
                    result.UnionWith(References(type, config));
        }
        else
        {
            foreach (var action in request.Actions ?? [])
                result.UnionWith(References(action.Type, action.Config));
        }
        return result;
    }

    /// <summary>Claves de las acciones ya guardadas de una automatización.</summary>
    public static HashSet<string> References(IEnumerable<Data.Entities.WorkflowAction> actions)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in actions)
        {
            try
            {
                using var doc = JsonDocument.Parse(action.ConfigJson);
                result.UnionWith(References(action.Type, doc.RootElement));
            }
            catch (JsonException) { /* configuración dañada: no referencia nada */ }
        }
        return result;
    }

    /// <summary>Las claves que <paramref name="scope"/> no puede usar, en palabras del operador. Vacía = todo permitido.</summary>
    public static async Task<List<string>> DeniedAsync(UserScope scope, IEnumerable<string> references, VmsDbContext db,
        CancellationToken ct)
    {
        var denied = new List<string>();
        if (scope.Unrestricted) return denied;
        foreach (string reference in references.Order(StringComparer.Ordinal))
        {
            string[] p = reference.Split(':', 3);
            bool ok = p[0] switch
            {
                "door" => scope.CanOperateDoor(int.Parse(p[1])),
                "area" => scope.CanOperateArea(int.Parse(p[1]), int.Parse(p[2])),
                "panel" => scope.CanOperateAllAreas(int.Parse(p[1])),
                "ptz" => scope.CanOperateChannel(int.Parse(p[1])),
                "snapshot" => scope.CanViewChannel(int.Parse(p[1])),
                "io" => scope.CanOperateIoModule(int.Parse(p[1])),
                "speaker" => scope.CanOperateSpeaker(int.Parse(p[1])),
                // Un grupo vale solo si alcanza a TODOS sus parlantes (hoy y los que se le agreguen se revisan al editar).
                "speaker-group" => (await db.Speakers.AsNoTracking()
                        .Where(s => s.GroupName != null && s.GroupName.ToLower() == p[1].ToLower())
                        .Select(s => s.Id).ToListAsync(ct))
                    .All(scope.CanOperateSpeaker),
                _ => true,
            };
            if (!ok) denied.Add(Label(p));
        }
        return denied;
    }

    private static string Label(string[] p) => p[0] switch
    {
        "door" => $"puerta #{p[1]}",
        "area" => $"área {p[2]} del panel #{p[1]}",
        "panel" => $"todas las áreas del panel #{p[1]}",
        "ptz" => $"PTZ de la cámara #{p[1]}",
        "snapshot" => $"fotos de la cámara #{p[1]}",
        "io" => $"módulo de E/S #{p[1]}",
        "speaker" => $"parlante #{p[1]}",
        "speaker-group" => $"grupo de parlantes «{p[1]}»",
        _ => string.Join(':', p),
    };

    private static int Int(JsonElement config, string name) =>
        config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;

    private static IEnumerable<int> Ints(JsonElement config, string name)
    {
        if (!config.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int n)) yield return n;
    }
}
