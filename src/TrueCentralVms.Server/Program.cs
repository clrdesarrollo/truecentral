using TrueCentralVms.Server.Services.Licensing;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Hikvision;
using TrueCentralVms.Server.Api;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Supervisor;
using TrueCentralVms.Server.Services.Workflows;
using TrueCentralVms.Server.Services.Workflows.Actions;

// El servidor corre igual como consola (desarrollo) o como servicio de Windows
// (instalado). Como servicio, el directorio de trabajo inicial es System32, así
// que el content root debe apuntar a la carpeta del ejecutable (appsettings,
// wwwroot y tools se buscan desde ahí).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});
builder.Host.UseWindowsService(o => o.ServiceName = ServiceSupervisor.WindowsServiceName);
// El apagado ordenado incluye detener PostgreSQL embebido (pg_ctl stop): darle
// margen antes de que el host (o el SCM) corte el proceso.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(60));

// Ajustes propios de cada instalación (puerto, rutas...). El instalador nunca
// sobreescribe este archivo, así que los cambios sobreviven a las actualizaciones.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

// ---------------------------------------------------------------------------
// Servicios
// ---------------------------------------------------------------------------
// PostgreSQL embebido: se distribuye con el sistema (tools\postgres) y el
// servidor lo inicia/detiene solo. La cadena de conexión queda disponible
// recién después de StartAsync (más abajo), antes del primer uso del contexto.
var postgres = new EmbeddedPostgres(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(postgres);
// En tiempo de diseño (dotnet ef migrations add) el host se construye sin
// PostgreSQL iniciado: la cadena es un marcador, solo se necesita el modelo.
builder.Services.AddDbContext<VmsDbContext>(o => o.UseNpgsql(EF.IsDesignTime
    ? "Host=127.0.0.1;Database=design-time;Username=postgres;Password=design-time"
    : postgres.ConnectionString));

builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<PasswordGovernance>();
builder.Services.AddSingleton<CredentialProtector>();
builder.Services.AddSingleton<SystemMetrics>();

// Bitácora de auditoría (ISO 27001): registro solo-agregar de todas las
// acciones, con purga opcional por retención (Audit:RetentionDays).
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<AuditRetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditRetentionService>());

// Licenciamiento contra el servidor central de licencias de CLRobotics:
// archivo .lic firmado (verificación sin conexión) + heartbeat en línea con
// período de gracia. Módulos y cupos por canal salen de la licencia; sin
// licencia rige el período de prueba (Licensing:TrialDays).
builder.Services.AddSingleton<LicenseService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LicenseService>());

// Registro de drivers de dispositivos. Para soportar una marca nueva (Dahua,
// ONVIF, ...) basta con implementar IDeviceDriverFactory en su propio
// proyecto y agregarla aquí: aparece sola en el panel.
builder.Services.AddSingleton<IDeviceDriverFactory, HikvisionDeviceDriverFactory>();
builder.Services.AddSingleton<IDeviceDriverFactory, TrueCentralVms.Drivers.Dahua.DahuaDeviceDriverFactory>();
builder.Services.AddSingleton<IDeviceDriverFactory, TrueCentralVms.Drivers.Onvif.OnvifDeviceDriverFactory>();
builder.Services.AddSingleton<DriverRegistry>();
builder.Services.AddSingleton<DeviceStatusMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeviceStatusMonitor>());

// Muro de video: drivers de DECODIFICACIÓN (distintos de los de dispositivo:
// aquí el equipo pinta las pantallas del muro). Para soportar una marca nueva
// basta con implementar IDecoderDriverFactory y agregarla aquí.
builder.Services.AddSingleton<IDecoderDriverFactory, HikvisionDecoderDriverFactory>();
builder.Services.AddSingleton<IDecoderDriverFactory, TrueCentralVms.Drivers.Onvif.OnvifDecoderDriverFactory>();
builder.Services.AddSingleton<DecoderDriverRegistry>();
builder.Services.AddSingleton<DecoderSessionManager>();
builder.Services.AddScoped<WallService>();

// Aplicaciones → Reconocimiento de patentes: el servicio mantiene abierto el
// canal de eventos ANPR de cada equipo marcado como fuente y guarda las fotos
// en disco (la base solo lleva la ruta).
builder.Services.AddSingleton<AnprStore>();
builder.Services.AddSingleton<AnprService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AnprService>());

// Paneles de alarma (centrales de intrusión): el servicio mantiene el sondeo
// de estado y el canal de eventos de cada panel habilitado. Para otra marca
// basta con implementar IAlarmPanelDriverFactory y agregarla aquí.
builder.Services.AddSingleton<IAlarmPanelDriverFactory, HikvisionAlarmPanelDriverFactory>();
builder.Services.AddSingleton<IAlarmPanelDriverFactory, HikvisionIpReceiverDriverFactory>();
// Receptora de paneles instalada junto al servidor: el servidor la activa solo
// (contraseña generada y guardada cifrada) para que sea un servicio interno
// que el operador no ve ni administra.
builder.Services.AddSingleton<LocalIpReceiverService>();
builder.Services.AddSingleton<AlarmDriverRegistry>();
builder.Services.AddSingleton<AlarmPanelService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlarmPanelService>());

// Parlantes IP: drivers, registro y servicio (sondeo de estado, reproducción sincronizada, voz en vivo).
builder.Services.AddSingleton<ISpeakerDriverFactory, HikvisionSpeakerDriverFactory>();
builder.Services.AddSingleton<SpeakerDriverRegistry>();
builder.Services.AddSingleton<SpeakerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SpeakerService>());
// Centro receptor de alarmas (SIA DC-09: ADM-CID / SIA-DCS por TCP) al que
// los paneles reportan como "Alarm Receiving Center".
builder.Services.AddSingleton<AlarmReceiverService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlarmReceiverService>());

// Automatizaciones (workflows): "cuando pase ESTO, hacer ESTO OTRO". El motor
// escucha lo que publican los módulos (hoy, los paneles de alarma) y ejecuta
// las acciones configuradas. Para soportar una acción nueva basta con
// implementar IWorkflowActionExecutor y agregarla aquí: aparece sola en el
// editor del panel.
builder.Services.AddSingleton<WorkflowStore>();
builder.Services.AddSingleton<SmtpSender>();
builder.Services.AddSingleton<IWorkflowActionExecutor, SnapshotAction>();
builder.Services.AddSingleton<IWorkflowActionExecutor, EmailAction>();
builder.Services.AddSingleton<IWorkflowActionExecutor, FtpAction>();
builder.Services.AddSingleton<IWorkflowActionExecutor, HttpAction>();
builder.Services.AddSingleton<IWorkflowActionExecutor, SpeakerAction>();
builder.Services.AddSingleton<IWorkflowActionExecutor, NotifyAction>();
builder.Services.AddSingleton<WorkflowEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkflowEngine>());

// Plano de media: MediaMTX embebido + tokens de streaming + contabilidad.
builder.Services.AddSingleton<StreamTokenService>();
// Relés de reproducción acelerada (el servidor toma la sesión RTSP del equipo
// para poder pedirle velocidad; a 1× no interviene).
builder.Services.AddSingleton<TrueCentralVms.Server.Services.Rtsp.PlaybackRelayManager>();
builder.Services.AddSingleton<MediaMtxManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MediaMtxManager>());
builder.Services.AddSingleton<SessionAccounting>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionAccounting>());

// Supervisor de servicios (watchdog): vigila procesos hijos y subsistemas, los
// reinicia si caen y deja que un administrador los controle desde el panel.
// Va AL FINAL de los hosted services: cuando empieza a juzgarlos, todos los
// demás ya arrancaron. Los servicios supervisados se registran como singleton
// + AddHostedService(sp => ...) para que el supervisor tome la MISMA instancia.
builder.Services.AddSingleton<ServiceSupervisor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServiceSupervisor>());

builder.Services.AddSignalR();
builder.Services.AddMemoryCache();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
// Los enums de los DTO viajan como texto en JSON ("Nvr", "Online", ...).
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Logs opcionales del SDK Hikvision (ruta con variables de entorno admitidas).
string? sdkLogDirectory = builder.Configuration["Hikvision:SdkLogDirectory"];
HikvisionSdk.LogDirectory = string.IsNullOrWhiteSpace(sdkLogDirectory)
    ? null
    : Environment.ExpandEnvironmentVariables(sdkLogDirectory);

var app = builder.Build();

// ---------------------------------------------------------------------------
// Base de datos: iniciar PostgreSQL embebido y aplicar migraciones
// ---------------------------------------------------------------------------
await postgres.StartAsync(app.Logger);
app.Lifetime.ApplicationStopped.Register(() => postgres.Stop(app.Logger));

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
    await db.Database.MigrateAsync();

    // El servidor se distribuye "desactivado": sin usuarios no hay login. El
    // primer administrador se crea desde el asistente del panel web, solo
    // accesible desde la propia máquina del servidor (ver SetupApi).
    if (!await db.Users.AnyAsync())
        app.Logger.LogWarning(
            "Sistema sin inicializar: abra http://localhost:5090 EN ESTA MÁQUINA para crear el primer administrador.");
}

// La receptora local se activa en segundo plano: si no está instalada o tarda
// en levantar, el servidor arranca igual y lo deja anotado en el registro. Se
// reintenta un rato porque la receptora arranca sus propios servicios (nginx,
// su PostgreSQL) y puede tardar más que este servidor.
_ = Task.Run(async () =>
{
    var stopping = app.Lifetime.ApplicationStopping;
    using var scope = app.Services.CreateScope();
    var receiver = scope.ServiceProvider.GetRequiredService<LocalIpReceiverService>();
    // La receptora crea su propia base en el primer arranque y puede tardar
    // varios minutos, mas de lo que tarda este servidor. Se reintenta durante
    // una hora para que la activación no dependa de reiniciar el servicio.
    for (int attempt = 1; attempt <= 60 && !stopping.IsCancellationRequested; attempt++)
    {
        var state = await receiver.EnsureActivatedAsync(stopping);
        if (state.Present || !receiver.Enabled)
            break;   // está (activada o no), o no hay nada que hacer
        try { await Task.Delay(TimeSpan.FromMinutes(1), stopping); }
        catch (OperationCanceledException) { break; }
    }
});

app.UseCors();
// WebSocket propio (fuera de SignalR) para la voz del operador hacia los parlantes.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
app.UseDefaultFiles();
// El panel es una SPA servida como archivos estáticos: se exige revalidación
// (ETag/304) para que los navegadores no se queden con app.js viejo tras una
// actualización del sistema.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

// ---------------------------------------------------------------------------
// Autenticación por token opaco (Authorization: Bearer <token> o
// ?access_token= — este último para el WebSocket de SignalR)
// ---------------------------------------------------------------------------
app.Use(async (context, next) =>
{
    var tokens = context.RequestServices.GetRequiredService<TokenService>();
    string? token = null;

    string? header = context.Request.Headers.Authorization.FirstOrDefault();
    if (header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        token = header["Bearer ".Length..].Trim();
    token ??= context.Request.Query["access_token"].FirstOrDefault();

    if (token is not null && tokens.Validate(token) is { } session)
        context.Items["session"] = session;

    await next();
});

// Modo restringido por licencia (vencida, revocada, prueba terminada, gracia
// agotada): se rechazan con 402 las escrituras y las solicitudes de video.
// Las lecturas, el login y la propia API de licencia siguen disponibles para
// poder ver el motivo y regularizar. Nada se borra.
app.Use(async (context, next) =>
{
    var license = context.RequestServices.GetRequiredService<LicenseService>();
    if (!license.IsOperational && LicenseService.IsRestrictedRequest(context))
    {
        context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
        await context.Response.WriteAsJsonAsync(new
        {
            error = license.Snapshot.Message ?? "El sistema está en modo restringido por licencia.",
            licenseDenied = true,
        });
        return;
    }
    await next();
});

// ---------------------------------------------------------------------------
// API
// ---------------------------------------------------------------------------
// Sonda de disponibilidad (pública): el panel web la usa para detectar
// desconexiones del servidor y el instalador para verificar la versión.
string serverVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
app.MapGet("/api/health", () => Results.Ok(new { ok = true, time = DateTime.UtcNow, version = serverVersion }));

app.MapSetupApi(serverVersion);
app.MapAuthApi();
app.MapUsersApi();
app.MapDevicesApi();
app.MapStreamsApi();
app.MapStreamingAuthApi();
app.MapPlaybackApi();
app.MapDiscoveryApi();
app.MapSystemApi();
app.MapLicenseApi();
app.MapDecodersApi();
app.MapWallsApi();
app.MapAnprApi();
app.MapAuditApi();
app.MapAlarmsApi();
app.MapSpeakersApi();
app.MapWorkflowsApi();

app.MapHub<VmsHub>(VmsHubContract.HubPath);

// La SPA del panel se sirve para cualquier ruta no-API (hash router).
app.MapFallbackToFile("index.html");

app.Logger.LogInformation("CLR TrueCentral VMS v{Version} escuchando en {Urls}.",
    serverVersion, builder.Configuration["Urls"]);

// Evidencia de disponibilidad: cada arranque del servidor queda en la bitácora.
await app.Services.GetRequiredService<AuditService>().LogSystemAsync("system", "server-started",
    detail: $"Servidor CLR TrueCentral VMS v{serverVersion} iniciado.");

// ...y cada apagado ordenado, lo pida el Watchdog, services.msc o el propio
// SCM al apagar el equipo. Va en ApplicationStopping y no en ApplicationStopped
// porque PostgreSQL se detiene recién en ese último (ver más arriba): en este
// punto la base sigue viva para recibir el evento.
app.Lifetime.ApplicationStopping.Register(() =>
{
    try
    {
        app.Services.GetRequiredService<AuditService>()
            .LogSystemAsync("system", "server-stopped",
                detail: $"Servidor CLR TrueCentral VMS v{serverVersion} detenido.")
            .GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        // Un apagado no se puede abortar por no poder escribir la bitácora.
        app.Logger.LogWarning(ex, "No se pudo registrar el apagado del servidor en la bitácora.");
    }
});

// Precalentamiento del muro en segundo plano: sincroniza cada muro al arrancar
// para que el primer comando del operador (p. ej. la pantalla completa por
// doble clic) sea instantáneo en vez de pagar la sincronización perezosa de la
// sesión nueva contra el decodificador.
_ = Task.Run(async () =>
{
    try
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WallService>().WarmUpDecodersAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "El precalentamiento de los decodificadores del muro falló.");
    }
});

app.Run();
