using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Licensing;
using TrueCentralVms.Server.Services.Patrol;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// API del control de rondas para el panel: bases, bastones, puntos, guardias,
/// llaveros, bitácora y la lectura a pedido (orden a la base o Web Serial).
/// Todo se filtra por el alcance de ubicación del usuario.
/// </summary>
public static partial class PatrolApi
{
    [GeneratedRegex("^[0-9A-F]{10}$")]
    private static partial Regex TagPattern();

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    private static string? NormalizeTag(string? tag)
    {
        string t = (tag ?? "").Trim().ToUpperInvariant().Replace(" ", "");
        return TagPattern().IsMatch(t) ? t : null;
    }

    private static async Task<string?> TagInUseAsync(VmsDbContext db, string tag, int? checkpointId, int? tagRowId, CancellationToken ct)
    {
        if (await db.PatrolCheckpoints.AnyAsync(c => c.TagId == tag && c.Id != checkpointId, ct))
            return "Ese tag ya es un punto de control.";
        if (await db.PatrolTags.AnyAsync(t => t.TagId == tag && t.Id != tagRowId, ct))
            return "Ese tag ya es un llavero de guardia.";
        return null;
    }

    public static void MapPatrolApi(this WebApplication app)
    {
        // ------------------------------------------------------------------
        // Bases de descarga
        app.MapGet("/api/patrol/agents", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolView, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var agents = await db.PatrolAgents.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);
            return Results.Ok(agents.Where(a => scope.CanView(a.LocationId)).Select(ToDto));
        });

        app.MapPost("/api/patrol/agents", async (HttpContext ctx, PatrolAgentWriteDto request, VmsDbContext db,
            AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out _) is { } failure) return failure;
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
                return Error("El nombre es obligatorio (máximo 128 caracteres).");
            var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, null, ct);
            if (locationError is not null) return Error(locationError, StatusCodes.Status404NotFound);
            var agent = new PatrolAgent { Name = request.Name.Trim(), LocationId = locationId };
            db.PatrolAgents.Add(agent);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "patrol", "agent-created", "patrol-agent", agent.Id.ToString(), agent.Name,
                $"Agregó la base de descarga '{agent.Name}'.");
            await patrol.NotifyAsync("agents", ct);
            return Results.Ok(ToDto(agent));
        });

        app.MapPut("/api/patrol/agents/{id:int}", async (HttpContext ctx, int id, PatrolAgentWriteDto request, VmsDbContext db,
            AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out _) is { } failure) return failure;
            var agent = await db.PatrolAgents.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (agent is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
                return Error("El nombre es obligatorio (máximo 128 caracteres).");
            var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, agent.LocationId, ct);
            if (locationError is not null) return Error(locationError, StatusCodes.Status404NotFound);
            string before = agent.Name;
            agent.Name = request.Name.Trim();
            agent.LocationId = locationId;
            agent.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "patrol", "agent-updated", "patrol-agent", agent.Id.ToString(), agent.Name,
                before == agent.Name ? $"Modificó la base '{agent.Name}'." : $"Renombró la base '{before}' a '{agent.Name}'.");
            await patrol.NotifyAsync("agents", ct);
            return Results.Ok(ToDto(agent));
        });

        app.MapDelete("/api/patrol/agents/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out _) is { } failure) return failure;
            var agent = await db.PatrolAgents.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (agent is null) return Results.NotFound();
            db.PatrolAgents.Remove(agent);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "patrol", "agent-deleted", "patrol-agent", id.ToString(), agent.Name,
                $"Eliminó la base '{agent.Name}': su token deja de valer.");
            await patrol.NotifyAsync("agents", ct);
            return Results.Ok();
        });

        // Código de enrolamiento (8 caracteres, 24 h). Generar uno nuevo invalida el token anterior.
        app.MapPost("/api/patrol/agents/{id:int}/enrollment", async (HttpContext ctx, int id, VmsDbContext db,
            AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out _) is { } failure) return failure;
            var agent = await db.PatrolAgents.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (agent is null) return Results.NotFound();
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            string code = new(RandomNumberGenerator.GetItems<char>(alphabet, 8));
            agent.EnrollmentCodeHash = PatrolAgentApi.Hash(code);
            agent.EnrollmentExpiresAt = DateTime.UtcNow.AddHours(24);
            agent.TokenHash = null;
            agent.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "patrol", "agent-enrollment-code", "patrol-agent", agent.Id.ToString(), agent.Name,
                $"Generó un código de enrolamiento para la base '{agent.Name}' (vence en 24 h; la conexión anterior queda revocada).");
            await patrol.NotifyAsync("agents", ct);
            return Results.Ok(new PatrolAgentEnrollmentDto($"{code[..4]}-{code[4..]}", agent.EnrollmentExpiresAt.Value));
        });

        // ------------------------------------------------------------------
        // Bastones
        app.MapGet("/api/patrol/readers", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolView, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var readers = await db.PatrolReaders.AsNoTracking().Include(r => r.Agent).OrderBy(r => r.Name).ToListAsync(ct);
            var pending = await db.PatrolAgentCommands.Where(c => c.CompletedAt == null).Select(c => c.ReaderId).ToListAsync(ct);
            return Results.Ok(readers.Where(r => scope.CanView(r.LocationId)).Select(r => ToDto(r, pending.Contains(r.Id))));
        });

        app.MapPut("/api/patrol/readers/{id:int}", async (HttpContext ctx, int id, PatrolReaderWriteDto request, VmsDbContext db,
            LicenseService license, AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out _) is { } failure) return failure;
            var reader = await db.PatrolReaders.Include(r => r.Agent).FirstOrDefaultAsync(r => r.Id == id, ct);
            if (reader is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
                return Error("El nombre es obligatorio (máximo 128 caracteres).");
            if (!reader.Enabled && request.Enabled && reader.Status == PatrolReaderStatus.Approved
                && license.Deny(LicenseFeatures.ModulePatrol, LicenseFeatures.PatrolReaders,
                    await db.PatrolReaders.CountAsync(r => r.Enabled && r.Status == PatrolReaderStatus.Approved && r.Id != id, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "patrol-reader", reader.Name);
            var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, reader.LocationId, ct);
            if (locationError is not null) return Error(locationError, StatusCodes.Status404NotFound);
            var changes = new List<string>();
            if (reader.Name != request.Name.Trim()) changes.Add($"nombre '{reader.Name}' → '{request.Name.Trim()}'");
            if (reader.Enabled != request.Enabled) changes.Add(request.Enabled ? "habilitado" : "deshabilitado");
            if (reader.LocationId != locationId) changes.Add("ubicación");
            reader.Name = request.Name.Trim();
            reader.Enabled = request.Enabled;
            reader.LocationId = locationId;
            reader.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "patrol", "reader-updated", "patrol-reader", reader.Id.ToString(), reader.Name,
                $"Modificó el bastón {reader.Serial:D6}: {(changes.Count == 0 ? "sin cambios" : string.Join(", ", changes))}.");
            await patrol.NotifyAsync("readers", ct);
            return Results.Ok(ToDto(reader, false));
        });

        app.MapPost("/api/patrol/readers/{id:int}/approve", async (HttpContext ctx, int id, VmsDbContext db,
            LicenseService license, AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out _) is { } failure) return failure;
            var reader = await db.PatrolReaders.Include(r => r.Agent).FirstOrDefaultAsync(r => r.Id == id, ct);
            if (reader is null) return Results.NotFound();
            if (license.Deny(LicenseFeatures.ModulePatrol, LicenseFeatures.PatrolReaders,
                    await db.PatrolReaders.CountAsync(r => r.Enabled && r.Status == PatrolReaderStatus.Approved && r.Id != id, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "patrol-reader", reader.Name);
            reader.Status = PatrolReaderStatus.Approved;
            reader.Enabled = true;
            reader.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "patrol", "reader-approved", "patrol-reader", reader.Id.ToString(), reader.Name,
                $"Aprobó el bastón {reader.Serial:D6} ('{reader.Name}'): desde ahora la base lo vacía tras cada descarga.");
            await patrol.NotifyAsync("readers", ct);
            return Results.Ok(ToDto(reader, false));
        });

        app.MapPost("/api/patrol/readers/{id:int}/reject", async (HttpContext ctx, int id, VmsDbContext db,
            AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out _) is { } failure) return failure;
            var reader = await db.PatrolReaders.Include(r => r.Agent).FirstOrDefaultAsync(r => r.Id == id, ct);
            if (reader is null) return Results.NotFound();
            reader.Status = PatrolReaderStatus.Rejected;
            reader.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "patrol", "reader-rejected", "patrol-reader", reader.Id.ToString(), reader.Name,
                $"Rechazó el bastón {reader.Serial:D6}: sus lecturas se siguen guardando, pero nunca se vacía.");
            await patrol.NotifyAsync("readers", ct);
            return Results.Ok(ToDto(reader, false));
        });

        app.MapDelete("/api/patrol/readers/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out _) is { } failure) return failure;
            var reader = await db.PatrolReaders.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (reader is null) return Results.NotFound();
            if (await db.PatrolRawReads.AnyAsync(r => r.ReaderId == id, ct))
                return Error("El bastón tiene lecturas guardadas: deshabilítelo en vez de eliminarlo (se perdería su historial).",
                    StatusCodes.Status409Conflict);
            db.PatrolReaders.Remove(reader);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "patrol", "reader-deleted", "patrol-reader", id.ToString(), reader.Name,
                $"Eliminó el bastón {reader.Serial:D6} ('{reader.Name}').");
            await patrol.NotifyAsync("readers", ct);
            return Results.Ok();
        });

        // Orden a la base: leer (y quizás vaciar) un bastón ahora. Si no está en la base, queda pendiente.
        app.MapPost("/api/patrol/readers/{id:int}/read", async (HttpContext ctx, int id, PatrolReadRequestDto request,
            VmsDbContext db, AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, request.Erase ? Permissions.PatrolErase : Permissions.PatrolRead, out var session) is { } failure)
                return failure;
            var reader = await db.PatrolReaders.Include(r => r.Agent).FirstOrDefaultAsync(r => r.Id == id, ct);
            if (reader is null) return Results.NotFound();
            if (!(await ctx.ScopeAsync(session)).CanOperate(reader.LocationId))
                return await ctx.OutOfScopeAsync(session, "patrol-reader", id.ToString(), reader.Name, "leer el bastón");
            if (reader.AgentId is null) return Error("Este bastón nunca se ha conectado a una base: léalo desde el navegador.");
            if (await db.PatrolAgentCommands.AnyAsync(c => c.ReaderId == id && c.CompletedAt == null, ct))
                return Error("Ya hay una lectura pendiente para este bastón.", StatusCodes.Status409Conflict);
            db.PatrolAgentCommands.Add(new PatrolAgentCommandRow
            {
                AgentId = reader.AgentId.Value, ReaderId = id, Kind = request.Erase ? "read-erase" : "read",
                RequestedByUserId = session.UserId, RequestedBy = session.Username,
            });
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "patrol", "read-requested", "patrol-reader", reader.Id.ToString(), reader.Name,
                $"Pidió a la base '{reader.Agent?.Name}' {(request.Erase ? "leer y vaciar" : "leer")} el bastón {reader.Serial:D6}" +
                (reader.Docked ? "." : " (no está en la base: se hará cuando vuelva)."));
            await patrol.NotifyAsync("readers", ct);
            return Results.Ok(new { docked = reader.Docked });
        });

        app.MapGet("/api/patrol/downloads", async (HttpContext ctx, int? readerId, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolView, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var readers = await db.PatrolReaders.AsNoTracking().ToDictionaryAsync(r => r.Id, ct);
            var query = db.PatrolDownloads.AsNoTracking().AsQueryable();
            if (readerId is not null) query = query.Where(d => d.ReaderId == readerId);
            var rows = await query.OrderByDescending(d => d.Id).Take(200).ToListAsync(ct);
            return Results.Ok(rows.Where(d => readers.TryGetValue(d.ReaderId, out var r) && scope.CanView(r.LocationId))
                .Select(d => new PatrolDownloadDto(d.Id, d.ReaderId, readers[d.ReaderId].Name, d.Origin, d.Actor,
                    d.StartedAt, d.FinishedAt, d.Blocks, d.Reads, d.NewReads, d.NewPasses, d.ClockSet, d.Erased, d.Status, d.Error)));
        });

        // ------------------------------------------------------------------
        // Lectura desde el navegador (Web Serial)
        app.MapGet("/api/patrol/server-time", (HttpContext ctx) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolRead, out _) is { } failure) return failure;
            var now = DateTime.UtcNow;
            return Results.Ok(new PatrolServerTimeDto(PatrolService.ToLocal(now), now));
        });

        app.MapPost("/api/patrol/browser-upload", async (HttpContext ctx, PatrolBrowserUploadDto request,
            LicenseService license, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolRead, out var session) is { } failure) return failure;
            if (!license.IsModuleEnabled(LicenseFeatures.ModulePatrol))
                return await license.DenyAsync(ctx, "La licencia no incluye el control de rondas.", "patrol-reader", $"{request.Serial:D6}");
            if (PatrolAgentApi.Validate(request.BlocksHex) is { } invalid) return Error(invalid);
            var result = await patrol.IngestAsync(request.Serial, request.BlocksHex, request.DeclaredBlocks, request.ClockSetAt,
                new PatrolUploadSource(PatrolDownloadOrigin.Browser, session.Username, null, session.UserId,
                    ctx.Connection.RemoteIpAddress?.ToString(), null), ct);
            // Vaciar desde el navegador exige además el permiso de vaciar.
            if (!(await ctx.ScopeAsync(session)).Has(Permissions.PatrolErase)) result = result with { EraseAllowed = false };
            return Results.Ok(result);
        });

        app.MapPost("/api/patrol/browser-erase-report", async (HttpContext ctx, PatrolAgentEraseReport request,
            VmsDbContext db, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolErase, out var session) is { } failure) return failure;
            if (!await db.PatrolDownloads.AnyAsync(d => d.Id == request.DownloadId && d.Origin == PatrolDownloadOrigin.Browser, ct))
                return Results.NotFound();
            await patrol.ReportEraseAsync(request.DownloadId, request.Erased, request.Error,
                new PatrolUploadSource(PatrolDownloadOrigin.Browser, session.Username, null, session.UserId,
                    ctx.Connection.RemoteIpAddress?.ToString(), null), ct);
            return Results.Ok();
        });

        // ------------------------------------------------------------------
        // Puntos de control
        app.MapGet("/api/patrol/checkpoints", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolView, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var rows = await db.PatrolCheckpoints.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
            var last = await db.PatrolPasses.Where(p => !p.Voided).GroupBy(p => p.CheckpointId)
                .Select(g => new { g.Key, At = g.Max(p => p.ReadAt) }).ToDictionaryAsync(x => x.Key, x => x.At, ct);
            var paths = await LocationPathsAsync(db, ct);
            return Results.Ok(rows.Where(c => scope.CanView(c.LocationId)).Select(c => ToDto(c, paths, last.GetValueOrDefault(c.Id))));
        });

        app.MapPost("/api/patrol/checkpoints", async (HttpContext ctx, PatrolCheckpointWriteDto request, VmsDbContext db,
            LicenseService license, AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out _) is { } failure) return failure;
            var (row, invalid) = await ValidateCheckpointAsync(db, request, null, ct);
            if (invalid is not null) return invalid;
            if (license.Deny(LicenseFeatures.ModulePatrol, request.Enabled ? LicenseFeatures.PatrolCheckpoints : null,
                    await db.PatrolCheckpoints.CountAsync(c => c.Enabled, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "patrol-checkpoint", request.Name.Trim());
            db.PatrolCheckpoints.Add(row!);
            await db.SaveChangesAsync(ct);
            int created = row!.TagId is null ? 0 : await patrol.ReprocessTagAsync(db, row.TagId, ct);
            await audit.LogAsync(ctx, "patrol", "checkpoint-created", "patrol-checkpoint", row.Id.ToString(), row.Name,
                $"Agregó el punto de control '{row.Name}'" + (row.TagId is null ? "." : $" (tag {row.TagId}).") +
                (created > 0 ? $" Se armaron {created} pasos con lecturas que ya estaban guardadas." : ""));
            await patrol.NotifyAsync("checkpoints", ct);
            return Results.Ok(ToDto(row, await LocationPathsAsync(db, ct), null));
        });

        app.MapPut("/api/patrol/checkpoints/{id:int}", async (HttpContext ctx, int id, PatrolCheckpointWriteDto request,
            VmsDbContext db, LicenseService license, AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out _) is { } failure) return failure;
            var existing = await db.PatrolCheckpoints.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (existing is null) return Results.NotFound();
            var (row, invalid) = await ValidateCheckpointAsync(db, request, existing, ct);
            if (invalid is not null) return invalid;
            if (!existing.Enabled && request.Enabled
                && license.Deny(LicenseFeatures.ModulePatrol, LicenseFeatures.PatrolCheckpoints,
                    await db.PatrolCheckpoints.CountAsync(c => c.Enabled && c.Id != id, ct)) is { } denied)
                return await license.DenyAsync(ctx, denied, "patrol-checkpoint", existing.Name);
            string? oldTag = existing.TagId;
            existing.Name = row!.Name;
            existing.Kind = row.Kind;
            existing.TagId = row.TagId;
            existing.AccessDoorId = row.AccessDoorId;
            existing.LocationId = row.LocationId;
            existing.Enabled = row.Enabled;
            existing.Notes = row.Notes;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            if (existing.TagId is not null && existing.TagId != oldTag) await patrol.ReprocessTagAsync(db, existing.TagId, ct);
            await audit.LogAsync(ctx, "patrol", "checkpoint-updated", "patrol-checkpoint", existing.Id.ToString(), existing.Name,
                $"Modificó el punto de control '{existing.Name}'" + (oldTag != existing.TagId ? $" (tag {oldTag} → {existing.TagId})." : "."));
            await patrol.NotifyAsync("checkpoints", ct);
            return Results.Ok(ToDto(existing, await LocationPathsAsync(db, ct), null));
        });

        app.MapDelete("/api/patrol/checkpoints/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out _) is { } failure) return failure;
            var row = await db.PatrolCheckpoints.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (row is null) return Results.NotFound();
            if (await db.PatrolPasses.AnyAsync(p => p.CheckpointId == id, ct))
                return Error("El punto tiene pasos en la bitácora: deshabilítelo en vez de eliminarlo.", StatusCodes.Status409Conflict);
            db.PatrolCheckpoints.Remove(row);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "patrol", "checkpoint-deleted", "patrol-checkpoint", id.ToString(), row.Name,
                $"Eliminó el punto de control '{row.Name}'.");
            await patrol.NotifyAsync("checkpoints", ct);
            return Results.Ok();
        });

        // Tags leídos por los bastones que todavía no son ni punto ni llavero.
        app.MapGet("/api/patrol/unknown-tags", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out _) is { } failure) return failure;
            var rows = await db.PatrolRawReads
                .Where(r => !db.PatrolCheckpoints.Any(c => c.TagId == r.TagId) && !db.PatrolTags.Any(t => t.TagId == r.TagId))
                .GroupBy(r => r.TagId)
                .Select(g => new { TagId = g.Key, Reads = g.Count(), First = g.Min(r => r.ReadAt), Last = g.Max(r => r.ReadAt),
                    LastReader = g.OrderByDescending(r => r.ReadAt).Select(r => r.ReaderId).First() })
                .OrderByDescending(x => x.Last).Take(200).ToListAsync(ct);
            var names = await db.PatrolReaders.ToDictionaryAsync(r => r.Id, r => r.Name, ct);
            return Results.Ok(rows.Select(x => new PatrolUnknownTagDto(x.TagId, x.Reads, x.First, x.Last, names.GetValueOrDefault(x.LastReader))));
        });

        // ------------------------------------------------------------------
        // Guardias y llaveros
        app.MapGet("/api/patrol/guards", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolView, out _) is { } failure) return failure;
            var guards = await db.PatrolGuards.AsNoTracking().Include(g => g.AccessPerson).Include(g => g.Tags)
                .OrderBy(g => g.AccessPerson!.FirstName).ThenBy(g => g.AccessPerson!.LastName).ToListAsync(ct);
            var last = await db.PatrolPasses.Where(p => !p.Voided && p.PatrolGuardId != null).GroupBy(p => p.PatrolGuardId)
                .Select(g => new { g.Key, At = g.Max(p => p.ReadAt) }).ToDictionaryAsync(x => x.Key!.Value, x => x.At, ct);
            return Results.Ok(guards.Select(g => ToDto(g, last.GetValueOrDefault(g.Id))));
        });

        app.MapPost("/api/patrol/guards", async (HttpContext ctx, PatrolGuardCreateDto request, VmsDbContext db,
            AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out _) is { } failure) return failure;
            AccessPerson? person;
            bool createdPerson = false;
            if (request.AccessPersonId is { } personId)
            {
                person = await db.AccessPersons.FirstOrDefaultAsync(p => p.Id == personId, ct);
                if (person is null) return Error("La persona no existe.", StatusCodes.Status404NotFound);
            }
            else
            {
                string first = (request.FirstName ?? "").Trim(), lastName = (request.LastName ?? "").Trim();
                if (first.Length is 0 or > 64 || lastName.Length > 64) return Error("El nombre es obligatorio (máximo 64 caracteres).");
                string? employeeNo = string.IsNullOrWhiteSpace(request.EmployeeNo) ? null : request.EmployeeNo.Trim();
                if (employeeNo is { Length: > 32 }) return Error("El RUT o número de empleado admite 32 caracteres.");
                // Sin duplicados: si ya está en el padrón, se le agrega el rol en vez de crear otra persona.
                person = employeeNo is null ? null : await db.AccessPersons.FirstOrDefaultAsync(p => p.EmployeeNo == employeeNo, ct);
                if (person is null)
                {
                    person = new AccessPerson
                    {
                        FirstName = first, LastName = lastName,
                        EmployeeNo = employeeNo ?? await NextEmployeeNoAsync(db, ct),
                        Position = "Guardia",
                    };
                    db.AccessPersons.Add(person);
                    await db.SaveChangesAsync(ct);
                    createdPerson = true;
                }
            }
            if (await db.PatrolGuards.AnyAsync(g => g.AccessPersonId == person.Id, ct))
                return Error($"'{person.FullName}' ya es guardia.", StatusCodes.Status409Conflict);
            var guard = new PatrolGuard { AccessPersonId = person.Id, Notes = Trim(request.Notes, 1024) };
            db.PatrolGuards.Add(guard);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "patrol", "guard-created", "patrol-guard", guard.Id.ToString(), person.FullName,
                createdPerson
                    ? $"Agregó al guardia '{person.FullName}' ({person.EmployeeNo}); se creó en el padrón sin niveles de acceso."
                    : $"Marcó como guardia a '{person.FullName}' ({person.EmployeeNo}), que ya estaba en el padrón.");
            await patrol.NotifyAsync("guards", ct);
            guard.AccessPerson = person;
            return Results.Ok(ToDto(guard, null));
        });

        app.MapPut("/api/patrol/guards/{id:int}", async (HttpContext ctx, int id, PatrolGuardUpdateDto request, VmsDbContext db,
            AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out _) is { } failure) return failure;
            var guard = await db.PatrolGuards.Include(g => g.AccessPerson).Include(g => g.Tags).FirstOrDefaultAsync(g => g.Id == id, ct);
            if (guard is null) return Results.NotFound();
            guard.Notes = Trim(request.Notes, 1024);
            var changes = new List<string>();
            if (request.TagIds is not null)
            {
                if (!guard.Active && request.TagIds.Count > 0) return Error("Un guardia desvinculado no puede tener llaveros.");
                var wanted = request.TagIds.ToHashSet();
                foreach (var t in guard.Tags.Where(t => !wanted.Contains(t.Id)).ToList())
                {
                    t.PatrolGuardId = null;
                    t.AssignedAt = null;
                    changes.Add($"quitó el llavero {t.TagId}");
                }
                var toAdd = await db.PatrolTags.Where(t => wanted.Contains(t.Id) && t.PatrolGuardId != id).ToListAsync(ct);
                foreach (var t in toAdd)
                {
                    if (t.PatrolGuardId is not null) return Error($"El llavero {t.TagId} ya está asignado a otro guardia.", StatusCodes.Status409Conflict);
                    t.PatrolGuardId = id;
                    t.AssignedAt = DateTime.UtcNow;
                    changes.Add($"asignó el llavero {t.TagId}");
                }
            }
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "patrol", "guard-updated", "patrol-guard", guard.Id.ToString(), guard.AccessPerson?.FullName,
                $"Modificó al guardia '{guard.AccessPerson?.FullName}'" + (changes.Count > 0 ? $": {string.Join(", ", changes)}." : "."));
            await patrol.NotifyAsync("guards", ct);
            var updated = await db.PatrolGuards.AsNoTracking().Include(g => g.AccessPerson).Include(g => g.Tags).FirstAsync(g => g.Id == id, ct);
            return Results.Ok(ToDto(updated, null));
        });

        // Desvincular: desactiva a la persona en TODO el sistema (sale de los terminales), libera sus llaveros y conserva el historial.
        app.MapPost("/api/patrol/guards/{id:int}/deactivate", async (HttpContext ctx, int id, PatrolGuardDeactivateDto request,
            VmsDbContext db, AccessSyncService sync, AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out var session) is { } failure) return failure;
            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 512)
                return Error("Indique el motivo (máximo 512 caracteres).");
            var guard = await db.PatrolGuards.Include(g => g.AccessPerson).Include(g => g.Tags).FirstOrDefaultAsync(g => g.Id == id, ct);
            if (guard is null) return Results.NotFound();
            if (!guard.Active) return Error("El guardia ya está desvinculado.", StatusCodes.Status409Conflict);
            var person = guard.AccessPerson!;
            bool hadAccess = person.Enabled;
            var released = guard.Tags.Select(t => t.TagId).ToList();
            foreach (var t in guard.Tags) { t.PatrolGuardId = null; t.AssignedAt = null; }
            guard.Active = false;
            guard.DeactivatedAt = DateTime.UtcNow;
            guard.DeactivatedBy = session.Username;
            guard.DeactivationReason = request.Reason.Trim();
            person.Enabled = false;
            person.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            if (hadAccess)
            {
                await AccessSyncService.MarkPendingAsync(db, [person.Id], ct);
                sync.RequestSync();
            }
            await audit.LogAsync(ctx, "patrol", "guard-deactivated", "patrol-guard", guard.Id.ToString(), person.FullName,
                $"Desvinculó al guardia '{person.FullName}' ({person.EmployeeNo}). Motivo: {guard.DeactivationReason}. " +
                $"Persona desactivada en el control de acceso (se quita de los terminales)" +
                (released.Count > 0 ? $"; llaveros liberados: {string.Join(", ", released)}." : "."));
            await patrol.NotifyAsync("guards", ct);
            return Results.Ok();
        });

        app.MapPost("/api/patrol/guards/{id:int}/reactivate", async (HttpContext ctx, int id, VmsDbContext db,
            AccessSyncService sync, AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out _) is { } failure) return failure;
            if (ApiSecurity.Require(ctx, Permissions.PersonsManage, out _) is { } noPersons) return noPersons;
            var guard = await db.PatrolGuards.Include(g => g.AccessPerson).FirstOrDefaultAsync(g => g.Id == id, ct);
            if (guard is null) return Results.NotFound();
            guard.Active = true;
            guard.DeactivatedAt = null;
            guard.DeactivatedBy = null;
            guard.DeactivationReason = null;
            guard.AccessPerson!.Enabled = true;
            guard.AccessPerson.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await AccessSyncService.MarkPendingAsync(db, [guard.AccessPersonId], ct);
            sync.RequestSync();
            await audit.LogAsync(ctx, "patrol", "guard-reactivated", "patrol-guard", guard.Id.ToString(), guard.AccessPerson.FullName,
                $"Reincorporó al guardia '{guard.AccessPerson.FullName}' (persona reactivada en el control de acceso).");
            await patrol.NotifyAsync("guards", ct);
            return Results.Ok();
        });

        app.MapGet("/api/patrol/tags", async (HttpContext ctx, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolView, out _) is { } failure) return failure;
            var tags = await db.PatrolTags.AsNoTracking().Include(t => t.PatrolGuard!).ThenInclude(g => g.AccessPerson)
                .OrderBy(t => t.TagId).ToListAsync(ct);
            var last = await db.PatrolPasses.Where(p => p.PatrolTagId != null).GroupBy(p => p.PatrolTagId)
                .Select(g => new { g.Key, At = g.Max(p => p.ReadAt) }).ToDictionaryAsync(x => x.Key!.Value, x => x.At, ct);
            return Results.Ok(tags.Select(t => ToDto(t, last.GetValueOrDefault(t.Id))));
        });

        app.MapPost("/api/patrol/tags", async (HttpContext ctx, PatrolTagWriteDto request, VmsDbContext db,
            AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out _) is { } failure) return failure;
            if (NormalizeTag(request.TagId) is not { } tagId) return Error("El tag son 10 caracteres hexadecimales (ej. 0D00D6712D).");
            if (await TagInUseAsync(db, tagId, null, null, ct) is { } inUse) return Error(inUse, StatusCodes.Status409Conflict);
            if (request.GuardId is { } gid && !await db.PatrolGuards.AnyAsync(g => g.Id == gid && g.Active, ct))
                return Error("El guardia no existe o está desvinculado.", StatusCodes.Status404NotFound);
            var tag = new PatrolTag
            {
                TagId = tagId, Label = Trim(request.Label, 128), PatrolGuardId = request.GuardId, Enabled = request.Enabled,
                AssignedAt = request.GuardId is null ? null : DateTime.UtcNow,
            };
            db.PatrolTags.Add(tag);
            await db.SaveChangesAsync(ct);
            int created = await patrol.ReprocessTagAsync(db, tagId, ct);
            await audit.LogAsync(ctx, "patrol", "tag-created", "patrol-tag", tag.Id.ToString(), tagId,
                $"Agregó el llavero {tagId}" + (created > 0 ? $"; se completaron pasos con lecturas ya guardadas." : "."));
            await patrol.NotifyAsync("guards", ct);
            return Results.Ok(new { tag.Id });
        });

        app.MapPut("/api/patrol/tags/{id:int}", async (HttpContext ctx, int id, PatrolTagWriteDto request, VmsDbContext db,
            AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out _) is { } failure) return failure;
            var tag = await db.PatrolTags.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (tag is null) return Results.NotFound();
            if (request.GuardId is { } gid && gid != tag.PatrolGuardId && !await db.PatrolGuards.AnyAsync(g => g.Id == gid && g.Active, ct))
                return Error("El guardia no existe o está desvinculado.", StatusCodes.Status404NotFound);
            if (tag.PatrolGuardId != request.GuardId) tag.AssignedAt = request.GuardId is null ? null : DateTime.UtcNow;
            tag.PatrolGuardId = request.GuardId;
            tag.Label = Trim(request.Label, 128);
            tag.Enabled = request.Enabled;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "patrol", "tag-updated", "patrol-tag", tag.Id.ToString(), tag.TagId, $"Modificó el llavero {tag.TagId}.");
            await patrol.NotifyAsync("guards", ct);
            return Results.Ok();
        });

        app.MapDelete("/api/patrol/tags/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolConfigure, out _) is { } failure) return failure;
            var tag = await db.PatrolTags.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (tag is null) return Results.NotFound();
            db.PatrolTags.Remove(tag);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "patrol", "tag-deleted", "patrol-tag", id.ToString(), tag.TagId,
                $"Eliminó el llavero {tag.TagId} (los pasos conservan el nombre del guardia).");
            await patrol.NotifyAsync("guards", ct);
            return Results.Ok();
        });

        // ------------------------------------------------------------------
        // Bitácora
        app.MapGet("/api/patrol/passes", async (HttpContext ctx, DateTime? from, DateTime? to, int? guardId, int? checkpointId,
            bool? includeVoided, int? page, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolView, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var (rows, total) = await QueryPassesAsync(db, scope, from, to, guardId, checkpointId, includeVoided == true,
                Math.Max(page ?? 1, 1), 100, ct);
            return Results.Ok(new PatrolPassPageDto(rows, total));
        });

        app.MapGet("/api/patrol/passes/export", async (HttpContext ctx, DateTime? from, DateTime? to, int? guardId, int? checkpointId,
            VmsDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolExport, out var session) is { } failure) return failure;
            var scope = await ctx.ScopeAsync(session);
            var (rows, total) = await QueryPassesAsync(db, scope, from, to, guardId, checkpointId, true, 1, 100_000, ct);
            var csv = new StringBuilder("Fecha;Hora;Punto;Ubicación;Guardia;RUT/N°;Llavero;Bastón;Origen;Estado;Corrección\r\n");
            foreach (var p in rows)
            {
                var local = PatrolService.ToLocal(p.ReadAt);
                csv.Append(string.Join(';', [
                    local.ToString("dd-MM-yyyy"), local.ToString("HH:mm:ss"), Csv(p.CheckpointName), Csv(p.Location),
                    Csv(p.GuardName ?? "Sin guardia"), Csv(p.GuardEmployeeNo), Csv(p.GuardTagId), Csv(p.ReaderName),
                    p.Source.ToString(), p.Voided ? $"Anulado: {Csv(p.VoidReason)}" : "Válido",
                    p.EditedBy is null ? "" : Csv($"{p.EditedBy}: {p.EditReason}")])).Append("\r\n");
            }
            await audit.LogAsync(ctx, "patrol", "log-exported", "patrol-log", null, null,
                $"Exportó {total} pasos de la bitácora de rondas ({from:dd-MM-yyyy} a {to:dd-MM-yyyy}).");
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
            return Results.File(bytes, "text/csv; charset=utf-8", $"rondas-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        });

        app.MapPut("/api/patrol/passes/{id:long}", async (HttpContext ctx, long id, PatrolPassEditDto request, VmsDbContext db,
            AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolEdit, out var session) is { } failure) return failure;
            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 512)
                return Error("Indique el motivo de la corrección (máximo 512 caracteres).");
            var pass = await db.PatrolPasses.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (pass is null) return Results.NotFound();
            var guard = request.GuardId is null ? null
                : await db.PatrolGuards.Include(g => g.AccessPerson).FirstOrDefaultAsync(g => g.Id == request.GuardId, ct);
            if (request.GuardId is not null && guard is null) return Error("El guardia no existe.", StatusCodes.Status404NotFound);
            string before = pass.GuardName ?? "sin guardia";
            pass.PatrolGuardId = guard?.Id;
            pass.GuardName = guard?.AccessPerson?.FullName;
            pass.GuardEmployeeNo = guard?.AccessPerson?.EmployeeNo;
            pass.EditedBy = session.Username;
            pass.EditedAt = DateTime.UtcNow;
            pass.EditReason = request.Reason.Trim();
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "patrol", "pass-edited", "patrol-pass", id.ToString(), null,
                $"Corrigió el paso {id} ({PatrolService.ToLocal(pass.ReadAt):dd-MM-yyyy HH:mm:ss}): guardia '{before}' → " +
                $"'{pass.GuardName ?? "sin guardia"}'. Motivo: {pass.EditReason}");
            await patrol.NotifyAsync("passes", ct);
            return Results.Ok();
        });

        app.MapPost("/api/patrol/passes/{id:long}/void", async (HttpContext ctx, long id, PatrolPassVoidDto request, VmsDbContext db,
            AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.PatrolEdit, out var session) is { } failure) return failure;
            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 512)
                return Error("Indique el motivo de la anulación (máximo 512 caracteres).");
            var pass = await db.PatrolPasses.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (pass is null) return Results.NotFound();
            pass.Voided = true;
            pass.VoidReason = request.Reason.Trim();
            pass.EditedBy = session.Username;
            pass.EditedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "patrol", "pass-voided", "patrol-pass", id.ToString(), null,
                $"Anuló el paso {id} ({PatrolService.ToLocal(pass.ReadAt):dd-MM-yyyy HH:mm:ss}). Motivo: {pass.VoidReason}");
            await patrol.NotifyAsync("passes", ct);
            return Results.Ok();
        });
    }

    // ----------------------------------------------------------------------

    private static async Task<(List<PatrolPassDto> Rows, int Total)> QueryPassesAsync(VmsDbContext db, UserScope scope,
        DateTime? from, DateTime? to, int? guardId, int? checkpointId, bool includeVoided, int page, int pageSize, CancellationToken ct)
    {
        var start = from is null ? DateTime.UtcNow.AddDays(-1) : PatrolService.ToUtc(from.Value);
        var end = to is null ? DateTime.UtcNow.AddMinutes(1) : PatrolService.ToUtc(to.Value);
        var visible = (await db.PatrolCheckpoints.AsNoTracking().ToListAsync(ct)).Where(c => scope.CanView(c.LocationId)).ToList();
        var ids = visible.Select(c => c.Id).ToList();
        var q = db.PatrolPasses.AsNoTracking().Where(p => p.ReadAt >= start && p.ReadAt < end && ids.Contains(p.CheckpointId));
        if (!includeVoided) q = q.Where(p => !p.Voided);
        if (guardId is not null) q = q.Where(p => p.PatrolGuardId == guardId);
        if (checkpointId is not null) q = q.Where(p => p.CheckpointId == checkpointId);
        int total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(p => p.ReadAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var names = visible.ToDictionary(c => c.Id);
        var paths = await LocationPathsAsync(db, ct);
        var readers = await db.PatrolReaders.ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var tagIds = rows.Where(p => p.PatrolTagId != null).Select(p => p.PatrolTagId!.Value).Distinct().ToList();
        var tags = await db.PatrolTags.Where(t => tagIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.TagId, ct);
        return (rows.Select(p => new PatrolPassDto(p.Id, p.ReadAt, p.CheckpointId, names[p.CheckpointId].Name,
            names[p.CheckpointId].LocationId is { } l ? paths.GetValueOrDefault(l) : null,
            p.PatrolGuardId, p.GuardName, p.GuardEmployeeNo, p.PatrolTagId is { } t ? tags.GetValueOrDefault(t) : null,
            p.ReaderId, p.ReaderId is { } r ? readers.GetValueOrDefault(r) : null, p.Source,
            p.Voided, p.VoidReason, p.EditedBy, p.EditedAt, p.EditReason)).ToList(), total);
    }

    private static async Task<(PatrolCheckpoint? Row, IResult? Error)> ValidateCheckpointAsync(VmsDbContext db,
        PatrolCheckpointWriteDto request, PatrolCheckpoint? existing, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
            return (null, Error("El nombre es obligatorio (máximo 128 caracteres)."));
        string? tagId = null;
        if (request.Kind == PatrolCheckpointKind.Tag)
        {
            tagId = NormalizeTag(request.TagId);
            if (tagId is null) return (null, Error("El tag son 10 caracteres hexadecimales (ej. 3C007BC71D)."));
            if (await TagInUseAsync(db, tagId, existing?.Id, null, ct) is { } inUse) return (null, Error(inUse, StatusCodes.Status409Conflict));
        }
        else
        {
            if (request.AccessDoorId is null || !await db.AccessDoors.AnyAsync(d => d.Id == request.AccessDoorId, ct))
                return (null, Error("Elija la puerta o terminal de acceso del punto."));
        }
        var (locationId, locationError) = await EquipmentLocation.ResolveAsync(db, request.LocationId, existing?.LocationId, ct);
        if (locationError is not null) return (null, Error(locationError, StatusCodes.Status404NotFound));
        return (new PatrolCheckpoint
        {
            Name = request.Name.Trim(), Kind = request.Kind, TagId = tagId,
            AccessDoorId = request.Kind == PatrolCheckpointKind.Access ? request.AccessDoorId : null,
            LocationId = locationId, Enabled = request.Enabled, Notes = Trim(request.Notes, 1024),
        }, null);
    }

    private static async Task<Dictionary<int, string>> LocationPathsAsync(VmsDbContext db, CancellationToken ct)
    {
        var locations = await db.Locations.AsNoTracking().Select(l => new { l.Id, l.Name, l.ParentId }).ToListAsync(ct);
        var byId = locations.ToDictionary(l => l.Id);
        var result = new Dictionary<int, string>();
        foreach (var l in locations)
        {
            var parts = new List<string>();
            for (var cur = l; cur is not null && parts.Count < 32; cur = cur.ParentId is { } p ? byId.GetValueOrDefault(p) : null)
                parts.Insert(0, cur.Name);
            result[l.Id] = string.Join(" / ", parts);
        }
        return result;
    }

    private static async Task<string> NextEmployeeNoAsync(VmsDbContext db, CancellationToken ct)
    {
        var existing = await db.AccessPersons.Select(p => p.EmployeeNo).ToListAsync(ct);
        int highest = 999;
        foreach (string value in existing)
            if (int.TryParse(value, out int number) && number > highest) highest = number;
        return (highest + 1).ToString();
    }

    private static PatrolAgentDto ToDto(PatrolAgent a)
    {
        var now = DateTime.UtcNow;
        var status = a.TokenHash is null ? PatrolAgentStatus.Pending
            : a.LastSeenAt is { } seen && now - seen <= TimeSpan.FromSeconds(PatrolAgentProtocol.OfflineAfterSeconds)
                ? PatrolAgentStatus.Online : PatrolAgentStatus.Offline;
        return new PatrolAgentDto(a.Id, a.Name, status, a.MachineName, a.Version, a.LocationId, null, a.LastSeenAt, a.LastError,
            a.EnrollmentCodeHash is not null && a.EnrollmentExpiresAt > now, a.EnrollmentExpiresAt);
    }

    private static PatrolReaderDto ToDto(PatrolReader r, bool pendingCommand) => new(
        r.Id, r.Serial, r.Serial.ToString("D6"), r.Name, r.DriverKey, r.Status, r.Enabled, r.DisabledByLicense,
        r.LocationId, null, r.AgentId, r.Agent?.Name, r.Docked, r.DockedChangedAt, r.LastDownloadAt, r.LastClockSetAt,
        r.LastMemoryBlocks, r.LastError, pendingCommand);

    private static PatrolCheckpointDto ToDto(PatrolCheckpoint c, Dictionary<int, string> paths, DateTime? lastPass) => new(
        c.Id, c.Name, c.Kind, c.TagId, c.AccessDoorId, c.LocationId,
        c.LocationId is { } l ? paths.GetValueOrDefault(l) : null, c.Enabled, c.DisabledByLicense, c.Notes,
        NullIfUnset(lastPass));

    private static PatrolTagDto ToDto(PatrolTag t, DateTime? lastRead) => new(
        t.Id, t.TagId, t.Label, t.Enabled, t.PatrolGuardId, t.PatrolGuard?.AccessPerson?.FullName,
        NullIfUnset(lastRead));

    private static PatrolGuardDto ToDto(PatrolGuard g, DateTime? lastPass) => new(
        g.Id, g.AccessPersonId, g.AccessPerson?.FullName ?? "", g.AccessPerson?.EmployeeNo ?? "", g.Active,
        g.AccessPerson?.Enabled ?? false, g.Notes,
        g.Tags.OrderBy(t => t.TagId).Select(t => new PatrolTagDto(t.Id, t.TagId, t.Label, t.Enabled, g.Id, g.AccessPerson?.FullName, null)).ToList(),
        NullIfUnset(lastPass), g.DeactivatedAt, g.DeactivatedBy, g.DeactivationReason);

    /// <summary>GetValueOrDefault de un diccionario de DateTime da DateTime.MinValue, no null.</summary>
    private static DateTime? NullIfUnset(DateTime? value) => value is { } v && v != default ? v : null;

    private static string Csv(string? s) => (s ?? "").Replace(';', ',').Replace('\n', ' ').Replace('\r', ' ');

    private static string? Trim(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= max ? s.Trim() : s.Trim()[..max];
}
