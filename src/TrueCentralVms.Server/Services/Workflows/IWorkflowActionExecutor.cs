using System.Text.Json;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services.Workflows;

/// <summary>Archivo producido por una acción y disponible para las siguientes (fotos).</summary>
public sealed record WorkflowFile(string RelativePath, string FullPath, string FileName);

/// <summary>Resultado de una acción, tal como queda en el historial de la ejecución.</summary>
public sealed record WorkflowStepResult(bool Success, string Detail, IReadOnlyList<string>? Files = null)
{
    public static WorkflowStepResult Ok(string detail, IReadOnlyList<string>? files = null) => new(true, detail, files);
    public static WorkflowStepResult Fail(string detail) => new(false, detail);
}

/// <summary>
/// Todo lo que una acción necesita para ejecutarse: la configuración ya
/// deserializada, la contraseña descifrada, los datos del evento que disparó
/// el workflow y los archivos que produjeron las acciones anteriores.
/// </summary>
public sealed class WorkflowActionContext
{
    public required Workflow Workflow { get; init; }
    public required WorkflowAction Action { get; init; }
    public required WorkflowTrigger Trigger { get; init; }
    public required JsonElement Config { get; init; }
    /// <summary>Contraseña de la acción ya descifrada (null si no tiene).</summary>
    public string? Secret { get; init; }
    /// <summary>Archivos acumulados en esta ejecución (los agrega la acción "capturar foto").</summary>
    public required List<WorkflowFile> Files { get; init; }

    /// <summary>
    /// Cámaras involucradas en esta ejecución (las que capturaron foto). La
    /// alerta las guarda para que el operador pueda ver el VIVO de la cámara
    /// del sector, no solo la foto del instante.
    /// </summary>
    public required List<int> Channels { get; init; }

    /// <summary>
    /// Alertas creadas durante esta ejecución. El motor las enlaza con la
    /// ejecución una vez que la guarda (el id de la ejecución todavía no
    /// existe mientras las acciones corren).
    /// </summary>
    public required List<long> Alerts { get; init; }

    /// <summary>Reemplaza las marcas {panel}, {zona}, ... del texto.</summary>
    public string Render(string? template) => Trigger.Render(template, Workflow.Name);

    /// <summary>Igual que <see cref="Render"/> pero escapando los valores para una URL.</summary>
    public string RenderUrl(string? template) => Trigger.Render(template, Workflow.Name, Uri.EscapeDataString);

    // --- Lectura tolerante de la configuración (viene de un JSON del panel) ---

    public string Text(string key, string fallback = "")
    {
        if (!Config.TryGetProperty(key, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? fallback,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => fallback,
        };
    }

    public bool Flag(string key, bool fallback = false)
    {
        if (!Config.TryGetProperty(key, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out bool parsed) ? parsed : fallback,
            _ => fallback,
        };
    }

    public int Number(string key, int fallback)
    {
        if (!Config.TryGetProperty(key, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out int number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out int parsed) => parsed,
            _ => fallback,
        };
    }

    public IReadOnlyList<int> Numbers(string key)
    {
        if (!Config.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array) return [];
        var result = new List<int>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int number)) result.Add(number);
            else if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), out int parsed)) result.Add(parsed);
        }
        return result;
    }
}

/// <summary>
/// Una acción que un workflow puede ejecutar. Para agregar una acción nueva
/// (grabar un clip, encender una salida, avisar por otra vía) basta con
/// implementar esta interfaz y registrarla en el arranque: aparece sola en el
/// catálogo que consume el editor del panel.
/// </summary>
public interface IWorkflowActionExecutor
{
    /// <summary>Clave estable que se guarda en la base (ver <c>WorkflowActionTypes</c>).</summary>
    string Type { get; }

    /// <summary>Nombre para mostrar en el panel.</summary>
    string Label { get; }

    /// <summary>Qué hace, en una línea (ayuda del editor).</summary>
    string Description { get; }

    /// <summary>La acción puede llevar contraseña (se guarda cifrada, nunca se devuelve).</summary>
    bool UsesSecret => false;

    /// <summary>Valida la configuración al guardar; devuelve el error en español o null.</summary>
    string? Validate(JsonElement config) => null;

    /// <summary>
    /// Ejecuta la acción. NO debe lanzar: los errores previsibles se devuelven
    /// como resultado fallido con un mensaje en español (el motor igual
    /// atrapa lo inesperado).
    /// </summary>
    Task<WorkflowStepResult> ExecuteAsync(WorkflowActionContext context, CancellationToken ct);
}
