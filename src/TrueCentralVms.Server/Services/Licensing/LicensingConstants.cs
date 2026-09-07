using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Services.Licensing;

/// <summary>
/// Constantes del licenciamiento que viajan COMPILADAS en el binario.
///
/// La clave pública Ed25519 del producto "truecentral" es la única confianza
/// del VMS: un archivo .lic solo se acepta si su firma verifica con ella. Por
/// eso no puede venir de appsettings (quien pudiera cambiarla firmaría sus
/// propias licencias). Se obtiene del servidor de licencias con
/// <c>GET /api/v1/products/public-key/</c> (o al correr
/// <c>manage.py seed_truecentral</c>) y se pega aquí ANTES de compilar el
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
    public const string ProductPublicKey = "5hLQ1i/BDE9TedHERGAeeivqb+eNiUFT/1o2tLDMH/M=";

    /// <summary>Esquema de archivo .lic que este VMS entiende (v2 = con signed_payload).</summary>
    public const int SupportedLicenseSchema = 2;

    /// <summary>Esquema del archivo de solicitud de activación (.req) que genera el VMS.</summary>
    public const int ActivationRequestSchema = 1;

    /// <summary>
    /// Cupos del período de prueba incorporado: todos los módulos disponibles
    /// con cupos chicos, para evaluar el producto completo antes de comprar.
    /// El control de acceso queda fuera porque el VMS aún no lo implementa.
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
        [LicenseFeatures.ModuleAccess] = false,
        [LicenseFeatures.AccessDoors] = 0,
        [LicenseFeatures.ModuleVideowall] = true,
        [LicenseFeatures.Videowalls] = 1,
        [LicenseFeatures.VideowallDecoders] = 2,
        [LicenseFeatures.ModuleSpeakers] = true,
        [LicenseFeatures.SpeakerChannels] = 4,
        [LicenseFeatures.ModuleAutomation] = true,
        [LicenseFeatures.AutomationRules] = 10,
    };
}
