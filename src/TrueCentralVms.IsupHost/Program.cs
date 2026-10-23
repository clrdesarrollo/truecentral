// =============================================================================
// TrueCentralVms.IsupHost: servidor ISUP interno del VMS.
//
// Lo lanza y vigila el servidor (IsupHostManager) como hace con MediaMTX: no es
// un servicio aparte ni tiene interfaz. Abre hacia los equipos los puertos ISUP
// (registro, alarmas, video, voz, imágenes) y hacia el servidor una API HTTP
// SOLO en 127.0.0.1, protegida con un token que el servidor le pasa por
// variable de entorno. Las URL de video que lee ffmpeg (las escribe MediaMTX en
// su configuración) llevan un segundo token que solo sirve para eso.
//
// Argumentos: --api-port N --options <json> --data-dir <carpeta> [--native-dir <carpeta>] [--parent-pid N]
// Entorno:    TCVMS_ISUP_TOKEN (API completa), TCVMS_ISUP_MEDIA_TOKEN (solo /live y /playback)
// =============================================================================

using System.Globalization;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TrueCentralVms.Core.Isup;
using TrueCentralVms.IsupHost.Engine;

string Arg(string name, string fallback = "")
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

var token = Environment.GetEnvironmentVariable("TCVMS_ISUP_TOKEN") ?? "";
var mediaToken = Environment.GetEnvironmentVariable("TCVMS_ISUP_MEDIA_TOKEN") ?? "";
if (token.Length < 16 || mediaToken.Length < 16)
{
    Console.Error.WriteLine("Falta el token de la API (TCVMS_ISUP_TOKEN / TCVMS_ISUP_MEDIA_TOKEN). Este programa lo lanza el servidor del VMS.");
    return 2;
}
int apiPort = int.TryParse(Arg("--api-port", "7699"), out var ap) ? ap : 7699;
var dataDir = Arg("--data-dir", Path.Combine(AppContext.BaseDirectory, "isup-data"));
var nativeDir = Arg("--native-dir", Path.Combine(AppContext.BaseDirectory, "isup"));
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var options = File.Exists(Arg("--options")) ? JsonSerializer.Deserialize<IsupHostOptions>(File.ReadAllText(Arg("--options")), json) ?? new() : new IsupHostOptions();
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";

// Las DLL del SDK viven en su propia carpeta (isup\): se cargan de ahí por ruta completa, sin tocar la búsqueda del resto del proceso.
NativeLibrary.SetDllImportResolver(typeof(IsupEngine).Assembly, (name, _, _) =>
{
    var path = Path.Combine(nativeDir, name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll");
    return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
});

// El servidor lee esta salida como UTF-8 (sin esto la consola de Windows usa la página 850 y los acentos llegan rotos).
Console.OutputEncoding = new UTF8Encoding(false);
Directory.CreateDirectory(dataDir);
var engine = new IsupEngine();
engine.Log += line => Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}");

// Suscriptores del canal de eventos (normalmente uno: el servidor).
var subscribers = new List<Channel<IsupHostEvent>>();
engine.EventRaised += e =>
{
    lock (subscribers) foreach (var s in subscribers) s.Writer.TryWrite(e);
};

var startError = engine.Start(options, nativeDir, dataDir);
if (startError != null)
{
    Console.Error.WriteLine(startError);
    engine.Dispose();
    return 3;
}

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, apiPort));
var app = builder.Build();
app.UseWebSockets();

// Autorización: todo exige el token de la API, salvo el video, que también acepta el de medios (va en la URL de ffmpeg).
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    bool media = path.StartsWith("/live/", StringComparison.Ordinal) || path.StartsWith("/playback/", StringComparison.Ordinal);
    var given = ctx.Request.Headers["X-Isup-Token"].ToString();
    if (given.Length == 0) given = ctx.Request.Query["t"].ToString();
    bool ok = FixedEquals(given, token) || (media && FixedEquals(given, mediaToken));
    if (!ok) { ctx.Response.StatusCode = 401; return; }
    await next();
});

app.MapGet("/status", () => Results.Json(engine.GetStatus(version), json));

app.MapGet("/state/{deviceId}", (string deviceId) =>
    engine.GetDeviceState(deviceId) is { } s ? Results.Json(s, json) : Results.NotFound());

// Datos y canales por la configuración del SDK (los equipos ISUP 2.0/4.0 no tienen ISAPI).
app.MapGet("/info/{deviceId}", async (string deviceId, HttpContext ctx) =>
{
    try { return Results.Json(await Task.Run(() => engine.GetDeviceInfo(deviceId, options.CommandTimeoutMs)), json); }
    catch (IsupCommandException ex) { await WriteProblem(ctx, ex.Message, ex.Code); return Results.Empty; }
});

app.MapPut("/directory", async (HttpRequest req) =>
{
    var entries = await JsonSerializer.DeserializeAsync<List<IsupDirectoryEntry>>(req.Body, json) ?? [];
    engine.SetDirectory(entries);
    return Results.NoContent();
});

// Canal de eventos: una línea JSON por evento, con un latido cada 15 s para que el servidor note si el host se colgó.
app.MapGet("/events", async (HttpContext ctx) =>
{
    var ch = Channel.CreateBounded<IsupHostEvent>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });
    lock (subscribers) subscribers.Add(ch);
    try
    {
        ctx.Response.ContentType = "application/x-ndjson";
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        while (!ctx.RequestAborted.IsCancellationRequested)
        {
            IsupHostEvent e;
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted))
            {
                wait.CancelAfter(TimeSpan.FromSeconds(15));
                try { e = await ch.Reader.ReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested) { e = new IsupHostEvent(IsupEventKinds.Ping, "", DateTimeOffset.Now); }
            }
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(e, json) + "\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException) { }
    finally { lock (subscribers) subscribers.Remove(ch); }
});

// ISAPI por el túnel: verbo y ruta en la query, cuerpo tal cual. El resultado del SDK va en cabeceras; el cuerpo es la respuesta del equipo.
app.MapPost("/isapi/{deviceId}", async (string deviceId, HttpContext ctx) =>
{
    var method = ctx.Request.Query["method"].ToString();
    var path = ctx.Request.Query["path"].ToString();
    int timeout = int.TryParse(ctx.Request.Query["timeout"], out var t) ? t : 15000;
    if (method.Length == 0 || !path.StartsWith('/')) return Results.BadRequest();
    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
    var body = ms.Length > 0 ? ms.ToArray() : null;
    var r = await Task.Run(() => engine.Isapi(deviceId, method, path, body, timeout), ctx.RequestAborted);
    ctx.Response.Headers["X-Isup-Ok"] = r.Ok ? "1" : "0";
    ctx.Response.Headers["X-Isup-Error-Code"] = r.ErrorCode.ToString(CultureInfo.InvariantCulture);
    if (r.Error != null) ctx.Response.Headers["X-Isup-Error"] = Uri.EscapeDataString(r.Error);
    return Results.Bytes(r.Body, "application/octet-stream");
});

app.MapPost("/xml/{deviceId}", async (string deviceId, HttpContext ctx) =>
{
    var command = ctx.Request.Query["command"].ToString();
    var method = ctx.Request.Query["method"].ToString() is { Length: > 0 } m ? m : "CONTROL";
    using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
    var paramsXml = await reader.ReadToEndAsync(ctx.RequestAborted);
    var r = await Task.Run(() => engine.XmlRemoteControl(deviceId, command, paramsXml, method: method), ctx.RequestAborted);
    return r.Ok ? Results.Text(Encoding.UTF8.GetString(r.Body), "application/xml") : Problem(r.Error ?? "Error", r.ErrorCode);
});

app.MapPost("/ptz/{deviceId}/{channel:int}", (string deviceId, int channel, string command, bool start, int speed) =>
    Run(() => engine.Ptz(deviceId, channel, Enum.Parse<IsupPtzCommand>(command, ignoreCase: true), start, speed)));

app.MapPost("/preset/{deviceId}/{channel:int}", (string deviceId, int channel, string action, int index) =>
    Run(() => engine.Preset(deviceId, channel, Enum.Parse<IsupPresetAction>(action, ignoreCase: true), index)));

app.MapPost("/output/{deviceId}/{output:int}", (string deviceId, int output, bool on, int seconds) =>
    Run(() => engine.AlarmOutput(deviceId, output, on, seconds)));

app.MapPost("/wake/{deviceId}", (string deviceId) => Run(() => engine.WakeUp(deviceId)));

app.MapPost("/logout/{deviceId}", (string deviceId) => engine.ForceLogout(deviceId) ? Results.NoContent() : Results.NotFound());

// Video en vivo como MPEG-PS: lo lee ffmpeg (lanzado por MediaMTX bajo demanda) y lo publica por RTSP. Cortar la petición corta el video.
app.MapGet("/live/{deviceId}/{channel:int}/{stream:int}", async (string deviceId, int channel, int stream, HttpContext ctx) =>
{
    MediaStream media;
    try { media = await Task.Run(() => engine.StartLive(deviceId, channel, stream)); }
    catch (IsupCommandException ex) { await WriteProblem(ctx, ex.Message, ex.Code); return; }
    try { await Pump(ctx, media, pacer: null); }
    finally { engine.StopLive(media); }
});

// Archivos grabados de un canal entre dos horas locales (equipos ISUP 2.0/4.0, sin ISAPI).
app.MapGet("/files/{deviceId}/{channel:int}", async (string deviceId, int channel, string from, string to, HttpContext ctx) =>
{
    if (!DateTime.TryParseExact(from, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
        !DateTime.TryParseExact(to, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
    { ctx.Response.StatusCode = 400; return; }
    List<IsupRecordingFile> files;
    try { files = await Task.Run(() => engine.GetRecordingFiles(deviceId, channel, start, end)); }
    catch (IsupCommandException ex) { await WriteProblem(ctx, ex.Message, ex.Code); return; }
    await ctx.Response.WriteAsJsonAsync(files);
});

// Reproducción entre dos horas locales del equipo (yyyyMMddTHHmmss), dosificada a tiempo real con el reloj del propio flujo.
app.MapGet("/playback/{deviceId}/{channel:int}", async (string deviceId, int channel, string from, string to, HttpContext ctx) =>
{
    if (!DateTime.TryParseExact(from, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
        !DateTime.TryParseExact(to, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
    { ctx.Response.StatusCode = 400; return; }
    bool pace = ctx.Request.Query["pace"] != "0";
    MediaStream media;
    try { media = await Task.Run(() => engine.StartPlayback(deviceId, channel, start, end)); }
    catch (IsupCommandException ex) { await WriteProblem(ctx, ex.Message, ex.Code); return; }
    try { await Pump(ctx, media, pace ? new PsPacer() : null); }
    finally { engine.StopPlayback(media); }
});

// Voz bidireccional por WebSocket: primer mensaje de texto con la codificación del equipo; luego tramas binarias en ambos sentidos.
app.Map("/voice/{deviceId}/{channel:int}", async (string deviceId, int channel, HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    bool broadcast = ctx.Request.Query["broadcast"] == "1";
    VoiceSession session;
    try { session = await engine.OpenVoiceAsync(deviceId, channel, broadcast, ctx.RequestAborted); }
    catch (IsupCommandException ex) { await WriteProblem(ctx, ex.Message, ex.Code); return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });
    session.AudioReceived += data => outbound.Writer.TryWrite(data);
    session.Closed += () => outbound.Writer.TryComplete();
    try
    {
        var hello = JsonSerializer.SerializeToUtf8Bytes(new { codec = session.Codec, frame = session.FrameBytes }, json);
        await ws.SendAsync(hello, WebSocketMessageType.Text, true, ctx.RequestAborted);
        var sender = Task.Run(async () =>
        {
            await foreach (var data in outbound.Reader.ReadAllAsync(ctx.RequestAborted))
                await ws.SendAsync(data, WebSocketMessageType.Binary, true, ctx.RequestAborted);
        });
        var buffer = new byte[8192];
        while (ws.State == WebSocketState.Open && !session.IsClosed)
        {
            var r = await ws.ReceiveAsync(buffer, ctx.RequestAborted);
            if (r.MessageType == WebSocketMessageType.Close) break;
            if (r.MessageType == WebSocketMessageType.Binary && r.Count > 0 && !engine.SendVoice(session, buffer.AsSpan(0, r.Count))) break;
        }
        outbound.Writer.TryComplete();
        await sender.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { });
        if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }
    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
    finally { engine.CloseVoice(session); }
});

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
app.MapPost("/shutdown", () => { lifetime.StopApplication(); return Results.NoContent(); });

// Si el servidor que lanzó este proceso muere, el host se cierra (no deja los puertos ISUP tomados por un proceso huérfano).
if (int.TryParse(Arg("--parent-pid"), out var parentPid))
    _ = Task.Run(async () =>
    {
        try
        {
            using var parent = System.Diagnostics.Process.GetProcessById(parentPid);
            await parent.WaitForExitAsync();
        }
        catch (ArgumentException) { }
        Console.WriteLine("El servidor del VMS ya no está; se cierra el servicio ISUP.");
        lifetime.StopApplication();
    });

// Las fotos de los eventos quedan en disco hasta que el servidor las copia: se borran las de más de dos días.
var prune = new Timer(_ => engine.PrunePictures(2), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
lifetime.ApplicationStopping.Register(() => { prune.Dispose(); engine.Dispose(); });

Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} API interna del servicio ISUP en 127.0.0.1:{apiPort}.");
await app.RunAsync();
return 0;

// ---------------------------------------------------------------------------

static bool FixedEquals(string a, string b) =>
    a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

static IResult Run(Action action)
{
    try { action(); return Results.NoContent(); }
    catch (IsupCommandException ex) { return Problem(ex.Message, ex.Code); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
}

static IResult Problem(string message, int code) => Results.Json(new { error = message, code }, statusCode: code == -1 ? 404 : 502);

static async Task WriteProblem(HttpContext ctx, string message, int code)
{
    ctx.Response.StatusCode = code == -1 ? 404 : 502;
    await ctx.Response.WriteAsJsonAsync(new { error = message, code });
}

static async Task Pump(HttpContext ctx, MediaStream media, PsPacer? pacer)
{
    ctx.Response.ContentType = "video/MP2P";
    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    try
    {
        // El primer bloque puede tardar (el equipo tiene que conectarse al puerto de video): hasta 20 s; luego, 15 s sin datos = cortado.
        bool first = true;
        while (!ctx.RequestAborted.IsCancellationRequested)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            wait.CancelAfter(TimeSpan.FromSeconds(first ? 20 : 15));
            byte[]? chunk;
            try { chunk = await media.ReadAsync(wait.Token); }
            catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested) { break; }
            if (chunk == null) break;
            first = false;
            if (pacer != null) { int ms = pacer.WaitMs(chunk, chunk.Length); if (ms > 0) await Task.Delay(ms, ctx.RequestAborted); }
            await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException) { }
    catch (IOException) { }
}
