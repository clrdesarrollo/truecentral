using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
///    operation"), y una cuenta con permisos (admin). Ese interruptor lo deja
///    puesto el propio driver (<see cref="EnsureEventsEnabledAsync"/>, que
///    además se dispara solo si la suscripción es rechazada), así que una
///    receptora recién instalada no necesita que nadie entre a su web.
/// </summary>
public sealed class HikvisionIpReceiverDriver : IAlarmGatewayDriver
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
    /// Busca en la pasarela un equipo por su ID ISUP/OTAP exacto (el que el
    /// panel usa para reportar). Null si no está. Sirve para reconocer un alta
    /// que ya se hizo: la pasarela responde «addDeviceFailed» (no
    /// «deviceExist») cuando se repite un ID, así que no se puede distinguir
    /// por el error del alta.
    /// </summary>
    public static async Task<GatewayDeviceSummary?> FindGatewayDeviceAsync(AlarmConnectionInfo info, string deviceId,
        CancellationToken ct = default)
    {
        string wanted = (deviceId ?? "").Trim();
        if (wanted.Length == 0) return null;
        var devices = await ListDevicesAsync(ClientOf(info), wanted, ct);
        return devices
            .Where(d => string.Equals(d.IsupId, wanted, StringComparison.OrdinalIgnoreCase))
            .Select(d => new GatewayDeviceSummary(d.DevIndex, d.Name, d.Serial, d.AccountId, d.IsupId, d.Model, d.Version, d.Status))
            .FirstOrDefault();
    }

    // ------------------------------------------------------------------
    // Sincronización VMS ↔ receptora (IAlarmGatewayDriver)
    //
    // La receptora es la verdad de lo que funciona: si el equipo no está
    // registrado en ella, el panel no se comunica. El VMS guarda ID y clave y
    // con esto los hace cumplir: falta → se registra; está → se respeta (o se
    // reemplaza si un administrador escribió una clave nueva, porque la
    // registrada no se puede leer para compararla).
    // ------------------------------------------------------------------

    private static bool GatewaySaysOffline(string? status) =>
        status is not null && status.Contains("offline", StringComparison.OrdinalIgnoreCase);

    /// <summary>Busca por ID ISUP/OTAP exacto o, si es un uuid, por devIndex.</summary>
    private static async Task<GatewayDevice?> LookupAsync(HikvisionIsapiClient client, string wanted, CancellationToken ct)
    {
        if (Guid.TryParse(wanted, out _))
            return (await ListDevicesAsync(client, null, ct))
                .FirstOrDefault(d => string.Equals(d.DevIndex, wanted, StringComparison.OrdinalIgnoreCase));
        return (await ListDevicesAsync(client, wanted, ct))
            .FirstOrDefault(d => string.Equals(d.IsupId, wanted, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<GatewayRegistration> EnsureRegisteredAsync(AlarmConnectionInfo info, string deviceId, string? deviceKey,
        string? protocol, bool replaceIfPresent = false, string? name = null, CancellationToken ct = default)
    {
        string wanted = (deviceId ?? "").Trim();
        if (wanted.Length == 0)
            throw new DriverException("Falta el ID del equipo con el que el panel reporta a la receptora.");
        string? key = string.IsNullOrWhiteSpace(deviceKey) ? null : deviceKey.Trim();
        var existing = await LookupAsync(ClientOf(info), wanted, ct);
        string? stable = existing?.IsupId ?? (Guid.TryParse(wanted, out _) ? null : wanted);

        if (existing is not null && !(replaceIfPresent && key is not null))
        {
            bool offline = GatewaySaysOffline(existing.Status);
            return new GatewayRegistration(
                offline ? GatewayRegistrationOutcome.RegisteredOffline : GatewayRegistrationOutcome.Registered,
                existing.DevIndex, stable ?? existing.DevIndex,
                offline ? "Registrado en la receptora, pero el panel no reporta: revise en el panel el ID, la clave ISUP/OTAP " +
                          "y que llegue por red a este servidor." : null);
        }

        if (stable is null)
            return new GatewayRegistration(GatewayRegistrationOutcome.NotRegistered, null, null,
                $"El equipo '{wanted}' ya no está en la receptora y el panel se identificaba por ese uuid: edite el panel " +
                "e indique el ID ISUP/OTAP y la clave que tiene configurados.");
        if (key is null)
            return new GatewayRegistration(GatewayRegistrationOutcome.NotRegistered, null, stable,
                "El equipo no está registrado en la receptora y el sistema no tiene su clave: edite el panel y escriba la " +
                "clave ISUP/OTAP que tiene configurada para volver a registrarlo.");

        if (existing is not null)
            await DeleteGatewayDeviceAsync(info, existing.DevIndex, ct);
        string devIndex = await AddGatewayDeviceAsync(info,
            new GatewayDeviceSpec(protocol ?? "isup", stable, key, name ?? existing?.Name ?? stable, null, null, null), ct);
        ForgetResolved(info);
        return new GatewayRegistration(GatewayRegistrationOutcome.ReRegistered, devIndex, stable,
            existing is null ? "El equipo no estaba en la receptora: se registró de nuevo con la clave guardada."
                             : "Se volvió a registrar en la receptora con la clave indicada.");
    }

    public async Task UnregisterAsync(AlarmConnectionInfo info, string deviceId, CancellationToken ct = default)
    {
        string wanted = (deviceId ?? "").Trim();
        if (wanted.Length == 0) return;
        var existing = await LookupAsync(ClientOf(info), wanted, ct);
        if (existing is null) return;
        await DeleteGatewayDeviceAsync(info, existing.DevIndex, ct);
    }

    public async Task<IReadOnlyList<(string DevIndex, string? StableDeviceId, string Name, string? Status)>> ListRegisteredAsync(
        AlarmConnectionInfo info, CancellationToken ct = default)
    {
        var devices = await ListDevicesAsync(ClientOf(info), null, ct);
        return devices.Select(d => (d.DevIndex, d.IsupId, d.Name, d.Status)).ToList();
    }

    private static void ForgetResolved(AlarmConnectionInfo info)
    {
        foreach (var cached in Resolved.Keys.Where(k => k.StartsWith($"{info.Host}|{info.Port}|", StringComparison.Ordinal)).ToList())
            Resolved.TryRemove(cached, out _);
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
        // Probado contra la receptora (V2.5.0.6, 2026-09-10): cualquier símbolo
        // o espacio en la clave hace que responda badParameters/addDeviceFailed,
        // tanto en claro como cifrada. Solo letras y números pasan.
        if (key is not null && !key.All(char.IsAsciiLetterOrDigit))
            throw new DriverException("La clave del equipo solo admite letras y números, sin espacios ni símbolos: la " +
                                      "receptora rechaza cualquier otra. Cambie la clave ISUP en el panel (en el AX PRO: " +
                                      "Comunicación → ISUP) por una de 8 a 32 letras y números, y use esa misma aquí.");
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
        // El numero de abonado va SOLO si lo indicaron: probado contra el equipo,
        // el alta que funciona es la que no lo lleva. Mandarlo siempre —como
        // hace su formulario web— es justamente lo que la pasarela rechaza.
        if (!string.IsNullOrWhiteSpace(spec.AccountId)) device["accountID"] = spec.AccountId.Trim();
        if (!string.IsNullOrWhiteSpace(spec.Remark)) device["remark"] = spec.Remark.Trim();

        string keyField = protocol == "OTAP" ? "OTAPKey" : "EhomeKey";

        // Primero, la forma que se probó contra el equipo y funciona: la clave
        // tal cual. Solo si esa falla se intenta cifrada (security=1), por si
        // otra versión de la pasarela la exige así.
        try
        {
            return await PostAddDeviceAsync(info, device, "", ct);
        }
        catch (DriverException primera)
        {
            if (key is null || await BuildSecurityKeyAsync(ClientOf(info), info, ct) is not { } sec)
                throw;

            parameters[keyField] = AesCbcHex(key, sec.Key, sec.Iv);
            try
            {
                return await PostAddDeviceAsync(info, device, $"&security=1&iv={sec.Iv}", ct);
            }
            catch (DriverException)
            {
                throw primera;   // se informa el rechazo del intento normal, que es el util
            }
        }
    }

    /// <summary>Manda el alta y traduce el resultado que devuelve la pasarela.</summary>
    private static async Task<string> PostAddDeviceAsync(AlarmConnectionInfo info, Dictionary<string, object?> device,
        string extraQuery, CancellationToken ct)
    {
        string body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["DeviceInList"] = new[] { new Dictionary<string, object?> { ["Device"] = device } },
        });

        string json;
        try
        {
            json = await ManageAsync(info, "/ISAPI/ContentMgmt/DeviceMgmt/addDevice?format=json" + extraQuery,
                body, ct, "el alta de equipos");
        }
        catch (DriverException ex) when (AddFailureCode(ex.Message) is { } code)
        {
            // La pasarela también rechaza el alta con un código HTTP, y ahí el
            // mensaje genérico —«no tiene permiso»— despista: lo que explica qué
            // revisar es su subStatusCode, que viene en el cuerpo de la respuesta.
            throw new DriverException(DescribeAddFailure(code) +
                $" [clave {(extraQuery.Length > 0 ? "cifrada" : "en claro")}; respuesta: {ReceiverWords(ex.Message)}]", ex);
        }

        // La pasarela puede contestar algo que no es JSON (una pagina de error de
        // su nginx, por ejemplo). Sin esto reventaba con un 500 sin explicacion.
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new DriverException($"La pasarela respondió algo que no se pudo interpretar al agregar el equipo: {Shorten(json)}");
        }
        using (doc)
        {
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

    /// <summary>
    /// Se queda con lo que dijo la pasarela y descarta la explicación genérica
    /// del cliente HTTP, que para este caso es engañosa («no tiene permiso»).
    /// </summary>
    private static string ReceiverWords(string message)
    {
        int a = message.LastIndexOf('(');
        int b = message.LastIndexOf(')');
        return Shorten(b > a && a >= 0 ? message[(a + 1)..b] : message);
    }

    /// <summary>
    /// Busca en un mensaje de error el subStatusCode con el que la pasarela
    /// explica por qué rechazó el alta. Devuelve null si el fallo es de otra
    /// naturaleza (red, credenciales), que ya se explica solo.
    /// </summary>
    private static string? AddFailureCode(string message) =>
        new[] { "deviceExist", "monitorNodeOverLimit", "badParameters", "addDeviceFailed", "noMemory" }
            .FirstOrDefault(c => message.Contains(c, StringComparison.OrdinalIgnoreCase));

    private static string DescribeAddFailure(string? subStatusCode) => subStatusCode switch
    {
        "deviceExist" => "Ese equipo ya está agregado en la pasarela.",
        "monitorNodeOverLimit" => "La pasarela llegó al límite de equipos de su licencia.",
        "badParameters" => "La pasarela rechazó los datos del equipo: el ID admite hasta 31 letras y números y la clave " +
                           "de 8 a 32 letras y números (sin símbolos ni espacios).",
        "addDeviceFailed" => "La pasarela no pudo agregar el equipo: revise que el ID y la clave sean los que tiene " +
                             "configurados el panel (en el AX PRO: Comunicación → ISUP) y que la clave tenga de 8 a 32 " +
                             "caracteres, solo letras y números (la receptora rechaza símbolos y espacios).",
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
            response = await OpenSubscriptionAsync(client, ct);
        }
        catch (DriverException ex) when (IsProtocolDisabled(ex))
        {
            // La pasarela tiene apagada su salida de automatización: es su
            // único requisito para entregar eventos y se enciende con la misma
            // cuenta con la que ya estamos hablando. Una receptora recién
            // instalada —o una que alguien apagó desde su web— entra por acá
            // una vez y sigue de largo; si aun así no se puede, se explica.
            try { await EnablePrivateProtocolAsync(client, ct, force: false); }
            catch (AutomationOutputBusyException) { throw; }
            catch (Exception inner) { throw ProtocolDisabled(inner); }
            try { response = await OpenSubscriptionAsync(client, ct); }
            catch (DriverException retry) when (IsProtocolDisabled(retry)) { throw ProtocolDisabled(retry); }
        }
        string? boundary = response.Content.Headers.ContentType?.Parameters
            .FirstOrDefault(p => string.Equals(p.Name, "boundary", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"');
        var stream = await response.Content.ReadAsStreamAsync(ct);
        return new HikvisionAlarmPanelDriver.AlertStreamSubscription(response, stream, boundary ?? "boundary", onEvent, onActivity,
            (contentType, body) => ParseGatewayEvent(contentType, body, devIndex));
    }

    private static Task<HttpResponseMessage> OpenSubscriptionAsync(HikvisionIsapiClient client, CancellationToken ct) =>
        client.OpenStreamAsync("/ISAPI/Event/notification/subscribeDeviceMgmt?format=json", ct, HttpMethod.Post,
            "{\"SubscribeDeviceMgmt\":{\"eventMode\":\"all\",\"defenceMode\":\"all\"}}");

    /// <summary>La pasarela contestó 403 "Invalid operation": su salida de automatización está apagada.</summary>
    private static bool IsProtocolDisabled(Exception ex) =>
        ex.Message.Contains("Invalid Operation", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("invalidOperation", StringComparison.OrdinalIgnoreCase);

    private static DriverException ProtocolDisabled(Exception inner) =>
        new("El IP Receiver Pro rechazó la suscripción de eventos: habilite en la pasarela Automation Output → " +
            "Protocol con tipo «Private».", inner);

    // ------------------------------------------------------------------
    // Lo único que hay que dejar puesto en la pasarela
    // ------------------------------------------------------------------

    /// <summary>Ruta de «Automation Output → Protocol». El typo «Mangement» es del fabricante.</summary>
    private const string ProtocolParamsPath = "/ISAPI/System/ProtocolMangement/ProtocolParams?format=json";

    /// <inheritdoc />
    public Task EnsureEventsEnabledAsync(AlarmConnectionInfo info, CancellationToken ct = default) =>
        EnablePrivateProtocolAsync(ClientOf(info), ct, force: true);

    /// <summary>La salida de automatización está encendida con otro protocolo: no se pisa sola.</summary>
    private sealed class AutomationOutputBusyException(string message) : DriverException(message);

    /// <summary>
    /// Habilita la salida de automatización con protocolo «Private», que es lo
    /// que destraba la suscripción de eventos. Lee primero y solo escribe si
    /// hace falta, devolviendo el mismo documento con el resto de los
    /// parámetros intactos: la pasarela rechaza un PUT al que le falten campos
    /// que ella misma declaró. Verificado contra la V2.5.0.6: de fábrica
    /// contesta <c>{"ProtocolParams":{"deviceHeartbeatInterval":30,"enabled":false,"protocolType":"Sur-Gard"}}</c>
    /// y acepta ese mismo documento de vuelta, como <c>application/json</c>,
    /// con los dos campos cambiados (<c>statusCode 1</c>).
    /// </summary>
    /// <param name="force">
    /// true para la receptora de este servidor, que es nuestra y se configura
    /// sin preguntar. false para una pasarela ajena: si su salida ya está
    /// encendida con otro protocolo puede estar reportando a la central del
    /// cliente, así que no se le pisa y se explica qué hay que cambiar.
    /// </param>
    private static async Task EnablePrivateProtocolAsync(HikvisionIsapiClient client, CancellationToken ct, bool force)
    {
        string? current = await client.RequestAsync(HttpMethod.Get, ProtocolParamsPath, ct: ct);
        var root = current is { Length: > 0 } ? JsonNode.Parse(current) as JsonObject : null;
        var node = ProtocolNode(root);
        if (node is not null && IsTrue(node["enabled"]) &&
            string.Equals(AsText(node["protocolType"]), "Private", StringComparison.OrdinalIgnoreCase))
            return;

        if (!force && node is not null && IsTrue(node["enabled"]) && AsText(node["protocolType"]) is { Length: > 0 } inUse)
            throw new AutomationOutputBusyException(
                $"El IP Receiver Pro tiene su salida de automatización en uso con el protocolo «{inUse}», que puede estar " +
                "reportando a otra central: para recibir eventos acá debe quedar en «Private» (Automation Output → Protocol).");

        if (node is null)
        {
            // No entregó el recurso (404 o cuerpo inesperado): se manda la
            // forma que documenta la guía de la API.
            node = new JsonObject();
            root = new JsonObject { ["ProtocolParams"] = node };
        }
        node["enabled"] = true;
        node["protocolType"] = "Private";

        string? body = await client.RequestAsync(HttpMethod.Put, ProtocolParamsPath, root!.ToJsonString(),
            ct: ct, allowNotFound: false);
        HikvisionAlarmPanelDriver.EnsureOk(body, "habilitar su salida de automatización «Private»");
    }

    /// <summary>El objeto con los parámetros: la raíz, o el nodo que la envuelve.</summary>
    private static JsonObject? ProtocolNode(JsonObject? root)
    {
        if (root is null) return null;
        if (root.ContainsKey("protocolType") || root.ContainsKey("enabled")) return root;
        foreach (var (_, value) in root)
            if (value is JsonObject inner && (inner.ContainsKey("protocolType") || inner.ContainsKey("enabled")))
                return inner;
        return null;
    }

    /// <summary>true, "true" o 1: los firmwares mezclan las tres formas.</summary>
    private static bool IsTrue(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => string.Equals(node!.GetValue<string>(), "true", StringComparison.OrdinalIgnoreCase),
        JsonValueKind.Number => node!.GetValue<double>() != 0,
        _ => false,
    };

    private static string? AsText(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node?.ToString();

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
