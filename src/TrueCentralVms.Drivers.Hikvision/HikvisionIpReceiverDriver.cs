using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Driver de paneles de alarma a través del <b>Hik IP Receiver Pro</b>
/// (IPRP), la pasarela/receptora de Hikvision: los paneles se registran en
/// ella (ISUP 5.0, OTAP o Hik-Partner Pro) y el VMS habla solo con la
/// pasarela por su API REST (guía "Hik IP Receiver Pro API Developer Guide
/// V2.5.0"), autenticando por Digest con la cuenta de la pasarela.
///
/// Qué cambia respecto del driver directo:
///  - Cada llamada lleva <c>devIndex=&lt;uuid&gt;</c>: el uuid que la
///    pasarela asigna al equipo. El usuario puede indicar ese uuid o la
///    serie, la cuenta (accountID) o el ID ISUP/OTAP: se resuelve buscando
///    en <c>/ISAPI/ContentMgmt/DeviceMgmt/deviceList</c>.
///  - El estado de zonas se pide por POST paginado (<c>ZoneCond</c>) y llega
///    como <c>ZoneSearch</c>; las áreas igual que en el panel (SubSysList),
///    pero sin nombres (la pasarela no los expone).
///  - Los eventos llegan por UNA suscripción multipart
///    (<c>subscribeDeviceMgmt</c>) que trae los de TODOS los equipos con su
///    devIndex: se filtran los de este panel. Formato CIDAlarm
///    (CIDCode/subSys/zoneNo/CIDParam) + devStatusChanged (en línea/fuera
///    de línea respecto de la pasarela) + heartBeat cada 10 s.
///  - Requisitos en la pasarela: Automation Output → Protocol con tipo
///    <b>Private</b> habilitado (sin eso el API contesta 403 "Invalid
///    operation"), y una cuenta con permisos (admin).
/// </summary>
public sealed class HikvisionIpReceiverDriver : IAlarmPanelDriver
{
    private const string AllAreas = "0xffffffff";
    private static readonly TimeSpan ResolveTtl = TimeSpan.FromMinutes(5);

    /// <summary>Equipo tal como lo describe la pasarela.</summary>
    private sealed record GatewayDevice(string DevIndex, string Name, string? Serial, string? AccountId, string? IsupId,
        string? Model, string? Version, string? Status, int? Source);

    /// <summary>Resolución identificador → uuid, cacheada por pasarela (evita una búsqueda por cada sondeo).</summary>
    private static readonly ConcurrentDictionary<string, (GatewayDevice Device, DateTime At)> Resolved = new();

    private static HikvisionIsapiClient ClientOf(AlarmConnectionInfo info) => new(info, digestOnly: true);

    // ------------------------------------------------------------------
    // Sondeo
    // ------------------------------------------------------------------

    public async Task<AlarmPanelInfo> ProbeAsync(AlarmConnectionInfo info, CancellationToken ct = default)
    {
        var client = ClientOf(info);
        if (string.IsNullOrWhiteSpace(info.DeviceId))
        {
            // Sin identificador: validar credenciales y ayudar a elegir.
            var all = await ListDevicesAsync(client, null, ct);
            string list = all.Count == 0
                ? "la pasarela no tiene equipos agregados todavía"
                : "equipos en la pasarela: " + string.Join("; ", all.Take(20).Select(Describe));
            throw new DriverException($"Indique el equipo dentro del IP Receiver Pro (uuid, serie, cuenta o ID ISUP); {list}.");
        }

        var device = await ResolveAsync(client, info, ct, force: true);
        string type = device.Source switch
        {
            0 or 3 or 7 => "SecurityCP",
            1 or 4 => "Video",
            5 => "AccessControl",
            _ => "Gateway",
        };
        return new AlarmPanelInfo(device.Model, device.Serial, device.Version, type);
    }

    private static string Describe(GatewayDevice d)
    {
        var ids = new[] { d.IsupId is null ? null : $"ISUP {d.IsupId}", d.AccountId is null ? null : $"cuenta {d.AccountId}", d.Serial is null ? null : $"serie {d.Serial}" }
            .Where(s => s is not null);
        return $"«{d.Name}» ({string.Join(", ", ids!)}{(d.Model is null ? "" : $", {d.Model}")}, {d.Status ?? "estado desconocido"})";
    }

    /// <summary>
    /// Busca el equipo en la pasarela por uuid, serie, cuenta, ID ISUP/OTAP o
    /// nombre exacto. Lanza <see cref="DriverException"/> si no está o si
    /// la búsqueda es ambigua.
    /// </summary>
    private static async Task<GatewayDevice> ResolveAsync(HikvisionIsapiClient client, AlarmConnectionInfo info,
        CancellationToken ct, bool force = false)
    {
        string wanted = info.DeviceId!.Trim();
        string key = $"{info.Host}|{info.Port}|{wanted}";
        if (!force && Resolved.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < ResolveTtl)
            return cached.Device;

        var candidates = await ListDevicesAsync(client, Guid.TryParse(wanted, out _) ? null : wanted, ct);
        bool Eq(string? a) => string.Equals(a, wanted, StringComparison.OrdinalIgnoreCase);
        var exact = candidates.Where(d => Eq(d.DevIndex) || Eq(d.Serial) || Eq(d.AccountId) || Eq(d.IsupId) || Eq(d.Name)).ToList();
        if (exact.Count == 0 && candidates.Count == 1) exact = candidates;
        if (exact.Count == 0)
            throw new DriverException($"El IP Receiver Pro no tiene ningún equipo identificado como '{wanted}' " +
                                      "(uuid, serie, cuenta, ID ISUP o nombre).");
        if (exact.Count > 1)
            throw new DriverException($"'{wanted}' coincide con {exact.Count} equipos en el IP Receiver Pro; use el uuid o la serie: " +
                                      string.Join("; ", exact.Take(5).Select(Describe)) + ".");
        Resolved[key] = (exact[0], DateTime.UtcNow);
        return exact[0];
    }

    private static async Task<List<GatewayDevice>> ListDevicesAsync(HikvisionIsapiClient client, string? keyword, CancellationToken ct)
    {
        var result = new List<GatewayDevice>();
        int position = 0;
        while (true)
        {
            string filter = keyword is null ? "" : $",\"Filter\":{{\"key\":{JsonSerializer.Serialize(keyword)}}}";
            string body = $"{{\"SearchDescription\":{{\"position\":{position},\"maxResult\":100{filter}}}}}";
            string json = await client.RequestAsync(HttpMethod.Post, "/ISAPI/ContentMgmt/DeviceMgmt/deviceList?format=json", body,
                              ct: ct, allowNotFound: false)
                          ?? throw new DriverException("El IP Receiver Pro no respondió la lista de equipos.");
            using var doc = JsonDocument.Parse(json);
            if (!HikvisionAlarmPanelDriver.TryGetPropertyIgnoreCase(doc.RootElement, "SearchResult", out var search))
                throw new DriverException("El IP Receiver Pro respondió algo inesperado a la lista de equipos (¿es un Hik IP Receiver Pro?).");
            int total = HikvisionAlarmPanelDriver.GetInt(search, "totalMatches") ?? 0;
            int got = 0;
            foreach (var item in HikvisionAlarmPanelDriver.EnumerateList(search, "MatchList", "Device"))
            {
                got++;
                string? devIndex = HikvisionAlarmPanelDriver.GetString(item, "devIndex");
                if (string.IsNullOrEmpty(devIndex)) continue;
                string? isup = null;
                if (HikvisionAlarmPanelDriver.TryGetPropertyIgnoreCase(item, "ISUP", out var isupObj) && isupObj.ValueKind == JsonValueKind.Object)
                    isup = HikvisionAlarmPanelDriver.GetString(isupObj, "deviceID");
                if (isup is null && HikvisionAlarmPanelDriver.TryGetPropertyIgnoreCase(item, "OTAP", out var otapObj) && otapObj.ValueKind == JsonValueKind.Object)
                    isup = HikvisionAlarmPanelDriver.GetString(otapObj, "deviceID");
                result.Add(new GatewayDevice(
                    devIndex,
                    HikvisionAlarmPanelDriver.GetString(item, "devName") ?? devIndex,
                    Blank(HikvisionAlarmPanelDriver.GetString(item, "devSerial")),
                    Blank(HikvisionAlarmPanelDriver.GetString(item, "accountID")),
                    Blank(isup),
                    Blank(HikvisionAlarmPanelDriver.GetString(item, "devMode")),
                    Blank(HikvisionAlarmPanelDriver.GetString(item, "devVersion")),
                    Blank(HikvisionAlarmPanelDriver.GetString(item, "devStatus")),
                    HikvisionAlarmPanelDriver.GetInt(item, "devSource")));
            }
            position += got;
            if (got == 0 || position >= total) break;
        }
        return result;

        static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    // ------------------------------------------------------------------
    // Administración de equipos DENTRO de la pasarela
    //
    // Alta y baja de paneles en el IP Receiver Pro sin abrir su interfaz web:
    // el VMS queda como único punto de operación. La pasarela expone
    // addDevice/delDevice desde su versión 2.5.0 (guía del API, 7.3 y 7.5).
    // ------------------------------------------------------------------

    /// <summary>Equipo de la pasarela tal como se muestra en el VMS.</summary>
    public sealed record GatewayDeviceSummary(string DevIndex, string Name, string? Serial, string? AccountId,
        string? IsupId, string? Model, string? Version, string? Status);

    /// <summary>Datos para dar de alta un panel en la pasarela.</summary>
    public sealed record GatewayDeviceSpec(string Protocol, string DeviceId, string? DeviceKey, string Name,
        string? DeviceType, string? AccountId, string? Remark);

    /// <summary>
    /// Llamada de administración (alta/baja) contra la pasarela. No comparte
    /// el camino de <c>RequireAsync</c>, pensado para las lecturas de estado:
    /// aquí no interviene el protocolo «Private» ni el estado en línea del
    /// panel, y la respuesta trae su propio resultado por equipo, que es el
    /// que se traduce después.
    /// </summary>
    private static async Task<string> ManageAsync(AlarmConnectionInfo info, string path, string body,
        CancellationToken ct, string what)
    {
        string? text;
        try
        {
            text = await ClientOf(info).RequestAsync(HttpMethod.Post, path, body, ct: ct, allowNotFound: false);
        }
        catch (DriverException ex) when (ex.Message.Contains("404", StringComparison.OrdinalIgnoreCase) ||
                                         ex.Message.Contains("notSupport", StringComparison.OrdinalIgnoreCase) ||
                                         ex.Message.Contains("invalidOperation", StringComparison.OrdinalIgnoreCase))
        {
            throw new DriverException($"El IP Receiver Pro no admite {what} por API: requiere la versión V2.5.0 o superior " +
                                      "de la pasarela (en versiones anteriores el alta y la baja de equipos solo se hacen " +
                                      "desde su interfaz web).", ex);
        }
        if (string.IsNullOrWhiteSpace(text))
            throw new DriverException($"El IP Receiver Pro no respondió {what}.");
        return text;
    }

    /// <summary>
    /// La clave del panel (EhomeKey/OTAPKey) es un campo sensible: la pasarela
    /// espera recibirlo CIFRADO, no en claro. Su propio panel web lo marca como
    /// «security1» y lo cifra así (reproducido de su JavaScript):
    ///
    ///  1. <c>GET /ISAPI/Security/capabilities</c> entrega <c>salt</c>,
    ///     <c>keyIterateNum</c> e <c>isIrreversible</c>.
    ///  2. base = isIrreversible ? SHA256(usuario + salt + contraseña) : contraseña.
    ///  3. llave = SHA256(base + "AaBbCcDd1234!@#$"), repitiendo SHA256 sobre el
    ///     resultado hasta completar keyIterateNum vueltas; se toman los
    ///     primeros 32 caracteres del hexadecimal (16 bytes = AES-128).
    ///  4. El valor viaja como AES-128-CBC en hexadecimal, con un IV al azar que
    ///     se manda en la URL: <c>&amp;security=1&amp;iv=…</c>.
    ///
    /// Enviarlo en claro es justo lo que hace fallar el alta con
    /// «addDeviceFailed»: la pasarela intenta descifrar y no puede.
    /// </summary>
    private static async Task<(string Key, string Iv)?> BuildSecurityKeyAsync(HikvisionIsapiClient client,
        AlarmConnectionInfo info, CancellationToken ct)
    {
        string? json;
        try
        {
            json = await client.RequestAsync(HttpMethod.Get,
                $"/ISAPI/Security/capabilities?format=json&username={Uri.EscapeDataString(info.Username)}", ct: ct);
        }
        catch (DriverException)
        {
            return null;   // sin capacidades no se puede derivar: se enviará en claro
        }
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        if (!HikvisionAlarmPanelDriver.TryGetPropertyIgnoreCase(doc.RootElement, "SecurityCap", out var cap))
            return null;

        int iterations = HikvisionAlarmPanelDriver.GetInt(cap, "keyIterateNum") ?? 0;
        if (iterations <= 0)
            return null;   // la pasarela no usa este esquema

        string salt = HikvisionAlarmPanelDriver.GetString(cap, "salt") ?? "";
        bool irreversible = HikvisionAlarmPanelDriver.TryGetPropertyIgnoreCase(cap, "isIrreversible", out var irr) &&
                            irr.ValueKind == JsonValueKind.True;

        string basis = irreversible ? Sha256Hex(info.Username + salt + info.Password) : info.Password;
        string derived = Sha256Hex(basis + "AaBbCcDd1234!@#$");
        for (int i = 1; i < iterations; i++)
            derived = Sha256Hex(derived);

        return (derived[..32], Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>AES-128 CBC con relleno PKCS7; llave e IV en hexadecimal, salida hexadecimal.</summary>
    private static string AesCbcHex(string plain, string keyHex, string ivHex)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = Convert.FromHexString(keyHex);
        aes.IV = Convert.FromHexString(ivHex);
        using var encryptor = aes.CreateEncryptor();
        byte[] bytes = Encoding.UTF8.GetBytes(plain);
        return Convert.ToHexString(encryptor.TransformFinalBlock(bytes, 0, bytes.Length)).ToLowerInvariant();
    }

    /// <summary>Lista los equipos agregados en la pasarela (opcionalmente filtrados).</summary>
    public static async Task<IReadOnlyList<GatewayDeviceSummary>> ListGatewayDevicesAsync(
        AlarmConnectionInfo info, string? keyword, CancellationToken ct = default)
    {
        var devices = await ListDevicesAsync(ClientOf(info), string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim(), ct);
        return devices
            .Select(d => new GatewayDeviceSummary(d.DevIndex, d.Name, d.Serial, d.AccountId, d.IsupId, d.Model, d.Version, d.Status))
            .ToList();
    }

    /// <summary>
    /// Agrega un panel a la pasarela por ISUP 5.0 (EHome) u OTAP y devuelve el
    /// uuid (devIndex) que la pasarela le asignó, que es el identificador con
    /// el que después se opera el panel desde el VMS.
    /// </summary>
    public static async Task<string> AddGatewayDeviceAsync(AlarmConnectionInfo info, GatewayDeviceSpec spec,
        CancellationToken ct = default)
    {
        string protocol = NormalizeProtocol(spec.Protocol);
        string deviceId = (spec.DeviceId ?? "").Trim();
        if (deviceId.Length == 0)
            throw new DriverException("Falta el ID del equipo (el mismo que está configurado en el panel para reportar a la receptora).");
        if (deviceId.Length > 31 || !deviceId.All(char.IsLetterOrDigit))
            throw new DriverException("El ID del equipo admite hasta 31 caracteres, solo letras y números.");
        string? key = string.IsNullOrWhiteSpace(spec.DeviceKey) ? null : spec.DeviceKey.Trim();
        if (key is { Length: > 32 })
            throw new DriverException("La clave del equipo admite hasta 32 caracteres.");
        // Los equipos Hikvision exigen 8 o más para la clave ISUP/OTAP; con
        // menos, la pasarela rechaza el alta sin decir por qué.
        if (key is { Length: < 8 })
            throw new DriverException("La clave del equipo debe tener al menos 8 caracteres (es la que está configurada en el panel).");
        string name = string.IsNullOrWhiteSpace(spec.Name) ? deviceId : spec.Name.Trim();
        // "SecurityCP" (panel de alarma) o "encodingDev" (equipo de video).
        string devType = string.Equals(spec.DeviceType, "encodingDev", StringComparison.OrdinalIgnoreCase)
            ? "encodingDev" : "SecurityCP";

        var device = new Dictionary<string, object?>
        {
            ["protocolType"] = protocol,
            ["devName"] = name,
            ["devType"] = devType,
        };
        var parameters = new Dictionary<string, object?> { [protocol == "OTAP" ? "OTAPID" : "EhomeID"] = deviceId };
        if (key is not null) parameters[protocol == "OTAP" ? "OTAPKey" : "EhomeKey"] = key;   // se reemplaza cifrada más abajo
        device[protocol == "OTAP" ? "OTAPParams" : "EhomeParams"] = parameters;
        // El formulario del fabricante manda SIEMPRE accountID (el numero de
        // abonado con el que el panel se identifica ante la central) y remark.
        // Omitirlos es una de las cosas que la pasarela rechaza con
        // "addDeviceFailed", asi que si no se indica se usa el propio ID.
        device["accountID"] = string.IsNullOrWhiteSpace(spec.AccountId) ? deviceId : spec.AccountId.Trim();
        device["remark"] = string.IsNullOrWhiteSpace(spec.Remark) ? "" : spec.Remark.Trim();

        string keyField = protocol == "OTAP" ? "OTAPKey" : "EhomeKey";

        // Intento 1: la clave cifrada, que es como la espera la pasarela. Si la
        // pasarela no expone sus capacidades de seguridad, o si aun así rechaza
        // el alta, se reintenta en claro: hay versiones que la aceptan tal cual.
        var security = key is null ? null : await BuildSecurityKeyAsync(ClientOf(info), info, ct);
        if (security is { } sec)
        {
            parameters[keyField] = AesCbcHex(key!, sec.Key, sec.Iv);
            try
            {
                return await PostAddDeviceAsync(info, device, $"&security=1&iv={sec.Iv}", ct);
            }
            catch (DriverException)
            {
                parameters[keyField] = key;   // segundo intento, sin cifrar
            }
        }

        return await PostAddDeviceAsync(info, device, "", ct);
    }

    /// <summary>Manda el alta y traduce el resultado que devuelve la pasarela.</summary>
    private static async Task<string> PostAddDeviceAsync(AlarmConnectionInfo info, Dictionary<string, object?> device,
        string extraQuery, CancellationToken ct)
    {
        string body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["DeviceInList"] = new[] { new Dictionary<string, object?> { ["Device"] = device } },
        });

        string json = await ManageAsync(info, "/ISAPI/ContentMgmt/DeviceMgmt/addDevice?format=json" + extraQuery,
            body, ct, "el alta de equipos");

        using var doc = JsonDocument.Parse(json);
        foreach (var item in HikvisionAlarmPanelDriver.EnumerateList(doc.RootElement, "DeviceOutList", "Device"))
        {
            string? status = HikvisionAlarmPanelDriver.GetString(item, "status");
            if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
                return HikvisionAlarmPanelDriver.GetString(item, "devIndex")
                       ?? throw new DriverException("La pasarela agregó el equipo pero no devolvió su identificador.");
            // La respuesta literal va en el mensaje: sin ella hay que ir al
            // servidor a buscarla, y es lo único que dice qué rechazó de verdad.
            throw new DriverException(DescribeAddFailure(HikvisionAlarmPanelDriver.GetString(item, "subStatusCode")) +
                                      $" [clave {(extraQuery.Length > 0 ? "cifrada" : "en claro")}; respuesta: {Shorten(json)}]");
        }
        // Lote entero fallido: la pasarela responde solo con ResponseStatus.
        try
        {
            HikvisionAlarmPanelDriver.EnsureOk(json, "agregar el equipo");
        }
        catch (DriverException ex)
        {
            throw new DriverException($"{ex.Message} [clave {(extraQuery.Length > 0 ? "cifrada" : "en claro")}; " +
                                      $"respuesta: {Shorten(json)}]", ex);
        }
        throw new DriverException("La pasarela no informó el resultado del alta del equipo.");
    }

    /// <summary>Quita un equipo de la pasarela por su uuid (devIndex).</summary>
    public static async Task DeleteGatewayDeviceAsync(AlarmConnectionInfo info, string devIndex, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(devIndex))
            throw new DriverException("Falta el identificador del equipo a quitar de la pasarela.");

        string body = JsonSerializer.Serialize(new Dictionary<string, object?> { ["DevIndexList"] = new[] { devIndex.Trim() } });
        string json = await ManageAsync(info, "/ISAPI/ContentMgmt/DeviceMgmt/delDevice?format=json", body, ct,
            "la baja de equipos");

        // La caché de resolución quedaría apuntando a un equipo que ya no está.
        foreach (var cached in Resolved.Keys.Where(k => k.StartsWith($"{info.Host}|{info.Port}|", StringComparison.Ordinal)).ToList())
            Resolved.TryRemove(cached, out _);

        using var doc = JsonDocument.Parse(json);
        foreach (var item in HikvisionAlarmPanelDriver.EnumerateList(doc.RootElement, "DelDevList", "Dev"))
        {
            string? status = HikvisionAlarmPanelDriver.GetString(item, "status");
            if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
                return;
            throw new DriverException(HikvisionAlarmPanelDriver.GetString(item, "subStatusCode") switch
            {
                "theDeviceIdDoesNotExist" => "La pasarela ya no tiene ese equipo.",
                "badParameters" => "La pasarela rechazó los datos del equipo a quitar.",
                var other => $"La pasarela no pudo quitar el equipo{(other is null ? "" : $" ({other})")}.",
            });
        }
    }

    /// <summary>Recorta la respuesta cruda para que quepa en un mensaje de error.</summary>
    private static string Shorten(string value)
    {
        string one = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return one.Length <= 300 ? one : one[..300] + "…";
    }

    private static string NormalizeProtocol(string? protocol) => (protocol ?? "").Trim().ToLowerInvariant() switch
    {
        "otap" => "OTAP",
        "" or "isup" or "isup5" or "isup5.0" or "ehome" or "ehomev5" => "ehomeV5",
        _ => throw new DriverException($"Protocolo '{protocol}' no admitido por la pasarela: use ISUP 5.0 (ehomeV5) u OTAP."),
    };

    private static string DescribeAddFailure(string? subStatusCode) => subStatusCode switch
    {
        "deviceExist" => "Ese equipo ya está agregado en la pasarela.",
        "monitorNodeOverLimit" => "La pasarela llegó al límite de equipos de su licencia.",
        "badParameters" => "La pasarela rechazó los datos del equipo (revise el ID y la clave).",
        "addDeviceFailed" => "La pasarela no pudo agregar el equipo: revise que el ID y la clave sean los que tiene " +
                             "configurados el panel (en el AX PRO: Comunicación → ISUP) y que la clave tenga al menos 8 caracteres.",
        "noMemory" => "La pasarela no tiene memoria disponible para agregar el equipo.",
        var other => $"La pasarela no pudo agregar el equipo{(other is null ? "" : $" ({other})")}.",
    };

    private static async Task<(HikvisionIsapiClient Client, string DevIndex)> ConnectAsync(AlarmConnectionInfo info, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(info.DeviceId))
            throw new DriverException("Falta el identificador del equipo dentro del IP Receiver Pro.");
        var client = ClientOf(info);
        var device = await ResolveAsync(client, info, ct);
        return (client, device.DevIndex);
    }

    private static string Dev(string devIndex) => "&devIndex=" + Uri.EscapeDataString(devIndex);

    // ------------------------------------------------------------------
    // Estado
    // ------------------------------------------------------------------

    public async Task<AlarmPanelState> GetStateAsync(AlarmConnectionInfo info, CancellationToken ct = default)
    {
        var (client, devIndex) = await ConnectAsync(info, ct);

        string subsystems = await RequireAsync(client, HttpMethod.Get, $"/ISAPI/SecurityCP/status/subSystems?format=json{Dev(devIndex)}", null, ct,
            "el estado de áreas");

        // Zonas: búsqueda paginada; se reúnen en una sola lista para el mismo
        // traductor que usa el driver directo.
        var zones = new StringBuilder("{\"ZoneList\":[");
        int position = 0;
        bool first = true;
        while (true)
        {
            string cond = $"{{\"ZoneCond\":{{\"searchID\":\"tcvms\",\"searchResultPosition\":{position},\"maxResults\":32}}}}";
            string page = await RequireAsync(client, HttpMethod.Post, $"/ISAPI/SecurityCP/status/zones?format=json{Dev(devIndex)}", cond, ct,
                "el estado de zonas");
            using var doc = JsonDocument.Parse(page);
            if (!HikvisionAlarmPanelDriver.TryGetPropertyIgnoreCase(doc.RootElement, "ZoneSearch", out var search))
                search = doc.RootElement;
            int got = 0;
            foreach (var item in HikvisionAlarmPanelDriver.EnumerateList(search, "ZoneList", "Zone"))
            {
                if (!first) zones.Append(',');
                zones.Append("{\"Zone\":").Append(item.GetRawText()).Append('}');
                first = false;
                got++;
            }
            position += got;
            string status = HikvisionAlarmPanelDriver.GetString(search, "responseStatusStrg") ?? "OK";
            int total = HikvisionAlarmPanelDriver.GetInt(search, "totalMatches") ?? position;
            if (got == 0 || !status.Equals("MORE", StringComparison.OrdinalIgnoreCase) && position >= total) break;
        }
        zones.Append("]}");

        var areas = HikvisionAlarmPanelDriver.ParseAreas(subsystems, new Dictionary<int, string>());
        var zoneStates = HikvisionAlarmPanelDriver.ParseZones(zones.ToString(),
            new Dictionary<int, (string? Name, string? ZoneType, string? DetectorType, int? Area)>());

        AlarmHostState? host = null;
        try
        {
            string? hostJson = await client.RequestAsync(HttpMethod.Get, $"/ISAPI/SecurityCP/status/host?format=json{Dev(devIndex)}", ct: ct);
            if (hostJson is not null) host = HikvisionAlarmPanelDriver.ParseHost(hostJson);
        }
        catch (DriverException) { /* opcional */ }

        return new AlarmPanelState(areas, zoneStates, host);
    }

    private static async Task<string> RequireAsync(HikvisionIsapiClient client, HttpMethod method, string path, string? body,
        CancellationToken ct, string what)
    {
        string? text;
        try
        {
            text = await client.RequestAsync(method, path, body, ct: ct, allowNotFound: false);
        }
        catch (DriverException ex) when (ex.Message.Contains("Invalid Operation", StringComparison.OrdinalIgnoreCase) ||
                                         ex.Message.Contains("invalidOperation", StringComparison.OrdinalIgnoreCase))
        {
            throw new DriverException($"El IP Receiver Pro rechazó leer {what}: habilite en la pasarela Automation Output → Protocol " +
                                      "con tipo «Private» y verifique que el equipo esté en línea en ella.", ex);
        }
        catch (DriverException ex) when (ex.Message.Contains("networkError", StringComparison.OrdinalIgnoreCase) ||
                                         ex.Message.Contains("Device Busy", StringComparison.OrdinalIgnoreCase) ||
                                         ex.Message.Contains("respondió 503", StringComparison.OrdinalIgnoreCase))
        {
            // La pasarela contesta 503 "Device Busy / networkError" cuando el
            // equipo figura fuera de línea (no se ha registrado por ISUP/OTAP).
            throw new DriverException($"El equipo figura fuera de línea en el IP Receiver Pro y no se pudo leer {what}: " +
                                      "verifique que el panel esté registrado (ISUP/OTAP) contra la pasarela y en línea en ella.", ex);
        }
        if (string.IsNullOrWhiteSpace(text))
            throw new DriverException($"El IP Receiver Pro no entregó {what}.");
        // Un ResponseStatus en vez de datos = el equipo está fuera de línea en la pasarela o no soporta la consulta.
        if (text.Contains("\"statusCode\"") && !text.Contains("SubSysList") && !text.Contains("ZoneList"))
            HikvisionAlarmPanelDriver.EnsureOk(text, $"leer {what}");
        return text;
    }

    // ------------------------------------------------------------------
    // Órdenes (mismas rutas SecurityCP, más devIndex)
    // ------------------------------------------------------------------

    public async Task ArmAsync(AlarmConnectionInfo info, int areaNumber, AlarmArmMode mode, CancellationToken ct = default)
    {
        var (client, devIndex) = await ConnectAsync(info, ct);
        string ways = mode == AlarmArmMode.Stay ? "stay" : "away";
        string? body = await client.RequestAsync(HttpMethod.Put,
            $"/ISAPI/SecurityCP/control/arm/{AreaId(areaNumber)}?ways={ways}&format=json{Dev(devIndex)}", ct: ct, allowNotFound: false);
        HikvisionAlarmPanelDriver.EnsureOk(body, "armar el área");
    }

    public async Task DisarmAsync(AlarmConnectionInfo info, int areaNumber, CancellationToken ct = default)
    {
        var (client, devIndex) = await ConnectAsync(info, ct);
        string? body = await client.RequestAsync(HttpMethod.Put,
            $"/ISAPI/SecurityCP/control/disarm/{AreaId(areaNumber)}?format=json{Dev(devIndex)}", ct: ct, allowNotFound: false);
        HikvisionAlarmPanelDriver.EnsureOk(body, "desarmar el área");
    }

    public async Task ClearAlarmAsync(AlarmConnectionInfo info, int areaNumber, CancellationToken ct = default)
    {
        var (client, devIndex) = await ConnectAsync(info, ct);
        string? body = await client.RequestAsync(HttpMethod.Put,
            $"/ISAPI/SecurityCP/control/clearAlarm/{AreaId(areaNumber)}?format=json{Dev(devIndex)}", ct: ct, allowNotFound: false);
        HikvisionAlarmPanelDriver.EnsureOk(body, "borrar la alarma");
    }

    public async Task SetZoneBypassAsync(AlarmConnectionInfo info, int zoneNumber, bool bypassed, CancellationToken ct = default)
    {
        var (client, devIndex) = await ConnectAsync(info, ct);
        string verb = bypassed ? "bypass" : "bypassRecover";
        string json = $"{{\"List\":[{{\"id\":{zoneNumber.ToString(CultureInfo.InvariantCulture)}}}]}}";
        string? body = await client.RequestAsync(HttpMethod.Put, $"/ISAPI/SecurityCP/control/{verb}?format=json{Dev(devIndex)}", json,
            ct: ct, allowNotFound: false);
        HikvisionAlarmPanelDriver.EnsureOk(body, bypassed ? "anular la zona" : "restituir la zona");
    }

    private static string AreaId(int areaNumber) => areaNumber <= 0 ? AllAreas : areaNumber.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------
    // Eventos: suscripción multipart de la pasarela, filtrada por devIndex
    // ------------------------------------------------------------------

    public async Task<IAlarmSubscription> SubscribeEventsAsync(AlarmConnectionInfo info, Action<AlarmPanelEvent> onEvent,
        Action? onActivity = null, CancellationToken ct = default)
    {
        var (client, devIndex) = await ConnectAsync(info, ct);
        HttpResponseMessage response;
        try
        {
            response = await client.OpenStreamAsync("/ISAPI/Event/notification/subscribeDeviceMgmt?format=json", ct, HttpMethod.Post,
                "{\"SubscribeDeviceMgmt\":{\"eventMode\":\"all\",\"defenceMode\":\"all\"}}");
        }
        catch (DriverException ex) when (ex.Message.Contains("Invalid Operation", StringComparison.OrdinalIgnoreCase) ||
                                         ex.Message.Contains("invalidOperation", StringComparison.OrdinalIgnoreCase))
        {
            throw new DriverException("El IP Receiver Pro rechazó la suscripción de eventos: habilite en la pasarela Automation Output → " +
                                      "Protocol con tipo «Private».", ex);
        }
        string? boundary = response.Content.Headers.ContentType?.Parameters
            .FirstOrDefault(p => string.Equals(p.Name, "boundary", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"');
        var stream = await response.Content.ReadAsStreamAsync(ct);
        return new HikvisionAlarmPanelDriver.AlertStreamSubscription(response, stream, boundary ?? "boundary", onEvent, onActivity,
            (contentType, body) => ParseGatewayEvent(contentType, body, devIndex));
    }

    /// <summary>
    /// Traduce una parte de la suscripción. Solo cuentan las de este equipo
    /// (devIndex); el latido y la confirmación de suscripción no son eventos.
    /// </summary>
    public static AlarmPanelEvent? ParseGatewayEvent(string contentType, byte[] body, string devIndex)
    {
        if (body.Length == 0) return null;
        string text = Encoding.UTF8.GetString(body).Trim();
        if (!text.StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (HikvisionAlarmPanelDriver.TryGetPropertyIgnoreCase(root, "EventNotificationAlert", out var alert)) root = alert;
            string eventType = HikvisionAlarmPanelDriver.GetString(root, "eventType") ?? "";
            if (eventType.Length == 0 || eventType.Equals("heartBeat", StringComparison.OrdinalIgnoreCase)) return null;
            string? owner = HikvisionAlarmPanelDriver.GetString(root, "devIndex");
            if (!string.Equals(owner, devIndex, StringComparison.OrdinalIgnoreCase)) return null;

            var timestamp = DateTime.UtcNow;
            string? dateTime = HikvisionAlarmPanelDriver.GetString(root, "dateTime") ?? HikvisionAlarmPanelDriver.GetString(root, "dataTime");
            if (dateTime is not null && DateTimeOffset.TryParse(HikvisionAlarmPanelDriver.NormalizeOffset(dateTime), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed))
                timestamp = parsed.UtcDateTime;

            if (eventType.Equals("devStatusChanged", StringComparison.OrdinalIgnoreCase))
            {
                bool online = string.Equals(HikvisionAlarmPanelDriver.GetString(root, "status"), "online", StringComparison.OrdinalIgnoreCase);
                return new AlarmPanelEvent(timestamp,
                    online ? AlarmEventKind.Restore : AlarmEventKind.Trouble,
                    online ? AlarmSeverity.Info : AlarmSeverity.Warning,
                    online ? "R350" : "E350",
                    online ? "Panel en línea en el IP Receiver Pro" : "Panel sin conexión con el IP Receiver Pro",
                    null, null, null, text);
            }

            if (!eventType.Equals("CIDAlarm", StringComparison.OrdinalIgnoreCase))
                return null; // eventos de video, acceso, radar...: no son del módulo de alarmas

            if (!HikvisionAlarmPanelDriver.TryGetPropertyIgnoreCase(root, "CIDAlarm", out var cid) || cid.ValueKind != JsonValueKind.Object)
                return null;
            string? code = HikvisionAlarmPanelDriver.GetString(cid, "CIDCode");
            string? describe = HikvisionAlarmPanelDriver.GetString(cid, "CIDDescribe");
            int? area = HikvisionAlarmPanelDriver.GetInt(cid, "subSys");
            int? zone = HikvisionAlarmPanelDriver.GetInt(cid, "zoneNo");
            if (area is <= 0) area = null;
            if (zone is < 0) zone = null;

            // CIDParam = userType,userNo,zoneNo,keyboardNo,videoChanNo,dskNo,moduleAddr,userName
            string? user = null;
            if (HikvisionAlarmPanelDriver.GetString(cid, "CIDParam") is { } param)
            {
                var parts = param.Split(',');
                if (parts.Length >= 8 && parts[7].Trim() is { Length: > 0 } name && !name.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                    user = name;
                else if (parts.Length >= 2 && int.TryParse(parts[1], out int userNo) && userNo >= 0)
                    user = $"usuario {userNo}";
                if (zone is null && parts.Length >= 3 && int.TryParse(parts[2], out int z) && z >= 0) zone = z;
            }

            AlarmEventKind kind;
            AlarmSeverity severity;
            string label;
            if (HikvisionEventCodes.Translate(code) is { } hik)
                (kind, severity, label) = hik;
            else if (ContactIdCatalog.Translate(code) is { } known)
                (kind, severity, label) = known;
            else
            {
                int type = HikvisionAlarmPanelDriver.GetInt(cid, "CIDType") ?? 0;
                (kind, severity) = type switch
                {
                    1 or 2 or 3 or 4 => (AlarmEventKind.Alarm, AlarmSeverity.Critical),
                    5 => (AlarmEventKind.Trouble, AlarmSeverity.Warning),
                    _ => (AlarmEventKind.Info, AlarmSeverity.Info),
                };
                label = describe ?? $"Evento del panel (código {code})";
            }
            // Los eventos de apertura/cierre (4xx) traen el usuario, no una zona.
            if (kind is AlarmEventKind.Arm or AlarmEventKind.Disarm) zone = null;
            if (string.Equals(HikvisionAlarmPanelDriver.GetString(root, "eventState"), "inactive", StringComparison.OrdinalIgnoreCase)
                && kind == AlarmEventKind.Alarm)
            {
                kind = AlarmEventKind.Restore;
                severity = AlarmSeverity.Info;
                label += " (fin)";
            }
            return new AlarmPanelEvent(timestamp, kind, severity, code, label, area, zone, user, text);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public sealed class HikvisionIpReceiverDriverFactory : IAlarmPanelDriverFactory
{
    public string DriverKey => "hikvision-iprp";
    public string DisplayName => "Hikvision IP Receiver Pro (pasarela de paneles)";
    public int DefaultPort => 80;
    public bool DefaultHttps => false;
    public bool NeedsDeviceId => true;
    public IAlarmPanelDriver Create() => new HikvisionIpReceiverDriver();
}
