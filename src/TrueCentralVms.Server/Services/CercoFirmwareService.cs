using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Imagen de firmware de panel de cerco validada (ver <see cref="CercoFirmwareImage.TryParse"/>).
/// </summary>
public sealed record CercoFirmwareImage(byte[] Bytes, string Version, string Sha256Hex)
{
    /// <summary>Máximo que el ESP-07S puede ejecutar (1 MB mapeado menos el encabezado).</summary>
    public const int MaxBytes = 1_044_464;
    private static readonly byte[] Marker = Encoding.ASCII.GetBytes("CLR-CERCO-FW|");

    /// <summary>
    /// Acepta solo una imagen ESP8266 (byte mágico 0xE9) del firmware del cerco: debe traer
    /// la marca "CLR-CERCO-FW|&lt;versión&gt;|" que el firmware embebe desde 1.5.0. Así un .bin
    /// de otro equipo no llega a escribirse en el panel.
    /// </summary>
    public static CercoFirmwareImage? TryParse(byte[] bytes, out string error)
    {
        error = "";
        if (bytes.Length < 4096 || bytes.Length > MaxBytes) { error = "El archivo no tiene el tamaño de un firmware del panel (4 KB a 1 MB)."; return null; }
        if (bytes[0] != 0xE9) { error = "El archivo no es una imagen de firmware ESP8266."; return null; }
        // Firmado por CLRobotics (formato del core: bin + firma RSA-2048 + largo u32 LE). La
        // verificación criptográfica la hace el panel con su clave pública; aquí se rechaza
        // temprano un binario sin firmar para no hacer el viaje en vano.
        uint sigLen = BitConverter.ToUInt32(bytes, bytes.Length - 4);
        if (sigLen != 256 || bytes.Length < 4096 + 256 + 4)
        { error = "El firmware no está firmado. Use el archivo .signed.bin que genera la compilación (cerco-firmware-vX.Y.Z.signed.bin)."; return null; }
        int at = bytes.AsSpan().IndexOf(Marker);
        if (at < 0) { error = "El archivo no es un firmware de panel de cerco CLRobotics 1.5.0 o superior."; return null; }
        int start = at + Marker.Length, end = Array.IndexOf(bytes, (byte)'|', start);
        if (end < 0 || end - start is < 1 or > 16) { error = "La marca de versión del firmware es inválida."; return null; }
        string version = Encoding.ASCII.GetString(bytes, start, end - start);
        return new CercoFirmwareImage(bytes, version, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}

/// <summary>
/// Servidor de descarga de firmware para OTA de paneles de cerco, en PUERTO PROPIO
/// (Cerco:Firmware:Port, 5093 por omisión) para no exponer nada nuevo en la API :5090.
/// Solo atiende "GET /fw/&lt;token&gt;": cada imagen se publica con un token aleatorio de un
/// solo uso que vence a los 10 minutos, y la orden "ota" firmada que recibe el panel trae
/// la SHA-256: el panel descarta la imagen si no coincide, así que este canal HTTP en
/// claro no necesita ser confiable.
/// </summary>
public sealed class CercoFirmwareService(IConfiguration config, ILogger<CercoFirmwareService> logger) : BackgroundService
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);
    private sealed record Staged(CercoFirmwareImage Image, string PanelName, DateTime ExpiresAt);
    private readonly ConcurrentDictionary<string, Staged> _staged = new();

    public int Port { get; private set; }
    public bool Running { get; private set; }

    /// <summary>Publica la imagen para una descarga; devuelve el token.</summary>
    public string Stage(CercoFirmwareImage image, string panelName)
    {
        foreach (var (k, v) in _staged) if (v.ExpiresAt < DateTime.UtcNow) _staged.TryRemove(k, out _);
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _staged[token] = new Staged(image, panelName, DateTime.UtcNow + TokenLifetime);
        return token;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("Cerco:Firmware:Enabled", true))
        {
            logger.LogInformation("Servidor de firmware de cerco deshabilitado (Cerco:Firmware:Enabled=false).");
            return;
        }
        Port = config.GetValue("Cerco:Firmware:Port", 5093);
        var listener = new TcpListener(IPAddress.Any, Port);
        try { listener.Start(); }
        catch (SocketException ex)
        {
            logger.LogError(ex, "No se pudo abrir el puerto {Port} del servidor de firmware de cerco", Port);
            return;
        }
        Running = true;
        logger.LogInformation("Servidor de firmware de cerco (OTA) escuchando en :{Port}", Port);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch (OperationCanceledException) { break; }
                _ = Task.Run(() => ServeAsync(client, ct), ct);
            }
        }
        finally { listener.Stop(); Running = false; }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(TimeSpan.FromSeconds(10));
            string? requestLine = await ReadRequestLineAsync(stream, to.Token);
            var parts = requestLine?.Split(' ');
            string? token = parts is { Length: >= 2 } && parts[0] == "GET" && parts[1].StartsWith("/fw/", StringComparison.Ordinal)
                ? parts[1][4..] : null;
            // Un solo uso: se retira al empezar a servirla.
            if (token is null || !_staged.TryRemove(token, out var staged) || staged.ExpiresAt < DateTime.UtcNow)
            {
                await WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct);
                logger.LogWarning("Firmware de cerco: pedido rechazado desde {Remote} ({Req})", remote, requestLine ?? "vacío");
                return;
            }
            var img = staged.Image;
            await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n" +
                $"Content-Length: {img.Bytes.Length}\r\nConnection: close\r\n\r\n", ct);
            using var sendTo = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendTo.CancelAfter(TimeSpan.FromMinutes(3));
            await stream.WriteAsync(img.Bytes, sendTo.Token);
            await stream.FlushAsync(sendTo.Token);
            logger.LogInformation("Firmware de cerco {Version} entregado a '{Panel}' ({Remote}, {Bytes} bytes)",
                img.Version, staged.PanelName, remote, img.Bytes.Length);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            logger.LogWarning("Firmware de cerco: descarga interrumpida ({Remote}): {Msg}", remote, ex.Message);
        }
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        // Lee hasta el fin de los encabezados (máx. 4 KB) y devuelve la primera línea.
        var buf = new byte[4096];
        int n = 0;
        while (n < buf.Length)
        {
            int r = await stream.ReadAsync(buf.AsMemory(n), ct);
            if (r == 0) break;
            n += r;
            if (buf.AsSpan(0, n).IndexOf("\r\n\r\n"u8) >= 0) break;
        }
        if (n == 0) return null;
        string head = Encoding.ASCII.GetString(buf, 0, n);
        int eol = head.IndexOf("\r\n", StringComparison.Ordinal);
        return eol < 0 ? null : head[..eol];
    }

    private static Task WriteAsync(NetworkStream s, string text, CancellationToken ct) =>
        s.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();
}
