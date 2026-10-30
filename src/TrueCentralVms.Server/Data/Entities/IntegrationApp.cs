namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Aplicación integrada (API de integración): un sistema externo —el kiosco del
/// casino, remuneraciones— que lee el padrón con su llave y su secreto, sin ser
/// un usuario del VMS. Cada habilitada consume un cupo <c>integration_apps</c>.
/// </summary>
public class IntegrationApp
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Llave pública (va en cada petición); única.</summary>
    public string AppKey { get; set; } = "";
    /// <summary>
    /// Secreto cifrado con <c>CredentialProtector</c>: hace falta en claro para
    /// verificar la firma HMAC, por eso no se guarda un hash. Se muestra una sola
    /// vez, al crearla o al cambiarlo.
    /// </summary>
    public byte[] SecretCiphertext { get; set; } = [];
    public DateTime SecretIssuedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Alcances (<c>IntegrationScopes</c>) separados por coma.</summary>
    public string Scopes { get; set; } = "";
    /// <summary>Direcciones o redes CIDR permitidas, una por línea; null = desde cualquiera.</summary>
    public string? AllowedIps { get; set; }
    /// <summary>Desde esta fecha (UTC) se rechaza; null = no vence.</summary>
    public DateTime? ExpiresAt { get; set; }

    public bool Enabled { get; set; } = true;
    /// <summary>Apagada por el cupo (o el módulo) de la licencia; ver <see cref="AlarmPanel.DisabledByLicense"/>.</summary>
    public bool DisabledByLicense { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public string? LastUsedIp { get; set; }

    public IReadOnlyList<string> ScopeList =>
        Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public IReadOnlyList<string> AllowedIpList =>
        (AllowedIps ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
