using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Receptor de paneles de cerco eléctrico. Escucha en un PUERTO PROPIO (aparte de
/// la API principal) conexiones WebSocket entrantes de los ESP8266 y corre la
/// autenticación mutua (challenge-response con el PSK), verifica el HMAC de los
/// eventos, persiste estado/eventos y los empuja a los clientes por SignalR.
///
/// Transporte: ws:// directo (on-prem). Para nube (wss://) poner un proxy TLS
/// delante de este puerto; el HMAC de aplicación protege integridad/autenticidad
/// aunque el transporte sea ws://. Config en <c>Cerco:Receiver</c> (Enabled, Port).
/// </summary>
public sealed class CercoReceiverService(
    IServiceScopeFactory scopeFactory,
    CercoConnectionManager connections,
    CredentialProtector protector,
    IHubContext<VmsHub> hub,
    IConfiguration config,
    ILogger<CercoReceiverService> logger) : BackgroundService
{
    private const int MaxFrameBytes = 8 * 1024;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ClockWindow = TimeSpan.FromSeconds(120);

    public int Port { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("Cerco:Receiver:Enabled", true))
        {
            logger.LogInformation("Receptor de cerco deshabilitado (Cerco:Receiver:Enabled=false).");
            return;
        }
        Port = config.GetValue("Cerco:Receiver:Port", 5092);
        var listener = new TcpListener(IPAddress.Any, Port);
        listener.Start();
        logger.LogInformation("Receptor de cerco escuchando WebSocket en :{Port}", Port);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch (OperationCanceledException) { break; }
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
        }
        finally { listener.Stop(); }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _client = client;
        WebSocket? ws = null;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            if (!await WsUpgrade.TryHandshakeAsync(stream, ct)) return;
            ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null,
                keepAliveInterval: TimeSpan.FromSeconds(15));
            await RunSessionAsync(ws, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // desconexión normal / cliente inválido
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error atendiendo panel de cerco");
        }
        finally
        {
            if (ws is not null) { try { ws.Dispose(); } catch { } }
        }
    }

    private async Task RunSessionAsync(WebSocket ws, CancellationToken ct)
    {
        // ---- 1) hello ----
        var hello = await ReceiveJsonAsync(ws, HandshakeTimeout, ct);
        if (hello is null || (string?)hello["t"] != "hello") return;
        string deviceId = (string?)hello["id"] ?? "";
        byte[] nonceC = FromB64((string?)hello["nonce_c"]);
        if (deviceId.Length == 0 || nonceC.Length != 16) return;

        // ---- panel + PSK ----
        int panelId;
        byte[] psk;
        long lastEventSeq;
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var panel = await db.CercoPanels.FirstOrDefaultAsync(p => p.DeviceId == deviceId, ct);
            if (panel is null || !panel.Enabled)
            {
                logger.LogWarning("Panel de cerco '{Id}' desconocido o deshabilitado — rechazado", deviceId);
                return;
            }
            panelId = panel.Id;
            lastEventSeq = panel.LastEventSeq;
            try { psk = protector.UnprotectBytes(panel.PskCiphertext); }
            catch { logger.LogError("No se pudo descifrar el PSK del panel '{Id}'", deviceId); return; }
        }

        // ---- 2) challenge ----
        byte[] nonceS = CercoCrypto.RandomBytes(16);
        byte[] proofS = CercoCrypto.Proof(psk, 'S', deviceId, nonceC, nonceS);
        int reportInterval = config.GetValue("Cerco:Receiver:ReportIntervalSeconds", 15);
        await SendJsonAsync(ws, new JsonObject
        {
            ["t"] = "challenge",
            ["nonce_s"] = Convert.ToBase64String(nonceS),
            ["proof_s"] = Convert.ToBase64String(proofS),
            ["server_ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["report_interval"] = reportInterval,
        }, ct);

        // ---- 3) auth ----
        var auth = await ReceiveJsonAsync(ws, HandshakeTimeout, ct);
        if (auth is null || (string?)auth["t"] != "auth") return;
        byte[] expectC = CercoCrypto.Proof(psk, 'C', deviceId, nonceS, nonceC);
        byte[] gotC = FromB64((string?)auth["proof_c"]);
        if (gotC.Length != expectC.Length || !CryptographicOperations.FixedTimeEquals(expectC, gotC))
        {
            logger.LogWarning("Panel de cerco '{Id}': proof_c inválido — cerrando", deviceId);
            await SendJsonAsync(ws, new JsonObject { ["t"] = "welcome", ["ok"] = false }, ct);
            return;
        }

        byte[] sk = CercoCrypto.SessionKey(psk, nonceC, nonceS);
        CryptographicOperations.ZeroMemory(psk);
        var conn = new CercoConnection(panelId, deviceId, ws, sk);
        connections.Register(conn);
        logger.LogInformation("Panel de cerco '{Id}' autenticado", deviceId);

        try
        {
            await MarkOnlineAsync(panelId, hello, ct);
            // last_evt_seq: el panel no persiste su contador de eventos en cada envío, así
            // que tras un reinicio continúa desde aquí (si no, el anti-replay descartaría
            // sus eventos). Firmado con sk para que no se pueda alterar en tránsito.
            await SendJsonAsync(ws, new JsonObject
            {
                ["t"] = "welcome", ["ok"] = true,
                ["last_evt_seq"] = lastEventSeq,
                ["mac"] = CercoCrypto.HmacB64(sk, $"welcome|{lastEventSeq}"),
            }, ct);
            // TrueCentral es la fuente de verdad de la configuración: reenviarla en
            // cada conexión (el panel solo escribe su flash si cambió algo).
            await PushConfigAsync(panelId, deviceId, ct);

            // ---- 4) bucle de estado / eventos ----
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var msg = await ReceiveJsonAsync(ws, Timeout.InfiniteTimeSpan, ct);
                if (msg is null) break;
                switch ((string?)msg["t"])
                {
                    case "state": await OnStateAsync(panelId, msg, ct); break;
                    case "rf_list": await OnRfListAsync(panelId, msg, ct); break;
                    case "event": lastEventSeq = await OnEventAsync(panelId, sk, msg, lastEventSeq, ct); break;
                    case "ping":  await conn.SendRawAsync("{\"t\":\"pong\"}", ct); break;
                    case "ack":   break;
                }
            }
        }
        finally
        {
            connections.Unregister(conn);
            await MarkOfflineAsync(panelId, ct);
        }
    }

    // ---- persistencia + push ----

    private async Task MarkOnlineAsync(int panelId, JsonObject hello, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var panel = await db.CercoPanels.Include(p => p.Zones).FirstAsync(p => p.Id == panelId, ct);
        panel.Status = CercoPanelStatus.Online;
        panel.LastSeenAt = DateTime.UtcNow;
        panel.Firmware = (string?)hello["fw"] ?? panel.Firmware;
        panel.Mac = (string?)hello["mac"] ?? panel.Mac;
        panel.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await hub.Clients.All.SendAsync(VmsHubContract.CercoPanelStateChanged, CercoMapper.ToDto(panel, true), ct);
    }

    private async Task MarkOfflineAsync(int panelId, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var panel = await db.CercoPanels.Include(p => p.Zones).FirstOrDefaultAsync(p => p.Id == panelId, CancellationToken.None);
            if (panel is null) return;
            panel.Status = CercoPanelStatus.Offline;
            panel.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await hub.Clients.All.SendAsync(VmsHubContract.CercoPanelStateChanged, CercoMapper.ToDto(panel, false), CancellationToken.None);
        }
        catch (Exception ex) { logger.LogDebug(ex, "No se pudo marcar offline el panel {Id}", panelId); }
    }

    private async Task OnStateAsync(int panelId, JsonObject m, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var panel = await db.CercoPanels.Include(p => p.Zones).FirstAsync(p => p.Id == panelId, ct);
        panel.Armed = (bool?)m["armed"] ?? panel.Armed;
        panel.Siren = (bool?)m["siren"] ?? panel.Siren;
        panel.HvOk = (bool?)m["hv_ok"] ?? panel.HvOk;
        panel.FenceOk = (bool?)m["fence_ok"] ?? panel.FenceOk;
        panel.ArcFault = (bool?)m["arc_fault"] ?? panel.ArcFault;
        panel.Voltage = (int?)m["voltage"] ?? panel.Voltage;
        panel.GroundMs = (int?)m["ground_ms"] ?? panel.GroundMs;
        panel.Equipo = (int?)m["equipo"] ?? panel.Equipo;
        panel.Rssi = (int?)m["rssi"] ?? panel.Rssi;
        panel.Arming = (bool?)m["arming"] ?? false;
        panel.KeyOn = (bool?)m["key_on"] ?? false;
        panel.RfLearning = (bool?)m["rf_learning"] ?? false;
        panel.Zone0Adc = (int?)m["z0_adc"] ?? panel.Zone0Adc;
        panel.ConfigSynced = m["cfg"] is JsonObject cfg && CercoMapper.Matches(panel, cfg);
        int power = (int?)m["power"] ?? 0;
        panel.PowerSource = Enum.IsDefined((CercoPowerSource)power) ? (CercoPowerSource)power : CercoPowerSource.Unknown;
        panel.PowerDropPermille = (int?)m["pwr_drop"];
        panel.ReturnUs = (int?)m["ret_us"] is int r && r > 0 ? r : null;
        panel.LastSeenAt = DateTime.UtcNow;
        panel.LastStateAt = DateTime.UtcNow;
        if (m["zones"] is JsonArray zs)
        {
            foreach (var zn in zs.OfType<JsonObject>())
            {
                int num = (int?)zn["z"] ?? -1;
                if (num < 0) continue;
                var zone = panel.Zones.FirstOrDefault(z => z.Number == num)
                           ?? AddZone(panel, num);
                zone.InAlarm = (bool?)zn["alarm"] ?? zone.InAlarm;
                zone.UpdatedAt = DateTime.UtcNow;
            }
        }
        panel.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await hub.Clients.All.SendAsync(VmsHubContract.CercoPanelStateChanged, CercoMapper.ToDto(panel, true), ct);
    }

    // Nombres dados por el operador a botones recién aprendidos ((panelId, slot) → nombre).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), string> _learnedNames = new();

    private async Task PushConfigAsync(int panelId, string deviceId, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var panel = await db.CercoPanels.AsNoTracking().FirstAsync(p => p.Id == panelId, ct);
            await connections.SendPanelCommandAsync(db, panelId, deviceId, "config", CercoMapper.ConfigArgs(panel), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "No se pudo enviar la configuración al panel de cerco {Id}", panelId);
        }
    }

    // Lista de botones RF que reporta el panel (fuente de verdad de los códigos).
    private async Task OnRfListAsync(int panelId, JsonObject m, CancellationToken ct)
    {
        if (m["items"] is not JsonArray items) return;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var remotes = await db.CercoRemotes.Where(r => r.CercoPanelId == panelId).ToListAsync(ct);
        var seen = new HashSet<int>();
        foreach (var it in items.OfType<JsonObject>())
        {
            int slot = (int?)it["s"] ?? -1;
            int action = (int?)it["a"] ?? 0;
            string code = ((string?)it["c"] ?? "").Trim();
            if (slot is < 0 or > 31 || !Enum.IsDefined((CercoRfAction)action) || action == 0 || code.Length > 16) continue;
            seen.Add(slot);
            _learnedNames.TryRemove((panelId, slot), out var learnedName);
            var r = remotes.FirstOrDefault(x => x.Slot == slot);
            if (r is null)
            {
                r = new CercoRemote { CercoPanelId = panelId, Slot = slot, Name = learnedName ?? $"Botón {slot + 1}" };
                db.CercoRemotes.Add(r);
            }
            else if (r.Code != code)
            {
                r.Name = learnedName ?? $"Botón {slot + 1}";   // otro control ocupó ese slot
                r.CreatedAt = DateTime.UtcNow;
            }
            else if (learnedName is not null)
            {
                r.Name = learnedName;                          // mismo botón re-programado
            }
            r.Code = code;
            r.Bits = (int?)it["b"] ?? 24;
            r.Action = (CercoRfAction)action;
        }
        db.CercoRemotes.RemoveRange(remotes.Where(r => !seen.Contains(r.Slot)));
        await db.SaveChangesAsync(ct);
        var list = await db.CercoRemotes.AsNoTracking().Where(r => r.CercoPanelId == panelId)
            .OrderBy(r => r.Slot).ToListAsync(ct);
        await hub.Clients.All.SendAsync(VmsHubContract.CercoRemotesChanged,
            new { panelId, remotes = list.Select(CercoMapper.ToDto).ToList() }, ct);
    }

    private static CercoZone AddZone(CercoPanel panel, int num)
    {
        var z = new CercoZone { CercoPanelId = panel.Id, Number = num, Name = $"Zona {num}" };
        panel.Zones.Add(z);
        return z;
    }

    private async Task<long> OnEventAsync(int panelId, byte[] sk, JsonObject m, long lastSeq, CancellationToken ct)
    {
        long seq = (long?)m["seq"] ?? 0;
        long ts = (long?)m["ts"] ?? 0;
        string ev = (string?)m["ev"] ?? "";
        int? zone = (int?)m["zone"];
        long detail = (long?)m["detail"] ?? 0;
        // HMAC(sk, "seq|ts|ev|zone") — idéntico al firmware
        string canon = $"{seq}|{ts}|{ev}|{zone ?? 0}";
        bool verified = CercoCrypto.VerifyB64(sk, canon, (string?)m["mac"]);
        bool fresh = seq > lastSeq;
        bool inWindow = ts == 0 || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) <= ClockWindow.TotalSeconds;

        if (!verified || !fresh || !inWindow)
        {
            logger.LogWarning("Evento de cerco descartado (panel {Id}): verified={V} fresh={F} window={W}",
                panelId, verified, fresh, inWindow);
            return lastSeq;
        }

        var (kind, sev) = CercoMapper.MapEvent(ev);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var panel = await db.CercoPanels.Include(p => p.Zones).FirstAsync(p => p.Id == panelId, ct);
        panel.LastEventSeq = seq;
        panel.LastSeenAt = DateTime.UtcNow;
        // reflejar el efecto del evento en el estado
        switch (kind)
        {
            case CercoEventKind.Armed: panel.Armed = true; break;
            case CercoEventKind.Disarmed: panel.Armed = false; break;
            case CercoEventKind.ArmFailed: panel.Armed = false; panel.Arming = false; if (detail == 0) panel.FenceOk = false; break;
            case CercoEventKind.RfLearned:
                // El nombre que dio el operador pasa al slot que el panel asignó; la
                // lista (rf_list) llega después y crea/actualiza la fila con él.
                panel.RfLearning = false;
                if (zone is int slot && connections.PendingRfName.TryRemove(panelId, out var nm))
                    _learnedNames[(panelId, slot)] = nm;
                break;
            case CercoEventKind.PowerLost: panel.PowerSource = CercoPowerSource.Battery; break;
            case CercoEventKind.PowerRestored: panel.PowerSource = CercoPowerSource.Mains; break;
            case CercoEventKind.RfLearnTimeout:
                panel.RfLearning = false;
                connections.PendingRfName.TryRemove(panelId, out _);
                break;
            case CercoEventKind.SirenOn: panel.Siren = true; break;
            case CercoEventKind.SirenOff: panel.Siren = false; break;
            case CercoEventKind.FenceCut: panel.FenceOk = false; break;
            case CercoEventKind.HvFault: panel.HvOk = false; break;
        }
        string? zoneName = zone is int zz ? panel.Zones.FirstOrDefault(z => z.Number == zz)?.Name : null;
        var entity = new CercoEvent
        {
            CercoPanelId = panelId,
            PanelName = panel.Name,
            Timestamp = ts > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime : DateTime.UtcNow,
            ReceivedAt = DateTime.UtcNow,
            Kind = kind,
            Severity = sev,
            Description = CercoMapper.Describe(kind, zone, detail),
            ZoneNumber = zone,
            ZoneName = zoneName,
            Verified = verified,
            RawJson = m.ToJsonString(),
        };
        db.CercoEvents.Add(entity);
        panel.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await hub.Clients.All.SendAsync(VmsHubContract.CercoEventReceived, CercoMapper.ToDto(entity), ct);
        await hub.Clients.All.SendAsync(VmsHubContract.CercoPanelStateChanged, CercoMapper.ToDto(panel, true), ct);
        return seq;
    }

    // ---- utilidades WS ----

    private static byte[] FromB64(string? s)
    {
        if (string.IsNullOrEmpty(s)) return [];
        try { return Convert.FromBase64String(s); } catch { return []; }
    }

    private static async Task SendJsonAsync(WebSocket ws, JsonObject o, CancellationToken ct) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(o.ToJsonString()), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonObject?> ReceiveJsonAsync(WebSocket ws, TimeSpan timeout, CancellationToken ct)
    {
        using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != Timeout.InfiniteTimeSpan) to.CancelAfter(timeout);
        var buf = new byte[4096];
        using var ms = new MemoryStream();
        WebSocketReceiveResult res;
        do
        {
            res = await ws.ReceiveAsync(buf, to.Token);
            if (res.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, res.Count);
            if (ms.Length > MaxFrameBytes) return null;
        } while (!res.EndOfMessage);
        if (res.MessageType != WebSocketMessageType.Text || ms.Length == 0) return null;
        try { return JsonNode.Parse(ms.ToArray()) as JsonObject; }
        catch { return null; }
    }
}

/// <summary>Handshake HTTP→WebSocket (RFC 6455) sobre un stream TCP crudo.</summary>
internal static class WsUpgrade
{
    private const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public static async Task<bool> TryHandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        string? request = await ReadHeadersAsync(stream, ct);
        if (request is null) return false;
        string? key = null;
        bool upgrade = false;
        foreach (var line in request.Split("\r\n"))
        {
            int c = line.IndexOf(':');
            if (c <= 0) continue;
            string name = line[..c].Trim();
            string value = line[(c + 1)..].Trim();
            if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)) key = value;
            else if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) &&
                     value.Contains("websocket", StringComparison.OrdinalIgnoreCase)) upgrade = true;
        }
        if (!upgrade || key is null) return false;
        string accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key + Guid)));
        string resp = "HTTP/1.1 101 Switching Protocols\r\n" +
                      "Upgrade: websocket\r\n" +
                      "Connection: Upgrade\r\n" +
                      $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        byte[] respBytes = Encoding.ASCII.GetBytes(resp);
        await stream.WriteAsync(respBytes, ct);
        return true;
    }

    private static async Task<string?> ReadHeadersAsync(NetworkStream stream, CancellationToken ct)
    {
        using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
        to.CancelAfter(TimeSpan.FromSeconds(10));
        var buf = new byte[2048];
        using var ms = new MemoryStream();
        while (ms.Length < 8192)
        {
            int n = await stream.ReadAsync(buf, to.Token);
            if (n <= 0) return null;
            ms.Write(buf, 0, n);
            string s = Encoding.ASCII.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            int end = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0) return s[..end];
        }
        return null;
    }
}
