using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Centro receptor de alarmas (ARC) del VMS: escucha por TCP tramas SIA
/// DC-09 (ADM-CID / SIA-DCS / NULL) que los paneles mandan como "Alarm
/// Receiving Center". Es la vía por la que un panel reporta TODO (alarmas,
/// armados, anulaciones, fallas, tests periódicos) con confirmación y
/// reintentos, así que no depende de que el alertStream del equipo funcione
/// ni de que quede un httpHost libre.
///
/// Reglas:
///  - Se acepta solo lo que viene desde la dirección de un panel habilitado
///    (misma política que la notificación HTTP); el resto recibe NAK y queda
///    en la bitácora (acelerado para no inundarla).
///  - Toda trama válida se confirma con ACK antes de procesarla: el panel
///    no debe reintentar por lentitud del VMS.
///  - El contenido no es la verdad: el evento entra al historial y el estado
///    real se relee de inmediato (<see cref="AlarmPanelService.NotifyPushed"/>).
///  - Tramas cifradas (token con asterisco) se contestan DUH: hay que
///    configurar el canal sin cifrado.
/// Configuración en <c>Alarms:Receiver</c> (Enabled, Port, ZoneNumbersStartAt).
/// En el panel Hikvision: Alarm Receiving Center → Tcp/IP, protocolo ADM-CID,
/// dirección del servidor, este puerto, cuenta cualquiera (3-16 dígitos).
/// </summary>
public sealed class AlarmReceiverService(
    IServiceScopeFactory scopeFactory,
    AlarmPanelService alarms,
    AuditService audit,
    IConfiguration config,
    ILogger<AlarmReceiverService> logger) : BackgroundService
{
    private const int MaxFrameBytes = 4096;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PanelCacheTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RejectAuditWindow = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (AlarmPanel? Panel, DateTime At)> _panelsByIp = new();

    public int Port { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("Alarms:Receiver:Enabled", true))
        {
            logger.LogInformation("Receptor SIA DC-09 deshabilitado (Alarms:Receiver:Enabled=false).");
            return;
        }
        Port = config.GetValue("Alarms:Receiver:Port", 5091);
        int zoneStart = config.GetValue("Alarms:Receiver:ZoneNumbersStartAt", 1);

        var listener = new TcpListener(IPAddress.Any, Port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            logger.LogError("No se pudo abrir el receptor SIA DC-09 en el puerto {Port}: {Error}", Port, ex.Message);
            await audit.LogSystemAsync("alarms", "arc-started", detail: $"No se pudo abrir el receptor de alarmas (SIA DC-09) en el puerto TCP {Port}: {ex.Message}",
                success: false);
            return;
        }
        logger.LogInformation("Receptor SIA DC-09 (ADM-CID / SIA-DCS) escuchando en el puerto TCP {Port}.", Port);
        await audit.LogSystemAsync("alarms", "arc-started", detail: $"Receptor de alarmas (SIA DC-09, ADM-CID / SIA-DCS) escuchando en el puerto TCP {Port}.");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Fallo aceptando una conexión en el receptor SIA DC-09.");
                    continue;
                }
                _ = Task.Run(() => HandleAsync(client, zoneStart, ct), ct);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleAsync(TcpClient client, int zoneStart, CancellationToken ct)
    {
        string ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address is { } addr
            ? (addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4() : addr).ToString()
            : "?";
        using (client)
        {
            client.NoDelay = true;
            NetworkStream stream;
            try { stream = client.GetStream(); }
            catch (Exception) { return; }

            var buffer = new byte[1024];
            var pending = new List<byte>(256);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(IdleTimeout);
                    int read;
                    try { read = await stream.ReadAsync(buffer, idle.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        logger.LogDebug("Conexión DC-09 desde {Ip} cerrada por inactividad.", ip);
                        return;
                    }
                    if (read <= 0) return;

                    for (int i = 0; i < read; i++)
                    {
                        byte b = buffer[i];
                        if (b == (byte)'\r')
                        {
                            if (pending.Count > 0)
                            {
                                string frame = Encoding.ASCII.GetString(pending.ToArray());
                                pending.Clear();
                                string reply = await ProcessAsync(ip, frame, zoneStart, ct);
                                await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), ct);
                            }
                            continue;
                        }
                        pending.Add(b);
                        if (pending.Count > MaxFrameBytes)
                        {
                            logger.LogWarning("Trama DC-09 desde {Ip} demasiado larga: se cierra la conexión.", ip);
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* cierre del servidor */ }
            catch (IOException) { /* el panel cortó */ }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Conexión DC-09 desde {Ip} terminada con error.", ip);
            }
        }
    }

    /// <summary>Traduce una trama y devuelve la respuesta que hay que escribirle al panel.</summary>
    private async Task<string> ProcessAsync(string ip, string raw, int zoneStart, CancellationToken ct)
    {
        if (!SiaDc09.TryParse(raw, out var frame, out string? error) || frame is null)
        {
            logger.LogWarning("Trama DC-09 inválida desde {Ip}: {Error}. Contenido: {Raw}", ip, error, Printable(raw));
            if (audit.ShouldLog($"arc-rejected:{ip}", RejectAuditWindow))
                await audit.LogSystemAsync("alarms", "arc-rejected", detail: $"Trama SIA DC-09 inválida desde {ip}: {error}.",
                    success: false, clientIp: ip, origin: "panel", data: new { raw = Printable(raw) });
            return SiaDc09.BuildNak();
        }

        var panel = await ResolvePanelAsync(ip, ct);
        if (panel is null)
        {
            logger.LogWarning("Trama DC-09 desde {Ip} (cuenta {Account}) rechazada: no hay panel habilitado con esa dirección.", ip, frame.Account);
            if (audit.ShouldLog($"arc-rejected:{ip}", RejectAuditWindow))
                await audit.LogSystemAsync("alarms", "arc-rejected",
                    detail: $"Reporte SIA DC-09 desde {ip} (cuenta '{frame.Account}', {frame.Token}) rechazado: no hay panel de alarma habilitado con esa dirección.",
                    success: false, clientIp: ip, origin: "panel", data: new { frame.Account, frame.Token, raw = Printable(raw) });
            return SiaDc09.BuildNak();
        }

        if (frame.Encrypted)
        {
            logger.LogWarning("El panel '{Name}' ({Ip}) reporta cifrado ({Token}): no soportado, configure el canal sin cifrado.", panel.Name, ip, frame.Token);
            if (audit.ShouldLog($"arc-encrypted:{panel.Id}", RejectAuditWindow))
                await audit.LogSystemAsync("alarms", "arc-rejected",
                    targetType: "alarm-panel", targetId: panel.Id.ToString(), targetName: panel.Name,
                    detail: $"El panel '{panel.Name}' ({ip}) reporta con cifrado (*{frame.Token}), que el receptor no soporta: configure el centro receptor con ADM-CID o SIA-DCS sin asterisco.",
                    success: false, clientIp: ip, origin: "panel");
            return SiaDc09.BuildDuh(frame);
        }

        var evt = SiaDc09.ToEvent(frame, zoneStart, DateTime.UtcNow);
        if (evt is null)
        {
            // Latido (NULL) o token desconocido: el panel sigue vivo, nada que registrar.
            alarms.NotifyAlive(panel.Id);
            logger.LogDebug("Latido DC-09 ({Token}) del panel '{Name}' ({Ip}).", frame.Token, panel.Name, ip);
        }
        else
        {
            alarms.NotifyPushed(panel.Id, evt);
            logger.LogInformation("ARC: panel '{Name}' ({Ip}) reportó {Kind} — {Description} (código {Code}, área {Area}, zona {Zone}).",
                panel.Name, ip, evt.Kind, evt.Description, evt.Code, evt.AreaNumber, evt.ZoneNumber);
        }
        return SiaDc09.BuildAck(frame);
    }

    private async Task<AlarmPanel?> ResolvePanelAsync(string ip, CancellationToken ct)
    {
        if (_panelsByIp.TryGetValue(ip, out var cached) && DateTime.UtcNow - cached.At < PanelCacheTtl)
            return cached.Panel;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var panels = await db.AlarmPanels.AsNoTracking().Where(p => p.Enabled).ToListAsync(ct);
        IPAddress.TryParse(ip, out var remote);
        var panel = panels.FirstOrDefault(p => string.Equals(p.Host, ip, StringComparison.OrdinalIgnoreCase))
                    ?? panels.FirstOrDefault(p => remote is not null && IPAddress.TryParse(p.Host, out var host) && host.Equals(remote));
        _panelsByIp[ip] = (panel, DateTime.UtcNow);
        return panel;
    }

    /// <summary>Olvida la caché de direcciones (tras crear/editar/eliminar un panel).</summary>
    public void InvalidateCache() => _panelsByIp.Clear();

    private static string Printable(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
            sb.Append(c < ' ' ? $"\\x{(int)c:X2}" : c);
        return sb.Length > 512 ? sb.ToString(0, 512) + "…" : sb.ToString();
    }
}
