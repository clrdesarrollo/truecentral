using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Ubicación elegida al dar de alta o editar un equipo. La de un equipo con
/// recursos (los canales de un grabador, las áreas y zonas de un panel, las
/// puertas de una controladora) es la que heredan sus recursos nuevos, y al
/// cambiarla se llevan consigo los que estaban con él; los que se ubicaron
/// aparte desde Recursos se quedan donde están.
/// </summary>
public static class EquipmentLocation
{
    /// <summary>
    /// La ubicación que pide el formulario: null = conservar la actual (en el
    /// alta, por ubicar), 0 = por ubicar, n = esa ubicación, que tiene que existir.
    /// </summary>
    public static async Task<(int? LocationId, string? Error)> ResolveAsync(
        VmsDbContext db, int? requested, int? current, CancellationToken ct)
    {
        if (requested is null) return (current, null);
        if (requested <= 0) return (null, null);
        return await db.Locations.AnyAsync(l => l.Id == requested, ct)
            ? (requested, null)
            : (current, "La ubicación elegida ya no existe: recargue la página.");
    }

    /// <summary>
    /// Lleva a <paramref name="current"/> los recursos que seguían al equipo:
    /// los que estaban por ubicar o en su ubicación anterior. Devuelve los que
    /// se movieron (no guarda: el llamador hace SaveChanges).
    /// </summary>
    public static List<T> Follow<T>(IEnumerable<T> resources, int? previous, int? current) where T : ILocatable
    {
        var moved = new List<T>();
        if (previous == current) return moved;
        foreach (var resource in resources)
        {
            if (resource.LocationId is not null && resource.LocationId != previous) continue;
            if (resource.LocationId == current) continue;
            resource.LocationId = current;
            moved.Add(resource);
        }
        return moved;
    }

    /// <summary>Texto para la bitácora: "Casa matriz › Bodega" o "por ubicar".</summary>
    public static string Describe(int? locationId) =>
        locationId is null ? "por ubicar" : LocationPaths.Of(locationId) ?? $"ubicación #{locationId}";

    /// <summary>El cambio para la lista de la bitácora: "ubicación (por ubicar) → 'Casa matriz › Bodega'".</summary>
    public static string Change(int? previous, int? current)
    {
        static string Quote(int? id) => id is null ? "(por ubicar)" : $"'{Describe(id)}'";
        return $"ubicación {Quote(previous)} → {Quote(current)}";
    }

    /// <summary>
    /// Cambió dónde está algo: se rehacen los alcances por ubicación (lo que
    /// ve y opera cada operador restringido) y las rutas que muestran los DTO.
    /// </summary>
    public static void Changed(HttpContext ctx) =>
        ctx.RequestServices.GetRequiredService<UserScopeService>().Invalidate();

    /// <summary>
    /// Algo cambió de ubicación: además de rehacer los alcances, los puestos
    /// recargan lo que depende de dónde está cada cosa (el árbol, Recursos y
    /// los módulos de un operador restringido, que pudo ganar o perder recursos).
    /// </summary>
    public static Task MovedAsync(HttpContext ctx, CancellationToken ct)
    {
        Changed(ctx);
        return ctx.RequestServices.GetRequiredService<IHubContext<VmsHub>>()
            .Clients.All.SendAsync(VmsHubContract.ConfigChanged, "locations", ct);
    }

    /// <summary>
    /// Deja en la bitácora los recursos que se movieron junto con su equipo,
    /// con sus claves ("Camera:12") en los datos: así el cambio aparece también
    /// en el historial de cada recurso (página Recursos).
    /// </summary>
    public static Task LogFollowersAsync(HttpContext ctx, AuditService audit,
        string equipmentType, int equipmentId, string equipmentName, int? locationId,
        IReadOnlyCollection<(ResourceKind Kind, int Id, string Name)> moved)
    {
        if (moved.Count == 0) return Task.CompletedTask;
        string names = string.Join(", ", moved.Take(10).Select(m => $"\"{m.Name}\"")) +
                       (moved.Count > 10 ? $" y {moved.Count - 10} más" : "");
        bool one = moved.Count == 1;
        string where = locationId is null
            ? (one ? "quedó por ubicar" : "quedaron por ubicar")
            : $"{(one ? "pasó" : "pasaron")} a {Describe(locationId)}";
        return audit.LogAsync(ctx, "locations", "equipment-located",
            targetType: equipmentType, targetId: equipmentId.ToString(), targetName: equipmentName,
            detail: $"{Capitalize(ResourceCatalog.Summarize(moved.Select(m => m.Kind)))} {where} junto con el equipo " +
                    $"'{equipmentName}': {names}.",
            data: new
            {
                locationId,
                resources = moved.Select(m => ResourceCatalog.Key(m.Kind, m.Id)).ToList(),
            });
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
