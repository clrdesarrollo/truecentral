using System.Text.Json;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Services.Workflows.Actions;

/// <summary>
/// Envía un correo con el detalle del evento y, si se marca, las fotos que
/// capturaron las acciones anteriores. El asunto y el cuerpo admiten marcas
/// ({panel}, {zona}, {fechahora}, ...).
///
/// Configuración:
/// <c>{ "to": "a@b.cl, c@d.cl", "cc": "", "subject": "...", "body": "...", "attachSnapshots": true }</c>
/// </summary>
public sealed class EmailAction(SmtpSender smtp) : IWorkflowActionExecutor
{
    public string Type => WorkflowActionTypes.Email;
    public string Label => "Enviar correo";
    public string Description => "Manda un correo con los datos del evento y, si quiere, las fotos capturadas.";

    public string? Validate(JsonElement config)
    {
        string? to = config.TryGetProperty("to", out var value) ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(to)) return "Indique al menos un destinatario del correo.";
        if (SmtpSender.ParseAddresses(to).Count == 0) return "Ningún destinatario del correo tiene una dirección válida.";
        return null;
    }

    public async Task<WorkflowStepResult> ExecuteAsync(WorkflowActionContext context, CancellationToken ct)
    {
        string to = context.Render(context.Text("to"));
        if (string.IsNullOrWhiteSpace(to)) return WorkflowStepResult.Fail("La acción no tiene destinatarios.");

        string subject = context.Render(context.Text("subject", "{tipo} en {panel}"));
        string body = context.Render(context.Text("body", DefaultBody));
        IReadOnlyList<WorkflowFile> attachments = context.Flag("attachSnapshots", true) ? context.Files : [];

        string? error = await smtp.SendAsync(to, context.Render(context.Text("cc")), subject, body, attachments, ct);
        if (error is not null) return WorkflowStepResult.Fail(error);

        int recipients = SmtpSender.ParseAddresses(to).Count;
        string detail = $"Correo enviado a {recipients} destinatario(s): «{subject}»";
        if (attachments.Count > 0) detail += $" con {attachments.Count} foto(s) adjunta(s)";
        return WorkflowStepResult.Ok(detail + ".");
    }

    /// <summary>Cuerpo por defecto (el editor lo ofrece como punto de partida).</summary>
    public const string DefaultBody =
        """
        {tipo}: {evento}

        Panel: {panel}
        Área: {area}
        Zona: {zona}
        Código: {codigo}
        Fecha y hora: {fechahora}

        Aviso automático de CLR TrueCentral VMS ({servidor}) — automatización «{workflow}».
        """;
}
