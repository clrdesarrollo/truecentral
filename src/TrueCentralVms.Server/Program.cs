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
using TrueCentralVms.Server.Services.Https;
using TrueCentralVms.Server.Services.Supervisor;
using TrueCentralVms.Server.Services.Workflows;
using TrueCentralVms.Server.Services.Workflows.Actions;

// Exporta el catálogo de licenciamiento (el JSON que importa el servidor de
// licencias) y termina sin levantar nada. Lo usan Jenkins y el desarrollador
// al cambiar LicenseCatalog.
if (args.Length > 0 && args[0] == LicenseCatalogExport.Argument)
{
    Environment.ExitCode = LicenseCatalogExport.Run(args.Length > 1 ? args[1] : null);
    return;
}

// Proceso aparte de un respaldo: lo lanza el propio servidor y recibe el trabajo
// por la entrada estándar. No levanta nada más (ver BackupWorker).
if (TrueCentralVms.Server.Services.Backup.BackupWorker.IsRequested(args))
{
    Environment.ExitCode = await TrueCentralVms.Server.Services.Backup.BackupWorker.RunChildAsync();
    return;
}

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
// Como servicio, conectarse con Windows antes del arranque pesado (base, certificado,
// respaldo y migraciones): Windows da solo 30 s (ver EarlyServiceLifetime). Si el
// servidor se detiene sin que Windows lo pida, sale con código de error para que el
// SCM lo vuelva a levantar (ver ScmAwareServiceLifetime).
if (WindowsServiceHelpers.IsWindowsService())
{
    builder.Services.AddSingleton<WindowsServiceLifetime, ScmAwareServiceLifetime>();
    builder.Services.AddSingleton<IHostLifetime, EarlyServiceLifetime>();
}
// El apagado ordenado incluye detener PostgreSQL embebido (pg_ctl stop): darle
// margen antes de que el host (o el SCM) corte el proceso. Los servicios se
// detienen en paralelo: el apagado dura lo que el más lento y no la suma de
// todos (con muchos equipos la suma pasaba el tope; ver ShutdownWatch).
builder.Services.Configure<HostOptions>(o =>
{
    o.ShutdownTimeout = TimeSpan.FromSeconds(
        Math.Clamp(builder.Configuration.GetValue("Host:ShutdownTimeoutSeconds", 60), 1, 300));
    o.ServicesStopConcurrently = true;
});

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

// Restauración de un respaldo por consola (con el servicio detenido): no levanta el servidor.
if (TrueCentralVms.Server.Services.Backup.BackupRestore.IsRequested(args))
{
    Environment.ExitCode = await TrueCentralVms.Server.Services.Backup.BackupRestore.RunAsync(args, postgres, builder.Configuration);
    return;
}
builder.Services.AddSingleton(postgres);
builder.Services.AddSingleton<RunJournal>();
var protector = new CredentialProtector(postgres);
builder.Services.AddSingleton(protector);

// HTTPS: todo lo de las personas y los programas propios va cifrado por
// Web:HttpsPort (443) con el certificado del servidor (propio o cargado por el
// cliente). Web:HttpPort (80) solo atiende a equipos sin TLS y redirige el
// resto; Web:InternalPort escucha HTTP solo en loopback para MediaMTX y las
// sondas de salud. Ver WebPorts y la regla por puerto más abajo.
var webPorts = WebPorts.From(builder.Configuration);
builder.Services.AddSingleton(webPorts);
// Acceso desde Internet (dirección pública, redes locales, escenarios y puertos).
builder.Services.AddSingleton<TrueCentralVms.Server.Services.Network.WanAccessService>();
if (!EF.IsDesignTime)
{
    var certificates = new ServerCertificateManager(postgres, protector, builder.Configuration);
    builder.Services.AddSingleton(certificates);
    builder.Services.AddHostedService<HttpsCertificateMonitor>();
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.ListenAnyIP(webPorts.HttpsPort, l => l.UseHttps(h => h.ServerCertificateSelector = (_, _) => certificates.Current));
        if (webPorts.HttpPort > 0) k.ListenAnyIP(webPorts.HttpPort);
        k.ListenLocalhost(webPorts.InternalPort);
    });
}
// En tiempo de diseño (dotnet ef migrations add) el host se construye sin
// PostgreSQL iniciado: la cadena es un marcador, solo se necesita el modelo.
builder.Services.AddDbContext<VmsDbContext>(o => o.UseNpgsql(EF.IsDesignTime
    ? "Host=127.0.0.1;Database=design-time;Username=postgres;Password=design-time"
    : postgres.ConnectionString));

builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton(_ => new LoginThrottle());
// Alcance por ubicación de cada usuario (qué recursos ve y opera) y el hub
// que entrega los eventos según ese alcance.
builder.Services.AddSingleton<UserScopeService>();
builder.Services.AddSingleton<ScopedHub>();
builder.Services.AddSingleton<PasswordGovernance>();
builder.Services.AddSingleton<SystemMetrics>();

// Bitácora de auditoría (ISO 27001): registro solo-agregar de todas las
// acciones, con purga opcional por retención (Audit:RetentionDays).
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<AuditRetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditRetentionService>());

// Respaldos cifrados: diario, antes de cada actualización y a pedido (Sistema → Respaldos).
builder.Services.AddSingleton<TrueCentralVms.Server.Services.Backup.BackupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrueCentralVms.Server.Services.Backup.BackupService>());

// Licenciamiento contra el servidor central de licencias de CLRobotics:
// archivo .lic firmado (verificación sin conexión) + heartbeat en línea con
// período de gracia. Módulos y cupos por canal salen de la licencia; sin
// licencia rige el período de prueba (Licensing:TrialDays).
builder.Services.AddSingleton<LicenseQuotaReconciler>();
builder.Services.AddSingleton<LicenseService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LicenseService>());

// Registro de drivers de dispositivos. Para soportar una marca nueva (Dahua,
// ONVIF, ...) basta con implementar IDeviceDriverFactory en su propio
// proyecto y agregarla aquí: aparece sola en el panel.
builder.Services.AddSingleton<IDeviceDriverFactory, HikvisionDeviceDriverFactory>();
builder.Services.AddSingleton<IDeviceDriverFactory, TrueCentralVms.Drivers.Dahua.DahuaDeviceDriverFactory>();
builder.Services.AddSingleton<IDeviceDriverFactory, TrueCentralVms.Drivers.Onvif.OnvifDeviceDriverFactory>();
builder.Services.AddSingleton<IDeviceDriverFactory, HikvisionIsupDeviceDriverFactory>();
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
builder.Services.AddSingleton<IAlarmPanelDriverFactory, HikvisionIsupAlarmPanelDriverFactory>();
builder.Services.AddSingleton<AlarmDriverRegistry>();
builder.Services.AddSingleton<AlarmPanelService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlarmPanelService>());

// Control de acceso: drivers, registro y servicio (sondeo de estado de terminales y controladoras).
// Tres marcas, tres transportes: ISAPI (HTTP) en Hikvision, CGI (HTTP) en
// Dahua y el protocolo propio del SDK (TCP 4370) en ZKTeco.
builder.Services.AddSingleton<IAccessControlDriverFactory, HikvisionAccessDriverFactory>();
builder.Services.AddSingleton<IAccessControlDriverFactory, HikvisionIsupAccessDriverFactory>();
builder.Services.AddSingleton<IAccessControlDriverFactory, TrueCentralVms.Drivers.Dahua.DahuaAccessDriverFactory>();
builder.Services.AddSingleton<IAccessControlDriverFactory, TrueCentralVms.Drivers.ZkTeco.ZkTecoAccessDriverFactory>();
builder.Services.AddSingleton<IAccessControlDriverFactory, TrueCentralVms.Drivers.ZkTeco.C3AccessDriverFactory>();
builder.Services.AddSingleton<AccessDriverRegistry>();
// Configuración propia de los equipos (puertas y lectores), por marca.
builder.Services.AddSingleton<IAccessDeviceSettingsProvider, HikvisionAccessSettingsProvider>();
builder.Services.AddSingleton<IAccessDeviceSettingsProvider, TrueCentralVms.Drivers.ZkTeco.C3AccessSettingsProvider>();
builder.Services.AddSingleton<AccessControlService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AccessControlService>());
// El padrón (personas, credenciales, horarios y niveles) lo escribe el
// sincronizador en los equipos, y el historial de accesos lo trae el lector de
// eventos: van aparte porque tienen ritmos distintos (uno reacciona a los
// cambios del operador, el otro sondea cada 15 s).
builder.Services.AddSingleton<AccessSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AccessSyncService>());
builder.Services.AddSingleton<AccessEventService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AccessEventService>());
// Hora de los equipos: revisa su reloj y su zona, y los pone en hora solos.
builder.Services.AddSingleton<DeviceClockService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeviceClockService>());
// Hora oficial: toma la hora de las fuentes NTP, corrige el reloj de Windows y
// se la da a los equipos como servidor NTP (UDP 123).
builder.Services.AddSingleton<TrueCentralVms.Server.Services.Time.TimeService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrueCentralVms.Server.Services.Time.TimeService>());

// Parlantes IP: drivers, registro y servicio (sondeo de estado, reproducción sincronizada, voz en vivo).
builder.Services.AddSingleton<ISpeakerDriverFactory, HikvisionSpeakerDriverFactory>();
builder.Services.AddSingleton<ISpeakerDriverFactory, HikvisionPaSpeakerDriverFactory>();
builder.Services.AddSingleton<ISpeakerDriverFactory, HikvisionIsupSpeakerDriverFactory>();
builder.Services.AddSingleton<TrueCentralVms.Server.Services.SpeakerAgents.SpeakerAgentHub>();
builder.Services.AddSingleton<ISpeakerDriverFactory, TrueCentralVms.Server.Services.SpeakerAgents.AgentSpeakerDriverFactory>();
builder.Services.AddSingleton<SpeakerDriverRegistry>();
builder.Services.AddSingleton<SpeakerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SpeakerService>());
// Módulos de E/S: drivers, registro y servicio (una conexión por módulo, sondeo
// de entradas y órdenes a los relés).
builder.Services.AddSingleton<IIoModuleDriverFactory, TrueCentralVms.Drivers.Adam.AdamIoDriverFactory>();
builder.Services.AddSingleton<IIoModuleDriverFactory, TrueCentralVms.Drivers.ZkTeco.C3IoDriverFactory>();
builder.Services.AddSingleton<IoModuleDriverRegistry>();
builder.Services.AddSingleton<IoModuleService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IoModuleService>());
// Citofonía: frentes de videoportero (enlace de llamadas por SDK, voz bidireccional y apertura de puerta).
builder.Services.AddSingleton<IIntercomDriverFactory, HikvisionIntercomDriverFactory>();
builder.Services.AddSingleton<IIntercomDriverFactory, HikvisionIsupIntercomDriverFactory>();
builder.Services.AddSingleton<IntercomDriverRegistry>();
builder.Services.AddSingleton<IntercomService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IntercomService>());
// Centro receptor de alarmas (SIA DC-09: ADM-CID / SIA-DCS por TCP) al que
// los paneles reportan como "Alarm Receiving Center".
builder.Services.AddSingleton<AlarmReceiverService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlarmReceiverService>());

// Paneles de cerco eléctrico: receptor WebSocket en PUERTO PROPIO (Cerco:Receiver:Port,
// 5092 por omisión) al que los ESP8266 se conectan y autentican con su PSK.
builder.Services.AddSingleton<CercoConnectionManager>();
builder.Services.AddSingleton<CercoReceiverService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CercoReceiverService>());
// Descarga de firmware para OTA de los paneles de cerco, también en PUERTO PROPIO
// (Cerco:Firmware:Port, 5093 por omisión): fuera de la API :5090.
builder.Services.AddSingleton<CercoFirmwareService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CercoFirmwareService>());

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
// Destinos de las acciones (HTTP, FTP, parlante por URL, correo): nunca el propio
// servidor salvo que se habilite a propósito (ver OutboundAddressPolicy).
OutboundAddressPolicy.AllowLoopback = builder.Configuration.GetValue("Workflows:AllowLoopbackTargets", false);
builder.Services.AddSingleton<IWorkflowActionExecutor, SpeakerAction>();
builder.Services.AddSingleton<IWorkflowActionExecutor, NotifyAction>();
builder.Services.AddSingleton<IWorkflowActionExecutor, DoorAction>();
builder.Services.AddSingleton<IWorkflowActionExecutor, PanelAction>();
builder.Services.AddSingleton<IWorkflowActionExecutor, TrueCentralVms.Server.Services.Workflows.Actions.PtzPresetAction>();
builder.Services.AddSingleton<IWorkflowActionExecutor, RelayAction>();
// Pantallas de alarma de las casetas (Raspberry Pi): emparejamiento, hub propio,
// entrega de los avisos de las automatizaciones y acuse sincronizado con la
// ventana de alarma de los operadores. Va antes del motor: la acción de aviso
// la usa para difundir a las pantallas.
builder.Services.AddSingleton<AlarmScreenService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlarmScreenService>());
builder.Services.AddSingleton<WorkflowEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkflowEngine>());
// Eventos de analítica de las cámaras (movimiento, cruce de línea, intrusión...):
// el servicio se suscribe SOLO a los equipos que piden las automatizaciones.
builder.Services.AddSingleton<VideoEventService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<VideoEventService>());

// Plano de media: MediaMTX embebido + tokens de streaming + contabilidad.
builder.Services.AddSingleton<StreamTokenService>();
// PTZ por velocidades (joystick): vía nativa o traducida y hombre muerto por canal.
builder.Services.AddSingleton<PtzMover>();
// Relés de reproducción acelerada (el servidor toma la sesión RTSP del equipo
// para poder pedirle velocidad; a 1× no interviene).
builder.Services.AddSingleton<TrueCentralVms.Server.Services.Rtsp.PlaybackRelayManager>();
builder.Services.AddSingleton<MediaMtxManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MediaMtxManager>());

// ISUP propio: un proceso interno (TrueCentralVms.IsupHost.exe) con el SDK ISUP de Hikvision, al que se registran los equipos
// que el servidor no alcanza por IP (paneles AX PRO, equipos detrás de NAT/4G/MPLS). El servicio es su cliente y el transporte
// que usan los drivers "hikvision-isup" (IsupGateway.Current, que queda publicado al construirse el servicio, antes de que
// arranque cualquier servicio de fondo).
builder.Services.AddSingleton<TrueCentralVms.Server.Services.Isup.IsupHostManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrueCentralVms.Server.Services.Isup.IsupHostManager>());
builder.Services.AddSingleton<TrueCentralVms.Server.Services.Isup.IsupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrueCentralVms.Server.Services.Isup.IsupService>());
// Flota: posiciones GPS de los vehículos (las entrega el servicio ISUP).
builder.Services.AddSingleton<TrueCentralVms.Server.Services.Fleet.FleetPositionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrueCentralVms.Server.Services.Fleet.FleetPositionService>());
builder.Services.AddSingleton<TrueCentralVms.Server.Services.Fleet.FleetEventService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrueCentralVms.Server.Services.Fleet.FleetEventService>());
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
// Conexiones vivas del hub con su usuario y su token: TokenService las corta
// al revocar la sesión (el hub solo valida al conectarse). El acceso al
// request en curso le sirve para auditar quién revocó.
builder.Services.AddSingleton<HubConnections>();
builder.Services.AddHttpContextAccessor();
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
if (app.Services.GetService<IHostLifetime>() is EarlyServiceLifetime earlyLifetime)
    await earlyLifetime.WaitForStartAsync(CancellationToken.None);

// ---------------------------------------------------------------------------
// Base de datos: iniciar PostgreSQL embebido y aplicar migraciones
// ---------------------------------------------------------------------------
// Etapas del arranque para quien espera afuera (el instalador): /api/health responde recién al final.
var startup = new StartupStatus(postgres.DataRoot, app.Logger);
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    startup.Failed(e.ExceptionObject as Exception ?? new Exception("Error desconocido al iniciar."));
startup.Report("database", 1, "Iniciando la base de datos");
await postgres.StartAsync(app.Logger);
// El certificado HTTPS vive en el directorio de datos: recién ahora (initdb exige encontrarlo vacío).
if (app.Services.GetService<ServerCertificateManager>() is { } httpsCertificates)
{
    startup.Report("certificate", 1, "Preparando el certificado HTTPS", "La primera vez se genera la llave del servidor");
    // Con el acceso desde Internet encendido, la dirección pública también va en el certificado propio.
    var wanAccess = app.Services.GetRequiredService<TrueCentralVms.Server.Services.Network.WanAccessService>();
    httpsCertificates.ExtraNames = () => wanAccess.Enabled ? [wanAccess.PublicHost] : [];
    httpsCertificates.Initialize();
}
app.Lifetime.ApplicationStopped.Register(() => postgres.Stop(app.Logger));

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
    // Versión nueva sobre una base existente: respaldo ANTES de migrar, para poder volver si algo sale mal.
    var pendingMigrations = (await db.Database.GetPendingMigrationsAsync()).ToList();
    if (pendingMigrations.Count > 0 && (await db.Database.GetAppliedMigrationsAsync()).Any())
    {
        startup.Report("backup", 2, "Respaldando la base de datos antes de actualizar", "Preparando el respaldo");
        await app.Services.GetRequiredService<TrueCentralVms.Server.Services.Backup.BackupService>()
            .BackupBeforeUpdateAsync(pendingMigrations, CancellationToken.None, startup.BackupProgress());
    }
    // De a una, para poder mostrar cuál va (MigrateAsync las aplica todas sin avisar).
    var migrator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
        .GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db);
    for (int i = 0; i < pendingMigrations.Count; i++)
    {
        string migration = pendingMigrations[i];
        startup.Report("migrate", 3, "Actualizando la estructura de la base de datos",
            $"{i + 1} de {pendingMigrations.Count}: {migration[(migration.IndexOf('_') + 1)..]}", i * 100 / pendingMigrations.Count);
        await migrator.MigrateAsync(migration);
    }
    await db.Database.MigrateAsync(); // sin pendientes no hace nada; queda como red de seguridad

    // Control de acceso: el horario 24/7 tiene que existir desde el principio,
    // porque es el que se ofrece por omisión al crear el primer nivel de acceso.
    await AccessCatalogApi.EnsureBuiltInScheduleAsync(db, CancellationToken.None);

    // Alcances por ubicación y árbol de ubicaciones en memoria: los equipos
    // muestran la ruta de su ubicación desde el primer pedido.
    await app.Services.GetRequiredService<UserScopeService>().SnapshotAsync();

    // El servidor se distribuye "desactivado": sin usuarios no hay login. El
    // primer administrador se crea desde el asistente del panel web, solo
    // accesible desde la propia máquina del servidor (ver SetupApi).
    if (!await db.Users.AnyAsync())
        app.Logger.LogWarning(
            "Sistema sin inicializar: abra {Url} EN ESTA MÁQUINA para crear el primer administrador.", webPorts.HttpsUrl("localhost"));
}
startup.Report("services", 4, "Iniciando los servicios", "Video, cerco, alarmas, control de acceso y demás");
app.Lifetime.ApplicationStarted.Register(startup.Ready);

// Regla por puerto: el interno (loopback) solo atiende lo suyo; el HTTP sin
// cifrar solo a los equipos sin TLS, redirige las páginas a HTTPS y rechaza el
// resto (una clave o un token no deben viajar en claro).
app.Use(async (context, next) =>
{
    int port = context.Connection.LocalPort;
    var path = context.Request.Path;
    if (port == webPorts.InternalPort && port != webPorts.HttpsPort && port != webPorts.HttpPort)
    {
        if (!WebPorts.InternalPaths.Any(p => path.StartsWithSegments(p)))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
    }
    else if (!context.Request.IsHttps && !WebPorts.PlainHttpPaths.Any(p => path.StartsWithSegments(p)))
    {
        // Redirección temporal: una permanente queda guardada en el navegador aunque después se cambien los puertos.
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            string host = context.Request.Host.Host;
            context.Response.Redirect(webPorts.HttpsUrl(host) + context.Request.PathBase + path + context.Request.QueryString,
                permanent: false);
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = $"Use HTTPS: {webPorts.HttpsUrl(context.Request.Host.Host)}" });
        }
        return;
    }
    await next();
});

// Internet Explorer 11 y anteriores no corren el panel: se les muestra un aviso para usar Edge.
app.Use(async (context, next) =>
{
    if (LegacyBrowserGate.IsLegacyPageRequest(context.Request))
    {
        await LegacyBrowserGate.WriteNoticeAsync(context.Response);
        return;
    }
    await next();
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
    {
        context.Items["session"] = session;
        context.Items["token"] = token;
        // Alcance y permisos de sus roles (en memoria): las guardas
        // ApiSecurity.Require los consultan sin volver a la base.
        var userScope = await context.RequestServices.GetRequiredService<UserScopeService>()
            .ForUserAsync(session.UserId, context.RequestAborted);
        context.Items["scope"] = userScope;
        // Desde fuera de las redes locales solo opera quien tiene «Entrar desde Internet» (cerrar sesión siempre se puede).
        var wan = context.RequestServices.GetRequiredService<TrueCentralVms.Server.Services.Network.WanAccessService>();
        if (wan.IsRemote(context.Connection.RemoteIpAddress) && !userScope.Has(TrueCentralVms.Core.Domain.Permissions.RemoteAccess)
            && !context.Request.Path.StartsWithSegments("/api/auth/logout"))
        {
            var audit = context.RequestServices.GetRequiredService<AuditService>();
            if (audit.ShouldLog($"remote-denied:{session.UserId}", TimeSpan.FromMinutes(10)))
                await audit.LogAsync(context, "auth", "request-remote-denied", targetType: "user", targetName: session.Username,
                    detail: $"Rechazado: '{session.Username}' intentó operar desde Internet ({context.Connection.RemoteIpAddress}) sin el permiso «Entrar desde Internet».",
                    success: false);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Su usuario no tiene permitido entrar desde Internet: conéctese desde la red local." });
            return;
        }
    }
    else if (context.Request.Path.StartsWithSegments(VmsHubContract.HubPath))
    {
        // El hub solo atiende sesiones vigentes y lo dice con 401 ANTES de abrir
        // la conexión (negotiate, WebSocket o sondeo): así el cliente de una
        // sesión revocada sabe que debe renovarla o volver al login, en vez de
        // conectarse, ser cortado y reintentar en bucle.
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next();
});

// ---------------------------------------------------------------------------
// Credencial de las pantallas de alarma (X-Screen-Token o ?access_token= en su
// hub). Va aparte de la de los usuarios: una pantalla no tiene sesión. Su hub
// responde 401 a una credencial desconocida (la pantalla vuelve a emparejarse)
// y 403 a la de una pantalla deshabilitada (la conserva y espera), ANTES de
// abrir la conexión.
// ---------------------------------------------------------------------------
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    bool screenHub = path.StartsWithSegments(AlarmScreenProtocol.HubPath);
    if (screenHub || path.StartsWithSegments("/api/screen"))
    {
        var (screen, disabled) = await context.RequestServices.GetRequiredService<AlarmScreenService>()
            .ResolveAsync(AlarmScreenService.TokenOf(context), context.RequestAborted);
        if (screen is not null) context.Items[AlarmScreenService.ItemKey] = screen;
        else if (disabled) context.Items[AlarmScreenService.DisabledItemKey] = true;
        if (screenHub && screen is null)
        {
            context.Response.StatusCode = disabled ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
            return;
        }
    }
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
app.MapRolesApi();
app.MapDevicesApi();
app.MapStreamsApi();
app.MapStreamingAuthApi();
app.MapPlaybackApi();
app.MapDiscoveryApi();
app.MapSystemApi();
app.MapBackupApi();
app.MapCertificateApi();
app.MapWanApi();
app.MapTimeApi();

app.MapLicenseApi();
app.MapDecodersApi();
app.MapWallsApi();
app.MapLiveViewsApi();
app.MapLocationsApi();
app.MapResourcesApi();
app.MapAnprApi();
app.MapAuditApi();
app.MapAlarmsApi();
app.MapIsupApi();
app.MapCercoApi();
app.MapSpeakersApi();
app.MapSpeakerAgentsApi();
app.MapIoModulesApi();
app.MapFleetApi();
app.MapFleetEventsApi();
app.MapIntercomsApi();
app.MapAccessApi();
app.MapAccessSettingsApi();
app.MapAccessCatalogApi();
app.MapAccessRecordsApi();
app.MapDeviceMaintenanceApi();
app.MapWorkflowsApi();
app.MapAlarmScreensApi();
app.MapDashboardApi();

app.MapHub<VmsHub>(VmsHubContract.HubPath);
app.MapHub<AlarmScreenHub>(AlarmScreenProtocol.HubPath);

// La SPA del panel se sirve para cualquier ruta no-API (hash router).
app.MapFallbackToFile("index.html");

app.Logger.LogInformation("CLR TrueCentral VMS v{Version} escuchando en HTTPS :{Https}, HTTP :{Http} (solo equipos sin TLS) e interno 127.0.0.1:{Internal}.",
    serverVersion, webPorts.HttpsPort, webPorts.HttpPort > 0 ? webPorts.HttpPort : "cerrado", webPorts.InternalPort);

// Evidencia de disponibilidad: cada arranque del servidor queda en la bitácora,
// con cómo terminó la ejecución anterior. Si terminó de mala forma (corte de luz,
// reinicio forzado del equipo, proceso matado o caído) va además como evento
// propio y como error en el registro de eventos de Windows (ver RunJournal).
var runJournal = app.Services.GetRequiredService<RunJournal>();
var previousRun = runJournal.Start(serverVersion);
var startupAudit = app.Services.GetRequiredService<AuditService>();
if (previousRun is { Unexpected: true })
{
    app.Logger.LogError("{Detail}", previousRun.Detail);
    await startupAudit.LogSystemAsync("system", "server-unexpected-stop", detail: previousRun.Detail,
        success: false, data: previousRun.Data);
}
else if (previousRun is not null)
    app.Logger.LogInformation("{Detail}", previousRun.Detail);
await startupAudit.LogSystemAsync("system", "server-started",
    detail: $"Servidor CLR TrueCentral VMS v{serverVersion} iniciado." + (previousRun is null ? "" : " " + previousRun.Detail));

// ...y cada apagado ordenado, lo pida el Watchdog, services.msc o el propio
// SCM al apagar el equipo. Va en ApplicationStopping y no en ApplicationStopped
// porque PostgreSQL se detiene recién en ese último (ver más arriba): en este
// punto la base sigue viva para recibir el evento.
app.Lifetime.ApplicationStopping.Register(() =>
{
    // Primero el archivo (no depende de la base): si el registro en la
    // bitácora falla, la próxima ejecución igual sabe que esta terminó en orden.
    string stopCause = runJournal.Stop();
    try
    {
        app.Services.GetRequiredService<AuditService>()
            .LogSystemAsync("system", "server-stopped",
                detail: $"Servidor CLR TrueCentral VMS v{serverVersion} detenido: {stopCause}.")
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

ShutdownWatch.Attach(app);
try
{
    app.Run();
}
catch (Exception ex) when (ShutdownWatch.IsShutdownTimeout(ex))
{
    // El apagado superó el tope, pero ya se detuvo todo lo que se pudo
    // (PostgreSQL incluido) y ShutdownWatch dejó constancia de quién se
    // demoró: se sale limpio en vez de caerse.
}
