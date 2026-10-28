using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrueCentralVms.Client.Services;

/// <summary>
/// Cuenta recordada en este equipo. La contraseña (si el usuario pidió
/// recordarla) se guarda cifrada con DPAPI: solo este usuario de Windows en
/// esta máquina puede descifrarla — el archivo de ajustes nunca contiene
/// contraseñas en texto plano.
/// </summary>
public sealed class SavedAccount
{
    public string ServerUrl { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>Contraseña cifrada (Base64 de DPAPI); null = no se guardó.</summary>
    public string? ProtectedPassword { get; set; }
    public DateTime LastLoginUtc { get; set; }

    public string DisplayLabel => $"{Username} — {ServerUrl.Replace("http://", "").Replace("https://", "")}";
}

/// <summary>
/// Lo que había en la Vista en Vivo: la grilla principal y cada pantalla
/// auxiliar, con la cámara y el stream de cada cuadro. Es de UN servidor y UN
/// usuario: otra persona que ingrese en este puesto no hereda las cámaras del
/// anterior (puede no tener permiso para verlas).
/// </summary>
public sealed class LiveSession
{
    public string ServerUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public DateTime SavedAtUtc { get; set; }
    /// <summary>La viñeta de Vista en Vivo estaba abierta.</summary>
    public bool LiveViewOpen { get; set; }
    public LiveSessionGrid Main { get; set; } = new();
    public List<LiveSessionScreen> AuxScreens { get; set; } = [];

    /// <summary>Hay algo que reabrir.</summary>
    [JsonIgnore]
    public bool HasCameras => Main.Cells.Count > 0 || AuxScreens.Any(s => s.Cells.Count > 0);
}

/// <summary>Una grilla: su división y qué cámara había en cada cuadro.</summary>
public class LiveSessionGrid
{
    /// <summary>Clave de la división ("4", "13") o la de una grilla a medida.</summary>
    public string LayoutName { get; set; } = "";
    public int Columns { get; set; }
    public int Rows { get; set; }
    public List<LiveSessionCell> Cells { get; set; } = [];
}

/// <summary>Un cuadro con cámara: posición (0..n-1), id del canal y stream (0 principal, 1 secundario).</summary>
public sealed class LiveSessionCell
{
    public int Index { get; set; }
    public int ChannelId { get; set; }
    public int Profile { get; set; }
}

/// <summary>Una pantalla auxiliar: su grilla y dónde estaba su ventana.</summary>
public sealed class LiveSessionScreen : LiveSessionGrid
{
    public int Slot { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>Lugar de una ventana en el escritorio (unidades de WPF) y si estaba maximizada.</summary>
public sealed class WindowBounds
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>Ajustes de un modelo de joystick PTZ (ver <see cref="ClientSettings.Joysticks"/>).</summary>
public sealed class JoystickConfig
{
    /// <summary>Mueve la cámara PTZ del cuadro seleccionado.</summary>
    public bool Enabled { get; set; } = true;
    public bool InvertY { get; set; }
    public bool InvertZoom { get; set; }
    /// <summary>
    /// Sigue moviendo la cámara aunque el cliente no esté en primer plano (el
    /// operador trabaja en otro programa con el joystick dedicado). Apagado, el
    /// joystick solo actúa con una ventana del cliente activa.
    /// </summary>
    public bool InBackground { get; set; }
    /// <summary>Acción de cada botón: índice desde 0 → clave de <c>JoystickActions</c> (las mismas del panel web).</summary>
    public Dictionary<int, string> Buttons { get; set; } = [];
}

/// <summary>Preferencias locales del cliente (%AppData%\CLRTrueCentralVMS\client.json).</summary>
public sealed class ClientSettings
{
    private const int MaxAccounts = 8;

    public string ServerUrl { get; set; } = "https://localhost";
    public string Username { get; set; } = "";
    public bool RememberPassword { get; set; }
    public bool AutoLogin { get; set; }
    /// <summary>Última división de pantalla usada en Vista en Vivo (nombre del
    /// layout, ej. "4", "6", "13"); null = la por defecto.</summary>
    public string? LastLayout { get; set; }
    /// <summary>Últimos inicios de sesión, el más reciente primero.</summary>
    public List<SavedAccount> Accounts { get; set; } = [];

    // ---------- Configuración (ventana de ajustes) ----------

    /// <summary>Carpeta de capturas de imagen; null/vacío = Imágenes\TrueCentral VMS.</summary>
    public string? SnapshotFolder { get; set; }
    /// <summary>Carpeta de grabaciones locales; null/vacío = Videos\TrueCentral VMS.</summary>
    public string? RecordingFolder { get; set; }
    /// <summary>Formato de las capturas: "jpg" o "png".</summary>
    public string SnapshotFormat { get; set; } = "jpg";
    /// <summary>Volumen inicial del audio de los cuadros (0..100).</summary>
    public int DefaultVolume { get; set; } = 75;
    /// <summary>Stream al abrir un canal: "auto" (main ≤4 cuadros), "main" o "sub".</summary>
    public string DefaultProfile { get; set; } = "auto";
    /// <summary>true = estirar el video al cuadro (sin barras negras); false = mantener proporción.</summary>
    public bool StretchVideo { get; set; }
    /// <summary>Al abrir un equipo completo: true = grilla a medida (columnas×filas
    /// sin cuadros de sobra); false = la división estándar más cercana.</summary>
    public bool FitGridToDevice { get; set; } = true;
    /// <summary>Flota en vivo: minutos sin teclado ni mouse en el PC tras los que se corta el video de los vehículos
    /// (consume sus datos móviles). 0 = nunca.</summary>
    public int FleetIdleMinutes { get; set; } = 5;
    /// <summary>Tiempo de espera de la API en segundos (se aplica al iniciar la aplicación).</summary>
    public int ApiTimeoutSeconds { get; set; } = 20;
    /// <summary>Micrófono para hablar por los parlantes IP (nombre del dispositivo); null = el predeterminado.</summary>
    public string? MicrophoneDevice { get; set; }
    /// <summary>Tono de apertura (dos pitidos cortos) antes de la voz al hablar por los parlantes IP.</summary>
    public bool TalkPreTone { get; set; } = true;
    /// <summary>Árbol de cámaras de Vista en vivo y Reproducción: true = por
    /// ubicación (Recursos del servidor), false = por equipo.</summary>
    public bool TreeByLocation { get; set; }
    /// <summary>
    /// Al iniciar sesión, volver a abrir las cámaras de la última sesión (grilla
    /// principal y pantallas auxiliares). Sirve para que un cierre inesperado —o
    /// uno a propósito— no obligue a cargar cámara por cámara de nuevo.
    /// </summary>
    public bool RestoreLastSession { get; set; }
    /// <summary>Lo que había en pantalla la última vez (se guarda a medida que cambia).</summary>
    public LiveSession? LastSession { get; set; }

    // ---------- Apariencia ----------

    /// <summary>
    /// Diseño del cliente: <see cref="ClientTheme.Console"/> (por defecto desde
    /// 2026-10-18: consola técnica con cifras en monoespaciada, telemetría por
    /// cuadro y botonera abajo) o <see cref="ClientTheme.Classic"/>
    /// (el diseño anterior, a elección en Configuración). Un client.json sin este
    /// dato abre la Consola. Se aplica al abrir el cliente: los estilos se
    /// resuelven al crear cada ventana.
    /// </summary>
    public string Appearance { get; set; } = ClientTheme.Console;
    /// <summary>Paleta de la Consola (<see cref="ConsolePalettes"/>): "grafito" (por defecto), "control", "petroleo" o "ambar".</summary>
    public string ConsolePalette { get; set; } = ConsolePalettes.Default;

    // ---------- Viñetas de la ventana principal ----------

    /// <summary>Orden de las viñetas de módulos elegido por el operador
    /// arrastrándolas (claves de sección: Home, Live, Playback…). Las que no
    /// están en la lista van al final, en su orden de siempre.</summary>
    public List<string> TabOrder { get; set; } = [];

    // ---------- Lugar de las ventanas ----------

    /// <summary>
    /// Abrir cada ventana donde quedó la última vez (monitor, posición, tamaño
    /// y maximizada) en vez de centrada: en los puestos con varias pantallas
    /// los avisos de alarma y de citofonía no saltan de monitor. Los lugares
    /// van en windows.json (ver <see cref="WindowPlacements"/>).
    /// </summary>
    public bool RememberWindowPlacement { get; set; } = true;
    /// <summary>Lugar de la ventana de alarma de las versiones anteriores a
    /// windows.json: solo se lee, para estrenar la ventana donde estaba.</summary>
    public WindowBounds? AlertWindowBounds { get; set; }

    // ---------- Exportación de grabaciones (diálogo Exportar) ----------

    /// <summary>Última carpeta de exportación elegida; null/vacío = carpeta de grabaciones.</summary>
    public string? ExportFolder { get; set; }
    /// <summary>Último formato de exportación: "mp4" o "mkv".</summary>
    public string ExportFormat { get; set; } = "mp4";
    /// <summary>Última división elegida en minutos por archivo; 0 = un solo archivo.</summary>
    public int ExportSplitMinutes { get; set; }

    // ---------- Muro de video ----------

    /// <summary>Lista de cámaras del muro plegada (pestaña « » del borde).</summary>
    public bool WallSourcesPanelCollapsed { get; set; }
    /// <summary>
    /// Última IP local que funcionó al proyectar la pantalla. En redes con
    /// router la IP correcta es la que ve el decodificador, y no se puede
    /// deducir: se recuerda la elegida y se ofrece primero.
    /// </summary>
    public string? ProjectionIp { get; set; }
    /// <summary>Reproducir en bucle el archivo de video proyectado.</summary>
    public bool ProjectionLoop { get; set; }

    // ---------- Joystick PTZ ----------

    /// <summary>Ajustes por modelo de joystick (clave "VID_xxxx&amp;PID_xxxx"): otro joystick no hereda los botones.</summary>
    public Dictionary<string, JoystickConfig> Joysticks { get; set; } = [];

    /// <summary>Ajustes del joystick <paramref name="id"/> (se crean con los valores por defecto).</summary>
    public JoystickConfig JoystickFor(string id) =>
        Joysticks.TryGetValue(id, out var config) ? config : Joysticks[id] = new JoystickConfig();

    public static string DefaultSnapshotFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "TrueCentral VMS");

    public static string DefaultRecordingFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "TrueCentral VMS");

    [JsonIgnore]
    public string EffectiveSnapshotFolder =>
        string.IsNullOrWhiteSpace(SnapshotFolder) ? DefaultSnapshotFolder : SnapshotFolder;

    [JsonIgnore]
    public string EffectiveRecordingFolder =>
        string.IsNullOrWhiteSpace(RecordingFolder) ? DefaultRecordingFolder : RecordingFolder;

    [JsonIgnore]
    public string EffectiveExportFolder =>
        string.IsNullOrWhiteSpace(ExportFolder) ? EffectiveRecordingFolder : ExportFolder;

    /// <summary>Ruta de client.json (windows.json va al lado).</summary>
    public static string FileLocation => FilePath;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CLRTrueCentralVMS", "client.json");

    public static ClientSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return (JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(FilePath)) ?? new()).UpgradeServerUrls();
        }
        catch { /* preferencias corruptas: se parte de cero */ }
        return new() { ServerUrl = InstalledServerUrl() ?? "https://localhost" };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* mejor esfuerzo */ }
    }

    /// <summary>
    /// Desde la 0.5.22 el servidor ya no atiende en http://…:5090 sino en https (443): las direcciones guardadas
    /// con el puerto viejo pasan a la nueva, así las cuentas y contraseñas recordadas siguen calzando.
    /// </summary>
    private ClientSettings UpgradeServerUrls()
    {
        ServerUrl = UpgradeUrl(ServerUrl);
        foreach (var account in Accounts) account.ServerUrl = UpgradeUrl(account.ServerUrl);
        if (LastSession is not null) LastSession.ServerUrl = UpgradeUrl(LastSession.ServerUrl);
        return this;
    }

    /// <summary>Dirección del servidor del que se descargó el instalador (la deja el instalador), si la hay.</summary>
    private static string? InstalledServerUrl()
    {
        try
        {
            string path = Path.Combine(ServerTrust.MachineDirectory, "server-url.txt");
            return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } url
                && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? url : null;
        }
        catch { return null; }
    }

    private static string UpgradeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.Port == 5090
            ? (uri.HostNameType == UriHostNameType.IPv6 ? $"https://[{uri.IdnHost}]" : $"https://{uri.IdnHost}")
            : url;

    public SavedAccount? FindAccount(string serverUrl, string username) =>
        Accounts.FirstOrDefault(a =>
            a.ServerUrl.Equals(serverUrl, StringComparison.OrdinalIgnoreCase) &&
            a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Registra un inicio de sesión exitoso: actualiza o crea la cuenta, cifra
    /// la contraseña solo si se pidió recordarla y conserva las más recientes.
    /// </summary>
    public void RecordLogin(string serverUrl, string username, string? passwordToRemember)
    {
        var account = FindAccount(serverUrl, username);
        if (account is null)
        {
            account = new SavedAccount();
            Accounts.Add(account);
        }
        account.ServerUrl = serverUrl;
        account.Username = username;
        account.LastLoginUtc = DateTime.UtcNow;
        account.ProtectedPassword = passwordToRemember is null ? null : CredentialVault.Protect(passwordToRemember);

        Accounts = Accounts.OrderByDescending(a => a.LastLoginUtc).Take(MaxAccounts).ToList();
        ServerUrl = serverUrl;
        Username = username;
        Save();
    }

    public void RemoveAccount(SavedAccount account)
    {
        Accounts.Remove(account);
        Save();
    }
}

/// <summary>
/// Cifrado local de credenciales con DPAPI (CurrentUser): la llave la
/// administra Windows y está ligada al perfil del usuario, así que el blob no
/// sirve copiado a otra máquina ni a otro usuario del mismo equipo.
/// </summary>
internal static class CredentialVault
{
    // Entropía propia de la aplicación: un blob DPAPI de otro programa no es
    // intercambiable con el nuestro aunque corra bajo el mismo usuario.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CLRTrueCentralVMS.credential.v1");

    public static string? Protect(string plaintext)
    {
        try
        {
            byte[] blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(blob);
        }
        catch
        {
            return null; // sin DPAPI disponible es mejor no recordar que guardar en claro
        }
    }

    public static string? Unprotect(string? base64Blob)
    {
        if (string.IsNullOrEmpty(base64Blob)) return null;
        try
        {
            byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(base64Blob), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null; // blob de otra máquina/usuario o corrupto: se pide la clave de nuevo
        }
    }
}
