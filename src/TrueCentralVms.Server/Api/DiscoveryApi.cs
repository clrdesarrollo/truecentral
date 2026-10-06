using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Drivers.Dahua;
using TrueCentralVms.Drivers.Hikvision;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Descubrimiento de equipos en la red local, al estilo "Online Device":
/// SADP (Hikvision, UDP 37020), DHDiscover (Dahua, UDP 37810) y WS-Discovery
/// (ONVIF, UDP 3702) corren en paralelo y el resultado se unifica por IP —
/// si un equipo responde por su protocolo de fábrica y además por ONVIF, gana
/// la entrada de fábrica (trae serie, puertos y estado de activación).
/// Por omisión solo se listan equipos de VIDEO: controles de acceso,
/// intercomunicadores, alarmas y switches se filtran. Con ?kind= se pide otra
/// familia: <c>decoders</c> (decodificadores de muro) o <c>access</c> (control
/// de acceso, y ahí solo lo que el módulo sabe manejar).
///
/// Limitación común: los sondeos son multicast/broadcast y no cruzan routers
/// ni VPN; solo se ve el segmento L2 del servidor. DHDiscover acepta además
/// un sondeo unicast dirigido (?host=IP) para equipos Dahua remotos.
/// </summary>
public static partial class DiscoveryApi
{
    public static void MapDiscoveryApi(this WebApplication app)
    {
        app.MapGet("/api/discovery/scan", async (HttpContext ctx, ILogger<Program> logger,
            Services.AuditService audit, Data.VmsDbContext db, string? host, string? kind, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out var session) is { } failure) return failure;
            // kind=decoders: solo decodificadores de muro (Hikvision DS-64/69/C10, Dahua NVD);
            // el resto de los equipos de video se omite (lo usa la página Decodificadores).
            bool decodersOnly = string.Equals(kind, "decoders", StringComparison.OrdinalIgnoreCase);
            // kind=access: solo equipos de control de acceso COMPATIBLES (los que
            // el módulo sabe administrar); lo usa la página Control de acceso.
            bool accessOnly = string.Equals(kind, "access", StringComparison.OrdinalIgnoreCase);
            // kind=intercom: solo frentes de videoportero que el módulo de
            // citofonía sabe manejar (Hikvision); lo usa la página Citofonía.
            bool intercomOnly = string.Equals(kind, "intercom", StringComparison.OrdinalIgnoreCase);

            // El panel repite el sondeo solo cada 30 s mientras la página está
            // abierta: se audita una vez por usuario cada 10 min.
            if (audit.ShouldLog($"scan:{session.UserId}", TimeSpan.FromMinutes(10)))
                await audit.LogAsync(ctx, accessOnly ? "access" : "devices", "discovery-scan",
                    detail: accessOnly
                        ? "Sondeó la red en busca de equipos de control de acceso (SADP)."
                        : intercomOnly
                        ? "Sondeó la red en busca de frentes de citofonía (SADP)."
                        : decodersOnly
                            ? "Sondeó la red en busca de decodificadores de muro (SADP, DHDiscover)."
                            : "Sondeó la red en busca de equipos de video (SADP, DHDiscover, WS-Discovery).");

            IPAddress? directed = null;
            if (!string.IsNullOrWhiteSpace(host) && !IPAddress.TryParse(host.Trim(), out directed))
                return Results.Json(new { error = "El parámetro host debe ser una dirección IP." },
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            var window = TimeSpan.FromSeconds(4);
            var sadpTask = SadpDiscovery.ScanAsync(window, logger, ct);
            var dahuaTask = DahuaDiscovery.ScanAsync(window, logger, directed, ct);
            // Cada familia se pregunta solo donde puede contestar algo útil:
            // WS-Discovery no distingue equipos de control de acceso, y ZKTeco
            // no aparece en ningún otro sondeo (habla su propio protocolo).
            // Los frentes de citofonía compatibles son todos Hikvision: basta SADP.
            var onvifTask = accessOnly || intercomOnly
                ? Task.FromResult(new List<OnvifDiscoveredDto>()) : WsDiscovery.ScanAsync(window, logger, ct);
            var knownZk = accessOnly
                ? (await db.AccessDevices.AsNoTracking().Where(d => d.DriverKey == "zkteco-tcp")
                    .Select(d => d.Host).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            var zkTask = accessOnly
                ? ZkDiscovery.ScanAsync(window, logger, directed, knownZk, ct)
                : Task.FromResult(new List<ZkDiscoveredDto>());
            await Task.WhenAll(sadpTask, dahuaTask, onvifTask, zkTask);

            // Por MAC y no por IP: los equipos de fábrica comparten la misma IP
            // (192.0.0.64 en Hikvision, 192.168.1.108 en Dahua) y con la IP de
            // clave se pisaban unos a otros y solo se listaba el último.
            var rows = new Dictionary<string, (string Ip, object Row)>(StringComparer.OrdinalIgnoreCase);
            var seenIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string ip, string mac, object row)
            {
                rows[mac.Length > 0 ? "mac:" + mac.Replace(":", "-") : "ip:" + ip] = (ip, row);
                seenIps.Add(ip);
            }

            foreach (var d in sadpTask.Result)
            {
                string? category = accessOnly ? CategorizeAccess(d.Model)
                    : intercomOnly ? CategorizeIntercom(d.Model)
                    : CategorizeHikvision(d.Model);
                if (category is null) continue;
                if (decodersOnly && category != "Decodificador") continue;
                Add(d.Ip, d.Mac, new
                {
                    d.Ip, Brand = "Hikvision",
                    // El control de acceso se administra por ISAPI (puerto HTTP), no por el SDK.
                    DriverKey = accessOnly ? "hikvision-isapi" : intercomOnly ? "hikvision-intercom" : "hikvision-netsdk",
                    d.CommandPort, d.HttpPort, d.Model, d.Serial, d.Mac, d.Activated, Category = category,
                    // Cambio de IP por SADP (POST /api/discovery/change-ip): pide la contraseña, solo si está activado.
                    CanChangeIp = d.Activated, d.SubnetMask, d.Gateway, d.Dhcp,
                    Reachable = IsInLocalSubnet(d.Ip),
                });
            }

            foreach (var d in dahuaTask.Result)
            {
                if (intercomOnly) break;               // sin driver de citofonía Dahua (VTO) todavía
                string? category = accessOnly
                    ? CategorizeDahuaAccess(d.DeviceClass, d.Model)
                    : CategorizeDahua(d.DeviceClass, d.Model);
                if (category is null) continue;
                if (decodersOnly && category != "Decodificador") continue;
                Add(d.Ip, d.Mac, new
                {
                    d.Ip, Brand = "Dahua",
                    // El control de acceso se administra por el CGI HTTP del equipo, no por el SDK.
                    DriverKey = accessOnly ? "dahua-http" : "dahua-netsdk",
                    CommandPort = d.SdkPort, d.HttpPort, d.Model, d.Serial, d.Mac,
                    // De fábrica sin inicializar: no tiene usuario, no se puede
                    // agregar hasta crearle uno (POST /api/discovery/dahua/initialize).
                    Activated = !d.Uninitialized, CanInitialize = d.Uninitialized,
                    // Si la recuperación por correo está activa, el equipo exige uno al inicializarse.
                    InitNeedsEmail = d.Uninitialized && (d.PwdResetWay & 0x02) != 0,
                    // Cambio de IP sin sesión (POST /api/discovery/change-ip): exige
                    // la contraseña del equipo, así que solo una vez inicializado.
                    CanChangeIp = d.InitStatus == 2, d.SubnetMask, d.Gateway, d.Dhcp,
                    Reachable = IsInLocalSubnet(d.Ip),
                    Category = category,
                });
            }

            foreach (var d in zkTask.Result)
            {
                if (seenIps.Contains(d.Ip)) continue;   // ya lo anunció su marca
                Add(d.Ip, d.Mac, new
                {
                    d.Ip, Brand = "ZKTeco", DriverKey = "zkteco-tcp",
                    CommandPort = d.Port, HttpPort = d.Port,
                    // Sin la clave de comunicación de fábrica el equipo no se
                    // identifica: se lista igual, con lo que se sabe de él.
                    Model = d.Model.Length > 0 ? d.Model : "ZKTeco (sin identificar)",
                    d.Serial, d.Mac, Activated = true, Category = "Terminal",
                });
            }

            foreach (var d in onvifTask.Result)
            {
                if (decodersOnly) break;               // WS-Discovery no distingue decodificadores
                if (seenIps.Contains(d.Ip)) continue; // ya visto por su protocolo de fábrica
                Add(d.Ip, "", new
                {
                    d.Ip, Brand = "ONVIF", DriverKey = "onvif",
                    CommandPort = d.HttpPort, d.HttpPort,
                    Model = d.Hardware.Length > 0 ? d.Hardware : d.Name,
                    Serial = "", Mac = "", Activated = true, Category = "Cámara",
                });
            }

            return Results.Ok(rows.OrderBy(r => r.Value.Ip, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Select(r => r.Value.Row));
        });

        // Inicializar un equipo Dahua de fábrica: crearle el usuario "admin"
        // con la contraseña elegida (lo mismo que "Initialize" en SmartPSS o el
        // ConfigTool). Va por multicast con la MAC, así que funciona aunque el
        // equipo siga en su IP de fábrica, fuera de la subred del servidor.
        app.MapPost("/api/discovery/dahua/initialize", async (HttpContext ctx, DahuaInitializeRequest request,
            ILogger<Program> logger, Services.AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;

            string mac = (request.Mac ?? "").Trim();
            string target = mac;

            // Todo intento queda en la bitácora, también los rechazados.
            async Task<IResult> Reject(int status, string error)
            {
                await audit.LogAsync(ctx, "devices", "device-initialized", targetType: "device", targetName: target,
                    detail: $"No se pudo inicializar el equipo Dahua {target}: {error}", success: false);
                return Results.Json(new { error }, statusCode: status);
            }

            if (mac.Length == 0) return await Reject(StatusCodes.Status422UnprocessableEntity, "Falta la MAC del equipo.");
            if (DahuaPasswordProblem(request.Password) is { } problem)
                return await Reject(StatusCodes.Status422UnprocessableEntity, problem);
            if (request.Password != request.ConfirmPassword)
                return await Reject(StatusCodes.Status422UnprocessableEntity, "Las contraseñas no coinciden.");
            string? email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
            if (email is not null && !EmailPattern().IsMatch(email))
                return await Reject(StatusCodes.Status422UnprocessableEntity, "El correo de recuperación no es válido.");

            // Se vuelve a preguntar al equipo en vez de confiar en lo que mandó
            // el panel: su estado pudo cambiar y el SDK exige el byPwdResetWay
            // exacto que anunció.
            var found = await DahuaDiscovery.ScanAsync(TimeSpan.FromSeconds(3), logger, ct: ct);
            var device = found.FirstOrDefault(d => string.Equals(d.Mac, mac, StringComparison.OrdinalIgnoreCase));
            if (device is null)
                return await Reject(StatusCodes.Status404NotFound,
                    "El equipo ya no responde en la red. Vuelva a buscar e intente de nuevo.");
            target = $"{device.Model} {device.Ip} ({device.Mac})";
            if (device.InitStatus == 2)
                return await Reject(StatusCodes.Status409Conflict,
                    "El equipo ya está inicializado: agréguelo con su usuario y contraseña.");
            if (device.InitStatus != 1)
                return await Reject(StatusCodes.Status422UnprocessableEntity,
                    "Este equipo es de una generación que no se inicializa: ya trae usuario de fábrica.");
            if ((device.PwdResetWay & 0x02) != 0 && email is null)
                return await Reject(StatusCodes.Status422UnprocessableEntity,
                    "Este equipo exige un correo para recuperar la contraseña.");

            var localIps = LocalAddressesFor(device.Ip);
            var result = await Task.Run(() => DahuaDeviceInitializer.Initialize(
                device.Mac, request.Password!, email, device.PwdResetWay, localIps), ct);
            if (!result.Success)
                return await Reject(StatusCodes.Status502BadGateway, result.Error ?? "error desconocido");

            await audit.LogAsync(ctx, "devices", "device-initialized", targetType: "device", targetName: target,
                detail: $"Inicializó el equipo Dahua {target}: se creó el usuario \"{DahuaDeviceInitializer.AdminUser}\"" +
                        (email is null ? "." : $" con correo de recuperación {email}."));

            logger.LogInformation("Equipo Dahua {Target} inicializado (interfaz {LocalIp}).", target, result.LocalIp ?? "por omisión");
            return Results.Ok(new
            {
                device.Ip, device.Mac, device.Model,
                Username = DahuaDeviceInitializer.AdminUser,
                // Fuera de la subred del servidor (la IP de fábrica 192.168.1.108,
                // por ejemplo) el equipo no se puede agregar hasta cambiarle la IP.
                Reachable = IsInLocalSubnet(device.Ip),
            });
        });

        MapDahuaChangeIp(app);
    }

    public sealed record DahuaInitializeRequest(string? Mac, string? Password, string? ConfirmPassword, string? Email);

    /// <param name="Brand">"Dahua" o "Hikvision".</param>
    /// <param name="Dhcp">true = el equipo toma IP y DNS del servidor DHCP (se ignoran IP, máscara, puerta y DNS).</param>
    /// <param name="Dns1">DNS preferido; vacío = no se tocan los DNS del equipo.</param>
    public sealed record ChangeIpRequest(string? Brand, string? Mac, bool Dhcp, string? Ip, string? Mask,
        string? Gateway, string? Dns1, string? Dns2, string? Username, string? Password, bool SyncTime = false);

    /// <summary>
    /// Cambio de red de un equipo sin iniciar sesión, por multicast y MAC: Dahua
    /// con CLIENT_ModifyDevice ("Change IP" de SmartPSS) y Hikvision con el SDK
    /// de SADP (SADP Tool). IP fija o DHCP; los DNS, que ninguno de los dos
    /// protocolos lleva, se aplican después por la API web del equipo.
    /// </summary>
    private static void MapDahuaChangeIp(WebApplication app)
    {
        // Redes de este servidor: el panel las usa para proponer máscara y
        // puerta de enlace al cambiar la IP de un equipo.
        app.MapGet("/api/discovery/local-networks", (HttpContext ctx) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var networks = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                            n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .SelectMany(n =>
                {
                    var props = n.GetIPProperties();
                    string gateway = props.GatewayAddresses
                        .Select(g => g.Address)
                        .FirstOrDefault(g => g.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                                             !g.Equals(IPAddress.Any))?.ToString() ?? "";
                    return props.UnicastAddresses
                        .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        .Select(a => new
                        {
                            Address = a.Address.ToString(), Mask = a.IPv4Mask.ToString(), Gateway = gateway,
                            Interface = n.Name,
                        });
                })
                // Primero las que tienen puerta de enlace: los adaptadores
                // virtuales (VMware, Hyper-V) no la tienen y casi nunca son la red de las cámaras.
                .OrderByDescending(n => n.Gateway.Length > 0)
                .ToList();
            return Results.Ok(networks);
        });

        app.MapPost("/api/discovery/change-ip", async (HttpContext ctx, ChangeIpRequest request,
            ILogger<Program> logger, Services.AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;

            bool hikvision = string.Equals(request.Brand, "Hikvision", StringComparison.OrdinalIgnoreCase);
            bool dahua = string.Equals(request.Brand, "Dahua", StringComparison.OrdinalIgnoreCase);
            string brand = hikvision ? "Hikvision" : "Dahua";
            bool dhcp = request.Dhcp;
            string mac = (request.Mac ?? "").Trim();
            string target = mac;
            string newIp = (request.Ip ?? "").Trim(), mask = (request.Mask ?? "").Trim(),
                   gateway = (request.Gateway ?? "").Trim();
            string dns1 = (request.Dns1 ?? "").Trim(), dns2 = (request.Dns2 ?? "").Trim();
            string username = string.IsNullOrWhiteSpace(request.Username) ? DahuaDeviceInitializer.AdminUser
                : request.Username.Trim();
            string wanted = dhcp ? "DHCP" : $"IP fija {newIp}";

            async Task<IResult> Reject(int status, string error)
            {
                await audit.LogAsync(ctx, "devices", "device-ip-changed", targetType: "device", targetName: target,
                    detail: $"No se pudo cambiar la red del equipo {brand} {target} ({wanted}): {error}", success: false);
                return Results.Json(new { error }, statusCode: status);
            }
            const int Invalid = StatusCodes.Status422UnprocessableEntity;

            if (!hikvision && !dahua) return await Reject(Invalid, "Solo se puede cambiar la IP de equipos Dahua y Hikvision.");
            if (mac.Length == 0) return await Reject(Invalid, "Falta la MAC del equipo.");
            if (string.IsNullOrEmpty(request.Password)) return await Reject(Invalid, "Falta la contraseña del equipo.");

            IPAddress ipAddress = IPAddress.None;
            if (!dhcp)
            {
                if (!TryParseV4(newIp, out ipAddress) || !TryParseV4(mask, out var maskAddress))
                    return await Reject(Invalid, "La IP y la máscara deben ser direcciones IPv4 (por ejemplo 192.168.10.60 y 255.255.255.0).");
                if (!IsValidMask(maskAddress))
                    return await Reject(Invalid, "La máscara de subred no es válida.");
                if (IsNetworkOrBroadcast(ipAddress, maskAddress))
                    return await Reject(Invalid, "Esa IP es la dirección de red o de difusión de la subred: elija otra.");
                if (gateway.Length > 0)
                {
                    if (!TryParseV4(gateway, out var gatewayAddress))
                        return await Reject(Invalid, "La puerta de enlace debe ser una dirección IPv4.");
                    if (!SameSubnet(ipAddress, gatewayAddress, maskAddress))
                        return await Reject(Invalid, "La puerta de enlace tiene que estar en la misma subred que la IP nueva.");
                    if (gatewayAddress.Equals(ipAddress))
                        return await Reject(Invalid, "La IP nueva no puede ser igual a la puerta de enlace.");
                }
                if (dns1.Length > 0 && !TryParseV4(dns1, out _))
                    return await Reject(Invalid, "El DNS preferido debe ser una dirección IPv4.");
                if (dns2.Length > 0 && !TryParseV4(dns2, out _))
                    return await Reject(Invalid, "El DNS alternativo debe ser una dirección IPv4.");
                if (dns2.Length > 0 && dns1.Length == 0)
                    return await Reject(Invalid, "Indique primero el DNS preferido.");
            }

            // Estado actual del equipo, desde la búsqueda de su propia marca.
            string oldIp, model;
            int httpPort;
            List<string> ipsInUse;
            DahuaSdkSearchResult? dahuaDevice = null;
            if (dahua)
            {
                // La búsqueda del SDK (no DHDiscover) entrega la estructura firmada
                // que el cambio de IP le tiene que devolver al equipo.
                var localIps = LocalIPv4().Select(l => l.Address.ToString()).ToList();
                var found = await DahuaNetworkSetup.SearchAsync(localIps, TimeSpan.FromSeconds(3), ct);
                dahuaDevice = found.FirstOrDefault(d => string.Equals(d.Mac, mac, StringComparison.OrdinalIgnoreCase));
                if (dahuaDevice is null)
                    return await Reject(StatusCodes.Status404NotFound,
                        "El equipo ya no responde en la red. Vuelva a buscar e intente de nuevo.");
                if ((dahuaDevice.InitStatus & 0x03) == 1)
                    return await Reject(StatusCodes.Status409Conflict,
                        "El equipo está sin inicializar: inicialícelo primero (la contraseña que elija es la que pide este cambio).");
                (oldIp, model, httpPort) = (dahuaDevice.Ip, dahuaDevice.Model, dahuaDevice.HttpPort);
                ipsInUse = found.Select(d => d.Ip).ToList();
            }
            else
            {
                var found = await SadpDiscovery.ScanAsync(TimeSpan.FromSeconds(3), logger, ct);
                var device = found.FirstOrDefault(d => HikvisionSadp.NormalizeMac(d.Mac) == HikvisionSadp.NormalizeMac(mac));
                if (device is null)
                    return await Reject(StatusCodes.Status404NotFound,
                        "El equipo ya no responde en la red. Vuelva a buscar e intente de nuevo.");
                if (!device.Activated)
                    return await Reject(StatusCodes.Status409Conflict, "El equipo no está activado: actívelo antes de cambiarle la IP.");
                (oldIp, model, httpPort) = (device.Ip, device.Model, device.HttpPort);
                ipsInUse = found.Select(d => d.Ip).ToList();
            }
            target = $"{model} {oldIp} ({mac})";

            if (!dhcp && newIp != oldIp)
            {
                // Una IP repetida dejaría a dos equipos peleándose la misma
                // dirección: se rechaza si algo ya responde en ella.
                bool inUse = ipsInUse.Contains(newIp);
                if (!inUse)
                {
                    try
                    {
                        using var ping = new System.Net.NetworkInformation.Ping();
                        inUse = (await ping.SendPingAsync(ipAddress, 1000)).Status ==
                                System.Net.NetworkInformation.IPStatus.Success;
                    }
                    catch (System.Net.NetworkInformation.PingException) { /* sin ruta: no se puede comprobar */ }
                }
                if (inUse)
                    return await Reject(StatusCodes.Status409Conflict,
                        $"La IP {newIp} ya está en uso por otro equipo de la red. Elija otra.");
            }

            string? changeError;
            if (dahua)
            {
                var result = await DahuaNetworkSetup.ChangeIpAsync(dahuaDevice!, dhcp, newIp, mask, gateway, username,
                    request.Password!, ct);
                changeError = result.Success ? null : result.Error ?? "error desconocido";
            }
            else
            {
                var result = await HikvisionSadp.ChangeNetworkAsync(mac, request.Password!, dhcp, newIp, mask, gateway, ct);
                changeError = result.Success ? null : result.Error ?? "error desconocido";
            }
            if (changeError is not null)
                return await Reject(StatusCodes.Status502BadGateway, changeError);

            await audit.LogAsync(ctx, "devices", "device-ip-changed", targetType: "device", targetName: target,
                detail: dhcp
                    ? $"Pasó el equipo {brand} {target} a DHCP."
                    : $"Cambió la IP del equipo {brand} {target}: {oldIp} → {newIp}, máscara {mask}" +
                      (gateway.Length > 0 ? $", puerta de enlace {gateway}." : ", sin puerta de enlace."));
            logger.LogInformation("Red del equipo {Brand} {Target} cambiada ({Wanted}).", brand, target, wanted);

            // El equipo tarda unos segundos en tomar la red nueva; se confirma
            // preguntándole de nuevo (con DHCP, así se sabe también qué IP le tocó).
            string? finalIp = null;
            for (int attempt = 0; attempt < 4 && finalIp is null; attempt++)
            {
                string? seen = dahua
                    ? (await DahuaDiscovery.ScanAsync(TimeSpan.FromSeconds(3), logger, dhcp ? null : ipAddress, ct))
                        .FirstOrDefault(x => string.Equals(x.Mac, mac, StringComparison.OrdinalIgnoreCase))?.Ip
                    : (await SadpDiscovery.ScanAsync(TimeSpan.FromSeconds(3), logger, ct))
                        .FirstOrDefault(x => HikvisionSadp.NormalizeMac(x.Mac) == HikvisionSadp.NormalizeMac(mac))?.Ip;
                if (seen is not null && (dhcp ? seen != oldIp || attempt >= 2 : seen == newIp)) finalIp = seen;
            }
            bool confirmed = finalIp is not null;
            string ip = finalIp ?? (dhcp ? oldIp : newIp);
            bool reachable = IsInLocalSubnet(ip);

            // Los DNS no viajan en el cambio por multicast: se aplican por la
            // API web del equipo, ya en su IP nueva. Con DHCP los entrega el servidor DHCP.
            bool dnsApplied = false;
            string? dnsError = null;
            if (!dhcp && dns1.Length > 0)
            {
                if (!reachable)
                    dnsError = "El equipo no quedó en la red de este servidor: configure los DNS desde el propio equipo.";
                else
                {
                    dnsError = await DeviceWebSetup.ApplyDnsAsync(brand, ip, httpPort > 0 ? httpPort : 80, username,
                        request.Password!, dns1, dns2, logger, ct);
                    dnsApplied = dnsError is null;
                }
                await audit.LogAsync(ctx, "devices", "device-dns-changed", targetType: "device", targetName: target,
                    detail: dnsApplied
                        ? $"Configuró los DNS del equipo {brand} {target}: {dns1}{(dns2.Length > 0 ? ", " + dns2 : "")}."
                        : $"No se pudieron configurar los DNS del equipo {brand} {target}: {dnsError}",
                    success: dnsApplied);
            }

            // Parámetros básicos: fecha, hora y zona horaria iguales a las del
            // servidor (el equipo de fábrica arranca con otra zona y otra hora).
            bool timeSynced = false;
            string? timeError = null;
            if (request.SyncTime)
            {
                if (!reachable || !confirmed)
                    timeError = "El equipo no quedó accesible desde este servidor: ajuste la fecha y la hora desde el propio equipo.";
                else
                {
                    timeError = await DeviceWebSetup.SyncTimeAsync(brand, ip, httpPort > 0 ? httpPort : 80, username,
                        request.Password!, logger, ct);
                    timeSynced = timeError is null;
                }
                await audit.LogAsync(ctx, "devices", "device-time-synced", targetType: "device", targetName: target,
                    detail: timeSynced
                        ? $"Sincronizó la fecha, la hora y la zona horaria del equipo {brand} {target} con el servidor " +
                          $"({TimeZoneInfo.Local.DisplayName})."
                        : $"No se pudo sincronizar la fecha y la hora del equipo {brand} {target}: {timeError}",
                    success: timeSynced);
            }

            return Results.Ok(new
            {
                Ip = ip, Mac = mac, Model = model, Username = username, Dhcp = dhcp,
                TimeSynced = timeSynced, TimeError = timeError,
                Confirmed = confirmed, Reachable = reachable, DnsApplied = dnsApplied, DnsError = dnsError,
            });
        });
    }

    private static bool TryParseV4(string text, out IPAddress address) =>
        IPAddress.TryParse(text, out address!) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && text.Count(c => c == '.') == 3;

    /// <summary>Máscara contigua (unos seguidos de ceros), entre /8 y /30.</summary>
    private static bool IsValidMask(IPAddress mask)
    {
        uint value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(mask.GetAddressBytes());
        uint inverted = ~value;
        int prefix = System.Numerics.BitOperations.PopCount(value);
        return (inverted & (inverted + 1)) == 0 && prefix is >= 8 and <= 30;
    }

    private static bool IsNetworkOrBroadcast(IPAddress ip, IPAddress mask)
    {
        uint a = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        uint m = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(mask.GetAddressBytes());
        return (a & ~m) == 0 || (a & ~m) == ~m;
    }

    /// <summary>
    /// Política de contraseñas de los equipos Dahua: 8 a 32 caracteres, al
    /// menos dos tipos entre mayúsculas, minúsculas, números y símbolos, y sin
    /// ' " ; : &amp;. Se revisa antes para dar un mensaje claro en vez del
    /// "contraseña ilegal" genérico del equipo.
    /// </summary>
    internal static string? DahuaPasswordProblem(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8 || password.Length > 32)
            return "La contraseña debe tener entre 8 y 32 caracteres.";
        if (password.IndexOfAny(['\'', '"', ';', ':', '&', ' ']) >= 0)
            return "La contraseña no puede llevar espacios ni los caracteres ' \" ; : &.";
        int kinds = (password.Any(char.IsUpper) ? 1 : 0) + (password.Any(char.IsLower) ? 1 : 0)
                  + (password.Any(char.IsDigit) ? 1 : 0) + (password.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
        return kinds < 2
            ? "La contraseña debe combinar al menos dos tipos de caracteres: mayúsculas, minúsculas, números o símbolos."
            : null;
    }

    private static List<(IPAddress Address, IPAddress Mask)> LocalIPv4() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                        n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(a => (a.Address, a.IPv4Mask))
            .ToList();

    private static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        byte[] x = a.GetAddressBytes(), y = b.GetAddressBytes(), m = mask.GetAddressBytes();
        for (int i = 0; i < 4; i++)
            if ((x[i] & m[i]) != (y[i] & m[i])) return false;
        return true;
    }

    private static bool IsInLocalSubnet(string ip) =>
        IPAddress.TryParse(ip, out var address) && LocalIPv4().Any(l => SameSubnet(l.Address, address, l.Mask));

    /// <summary>Interfaces locales a probar para el multicast: primero la de la subred del equipo, si hay.</summary>
    private static List<string> LocalAddressesFor(string deviceIp)
    {
        var locals = LocalIPv4();
        IPAddress.TryParse(deviceIp, out var device);
        return locals
            .OrderByDescending(l => device is not null && SameSubnet(l.Address, device, l.Mask))
            .Select(l => l.Address.ToString())
            .ToList();
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    /// <summary>
    /// Etiqueta del equipo de control de acceso Dahua; null = no lo es o el
    /// módulo todavía no lo maneja. La clase que anuncia DHDiscover sirve de
    /// respaldo cuando el modelo llega vacío.
    /// </summary>
    private static string? CategorizeDahuaAccess(string deviceClass, string model) =>
        (DahuaAccessDriver.ClassifyModel(model) ?? DahuaAccessDriver.ClassifyModel(deviceClass)) switch
        {
            AccessDeviceKind.Terminal => "Terminal",
            AccessDeviceKind.Controller => "Controladora",
            AccessDeviceKind.Turnstile => "Torniquete",
            _ => null,
        };

    /// <summary>
    /// Frentes de videoportero Hikvision que maneja el módulo de citofonía:
    /// DS-KD (modulares), DS-KV (villa) y DS-KB (frentes de puerta). Los
    /// monitores interiores (DS-KH) no son frentes y quedan fuera.
    /// </summary>
    private static string? CategorizeIntercom(string model)
    {
        string m = (model ?? "").Trim().ToUpperInvariant();
        return m.StartsWith("DS-KD") || m.StartsWith("DS-KV") || m.StartsWith("DS-KB") || m.StartsWith("IDS-KD")
            ? "Frente" : null;
    }

    /// <summary>
    /// Etiqueta del equipo de control de acceso; null = no es de control de
    /// acceso o el módulo todavía no lo maneja (intercomunicación y cerraduras
    /// autónomas): el administrador muestra SOLO lo compatible para no ofrecer
    /// altas que fallarían.
    /// </summary>
    private static string? CategorizeAccess(string model) => HikvisionAccessDriver.ClassifyModel(model) switch
    {
        AccessDeviceKind.Terminal => "Terminal",
        AccessDeviceKind.Controller => "Controladora",
        AccessDeviceKind.Turnstile => "Torniquete",
        _ => null,
    };

    /// <summary>
    /// Clasifica por el modelo Hikvision; null = no es un equipo de video (se
    /// oculta en este administrador). Prefijos: DS-K* control de acceso e
    /// intercomunicación, DS-P*/AX alarmas, DS-3* switches, DS-2* cámaras,
    /// *NI-* NVR, H(Q|U|G|T)HI DVR Turbo, DS-64/69 y DS-C10 decodificadores/controladores de muro.
    /// </summary>
    private static string? CategorizeHikvision(string model)
    {
        string m = (model ?? "").Trim().ToUpperInvariant();
        if (m.Length == 0) return "Otro";

        if (m.StartsWith("DS-K") || m.StartsWith("IDS-K")) return null;                   // control de acceso / intercom
        if (m.StartsWith("DS-P") || m.StartsWith("AX ") || m.StartsWith("AX-")) return null; // alarmas
        if (m.StartsWith("DS-3")) return null;                                            // switches
        if (m.StartsWith("DS-1")) return null;                                            // teclados/accesorios

        if (m.Contains("NVR") || m.Contains("NI-") || NvrSeries().IsMatch(m)) return "NVR";
        if (DvrTurbo().IsMatch(m) || m.Contains("DVR") || DvrSeries().IsMatch(m)) return "DVR";
        if (Decoder().IsMatch(m)) return "Decodificador";
        if (m.StartsWith("DS-2") || m.StartsWith("IDS-2") || m.StartsWith("IPC")) return "Cámara";
        return "Otro";
    }

    /// <summary>
    /// Clasifica por la clase que anuncia DHDiscover (con el modelo como
    /// respaldo); null = no es un equipo de video. Clases Dahua: IPC/SD
    /// cámaras (SD = domo PTZ), NVR/EVS grabadores IP, DVR/HCVR/XVR híbridos,
    /// NVD decodificadores, VT* intercomunicación, AS* control de acceso,
    /// AR* alarmas.
    /// </summary>
    private static string? CategorizeDahua(string deviceClass, string model)
    {
        string c = (deviceClass ?? "").Trim().ToUpperInvariant();
        string m = (model ?? "").Trim().ToUpperInvariant();

        if (c.StartsWith("VT") || m.StartsWith("VT")) return null;   // intercomunicación
        if (c.StartsWith("AS") || m.StartsWith("ASI") || m.StartsWith("ASA")) return null; // control de acceso
        if (c.StartsWith("AR")) return null;                         // alarmas

        if (c is "IPC" or "SD" or "ITC") return "Cámara";   // ITC = cámaras de tránsito/LPR
        if (c is "NVR" or "EVS") return "NVR";
        if (c is "DVR" or "HCVR" or "XVR" or "MCVR") return "DVR";
        if (c is "NVD") return "Decodificador";

        if (m.Contains("IPC") || m.Contains("-SD")) return "Cámara";
        if (m.Contains("NVR")) return "NVR";
        if (m.Contains("XVR") || m.Contains("HCVR") || m.Contains("DVR")) return "DVR";
        return "Otro";
    }

    [GeneratedRegex(@"^I?DS-(96|95|77|76)\d")]
    private static partial Regex NvrSeries();

    [GeneratedRegex(@"H[QUGT]HI")]
    private static partial Regex DvrTurbo();

    [GeneratedRegex(@"^I?DS-7[123]\d")]
    private static partial Regex DvrSeries();

    [GeneratedRegex(@"^DS-(6[49]\d|C1\d)")]
    private static partial Regex Decoder();
}
