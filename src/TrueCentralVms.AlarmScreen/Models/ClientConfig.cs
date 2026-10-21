using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.AlarmScreen.Models;

/// <summary>
/// Lo único que la pantalla guarda en disco. Es "tonta" a propósito: sin base de datos local
/// ni historial. Además de simplificar, protege la tarjeta SD de la Pi, que se corrompe con
/// los cortes de luz.
/// </summary>
public class ClientConfig
{
    /// <summary>Identidad estable del equipo. Se genera una vez y no cambia.</summary>
    public string MachineId { get; set; } = "";

    /// <summary>URL del servidor de TrueCentral. Vacío = buscarlo por autodiscovery.</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>Credencial entregada al emparejar. Es lo que permite el arranque automático.</summary>
    public string DeviceToken { get; set; } = "";

    /// <summary>
    /// Huella SHA-256 (hex) de la llave del servidor, fijada al emparejar: desde ahí solo se acepta ese servidor
    /// (o uno con un certificado válido para el sistema). Vacío = todavía sin fijar.
    /// </summary>
    public string PinnedServerKey { get; set; } = "";

    /// <summary>La misma huella en Base64, como la pide curl --pinnedpubkey (script de actualización nocturna).</summary>
    public string PinnedServerKeyBase64 { get; set; } = "";

    /// <summary>Nombre de la pantalla, solo para mostrarlo mientras no hay conexión.</summary>
    public string ScreenName { get; set; } = "";

    /// <summary>Nombre de la instalación, ídem.</summary>
    public string SiteName { get; set; } = "";

    /// <summary>
    /// Pantalla completa sin bordes. Encendido por omisión en Linux (la Pi de la caseta) y
    /// apagado en Windows, donde normalmente se está desarrollando o probando.
    /// </summary>
    public bool Kiosk { get; set; } = !OperatingSystem.IsWindows();

    // --- Espejo de la última configuración recibida del servidor ---
    // Se guarda para que un arranque sin red use lo mismo que ayer en lugar de los valores de
    // fábrica. La verdad vive en el servidor; esto es solo memoria de lo último que dijo.

    public int Volume { get; set; } = 80;
    public string Layout { get; set; } = AlarmScreenLayouts.Horizontal;
    public string? AudioOutput { get; set; }

    /// <summary>Cada cuántos segundos recordar que hay alarmas sin ver (0 = solo al llegar).</summary>
    public int RepeatBeepSeconds { get; set; } = 10;

    /// <summary>Cuántas veces insistir antes de callar. 0 = sin límite: suena hasta que se marque todo.</summary>
    public int MaxReminders { get; set; }

    public string? AlarmSound { get; set; }
    public string? HighPrioritySound { get; set; }
    public string? ReminderSound { get; set; }

    /// <summary>La configuración cacheada, para usar mientras no llega la del servidor.</summary>
    public AlarmScreenConfigDto ToScreenConfig() => new()
    {
        ScreenName = ScreenName,
        SiteName = SiteName,
        Volume = Volume,
        Layout = Layout,
        AudioOutput = AudioOutput,
        RepeatBeepSeconds = RepeatBeepSeconds,
        MaxReminders = MaxReminders,
        AlarmSound = AlarmSound,
        HighPrioritySound = HighPrioritySound,
        ReminderSound = ReminderSound
    };

    public void Remember(AlarmScreenConfigDto config)
    {
        if (config.ScreenName.Length > 0) ScreenName = config.ScreenName;
        if (config.SiteName.Length > 0) SiteName = config.SiteName;
        Volume = config.Volume;
        Layout = config.Layout;
        AudioOutput = config.AudioOutput;
        RepeatBeepSeconds = config.RepeatBeepSeconds;
        MaxReminders = config.MaxReminders;
        AlarmSound = config.AlarmSound;
        HighPrioritySound = config.HighPrioritySound;
        ReminderSound = config.ReminderSound;
    }
}
