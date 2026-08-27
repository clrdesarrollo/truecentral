namespace TrueCentralVms.Core.Domain;

/// <summary>Roles del sistema. Se guardan como texto en la base de datos.</summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Operator = "Operator";

    public static readonly string[] All = [Admin, Operator];

    public static bool IsValid(string? role) => role is Admin or Operator;
}
