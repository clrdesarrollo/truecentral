namespace TrueCentralVms.Core.Contracts;

// ---------------------------------------------------------------------------
// Licenciamiento
// ---------------------------------------------------------------------------
// El VMS se licencia contra el servidor central de licencias de CLRobotics
// (license_service_server): una licencia BASE por instalación, módulos que se
// habilitan por característica booleana y cupos por canal (cámaras, paneles,
// decodificadores, parlantes...) por característica entera. Las CLAVES de
// abajo son el contrato con el producto "truecentral" de ese servidor: cada
// una se define (nombre, tipo, valor por defecto, packages) en LicenseCatalog,
// que el servidor de licencias importa; no se cargan a mano en su web.

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

    // Cerco eléctrico (paneles energizadores propios de CLRobotics)
    public const string ModuleFence = "module_fence";
    public const string FencePanels = "fence_panels";

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

    // Citofonía
    public const string ModuleIntercom = "module_intercom";
    public const string IntercomDevices = "intercom_devices";

    // Entradas y salidas (módulos de E/S digital: relés de barreras, portones, balizas)
    public const string ModuleIo = "module_io";
    public const string IoModules = "io_modules";

    // Control de rondas (bastones de ronda y puntos de control): el módulo y dos cupos.
    public const string ModulePatrol = "module_patrol";
    public const string PatrolReaders = "patrol_readers";
    public const string PatrolCheckpoints = "patrol_checkpoints";

    // Pantallas de alarma (Raspberry Pi de las casetas que muestran los avisos)
    public const string ModuleAlarmScreens = "module_alarm_screens";
    public const string AlarmScreens = "alarm_screens";

    // Automatización
    public const string ModuleAutomation = "module_automation";
    public const string AutomationRules = "automation_rules";

    // Flota (DVR móviles en vehículos, por ISUP): se licencia por vehículo; sus canales no consumen video_channels.
    public const string ModuleFleet = "module_fleet";
    public const string FleetVehicles = "fleet_vehicles";

    // API de integración: sistemas externos (el kiosco de un casino, remuneraciones) que leen el padrón con
    // credenciales propias. Se licencia por aplicación habilitada.
    public const string ModuleIntegrationApi = "module_integration_api";
    public const string IntegrationApps = "integration_apps";

    /// <summary>Tope de canales habilitados por vehículo: un grabador fijo no debe poder darse de alta como vehículo para
    /// esquivar el cupo de canales de video.</summary>
    public const int MaxChannelsPerVehicle = 16;

    // Áreas en que se agrupan módulos y cupos en las pantallas de licencia.
    public const string AreaPlatform = "Plataforma";
    public const string AreaVideo = "Video";
    public const string AreaIntrusion = "Intrusión y perímetro";
    public const string AreaAccess = "Control de acceso";
    public const string AreaAudio = "Audio y comunicación";
    public const string AreaAutomation = "Automatización";
    public const string AreaSecurity = "Seguridad y guardias";

    /// <summary>
    /// Catálogo de presentación: módulo → nombre, clave del cupo asociado
    /// (null si el módulo no tiene cupo), unidad del cupo y área. El orden es
    /// el de las pantallas de licencia del panel y del cliente.
    /// </summary>
    public static readonly IReadOnlyList<LicenseModuleInfo> Modules =
    [
        new(ModuleVideo, "Video en vivo", VideoChannels, "canales de video", AreaVideo),
        new(ModulePlayback, "Reproducción remota", null, null, AreaVideo),
        new(ModuleAnpr, "Reconocimiento de patentes", AnprChannels, "fuentes ANPR", AreaVideo),
        new(ModuleVideowall, "Muro de video", Videowalls, "muros", AreaVideo),
        new(ModuleVideowall, "Decodificadores de muro", VideowallDecoders, "decodificadores", AreaVideo),
        new(ModuleFleet, "Flota (DVR móviles)", FleetVehicles, "vehículos", AreaVideo),
        new(ModuleAlarms, "Paneles de alarma", AlarmPanels, "paneles", AreaIntrusion),
        new(ModuleFence, "Cerco eléctrico", FencePanels, "paneles de cerco", AreaIntrusion),
        new(ModuleAlarmScreens, "Pantallas de alarma", AlarmScreens, "pantallas", AreaIntrusion),
        new(ModuleAccess, "Control de acceso", AccessDoors, "puertas", AreaAccess),
        new(ModuleSpeakers, "Parlantes IP", SpeakerChannels, "parlantes", AreaAudio),
        new(ModuleIntercom, "Citofonía", IntercomDevices, "frentes", AreaAudio),
        new(ModuleIo, "Módulos de E/S", IoModules, "módulos", AreaAutomation),
        new(ModuleAutomation, "Automatizaciones", AutomationRules, "reglas", AreaAutomation),
        new(ModulePatrol, "Control de rondas", PatrolReaders, "bastones", AreaSecurity),
        new(ModulePatrol, "Puntos de control de ronda", PatrolCheckpoints, "puntos", AreaSecurity),
        new(ModuleIntegrationApi, "API de integración", IntegrationApps, "aplicaciones", AreaPlatform),
    ];

    /// <summary>Cupos que no dependen de un módulo (siempre aplican).</summary>
    public static readonly IReadOnlyList<LicenseModuleInfo> BaseQuotas =
    [
        new(null, "Usuarios del sistema", MaxUsers, "usuarios", AreaPlatform),
        new(null, "Clientes de escritorio simultáneos", MaxClientSessions, "clientes", AreaPlatform),
    ];
}

/// <summary>Entrada del catálogo de presentación de módulos y cupos.</summary>
public sealed record LicenseModuleInfo(string? ModuleKey, string Name, string? QuotaKey, string? Unit, string Area);

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
    string Name, string? ModuleKey, bool Enabled, string? QuotaKey, int? Quota, int? InUse, string? Unit,
    string Area = "");

/// <summary>Expansión (add-on) sumada a la licencia base.</summary>
public sealed record LicenseAddonDto(string LicenseKey, string? Package, DateTime? ExpiresAt, string Summary);

/// <summary>
/// Un código instalado (la licencia base o una de sus expansiones) con lo que
/// aporta ESE código: <paramref name="Kind"/> es "Base" o "Expansion".
/// </summary>
public sealed record LicenseCodeDto(
    string LicenseKey, string Kind, string? Package, DateTime? IssuedAt, DateTime? ExpiresAt,
    string Summary, IReadOnlyList<LicenseCodeItemDto> Items);

/// <summary>Lo que un código aporta en un módulo o cupo ("Incluido", "+16 canales de video").</summary>
public sealed record LicenseCodeItemDto(string Area, string Name, string Value);

/// <summary>
/// Ingreso único de códigos: la licencia base y sus expansiones en una sola
/// lista; el servidor reconoce cuál es cuál.
/// </summary>
public sealed record LicenseCodesRequest(string? ActivationCode = null, IReadOnlyList<string>? Codes = null);

/// <summary>Resultado de aplicar una lista de códigos, uno por código.</summary>
public sealed record LicenseApplyResultDto(bool Success, string Message, IReadOnlyList<LicenseApplyItemDto> Items);

/// <summary>
/// <paramref name="Kind"/>: "Base", "Expansion" o null si no se pudo reconocer.
/// <paramref name="Step"/>: lo que se intentó con el código: "activate" (base
/// nueva), "refresh" (la base ya instalada) o "addon" (expansión).
/// </summary>
public sealed record LicenseApplyItemDto(string Code, string? Kind, bool Success, string Message, string Step);

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
    IReadOnlyList<LicenseAddonDto> Addons,
    IReadOnlyList<LicenseCodeDto>? Codes = null);

public sealed record LicenseActivateRequest(string ActivationCode);

/// <summary>Contenido (JSON) de un archivo .lic emitido por el servidor de licencias.</summary>
public sealed record LicenseImportRequest(string LicenseFile);

/// <summary>Archivo de solicitud de activación (.req) para activar sin internet.</summary>
public sealed record LicenseActivationRequestDto(string FileName, string Content);
