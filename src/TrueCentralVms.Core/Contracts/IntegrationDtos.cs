using System.Security.Cryptography;
using System.Text;

namespace TrueCentralVms.Core.Contracts;

// ---------------------------------------------------------------------------
// API de integración: sistemas externos (el kiosco de un casino, un sistema de
// remuneraciones) leen el padrón con credenciales propias —no con usuarios del
// VMS— firmando cada petición. Se administran en Sistema → Integraciones.
// ---------------------------------------------------------------------------

/// <summary>Lo que una aplicación integrada puede hacer (se marca al crearla).</summary>
public static class IntegrationScopes
{
    /// <summary>Leer el padrón: legajo, nombre, departamento, vigencia y campos personalizados.</summary>
    public const string PersonsRead = "persons.read";

    /// <summary>Descargar la foto del rostro (dato biométrico: va aparte).</summary>
    public const string PersonsPhoto = "persons.photo";

    public static readonly IReadOnlyList<(string Key, string Label, string Description)> All =
    [
        (PersonsRead, "Leer el padrón",
            "Legajo, nombre, departamento, cargo, vigencia, si está activa y los campos personalizados (por ejemplo la colación)."),
        (PersonsPhoto, "Descargar fotos",
            "La foto del rostro de cada persona. Es un dato biométrico: otórguelo solo al sistema que la necesite."),
    ];

    public static bool IsValid(string? key) => key is not null && All.Any(s => s.Key == key);
}

/// <summary>
/// Firma de las peticiones a <c>/api/integration/v1</c>. Cabeceras:
/// <c>X-TC-AppKey</c>, <c>X-TC-Timestamp</c> (segundos Unix), <c>X-TC-Nonce</c>
/// (un valor nuevo por petición) y <c>X-TC-Signature</c> = Base64(HMAC-SHA256(secreto,
/// texto)), con texto = método, ruta con la consulta tal como viaja, timestamp,
/// nonce y SHA-256 en hexadecimal (minúsculas) del cuerpo, separados por "\n".
/// El servidor acepta ±5 minutos de diferencia de hora y cada nonce una sola vez.
/// </summary>
public static class IntegrationSignature
{
    public const string AppKeyHeader = "X-TC-AppKey";
    public const string TimestampHeader = "X-TC-Timestamp";
    public const string NonceHeader = "X-TC-Nonce";
    public const string SignatureHeader = "X-TC-Signature";

    /// <summary>Diferencia de hora aceptada entre el sistema externo y el servidor.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    public static string TextToSign(string method, string pathAndQuery, string timestamp, string nonce, ReadOnlySpan<byte> body) =>
        string.Join('\n', method.ToUpperInvariant(), pathAndQuery, timestamp, nonce,
            Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());

    public static string Sign(string secret, string textToSign) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(textToSign)));
}

// ---- Administración (panel: Sistema → Integraciones) ----

public sealed record IntegrationAppDto(
    int Id,
    string Name,
    string? Description,
    string AppKey,
    IReadOnlyList<string> Scopes,
    /// <summary>Direcciones o redes (CIDR) desde las que se acepta; vacío = cualquiera.</summary>
    IReadOnlyList<string> AllowedIps,
    DateTime? ExpiresAt,
    bool Enabled,
    /// <summary>Deshabilitada por falta de cupo de licencia (vuelve sola si alcanza).</summary>
    bool DisabledByLicense,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime SecretIssuedAt,
    DateTime? LastUsedAt,
    string? LastUsedIp);

public sealed record IntegrationAppWriteDto(
    string Name,
    string? Description,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string>? AllowedIps,
    DateTime? ExpiresAt,
    bool Enabled = true);

/// <summary>La aplicación con su secreto en claro: solo al crearla o al cambiar el secreto, y nunca más.</summary>
public sealed record IntegrationAppSecretDto(IntegrationAppDto App, string AppSecret);

/// <summary>Lo que necesita quien configura el sistema externo, además de la llave y el secreto.</summary>
public sealed record IntegrationServerInfoDto(
    /// <summary>Huella SHA-256 (hexadecimal) de la llave pública del certificado HTTPS, para fijarla en el sistema externo.</summary>
    string? CertificateSpki,
    int HttpsPort);

public sealed record IntegrationScopeDto(string Key, string Label, string Description);

public sealed record IntegrationAppsPageDto(
    IReadOnlyList<IntegrationAppDto> Apps,
    IntegrationServerInfoDto Server,
    IReadOnlyList<IntegrationScopeDto> Scopes,
    /// <summary>Cupo de la licencia (null = sin módulo) y cuántas hay habilitadas.</summary>
    int? Quota,
    int InUse);

// ---- API v1 (lo que leen los sistemas externos) ----

public sealed record IntegrationHealthDto(bool Ok, string Version, DateTime Time, string App, IReadOnlyList<string> Scopes);

/// <summary>
/// Foto de una persona: <see cref="Version"/> cambia cuando se reemplaza, así
/// que el sistema externo puede no volver a descargar la que ya tiene.
/// </summary>
public sealed record IntegrationPhotoDto(string Version, string ContentType, int Bytes, DateTime UpdatedAt);

public sealed record IntegrationPersonDto(
    /// <summary>Legajo: con este número la reconocen los terminales. No cambia nunca.</summary>
    string EmployeeNo,
    /// <summary>Identificador interno de TrueCentral (estable).</summary>
    int Id,
    string FirstName,
    string LastName,
    string FullName,
    /// <summary>Departamento: el último tramo de la ruta (la «empresa» en el casino).</summary>
    string? Department,
    /// <summary>Ruta completa del departamento tal como está en TrueCentral ("Padre / Hijo").</summary>
    string? DepartmentPath,
    string? Position,
    bool Enabled,
    DateTime ValidFrom,
    DateTime ValidTo,
    /// <summary>Campos personalizados por nombre (por ejemplo «Colacion» = "1"), con el valor normalizado.</summary>
    IReadOnlyDictionary<string, string> Attributes,
    IntegrationPhotoDto? Photo,
    DateTime UpdatedAt);

public sealed record IntegrationPersonPageDto(int Total, int Page, int PageSize, IReadOnlyList<IntegrationPersonDto> Items);

public sealed record IntegrationDepartmentDto(string Name, string Path, int Persons);
