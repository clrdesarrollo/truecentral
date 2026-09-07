using System.Text.Json;
using System.Text.Json.Serialization;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Services.Workflows;

/// <summary>
/// Serialización compartida del módulo: las condiciones y el detalle de las
/// ejecuciones se guardan como JSON en la base y viajan por la API con el
/// MISMO formato (camelCase, enums como texto), así lo que se lee en la base
/// es lo mismo que ve el panel.
/// </summary>
public static class WorkflowJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Deserializa tolerando null y JSON corrupto (una condición ilegible no debe voltear el motor).</summary>
    public static T? TryDeserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json, Options); }
        catch (JsonException) { return default; }
    }

    /// <summary>Objeto JSON de la configuración de una acción; objeto vacío si es ilegible.</summary>
    public static JsonElement ParseConfig(string? json)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    return document.RootElement.Clone();
            }
            catch (JsonException) { /* configuración corrupta: se trata como vacía */ }
        }
        return EmptyObject;
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>Condiciones de un workflow (nunca null: sin filtro = todo pasa).</summary>
    public static WorkflowConditionsDto Conditions(string? json) =>
        TryDeserialize<WorkflowConditionsDto>(json) ?? new WorkflowConditionsDto();

    /// <summary>Pasos de una ejecución guardados en el historial.</summary>
    public static IReadOnlyList<WorkflowRunStepDto> Steps(string? json) =>
        TryDeserialize<List<WorkflowRunStepDto>>(json) ?? [];
}
