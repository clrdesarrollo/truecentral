namespace TrueCentralVms.Core.Contracts;

// ---------------------------------------------------------------------------
// Licenciamiento
// ---------------------------------------------------------------------------
// El VMS se licencia contra el servidor central de licencias de CLRobotics
// (license_service_server): una licencia BASE por instalación, módulos que se
// habilitan por característica booleana y cupos por canal (cámaras, paneles,
// decodificadores, parlantes...) por característica entera. Las CLAVES de
// abajo son el contrato con el catálogo del producto "truecentral" en ese
// servidor (manage.py seed_truecentral): cambiar una aquí sin cambiarla allá
// deja el módulo sin licencia.

/// <summary>Claves de las características licenciables del producto TrueCentral.</summary>
public static class LicenseFeatures
{
    public const string ProductCode = "truecentral";

    // Base
    public const string MaxUsers = "max_users";
    public const string MaxClientSessions = "max_client_sessions";

    // Video
    public const string ModuleVideo = "module_video";
    public const string VideoChannels = "video_channels";
    public const string ModulePlayback = "module_playback";
    public const string ModuleAnpr = "module_anpr";
    public const string AnprChannels = "anpr_channels";

    // Alarmas
    public const string ModuleAlarms = "module_alarms";
    public const string AlarmPanels = "alarm_panels";

    // Control de acceso (módulo aún no implementado en el VMS: la clave existe
    // para que la licencia ya pueda venderse y el catálogo no cambie después)
    public const string ModuleAccess = "module_access";
    public const string AccessDoors = "access_doors";

    // Muro de video
    public const string ModuleVideowall = "module_videowall";
    public const string Videowalls = "videowalls";
    public const string VideowallDecoders = "videowall_decoders";

    // Audio
    public const string ModuleSpeakers = "module_speakers";
    public const string SpeakerChannels = "speaker_channels";

    // Automatización
    public const string ModuleAutomation = "module_automation";
    public const string AutomationRules = "automation_rules";

    /// <summary>
    /// Catálogo de presentación: módulo → nombre, clave del cupo asociado
    /// (null si el módulo no tiene cupo) y unidad del cupo. El orden es el de
    /// las pantallas de licencia del panel y del cliente.
    /// </summary>
    public static readonly IReadOnlyList<LicenseModuleInfo> Modules =
    [
        new(ModuleVideo, "Video en vivo", VideoChannels, "canales de video"),
        new(ModulePlayback, "Reproducción remota", null, null),
        new(ModuleAnpr, "Reconocimiento de patentes", AnprChannels, "fuentes ANPR"),
        new(ModuleAlarms, "Paneles de alarma", AlarmPanels, "paneles"),
        new(ModuleAccess, "Control de acceso", AccessDoors, "puertas"),
        new(ModuleVideowall, "Muro de video", Videowalls, "muros"),
        new(ModuleVideowall, "Decodificadores de muro", VideowallDecoders, "decodificadores"),
        new(ModuleSpeakers, "Parlantes IP", SpeakerChannels, "parlantes"),
        new(ModuleAutomation, "Automatizaciones", AutomationRules, "reglas"),
    ];

    /// <summary>Cupos que no dependen de un módulo (siempre aplican).</summary>
    public static readonly IReadOnlyList<LicenseModuleInfo> BaseQuotas =
    [
        new(null, "Usuarios del sistema", MaxUsers, "usuarios"),
        new(null, "Clientes de escritorio simultáneos", MaxClientSessions, "clientes"),
    ];
}

/// <summary>Entrada del catálogo de presentación de módulos y cupos.</summary>
public sealed record LicenseModuleInfo(string? ModuleKey, string Name, string? QuotaKey, string? Unit);

/// <summary>Estado general del licenciamiento del servidor.</summary>
public enum LicenseState
{
    /// <summary>Sin licencia ni prueba vigente: solo administración; nada operativo.</summary>
    Unlicensed,
    /// <summary>Período de prueba incorporado (cupos reducidos, todos los módulos).</summary>
    Trial,
    /// <summary>Licencia válida y al día.</summary>
    Active,
    /// <summary>Licencia válida pero sin poder revalidar en línea; sigue operativa hasta agotar la gracia.</summary>
    GracePeriod,
    /// <summary>Licencia vencida, revocada, gracia agotada o archivo inválido: modo restringido.</summary>
    Restricted,
}

/// <summary>Un módulo o cupo con su valor licenciado y su uso actual.</summary>
public sealed record LicenseModuleDto(
    string Name, string? ModuleKey, bool Enabled, string? QuotaKey, int? Quota, int? InUse, string? Unit);

/// <summary>Expansión (add-on) sumada a la licencia base.</summary>
public sealed record LicenseAddonDto(string LicenseKey, string? Package, DateTime? ExpiresAt, string Summary);

/// <summary>
/// Estado del licenciamiento para el panel y el cliente. <paramref name="Operational"/>
/// es false en modo restringido: el servidor rechaza toda operación que no sea
/// administrar la propia licencia. <paramref name="Warning"/> viene con texto
/// cuando hay algo que avisar (prueba por vencer, gracia, restricción).
/// </summary>
public sealed record LicenseStatusDto(
    LicenseState State,
    bool Operational,
    string Mode,
    string? Message,
    string? Warning,
    string? LicenseKey,
    string? CustomerName,
    string? Package,
    DateTime? IssuedAt,
    DateTime? ExpiresAt,
    DateTime? LastValidatedAt,
    DateTime? NextValidationAt,
    DateTime? GraceEndsAt,
    int? DaysRemaining,
    string HardwareId,
    string Hostname,
    string ServerVersion,
    bool OnlineConfigured,
    string? LicenseServerUrl,
    IReadOnlyList<LicenseModuleDto> Modules,
    IReadOnlyList<LicenseAddonDto> Addons);

public sealed record LicenseActivateRequest(string ActivationCode);

/// <summary>Contenido (JSON) de un archivo .lic emitido por el servidor de licencias.</summary>
public sealed record LicenseImportRequest(string LicenseFile);

/// <summary>Archivo de solicitud de activación (.req) para activar sin internet.</summary>
public sealed record LicenseActivationRequestDto(string FileName, string Content);
