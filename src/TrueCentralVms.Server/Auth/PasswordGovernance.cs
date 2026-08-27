using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Auth;

/// <summary>
/// Gobierno de contraseñas: política de fortaleza (Core.PasswordPolicy),
/// historial (ninguna clave anterior se puede reutilizar) y caducidad
/// configurable por parámetro Security:PasswordMaxAgeDays (0 = deshabilitada).
/// </summary>
public sealed class PasswordGovernance(IConfiguration config)
{
    private int MaxAgeDays => config.GetValue("Security:PasswordMaxAgeDays", 0);

    public bool ExpirationEnabled => MaxAgeDays > 0;

    /// <summary>La clave del usuario superó la vigencia configurada.</summary>
    public bool IsExpired(User user) =>
        ExpirationEnabled && user.PasswordChangedAt.AddDays(MaxAgeDays) < DateTime.UtcNow;

    /// <summary>
    /// Valida una clave nueva: política de fortaleza y, para usuarios
    /// existentes, el historial completo (PBKDF2 contra cada fila). Devuelve el
    /// mensaje de error listo para mostrar, o null si la clave es aceptable.
    /// </summary>
    public async Task<string?> ValidateNewPasswordAsync(VmsDbContext db, int? userId, string? password, CancellationToken ct = default)
    {
        if (PasswordPolicy.Describe(password) is { } policyError)
            return policyError;

        if (userId is int id)
        {
            var history = await db.PasswordHistories
                .Where(h => h.UserId == id)
                .Select(h => new { h.PasswordHash, h.PasswordSalt })
                .ToListAsync(ct);
            if (history.Any(h => PasswordHasher.Verify(password!, h.PasswordHash, h.PasswordSalt)))
                return "Esa contraseña ya fue utilizada anteriormente: elija una distinta.";
        }
        return null;
    }

    /// <summary>
    /// Establece la clave en el usuario y la suma al historial. El llamador es
    /// responsable de validar antes (<see cref="ValidateNewPasswordAsync"/>) y
    /// de guardar los cambios.
    /// </summary>
    public void SetPassword(User user, string password)
    {
        var (hash, salt) = PasswordHasher.Hash(password);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.PasswordChangedAt = DateTime.UtcNow;
        user.PasswordHistories.Add(new PasswordHistory { PasswordHash = hash, PasswordSalt = salt });
    }
}
