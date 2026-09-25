using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Una conexión WebSocket viva y autenticada con un panel de cerco. Guarda la
/// clave de sesión (sk) para firmar los comandos salientes. El envío está
/// serializado (un panel = un socket).
/// </summary>
public sealed class CercoConnection(int panelId, string deviceId, WebSocket socket, byte[] sessionKey)
{
    private readonly SemaphoreSlim _send = new(1, 1);

    public int PanelId { get; } = panelId;
    public string DeviceId { get; } = deviceId;
    public WebSocket Socket { get; } = socket;
    public byte[] Sk { get; } = sessionKey;
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;

    public async Task<bool> SendRawAsync(string json, CancellationToken ct)
    {
        if (Socket.State != WebSocketState.Open) return false;
        await _send.WaitAsync(ct);
        try
        {
            await Socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
        finally { _send.Release(); }
    }
}

/// <summary>
/// Registro en memoria de los paneles de cerco conectados (device_id → conexión).
/// Lo consultan la API (para saber si un panel está en línea y enviarle comandos)
/// y el receptor (para registrar/soltar conexiones).
/// </summary>
public sealed class CercoConnectionManager
{
    private readonly ConcurrentDictionary<string, CercoConnection> _byDevice =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _cmdLocks = new();

    /// <summary>Nombre pendiente para el próximo botón RF que aprenda cada panel (panelId → nombre).</summary>
    public ConcurrentDictionary<int, string> PendingRfName { get; } = new();

    /// <summary>
    /// Asigna el próximo seq (incremento atómico en la BD) y envía el comando firmado.
    /// Serializado por panel: el equipo exige seq estrictamente creciente EN ORDEN DE
    /// LLEGADA, así que dos envíos concurrentes (API + receptor) no pueden cruzarse.
    /// </summary>
    public async Task<bool> SendPanelCommandAsync(VmsDbContext db, int panelId, string deviceId,
        string cmd, JsonObject? args, CancellationToken ct)
    {
        if (!IsConnected(deviceId)) return false;
        var gate = _cmdLocks.GetOrAdd(panelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            long seq = (await db.Database.SqlQuery<long>(
                $"UPDATE \"CercoPanels\" SET \"CmdSeq\" = \"CmdSeq\" + 1 WHERE \"Id\" = {panelId} RETURNING \"CmdSeq\" AS \"Value\"")
                .ToListAsync(ct)).Single();
            return await SendCommandAsync(deviceId, cmd, args, seq, ct);
        }
        finally { gate.Release(); }
    }

    public bool IsConnected(string deviceId) => _byDevice.ContainsKey(deviceId);

    public CercoConnection? Get(string deviceId) =>
        _byDevice.TryGetValue(deviceId, out var c) ? c : null;

    public void Register(CercoConnection c) => _byDevice[c.DeviceId] = c;

    /// <summary>Suelta la conexión solo si es exactamente la que se registró (evita soltar una reconexión).</summary>
    public void Unregister(CercoConnection c) =>
        ((ICollection<KeyValuePair<string, CercoConnection>>)_byDevice)
            .Remove(new KeyValuePair<string, CercoConnection>(c.DeviceId, c));

    /// <summary>
    /// Firma y envía un comando al panel. <paramref name="seq"/> lo asigna el
    /// llamador (persistido en la BD como contador estrictamente creciente) para
    /// el anti-replay del equipo. Devuelve false si el panel no está conectado.
    /// canon = "cmd_id|seq|ts|cmd|argsCanon" (idéntico al firmware).
    /// </summary>
    public async Task<bool> SendCommandAsync(string deviceId, string cmd, JsonObject? args, long seq, CancellationToken ct)
    {
        var c = Get(deviceId);
        if (c is null) return false;
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int cmdId = Random.Shared.Next(1, int.MaxValue);
        string ac = CercoCrypto.CanonArgs(args);
        string canon = $"{cmdId}|{seq}|{ts}|{cmd}|{ac}";
        string mac = CercoCrypto.HmacB64(c.Sk, canon);
        var msg = new JsonObject
        {
            ["t"] = "cmd", ["cmd_id"] = cmdId, ["seq"] = seq, ["ts"] = ts, ["cmd"] = cmd, ["mac"] = mac,
        };
        if (args is not null) msg["args"] = args.DeepClone();
        return await c.SendRawAsync(msg.ToJsonString(), ct);
    }
}
