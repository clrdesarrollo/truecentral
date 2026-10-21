using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Services;
using TrueCentralVms.Server.Services.Https;
using TrueCentralVms.Server.Services.Isup;
using TrueCentralVms.Server.Services.Network;

namespace TrueCentralVms.Server.Api;

/// <summary>Cambio de Sistema → Acceso desde Internet. Encenderlo exige <paramref name="Confirm"/> = "HABILITAR".</summary>
public sealed record WanWriteDto(bool Enabled, string? PublicHost, IReadOnlyList<string>? LocalNetworks,
    IReadOnlyList<string>? Scenarios, IReadOnlyDictionary<string, int>? PublicPorts, string? Confirm = null);

/// <summary>
/// Sistema → Acceso desde Internet: dirección pública, redes locales, escenarios
/// y la tabla de puertos para el técnico que configura el router.
/// </summary>
public static partial class WanApi
{
    [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostName();

    public static void MapWanApi(this WebApplication app)
    {
        app.MapGet("/api/system/wan", (HttpContext ctx, WanAccessService wan, ServerCertificateManager certificates) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemNetwork, out _) is { } failure) return failure;
            return Results.Ok(Describe(ctx, wan, certificates));
        });

        app.MapPut("/api/system/wan", async (HttpContext ctx, WanWriteDto request, WanAccessService wan,
            ServerCertificateManager certificates, MediaMtxManager mtx, IsupHostManager isup, AuditService audit, CancellationToken ct) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemNetwork, out var session) is { } failure) return failure;
            var before = wan.Current;

            string host = (request.PublicHost ?? "").Trim().TrimEnd('.');
            if (host.Contains("://") || host.Contains('/') || host.Contains(':') && !IPAddress.TryParse(host, out _))
                return Error("Escriba solo la IP pública o el nombre (sin http://, sin puerto ni rutas).");
            if (host.Length > 0 && !IPAddress.TryParse(host, out _) && !HostName().IsMatch(host))
                return Error("La dirección pública no es una IP ni un nombre válido.");
            if (request.Enabled && host.Length == 0)
                return Error("Indique la dirección pública (IP fija o nombre DNS) antes de encender el acceso.");

            var networks = (request.LocalNetworks ?? []).Select(n => n.Trim()).Where(n => n.Length > 0).Distinct().ToList();
            foreach (string n in networks)
                if (WanAccessService.ParseCidr(n) is null) return Error($"«{n}» no es una red válida (ejemplo: 192.168.0.0/16).");
            if (networks.Count == 0) return Error("Indique al menos una red local.");

            var known = WanAccessService.Scenarios.Select(s => s.Key).ToHashSet();
            var scenarios = (request.Scenarios ?? []).Where(known.Contains).Distinct().ToList();
            if (request.Enabled && scenarios.Count == 0) return Error("Elija al menos un escenario.");

            var ports = wan.Ports();
            var publicPorts = new Dictionary<string, int>();
            foreach (var (key, value) in request.PublicPorts ?? new Dictionary<string, int>())
            {
                var port = ports.FirstOrDefault(p => p.Key == key);
                if (port is null || port.FixedPublic || value == port.Internal) continue;
                if (value is < 1 or > 65535) return Error($"El puerto público de «{port.Name}» debe estar entre 1 y 65535.");
                publicPorts[key] = value;
            }

            bool turningOn = request.Enabled && !before.Enabled;
            if (turningOn && request.Confirm != "HABILITAR")
                return Error("Para encender el acceso desde Internet escriba HABILITAR (confirma que entiende el riesgo).");

            var settings = new WanSettings
            {
                Enabled = request.Enabled,
                PublicHost = host,
                LocalNetworks = networks,
                Scenarios = scenarios,
                PublicPorts = publicPorts,
                UpdatedUtc = DateTime.UtcNow,
                UpdatedBy = session.Username,
            };
            await wan.SaveAsync(settings, ct);

            // Lo que depende de la dirección pública: certificado propio, candidato WebRTC y lo que anuncia el ISUP.
            certificates.RefreshSelfSigned();
            await mtx.RefreshPathsAsync(ct);
            bool isupChanged = (before.Enabled && before.Scenarios.Contains(WanAccessService.ScenarioIsup) ? before.PublicHost : "")
                               != (settings.Enabled && settings.Scenarios.Contains(WanAccessService.ScenarioIsup) ? settings.PublicHost : "");
            if (isupChanged) await isup.RestartAsync(ct);

            string action = turningOn ? "wan-enabled" : before.Enabled && !request.Enabled ? "wan-disabled" : "wan-updated";
            var names = WanAccessService.Scenarios.Where(s => scenarios.Contains(s.Key)).Select(s => s.Name);
            await audit.LogAsync(ctx, "system", action, targetType: "wan", targetName: host,
                detail: $"{(settings.Enabled ? "Acceso desde Internet ENCENDIDO" : "Acceso desde Internet apagado")}: dirección pública " +
                        $"{(host.Length > 0 ? host : "(sin definir)")}; redes locales {string.Join(", ", networks)}; escenarios " +
                        $"{(scenarios.Count > 0 ? string.Join(", ", names) : "ninguno")}" +
                        (publicPorts.Count > 0 ? $"; puertos públicos distintos: {string.Join(", ", publicPorts.Select(p => $"{p.Key}={p.Value}"))}" : "") +
                        (isupChanged ? "; se reinició el servicio ISUP." : "."),
                data: settings);
            return Results.Ok(Describe(ctx, wan, certificates));
        });

        // Hoja para el técnico de red: qué reenviar en el router.
        app.MapGet("/api/system/wan/ports.csv", async (HttpContext ctx, WanAccessService wan, AuditService audit) =>
        {
            if (ApiSecurity.Require(ctx, Permissions.SystemNetwork, out _) is { } failure) return failure;
            var s = wan.Current;
            var csv = new StringBuilder("Escenario;Puerto;Protocolo;Puerto público (router);Reenviar a (servidor)\r\n");
            foreach (var p in wan.Ports().Where(p => p.Scenarios.Any(s.Scenarios.Contains)))
            {
                string scenarios = string.Join(" / ", WanAccessService.Scenarios.Where(x => p.Scenarios.Contains(x.Key) && s.Scenarios.Contains(x.Key))
                    .Select(x => x.Name));
                csv.Append($"{scenarios};{p.Name};{p.Protocol};{wan.PublicPort(p.Key)};{p.Internal}\r\n");
            }
            await audit.LogAsync(ctx, "system", "wan-ports-exported", targetType: "wan", targetName: s.PublicHost,
                detail: "Exportó la tabla de puertos del acceso desde Internet.");
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(),
                "text/csv", "truecentral-puertos-router.csv");
        });
    }

    private static object Describe(HttpContext ctx, WanAccessService wan, ServerCertificateManager certificates)
    {
        var s = wan.Current;
        var cert = certificates.Current;
        bool certCovers = s.PublicHost.Length == 0 || ServerCertificateManager.SubjectNames(cert)
            .Any(n => n.Equals(s.PublicHost, StringComparison.OrdinalIgnoreCase)
                      || n.StartsWith("*.", StringComparison.Ordinal) && s.PublicHost.EndsWith(n[1..], StringComparison.OrdinalIgnoreCase));
        var remote = ctx.Connection.RemoteIpAddress;
        return new
        {
            enabled = s.Enabled,
            publicHost = s.PublicHost,
            localNetworks = s.LocalNetworks,
            scenarios = s.Scenarios,
            updatedUtc = s.UpdatedUtc,
            updatedBy = s.UpdatedBy,
            catalog = WanAccessService.Scenarios.Select(x => new { key = x.Key, name = x.Name, description = x.Description }),
            ports = wan.Ports().Select(p => new
            {
                key = p.Key, name = p.Name, protocol = p.Protocol, @internal = p.Internal, fixedPublic = p.FixedPublic,
                scenarios = p.Scenarios,
                publicPort = s.PublicPorts.TryGetValue(p.Key, out int pp) && !p.FixedPublic ? pp : p.Internal,
            }),
            defaultLocalNetworks = WanAccessService.DefaultLocalNetworks,
            certificate = new
            {
                custom = certificates.IsCustom,
                coversPublicHost = certCovers,
            },
            you = new
            {
                ip = remote is null ? null : (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString(),
                remote = wan.IsRemote(remote),
            },
        };
    }

    private static IResult Error(string message) =>
        Results.Json(new { error = message }, statusCode: StatusCodes.Status400BadRequest);
}
