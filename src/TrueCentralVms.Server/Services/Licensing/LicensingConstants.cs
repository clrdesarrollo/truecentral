using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Services.Licensing;

/// <summary>
/// Constantes del licenciamiento que viajan COMPILADAS en el binario.
///
/// La clave pública Ed25519 del producto "truecentral" es la única confianza
/// del VMS: un archivo .lic solo se acepta si su firma verifica con ella. Por
/// eso no puede venir de appsettings (quien pudiera cambiarla firmaría sus
/// propias licencias). Se obtiene del servidor de licencias con
/// <c>GET /api/v1/products/public-key/</c> (o al importar el catálogo con
/// <c>manage.py sync_catalog</c>) y se pega aquí ANTES de compilar el
/// instalador de producción. Cada servidor de licencias (desarrollo,
/// producción) tiene su propio par: si se rota la clave hay que recompilar
/// y reemitir los archivos de licencia.
///
/// Solo en compilaciones DEBUG se admite <c>Licensing:PublicKey</c> en
/// appsettings para apuntar a un servidor de licencias de desarrollo.
/// </summary>
public static class LicensingConstants
{
    /// <summary>Clave pública Ed25519 (base64, 32 bytes) del producto en el servidor de licencias.</summary>
    public const string ProductPublicKey = "ffn1G8fb7oYwqlL9qmQRhdektXThw52i+DLxfr/1VR8=";

    /// <summary>Esquema de archivo .lic que este VMS entiende (v2 = con signed_payload).</summary>
    public const int SupportedLicenseSchema = 2;

    /// <summary>Esquema del archivo de solicitud de activación (.req) que genera el VMS.</summary>
    public const int ActivationRequestSchema = 1;

    /// <summary>
    /// Días del período de prueba incorporado. En Release es fijo (no se lee
    /// de appsettings, donde cualquiera podría alargarlo); solo DEBUG admite
    /// <c>Licensing:TrialDays</c> para probar.
    /// </summary>
    public const int TrialDays = 30;

    /// <summary>
    /// Secreto del que se deriva (junto con el identificador del equipo) la
    /// llave AES-GCM que cifra y autentica el estado local del licenciamiento
    /// (ver <see cref="LicenseStateVault"/>). No es una clave de firma: solo
    /// hace que el estado no se pueda leer ni editar con un editor de texto, ni
    /// traerse de otro equipo. Cambiarlo invalida el estado guardado de todas
    /// las instalaciones (se reconstruye desde la bitácora, ver LicenseService).
    /// </summary>
    internal const string StateSealSecret = "ouWGjQdEF9jmhSYLTaSqKlhrrXwhE/VwnIUf7zRyH6M=";

    /// <summary>
    /// Cupos del período de prueba incorporado: todos los módulos disponibles
    /// con cupos chicos, para evaluar el producto completo antes de comprar.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, object> TrialFeatures = new Dictionary<string, object>
    {
        [LicenseFeatures.MaxUsers] = 5,
        [LicenseFeatures.MaxClientSessions] = 3,
        [LicenseFeatures.ModuleVideo] = true,
        [LicenseFeatures.VideoChannels] = 16,
        [LicenseFeatures.ModulePlayback] = true,
        [LicenseFeatures.ModuleAnpr] = true,
        [LicenseFeatures.AnprChannels] = 2,
        [LicenseFeatures.ModuleAlarms] = true,
        [LicenseFeatures.AlarmPanels] = 2,
        [LicenseFeatures.ModuleFence] = true,
        [LicenseFeatures.FencePanels] = 2,
        [LicenseFeatures.ModuleAccess] = true,
        [LicenseFeatures.AccessDoors] = 4,
        [LicenseFeatures.ModuleVideowall] = true,
        [LicenseFeatures.Videowalls] = 1,
        [LicenseFeatures.VideowallDecoders] = 2,
        [LicenseFeatures.ModuleSpeakers] = true,
        [LicenseFeatures.SpeakerChannels] = 4,
        [LicenseFeatures.ModuleIntercom] = true,
        [LicenseFeatures.IntercomDevices] = 2,
        [LicenseFeatures.ModuleIo] = true,
        [LicenseFeatures.IoModules] = 2,
        [LicenseFeatures.ModuleAlarmScreens] = true,
        [LicenseFeatures.AlarmScreens] = 2,
        [LicenseFeatures.ModuleAutomation] = true,
        [LicenseFeatures.AutomationRules] = 10,
    };
}
