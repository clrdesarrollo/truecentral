using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Core.Isup;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Transporte ISAPI por ISUP: lo que <see cref="HikvisionIsapiClient"/> pediría por HTTP al equipo viaja por el túnel ISUP del
/// servidor (<see cref="IsupGateway"/>), y la respuesta vuelve como si fuera HTTP. Así los drivers que hablan ISAPI (acceso,
/// paneles, citofonía, parlantes) sirven igual para un equipo alcanzable por IP que para uno que se registra por ISUP.
///
/// Diferencias que este manejador absorbe:
/// <list type="bullet">
/// <item>El túnel no informa un código HTTP: se deduce del ResponseStatus que devuelve el equipo (statusCode 4 → 403, notSupport → 404…).</item>
/// <item>No hay autenticación por petición: la conexión ISUP ya la autenticó (ID + clave del equipo).</item>
/// <item>El túnel no lleva cabeceras, así que no admite multipart (la foto del rostro va por URL en modo ISUP).</item>
/// <item>No hay flujos largos: el <c>alertStream</c> se sintetiza con las alarmas que el equipo manda por el canal de alarmas de
/// ISUP, en el mismo formato multipart, para que los lectores de eventos existentes funcionen sin cambios.</item>
/// </list>
/// </summary>
internal sealed class IsupHttpHandler(string deviceId, string noun) : HttpMessageHandler
{
    /// <summary>Espera de la petición (la fija <see cref="HikvisionIsapiClient"/>; por omisión 15 s).</summary>
    public static readonly HttpRequestOptionsKey<TimeSpan> TimeoutOption = new("isup-timeout");

    public const string AlertStreamPath = "/ISAPI/Event/notification/alertStream";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var gateway = IsupGateway.Require();
        string path = request.RequestUri!.PathAndQuery;
        if (request.Method == HttpMethod.Get && path.StartsWith(AlertStreamPath, StringComparison.OrdinalIgnoreCase))
        {
            if (!gateway.IsOnline(deviceId)) throw new DriverException(NotConnected());
            return AlertStream(gateway);
        }

        byte[]? body = null;
        if (request.Content is not null)
        {
            if (request.Content.Headers.ContentType?.MediaType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true)
                throw new DriverException($"El {noun} está conectado por ISUP y esta operación necesita subir un archivo al equipo, " +
                                          "algo que el túnel ISUP no admite.");
            body = await request.Content.ReadAsByteArrayAsync(ct);
        }
        var timeout = request.Options.TryGetValue(TimeoutOption, out var t) ? t : TimeSpan.FromSeconds(15);
        var r = await gateway.IsapiAsync(deviceId, request.Method.Method, path, body, timeout, ct);
        if (!r.Ok)
        {
            if (r.ErrorCode == -1) throw new DriverException(NotConnected());
            throw new DriverException(r.Error ?? $"El {noun} no respondió por ISUP (error {r.ErrorCode}).");
        }
        var response = new HttpResponseMessage(StatusOf(r.Body)) { RequestMessage = request, Content = new ByteArrayContent(r.Body) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeOf(r.Body));
        return response;
    }

    private string NotConnected() =>
        $"El {noun} no está conectado al servidor por ISUP (ID {deviceId}). Revise en el equipo la plataforma ISUP " +
        "(dirección del servidor, puerto, ID y clave) y que tenga salida a internet o a la red del servidor.";

    // ------------------------------------------------------------------
    // Código HTTP a partir del ResponseStatus del equipo
    // ------------------------------------------------------------------

    private static readonly Regex XmlStatus = new(@"<statusCode>\s*(\d+)\s*</statusCode>", RegexOptions.Compiled);
    private static readonly Regex JsonStatus = new(@"""statusCode""\s*:\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex SubStatus = new(@"<subStatusCode>\s*([^<\s]+)\s*</subStatusCode>|""subStatusCode""\s*:\s*""([^""]*)""", RegexOptions.Compiled);

    /// <summary>
    /// Por HTTP, el equipo acompaña cada ResponseStatus con su código: 1 OK → 200, 2 ocupado → 503, 3 error del equipo → 500,
    /// 4 operación inválida → 403 (404 si es «notSupport», «invalidID» o «invalidURL»), 5/6 XML/JSON inválido → 400, 7 reinicio requerido → 200. Las respuestas
    /// que no son un ResponseStatus (datos) son un 200.
    /// </summary>
    internal static HttpStatusCode StatusOf(byte[] body)
    {
        if (body.Length == 0 || body.Length > 64 * 1024 || (body[0] != (byte)'<' && body[0] != (byte)'{' && body[0] != 0xEF)) return HttpStatusCode.OK;
        string text = Encoding.UTF8.GetString(body);
        if (!text.Contains("ResponseStatus", StringComparison.Ordinal) && !text.Contains("\"statusCode\"", StringComparison.Ordinal)) return HttpStatusCode.OK;
        var m = XmlStatus.Match(text);
        if (!m.Success) m = JsonStatus.Match(text);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out int code)) return HttpStatusCode.OK;
        var sub = SubStatus.Match(text);
        string subCode = sub.Success ? (sub.Groups[1].Success ? sub.Groups[1].Value : sub.Groups[2].Value) : "";
        return code switch
        {
            0 or 1 or 7 => HttpStatusCode.OK,
            2 => HttpStatusCode.ServiceUnavailable,
            3 => HttpStatusCode.InternalServerError,
            // Recurso inexistente o no soportado: por HTTP es un 404, que los drivers leen como «el equipo no tiene esta función».
            4 when subCode.Equals("notSupport", StringComparison.OrdinalIgnoreCase) || subCode.Equals("invalidID", StringComparison.OrdinalIgnoreCase)
                   || subCode.Equals("invalidURL", StringComparison.OrdinalIgnoreCase) => HttpStatusCode.NotFound,
            4 => HttpStatusCode.Forbidden,
            5 or 6 => HttpStatusCode.BadRequest,
            _ => HttpStatusCode.BadRequest,
        };
    }

    private static string MediaTypeOf(byte[] body)
    {
        if (body.Length >= 3 && body[0] == 0xFF && body[1] == 0xD8 && body[2] == 0xFF) return "image/jpeg";
        int i = body.Length >= 3 && body[0] == 0xEF ? 3 : 0;
        while (i < body.Length && body[i] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t') i++;
        if (i < body.Length && body[i] == (byte)'{') return "application/json";
        if (i < body.Length && body[i] == (byte)'<') return "application/xml";
        return "application/octet-stream";
    }

    // ------------------------------------------------------------------
    // alertStream sintetizado con las alarmas ISUP del equipo
    // ------------------------------------------------------------------

    private const string Boundary = "isupboundary";

    private HttpResponseMessage AlertStream(IIsupGateway gateway)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new AlertStreamContent(gateway, deviceId) };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/mixed; boundary={Boundary}");
        return response;
    }

    /// <summary>
    /// Cuerpo «infinito» que escribe una parte por cada alarma del equipo y un latido cada 20 s. Termina cuando el equipo se
    /// desconecta del ISUP (el lector lo da por caído y vuelve a intentar, igual que con un alertStream cortado).
    /// </summary>
    private sealed class AlertStreamContent(IIsupGateway gateway, string deviceId) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = -1; return false; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) =>
            PumpAsync(async (bytes, token) => { await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); return true; }, ct);

        // Los lectores (paneles y terminales de acceso) piden el cuerpo con ReadAsStreamAsync. Lo que hace HttpContent por omisión es
        // cargarlo ENTERO en memoria antes de devolverlo, y este no termina mientras el equipo esté en línea: quien abría la escucha
        // quedaba esperando para siempre (y con él el sondeo de todos los paneles). Se entrega una tubería que se va llenando, y la
        // escritura se detiene cuando el lector la cierra.
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(OpenPipe());

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult(OpenPipe());

        private Stream OpenPipe()
        {
            var pipe = new Pipe();
            _ = Task.Run(async () =>
            {
                Exception? error = null;
                try { await PumpAsync(async (bytes, token) => !(await pipe.Writer.WriteAsync(bytes, token)).IsCompleted, CancellationToken.None); }
                catch (Exception ex) { error = ex; }
                await pipe.Writer.CompleteAsync(error);
            });
            return pipe.Reader.AsStream();
        }

        /// <summary>Escribe las partes con <paramref name="write"/> hasta que el equipo se desconecta o el lector ya no está (false).</summary>
        private async Task PumpAsync(Func<byte[], CancellationToken, ValueTask<bool>> write, CancellationToken ct)
        {
            var queue = Channel.CreateBounded<(string ContentType, byte[] Body)>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
            using var subscription = gateway.SubscribeAlarms(deviceId, alarm =>
            {
                foreach (var part in PartsOf(alarm)) queue.Writer.TryWrite(part);
            });
            if (!await write(Part("application/json", Heartbeat()), ct)) return;
            while (!ct.IsCancellationRequested && gateway.IsOnline(deviceId))
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(20));
                byte[] next;
                try
                {
                    var part = await queue.Reader.ReadAsync(wait.Token);
                    next = Part(part.ContentType, part.Body);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    next = Part("application/json", Heartbeat());
                }
                if (!await write(next, ct)) return;
            }
        }

        private static byte[] Part(string contentType, byte[] body)
        {
            var head = Encoding.ASCII.GetBytes($"--{Boundary}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n\r\n");
            var part = new byte[head.Length + body.Length + 2];
            head.CopyTo(part, 0);
            body.CopyTo(part, head.Length);
            part[^2] = (byte)'\r';
            part[^1] = (byte)'\n';
            return part;
        }

        private static byte[] Heartbeat() =>
            Encoding.UTF8.GetBytes($"{{\"dateTime\":\"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}\",\"eventType\":\"heartBeat\",\"eventState\":\"inactive\"}}");

        /// <summary>
        /// Partes del alertStream equivalentes a una alarma ISUP: el XML/JSON ISAPI tal cual (más sus fotos como partes image/jpeg,
        /// igual que las manda el equipo por HTTP) o, si llegó como estructura, su traducción al JSON que entiende el lector.
        /// </summary>
        internal static IEnumerable<(string ContentType, byte[] Body)> PartsOf(IsupAlarm alarm)
        {
            if (alarm.Text is { Length: > 0 } text)
            {
                yield return (text.TrimStart().StartsWith('{') ? "application/json" : "application/xml", Encoding.UTF8.GetBytes(text));
                foreach (var path in alarm.Pictures ?? [])
                {
                    byte[]? jpeg = null;
                    try { jpeg = File.ReadAllBytes(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    if (jpeg is { Length: > 0 }) yield return ("image/jpeg", jpeg);
                }
                yield break;
            }
            if (alarm.Cid is { } cid) yield return ("application/json", CidJson(cid, alarm.ReceivedAt));
            else if (alarm.Basic is { } basic) yield return ("application/xml", BasicXml(basic, alarm.ReceivedAt));
        }
    }

    /// <summary>
    /// Reporte Contact-ID de un panel (NET_EHOME_CID_INFO) como el JSON «cidEvent» del alertStream de los AX PRO, que es lo que
    /// traduce el driver de paneles: código (calificador + evento), partición, zona y usuario.
    /// </summary>
    internal static byte[] CidJson(IsupCidAlarm cid, DateTimeOffset receivedAt)
    {
        string when = DateTime.TryParseExact(cid.TriggerTime, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? new DateTimeOffset(local, receivedAt.Offset).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)
            : receivedAt.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        string? user = cid.UserName is { Length: > 0 } name && !name.Equals("NONE", StringComparison.OrdinalIgnoreCase) ? name
                     : cid.UserNo >= 0 && cid.UserType is 1 or 2 ? $"usuario {cid.UserNo}" : null;
        var payload = new Dictionary<string, object?>
        {
            ["dateTime"] = when,
            ["eventType"] = "cidEvent",
            ["eventState"] = "active",
            ["eventDescription"] = cid.Description,
            ["CIDEvent"] = new Dictionary<string, object?>
            {
                ["code"] = cid.Code.ToString(CultureInfo.InvariantCulture),
                ["type"] = cid.CidType.ToString(CultureInfo.InvariantCulture),
                ["description"] = cid.Description,
                ["subSystem"] = cid.SubSystem > 0 ? cid.SubSystem : null,
                ["zone"] = cid.ZoneNo >= 0 ? cid.ZoneNo : null,
                ["userName"] = user,
                ["source"] = "isup",
            },
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    /// <summary>Alarma básica de ISUP (NET_EHOME_ALARM_INFO) como un EventNotificationAlert ISAPI.</summary>
    internal static byte[] BasicXml(IsupBasicAlarm basic, DateTimeOffset receivedAt)
    {
        string eventType = basic.AlarmType switch
        {
            5 => "videoloss", 6 => "IO", 7 => "shelteralarm", 8 => "VMD", 11 => "PIR", 19 => "illAccess", 97 => "facedetection",
            98 => "defocus", 99 => "audioexception", 100 => "scenechangedetection", 101 => "linedetection", 102 => "regionEntrance",
            103 => "regionExiting", 104 => "fielddetection", 105 => "loitering", 106 => "attendedBaggage", 109 => "peopleGathering",
            112 => "unattendedBaggage", 113 => "attendedBaggage", 0 => "diskfull", 1 => "diskerror", _ => $"isupAlarm{basic.AlarmType}",
        };
        string when = DateTime.TryParseExact(basic.DeviceTime, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? new DateTimeOffset(local, receivedAt.Offset).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)
            : receivedAt.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><EventNotificationAlert version=\"2.0\" xmlns=\"http://www.hikvision.com/ver20/XMLSchema\">");
        xml.Append($"<dateTime>{when}</dateTime>");
        if (basic.Channel > 0) xml.Append($"<channelID>{basic.Channel}</channelID>");
        if (basic.AlarmInput > 0) xml.Append($"<inputIOPortID>{basic.AlarmInput}</inputIOPortID>");
        xml.Append($"<eventType>{eventType}</eventType><eventState>{(basic.Action == 0 ? "active" : "inactive")}</eventState>");
        xml.Append($"<eventDescription>{System.Security.SecurityElement.Escape(basic.Remark ?? eventType)}</eventDescription>");
        xml.Append("</EventNotificationAlert>");
        return Encoding.UTF8.GetBytes(xml.ToString());
    }
}
