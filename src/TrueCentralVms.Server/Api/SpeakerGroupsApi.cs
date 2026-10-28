using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Workflows;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// Grupos de parlantes ("Perímetro", "Bodega", "Todos"): para hablar o hacer
/// sonar varios de una vez desde el Centro de mando, un tablero o una
/// automatización. Los ve quien usa o configura parlantes; los arma quien
/// configura parlantes. Las automatizaciones los nombran por su nombre: al
/// renombrar uno, sus acciones se actualizan solas.
/// </summary>
public static class SpeakerGroupsApi
{
    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? Validate(SpeakerGroupWriteDto request)
    {
        string name = (request.Name ?? "").Trim();
        if (name.Length == 0) return "El nombre del grupo es obligatorio.";
        if (name.Length > 64) return "El nombre no puede superar los 64 caracteres.";
        if ((Clean(request.Description)?.Length ?? 0) > 255) return "La descripción no puede superar los 255 caracteres.";
        return null;
    }

    private static string Names(IEnumerable<Speaker> speakers) =>
        string.Join(", ", speakers.Select(s => $"'{s.Name}'").DefaultIfEmpty("ninguno"));

    /// <summary>Acciones "sonar parlante" que nombran al grupo (sin distinguir mayúsculas).</summary>
    private static async Task<List<WorkflowAction>> ActionsUsingAsync(VmsDbContext db, string groupName, CancellationToken ct)
    {
        var candidates = await db.WorkflowActions
            .Where(a => a.Type == WorkflowActionTypes.Speaker && a.ConfigJson.Contains("\"group\""))
            .ToListAsync(ct);
        return candidates.Where(a =>
        {
            try
            {
                return JsonNode.Parse(a.ConfigJson)?["group"]?.GetValue<string>()?.Trim() is { } group &&
                       string.Equals(group, groupName, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }).ToList();
    }

    private static async Task AnnounceAsync(HttpContext ctx, SpeakerService service, CancellationToken ct)
    {
        await service.ReloadGroupsAsync(ct);
        await ctx.RequestServices.GetRequiredService<IHubContext<VmsHub>>()
            .Clients.All.SendAsync(VmsHubContract.ConfigChanged, "speaker-groups", ct);
    }

    public static void MapSpeakerGroupsApi(this WebApplication app)
    {
        app.MapGet("/api/speakers/groups", (HttpContext ctx, SpeakerService service) =>
        {
            if (ApiSecurity.RequireAny(ctx, out _, Permissions.SpeakersPlay, Permissions.SpeakersConfigure) is { } failure) return failure;
            return Results.Ok(service.Groups);
        });

        app.MapPost("/api/speakers/groups", async (HttpContext ctx, SpeakerGroupWriteDto request, VmsDbContext db,
            SpeakerService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SpeakersConfigure, out _) is { } failure) return failure;
            if (Validate(request) is { } invalid) return Error(invalid);
            string name = request.Name.Trim();
            if (await db.SpeakerGroups.AnyAsync(g => g.Name.ToLower() == name.ToLower(), ct))
                return Error("Ya hay un grupo con ese nombre.", StatusCodes.Status409Conflict);
            var ids = (request.SpeakerIds ?? []).Distinct().ToList();
            var speakers = await db.Speakers.Where(s => ids.Contains(s.Id)).OrderBy(s => s.Name).ToListAsync(ct);

            var group = new SpeakerGroup
            {
                Name = name,
                Description = Clean(request.Description),
                Members = speakers.Select(s => new SpeakerGroupMember { SpeakerId = s.Id }).ToList(),
            };
            db.SpeakerGroups.Add(group);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "speakers", "group-created",
                targetType: "speaker-group", targetId: group.Id.ToString(), targetName: group.Name,
                detail: $"Creó el grupo de parlantes '{group.Name}' con {speakers.Count} parlante(s): {Names(speakers)}.",
                data: new { speakerIds = speakers.Select(s => s.Id).ToList() });
            await AnnounceAsync(ctx, service, ct);
            return Results.Ok(service.Groups.First(g => g.Id == group.Id));
        });

        // Cambia nombre, descripción e integrantes. Las automatizaciones que lo
        // nombraban por el nombre anterior pasan al nuevo.
        app.MapPut("/api/speakers/groups/{id:int}", async (HttpContext ctx, int id, SpeakerGroupWriteDto request,
            VmsDbContext db, SpeakerService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SpeakersConfigure, out _) is { } failure) return failure;
            if (Validate(request) is { } invalid) return Error(invalid);
            var group = await db.SpeakerGroups.Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == id, ct);
            if (group is null) return Results.NotFound();
            string name = request.Name.Trim();
            if (await db.SpeakerGroups.AnyAsync(g => g.Id != id && g.Name.ToLower() == name.ToLower(), ct))
                return Error("Ya hay un grupo con ese nombre.", StatusCodes.Status409Conflict);

            var ids = (request.SpeakerIds ?? []).Distinct().ToList();
            var known = await db.Speakers.Where(s => ids.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct);
            var before = group.Members.Select(m => m.SpeakerId).ToHashSet();
            var addedIds = known.Where(i => !before.Contains(i)).ToList();
            var removedIds = before.Where(i => !known.Contains(i)).ToList();
            var touched = await db.Speakers.AsNoTracking().Where(s => addedIds.Contains(s.Id) || removedIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, ct);

            var changes = new List<string>();
            string oldName = group.Name;
            var workflowActions = new List<WorkflowAction>();
            if (!string.Equals(oldName, name, StringComparison.Ordinal))
            {
                changes.Add($"nombre '{oldName}' → '{name}'");
                workflowActions = await ActionsUsingAsync(db, oldName, ct);
                foreach (var action in workflowActions)
                {
                    var config = JsonNode.Parse(action.ConfigJson)!.AsObject();
                    config["group"] = name;
                    // Sin escapar tildes: la configuración queda legible igual que la guarda el editor.
                    action.ConfigJson = config.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                }
            }
            if (Clean(request.Description) != group.Description) changes.Add("descripción");
            if (addedIds.Count > 0) changes.Add("agregó " + Names(addedIds.Select(i => touched[i])));
            if (removedIds.Count > 0) changes.Add("quitó " + Names(removedIds.Select(i => touched[i])));

            group.Name = name;
            group.Description = Clean(request.Description);
            group.UpdatedAt = DateTime.UtcNow;
            group.Members.RemoveAll(m => removedIds.Contains(m.SpeakerId));
            foreach (int add in addedIds) group.Members.Add(new SpeakerGroupMember { SpeakerGroupId = id, SpeakerId = add });
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "speakers", "group-updated",
                targetType: "speaker-group", targetId: id.ToString(), targetName: group.Name,
                detail: $"Modificó el grupo de parlantes '{group.Name}': " +
                        (changes.Count > 0 ? string.Join("; ", changes) : "sin cambios") +
                        (workflowActions.Count > 0 ? $". {workflowActions.Count} acción(es) de automatizaciones pasaron al nombre nuevo." : "."),
                data: new { speakerIds = group.Members.Select(m => m.SpeakerId).ToList() });
            await AnnounceAsync(ctx, service, ct);
            return Results.Ok(service.Groups.First(g => g.Id == id));
        });

        // Los parlantes no se tocan; las automatizaciones que lo nombraban
        // quedan sin ese grupo (se avisa cuántas antes de borrarlo, con dryRun).
        app.MapDelete("/api/speakers/groups/{id:int}", async (HttpContext ctx, int id, bool? dryRun, VmsDbContext db,
            SpeakerService service, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SpeakersConfigure, out _) is { } failure) return failure;
            var group = await db.SpeakerGroups.FirstOrDefaultAsync(g => g.Id == id, ct);
            if (group is null) return Results.NotFound();
            int used = (await ActionsUsingAsync(db, group.Name, ct)).Count;
            if (dryRun == true) return Results.Ok(new { workflowActions = used });

            db.SpeakerGroups.Remove(group);
            // Los tableros que lo tenían en un cuadro lo pierden (el cuadro queda libre).
            db.LiveViewItems.RemoveRange(await db.LiveViewItems
                .Where(i => i.Kind == DashboardItemKinds.SpeakerGroup && i.RefId == id).ToListAsync(ct));
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(ctx, "speakers", "group-deleted",
                targetType: "speaker-group", targetId: id.ToString(), targetName: group.Name,
                detail: $"Eliminó el grupo de parlantes '{group.Name}'" +
                        (used > 0 ? $"; {used} acción(es) de automatizaciones lo nombraban y ya no harán sonar nada por él." : "."));
            await AnnounceAsync(ctx, service, ct);
            return Results.Ok();
        });
    }
}
