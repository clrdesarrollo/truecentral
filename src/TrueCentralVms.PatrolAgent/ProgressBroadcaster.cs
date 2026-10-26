using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.PatrolAgent;

/// <summary>
/// Publica el progreso de las descargas por un named pipe local. El servicio
/// corre sin escritorio (sesión 0) y no puede mostrar ventanas: el aviso en
/// pantalla lo pone truecentral-patrol-aviso, que se inicia con la sesión del
/// usuario y se conecta aquí. Solo lectura para los usuarios de la máquina.
/// </summary>
public sealed class ProgressBroadcaster(ILogger<ProgressBroadcaster> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly List<NamedPipeServerStream> _clients = [];
    private readonly object _gate = new();
    private PatrolDockProgress? _last;

    public void Publish(PatrolDockProgress progress)
    {
        byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(progress, Json) + "\n");
        lock (_gate)
        {
            // Una descarga en curso se le muestra también a un aviso que se conecte tarde.
            _last = progress.Stage is "done" or "removed" or "error" or "queued" ? null : progress;
            foreach (var client in _clients.ToList())
            {
                try { client.Write(line); client.Flush(); }
                catch (IOException) { _clients.Remove(client); client.Dispose(); }
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = Create();
                await pipe.WaitForConnectionAsync(ct);
                lock (_gate)
                {
                    _clients.Add(pipe);
                    if (_last is not null)
                    {
                        pipe.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_last, Json) + "\n"));
                        pipe.Flush();
                    }
                }
                pipe = null;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Canal del aviso en pantalla: {Message}", ex.Message);
                await Task.Delay(2000, ct);
            }
            finally { pipe?.Dispose(); }
        }
        lock (_gate) foreach (var c in _clients) c.Dispose();
    }

    private static NamedPipeServerStream Create()
    {
        if (!OperatingSystem.IsWindows())
            return new NamedPipeServerStream(PatrolDockProgress.PipeName, PipeDirection.Out,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.Read | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PatrolDockProgress.PipeName, PipeDirection.Out,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            0, 0, security);
    }
}
