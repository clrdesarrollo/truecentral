using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services.Workflows;

/// <summary>
/// Correo saliente del VMS. La configuración es única para todo el sistema
/// (fila <see cref="SmtpSettings"/>) y la usan tanto la acción "enviar correo"
/// de las automatizaciones como el botón de prueba del panel.
///
/// Devuelve el error en español en vez de lanzar: un correo que no salió es
/// un paso fallido de la ejecución, no una excepción que voltee el motor.
/// </summary>
public sealed class SmtpSender(
    IServiceScopeFactory scopeFactory,
    CredentialProtector credentials,
    ILogger<SmtpSender> logger)
{
    /// <summary>Configuración guardada, o null si nunca se configuró.</summary>
    public async Task<SmtpSettings?> LoadAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        return await db.SmtpSettings.AsNoTracking().OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Envía un correo con la configuración guardada. Devuelve null si salió
    /// bien o el motivo del fallo.
    /// </summary>
    public async Task<string?> SendAsync(string to, string? cc, string subject, string body,
        IReadOnlyList<WorkflowFile>? attachments, CancellationToken ct)
    {
        var settings = await LoadAsync(ct);
        if (settings is null || !settings.Enabled || string.IsNullOrWhiteSpace(settings.Host))
            return "No hay servidor de correo configurado (Automatizaciones → Servidor de correo).";
        return await SendAsync(settings, to, cc, subject, body, attachments, ct);
    }

    public async Task<string?> SendAsync(SmtpSettings settings, string to, string? cc, string subject, string body,
        IReadOnlyList<WorkflowFile>? attachments, CancellationToken ct)
    {
        var recipients = ParseAddresses(to);
        if (recipients.Count == 0) return "No hay destinatarios válidos.";
        if (string.IsNullOrWhiteSpace(settings.FromAddress)) return "Falta la dirección del remitente en el servidor de correo.";

        var message = new MimeMessage();
        try
        {
            message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        }
        catch (ParseException)
        {
            return $"La dirección del remitente '{settings.FromAddress}' no es válida.";
        }
        foreach (var address in recipients) message.To.Add(address);
        foreach (var address in ParseAddresses(cc)) message.Cc.Add(address);
        message.Subject = string.IsNullOrWhiteSpace(subject) ? "Aviso de CLR TrueCentral VMS" : subject;

        var builder = new BodyBuilder { TextBody = body };
        foreach (var file in attachments ?? [])
        {
            try
            {
                byte[] content = await File.ReadAllBytesAsync(file.FullPath, ct);
                builder.Attachments.Add(file.FileName, content, ContentTypeOf(file.FileName));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No se pudo adjuntar el archivo {File} al correo.", file.FullPath);
            }
        }
        message.Body = builder.ToMessageBody();

        var options = settings.Security switch
        {
            SmtpSecurity.Ssl => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            _ => SecureSocketOptions.None,
        };

        using var client = new SmtpClient { Timeout = 30_000 };
        if (settings.AllowInvalidCertificate)
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        try
        {
            await client.ConnectAsync(settings.Host, settings.Port, options, ct);
            if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                string password = settings.PasswordCiphertext is { Length: > 0 }
                    ? credentials.Unprotect(settings.PasswordCiphertext)
                    : "";
                await client.AuthenticateAsync(settings.Username, password, ct);
            }
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
            return null;
        }
        catch (AuthenticationException)
        {
            return "El servidor de correo rechazó el usuario o la contraseña.";
        }
        catch (SmtpCommandException ex)
        {
            return $"El servidor de correo rechazó el envío: {ex.Message} ({ex.StatusCode}).";
        }
        catch (SslHandshakeException ex)
        {
            return $"No se pudo establecer el canal seguro con el servidor de correo: {ex.Message}. " +
                   "Revise el tipo de seguridad (STARTTLS suele ser 587 y TLS implícito 465).";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fallo al enviar correo por {Host}:{Port}.", settings.Host, settings.Port);
            return $"No se pudo enviar el correo: {ex.Message}";
        }
        finally
        {
            if (client.IsConnected)
            {
                try { await client.DisconnectAsync(true, CancellationToken.None); } catch (Exception) { /* ya se cae solo */ }
            }
        }
    }

    /// <summary>Direcciones separadas por coma, punto y coma o salto de línea.</summary>
    public static List<MailboxAddress> ParseAddresses(string? value)
    {
        var result = new List<MailboxAddress>();
        if (string.IsNullOrWhiteSpace(value)) return result;
        foreach (string part in value.Split([',', ';', '\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (MailboxAddress.TryParse(part, out var address)) result.Add(address);
        }
        return result;
    }

    private static ContentType ContentTypeOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => new ContentType("image", "jpeg"),
        ".png" => new ContentType("image", "png"),
        ".txt" => new ContentType("text", "plain"),
        _ => new ContentType("application", "octet-stream"),
    };
}
