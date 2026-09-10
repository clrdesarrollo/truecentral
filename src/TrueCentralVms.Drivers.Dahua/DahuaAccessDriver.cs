using System.Net;
using System.Text.RegularExpressions;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Dahua;

public sealed class DahuaAccessDriverFactory : IAccessControlDriverFactory
{
    public string DriverKey => "dahua-http";
    public string DisplayName => "Dahua ASI/ASC (control de acceso por CGI HTTP)";
    public int DefaultPort => 80;
    public bool DefaultHttps => false;
    public AccessAuthMode AuthMode => AccessAuthMode.UserPassword;
    public string? Hint => "Use un usuario local del equipo (normalmente admin), el mismo de su página web.";
    public IAccessControlDriver Create() => new DahuaAccessDriver();
}

/// <summary>
/// Control de acceso Dahua (familias ASI terminales, ASC controladoras y ASG
/// torniquetes) por la API CGI HTTP del equipo, autenticada con Digest — la
/// misma vía que ya usa el driver de video para las capturas, y la que
/// documenta Dahua para integraciones.
///
/// Rutas que usa el administrador de dispositivos:
/// <list type="bullet">
/// <item><c>/cgi-bin/magicBox.cgi?action=getSystemInfo</c> — tipo de equipo,
/// serie y versión de hardware; <c>getSoftwareVersion</c> el firmware y
/// <c>getMachineName</c> el nombre configurado.</item>
/// <item><c>/cgi-bin/configManager.cgi?action=getConfig&amp;name=AccessControl</c> —
/// confirma que el equipo tiene subsistema de puertas y dice cuántas
/// administra (una entrada <c>table.AccessControl[i]</c> por puerta).</item>
/// <item><c>/cgi-bin/recordFinder.cgi?action=getQuerySize&amp;name=…</c> — si
/// responde, el equipo lleva registro de tarjetas y de eventos de acceso.</item>
/// </list>
/// Las respuestas son texto plano <c>clave=valor</c>, una por línea. Igual que
/// en Hikvision, todo lo que no sea la identificación se consulta de forma
/// TOLERANTE: los firmware de la familia varían en qué CGI exponen, y un 400
/// o un 404 significan "no lo soporta", no un error.
///
/// Nunca se sondea <c>accessControl.cgi?action=openDoor</c> para detectar
/// capacidades: esa llamada ABRE la puerta.
/// </summary>
public sealed partial class DahuaAccessDriver : IAccessControlDriver
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Tope de puertas que se leen del equipo (las controladoras de la familia llegan a 8).</summary>
    private const int MaxDoors = 16;

    /// <summary>
    /// Clasifica un modelo Dahua como equipo de control de acceso; null = no
    /// lo es (o no lo soporta este driver). Los modelos llegan con o sin el
    /// prefijo comercial (<c>DHI-ASI1212D</c> por DHDiscover, <c>ASI1212D</c>
    /// por CGI). ASI/ASA terminales, ASC controladoras, ASG torniquetes; los
    /// videoporteros (VT*) y las cerraduras autónomas quedan fuera.
    /// La usa también el descubrimiento para listar solo lo compatible.
    /// </summary>
    public static AccessDeviceKind? ClassifyModel(string? model)
    {
        string m = (model ?? "").Trim().ToUpperInvariant().Replace(" ", "");
        if (m.Length == 0) return null;
        foreach (string prefix in new[] { "DHI-", "DH-", "DAHUA-" })
            if (m.StartsWith(prefix)) m = m[prefix.Length..];

        if (m.StartsWith("ASI") || m.StartsWith("ASA")) return AccessDeviceKind.Terminal;
        if (m.StartsWith("ASC")) return AccessDeviceKind.Controller;
        if (m.StartsWith("ASG") || m.StartsWith("AST")) return AccessDeviceKind.Turnstile;
        return null;
    }

    // ------------------------------------------------------------------
    // Identificación
    // ------------------------------------------------------------------

    public async Task<AccessDeviceInfo> ProbeAsync(AccessConnectionInfo info, CancellationToken ct = default)
    {
        using var http = CreateClient(info);

        var system = Parse(await GetAsync(http, info, "/cgi-bin/magicBox.cgi?action=getSystemInfo", ct, required: true));
        string? model = system.GetValueOrDefault("deviceType") ?? system.GetValueOrDefault("updateSerial");
        string? serial = system.GetValueOrDefault("serialNumber");

        var software = Parse(await GetAsync(http, info, "/cgi-bin/magicBox.cgi?action=getSoftwareVersion", ct));
        string? firmware = software.GetValueOrDefault("version");

        // El MAC no viene en getSystemInfo: está en la configuración de red.
        var network = Parse(await GetAsync(http, info, "/cgi-bin/configManager.cgi?action=getConfig&name=Network", ct));
        string? mac = network.FirstOrDefault(p => p.Key.EndsWith(".PhysicalAddress", StringComparison.OrdinalIgnoreCase)).Value;

        // Confirmar que es control de acceso: una cámara Dahua también responde
        // magicBox, pero no tiene la configuración de puertas.
        var doorConfig = Parse(await GetAsync(http, info,
            "/cgi-bin/configManager.cgi?action=getConfig&name=AccessControl", ct));
        if (doorConfig.Count == 0)
            doorConfig = Parse(await GetAsync(http, info,
                "/cgi-bin/configManager.cgi?action=getConfig&name=AccessControlGeneral", ct));
        if (doorConfig.Count == 0)
            throw new DriverException(
                $"El equipo ({model ?? "modelo desconocido"}) responde el CGI de Dahua pero no es un equipo de control " +
                "de acceso (no expone la configuración de puertas).");

        var doors = ReadDoors(doorConfig);
        bool cards = await AnswersAsync(http, info, "/cgi-bin/recordFinder.cgi?action=getQuerySize&name=AccessControlCard", ct);
        bool events = await AnswersAsync(http, info, "/cgi-bin/recordFinder.cgi?action=getQuerySize&name=AccessControlCardRec", ct);
        var definition = Parse(await GetAsync(http, info, "/cgi-bin/magicBox.cgi?action=getProductDefinition", ct));

        var capabilities = new AccessCapabilities(
            DoorCount: doors.Count,
            // El equipo acepta la orden de apertura remota por el mismo CGI de
            // la configuración de puertas (no se prueba: abriría la puerta).
            SupportsRemoteControl: true,
            SupportsEvents: events,
            SupportsCards: cards,
            SupportsFingerprint: FlagOf(definition, "Finger"),
            SupportsFace: FlagOf(definition, "Face"),
            UserCapacity: CapacityOf(definition, "User"),
            CardCapacity: CapacityOf(definition, "Card"));

        var kind = ClassifyModel(model) ?? (doors.Count > 1 ? AccessDeviceKind.Controller : AccessDeviceKind.Unknown);
        return new AccessDeviceInfo(model, serial, firmware, system.GetValueOrDefault("processor"), mac,
            kind, capabilities, doors);
    }

    public async Task PingAsync(AccessConnectionInfo info, CancellationToken ct = default)
    {
        using var http = CreateClient(info);
        _ = await GetAsync(http, info, "/cgi-bin/magicBox.cgi?action=getSystemInfo", ct, required: true);
    }

    // ------------------------------------------------------------------
    // Puertas
    // ------------------------------------------------------------------

    /// <summary>
    /// Puertas declaradas por la configuración: una entrada
    /// <c>table.AccessControl[i]</c> por puerta. El nombre sale de la propia
    /// configuración cuando el equipo lo trae; si no, queda "Puerta n".
    /// </summary>
    private static List<AccessDoorInfo> ReadDoors(Dictionary<string, string> config)
    {
        var indexes = new SortedSet<int>();
        foreach (string key in config.Keys)
            if (IndexPattern().Match(key) is { Success: true } match && int.TryParse(match.Groups["i"].Value, out int index))
                indexes.Add(index);

        // Sin índices en la respuesta (firmware que devuelve una sola puerta
        // sin tabla), el equipo administra una.
        if (indexes.Count == 0) indexes.Add(0);

        var doors = new List<AccessDoorInfo>();
        foreach (int index in indexes.Take(MaxDoors))
        {
            int number = index + 1;   // Dahua numera desde 0; el VMS muestra desde 1
            string? name = null;
            foreach (var pair in config)
            {
                if (!pair.Key.Contains($"[{index}]", StringComparison.Ordinal)) continue;
                if (!pair.Key.EndsWith(".Name", StringComparison.OrdinalIgnoreCase) &&
                    !pair.Key.EndsWith(".DoorName", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(pair.Value)) { name = pair.Value.Trim(); break; }
            }
            doors.Add(new AccessDoorInfo(number, string.IsNullOrWhiteSpace(name) ? $"Puerta {number}" : name!));
        }
        return doors;
    }

    // ------------------------------------------------------------------
    // Transporte CGI
    // ------------------------------------------------------------------

    private static HttpClient CreateClient(AccessConnectionInfo info)
    {
        var handler = new HttpClientHandler
        {
            // Digest lo negocia el propio handler ante el 401 con desafío.
            Credentials = new NetworkCredential(info.Username, info.Password),
            PreAuthenticate = true,
            AllowAutoRedirect = false,
        };
        if (info.UseHttps)
            // Los equipos de terreno traen certificado autofirmado: la confianza
            // es por dirección y credenciales (mismo criterio que los otros drivers).
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        return new HttpClient(handler) { Timeout = RequestTimeout };
    }

    /// <summary>
    /// Ejecuta un CGI y devuelve el cuerpo. null = el equipo no soporta esa
    /// ruta (404/400), salvo que sea <paramref name="required"/>. Lanza
    /// <see cref="DriverException"/> con mensaje en español si el equipo es
    /// inalcanzable o rechaza las credenciales.
    /// </summary>
    private static async Task<string?> GetAsync(HttpClient http, AccessConnectionInfo info, string path,
        CancellationToken ct, bool required = false)
    {
        string url = $"{(info.UseHttps ? "https" : "http")}://{info.Host}:{info.Port}{path}";
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new DriverException($"No se pudo conectar con el equipo en {info.Host}:{info.Port}: {ex.Message}", ex);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DriverException($"El equipo en {info.Host}:{info.Port} no respondió a tiempo.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new DriverException("El equipo rechazó las credenciales (usuario o contraseña incorrectos).");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new DriverException("El equipo denegó la operación (el usuario no tiene permiso).");
            string body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode) return body;
            if (required)
                throw new DriverException($"El equipo respondió {(int)response.StatusCode} a {path}" +
                                          (body.Length > 0 ? $": {Shorten(body)}" : "."));
            return null;   // función que este firmware no expone
        }
    }

    /// <summary>true si el equipo contesta ese CGI con contenido útil (la función existe).</summary>
    private static async Task<bool> AnswersAsync(HttpClient http, AccessConnectionInfo info, string path, CancellationToken ct)
    {
        try
        {
            string? body = await GetAsync(http, info, path, ct);
            return body is not null && !body.Contains("Error", StringComparison.OrdinalIgnoreCase);
        }
        catch (DriverException) { return false; }
    }

    /// <summary>Respuestas del CGI: líneas <c>clave=valor</c>.</summary>
    private static Dictionary<string, string> Parse(string? body)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(body)) return values;
        foreach (string line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0) continue;
            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return values;
    }

    /// <summary>Bandera de la definición de producto cuya clave menciona esa función (SupportFingerPrint, FaceFun…).</summary>
    private static bool FlagOf(Dictionary<string, string> definition, string feature) =>
        definition.Any(p => p.Key.Contains(feature, StringComparison.OrdinalIgnoreCase) &&
                            (p.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                             (int.TryParse(p.Value, out int number) && number > 0)));

    /// <summary>Cupo declarado en la definición de producto (MaxUserNum, CardNum…).</summary>
    private static int? CapacityOf(Dictionary<string, string> definition, string feature)
    {
        foreach (var pair in definition)
        {
            if (!pair.Key.Contains(feature, StringComparison.OrdinalIgnoreCase)) continue;
            if (!pair.Key.Contains("Max", StringComparison.OrdinalIgnoreCase) &&
                !pair.Key.Contains("Num", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(pair.Value, out int value) && value > 0) return value;
        }
        return null;
    }

    private static string Shorten(string text) =>
        text.Length <= 160 ? text.Trim() : text[..160].Trim() + "…";

    [GeneratedRegex(@"\[(?<i>\d+)\]")]
    private static partial Regex IndexPattern();

    // ==================================================================
    // Operación
    // ==================================================================

    // El equipo informa si la hoja está abierta, pero el padrón (personas,
    // credenciales y horarios) se administra en el propio equipo o en su
    // software: el VMS lee sus eventos y opera sus puertas, y lo dice.
    public bool SupportsDoorStatus => true;

    /// <summary>
    /// Abre o cierra una puerta con
    /// <c>/cgi-bin/accessControl.cgi?action=openDoor|closeDoor</c>. Dahua
    /// numera los canales desde 0 y el VMS las puertas desde 1.
    ///
    /// "Mantener abierta" y "bloquear" no se ofrecen: en esta familia son un
    /// cambio de configuración del equipo que cada firmware escribe distinto,
    /// y dejar una puerta abierta por una llamada que quizá no se aplicó sería
    /// peor que decir que no se puede.
    /// </summary>
    public async Task ControlDoorAsync(AccessConnectionInfo info, int doorNumber, AccessDoorCommand command,
        CancellationToken ct = default)
    {
        string action = command switch
        {
            AccessDoorCommand.Open => "openDoor",
            AccessDoorCommand.Close => "closeDoor",
            _ => throw new DriverException(
                "Los equipos Dahua no aceptan \"mantener abierta\" ni \"bloquear\" desde el VMS: " +
                "esos modos se configuran en el propio equipo."),
        };
        using var http = CreateClient(info);
        int channel = doorNumber - 1;
        string path = $"/cgi-bin/accessControl.cgi?action={action}&channel={channel}&Type=Remote";
        string body = await GetAsync(http, info, path, ct, required: true) ?? "";
        if (body.Contains("Error", StringComparison.OrdinalIgnoreCase))
            throw new DriverException($"El equipo rechazó la orden de puerta: {Shorten(body)}");
    }

    /// <summary>
    /// Estado de la hoja de cada puerta con
    /// <c>accessControl.cgi?action=getDoorStatus</c> (responde
    /// <c>Info=Open</c> o <c>Info=Close</c>). El modo (normal, mantenida
    /// abierta, bloqueada) no se consulta porque este driver tampoco lo
    /// cambia: queda en "normal" y el equipo manda.
    /// </summary>
    public async Task<IReadOnlyList<AccessDoorStatus>> ReadDoorStatusAsync(AccessConnectionInfo info, int doorCount,
        CancellationToken ct = default)
    {
        using var http = CreateClient(info);
        var result = new List<AccessDoorStatus>();
        for (int number = 1; number <= doorCount; number++)
        {
            string? body;
            try { body = await GetAsync(http, info, $"/cgi-bin/accessControl.cgi?action=getDoorStatus&channel={number - 1}", ct); }
            catch (DriverException) { return result; }   // el firmware no expone la ruta: mejor nada que inventar
            if (body is null) return result;

            var values = Parse(body);
            string status = (values.GetValueOrDefault("Info") ?? values.GetValueOrDefault("status") ?? "").Trim();
            bool? open = status.Equals("Open", StringComparison.OrdinalIgnoreCase) ? true
                : status.Equals("Close", StringComparison.OrdinalIgnoreCase) ? false
                : null;
            result.Add(new AccessDoorStatus(number, AccessDoorMode.Normal, open));
        }
        return result;
    }

    /// <summary>
    /// Historial de accesos con el buscador de registros de Dahua, que es un
    /// diálogo de cuatro pasos: se crea un buscador
    /// (<c>factory.create</c>), se le fija el rango (<c>startFind</c>), se lee
    /// por tandas (<c>doFind</c>) y se destruye (<c>destroy</c>). Los tiempos
    /// van en segundos desde el epoch, en la hora del equipo.
    ///
    /// Las rutas y los campos vienen de la documentación de la API HTTP de
    /// Dahua, todavía sin validar contra un equipo de esta familia: lo que no
    /// se entiende se guarda como "otro" con su texto crudo en vez de
    /// adivinarse.
    /// </summary>
    public async Task<IReadOnlyList<AccessEventRecord>> FetchEventsAsync(AccessConnectionInfo info, DateTime sinceUtc,
        int max, CancellationToken ct = default)
    {
        using var http = CreateClient(info);
        const string recordSet = "AccessControlCardRec";

        string? created = await GetAsync(http, info, $"/cgi-bin/recordFinder.cgi?action=factory.create&name={recordSet}", ct);
        if (created is null) return [];
        string token = (Parse(created).GetValueOrDefault("result") ?? created).Trim();
        if (token.Length == 0) return [];

        try
        {
            long start = new DateTimeOffset(DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
            long end = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds();
            _ = await GetAsync(http, info,
                $"/cgi-bin/recordFinder.cgi?action=startFind&object={token}" +
                $"&condition.StartTime={start}&condition.EndTime={end}", ct, required: true);

            var events = new List<AccessEventRecord>();
            while (events.Count < max)
            {
                string? page = await GetAsync(http, info,
                    $"/cgi-bin/recordFinder.cgi?action=doFind&object={token}&count={Math.Min(50, max - events.Count)}", ct);
                if (page is null) break;
                var values = Parse(page);
                if (!int.TryParse(values.GetValueOrDefault("found"), out int found) || found <= 0) break;
                for (int i = 0; i < found; i++) events.Add(ParseRecord(values, i));
                if (found < 50) break;
            }
            return events.Where(e => e.Timestamp > sinceUtc).OrderBy(e => e.Timestamp).ToList();
        }
        finally
        {
            // El equipo mantiene vivo el buscador hasta que se lo destruye, y
            // acepta pocos a la vez: soltarlo es obligatorio, falle o no la lectura.
            try { _ = await GetAsync(http, info, $"/cgi-bin/recordFinder.cgi?action=destroy&object={token}", CancellationToken.None); }
            catch (DriverException) { /* el buscador caduca solo */ }
        }
    }

    /// <summary>Un renglón <c>items[i].Campo=valor</c> de la respuesta del buscador.</summary>
    private static AccessEventRecord ParseRecord(Dictionary<string, string> values, int index)
    {
        string? Field(string name) =>
            values.GetValueOrDefault($"items[{index}].{name}") is { Length: > 0 } value ? value : null;

        var timestamp = long.TryParse(Field("CreateTime"), out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : DateTime.UtcNow;

        // Status 1 = pasó; ErrorCode distinto de 0 = lo rechazó.
        string status = Field("Status") ?? "";
        string errorCode = Field("ErrorCode") ?? "0";
        bool denied = errorCode is not ("0" or "") || status == "0";

        var credential = Field("Method") switch
        {
            "1" => AccessCredentialKind.Pin,
            "2" => AccessCredentialKind.Card,
            "3" => AccessCredentialKind.Fingerprint,
            "4" => AccessCredentialKind.Face,
            "6" => AccessCredentialKind.Qr,
            _ => string.IsNullOrEmpty(Field("CardNo")) ? AccessCredentialKind.Unknown : AccessCredentialKind.Card,
        };

        return new AccessEventRecord(
            Timestamp: timestamp,
            DoorNumber: int.TryParse(Field("Door"), out int door) ? door + 1 : null,
            Kind: denied ? AccessEventKind.Denied : AccessEventKind.Granted,
            Credential: credential,
            Description: denied ? $"Acceso denegado por el equipo (código {errorCode})" : "Acceso concedido",
            EmployeeNo: Field("UserID"),
            PersonName: Field("CardName") ?? Field("UserName"),
            CardNumber: Field("CardNo"),
            MajorType: null,
            MinorType: int.TryParse(errorCode, out int code) ? code : null,
            RawJson: null);
    }
}
