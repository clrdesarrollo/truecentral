using System.Text.Json;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services.Workflows.Actions;

/// <summary>
/// Anota un ingreso o salida en el registro de accesos vehiculares: quién
/// (persona y vehículo del padrón), qué patente leyó qué cámara, a qué hora y
/// qué pasó (apertura automática, fuera de horario, rechazada, falló la
/// apertura). Va en cada rama del diagrama del acceso, así que el resultado
/// lo pone el usuario al configurarla, no se adivina.
///
/// Configuración: <c>{ "direction": "Entry", "accessPoint": "Acceso principal",
/// "result": "AutoOpened" }</c> (direction: Entry | Exit; result: ver
/// <see cref="VehicleAccessResult"/>).
///
/// El registro es histórico: copia nombres y el recorte de la patente (la
/// lectura ANPR se purga mucho antes). No va a la bitácora de auditoría: es
/// un evento del acceso, no una acción de un usuario (las órdenes a los relés
/// sí quedan en la bitácora, las anota la acción de relé).
/// </summary>
public sealed class VehicleAccessAction(
    IServiceScopeFactory scopeFactory,
    AnprStore store,
    ILogger<VehicleAccessAction> logger) : IWorkflowActionExecutor
{
    public string Type => WorkflowActionTypes.VehicleAccess;
    public string Label => "Registrar paso vehicular";
    public string Description => "Anota el ingreso o la salida del vehículo (persona, patente, cámara, hora y resultado) en el registro de accesos vehiculares.";

    public static string DirectionLabel(VehicleAccessDirection direction) =>
        direction == VehicleAccessDirection.Exit ? "Salida" : "Ingreso";

    public static string ResultLabel(VehicleAccessResult result) => result switch
    {
        VehicleAccessResult.AutoOpened => "Apertura automática",
        VehicleAccessResult.OutsideSchedule => "Fuera de horario: abre el guardia",
        VehicleAccessResult.Rejected => "Rechazado",
        VehicleAccessResult.OpenFailed => "Falló la apertura",
        _ => "Registrado",
    };

    public string? Validate(JsonElement config)
    {
        string direction = config.TryGetProperty("direction", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
        if (!Enum.TryParse<VehicleAccessDirection>(direction, true, out _)) return "Elija si es un ingreso o una salida.";
        string result = config.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
        if (!Enum.TryParse<VehicleAccessResult>(result, true, out _)) return "Elija el resultado que se anota.";
        string point = config.TryGetProperty("accessPoint", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        if (point.Trim().Length == 0) return "Escriba el nombre del acceso (p. ej. «Acceso principal»).";
        if (point.Trim().Length > 96) return "El nombre del acceso no puede pasar de 96 caracteres.";
        return null;
    }

    public async Task<WorkflowStepResult> ExecuteAsync(WorkflowActionContext context, CancellationToken ct)
    {
        var trigger = context.Trigger;
        if (trigger.Plate is not { Length: > 0 } plate)
            return WorkflowStepResult.Fail("Esta acción solo sirve en automatizaciones de lectura de patente.");
        if (!Enum.TryParse<VehicleAccessDirection>(context.Text("direction"), true, out var direction))
            return WorkflowStepResult.Fail("La acción no dice si es un ingreso o una salida.");
        if (!Enum.TryParse<VehicleAccessResult>(context.Text("result", nameof(VehicleAccessResult.Logged)), true, out var result))
            result = VehicleAccessResult.Logged;
        string accessPoint = context.Text("accessPoint").Trim() is { Length: > 0 } named ? named : "Acceso vehicular";

        string Field(string key) => trigger.Fields.TryGetValue(key, out var value) && value.Length > 0 ? value : "";
        string? Optional(string key) => Field(key) is { Length: > 0 } value ? value : null;
        string camera = Field("camara") is { Length: > 0 } cam
            ? (Field("equipo") is { Length: > 0 } dev && dev != cam ? $"{dev} · {cam}" : cam)
            : Field("equipo");

        var record = new VehicleAccessRecord
        {
            At = DateTime.UtcNow,
            AccessPoint = Cut(accessPoint, 96)!,
            Direction = direction,
            Result = result,
            Plate = Cut(plate, 24)!,
            Confidence = trigger.Confidence,
            PlateStatus = trigger.PlateStatus ?? PlateRegistryStatuses.Unregistered,
            DeviceId = trigger.DeviceId is > 0 ? trigger.DeviceId : null,
            ChannelId = trigger.ChannelId,
            CameraName = Cut(camera, 256) ?? "",
            AccessVehicleId = trigger.AccessVehicleId,
            VehicleLabel = Cut(Optional("vehiculo"), 64),
            AccessPersonId = trigger.AccessPersonId,
            PersonName = Cut(Optional("persona"), 256),
            EmployeeNo = Cut(Optional("empleado"), 64),
            Department = Cut(Optional("departamento"), 128),
            WorkflowId = context.Workflow.Id > 0 ? context.Workflow.Id : null,
            WorkflowName = Cut(context.Workflow.Name, 128),
            PlateEventId = trigger.PlateEventId,
            PlateImagePath = store.Save(trigger.PlateImage, trigger.At, "placa", "vehicular"),
            Detail = Cut(Optional("motivopadron"), 512),
        };

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            db.VehicleAccessRecords.Add(record);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            store.Delete([record.PlateImagePath]);
            logger.LogWarning(ex, "No se pudo anotar el paso vehicular de la patente {Plate}.", plate);
            return WorkflowStepResult.Fail($"No se pudo anotar el paso vehicular: {ex.Message}");
        }

        string who = record.PersonName is { } person
            ? person + (record.VehicleLabel is { } label ? $" ({label})" : "")
            : PlateRegistryStatuses.Label(record.PlateStatus).ToLowerInvariant();
        return WorkflowStepResult.Ok(
            $"{DirectionLabel(direction)} de {record.Plate} ({who}) en «{record.AccessPoint}»: {ResultLabel(result).ToLowerInvariant()}.");
    }

    private static string? Cut(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
