using System.IO;
using System.Text.Json;

namespace TrueCentralVms.Watchdog;

/// <summary>
/// Localiza la instalación del servidor y lee su configuración real (puerto web
/// y puerto de PostgreSQL) para monitorear exactamente lo que está instalado.
/// El Watchdog se instala en {app}\watchdog, así que el servidor se busca en la
/// carpeta del ejecutable y en su carpeta padre.
/// </summary>
public sealed class ServerEnvironment
{
    public string ServerDirectory { get; private init; } = "";
    public bool ServerFound { get; private init; }
    public string BaseUrl { get; private init; } = $"http://localhost:{DefaultWebPort}";
    public int WebPort { get; private init; } = DefaultWebPort;
    public int PgPort { get; private init; } = DefaultPgPort;

    /// <summary>Puerto web del servidor (el mismo del panel de administración).</summary>
    private const int DefaultWebPort = 5090;

    /// <summary>Puerto por defecto del PostgreSQL embebido (privado, solo 127.0.0.1).</summary>
    private const int DefaultPgPort = 25490;

    /// <summary>Ejecutable del servidor: delata cuál de las carpetas candidatas es la instalación.</summary>
    private const string ServerExeName = "TrueCentralVms.Server.exe";

    /// <summary>
    /// URL de sondeo del estado. Va por 127.0.0.1 y no por "localhost" a
    /// propósito: si el servidor quedara atado solo a IPv4, resolver "localhost"
    /// a ::1 cuesta ~2 s por intento y el sondeo agotaría su timeout, marcando
    /// como caída una API que está perfectamente viva.
    /// </summary>
    public string HealthUrl => $"http://127.0.0.1:{WebPort}/api/health";

    public static ServerEnvironment Discover()
    {
        string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var candidates = new List<string> { exeDir };
        if (Path.GetDirectoryName(exeDir) is { } parent)
            candidates.Add(parent);

        string? serverDir = candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, ServerExeName)))
                         ?? candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "appsettings.json")));
        if (serverDir is null)
            return new ServerEnvironment { ServerDirectory = exeDir, ServerFound = false };

        // Misma precedencia que el servidor instalado (entorno Production):
        // appsettings.json < appsettings.Production.json < appsettings.Local.json.
        string? urls = null;
        int pgPort = DefaultPgPort;
        string? dataDir = null;
        foreach (string name in new[] { "appsettings.json", "appsettings.Production.json", "appsettings.Local.json" })
        {
            try
            {
                string path = Path.Combine(serverDir, name);
                if (!File.Exists(path))
                    continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
                if (doc.RootElement.TryGetProperty("Urls", out var u) && u.ValueKind == JsonValueKind.String)
                    urls = u.GetString();
                if (doc.RootElement.TryGetProperty("Database", out var db) && db.ValueKind == JsonValueKind.Object)
                {
                    if (db.TryGetProperty("PgPort", out var p) && p.TryGetInt32(out int port))
                        pgPort = port;
                    if (db.TryGetProperty("PgDataDir", out var d) && d.ValueKind == JsonValueKind.String)
                        dataDir = d.GetString();
                }
            }
            catch
            {
                // Configuración ilegible: se sigue con la anterior/valores por defecto.
            }
        }

        // El clúster ya creado manda sobre la configuración (misma regla que el
        // servidor): si no, al cambiar el puerto por defecto el Watchdog daría
        // por caída una base que está perfectamente viva en su puerto de siempre.
        if (ReadClusterPort(serverDir, dataDir) is int clusterPort)
            pgPort = clusterPort;

        int webPort = ParseWebPort(urls) ?? DefaultWebPort;
        return new ServerEnvironment
        {
            ServerDirectory = serverDir,
            ServerFound = true,
            WebPort = webPort,
            PgPort = pgPort,
            BaseUrl = $"http://localhost:{webPort}",
        };
    }

    /// <summary>
    /// Puerto real del clúster, leído de su postgresql.conf (null si no hay
    /// clúster o no se puede leer). El Watchdog corre elevado, así que puede
    /// entrar a la carpeta de datos aunque esté restringida a SYSTEM/Admins.
    /// </summary>
    private static int? ReadClusterPort(string serverDir, string? dataDir)
    {
        try
        {
            string dir = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(string.IsNullOrWhiteSpace(dataDir) ? "pgdata" : dataDir),
                serverDir);
            if (!File.Exists(Path.Combine(dir, "PG_VERSION")))
                return null;

            int? found = null;
            foreach (string raw in File.ReadLines(Path.Combine(dir, "postgresql.conf")))
            {
                string line = raw.Trim();
                if (!line.StartsWith("port", StringComparison.OrdinalIgnoreCase))
                    continue;
                string rest = line[4..].TrimStart();
                if (!rest.StartsWith('='))
                    continue;
                string value = rest[1..].Trim();
                int comment = value.IndexOf('#');
                if (comment >= 0)
                    value = value[..comment].Trim();
                if (int.TryParse(value, out int parsed) && parsed > 0)
                    found = parsed; // gana la última, como hace PostgreSQL
            }
            return found;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Primer puerto http de la lista de Urls ("http://*:5090;...").</summary>
    private static int? ParseWebPort(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls))
            return null;
        foreach (string raw in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate = raw.Replace("0.0.0.0", "localhost").Replace("[::]", "localhost")
                                  .Replace("+", "localhost").Replace("*", "localhost");
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp)
                return uri.Port;
        }
        return null;
    }
}
