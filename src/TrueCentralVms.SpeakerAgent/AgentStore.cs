using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrueCentralVms.SpeakerAgent;

/// <summary>Lo que el agente recuerda entre reinicios (state.json en <see cref="AgentStore.DataDirectory"/>).</summary>
public sealed class AgentState
{
    /// <summary>Identidad del equipo: 32 hex generados en el primer arranque. No cambia al reenrolar.</summary>
    public string AgentId { get; set; } = "";
    /// <summary>Servidor tal como lo escribió el instalador ("http://192.168.1.10:5090").</summary>
    public string? ServerUrl { get; set; }
    /// <summary>Token del equipo, protegido (DPAPI en Windows; en Linux el archivo queda con permisos 600).</summary>
    public string? ProtectedToken { get; set; }
    /// <summary>Huella SHA-256 del certificado del servidor vista al enrolarse (solo wss://): se exige la misma después.</summary>
    public string? PinnedCertificate { get; set; }
    public int? SpeakerId { get; set; }
    public string? SpeakerName { get; set; }
    public DateTime? EnrolledAt { get; set; }

    public int Volume { get; set; } = 80;
    /// <summary>Dispositivo de salida (nombre de ALSA o de Windows); null = el predeterminado.</summary>
    public string? OutputDevice { get; set; }

    /// <summary>Clave de la página local (PBKDF2-SHA256). Null = aún no se define.</summary>
    public string? LocalPasswordHash { get; set; }
    public string? LocalPasswordSalt { get; set; }
}

/// <summary>Carga y guarda <see cref="AgentState"/> de forma atómica; avisa cada cambio a quien lo necesite (la conexión, el audio).</summary>
public sealed class AgentStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private AgentState _state;

    public AgentStore(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "state.json");
        _state = LoadFromDisk();
        if (!Core.Contracts.SpeakerAgentProtocol.IsValidAgentId(_state.AgentId))
        {
            _state.AgentId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            SaveToDisk(_state);
        }
    }

    public string DataDirectory { get; }

    /// <summary>Se dispara después de guardar (fuera del candado).</summary>
    public event Action? Changed;

    /// <summary>Copia del estado actual (modificarla no cambia nada: use <see cref="Update"/>).</summary>
    public AgentState Snapshot
    {
        get
        {
            lock (_gate) return JsonSerializer.Deserialize<AgentState>(JsonSerializer.Serialize(_state))!;
        }
    }

    public void Update(Action<AgentState> change)
    {
        lock (_gate)
        {
            change(_state);
            SaveToDisk(_state);
        }
        Changed?.Invoke();
    }

    /// <summary>Vuelve a leer el archivo (lo pudo cambiar el comando <c>enroll</c> con el servicio corriendo).</summary>
    public void Reload()
    {
        lock (_gate) _state = LoadFromDisk();
        Changed?.Invoke();
    }

    public DateTime FileStamp => File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;

    public bool IsEnrolled
    {
        get { lock (_gate) return _state.ProtectedToken is not null && _state.ServerUrl is not null; }
    }

    public string? Token
    {
        get
        {
            string? stored;
            lock (_gate) stored = _state.ProtectedToken;
            if (stored is null) return null;
            try { return Unprotect(stored); }
            catch (CryptographicException) { return null; }
        }
    }

    public static string Protect(string secret)
    {
        if (!OperatingSystem.IsWindows()) return secret;
        byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.LocalMachine);
        return "dpapi:" + Convert.ToBase64String(data);
    }

    private static string Unprotect(string stored)
    {
        if (!stored.StartsWith("dpapi:", StringComparison.Ordinal)) return stored;
        if (!OperatingSystem.IsWindows()) throw new CryptographicException("Token protegido en otro sistema.");
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), null, DataProtectionScope.LocalMachine));
    }

    // ------------------------------------------------------------------
    // Clave de la página local
    // ------------------------------------------------------------------

    public bool HasLocalPassword
    {
        get { lock (_gate) return _state.LocalPasswordHash is not null; }
    }

    public void SetLocalPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        Update(s =>
        {
            s.LocalPasswordSalt = Convert.ToBase64String(salt);
            s.LocalPasswordHash = Convert.ToBase64String(hash);
        });
    }

    public bool CheckLocalPassword(string password)
    {
        string? hash, salt;
        lock (_gate) { hash = _state.LocalPasswordHash; salt = _state.LocalPasswordSalt; }
        if (hash is null || salt is null) return false;
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(salt), 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(hash));
    }

    // ------------------------------------------------------------------

    private AgentState LoadFromDisk()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<AgentState>(File.ReadAllText(_path)) ?? new AgentState() : new AgentState();
        }
        catch (JsonException)
        {
            // Archivo dañado (corte de luz a mitad de escritura): se parte de cero y hay que volver a enrolar.
            return new AgentState();
        }
    }

    private void SaveToDisk(AgentState state)
    {
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, _path, overwrite: true);
        if (OperatingSystem.IsLinux() && geteuid() == 0) GiveToDirectoryOwner(_path);
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern uint geteuid();

    /// <summary>
    /// "sudo truecentral-speaker enroll" escribe como root: el archivo se devuelve al dueño de la carpeta de datos
    /// (el usuario del servicio), si no el servicio no podría leer su propio token.
    /// </summary>
    private void GiveToDirectoryOwner(string path)
    {
        try
        {
            using var chown = System.Diagnostics.Process.Start("chown", ["--reference", DataDirectory, path]);
            chown?.WaitForExit(5000);
        }
        catch (Exception) { /* sin chown: queda como estaba */ }
    }

    /// <summary>Carpeta de datos por omisión del sistema.</summary>
    public static string DefaultDataDirectory()
    {
        if (Environment.GetEnvironmentVariable("TRUECENTRAL_SPEAKER_DATA") is { Length: > 0 } fromEnv) return fromEnv;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TrueCentral Speaker");
        const string system = "/var/lib/truecentral-speaker";
        try
        {
            Directory.CreateDirectory(system);
            string probe = Path.Combine(system, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return system;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Sin root (desarrollo): en la carpeta del usuario.
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "truecentral-speaker");
        }
    }
}
