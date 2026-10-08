using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Core.Domain;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// API del módulo Paneles de cerco eléctrico: mantenedor (administrador), estado,
/// órdenes (armar/desarmar/silenciar/zona, cualquier usuario con sesión, auditadas)
/// e historial de eventos. La conexión con el equipo la mantiene el
/// <see cref="CercoReceiverService"/> en su puerto propio; el estado y los eventos
/// en vivo llegan empujados por el hub (<see cref="VmsHubContract.CercoPanelStateChanged"/>
/// y <see cref="VmsHubContract.CercoEventReceived"/>).
///
/// Modelo tipo ISUP: al crear (o rotar) se genera un DeviceId de 6 dígitos y un
/// código de enrolamiento de 8 dígitos que se muestran UNA sola vez; el instalador
/// los carga en el equipo. En la primera conexión ambos lados derivan la PSK del
/// código (ver <see cref="CercoReceiverService"/>); la PSK se guarda cifrada y nunca
/// se expone.
/// </summary>
public static class CercoApi
{
    private const int MaxTake = 500;
    private static readonly TimeSpan EnrollValidity = TimeSpan.FromHours(48);

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    /// <summary>ID de equipo: 6 dígitos (100000..999999), fácil de dictar y teclear en terreno.</summary>
    private static string NewDeviceId() =>
        RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string NewEnrollCode() =>
        RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);

    private static CercoPanelCredentialsDto GenerateCredentials(CercoPanel panel, CredentialProtector protector, IConfiguration config, HttpContext ctx)
    {
        string code = NewEnrollCode();
        panel.EnrollCodeCiphertext = protector.ProtectBytes(System.Text.Encoding.ASCII.GetBytes(code));
        panel.EnrollExpiresAt = DateTime.UtcNow + EnrollValidity;
        panel.EnrollAttempts = 0;
        panel.Enrolled = false;
        panel.PskCiphertext = [];               // la PSK anterior deja de valer
        int port = config.GetValue("Cerco:Receiver:Port", 5092);
        string? pub = config.GetValue<string?>("Cerco:Receiver:PublicUrl", null);
        // El panel se conecta desde OTRO equipo, así que "localhost"/127.0.0.1 (que es
        // el host cuando el admin entra al panel web local) no sirve: se reemplaza por
        // la IP LAN real del servidor. Config Cerco:Receiver:PublicUrl la fija a mano.
        string host = ctx.Request.Host.Host;
        if (string.IsNullOrEmpty(host) || host is "localhost" or "127.0.0.1" || host == "::1")
            host = ServerLanIp() ?? host;
        string hint = !string.IsNullOrWhiteSpace(pub) ? pub! : $"ws://{host}:{port}/panel";
        return new CercoPanelCredentialsDto(panel.DeviceId, code, hint, panel.EnrollExpiresAt.Value);
    }

    /// <summary>Primera IPv4 LAN del servidor (para sugerir la URL WS a los paneles).</summary>
    private static string? ServerLanIp()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                            && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                            && !System.Net.IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .FirstOrDefault(ip => !ip.StartsWith("169.254."));
        }
        catch { return null; }
    }

    private const int MaxCsvRows = 100_000;

    /// <summary>Filtros comunes del historial y la exportación.</summary>
    private static IQueryable<CercoEvent> FilterEvents(VmsDbContext db, int? panelId, CercoEventKind? kind, DateTime? from, DateTime? to)
    {
        var q = db.CercoEvents.AsNoTracking().AsQueryable();
        if (panelId is int pid) q = q.Where(e => e.CercoPanelId == pid);
        if (kind is { } k) q = q.Where(e => e.Kind == k);
        // Las fechas llegan en hora local del servidor (datetime-local del panel web); se guarda UTC.
        if (from is { } f) q = q.Where(e => e.ReceivedAt >= DateTime.SpecifyKind(f, DateTimeKind.Local).ToUniversalTime());
        if (to is { } t) q = q.Where(e => e.ReceivedAt <= DateTime.SpecifyKind(t, DateTimeKind.Local).ToUniversalTime());
        return q;
    }

    private static string SeverityLabel(CercoSeverity s) =>
        s switch { CercoSeverity.Critical => "Crítico", CercoSeverity.Warning => "Advertencia", _ => "Información" };

    /// <summary>Campo CSV con ";" como separador: comillas si hace falta y sin inyección de fórmulas.</summary>
    private static string Csv(string? value)
    {
        string v = value ?? "";
        if (v.Length > 0 && "=+-@".Contains(v[0])) v = "'" + v;
        return v.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    /// <summary>Alcance por ubicación: solo eventos de paneles de sus ubicaciones (en la consulta).</summary>
    private static IQueryable<CercoEvent> InScope(VmsDbContext db, IQueryable<CercoEvent> query, UserScope scope)
    {
        if (!scope.FiltersView) return query;
        var allowed = scope.Locations.ToList();
        return query.Where(e => db.CercoPanels.Any(p => p.Id == e.CercoPanelId && p.LocationId != null
                                                        && allowed.Contains(p.LocationId.Value)));
    }

    public static void MapCercoApi(this WebApplication app)
    {
        // -------- listar --------
        app.MapGet("/api/cerco/panels", async (HttpContext ctx, VmsDbContext db, CercoConnectionManager conns, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var panels = await db.CercoPanels.Include(p => p.Zones).AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
            return Results.Ok(panels.Where(p => scope.CanView(p.LocationId))
                .Select(p => CercoMapper.ToDto(p, conns.IsConnected(p.DeviceId))).ToList());
        });

        app.MapGet("/api/cerco/panels/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, CercoConnectionManager conns, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireUser(ctx, out var session) is { } failure) return failure;
            var panel = await db.CercoPanels.Include(p => p.Zones).AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            return panel is null || !(await ctx.ScopeAsync(session)).CanView(panel.LocationId)
                ? Results.NotFound()
                : Results.Ok(CercoMapper.ToDto(panel, conns.IsConnected(panel.DeviceId)));
        });

        // -------- crear (genera ID + PSK) --------
        app.MapPost("/api/cerco/panels", async (HttpContext ctx, CercoPanelUpsertDto request, VmsDbContext db,
            CredentialProtector protector, IConfiguration config, ScopedHub hub, CercoConnectionManager conns,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.CercoConfigure, out _) is { } failure) return failure;
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
                return Error("El nombre es obligatorio (máximo 128 caracteres).");
            var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, null, ct);
            if (locationError is not null) return Error(locationError, StatusCodes.Status404NotFound);

            var panel = new CercoPanel
            {
                Name = request.Name.Trim(),
                LocationId = locationId,
                Enabled = request.Enabled,
                DeviceId = NewDeviceId(),
                Status = CercoPanelStatus.Unknown,
            };
            var creds = GenerateCredentials(panel, protector, config, ctx);
            db.CercoPanels.Add(panel);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                panel.DeviceId = NewDeviceId();
                creds = creds with { DeviceId = panel.DeviceId };
                await db.SaveChangesAsync(ct);
            }
            await audit.LogAsync(ctx, "cerco", "panel-created", targetType: "cerco-panel",
                targetId: panel.Id.ToString(), targetName: panel.Name,
                detail: $"Creó el panel de cerco '{panel.Name}' ({panel.DeviceId})" +
                        (panel.LocationId is null ? "." : $" en {EquipmentLocation.Describe(panel.LocationId)}."),
                data: new { panel.DeviceId, panel.LocationId });
            // Antes de avisar: el alcance de cada operador tiene que conocer el panel nuevo
            // (y, si nace ubicado, los puestos releen lo que pueden operar).
            if (panel.LocationId is null) EquipmentLocation.Changed(ctx);
            else await EquipmentLocation.MovedAsync(ctx, ct);
            await hub.SendAsync(VmsHubContract.CercoPanelStateChanged, CercoMapper.ToDto(panel, false), s => s.CanViewCerco(panel.Id), ct);
            // Las credenciales se devuelven UNA vez, junto al panel creado.
            return Results.Ok(new { panel = CercoMapper.ToDto(panel, false), credentials = creds });
        });

        // -------- editar --------
        app.MapPut("/api/cerco/panels/{id:int}", async (HttpContext ctx, int id, CercoPanelUpsertDto request, VmsDbContext db,
            ScopedHub hub, CercoConnectionManager conns, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.CercoConfigure, out _) is { } failure) return failure;
            var panel = await db.CercoPanels.Include(p => p.Zones).FirstOrDefaultAsync(p => p.Id == id, ct);
            if (panel is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
                return Error("El nombre es obligatorio (máximo 128 caracteres).");
            var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, panel.LocationId, ct);
            if (locationError is not null) return Error(locationError, StatusCodes.Status404NotFound);

            var changes = new List<string>();
            if (panel.Name != request.Name.Trim()) changes.Add($"nombre '{panel.Name}' → '{request.Name.Trim()}'");
            if (panel.Enabled != request.Enabled) changes.Add(request.Enabled ? "activado" : "desactivado");
            int? previousLocation = panel.LocationId;
            if (locationId != previousLocation)
                changes.Add(EquipmentLocation.Change(previousLocation, locationId));
            panel.Name = request.Name.Trim();
            panel.LocationId = locationId;
            panel.Enabled = request.Enabled;
            panel.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "cerco", "panel-updated", targetType: "cerco-panel",
                targetId: panel.Id.ToString(), targetName: panel.Name,
                detail: $"Editó el panel de cerco '{panel.Name}': " + (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios."));
            if (locationId != previousLocation) await EquipmentLocation.MovedAsync(ctx, ct);
            await hub.SendAsync(VmsHubContract.CercoPanelStateChanged, CercoMapper.ToDto(panel, conns.IsConnected(panel.DeviceId)), s => s.CanViewCerco(panel.Id), ct);
            return Results.Ok(CercoMapper.ToDto(panel, conns.IsConnected(panel.DeviceId)));
        });

        // -------- eliminar --------
        app.MapDelete("/api/cerco/panels/{id:int}", async (HttpContext ctx, int id, VmsDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.CercoConfigure, out _) is { } failure) return failure;
            var panel = await db.CercoPanels.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (panel is null) return Results.NotFound();
            db.CercoPanels.Remove(panel);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "cerco", "panel-deleted", targetType: "cerco-panel",
                targetId: id.ToString(), targetName: panel.Name,
                detail: $"Eliminó el panel de cerco '{panel.Name}' ({panel.DeviceId}).");
            return Results.NoContent();
        });

        // -------- rotar clave: nuevo código de enrolamiento (se muestra una vez) --------
        app.MapPost("/api/cerco/panels/{id:int}/rotate-key", async (HttpContext ctx, int id, VmsDbContext db,
            CredentialProtector protector, IConfiguration config, AuditService audit, ScopedHub hub,
            CercoConnectionManager conns, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.CercoConfigure, out _) is { } failure) return failure;
            var panel = await db.CercoPanels.Include(p => p.Zones).FirstOrDefaultAsync(p => p.Id == id, ct);
            if (panel is null) return Results.NotFound();
            var creds = GenerateCredentials(panel, protector, config, ctx);
            panel.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "cerco", "panel-key-rotated", targetType: "cerco-panel",
                targetId: panel.Id.ToString(), targetName: panel.Name,
                detail: $"Rotó la clave del panel de cerco '{panel.Name}'. Hay que re-enrolar el equipo con el código nuevo.");
            await hub.SendAsync(VmsHubContract.CercoPanelStateChanged, CercoMapper.ToDto(panel, conns.IsConnected(panel.DeviceId)), s => s.CanViewCerco(panel.Id), ct);
            return Results.Ok(creds);
        });

        // -------- mantenimiento del equipo (administrador) --------
        app.MapPost("/api/cerco/panels/{id:int}/reboot", (HttpContext ctx, int id) =>
            Command(ctx, id, "reboot", null, "reinició", admin: true));
        // Restaurar de fábrica: el panel borra WiFi/servidor/PSK/contraseña y vuelve al portal
        // SoftAP (conserva ajustes del cerco y controles RF). Hay que re-provisionarlo.
        app.MapPost("/api/cerco/panels/{id:int}/factory", (HttpContext ctx, int id) =>
            Command(ctx, id, "factory", null, "restauró de fábrica", admin: true));

        // -------- actualización de firmware (OTA, administrador) --------
        // 1:1: el .bin se valida, se publica con un token de un uso en el servidor de
        // firmware (puerto propio, 5093) y se le envía SOLO a este panel la orden "ota"
        // firmada con url, tamaño y SHA-256. El avance llega por el hub (CercoOtaProgress)
        // y el resultado final, como evento FirmwareUpdated al reconectar con la versión nueva.
        app.MapPost("/api/cerco/panels/{id:int}/firmware", async (HttpContext ctx, int id, IFormFile file, VmsDbContext db,
            CercoConnectionManager conns, CercoFirmwareService firmware, IConfiguration config, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.CercoConfigure, out var session) is { } failure) return failure;
            var panel = await db.CercoPanels.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            if (panel is null) return Results.NotFound();
            if (!firmware.Running)
                return Error($"El servidor de firmware (puerto {config.GetValue("Cerco:Firmware:Port", 5093)}) no está activo. Revise Cerco:Firmware en la configuración.", StatusCodes.Status503ServiceUnavailable);
            var conn = conns.Get(panel.DeviceId);
            if (conn is null) return Error("El panel no está conectado.", StatusCodes.Status409Conflict);
            if (panel.Armed || panel.Arming) return Error("Desarme el panel antes de actualizar el firmware.", StatusCodes.Status409Conflict);
            if (file.Length > CercoFirmwareImage.MaxBytes) return Error("El archivo es demasiado grande para el panel (máx. 1 MB).");

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var image = CercoFirmwareImage.TryParse(ms.ToArray(), out var invalid);
            if (image is null) return Error(invalid);

            string? host = config.GetValue<string?>("Cerco:Firmware:PublicHost", null);
            if (string.IsNullOrWhiteSpace(host)) host = conn.ServerAddress?.ToString();
            if (string.IsNullOrWhiteSpace(host))
                return Error("No se pudo determinar la IP del servidor tal como la ve el panel. Configure Cerco:Firmware:PublicHost.");

            string token = firmware.Stage(image, panel.Name);
            var args = new JsonObject
            {
                ["url"] = $"http://{host}:{firmware.Port}/fw/{token}",
                ["size"] = image.Bytes.Length,
                ["sha"] = image.Sha256Hex,
            };
            bool sent = await conns.SendPanelCommandAsync(db, panel.Id, panel.DeviceId, "ota", args, ct);
            await audit.LogAsync(ctx, "cerco", "firmware", targetType: "cerco-panel",
                targetId: panel.Id.ToString(), targetName: panel.Name,
                detail: $"{session.Username} envió el firmware {image.Version} a '{panel.Name}' (tenía {panel.Firmware ?? "?"}).", success: sent);
            return sent
                ? Results.Ok(new { ok = true, version = image.Version, size = image.Bytes.Length, current = panel.Firmware })
                : Error("No se pudo entregar la orden al panel.", StatusCodes.Status502BadGateway);
        }).DisableAntiforgery();

        // -------- órdenes: armar / desarmar / silenciar / zona --------
        app.MapPost("/api/cerco/panels/{id:int}/arm", (HttpContext ctx, int id) => Command(ctx, id, "arm", null, "armó"));
        app.MapPost("/api/cerco/panels/{id:int}/disarm", (HttpContext ctx, int id) => Command(ctx, id, "disarm", null, "desarmó"));
        app.MapPost("/api/cerco/panels/{id:int}/silence", (HttpContext ctx, int id) => Command(ctx, id, "silence", null, "silenció la sirena de"));

        app.MapPost("/api/cerco/panels/{id:int}/siren", async (HttpContext ctx, int id, CercoCommandDto request) =>
            await Command(ctx, id, "siren", new JsonObject { ["on"] = request.On ?? false }, (request.On ?? false) ? "activó la sirena de" : "apagó la sirena de"));

        app.MapPost("/api/cerco/panels/{id:int}/zone", async (HttpContext ctx, int id, CercoCommandDto request) =>
        {
            if (request.Zone is null) return Error("Falta el número de zona.");
            var args = new JsonObject { ["z"] = request.Zone, ["enable"] = request.On ?? true };
            return await Command(ctx, id, "zone", args, $"cambió la zona {request.Zone} de");
        });

        // -------- configuración (administrador) --------
        app.MapGet("/api/cerco/panels/{id:int}/config", async (HttpContext ctx, int id, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.CercoConfigure, out _) is { } failure) return failure;
            var panel = await db.CercoPanels.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            return panel is null ? Results.NotFound() : Results.Ok(CercoMapper.ConfigOf(panel));
        });

        app.MapPut("/api/cerco/panels/{id:int}/config", async (HttpContext ctx, int id, CercoConfigDto request, VmsDbContext db,
            CercoConnectionManager conns, ScopedHub hub, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.CercoConfigure, out var session) is { } failure) return failure;
            if (CercoMapper.Validate(request) is { } err) return Error(err);
            var panel = await db.CercoPanels.Include(p => p.Zones).FirstOrDefaultAsync(p => p.Id == id, ct);
            if (panel is null) return Results.NotFound();
            panel.HvLevel = request.HvLevel;
            panel.SirenSeconds = request.SirenSeconds;
            panel.ExitDelaySeconds = request.ExitDelaySeconds;
            panel.Chirp = request.Chirp;
            panel.KeyMode = request.KeyMode;
            panel.Zone0Mode = request.Zone0Mode;
            panel.Zone0BlocksArm = request.Zone0BlocksArm;
            panel.ConfigSynced = false;             // hasta que el panel reporte la nueva
            panel.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            // Si está conectado se aplica ya; si no, se envía al reconectar.
            bool sent = await conns.SendPanelCommandAsync(db, panel.Id, panel.DeviceId, "config", CercoMapper.ConfigArgs(panel), ct);
            await audit.LogAsync(ctx, "cerco", "panel-config", targetType: "cerco-panel",
                targetId: panel.Id.ToString(), targetName: panel.Name,
                detail: $"{session.Username} cambió la configuración de '{panel.Name}': nivel {panel.HvLevel}, sirena {panel.SirenSeconds}s, " +
                        $"salida {panel.ExitDelaySeconds}s, llave {panel.KeyMode}, zona 0 {panel.Zone0Mode}{(panel.Zone0BlocksArm ? " (bloquea armado)" : "")}.");
            await hub.SendAsync(VmsHubContract.CercoPanelStateChanged, CercoMapper.ToDto(panel, conns.IsConnected(panel.DeviceId)), s => s.CanViewCerco(panel.Id), ct);
            return Results.Ok(new { config = CercoMapper.ConfigOf(panel), sent });
        });

        // -------- controles RF (administrador) --------
        app.MapGet("/api/cerco/panels/{id:int}/remotes", async (HttpContext ctx, int id, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.CercoConfigure, out _) is { } failure) return failure;
            var list = await db.CercoRemotes.AsNoTracking().Where(r => r.CercoPanelId == id).OrderBy(r => r.Slot).ToListAsync(ct);
            return Results.Ok(list.Select(CercoMapper.ToDto).ToList());
        });

        app.MapPost("/api/cerco/panels/{id:int}/remotes/learn", async (HttpContext ctx, int id, CercoRfLearnDto request,
            CercoConnectionManager conns) =>
        {
            // Antes de tocar nada: el nombre pendiente es estado compartido del panel.
            if (ApiSecurity.Require(ctx, Permissions.CercoConfigure, out _) is { } failure) return failure;
            string name = (request.Name ?? "").Trim();
            if (name.Length is 0 or > 64) return Error("El nombre del botón es obligatorio (máximo 64 caracteres).");
            if (request.Action == CercoRfAction.None || !Enum.IsDefined(request.Action)) return Error("Elija la acción del botón.");
            conns.PendingRfName[id] = name;
            return await Command(ctx, id, "rf_learn", new JsonObject { ["action"] = (int)request.Action },
                $"inició la programación del botón '{name}' ({CercoMapper.ActionName(request.Action)}) en", admin: true);
        });

        app.MapPut("/api/cerco/panels/{id:int}/remotes/{slot:int}", async (HttpContext ctx, int id, int slot, CercoRemoteRenameDto request,
            VmsDbContext db, ScopedHub hub, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.CercoConfigure, out _) is { } failure) return failure;
            string name = (request.Name ?? "").Trim();
            if (name.Length is 0 or > 64) return Error("El nombre es obligatorio (máximo 64 caracteres).");
            var r = await db.CercoRemotes.FirstOrDefaultAsync(x => x.CercoPanelId == id && x.Slot == slot, ct);
            if (r is null) return Results.NotFound();
            r.Name = name;
            await db.SaveChangesAsync(ct);
            var list = await db.CercoRemotes.AsNoTracking().Where(x => x.CercoPanelId == id).OrderBy(x => x.Slot).ToListAsync(ct);
            await hub.SendAsync(VmsHubContract.CercoRemotesChanged, new { panelId = id, remotes = list.Select(CercoMapper.ToDto).ToList() }, s => s.CanViewCerco(id), ct);
            return Results.Ok(CercoMapper.ToDto(r));
        });

        // El panel borra y reporta la lista nueva (rf_list): la BD se sincroniza con eso.
        app.MapDelete("/api/cerco/panels/{id:int}/remotes/{slot:int}", (HttpContext ctx, int id, int slot) =>
            slot is < 0 or > 31 ? Task.FromResult(Error("Botón inválido."))
                : Command(ctx, id, "rf_delete", new JsonObject { ["slot"] = slot }, $"eliminó el botón {slot + 1} de", admin: true));

        app.MapDelete("/api/cerco/panels/{id:int}/remotes", (HttpContext ctx, int id) =>
            Command(ctx, id, "rf_delete", new JsonObject { ["slot"] = -1 }, "eliminó TODOS los controles de", admin: true));

        // Handler común de comandos (firma + envío por el receptor + auditoría).
        async Task<IResult> Command(HttpContext ctx, int id, string cmd, JsonObject? args, string verb, bool admin = false)
        {
            // Mantenimiento y controles remotos = configurar; el resto = operar.
            var failure = ApiSecurity.Require(ctx, admin ? Permissions.CercoConfigure : Permissions.CercoOperate, out var session);
            if (failure is not null) return failure;
            var db = ctx.RequestServices.GetRequiredService<VmsDbContext>();
            var conns = ctx.RequestServices.GetRequiredService<CercoConnectionManager>();
            var audit = ctx.RequestServices.GetRequiredService<AuditService>();
            var ct = ctx.RequestAborted;

            var panel = await db.CercoPanels.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            if (panel is null) return Results.NotFound();
            if (!(await ctx.ScopeAsync(session)).CanOperate(panel.LocationId))
                return await ctx.OutOfScopeAsync(session, "cerco-panel", id.ToString(), panel.Name, $"dar la orden '{cmd}' a");
            if (!conns.IsConnected(panel.DeviceId))
            {
                await audit.LogAsync(ctx, "cerco", "command", targetType: "cerco-panel",
                    targetId: id.ToString(), targetName: panel.Name,
                    detail: $"No se pudo enviar '{cmd}' a '{panel.Name}': el panel no está conectado.", success: false);
                return Error("El panel no está conectado.", StatusCodes.Status409Conflict);
            }
            // seq estrictamente creciente, persistido y serializado por panel (anti-replay en el equipo).
            bool sent = await conns.SendPanelCommandAsync(db, panel.Id, panel.DeviceId, cmd, args, ct);
            await audit.LogAsync(ctx, "cerco", "command", targetType: "cerco-panel",
                targetId: id.ToString(), targetName: panel.Name,
                detail: $"{session.Username} {verb} '{panel.Name}' (cmd={cmd}).", success: sent);
            return sent ? Results.Ok(new { ok = true }) : Error("No se pudo entregar el comando al panel.", StatusCodes.Status502BadGateway);
        }

        // -------- eventos recientes (monitor) --------
        app.MapGet("/api/cerco/events", async (HttpContext ctx, int? panelId, int? take, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.CercoMonitor, out var session) is { } failure) return failure;
            int limit = Math.Clamp(take ?? 100, 1, MaxTake);
            var q = InScope(db, db.CercoEvents.AsNoTracking(), await ctx.ScopeAsync(session))
                .OrderByDescending(e => e.ReceivedAt).AsQueryable();
            if (panelId is int pid) q = q.Where(e => e.CercoPanelId == pid);
            var events = await q.Take(limit).ToListAsync(ct);
            return Results.Ok(events.Select(CercoMapper.ToDto).ToList());
        });

        // -------- historial (paginado, con filtros) --------
        app.MapGet("/api/cerco/events/history", async (HttpContext ctx, VmsDbContext db, int? panelId,
            CercoEventKind? kind, DateTime? from, DateTime? to, int? page, int? pageSize, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.CercoMonitor, out var session) is { } failure) return failure;
            var query = InScope(db, FilterEvents(db, panelId, kind, from, to), await ctx.ScopeAsync(session));
            long total = await query.LongCountAsync(ct);
            int size = Math.Clamp(pageSize ?? 50, 1, 200);
            int current = Math.Max(1, page ?? 1);
            var items = await query
                .OrderByDescending(e => e.ReceivedAt).ThenByDescending(e => e.Id)
                .Skip((current - 1) * size).Take(size)
                .ToListAsync(ct);
            return Results.Ok(new CercoEventPageDto(total, current, size, items.Select(CercoMapper.ToDto).ToList()));
        });

        // -------- exportación CSV (";" + UTF-8 con BOM: abre directo en Excel es-CL) --------
        app.MapGet("/api/cerco/events/export", async (HttpContext ctx, VmsDbContext db, AuditService audit, int? panelId,
            CercoEventKind? kind, DateTime? from, DateTime? to, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.CercoMonitor, out _) is { } failure) return failure;
            var rows = await FilterEvents(db, panelId, kind, from, to)
                .OrderByDescending(e => e.ReceivedAt).ThenByDescending(e => e.Id)
                .Take(MaxCsvRows)
                .ToListAsync(ct);
            var csv = new System.Text.StringBuilder();
            csv.AppendLine("Fecha (local);Hora del panel (local);Panel;Evento;Severidad;Zona;Firma verificada");
            foreach (var e in rows)
            {
                csv.AppendJoin(';',
                    Csv(e.ReceivedAt.ToLocalTime().ToString("dd-MM-yyyy HH:mm:ss")),
                    Csv(e.Timestamp.ToLocalTime().ToString("dd-MM-yyyy HH:mm:ss")),
                    Csv(e.PanelName), Csv(e.Description), Csv(SeverityLabel(e.Severity)),
                    Csv(e.ZoneNumber?.ToString()), Csv(e.Verified ? "Sí" : "No"));
                csv.AppendLine();
            }
            await audit.LogAsync(ctx, "cerco", "events-exported",
                detail: $"Exportó {rows.Count} eventos de cerco a CSV.",
                data: new { rows = rows.Count, panelId, kind, from, to });
            byte[] bom = System.Text.Encoding.UTF8.GetPreamble();
            byte[] body = System.Text.Encoding.UTF8.GetBytes(csv.ToString());
            return Results.File(bom.Concat(body).ToArray(), "text/csv", $"eventos_cerco_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        });
    }
}
