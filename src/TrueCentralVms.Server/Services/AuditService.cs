using System.Collections.Concurrent;
using System.Text.Json;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Escritor de la bitácora de auditoría. Singleton con su propio scope de base
/// por escritura: se puede llamar desde cualquier parte (handlers, servicios de
/// fondo) sin interferir con el DbContext de la operación en curso. Un fallo al
/// auditar NUNCA voltea la operación del usuario: se registra en el log del
/// servidor y la solicitud sigue su camino.
/// </summary>
public sealed class AuditService(IServiceScopeFactory scopeFactory, ILogger<AuditService> logger)
{
    /// <summary>Último registro por clave de acelerador (acciones repetitivas).</summary>
    private readonly ConcurrentDictionary<string, DateTime> _throttle = new();
    private DateTime _throttleSweptAt = DateTime.UtcNow;

    /// <summary>
    /// Registra una acción hecha a través de la API. El actor, la IP y el
    /// origen (panel web / cliente de escritorio) salen del propio request.
    /// </summary>
    public Task LogAsync(HttpContext ctx, string category, string action,
        string? targetType = null, string? targetId = null, string? targetName = null,
        string? detail = null, bool success = true, object? data = null)
    {
        var session = ApiSecurity.CurrentSession(ctx);
        return WriteAsync(new AuditEvent
        {
            UserId = session?.UserId,
            Username = session?.Username ?? "",
            Role = session?.Role,
            Origin = OriginOf(ctx),
            ClientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "",
            Category = category,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            TargetName = targetName,
            Detail = detail,
            Success = success,
            DataJson = ToJson(data),
        });
    }

    /// <summary>
    /// Variante para eventos sin sesión en el request (login fallido, callback
    /// de MediaMTX): el actor se pasa a mano.
    /// </summary>
    public Task LogAsAsync(HttpContext ctx, int? userId, string username, string? role,
        string category, string action,
        string? targetType = null, string? targetId = null, string? targetName = null,
        string? detail = null, bool success = true, object? data = null, string? clientIp = null)
        => WriteAsync(new AuditEvent
        {
            UserId = userId,
            Username = username,
            Role = role,
            Origin = OriginOf(ctx),
            ClientIp = clientIp ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "",
            Category = category,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            TargetName = targetName,
            Detail = detail,
            Success = success,
            DataJson = ToJson(data),
        });

    /// <summary>Eventos del propio servidor (arranque, cierres detectados, purgas)
    /// y del plano de media, donde el actor se conoce por la concesión y no por
    /// un request HTTP del usuario.</summary>
    public Task LogSystemAsync(string category, string action,
        string? targetType = null, string? targetId = null, string? targetName = null,
        string? detail = null, bool success = true, object? data = null,
        int? userId = null, string username = "sistema", string? clientIp = null,
        string origin = "server")
        => WriteAsync(new AuditEvent
        {
            UserId = userId,
            Username = username,
            Role = null,
            Origin = origin,
            ClientIp = clientIp ?? "",
            Category = category,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            TargetName = targetName,
            Detail = detail,
            Success = success,
            DataJson = ToJson(data),
        });

    /// <summary>
    /// true si corresponde registrar la acción; false si ya se registró una
    /// igual dentro de la ventana. Para acciones legítimas pero repetitivas
    /// (órdenes PTZ continuas, miniaturas, sondeos automáticos) donde una fila
    /// por gesto solo sería ruido: queda constancia de QUE se hizo y cuándo
    /// empezó, no de cada repetición.
    /// </summary>
    public bool ShouldLog(string throttleKey, TimeSpan window)
    {
        var now = DateTime.UtcNow;

        // Limpieza perezosa: las claves viejas no crecen sin límite.
        if (now - _throttleSweptAt > TimeSpan.FromMinutes(30))
        {
            _throttleSweptAt = now;
            foreach (var pair in _throttle.Where(p => now - p.Value > TimeSpan.FromHours(1)).ToList())
                _throttle.TryRemove(pair.Key, out _);
        }

        if (_throttle.TryGetValue(throttleKey, out var last) && now - last < window)
            return false;
        _throttle[throttleKey] = now;
        return true;
    }

    /// <summary>Origen del request: el cliente WPF se identifica con la
    /// cabecera X-TCVMS-Client; sin ella se asume el panel web.</summary>
    private static string OriginOf(HttpContext ctx) =>
        ctx.Request.Headers.ContainsKey("X-TCVMS-Client") ? "client" : "web";

    private static string? ToJson(object? data) =>
        data is null ? null : JsonSerializer.Serialize(data, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private async Task WriteAsync(AuditEvent entry)
    {
        try
        {
            // Truncado defensivo: la bitácora nunca debe fallar por un nombre
            // o detalle más largo que la columna.
            entry.Username = Truncate(entry.Username, 64)!;
            entry.Role = Truncate(entry.Role, 16);
            entry.Origin = Truncate(entry.Origin, 16)!;
            entry.ClientIp = Truncate(entry.ClientIp, 64)!;
            entry.Category = Truncate(entry.Category, 32)!;
            entry.Action = Truncate(entry.Action, 48)!;
            entry.TargetType = Truncate(entry.TargetType, 32);
            entry.TargetId = Truncate(entry.TargetId, 64);
            entry.TargetName = Truncate(entry.TargetName, 128);
            entry.Detail = Truncate(entry.Detail, 512);

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            db.AuditEvents.Add(entry);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo escribir el evento de auditoría {Category}/{Action} de '{User}'.",
                entry.Category, entry.Action, entry.Username);
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}
