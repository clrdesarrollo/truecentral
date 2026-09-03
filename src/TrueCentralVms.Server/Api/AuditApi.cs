using System.Text;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Services;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Consulta de la bitácora de auditoría (SOLO administradores, solo panel web)
/// y recepción de los eventos que reporta el cliente de escritorio (capturas y
/// grabaciones locales). No existe modificación ni borrado: la bitácora es
/// solo-agregar y la única salida es la purga por retención configurada.
/// </summary>
public static class AuditApi
{
    private const int MaxPageSize = 200;
    private const int MaxCsvRows = 100_000;

    /// <summary>Aplica los filtros comunes de la consulta y la exportación.</summary>
    private static IQueryable<AuditEvent> Filter(VmsDbContext db, DateTime? from, DateTime? to,
        string? category, string? action, string? username, string? text, bool? success)
    {
        var query = db.AuditEvents.AsNoTracking().AsQueryable();
        // Las fechas llegan en hora local del servidor (datetime-local del
        // panel); la bitácora guarda UTC.
        if (from is { } f)
            query = query.Where(e => e.Timestamp >= DateTime.SpecifyKind(f, DateTimeKind.Local).ToUniversalTime());
        if (to is { } t)
            query = query.Where(e => e.Timestamp <= DateTime.SpecifyKind(t, DateTimeKind.Local).ToUniversalTime());
        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(e => e.Category == category);
        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(e => e.Action == action);
        if (!string.IsNullOrWhiteSpace(username))
            query = query.Where(e => e.Username == username);
        if (success is { } ok)
            query = query.Where(e => e.Success == ok);
        if (!string.IsNullOrWhiteSpace(text))
        {
            string needle = $"%{text.Trim()}%";
            query = query.Where(e =>
                EF.Functions.ILike(e.Detail ?? "", needle) ||
                EF.Functions.ILike(e.TargetName ?? "", needle) ||
                EF.Functions.ILike(e.TargetId ?? "", needle) ||
                EF.Functions.ILike(e.ClientIp, needle) ||
                EF.Functions.ILike(e.Username, needle));
        }
        return query;
    }

    private static AuditEventDto ToDto(AuditEvent e) => new(
        e.Id, e.Timestamp, e.Username, e.Role, e.Origin, e.ClientIp, e.Category, e.Action,
        e.TargetType, e.TargetId, e.TargetName, e.Detail, e.Success, e.DataJson);

    public static void MapAuditApi(this WebApplication app)
    {
        // ------------------------------------------------------------------
        // Consulta paginada (más recientes primero)
        // ------------------------------------------------------------------
        app.MapGet("/api/audit", async (HttpContext ctx, VmsDbContext db,
            DateTime? from, DateTime? to, string? category, string? action, string? username,
            string? text, bool? success, int? page, int? pageSize, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;

            var query = Filter(db, from, to, category, action, username, text, success);
            long total = await query.LongCountAsync(ct);

            int size = Math.Clamp(pageSize ?? 50, 1, MaxPageSize);
            int current = Math.Max(1, page ?? 1);
            var items = await query
                .OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id)
                .Skip((current - 1) * size)
                .Take(size)
                .ToListAsync(ct);

            return Results.Ok(new AuditPageDto(total, current, size, items.Select(ToDto).ToList()));
        });

        // ------------------------------------------------------------------
        // Catálogo de filtros: categorías con sus acciones (etiquetas en
        // español) y los usuarios que aparecen en la bitácora (incluye
        // usuarios ya eliminados: sus eventos permanecen).
        // ------------------------------------------------------------------
        app.MapGet("/api/audit/catalog", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var usernames = await db.AuditEvents.AsNoTracking()
                .Where(e => e.Username != "")
                .Select(e => e.Username)
                .Distinct()
                .OrderBy(u => u)
                .Take(500)
                .ToListAsync(ct);
            return Results.Ok(new
            {
                categories = AuditCatalog.Categories.Select(c => new
                {
                    key = c.Key,
                    label = c.Label,
                    actions = c.Actions.Select(a => new { key = a.Key, label = a.Label }),
                }),
                usernames,
            });
        });

        // ------------------------------------------------------------------
        // Exportación CSV (evidencia para auditorías). Separador ";" y UTF-8
        // con BOM: se abre directo en Excel con configuración regional es-CL.
        // ------------------------------------------------------------------
        app.MapGet("/api/audit/export", async (HttpContext ctx, VmsDbContext db, AuditService audit,
            DateTime? from, DateTime? to, string? category, string? action, string? username,
            string? text, bool? success, CancellationToken ct) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;

            var labels = AuditCatalog.Categories.ToDictionary(c => c.Key, c => c);
            var rows = await Filter(db, from, to, category, action, username, text, success)
                .OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id)
                .Take(MaxCsvRows)
                .ToListAsync(ct);

            var csv = new StringBuilder();
            csv.AppendLine("Fecha (local);Usuario;Rol;Origen;IP;Categoría;Acción;Objeto;Id objeto;Nombre objeto;Detalle;Resultado;Datos");
            foreach (var e in rows)
            {
                labels.TryGetValue(e.Category, out var cat);
                string actionLabel = cat?.Actions.FirstOrDefault(a => a.Key == e.Action)?.Label ?? e.Action;
                csv.AppendJoin(';',
                    Csv(e.Timestamp.ToLocalTime().ToString("dd-MM-yyyy HH:mm:ss")),
                    Csv(e.Username), Csv(e.Role), Csv(OriginLabel(e.Origin)), Csv(e.ClientIp),
                    Csv(cat?.Label ?? e.Category), Csv(actionLabel),
                    Csv(e.TargetType), Csv(e.TargetId), Csv(e.TargetName),
                    Csv(e.Detail), Csv(e.Success ? "Éxito" : "Fallo"), Csv(e.DataJson));
                csv.AppendLine();
            }

            // La descarga de evidencia también es una acción auditable.
            await audit.LogAsync(ctx, "system", "audit-exported",
                detail: $"Exportó {rows.Count} eventos de la bitácora a CSV.",
                data: new { rows = rows.Count, from, to, category, action, username, text, success });

            byte[] bom = Encoding.UTF8.GetPreamble();
            byte[] body = Encoding.UTF8.GetBytes(csv.ToString());
            return Results.File(bom.Concat(body).ToArray(), "text/csv",
                $"auditoria_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        });

        // ------------------------------------------------------------------
        // Eventos reportados por el cliente de escritorio (capturas y
        // grabaciones locales, con la ruta donde quedaron). Solo se aceptan
        // las acciones del catálogo de cliente, siempre a nombre de la sesión
        // que llama: un cliente no puede fabricar eventos de otro usuario.
        // ------------------------------------------------------------------
        app.MapPost("/api/audit/client-event", async (HttpContext ctx, ClientAuditEventDto request,
            AuditService audit) =>
        {
            if (ApiSecurity.RequireUser(ctx, out _) is { } failure) return failure;

            if (!AuditCatalog.ClientActions.TryGetValue(request.Action ?? "", out var mapped))
                return Results.Json(new { error = $"Acción de auditoría desconocida: '{request.Action}'." },
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            string target = request.DeviceName is { Length: > 0 }
                ? request.ChannelName is { Length: > 0 }
                    ? $"{request.DeviceName} · {request.ChannelName}"
                    : request.DeviceName
                : request.ChannelName ?? "";

            string detail = mapped.Detail;
            if (target.Length > 0) detail += $" de {target}";
            if (request.FilePath is { Length: > 0 }) detail += $" en {request.FilePath}";
            if (request.Detail is { Length: > 0 }) detail += $" ({request.Detail})";

            await audit.LogAsync(ctx, mapped.Category, mapped.Action,
                targetType: request.DeviceId is not null ? "channel" : null,
                targetId: request.DeviceId is not null ? $"{request.DeviceId}/{request.ChannelNumber}" : null,
                targetName: target.Length > 0 ? target : null,
                detail: detail,
                data: new { request.FilePath, machine = ClientMachine(ctx) });
            return Results.Ok();
        });
    }

    private static string OriginLabel(string origin) => origin switch
    {
        "web" => "Panel web",
        "client" => "Cliente escritorio",
        "server" => "Servidor",
        _ => origin,
    };

    /// <summary>Nombre de máquina que el cliente WPF declara en su cabecera.</summary>
    private static string? ClientMachine(HttpContext ctx) =>
        ctx.Request.Headers.TryGetValue("X-TCVMS-Client", out var value) ? value.ToString() : null;

    /// <summary>Campo CSV: comillas dobladas y sin saltos de línea.</summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        string clean = value.Replace("\r", " ").Replace("\n", " ");
        return $"\"{clean.Replace("\"", "\"\"")}\"";
    }
}
