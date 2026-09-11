using System.Text.Json;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services.Workflows;

/// <summary>
/// El diagrama de flujo de una automatización tal como lo ejecuta el motor:
/// nodos (disparador, condiciones, esperas, acciones, fines) y conexiones
/// con su puerto de salida. Se construye desde <c>Workflow.GraphJson</c> o,
/// para las automatizaciones anteriores al editor visual, desde la lista
/// lineal de acciones (una línea recta).
///
/// La configuración de cada acción NO vive en el grafo sino en su fila
/// <see cref="WorkflowAction"/> (con la contraseña cifrada); el nodo de
/// acción y la fila se enlazan por <see cref="WorkflowAction.NodeId"/>.
/// </summary>
public sealed class WorkflowGraph
{
    public const int MaxNodes = 60;
    public const int MaxDelaySeconds = 3600;

    public sealed record Node(string Id, string Kind, double X, double Y, string? Label, int DelaySeconds,
        WorkflowConditionsDto? Conditions, string? Type);

    public sealed record Edge(string From, string To, string Port);

    public IReadOnlyList<Node> Nodes { get; }
    public IReadOnlyList<Edge> Edges { get; }
    public Node Trigger { get; }

    private readonly Dictionary<string, Node> _byId;
    private readonly ILookup<string, Edge> _outgoing;

    private WorkflowGraph(List<Node> nodes, List<Edge> edges)
    {
        Nodes = nodes;
        Edges = edges;
        _byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        _outgoing = edges.ToLookup(e => e.From, StringComparer.Ordinal);
        Trigger = nodes.First(n => n.Kind == WorkflowNodeKinds.Trigger);
    }

    public Node? Find(string id) => _byId.TryGetValue(id, out var node) ? node : null;

    /// <summary>Nodos destino que salen del puerto dado, en el orden en que se dibujaron las conexiones.</summary>
    public IEnumerable<Node> Next(string nodeId, string port) =>
        _outgoing[nodeId].Where(e => string.Equals(e.Port, port, StringComparison.OrdinalIgnoreCase))
            .Select(e => Find(e.To)).Where(n => n is not null)!;

    // ------------------------------------------------------------------
    // Construcción
    // ------------------------------------------------------------------

    /// <summary>El grafo de la automatización: el guardado, o la línea recta de sus acciones.</summary>
    public static WorkflowGraph Of(Workflow workflow) => Parse(workflow.GraphJson) ?? Linear(workflow);

    private static WorkflowGraph? Parse(string? json)
    {
        var dto = WorkflowJson.TryDeserialize<WorkflowGraphDto>(json);
        if (dto is null || dto.Nodes is null || dto.Nodes.All(n => n.Kind != WorkflowNodeKinds.Trigger)) return null;
        var nodes = dto.Nodes.Select(n => new Node(n.Id, n.Kind, n.X, n.Y, n.Label, n.DelaySeconds, n.Conditions, n.Type)).ToList();
        var edges = (dto.Edges ?? []).Select(e => new Edge(e.From, e.To, string.IsNullOrEmpty(e.Port) ? WorkflowPorts.Next : e.Port)).ToList();
        return new WorkflowGraph(nodes, edges);
    }

    /// <summary>
    /// Línea recta con las acciones en orden (automatizaciones anteriores al
    /// editor). "Seguir si falla" se traduce en una conexión extra desde el
    /// puerto de error al mismo nodo siguiente.
    /// </summary>
    private static WorkflowGraph Linear(Workflow workflow)
    {
        var nodes = new List<Node> { new("trigger", WorkflowNodeKinds.Trigger, 0, 0, null, 0, null, null) };
        var edges = new List<Edge>();
        string previous = "trigger";
        bool previousContinues = true;
        int y = 0;
        foreach (var action in workflow.Actions.OrderBy(a => a.Order))
        {
            string id = action.NodeId ?? $"a{action.Id}";
            y += 130;
            nodes.Add(new Node(id, WorkflowNodeKinds.Action, 0, y, null, action.DelaySeconds, null, action.Type));
            edges.Add(new Edge(previous, id, WorkflowPorts.Next));
            if (previous != "trigger" && previousContinues) edges.Add(new Edge(previous, id, WorkflowPorts.Error));
            previous = id;
            previousContinues = action.ContinueOnError;
        }
        return new WorkflowGraph(nodes, edges);
    }

    /// <summary>Posiciones de la línea recta (para que el editor la muestre ordenada de entrada).</summary>
    public static WorkflowGraphDto LinearDto(Workflow workflow)
    {
        var graph = Linear(workflow);
        var actions = workflow.Actions.OrderBy(a => a.Order).ToList();
        var nodes = graph.Nodes.Select(n =>
        {
            if (n.Kind != WorkflowNodeKinds.Action) return new WorkflowNodeDto(n.Id, n.Kind, 260, n.Y, n.Label);
            var action = actions.First(a => (a.NodeId ?? $"a{a.Id}") == n.Id);
            return ActionNode(n, action);
        }).ToList();
        return new WorkflowGraphDto(nodes, graph.Edges.Select(e => new WorkflowEdgeDto(e.From, e.To, e.Port)).ToList());
    }

    /// <summary>DTO completo para el editor: los nodos de acción llevan su configuración (sin la contraseña).</summary>
    public static WorkflowGraphDto ToDto(Workflow workflow)
    {
        var stored = WorkflowJson.TryDeserialize<WorkflowGraphDto>(workflow.GraphJson);
        if (stored is null || stored.Nodes is null || stored.Nodes.All(n => n.Kind != WorkflowNodeKinds.Trigger))
            return LinearDto(workflow);

        var byNode = workflow.Actions.Where(a => a.NodeId is not null).ToDictionary(a => a.NodeId!, StringComparer.Ordinal);
        var nodes = stored.Nodes.Select(n =>
        {
            if (n.Kind == WorkflowNodeKinds.Trigger)
                return n with { Conditions = WorkflowJson.Conditions(workflow.ConditionsJson) };
            if (n.Kind != WorkflowNodeKinds.Action || !byNode.TryGetValue(n.Id, out var action))
                return n with { Config = null, Secret = null };
            return ActionNode(new Node(n.Id, n.Kind, n.X, n.Y, n.Label, action.DelaySeconds, null, action.Type), action);
        }).ToList();
        return new WorkflowGraphDto(nodes, stored.Edges ?? []);
    }

    private static WorkflowNodeDto ActionNode(Node n, WorkflowAction action) => new(
        n.Id, WorkflowNodeKinds.Action, n.X, n.Y, n.Label,
        Type: action.Type,
        Config: WorkflowJson.ParseConfig(action.ConfigJson),
        Enabled: action.Enabled,
        DelaySeconds: action.DelaySeconds,
        HasSecret: action.SecretCiphertext is { Length: > 0 },
        ActionId: action.Id);

    // ------------------------------------------------------------------
    // Validación y volcado desde el editor
    // ------------------------------------------------------------------

    /// <summary>Revisa el diagrama que manda el editor; devuelve el error en español o null.</summary>
    public static string? Validate(WorkflowGraphDto graph, WorkflowEngine engine)
    {
        if (graph.Nodes is null || graph.Nodes.Count == 0) return "El diagrama está vacío.";
        if (graph.Nodes.Count > MaxNodes) return $"Un diagrama admite hasta {MaxNodes} pasos.";

        var ids = new HashSet<string>(StringComparer.Ordinal);
        int triggers = 0, actions = 0;
        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || node.Id.Length > 32) return "Un paso del diagrama no tiene identificador válido.";
            if (!ids.Add(node.Id)) return $"Hay dos pasos con el mismo identificador ('{node.Id}').";
            if (!WorkflowNodeKinds.All.Contains(node.Kind)) return $"Tipo de paso desconocido: '{node.Kind}'.";
            if (node.Label is { Length: > 64 }) return "El rótulo de un paso no puede superar los 64 caracteres.";
            switch (node.Kind)
            {
                case WorkflowNodeKinds.Trigger:
                    triggers++;
                    break;
                case WorkflowNodeKinds.Action:
                {
                    actions++;
                    if (string.IsNullOrEmpty(node.Type)) return "Un paso de acción no tiene tipo.";
                    var executor = engine.FindExecutor(node.Type);
                    if (executor is null) return $"Acción desconocida: '{node.Type}'.";
                    if (node.DelaySeconds is < 0 or > 600) return $"«{executor.Label}»: la espera previa debe estar entre 0 y 600 segundos.";
                    var config = node.Config ?? WorkflowJson.ParseConfig(null);
                    if (config.ValueKind != JsonValueKind.Object) return $"La configuración de la acción «{executor.Label}» no es válida.";
                    if (executor.Validate(config) is { } invalid) return $"«{executor.Label}»: {invalid}";
                    break;
                }
                case WorkflowNodeKinds.Delay:
                    if (node.DelaySeconds is < 1 or > MaxDelaySeconds)
                        return $"Una espera debe estar entre 1 y {MaxDelaySeconds} segundos.";
                    break;
                case WorkflowNodeKinds.Condition:
                    if (node.Conditions is null) return $"La condición '{node.Label ?? node.Id}' no tiene nada que evaluar.";
                    if (ValidateConditions(node.Conditions) is { } bad) return $"Condición '{node.Label ?? node.Id}': {bad}";
                    break;
            }
        }
        if (triggers != 1) return "El diagrama debe tener exactamente un punto de partida (disparador).";
        if (actions == 0) return "Agregue al menos una acción al diagrama.";

        var byId = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        foreach (var edge in graph.Edges ?? [])
        {
            if (!byId.TryGetValue(edge.From, out var from) || !byId.ContainsKey(edge.To))
                return "Hay una conexión que apunta a un paso que no existe.";
            if (byId[edge.To].Kind == WorkflowNodeKinds.Trigger) return "Nada puede conectarse hacia el punto de partida.";
            if (from.Kind == WorkflowNodeKinds.End) return "Un paso de fin no tiene salida.";
            string port = string.IsNullOrEmpty(edge.Port) ? WorkflowPorts.Next : edge.Port;
            bool valid = from.Kind switch
            {
                WorkflowNodeKinds.Condition => port is WorkflowPorts.Yes or WorkflowPorts.No,
                WorkflowNodeKinds.Action => port is WorkflowPorts.Next or WorkflowPorts.Error,
                _ => port == WorkflowPorts.Next,
            };
            if (!valid) return $"La salida '{port}' no existe en el paso '{from.Label ?? from.Kind}'.";
        }

        // Todo lo dibujado tiene que ser alcanzable desde el disparador: un
        // paso suelto es casi siempre un error de armado.
        var trigger = graph.Nodes.First(n => n.Kind == WorkflowNodeKinds.Trigger);
        var reachable = new HashSet<string>(StringComparer.Ordinal) { trigger.Id };
        var queue = new Queue<string>([trigger.Id]);
        var outgoing = (graph.Edges ?? []).ToLookup(e => e.From, StringComparer.Ordinal);
        while (queue.TryDequeue(out var current))
            foreach (var edge in outgoing[current])
                if (reachable.Add(edge.To)) queue.Enqueue(edge.To);
        var loose = graph.Nodes.FirstOrDefault(n => !reachable.Contains(n.Id));
        if (loose is not null)
            return $"El paso '{loose.Label ?? Describe(loose, engine)}' no está conectado al flujo: conéctelo o elimínelo.";
        if (!graph.Nodes.Any(n => n.Kind == WorkflowNodeKinds.Action && reachable.Contains(n.Id)))
            return "Ninguna acción está conectada al punto de partida.";
        return null;
    }

    private static string Describe(WorkflowNodeDto node, WorkflowEngine engine) => node.Kind switch
    {
        WorkflowNodeKinds.Action => engine.FindExecutor(node.Type ?? "")?.Label ?? node.Type ?? "acción",
        WorkflowNodeKinds.Condition => "condición",
        WorkflowNodeKinds.Delay => "espera",
        WorkflowNodeKinds.End => "fin",
        _ => node.Kind,
    };

    /// <summary>Reglas comunes a las condiciones del disparador y de los nodos de condición.</summary>
    public static string? ValidateConditions(WorkflowConditionsDto conditions)
    {
        if (conditions.FromTime is { Length: > 0 } from && !TimeSpan.TryParse(from, out _))
            return "la hora de inicio de la ventana horaria no es válida (use hh:mm).";
        if (conditions.ToTime is { Length: > 0 } to && !TimeSpan.TryParse(to, out _))
            return "la hora de término de la ventana horaria no es válida (use hh:mm).";
        if (conditions.DaysOfWeek is { Count: > 0 } days && days.Any(d => d is < 0 or > 6))
            return "los días de la semana deben ir de 0 (domingo) a 6 (sábado).";
        if (conditions.SustainedSeconds is < 0 or > 600)
            return "la condición sostenida debe estar entre 0 y 600 segundos.";
        if (conditions.MinConfidence is < 0 or > 100)
            return "la confianza mínima debe estar entre 0 y 100.";
        if (conditions.ScheduleTimes is { Count: > 0 } times && times.Any(t => !TimeSpan.TryParse(t, out _)))
            return "las horas programadas deben tener el formato hh:mm.";
        if (conditions.PlateMatch is { Length: > 0 } match && match is not ("any" or "listed" or "unlisted"))
            return "el modo de comparación de patentes no es válido.";
        return null;
    }

    /// <summary>
    /// Vuelca el diagrama del editor sobre la entidad: guarda los nodos y
    /// conexiones (sin la configuración de las acciones) en GraphJson y una
    /// fila <see cref="WorkflowAction"/> por nodo de acción. Las contraseñas
    /// que el panel no reenvía (nunca las recibe) se arrastran por ActionId.
    /// </summary>
    public static void Apply(Workflow workflow, WorkflowGraphDto graph, IReadOnlyList<WorkflowAction> previous,
        CredentialProtector protector)
    {
        // Orden de aparición desde el disparador: es el orden de la lista
        // lineal si alguien la lee sin el diagrama, y el del historial.
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        var trigger = graph.Nodes.First(n => n.Kind == WorkflowNodeKinds.Trigger);
        var outgoing = (graph.Edges ?? []).ToLookup(e => e.From, StringComparer.Ordinal);
        var queue = new Queue<string>([trigger.Id]);
        var seen = new HashSet<string>(StringComparer.Ordinal) { trigger.Id };
        int position = 0;
        while (queue.TryDequeue(out var current))
        {
            order[current] = position++;
            foreach (var edge in outgoing[current])
                if (seen.Add(edge.To)) queue.Enqueue(edge.To);
        }

        int next = 1;
        foreach (var node in graph.Nodes.Where(n => n.Kind == WorkflowNodeKinds.Action)
                     .OrderBy(n => order.TryGetValue(n.Id, out var p) ? p : int.MaxValue))
        {
            byte[]? secret = null;
            if (!string.IsNullOrEmpty(node.Secret))
                secret = protector.Protect(node.Secret);
            else if (node.ActionId > 0)
                secret = previous.FirstOrDefault(p => p.Id == node.ActionId)?.SecretCiphertext;
            else
                secret = previous.FirstOrDefault(p => p.NodeId == node.Id)?.SecretCiphertext;

            workflow.Actions.Add(new WorkflowAction
            {
                Order = next++,
                NodeId = node.Id,
                Type = node.Type!,
                Enabled = node.Enabled,
                // En el diagrama "seguir si falla" es una conexión desde el
                // puerto de error; la bandera queda por compatibilidad.
                ContinueOnError = outgoing[node.Id].Any(e => e.Port == WorkflowPorts.Error),
                DelaySeconds = Math.Clamp(node.DelaySeconds, 0, 600),
                ConfigJson = (node.Config ?? WorkflowJson.ParseConfig(null)).GetRawText(),
                SecretCiphertext = secret,
            });
        }

        var stored = new WorkflowGraphDto(
            graph.Nodes.Select(n => new WorkflowNodeDto(n.Id, n.Kind, Math.Round(n.X), Math.Round(n.Y), Clean(n.Label),
                Type: n.Kind == WorkflowNodeKinds.Action ? n.Type : null,
                Enabled: n.Enabled,
                DelaySeconds: n.Kind == WorkflowNodeKinds.Delay ? n.DelaySeconds : 0,
                Conditions: n.Kind == WorkflowNodeKinds.Condition ? n.Conditions : null)).ToList(),
            (graph.Edges ?? []).Select(e => new WorkflowEdgeDto(e.From, e.To, string.IsNullOrEmpty(e.Port) ? WorkflowPorts.Next : e.Port))
                .ToList());
        workflow.GraphJson = WorkflowJson.Serialize(stored);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
