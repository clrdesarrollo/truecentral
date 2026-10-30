using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Integration;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// API de integración v1: lo que leen los sistemas externos con su aplicación
/// integrada (ver <see cref="IntegrationAuth"/>). Solo lectura del padrón: el
/// kiosco del casino arma con esto su lista de personas, su «empresa» (el
/// departamento) y la colación (un campo personalizado). El contrato es propio
/// y estable: lo nuevo se agrega como campos opcionales o en una v2.
/// </summary>
public static class IntegrationApi
{
    private const int DefaultPageSize = 200;
    private const int MaxPageSize = 500;

    /// <summary>Separador de la ruta de departamentos que deja el migrador ("Padre / Hijo").</summary>
    private const string PathSeparator = " / ";

    public static void MapIntegrationApi(this WebApplication app, string serverVersion)
    {
        var v1 = app.MapGroup("/api/integration/v1");

        // Para probar la conexión: la versión, la hora del servidor y lo que la aplicación tiene permitido.
        v1.MapGet("/health", async (HttpContext ctx, VmsDbContext db, IntegrationAuth auth, CancellationToken ct) =>
        {
            var (integration, failure) = await auth.AuthenticateAsync(ctx, db, null, ct);
            if (failure is not null) return failure;
            return Results.Ok(new IntegrationHealthDto(true, serverVersion, DateTime.UtcNow, integration!.Name, integration.ScopeList));
        });

        // El padrón, de a páginas en orden fijo. Para deducir las bajas hay que recorrerlo entero: una persona
        // eliminada no aparece en updatedSince.
        v1.MapGet("/persons", async (HttpContext ctx, VmsDbContext db, IntegrationAuth auth, int? page, int? pageSize,
            DateTime? updatedSince, CancellationToken ct) =>
        {
            var (_, failure) = await auth.AuthenticateAsync(ctx, db, IntegrationScopes.PersonsRead, ct);
            if (failure is not null) return failure;
            int size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
            int number = Math.Max(page ?? 1, 1);

            var query = db.AccessPersons.AsNoTracking();
            if (updatedSince is { } since)
            {
                var sinceUtc = since.ToUniversalTime();
                query = query.Where(p => p.UpdatedAt >= sinceUtc);
            }
            int total = await query.CountAsync(ct);
            var rows = await query.OrderBy(p => p.Id).Skip((number - 1) * size).Take(size)
                .Select(p => new
                {
                    p.Id, p.EmployeeNo, p.FirstName, p.LastName, p.Department, p.Position, p.Enabled,
                    p.ValidFrom, p.ValidTo, p.UpdatedAt,
                    Face = p.Face == null ? null : new { p.Face.Id, p.Face.ContentType, p.Face.Bytes, p.Face.CreatedAt },
                    Fields = p.FieldValues.Select(v => new { v.AccessPersonFieldId, v.Value }).ToList(),
                })
                .ToListAsync(ct);
            var fieldNames = await db.AccessPersonFields.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Name, ct);

            var items = rows.Select(p => new IntegrationPersonDto(
                p.EmployeeNo, p.Id, p.FirstName, p.LastName, $"{p.FirstName} {p.LastName}".Trim(),
                LeafOf(p.Department), p.Department, p.Position, p.Enabled, p.ValidFrom, p.ValidTo,
                p.Fields.Where(f => fieldNames.ContainsKey(f.AccessPersonFieldId))
                    .ToDictionary(f => fieldNames[f.AccessPersonFieldId], f => f.Value),
                p.Face is null ? null : new IntegrationPhotoDto($"{p.Face.Id}-{p.Face.CreatedAt.Ticks:x}-{p.Face.Bytes}",
                    p.Face.ContentType, p.Face.Bytes, p.Face.CreatedAt),
                p.UpdatedAt)).ToList();
            return Results.Ok(new IntegrationPersonPageDto(total, number, size, items));
        });

        // La foto del rostro (dato biométrico, permiso aparte), por legajo.
        v1.MapGet("/persons/{employeeNo}/photo", async (HttpContext ctx, string employeeNo, VmsDbContext db,
            IntegrationAuth auth, CredentialProtector protector, AuditService audit, CancellationToken ct) =>
        {
            var (integration, failure) = await auth.AuthenticateAsync(ctx, db, IntegrationScopes.PersonsPhoto, ct);
            if (failure is not null) return failure;
            var face = await db.AccessFaces.AsNoTracking()
                .Where(f => f.AccessPerson!.EmployeeNo == employeeNo)
                .Select(f => new { f.ImageCiphertext, f.ContentType })
                .FirstOrDefaultAsync(ct);
            if (face is null) return Results.NotFound(new { error = $"La persona {employeeNo} no existe o no tiene foto." });

            // Una fila por hora y aplicación: el kiosco baja decenas de fotos seguidas.
            if (audit.ShouldLog($"integration-photos:{integration!.Id}", TimeSpan.FromHours(1)))
                await audit.LogAsAsync(ctx, null, IntegrationAuth.ActorOf(integration), null, "integration", "photos-read",
                    targetType: "integration-app", targetId: integration.Id.ToString(), targetName: integration.Name,
                    detail: $"La aplicación integrada «{integration.Name}» está descargando fotos de personas " +
                            $"(la primera de esta hora: legajo {employeeNo}).",
                    clientIp: ctx.Connection.RemoteIpAddress?.ToString());
            return Results.File(protector.UnprotectBytes(face.ImageCiphertext), face.ContentType);
        });

        // Departamentos con cuántas personas tiene cada uno (la «empresa» del casino es el último tramo).
        v1.MapGet("/departments", async (HttpContext ctx, VmsDbContext db, IntegrationAuth auth, CancellationToken ct) =>
        {
            var (_, failure) = await auth.AuthenticateAsync(ctx, db, IntegrationScopes.PersonsRead, ct);
            if (failure is not null) return failure;
            var groups = await db.AccessPersons.AsNoTracking().Where(p => p.Department != null)
                .GroupBy(p => p.Department!).Select(g => new { Path = g.Key, Count = g.Count() })
                .OrderBy(g => g.Path).ToListAsync(ct);
            return Results.Ok(groups.Select(g => new IntegrationDepartmentDto(LeafOf(g.Path)!, g.Path, g.Count)).ToList());
        });
    }

    /// <summary>"Planta / Casino" → "Casino"; sin ruta, el nombre tal cual.</summary>
    public static string? LeafOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        int cut = path.LastIndexOf(PathSeparator, StringComparison.Ordinal);
        return (cut < 0 ? path : path[(cut + PathSeparator.Length)..]).Trim();
    }
}
