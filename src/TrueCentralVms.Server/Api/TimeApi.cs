using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Time;

namespace TrueCentralVms.Server.Api;

/// <summary>Cambio de Sistema → Red → Hora (NTP).</summary>
public sealed record TimeWriteDto(IReadOnlyList<string>? Sources, bool AdjustSystemClock, bool ServerEnabled,
    bool ServeWhenUnsynchronized, int PollMinutes);

/// <summary>
/// Sistema → Red → Hora (NTP): de dónde saca la hora el servidor, si corrige
/// el reloj de Windows, el servidor NTP para los equipos y la zona horaria con
/// sus cambios de hora.
/// </summary>
public static partial class TimeApi
{
    [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostName();

    private const int MaxSources = 8;

    public static void MapTimeApi(this WebApplication app)
    {
        app.MapGet("/api/system/time", async (HttpContext ctx, TimeService time, VmsDbContext db, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemNetwork, out _) is { } failure) return failure;
            return Results.Ok(await DescribeAsync(ctx, time, db, ct));
        });

        app.MapPut("/api/system/time", async (HttpContext ctx, TimeWriteDto request, TimeService time, VmsDbContext db,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemNetwork, out var session) is { } failure) return failure;
            var sources = new List<string>();
            foreach (string raw in request.Sources ?? [])
            {
                string host = raw.Trim().TrimEnd('.');
                if (host.Length == 0) continue;
                if (host.Contains("://") || host.Contains('/') || host.Contains(':') && !IPAddress.TryParse(host, out _))
                    return Error($"«{host}»: escriba solo el nombre o la IP del servidor NTP (sin ntp://, puerto ni rutas).");
                if (!IPAddress.TryParse(host, out _) && !HostName().IsMatch(host))
                    return Error($"«{host}» no es una IP ni un nombre válido.");
                if (!sources.Contains(host, StringComparer.OrdinalIgnoreCase)) sources.Add(host);
            }
            if (sources.Count > MaxSources) return Error($"Use a lo más {MaxSources} fuentes.");
            if (request.PollMinutes is < 1 or > 1440) return Error("La consulta va de cada 1 minuto a cada 24 horas.");

            var before = time.Current;
            var settings = new TimeSettings
            {
                Sources = sources,
                AdjustSystemClock = request.AdjustSystemClock,
                ServerEnabled = request.ServerEnabled,
                ServeWhenUnsynchronized = request.ServeWhenUnsynchronized,
                PollMinutes = request.PollMinutes,
                UpdatedUtc = DateTime.UtcNow,
                UpdatedBy = session.Username,
            };
            await time.SaveAsync(settings, ct);
            await audit.LogAsync(ctx, "time", "time-settings-changed", targetType: "time", targetName: "Hora oficial",
                detail: $"Cambió los ajustes de la hora oficial: {Diff(before, settings)}.", data: settings);
            return Results.Ok(await DescribeAsync(ctx, time, db, ct));
        });

        // Consultar las fuentes ya, sin esperar la vuelta.
        app.MapPost("/api/system/time/sync", async (HttpContext ctx, TimeService time, VmsDbContext db, AuditService audit,
            CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemNetwork, out _) is { } failure) return failure;
            await time.SyncNowAsync(ct);
            var settings = time.Current;
            await audit.LogAsync(ctx, "time", "time-sync-requested", targetType: "time", targetName: "Hora oficial",
                detail: settings.Sources.Count == 0
                    ? "Pidió consultar la hora oficial, pero no hay fuentes configuradas (red aislada)."
                    : $"Consultó la hora oficial ({string.Join(", ", settings.Sources)}).");
            return Results.Ok(await DescribeAsync(ctx, time, db, ct));
        });

        // Liberar el puerto 123 y dejar un solo programa corrigiendo el reloj.
        app.MapPost("/api/system/time/windows-time/disable", async (HttpContext ctx, TimeService time, VmsDbContext db,
            AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemNetwork, out _) is { } failure) return failure;
            string? error = await time.DisableWindowsTimeAsync(ct);
            await audit.LogAsync(ctx, "time", "time-windows-time-disabled", targetType: "time", targetName: TimeService.WindowsTimeName,
                detail: error is null
                    ? "Detuvo y deshabilitó el servicio Hora de Windows (W32Time): la hora la maneja TrueCentral."
                    : $"No se pudo detener el servicio Hora de Windows: {error}.",
                success: error is null);
            if (error is not null) return Error($"No se pudo: {error}.");
            await Task.Delay(500, ct);   // que el servidor NTP alcance a tomar el puerto
            return Results.Ok(await DescribeAsync(ctx, time, db, ct));
        });
    }

    private static async Task<object> DescribeAsync(HttpContext ctx, TimeService time, VmsDbContext db, CancellationToken ct)
    {
        var settings = time.Current;
        var policy = await DeviceClockService.LoadPolicyAsync(db, ct);
        var zone = policy.Zone();
        var serverUtc = time.UtcNow;
        var names = await DeviceNamesAsync(db, ct);
        var clients = time.Clients().Select(c => new
        {
            address = c.Address,
            device = names.GetValueOrDefault(c.Address),
            firstUtc = c.FirstUtc,
            lastUtc = c.LastUtc,
            requests = c.Requests,
        }).ToList();

        // La dirección que hay que darle a los equipos: la de quien mira la
        // página suele estar en la misma red que ellos.
        // Desde el mismo servidor, la de la ruta por omisión.
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
        string? caller = TimeService.LocalAddressToward(remote is not null && !IPAddress.IsLoopback(remote) ? remote.ToString() : "192.0.2.1");


        return new
        {
            settings = new
            {
                sources = settings.Sources,
                adjustSystemClock = settings.AdjustSystemClock,
                serverEnabled = settings.ServerEnabled,
                serveWhenUnsynchronized = settings.ServeWhenUnsynchronized,
                pollMinutes = settings.PollMinutes,
                updatedUtc = settings.UpdatedUtc,
                updatedBy = settings.UpdatedBy,
            },
            defaultSources = TimeSettings.DefaultSources,
            status = time.Status(),
            serverAddress = caller,
            clients,
            zone = new
            {
                id = zone.Id,
                name = zone.DisplayName,
                description = DeviceTimeZones.Describe(zone),
                fromPolicy = !string.IsNullOrWhiteSpace(policy.TimeZoneId),
                utcOffsetMinutes = (int)zone.GetUtcOffset(serverUtc).TotalMinutes,
                offsetLabel = DeviceTimeZones.OffsetLabel(zone.GetUtcOffset(serverUtc)),
                daylightNow = zone.IsDaylightSavingTime(serverUtc),
                localTime = TimeZoneInfo.ConvertTimeFromUtc(serverUtc, zone).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                transitions = Transitions(zone, serverUtc, 2),
                windowsId = TimeZoneInfo.Local.Id,
                windowsName = TimeZoneInfo.Local.DisplayName,
                sameAsWindows = zone.Id == TimeZoneInfo.Local.Id,
            },
            devices = new
            {
                mode = policy.Mode,
                ntpFromServer = policy.NtpFromServer,
                ntpServer = policy.NtpServer,
                autoCorrect = policy.AutoCorrect,
            },
        };
    }

    /// <summary>IP → nombre de los equipos conocidos, para decir quién pide la hora.</summary>
    private static async Task<Dictionary<string, string>> DeviceNamesAsync(VmsDbContext db, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(IEnumerable<(string Host, string Name)> rows)
        {
            foreach (var (host, name) in rows)
                if (!string.IsNullOrWhiteSpace(host)) map.TryAdd(host.Trim(), name);
        }
        Add((await db.Devices.AsNoTracking().Select(d => new { d.Host, d.Name }).ToListAsync(ct)).Select(d => (d.Host, d.Name)));
        Add((await db.AccessDevices.AsNoTracking().Select(d => new { d.Host, d.Name }).ToListAsync(ct)).Select(d => (d.Host, d.Name)));
        Add((await db.Intercoms.AsNoTracking().Select(d => new { d.Host, d.Name }).ToListAsync(ct)).Select(d => (d.Host, d.Name)));
        Add((await db.AlarmPanels.AsNoTracking().Select(d => new { d.Host, d.Name }).ToListAsync(ct)).Select(d => (d.Host, d.Name)));
        Add((await db.Speakers.AsNoTracking().Select(d => new { d.Host, d.Name }).ToListAsync(ct)).Select(d => (d.Host, d.Name)));
        Add((await db.IoModules.AsNoTracking().Select(d => new { d.Host, d.Name }).ToListAsync(ct)).Select(d => (d.Host, d.Name)));
        Add((await db.Decoders.AsNoTracking().Select(d => new { d.Host, d.Name }).ToListAsync(ct)).Select(d => (d.Host, d.Name)));
        return map;
    }

    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-CL");

    /// <summary>
    /// Los próximos cambios de hora de la zona: cuándo (en UTC) y cómo se ve en
    /// los relojes. Se buscan día a día y se afinan al minuto, así que valen
    /// para cualquier regla de Windows (incluidas las de fecha fija).
    /// </summary>
    private static List<object> Transitions(TimeZoneInfo zone, DateTime fromUtc, int count)
    {
        var list = new List<object>();
        if (!zone.SupportsDaylightSavingTime) return list;
        var offset = zone.GetUtcOffset(fromUtc);
        for (int day = 1; day <= 800 && list.Count < count; day++)
        {
            var next = fromUtc.AddDays(day);
            var nextOffset = zone.GetUtcOffset(next);
            if (nextOffset == offset) continue;

            DateTime lo = next.AddDays(-1), hi = next;
            while (hi - lo > TimeSpan.FromMinutes(1))
            {
                var mid = lo + (hi - lo) / 2;
                if (zone.GetUtcOffset(mid) == offset) lo = mid; else hi = mid;
            }
            var at = new DateTime(hi.Ticks - hi.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
            if (zone.GetUtcOffset(at) != nextOffset) at = at.AddMinutes(1);

            var before = at + offset;
            var after = at + nextOffset;
            bool forward = nextOffset > offset;
            var delta = (nextOffset - offset).Duration();
            string amount = delta.TotalMinutes % 60 == 0 ? $"{delta.TotalHours:0} hora{(delta.TotalHours == 1 ? "" : "s")}" : $"{delta.TotalMinutes:0} minutos";
            list.Add(new
            {
                utc = at,
                forward,
                fromOffset = DeviceTimeZones.OffsetLabel(offset),
                toOffset = DeviceTimeZones.OffsetLabel(nextOffset),
                text = $"{When(before)} los relojes {(forward ? "se adelantan" : "se atrasan")} {amount}: " +
                       $"pasan a marcar las {after:HH:mm} ({DeviceTimeZones.OffsetLabel(nextOffset)}).",
            });
            offset = nextOffset;
        }
        return list;
    }

    /// <summary>"El domingo 4 de abril de 2027 a las 03:00", o a medianoche "En la medianoche del sábado 3 al domingo 4 de abril de 2027".</summary>
    private static string When(DateTime local)
    {
        string day = local.ToString("dddd d 'de' MMMM 'de' yyyy", Spanish);
        if (local.TimeOfDay != TimeSpan.Zero) return $"El {day}, a las {local:HH:mm},";
        string eve = local.AddDays(-1).ToString(local.AddDays(-1).Month == local.Month ? "dddd d" : "dddd d 'de' MMMM", Spanish);
        return $"En la medianoche del {eve} al {day}";
    }

    private static string Diff(TimeSettings a, TimeSettings b)
    {
        var changes = new List<string>();
        static string List(List<string> s) => s.Count == 0 ? "ninguna (red aislada)" : string.Join(", ", s);
        if (!a.Sources.SequenceEqual(b.Sources, StringComparer.OrdinalIgnoreCase))
            changes.Add($"fuentes {List(a.Sources)} → {List(b.Sources)}");
        if (a.AdjustSystemClock != b.AdjustSystemClock)
            changes.Add(b.AdjustSystemClock ? "corrige el reloj del servidor" : "ya no corrige el reloj del servidor");
        if (a.ServerEnabled != b.ServerEnabled)
            changes.Add(b.ServerEnabled ? "servidor NTP para los equipos encendido" : "servidor NTP para los equipos APAGADO");
        if (a.ServeWhenUnsynchronized != b.ServeWhenUnsynchronized)
            changes.Add(b.ServeWhenUnsynchronized ? "da la hora aunque no haya fuente" : "sin fuente no da la hora");
        if (a.PollMinutes != b.PollMinutes) changes.Add($"consulta cada {a.PollMinutes} min → {b.PollMinutes} min");
        return changes.Count > 0 ? string.Join(", ", changes) : "sin cambios";
    }

    private static IResult Error(string message) =>
        Results.Json(new { error = message }, statusCode: StatusCodes.Status400BadRequest);
}
