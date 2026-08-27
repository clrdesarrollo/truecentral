namespace TrueCentralVms.Core.Auth;

/// <summary>
/// Política de fortaleza de contraseñas del producto. Vive en Core para que el
/// servidor la aplique como fuente de verdad y el cliente WPF pueda validar
/// antes de enviar (el panel web replica las mismas reglas en JS).
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;

    /// <summary>
    /// Valida la contraseña contra la política: mínimo 8 caracteres, al menos
    /// una mayúscula, una minúscula, un número y un carácter especial.
    /// Devuelve la lista de requisitos incumplidos (vacía si cumple).
    /// </summary>
    public static IReadOnlyList<string> Validate(string? password)
    {
        var errors = new List<string>();
        password ??= "";

        if (password.Length < MinLength)
            errors.Add($"al menos {MinLength} caracteres");
        if (!password.Any(char.IsUpper))
            errors.Add("una letra mayúscula");
        if (!password.Any(char.IsLower))
            errors.Add("una letra minúscula");
        if (!password.Any(char.IsDigit))
            errors.Add("un número");
        if (!password.Any(c => !char.IsLetterOrDigit(c)))
            errors.Add("un carácter especial (por ejemplo . , ! $ % # @)");

        return errors;
    }

    /// <summary>Mensaje de error listo para mostrar, o null si la contraseña cumple.</summary>
    public static string? Describe(string? password)
    {
        var errors = Validate(password);
        return errors.Count == 0 ? null : "La contraseña debe tener " + string.Join(", ", errors) + ".";
    }
}
