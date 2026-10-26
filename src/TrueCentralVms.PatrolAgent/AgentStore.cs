using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.PatrolAgent;

/// <summary>Lo que la base recuerda entre reinicios (state.json en la carpeta de datos).</summary>
public sealed class AgentState
{
    public string? ServerUrl { get; set; }
    /// <summary>Token del agente, protegido con DPAPI de máquina.</summary>
    public string? ProtectedToken { get; set; }
    /// <summary>SHA-256 de la clave pública del certificado del servidor, fijada al enrolarse.</summary>
    public string? PinnedSpki { get; set; }
    public int? AgentId { get; set; }
    public string? AgentName { get; set; }
    public DateTime? EnrolledAt { get; set; }
}

/// <summary>
/// Estado del agente y cola local de descargas. Una descarga se escribe en la
/// cola ANTES de subirla y se borra de la cola solo cuando el servidor confirma
/// que la guardó: si se corta la luz o la red, no se pierde.
/// </summary>
public sealed class AgentStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private AgentState _state;

    public AgentStore(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        QueueDirectory = Path.Combine(dataDirectory, "cola");
        Directory.CreateDirectory(QueueDirectory);
        _path = Path.Combine(dataDirectory, "state.json");
        _state = File.Exists(_path) ? JsonSerializer.Deserialize<AgentState>(File.ReadAllText(_path), Json) ?? new() : new();
    }

    public string DataDirectory { get; }
    public string QueueDirectory { get; }

    public static string DefaultDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TrueCentral Rondas");

    public AgentState Snapshot
    {
        get { lock (_gate) return JsonSerializer.Deserialize<AgentState>(JsonSerializer.Serialize(_state, Json), Json)!; }
    }

    public void Update(Action<AgentState> change)
    {
        lock (_gate)
        {
            change(_state);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, Json));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    public bool IsEnrolled { get { lock (_gate) return _state.ProtectedToken is not null && _state.ServerUrl is not null; } }

    public string? Token
    {
        get
        {
            string? stored;
            lock (_gate) stored = _state.ProtectedToken;
            if (stored is null) return null;
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.LocalMachine));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
        }
    }

    public static string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.LocalMachine));

    // ------------------------------------------------------------------
    // Cola local

    /// <summary>Guarda una descarga en la cola (escritura atómica). Devuelve la ruta.</summary>
    public string Enqueue(PatrolAgentUpload upload)
    {
        string name = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{upload.Serial:D6}.json";
        string path = Path.Combine(QueueDirectory, name);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(upload, Json));
        File.Move(path + ".tmp", path);
        return path;
    }

    public IEnumerable<(string Path, PatrolAgentUpload Upload)> Queued()
    {
        foreach (var path in Directory.GetFiles(QueueDirectory, "*.json").Order())
        {
            PatrolAgentUpload? upload = null;
            try { upload = JsonSerializer.Deserialize<PatrolAgentUpload>(File.ReadAllText(path), Json); }
            catch (JsonException) { File.Move(path, path + ".danado", overwrite: true); }
            if (upload is not null) yield return (path, upload);
        }
    }

    public static void Dequeue(string path) { if (File.Exists(path)) File.Delete(path); }
}
