using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace TrueCentralVms.Server.Auth;

/// <summary>Para qué sirve un ticket de URL (ver <see cref="UrlTicketService"/>).</summary>
[Flags]
public enum UrlTicketUse
{
    /// <summary>Imágenes y audio que el panel pone en un &lt;img&gt; o un &lt;audio&gt;: varias lecturas durante su minuto.</summary>
    Media = 1,
    /// <summary>Descargas y exportaciones: una sola solicitud, a la dirección exacta para la que se pidió.</summary>
    Download = 2,
}

/// <summary>Marca de un endpoint que acepta tickets de URL (<see cref="UrlTicketEndpointExtensions.AllowUrlTicket{TBuilder}"/>).</summary>
public sealed record UrlTicketEndpoint(UrlTicketUse Use);

public static class UrlTicketEndpointExtensions
{
    /// <summary>El endpoint (solo GET) acepta un ticket de URL de ese uso en vez de la cabecera Authorization.</summary>
    public static TBuilder AllowUrlTicket<TBuilder>(this TBuilder builder, UrlTicketUse use) where TBuilder : IEndpointConventionBuilder
        => builder.WithMetadata(new UrlTicketEndpoint(use));
}

/// <summary>Pedido de <c>POST /api/auth/ticket</c>: purpose "media", o "download" con la dirección (ruta y parámetros) a bajar.</summary>
public sealed record UrlTicketRequest(string? Purpose, string? Url);

/// <summary>
/// Tickets de URL: la credencial de lo que el navegador pide sin poder mandar la
/// cabecera Authorization (un &lt;img&gt;, un &lt;audio&gt;, una descarga). Antes viajaba el
/// token de la sesión en la dirección (?access_token=) y quedaba en el historial,
/// en la lista de descargas y en cualquier enlace copiado, con todo el poder de
/// la sesión durante 12 h. Un ticket es opaco, dura un minuto, sirve solo para su
/// uso en los endpoints marcados con <see cref="UrlTicketEndpointExtensions.AllowUrlTicket{TBuilder}"/>
/// y muere con la sesión que lo pidió. Viven en memoria, como las sesiones (y
/// como los tokens de streaming): un reinicio los borra junto con ellas.
/// </summary>
public sealed class UrlTicketService
{
    /// <summary>Parámetro de la dirección que lleva el ticket.</summary>
    public const string QueryKey = "ticket";

    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Un ticket vencido o usado se recuerda un rato más: así el rechazo dice
    /// «venció» (lo normal con una imagen que tardó en cargar, no va a la
    /// bitácora) o «ya se usó» en vez de «no existe».
    /// </summary>
    private static readonly TimeSpan Remembered = TimeSpan.FromMinutes(10);

    /// <summary>Tickets vigentes por sesión: el panel pide uno de imágenes cada medio minuto por pestaña, más las descargas.</summary>
    private const int MaxLivePerSession = 64;

    private sealed record Ticket(string SessionToken, SessionInfo Owner, UrlTicketUse Use, string? Target,
        DateTime ExpiresAt, bool Used = false);

    private readonly ConcurrentDictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);

    /// <summary>
    /// Emite un ticket para la sesión. <paramref name="target"/> (solo descargas)
    /// es la dirección normalizada con <see cref="CanonicalTarget"/>. Null si la
    /// sesión ya tiene demasiados tickets vigentes.
    /// </summary>
    public string? Issue(string sessionToken, SessionInfo owner, UrlTicketUse use, string? target = null)
    {
        Prune();
        var now = DateTime.UtcNow;
        if (_tickets.Values.Count(t => t.SessionToken == sessionToken && !t.Used && t.ExpiresAt >= now) >= MaxLivePerSession)
            return null;
        string ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _tickets[ticket] = new Ticket(sessionToken, owner, use, target, now.Add(Lifetime));
        return ticket;
    }

    /// <summary>Resultado de presentar un ticket: el token de la sesión si vale; si no, el motivo del rechazo.</summary>
    /// <param name="Owner">Quién lo pidió (también si se rechaza, para la bitácora); null si no existe.</param>
    /// <param name="Suspicious">El rechazo no es parte del uso normal (desconocido, reusado, otra dirección): va a la bitácora.</param>
    public readonly record struct Redemption(string? SessionToken, SessionInfo? Owner, string? Rejection, bool Suspicious);

    /// <summary>
    /// Canjea el ticket de la solicitud. <paramref name="allowed"/> es lo que
    /// acepta el endpoint (null = ninguno). Una descarga se gasta al presentarse,
    /// valga o no: una segunda solicitud con el mismo ticket se rechaza.
    /// </summary>
    public Redemption Redeem(string ticket, UrlTicketUse? allowed, HttpRequest request)
    {
        if (!_tickets.TryGetValue(ticket, out var t))
            return new(null, null, "no existe (pudo vencer hace rato o ser inventado)", true);
        if (t.Used)
            return new(null, t.Owner, "ya se usó: un ticket de descarga sirve una sola vez", true);
        if (t.Use == UrlTicketUse.Download && !_tickets.TryUpdate(ticket, t with { Used = true }, t))
            return new(null, t.Owner, "ya se usó: un ticket de descarga sirve una sola vez", true);
        if (t.ExpiresAt < DateTime.UtcNow)
            return new(null, t.Owner, "venció", false);
        bool readOnly = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
        if (!readOnly || allowed is not { } accepted || (accepted & t.Use) == 0)
            return new(null, t.Owner, t.Use == UrlTicketUse.Media
                ? "es para ver imágenes y audio, no para esta dirección"
                : "es para una descarga, no para esta dirección", true);
        if (t.Use == UrlTicketUse.Download && t.Target != Canonical(request.Path, request.Query))
            return new(null, t.Owner, "es para otra descarga (la dirección no coincide)", true);
        return new(t.SessionToken, t.Owner, null, false);
    }

    /// <summary>
    /// La dirección de una descarga normalizada para compararla con la solicitud
    /// que llega: ruta decodificada y parámetros decodificados y ordenados, sin el
    /// ticket. Así no importa cómo codifique el navegador un apóstrofo o un
    /// espacio. Null si no es una dirección local (/api/...).
    /// </summary>
    public static string? CanonicalTarget(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith('/') || url.StartsWith("//") || url.Contains('\\')) return null;
        int hash = url.IndexOf('#');
        if (hash >= 0) url = url[..hash];
        int q = url.IndexOf('?');
        var path = PathString.FromUriComponent(q < 0 ? url : url[..q]);
        var query = q < 0 ? new Dictionary<string, StringValues>() : QueryHelpers.ParseQuery(url[q..]);
        return Canonical(path, query);
    }

    private static string Canonical(PathString path, IEnumerable<KeyValuePair<string, StringValues>> query) =>
        path.Value + "?" + string.Join("&", query
            .Where(p => !string.Equals(p.Key, QueryKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(p => p.Value.Select(v => $"{Uri.EscapeDataString(p.Key.ToLowerInvariant())}={Uri.EscapeDataString(v ?? "")}")));

    private void Prune()
    {
        var limit = DateTime.UtcNow - Remembered;
        foreach (var pair in _tickets)
            if (pair.Value.ExpiresAt < limit)
                _tickets.TryRemove(pair.Key, out _);
    }
}
