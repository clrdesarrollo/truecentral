using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services.Workflows;

/// <summary>Conversión entidad → DTO compartida por la API y el motor.</summary>
public static class WorkflowMapper
{
    public static WorkflowDto ToDto(Workflow workflow) => new(
        workflow.Id, workflow.Name, workflow.Description, workflow.Enabled, workflow.TriggerType,
        WorkflowJson.Conditions(workflow.ConditionsJson), workflow.CooldownSeconds,
        workflow.LastRunAt, workflow.RunCount, workflow.CreatedAt, workflow.CreatedBy,
        workflow.Actions.OrderBy(a => a.Order).Select(ToDto).ToList());

    public static WorkflowActionDto ToDto(WorkflowAction action) => new(
        action.Id, action.Order, action.Type, action.Enabled, action.ContinueOnError, action.DelaySeconds,
        WorkflowJson.ParseConfig(action.ConfigJson), action.SecretCiphertext is { Length: > 0 });

    public static WorkflowRunDto ToDto(WorkflowRun run, IReadOnlyList<WorkflowRunStepDto>? steps = null) => new(
        run.Id, run.WorkflowId, run.WorkflowName, run.StartedAt, run.FinishedAt, run.Success,
        run.TriggerSummary, run.Error, run.StartedBy, steps ?? WorkflowJson.Steps(run.StepsJson));

    public static WorkflowAlertDto ToDto(WorkflowAlert alert) => new(
        alert.Id, alert.RunId, alert.WorkflowId, alert.WorkflowName, alert.RaisedAt,
        alert.Title, alert.Message, alert.Severity, alert.ImagePath,
        WorkflowJson.TryDeserialize<List<string>>(alert.ImagePathsJson) ?? (alert.ImagePath is { Length: > 0 } one ? [one] : []),
        WorkflowJson.TryDeserialize<List<int>>(alert.ChannelIdsJson) ?? [],
        alert.Sound, alert.SoundRepeat,
        alert.TriggerSummary, alert.RequiresAck,
        alert.AcknowledgedAt, alert.AcknowledgedBy, alert.AcknowledgedFrom,
        alert.AcknowledgedAt is { } ack ? (int)Math.Round((ack - alert.RaisedAt).TotalSeconds) : null);

    public static SmtpSettingsDto ToDto(SmtpSettings settings) => new(
        settings.Enabled, settings.Host, settings.Port, settings.Security, settings.Username,
        settings.PasswordCiphertext is { Length: > 0 }, settings.AllowInvalidCertificate,
        settings.FromAddress, settings.FromName, settings.UpdatedAt);
}
