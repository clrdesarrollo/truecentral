using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Campos personalizados de las personas: datos propios de cada instalación
/// que el padrón no trae de fábrica (el turno de colación de un casino, la
/// empresa contratista, el número de casillero…). El administrador los define
/// y se completan en la ficha de cada persona.
///
/// No van a los equipos: son información del padrón, para filtrar la lista,
/// para los reportes y para las integraciones (el kiosco del casino lee la
/// colación de acá). Por eso cambiarlos no escribe nada en los equipos: la
/// persona pasa por el sincronizador como en toda edición, pero lo que le
/// corresponde a cada equipo no cambió (mismo hash del plan) y se salta.
///
/// Los valores se guardan NORMALIZADOS según el tipo (número con punto,
/// "true"/"false", "yyyy-MM-dd", la opción tal como se definió): así un filtro
/// "Colacion = 2" encuentra igual a quien se cargó como "2" que como "2,0".
/// </summary>
public static class AccessPersonFieldsApi
{
    /// <summary>Tope de campos: un padrón con más de esto necesita otra herramienta, no más columnas.</summary>
    public const int MaxFields = 50;
    private const int MaxValueLength = AccessPersonFieldRules.MaxValueLength;
    private const int MaxOptions = 100;
    private const int MaxOptionLength = 128;

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    public static void MapAccessPersonFields(this WebApplication app)
    {
        app.MapGet("/api/access/person-fields", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PersonsView, out _) is { } failure) return failure;
            return Results.Ok(await ListAsync(db, ct));
        });

        app.MapPost("/api/access/person-fields", async (HttpContext ctx, AccessPersonFieldWriteDto request,
            VmsDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PersonsManage, out _) is { } failure) return failure;
            if (await ValidateAsync(db, request, null, ct) is { } invalid) return Error(invalid);
            if (await db.AccessPersonFields.CountAsync(ct) >= MaxFields)
                return Error($"Se admiten hasta {MaxFields} campos personalizados.");

            var field = new AccessPersonField
            {
                Name = request.Name.Trim(),
                Kind = request.Kind,
                Options = OptionsText(request),
                Required = request.Required,
                ShowInList = request.ShowInList,
                SortOrder = request.SortOrder,
                Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            };
            db.AccessPersonFields.Add(field);
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "access", "person-field-created",
                targetType: "access-person-field", targetId: field.Id.ToString(), targetName: field.Name,
                detail: $"Creó el campo personalizado de personas '{field.Name}' ({Describe(field)}).",
                data: new { field.Name, Kind = field.Kind.ToString(), field.Required, Options = field.OptionList });
            return Results.Ok(ToDto(field, 0));
        });

        app.MapPut("/api/access/person-fields/{id:int}", async (HttpContext ctx, int id,
            AccessPersonFieldWriteDto request, VmsDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PersonsManage, out _) is { } failure) return failure;
            var field = await db.AccessPersonFields.Include(f => f.Values).FirstOrDefaultAsync(f => f.Id == id, ct);
            if (field is null) return Results.NotFound();
            if (await ValidateAsync(db, request, id, ct) is { } invalid) return Error(invalid);

            var changes = new List<string>();
            string name = request.Name.Trim();
            if (field.Name != name) changes.Add($"nombre '{field.Name}' → '{name}'");
            if (field.Kind != request.Kind) changes.Add($"tipo {KindName(field.Kind)} → {KindName(request.Kind)}");
            string? options = OptionsText(request);
            if ((field.Options ?? "") != (options ?? "")) changes.Add("opciones cambiadas");
            if (field.Required != request.Required) changes.Add(request.Required ? "ahora obligatorio" : "ya no obligatorio");
            if (field.ShowInList != request.ShowInList)
                changes.Add(request.ShowInList ? "se muestra en la lista" : "ya no se muestra en la lista");

            // Cambiar el tipo o las opciones no puede dejar valores que el campo
            // ya no admite: se re-normalizan con el tipo nuevo y, si alguno no
            // sirve, no se cambia nada y se avisa cuántos son.
            var probe = new AccessPersonField { Name = name, Kind = request.Kind, Options = options };
            var renormalized = new List<(AccessPersonFieldValue Value, string Normalized)>();
            int rejected = 0;
            string? example = null;
            foreach (var value in field.Values)
            {
                if (NormalizeValue(probe, value.Value) is { } normalized) renormalized.Add((value, normalized));
                else { rejected++; example ??= value.Value; }
            }
            if (rejected > 0)
                return Error($"{rejected} persona(s) tienen en '{field.Name}' un valor que no sirve como " +
                             $"{KindName(request.Kind)} (por ejemplo «{example}»). Corríjalos antes de cambiar el tipo o las opciones.");

            field.Name = name;
            field.Kind = request.Kind;
            field.Options = options;
            field.Required = request.Required;
            field.ShowInList = request.ShowInList;
            field.SortOrder = request.SortOrder;
            field.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
            field.UpdatedAt = DateTime.UtcNow;
            foreach (var (value, normalized) in renormalized) value.Value = normalized;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "access", "person-field-updated",
                targetType: "access-person-field", targetId: field.Id.ToString(), targetName: field.Name,
                detail: $"Modificó el campo personalizado de personas '{field.Name}': " +
                        (changes.Count > 0 ? string.Join(", ", changes) + "." : "sin cambios de fondo."));
            return Results.Ok(ToDto(field, field.Values.Count));
        });

        app.MapDelete("/api/access/person-fields/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PersonsManage, out _) is { } failure) return failure;
            var field = await db.AccessPersonFields.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (field is null) return Results.NotFound();
            int values = await db.AccessPersonFieldValues.CountAsync(v => v.AccessPersonFieldId == id, ct);
            db.AccessPersonFields.Remove(field);   // los valores se van en cascada
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(ctx, "access", "person-field-deleted",
                targetType: "access-person-field", targetId: id.ToString(), targetName: field.Name,
                detail: $"Eliminó el campo personalizado de personas '{field.Name}' y su valor en {values} persona(s).",
                data: new { field.Name, Values = values });
            return Results.Ok();
        });
    }

    // ==================================================================
    // Definiciones
    // ==================================================================

    public static async Task<List<AccessPersonFieldDto>> ListAsync(VmsDbContext db, CancellationToken ct)
    {
        var counts = await db.AccessPersonFieldValues.GroupBy(v => v.AccessPersonFieldId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var fields = await db.AccessPersonFields.AsNoTracking()
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Name).ToListAsync(ct);
        return fields.Select(f => ToDto(f, counts.GetValueOrDefault(f.Id))).ToList();
    }

    private static AccessPersonFieldDto ToDto(AccessPersonField f, int valueCount) => new(
        f.Id, f.Name, f.Kind, f.OptionList, f.Required, f.ShowInList, f.SortOrder, f.Description, valueCount,
        f.CreatedAt, f.UpdatedAt);

    private static async Task<string?> ValidateAsync(VmsDbContext db, AccessPersonFieldWriteDto request, int? id,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 64)
            return "El nombre del campo es obligatorio (máximo 64 caracteres).";
        if (!Enum.IsDefined(request.Kind)) return "El tipo de campo no es válido.";
        string lower = request.Name.Trim().ToLower();
        if (await db.AccessPersonFields.AnyAsync(f => f.Name.ToLower() == lower && (id == null || f.Id != id), ct))
            return "Ya existe otro campo con ese nombre.";
        if (request.Description is { Length: > 256 }) return "La descripción admite hasta 256 caracteres.";
        if (request.Kind == AccessPersonFieldKind.Choice)
        {
            var options = CleanOptions(request.Options);
            if (options.Count == 0) return "Un campo de lista necesita al menos una opción.";
            if (options.Count > MaxOptions) return $"Se admiten hasta {MaxOptions} opciones.";
            if (options.Any(o => o.Length > MaxOptionLength)) return $"Cada opción admite hasta {MaxOptionLength} caracteres.";
            if (options.Select(o => o.ToLower()).Distinct().Count() != options.Count) return "Hay opciones repetidas.";
        }
        return null;
    }

    private static List<string> CleanOptions(IReadOnlyList<string>? options) =>
        (options ?? []).Select(o => (o ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim())
            .Where(o => o.Length > 0).ToList();

    private static string? OptionsText(AccessPersonFieldWriteDto request) =>
        request.Kind == AccessPersonFieldKind.Choice ? string.Join('\n', CleanOptions(request.Options)) : null;

    private static string KindName(AccessPersonFieldKind kind) => AccessPersonFieldRules.KindName(kind);

    private static string Describe(AccessPersonField f) =>
        KindName(f.Kind) +
        (f.Kind == AccessPersonFieldKind.Choice ? $": {string.Join(", ", f.OptionList)}" : "") +
        (f.Required ? ", obligatorio" : "");

    // ==================================================================
    // Valores
    // ==================================================================

    /// <summary>El valor normalizado según el tipo del campo, o null si no sirve (ver <see cref="AccessPersonFieldRules"/>).</summary>
    public static string? NormalizeValue(AccessPersonField field, string? raw) =>
        AccessPersonFieldRules.Normalize(field.Kind, field.OptionList, raw);

    /// <summary>
    /// Revisa los valores que manda el formulario de una persona y los deja
    /// normalizados (null = quitar el valor). Con <paramref name="input"/> null
    /// no hay nada que revisar: quien no administra campos no los toca, y por
    /// eso tampoco se le exigen los obligatorios.
    /// </summary>
    public static async Task<(Dictionary<int, string?>? Values, string? Error)> ValidateValuesAsync(VmsDbContext db,
        IReadOnlyDictionary<int, string?>? input, AccessPerson? existing, CancellationToken ct)
    {
        if (input is null) return (null, null);
        var fields = await db.AccessPersonFields.AsNoTracking().ToDictionaryAsync(f => f.Id, ct);
        var result = new Dictionary<int, string?>();
        foreach (var (fieldId, raw) in input)
        {
            if (!fields.TryGetValue(fieldId, out var field))
                return (null, "Uno de los campos personalizados indicados ya no existe.");
            if (string.IsNullOrWhiteSpace(raw)) { result[fieldId] = null; continue; }
            if (raw.Trim().Length > MaxValueLength)
                return (null, $"«{field.Name}» admite hasta {MaxValueLength} caracteres.");
            if (NormalizeValue(field, raw) is not { } normalized)
                return (null, field.Kind switch
                {
                    AccessPersonFieldKind.Number => $"«{field.Name}» tiene que ser un número.",
                    AccessPersonFieldKind.Choice => $"«{field.Name}» tiene que ser una de sus opciones: {string.Join(", ", field.OptionList)}.",
                    AccessPersonFieldKind.YesNo => $"«{field.Name}» tiene que ser sí o no.",
                    AccessPersonFieldKind.Date => $"«{field.Name}» tiene que ser una fecha (aaaa-mm-dd).",
                    _ => $"«{field.Name}» no es válido.",
                });
            result[fieldId] = normalized;
        }

        foreach (var field in fields.Values.Where(f => f.Required).OrderBy(f => f.SortOrder))
        {
            string? final = result.TryGetValue(field.Id, out var given)
                ? given
                : existing?.FieldValues.FirstOrDefault(v => v.AccessPersonFieldId == field.Id)?.Value;
            if (string.IsNullOrEmpty(final)) return (null, $"«{field.Name}» es obligatorio.");
        }
        return (result, null);
    }

    /// <summary>
    /// Deja en la persona los valores ya validados. Devuelve los cambios en
    /// palabras, para la bitácora ("Colacion '1' → '2'").
    /// </summary>
    public static async Task<List<string>> ApplyValuesAsync(VmsDbContext db, AccessPerson person,
        IReadOnlyDictionary<int, string?> values, CancellationToken ct)
    {
        var names = await db.AccessPersonFields.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Name, ct);
        var changes = new List<string>();
        foreach (var (fieldId, value) in values)
        {
            string name = names.GetValueOrDefault(fieldId, $"campo {fieldId}");
            var current = person.FieldValues.FirstOrDefault(v => v.AccessPersonFieldId == fieldId);
            if (value is null)
            {
                if (current is null) continue;
                person.FieldValues.Remove(current);
                db.AccessPersonFieldValues.Remove(current);
                changes.Add($"{name} '{current.Value}' → —");
            }
            else if (current is null)
            {
                person.FieldValues.Add(new AccessPersonFieldValue { AccessPersonFieldId = fieldId, Value = value });
                changes.Add($"{name} — → '{value}'");
            }
            else if (current.Value != value)
            {
                changes.Add($"{name} '{current.Value}' → '{value}'");
                current.Value = value;
            }
        }
        return changes;
    }
}
