using System.Text;
using System.Text.Json;
using FluentFTP;
using FluentFTP.Exceptions;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Services.Workflows.Actions;

/// <summary>
/// Sube a un servidor FTP/FTPS los archivos que produjo la ejecución (las
/// fotos de la acción "capturar foto") y, opcionalmente, un informe de texto
/// con los datos del evento. El directorio remoto admite marcas, así que se
/// puede ordenar por fecha o por panel ("/alarmas/{fecha}/{panel}").
///
/// Configuración:
/// <c>{ "host":"", "port":21, "username":"", "security":"None|Explicit|Implicit",
///      "remoteDirectory":"/alarmas/{fecha}", "includeReport":true,
///      "allowInvalidCertificate":false }</c>
/// La contraseña va aparte, cifrada.
/// </summary>
public sealed class FtpAction(ILogger<FtpAction> logger) : IWorkflowActionExecutor
{
    public string Type => WorkflowActionTypes.Ftp;
    public string Label => "Subir a FTP";
    public string Description => "Sube las fotos de la ejecución (y un informe opcional) a un servidor FTP o FTPS.";
    public bool UsesSecret => true;

    public string? Validate(JsonElement config)
    {
        string? host = config.TryGetProperty("host", out var value) ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(host)) return "Indique la dirección del servidor FTP.";
        return null;
    }

    public async Task<WorkflowStepResult> ExecuteAsync(WorkflowActionContext context, CancellationToken ct)
    {
        string host = context.Text("host").Trim();
        if (host.Length == 0) return WorkflowStepResult.Fail("La acción no tiene servidor FTP configurado.");
        int port = context.Number("port", 21);
        string username = context.Text("username").Trim();
        string directory = NormalizeDirectory(context.Render(context.Text("remoteDirectory", "/")));

        // Lo que se sube: las fotos de la ejecución y, si se pidió, un informe
        // de texto con el evento (sirve para los FTP que alimentan a otro
        // sistema, que muchas veces solo leen texto).
        var uploads = new List<(string Name, byte[] Content)>();
        foreach (var file in context.Files)
        {
            try { uploads.Add((file.FileName, await File.ReadAllBytesAsync(file.FullPath, ct))); }
            catch (Exception ex) { logger.LogWarning(ex, "No se pudo leer {File} para subirlo por FTP.", file.FullPath); }
        }
        if (context.Flag("includeReport", false))
            uploads.Add(($"{context.Trigger.At:yyyyMMdd-HHmmss}-{WorkflowStore.Sanitize(context.Workflow.Name)}.txt",
                Encoding.UTF8.GetBytes(BuildReport(context))));

        if (uploads.Count == 0)
            return WorkflowStepResult.Fail("No había archivos que subir: agregue antes una acción «Capturar foto» o marque el informe de texto.");

        var encryption = context.Text("security", "None").ToLowerInvariant() switch
        {
            "explicit" => FtpEncryptionMode.Explicit,
            "implicit" => FtpEncryptionMode.Implicit,
            _ => FtpEncryptionMode.None,
        };

        await using var client = new AsyncFtpClient(host, username, context.Secret ?? "", port);
        client.Config.EncryptionMode = encryption;
        client.Config.ValidateAnyCertificate = context.Flag("allowInvalidCertificate", false);
        client.Config.ConnectTimeout = 15_000;
        client.Config.ReadTimeout = 30_000;
        client.Config.DataConnectionConnectTimeout = 15_000;
        client.Config.DataConnectionReadTimeout = 30_000;
        // Los servidores FTPS suelen exigir también el canal de datos cifrado.
        client.Config.DataConnectionEncryption = encryption != FtpEncryptionMode.None;

        int uploaded = 0;
        try
        {
            await client.Connect(ct);
            foreach (var (name, content) in uploads)
            {
                string remotePath = directory.EndsWith('/') ? directory + name : $"{directory}/{name}";
                var status = await client.UploadBytes(content, remotePath, FtpRemoteExists.Overwrite,
                    createRemoteDir: true, token: ct);
                if (status == FtpStatus.Success) uploaded++;
                else logger.LogWarning("El servidor FTP {Host} rechazó '{Path}' ({Status}).", host, remotePath, status);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (FtpAuthenticationException)
        {
            return WorkflowStepResult.Fail($"El servidor FTP {host} rechazó el usuario o la contraseña.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fallo al subir archivos al FTP {Host}:{Port}.", host, port);
            return WorkflowStepResult.Fail($"No se pudo subir a {host}: {ex.Message}");
        }
        finally
        {
            try { if (client.IsConnected) await client.Disconnect(CancellationToken.None); }
            catch (Exception ex) { logger.LogDebug(ex, "Cierre sucio de la conexión FTP."); }
        }

        if (uploaded == 0)
            return WorkflowStepResult.Fail($"El servidor {host} no aceptó ninguno de los {uploads.Count} archivo(s).");
        return WorkflowStepResult.Ok($"{uploaded} de {uploads.Count} archivo(s) subidos a {host}:{port} en '{directory}'.");
    }

    /// <summary>Informe de texto del evento (mismo contenido que el cuerpo del correo).</summary>
    private static string BuildReport(WorkflowActionContext context)
    {
        var report = new StringBuilder();
        report.AppendLine($"CLR TrueCentral VMS — automatización «{context.Workflow.Name}»");
        report.AppendLine(new string('-', 60));
        foreach (var (key, value) in context.Trigger.Fields.OrderBy(f => f.Key))
        {
            if (value.Length == 0) continue;
            report.AppendLine($"{key,-12}: {value}");
        }
        report.AppendLine(new string('-', 60));
        report.AppendLine(context.Trigger.Summary);
        return report.ToString();
    }

    private static string NormalizeDirectory(string directory)
    {
        string clean = directory.Trim().Replace('\\', '/');
        if (clean.Length == 0) clean = "/";
        if (!clean.StartsWith('/')) clean = "/" + clean;
        return clean.Length > 1 ? clean.TrimEnd('/') : clean;
    }
}
