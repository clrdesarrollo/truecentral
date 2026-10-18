using System.Collections.Concurrent;
using System.Security.Cryptography;
using TrueCentralVms.SpeakerAgent.Audio;

namespace TrueCentralVms.SpeakerAgent;

/// <summary>
/// Página local de puesta en marcha (http://equipo:8088): servidor y código de enrolamiento, salida de audio,
/// volumen y tono de prueba. No es otro VMS: lo demás se administra desde TrueCentral. La primera visita define la
/// clave de la página; sin ella, nadie en la red puede enrolar el equipo en otro servidor.
/// </summary>
public static class LocalWeb
{
    private const string Cookie = "tcs_session";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);
    private static readonly ConcurrentDictionary<string, DateTime> Sessions = new();

    private sealed record PasswordRequest(string? Password);
    private sealed record EnrollRequest(string? Server, string? Code);
    private sealed record VolumeRequest(int Volume);
    private sealed record DeviceRequest(string? Device);

    private static bool Authenticated(HttpContext ctx) =>
        ctx.Request.Cookies.TryGetValue(Cookie, out var id) && id is not null
        && Sessions.TryGetValue(id, out var expires) && expires > DateTime.UtcNow;

    private static void StartSession(HttpContext ctx)
    {
        string id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        Sessions[id] = DateTime.UtcNow + SessionLifetime;
        foreach (var old in Sessions.Where(s => s.Value < DateTime.UtcNow).Select(s => s.Key).ToList()) Sessions.TryRemove(old, out _);
        ctx.Response.Cookies.Append(Cookie, id, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, MaxAge = SessionLifetime });
    }

    private static IResult Fail(string message, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new { error = message }, statusCode: status);

    public static void MapLocalWeb(this WebApplication app)
    {
        app.MapGet("/", () => Results.Content(LocalPage.Html, "text/html; charset=utf-8"));

        app.MapGet("/api/status", (HttpContext ctx, AgentStore store, AgentConnection connection, AudioEngine audio) =>
        {
            var state = store.Snapshot;
            bool auth = Authenticated(ctx);
            return Results.Ok(new
            {
                needsPassword = !store.HasLocalPassword,
                authenticated = auth,
                version = DeviceIdentity.Version,
                hardware = DeviceIdentity.Hardware,
                hostname = Environment.MachineName,
                // Lo demás solo con sesión: la dirección del servidor y el estado no son para cualquiera en la red.
                enrolled = auth ? store.IsEnrolled : (bool?)null,
                server = auth ? state.ServerUrl : null,
                speakerName = auth ? state.SpeakerName : null,
                connected = auth ? connection.Connected : (bool?)null,
                connectedSince = auth ? connection.ConnectedSince : null,
                lastError = auth ? connection.LastError : null,
                volume = auth ? audio.Volume : (int?)null,
                outputDevice = auth ? state.OutputDevice ?? "" : null,
                outputError = auth ? audio.OutputError : null,
                playing = auth ? audio.CurrentName : null,
                agentId = auth ? state.AgentId : null,
            });
        });

        app.MapPost("/api/password", (HttpContext ctx, PasswordRequest request, AgentStore store) =>
        {
            // Solo la primera vez, o con sesión para cambiarla.
            if (store.HasLocalPassword && !Authenticated(ctx)) return Fail("Inicie sesión para cambiar la clave.", StatusCodes.Status401Unauthorized);
            if (request.Password is not { Length: >= 8 }) return Fail("La clave debe tener al menos 8 caracteres.");
            store.SetLocalPassword(request.Password);
            StartSession(ctx);
            return Results.Ok();
        });

        app.MapPost("/api/login", async (HttpContext ctx, PasswordRequest request, AgentStore store) =>
        {
            if (!store.HasLocalPassword) return Fail("Aún no hay clave: defínala primero.");
            if (string.IsNullOrEmpty(request.Password) || !store.CheckLocalPassword(request.Password))
            {
                await Task.Delay(1000);   // frena los intentos a ciegas
                return Fail("Clave incorrecta.", StatusCodes.Status401Unauthorized);
            }
            StartSession(ctx);
            return Results.Ok();
        });

        app.MapPost("/api/logout", (HttpContext ctx) =>
        {
            if (ctx.Request.Cookies.TryGetValue(Cookie, out var id) && id is not null) Sessions.TryRemove(id, out _);
            ctx.Response.Cookies.Delete(Cookie);
            return Results.Ok();
        });

        app.MapPost("/api/enroll", async (HttpContext ctx, EnrollRequest request, AgentStore store, AgentConnection connection) =>
        {
            if (!Authenticated(ctx)) return Fail("Inicie sesión.", StatusCodes.Status401Unauthorized);
            if (string.IsNullOrWhiteSpace(request.Server) || string.IsNullOrWhiteSpace(request.Code))
                return Fail("Indique el servidor y el código de enrolamiento.");
            try
            {
                string name = await AgentConnection.EnrollAsync(store, request.Server, request.Code, ctx.RequestAborted);
                connection.Restart();
                return Results.Ok(new { message = $"Equipo enrolado como «{name}». Conectando…" });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return Fail(ex.Message);
            }
        });

        app.MapPost("/api/forget", (HttpContext ctx, AgentStore store, AgentConnection connection) =>
        {
            if (!Authenticated(ctx)) return Fail("Inicie sesión.", StatusCodes.Status401Unauthorized);
            store.Update(s => { s.ProtectedToken = null; s.PinnedCertificate = null; s.SpeakerId = null; s.SpeakerName = null; s.EnrolledAt = null; });
            connection.Restart();
            return Results.Ok();
        });

        app.MapPost("/api/volume", (HttpContext ctx, VolumeRequest request, AudioEngine audio) =>
        {
            if (!Authenticated(ctx)) return Fail("Inicie sesión.", StatusCodes.Status401Unauthorized);
            audio.SetVolume(request.Volume, fromServer: false);
            return Results.Ok(new { volume = audio.Volume });
        });

        app.MapGet("/api/devices", (HttpContext ctx) =>
            !Authenticated(ctx) ? Fail("Inicie sesión.", StatusCodes.Status401Unauthorized) : Results.Ok(AudioOutputs.List()));

        app.MapPost("/api/device", (HttpContext ctx, DeviceRequest request, AgentStore store) =>
        {
            if (!Authenticated(ctx)) return Fail("Inicie sesión.", StatusCodes.Status401Unauthorized);
            store.Update(s => s.OutputDevice = string.IsNullOrWhiteSpace(request.Device) ? null : request.Device.Trim());
            return Results.Ok();
        });

        app.MapPost("/api/test-tone", (HttpContext ctx, AudioEngine audio) =>
        {
            if (!Authenticated(ctx)) return Fail("Inicie sesión.", StatusCodes.Status401Unauthorized);
            try { audio.PlayTone(3); return Results.Ok(); }
            catch (InvalidOperationException ex) { return Fail(ex.Message, StatusCodes.Status409Conflict); }
        });
    }
}
