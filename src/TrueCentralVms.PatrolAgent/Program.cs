using TrueCentralVms.Core.Contracts;
using TrueCentralVms.PatrolAgent;

// ---------------------------------------------------------------------------
// TrueCentral Rondas: base de descarga de bastones
//
//   truecentral-patrol                                     servicio (o consola)
//   truecentral-patrol enroll --server <url> --code <código>
//   truecentral-patrol forget                              olvida el enrolamiento
//   truecentral-patrol status
//   truecentral-patrol version
//
// Opciones: --data <carpeta> (%ProgramData%\TrueCentral Rondas), --port COM7
// (repetible; por omisión todos los puertos CP210x) y --spool-erase <bloques>
// (con el servidor caído, vaciar igual sobre ese uso de memoria; 9600 = 80 %,
// 0 = nunca).
//
// Instalar como servicio (administrador):
//   sc.exe create TrueCentralRondas binPath= "C:\...\truecentral-patrol.exe" start= auto
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
        Console.WriteLine(Program.Version);
        return 0;

    case "status":
    {
        var store = new AgentStore(dataDirectory);
        var s = store.Snapshot;
        Console.WriteLine($"Versión:   {Program.Version}");
        Console.WriteLine($"Datos:     {dataDirectory}");
        Console.WriteLine(store.IsEnrolled ? $"Enrolada:  sí, en {s.ServerUrl} como «{s.AgentName}» ({s.EnrolledAt:yyyy-MM-dd HH:mm} UTC)" : "Enrolada:  no");
        Console.WriteLine($"Cola:      {store.Queued().Count()} descarga(s) pendiente(s) de entregar");
        string found = string.Join(", ", Cp210xPorts.Find(null));
        Console.WriteLine($"Puertos:   {(found.Length > 0 ? found : "ninguno CP210x conectado")}");
        return 0;
    }

    case "enroll":
    {
        string? server = Option("--server");
        string? code = Option("--code");
        if (server is null || code is null)
        {
            Console.Error.WriteLine("Uso: truecentral-patrol enroll --server https://<servidor> --code ABCD-EFGH");
            return 2;
        }
        var store = new AgentStore(dataDirectory);
        using var client = new ServerClient(server, null, null);
        try
        {
            var r = await client.EnrollAsync(new PatrolAgentEnrollRequest(code, Environment.MachineName, Program.Version), CancellationToken.None);
            store.Update(s =>
            {
                s.ServerUrl = server.TrimEnd('/');
                s.ProtectedToken = AgentStore.Protect(r.Token);
                s.PinnedSpki = client.SeenSpki;
                s.AgentId = r.AgentId;
                s.AgentName = r.AgentName;
                s.EnrolledAt = DateTime.UtcNow;
            });
            Console.WriteLine($"Base enrolada como «{r.AgentName}». Reinicie el servicio (o ejecútelo) para empezar.");
            return 0;
        }
        catch (Exception ex) when (ex is ServerRejectedException or HttpRequestException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    case "forget":
    {
        var store = new AgentStore(dataDirectory);
        store.Update(s => { s.ProtectedToken = null; s.PinnedSpki = null; s.AgentId = null; s.AgentName = null; s.EnrolledAt = null; });
        Console.WriteLine("Enrolamiento olvidado.");
        return 0;
    }

    case "run":
        break;

    default:
        Console.Error.WriteLine($"Comando desconocido: {command}. Use enroll, forget, status o version (sin comando corre el servicio).");
        return 2;
}

var ports = args.Select((a, i) => (a, i)).Where(x => x.a == "--port" && x.i + 1 < args.Length).Select(x => args[x.i + 1]).ToList();
int spoolErase = int.TryParse(Option("--spool-erase"), out int t) ? t : 9600;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "TrueCentralRondas");
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Services.AddSingleton(new AgentStore(dataDirectory));
builder.Services.AddSingleton(new AgentOptions(ports.Count > 0 ? ports : null, spoolErase));
builder.Services.AddHostedService<PatrolAgentService>();
await builder.Build().RunAsync();
return 0;

public partial class Program
{
    public static string Version =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
