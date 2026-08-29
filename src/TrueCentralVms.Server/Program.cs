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

// El servidor corre igual como consola (desarrollo) o como servicio de Windows
// (instalado). Como servicio, el directorio de trabajo inicial es System32, así
// que el content root debe apuntar a la carpeta del ejecutable (appsettings,
// wwwroot y tools se buscan desde ahí).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});
builder.Host.UseWindowsService(o => o.ServiceName = "CLRTrueCentralVMS");
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
builder.Services.AddDbContext<VmsDbContext>(o => o.UseNpgsql(postgres.ConnectionString));

builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<PasswordGovernance>();
builder.Services.AddSingleton<CredentialProtector>();
builder.Services.AddSingleton<SystemMetrics>();

// Registro de drivers de dispositivos. Para soportar una marca nueva (Dahua,
// ONVIF, ...) basta con implementar IDeviceDriverFactory en su propio
// proyecto y agregarla aquí: aparece sola en el panel.
builder.Services.AddSingleton<IDeviceDriverFactory, HikvisionDeviceDriverFactory>();
builder.Services.AddSingleton<IDeviceDriverFactory, TrueCentralVms.Drivers.Dahua.DahuaDeviceDriverFactory>();
builder.Services.AddSingleton<IDeviceDriverFactory, TrueCentralVms.Drivers.Onvif.OnvifDeviceDriverFactory>();
builder.Services.AddSingleton<DriverRegistry>();
builder.Services.AddHostedService<DeviceStatusMonitor>();

// Plano de media: MediaMTX embebido + tokens de streaming + contabilidad.
builder.Services.AddSingleton<StreamTokenService>();
builder.Services.AddSingleton<MediaMtxManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MediaMtxManager>());
builder.Services.AddHostedService<SessionAccounting>();

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

app.UseCors();
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
app.MapDiscoveryApi();
app.MapSystemApi();

app.MapHub<VmsHub>(VmsHubContract.HubPath);

// La SPA del panel se sirve para cualquier ruta no-API (hash router).
app.MapFallbackToFile("index.html");

app.Logger.LogInformation("CLR TrueCentral VMS v{Version} escuchando en {Urls}.",
    serverVersion, builder.Configuration["Urls"]);
app.Run();
