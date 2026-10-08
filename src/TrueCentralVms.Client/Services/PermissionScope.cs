using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.Services;

/// <summary>
/// Qué puede HACER esta sesión según sus roles (la unión de sus permisos, ver
/// <see cref="Core.Domain.Permissions"/>): los módulos que no le corresponden
/// se ocultan del riel y del inicio. Mientras no se haya leído, o si el servidor
/// es anterior a los roles, no se oculta nada (el servidor valida igual cada
/// solicitud). Hay una sola sesión por proceso: la instancia es compartida.
/// </summary>
public sealed class PermissionScope
{
    private sealed record Snapshot(bool Admin, HashSet<string> Keys, IReadOnlyList<string> Roles, string Signature);

    // Antes que Current: los estáticos se inicializan en orden de declaración.
    private static readonly Snapshot Everything = new(true, [], [], "all");

    public static PermissionScope Current { get; } = new();

    private volatile Snapshot _snapshot = Everything;

    /// <summary>Cambiaron los permisos. Se avisa en el hilo que llamó a <see cref="Update"/> (el de la interfaz).</summary>
    public event Action? Changed;

    public bool IsAdmin => _snapshot.Admin;

    /// <summary>Nombres de sus roles (vacío si el servidor no los informa).</summary>
    public IReadOnlyList<string> Roles => _snapshot.Roles;

    public bool Has(string permission) => _snapshot.Admin || _snapshot.Keys.Contains(permission);

    public bool HasAny(params string[] permissions) => permissions.Any(Has);

    /// <summary>Aplica lo que entregó el servidor; avisa solo si algo cambió.</summary>
    public void Update(MyPermissionsDto dto)
    {
        var next = new Snapshot(dto.IsAdmin, [.. dto.Permissions], dto.Roles,
            $"{dto.IsAdmin}|{string.Join(',', dto.Permissions.Order())}|{string.Join(',', dto.Roles)}");
        if (next.Signature == _snapshot.Signature) return;
        _snapshot = next;
        Changed?.Invoke();
    }
}
