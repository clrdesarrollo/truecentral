using TrueCentralVms.SpeakerAgent;
using TrueCentralVms.SpeakerAgent.Audio;

// ---------------------------------------------------------------------------
// TrueCentral Speaker
//
//   truecentral-speaker                         servicio (systemd / servicio de Windows)
//   truecentral-speaker enroll --server <url> --code <código>
//   truecentral-speaker forget                  olvida el enrolamiento
//   truecentral-speaker status                  estado guardado (sin conectarse)
//   truecentral-speaker version
//
// Opciones comunes: --data <carpeta> (por omisión /var/lib/truecentral-speaker o
// %ProgramData%\TrueCentral Speaker) y --web-port <puerto> (8088).
// ---------------------------------------------------------------------------

Console.OutputEncoding = System.Text.Encoding.UTF8;

string? Option(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

string command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0].ToLowerInvariant() : "run";
string dataDirectory = Option("--data") ?? AgentStore.DefaultDataDirectory();

switch (command)
{
    case "version":
        Console.WriteLine(DeviceIdentity.Version);
        return 0;

    case "status":
    {
        var store = new AgentStore(dataDirectory);
        var state = store.Snapshot;
        Console.WriteLine($"Equipo:     {Environment.MachineName} ({DeviceIdentity.Hardware})");
        Console.WriteLine($"Versión:    {DeviceIdentity.Version}");
        Console.WriteLine($"Id:         {state.AgentId}");
        Console.WriteLine($"Datos:      {dataDirectory}");
        Console.WriteLine(store.IsEnrolled
            ? $"Enrolado:   sí, en {state.ServerUrl} como «{state.SpeakerName}» ({state.EnrolledAt:yyyy-MM-dd HH:mm} UTC)"
            : "Enrolado:   no");
        Console.WriteLine($"Volumen:    {state.Volume}");
        Console.WriteLine($"Salida:     {state.OutputDevice ?? "predeterminada"}");
        return 0;
    }

    case "enroll":
    {
        string? server = Option("--server");
        string? code = Option("--code");
        if (server is null || code is null)
        {
            Console.Error.WriteLine("Uso: truecentral-speaker enroll --server http://<servidor>:5090 --code ABCD-EFGH");
            return 2;
        }
        var store = new AgentStore(dataDirectory);
        try
        {
            string name = await AgentConnection.EnrollAsync(store, server, code, CancellationToken.None);
            Console.WriteLine($"Equipo enrolado como «{name}». El servicio se conecta solo en unos segundos.");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    case "forget":
    {
        var store = new AgentStore(dataDirectory);
        store.Update(s => { s.ProtectedToken = null; s.PinnedCertificate = null; s.SpeakerId = null; s.SpeakerName = null; s.EnrolledAt = null; });
        Console.WriteLine("Enrolamiento olvidado.");
        return 0;
    }

    case "run":
        break;

    default:
        Console.Error.WriteLine($"Comando desconocido: {command}. Use enroll, forget, status o version (sin comando corre el servicio).");
        return 2;
}

int webPort = int.TryParse(Option("--web-port"), out int port) ? port : 8088;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.WebHost.UseUrls($"http://0.0.0.0:{webPort}");
builder.Services.AddWindowsService(o => o.ServiceName = "TrueCentralSpeaker");
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);

builder.Services.AddSingleton(new AgentStore(dataDirectory));
builder.Services.AddSingleton<AudioEngine>();
builder.Services.AddSingleton<AgentConnection>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentConnection>());

var app = builder.Build();
app.MapLocalWeb();
app.Logger.LogInformation("TrueCentral Speaker {Version} ({Hardware}); datos en {Data}; página local en el puerto {Port}.",
    DeviceIdentity.Version, DeviceIdentity.Hardware, dataDirectory, webPort);
await app.RunAsync();
app.Services.GetRequiredService<AudioEngine>().Dispose();
return 0;
