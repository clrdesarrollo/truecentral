using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Licensing;
using TrueCentralVms.Server.Services.Patrol;

namespace TrueCentralVms.Server.Api;

/// <summary>
/// API de las bases de descarga (agente en el PC de la guardia). No usa sesión
/// de usuario: la base se enrola una vez con un código del panel y desde ahí se
/// identifica con "Authorization: Agent &lt;token&gt;" (se guarda solo el SHA-256).
/// </summary>
public static class PatrolAgentApi
{
    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static async Task<PatrolAgent?> AuthenticateAsync(HttpContext ctx, VmsDbContext db, CancellationToken ct)
    {
        string? header = ctx.Request.Headers.Authorization;
        string prefix = PatrolAgentProtocol.AuthScheme + " ";
        if (header is null || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string token = header[prefix.Length..].Trim();
        if (token.Length < 32) return null;
        string hash = Hash(token);
        return await db.PatrolAgents.FirstOrDefaultAsync(a => a.TokenHash == hash, ct);
    }

    private static PatrolUploadSource SourceOf(HttpContext ctx, PatrolAgent agent, PatrolDownloadOrigin origin, long? commandId) =>
        new(origin, $"base '{agent.Name}'", agent.Id, null, ctx.Connection.RemoteIpAddress?.ToString(), commandId);

    public static void MapPatrolAgentApi(this WebApplication app)
    {
        app.MapPost("/api/patrol-agent/enroll", async (HttpContext ctx, PatrolAgentEnrollRequest request, VmsDbContext db,
            AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            string code = (request.Code ?? "").Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");
            string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
            var agent = code.Length == 0 ? null : await db.PatrolAgents.FirstOrDefaultAsync(a => a.EnrollmentCodeHash == Hash(code), ct);
            if (agent is null || agent.EnrollmentExpiresAt is null || agent.EnrollmentExpiresAt < DateTime.UtcNow)
            {
                await audit.LogSystemAsync("patrol", "agent-enroll-failed", "patrol-agent", agent?.Id.ToString(), agent?.Name,
                    $"Intento de enrolar una base desde {ip} ({request.MachineName}) con un código inválido o vencido.",
                    success: false, username: "base de descarga", clientIp: ip, origin: "agent");
                // Frena a quien pruebe códigos al azar.
                await Task.Delay(1500, ct);
                return Error("Código inválido o vencido. Genere uno nuevo en el panel (Rondas → Bases).", StatusCodes.Status403Forbidden);
            }

            string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            agent.TokenHash = Hash(token);
            agent.EnrollmentCodeHash = null;
            agent.EnrollmentExpiresAt = null;
            agent.MachineName = Trim(request.MachineName, 128);
            agent.Version = Trim(request.Version, 32);
            agent.RemoteAddress = Trim(ip, 64);
            agent.LastSeenAt = DateTime.UtcNow;
            agent.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.LogSystemAsync("patrol", "agent-enrolled", "patrol-agent", agent.Id.ToString(), agent.Name,
                $"Base '{agent.Name}' enrolada en el equipo {agent.MachineName} ({ip}).", username: $"base '{agent.Name}'",
                clientIp: ip, origin: "agent");
            await patrol.NotifyAsync("agents", ct);
            return Results.Ok(new PatrolAgentEnrollResponse(agent.Id, agent.Name, token));
        });

        // Latido: estado de los bastones que ve la base + hora del servidor + órdenes pendientes.
        app.MapPost("/api/patrol-agent/heartbeat", async (HttpContext ctx, PatrolAgentHeartbeat request, VmsDbContext db,
            AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            var agent = await AuthenticateAsync(ctx, db, ct);
            if (agent is null) return Results.Unauthorized();
            var now = DateTime.UtcNow;
            bool wasOffline = agent.LastSeenAt is null || now - agent.LastSeenAt > TimeSpan.FromSeconds(PatrolAgentProtocol.OfflineAfterSeconds);
            agent.LastSeenAt = now;
            agent.Version = Trim(request.Version, 32);
            agent.RemoteAddress = Trim(ctx.Connection.RemoteIpAddress?.ToString(), 64);
            agent.LastError = Trim(request.Error, 512);

            bool changed = wasOffline;
            var serials = request.Readers.Select(r => r.Serial).ToList();
            var known = await db.PatrolReaders.Where(r => serials.Contains(r.Serial) || r.AgentId == agent.Id).ToListAsync(ct);
            foreach (var reader in known)
            {
                var seen = request.Readers.FirstOrDefault(r => r.Serial == reader.Serial);
                bool docked = seen?.Docked == true;
                if (seen is null && reader.AgentId != agent.Id) continue;
                if (seen is not null) reader.AgentId = agent.Id;
                if (reader.Docked == docked) continue;
                reader.Docked = docked;
                reader.DockedChangedAt = now;
                reader.UpdatedAt = now;
                changed = true;
                await audit.LogSystemAsync("patrol", docked ? "reader-docked" : "reader-undocked",
                    "patrol-reader", reader.Id.ToString(), reader.Name,
                    docked ? $"'{reader.Name}' puesto en la base '{agent.Name}'." : $"'{reader.Name}' retirado de la base '{agent.Name}'.",
                    username: $"base '{agent.Name}'", origin: "agent");
            }

            var commands = await db.PatrolAgentCommands.Where(c => c.AgentId == agent.Id && c.CompletedAt == null)
                .OrderBy(c => c.Id).ToListAsync(ct);
            var readerSerials = await db.PatrolReaders.Where(r => commands.Select(c => c.ReaderId).Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => r.Serial, ct);
            foreach (var c in commands) c.SentAt ??= now;
            await db.SaveChangesAsync(ct);
            if (wasOffline)
                await audit.LogSystemAsync("patrol", "agent-online", "patrol-agent", agent.Id.ToString(), agent.Name,
                    $"Base '{agent.Name}' conectada ({agent.MachineName}).", username: $"base '{agent.Name}'", origin: "agent");
            if (changed) await patrol.NotifyAsync("readers", ct);

            var local = PatrolService.ToLocal(now);
            return Results.Ok(new PatrolAgentHeartbeatResponse(local, now,
                commands.Where(c => readerSerials.ContainsKey(c.ReaderId))
                    .Select(c => new PatrolAgentCommand(c.Id, readerSerials[c.ReaderId], c.Kind)).ToList()));
        });

        app.MapPost("/api/patrol-agent/uploads", async (HttpContext ctx, PatrolAgentUpload request, VmsDbContext db,
            LicenseService license, PatrolService patrol, CancellationToken ct) =>
        {
            var agent = await AuthenticateAsync(ctx, db, ct);
            if (agent is null) return Results.Unauthorized();
            if (!license.IsModuleEnabled(LicenseFeatures.ModulePatrol))
                return Error("La licencia no incluye el control de rondas: la base conserva las lecturas y no vacía el bastón.",
                    StatusCodes.Status402PaymentRequired);
            if (Validate(request.BlocksHex) is { } invalid) return Error(invalid);
            var origin = request.CommandId is null ? PatrolDownloadOrigin.Dock : PatrolDownloadOrigin.DockOnDemand;
            var result = await patrol.IngestAsync(request.Serial, request.BlocksHex, request.DeclaredBlocks, request.ClockSetAt,
                SourceOf(ctx, agent, origin, request.CommandId), ct);
            if (request.CommandId is { } commandId)
            {
                var command = await db.PatrolAgentCommands.FirstOrDefaultAsync(c => c.Id == commandId && c.AgentId == agent.Id, ct);
                // Una orden "read" no vacía, aunque el bastón esté aprobado.
                if (command is { Kind: "read" }) result = result with { EraseAllowed = false };
            }
            return Results.Ok(result);
        });

        app.MapPost("/api/patrol-agent/erase-report", async (HttpContext ctx, PatrolAgentEraseReport request, VmsDbContext db,
            PatrolService patrol, CancellationToken ct) =>
        {
            var agent = await AuthenticateAsync(ctx, db, ct);
            if (agent is null) return Results.Unauthorized();
            if (!await db.PatrolDownloads.AnyAsync(d => d.Id == request.DownloadId && d.AgentId == agent.Id, ct)) return Results.NotFound();
            await patrol.ReportEraseAsync(request.DownloadId, request.Erased, request.Error,
                SourceOf(ctx, agent, PatrolDownloadOrigin.Dock, null), ct);
            return Results.Ok();
        });

        app.MapPost("/api/patrol-agent/command-result", async (HttpContext ctx, PatrolAgentCommandResult request, VmsDbContext db,
            PatrolService patrol, CancellationToken ct) =>
        {
            var agent = await AuthenticateAsync(ctx, db, ct);
            if (agent is null) return Results.Unauthorized();
            var command = await db.PatrolAgentCommands.FirstOrDefaultAsync(c => c.Id == request.CommandId && c.AgentId == agent.Id, ct);
            if (command is null) return Results.NotFound();
            command.CompletedAt = DateTime.UtcNow;
            command.Success = request.Success;
            command.Message = Trim(request.Message, 512);
            await db.SaveChangesAsync(ct);
            await patrol.NotifyAsync("readers", ct);
            return Results.Ok();
        });

        app.MapPost("/api/patrol-agent/events", async (HttpContext ctx, PatrolAgentReaderEvent request, VmsDbContext db,
            AuditService audit, PatrolService patrol, CancellationToken ct) =>
        {
            var agent = await AuthenticateAsync(ctx, db, ct);
            if (agent is null) return Results.Unauthorized();
            var reader = await db.PatrolReaders.FirstOrDefaultAsync(r => r.Serial == request.Serial, ct);
            if (request.Event == "error")
            {
                if (reader is not null) { reader.LastError = Trim(request.Message, 512); await db.SaveChangesAsync(ct); }
                if (audit.ShouldLog($"patrol-reader-error:{agent.Id}:{request.Serial}:{request.Message}", TimeSpan.FromMinutes(10)))
                    await audit.LogSystemAsync("patrol", "reader-error", "patrol-reader", reader?.Id.ToString(),
                        reader?.Name ?? $"Bastón {request.Serial:D6}",
                        $"Base '{agent.Name}': {request.Message}", success: false, username: $"base '{agent.Name}'", origin: "agent");
                await patrol.NotifyAsync("readers", ct);
            }
            return Results.Ok();
        });
    }

    /// <summary>Tope generoso: la memoria llena son 12 000 bloques de 66 bytes.</summary>
    internal static string? Validate(IReadOnlyList<string>? blocksHex)
    {
        if (blocksHex is null) return "Faltan los bloques.";
        if (blocksHex.Count > PatrolService.MemoryBlocks + 100) return "Demasiados bloques.";
        foreach (var b in blocksHex)
            if (b.Length is 0 or > 1024 || b.Length % 2 != 0 || !b.All(Uri.IsHexDigit)) return "Bloque mal formado.";
        return null;
    }

    private static string? Trim(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
