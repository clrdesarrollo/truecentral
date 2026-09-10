using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TrueCentralVms.WebControl.Fingerprint;

namespace TrueCentralVms.WebControl;

/// <summary>Cuerpo de <c>POST /api/enroll/start</c>.</summary>
public sealed record StartEnrollRequest(string? ReaderId, int? CollectTimes, int? TimeoutSeconds);

/// <summary>
/// API local del web control: el panel web (servido por el servidor
/// TrueCentral) no puede tocar el hardware del equipo, así que llama por HTTP a
/// este proceso en 127.0.0.1. Hoy expone el lector de huellas USB; las
/// capacidades que se agreguen (decodificación de video, lectores de tarjeta…)
/// se anuncian en <c>/api/control/status</c> para que el panel las descubra.
///
/// Escucha solo en loopback: nada de la red puede alcanzarlo.
/// </summary>
public sealed class WebControlHost(WebControlSettings settings, FingerprintService fingerprint, Action<string> log)
{
    private WebApplication? _app;

    public int Port { get; private set; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task StartAsync()
    {
        Port = FirstFreePort(settings.Ports);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, Port));
        builder.Services.AddCors(o => o.AddDefaultPolicy(policy =>
        {
            // Sin credenciales: al panel le basta con que le dejen leer las
            // respuestas desde su propio origen.
            if (settings.AllowedOrigins.Contains("*")) policy.AllowAnyOrigin();
            else policy.WithOrigins(settings.AllowedOrigins);
            policy.AllowAnyHeader().AllowAnyMethod();
        }));

        var app = builder.Build();
        app.UseCors();
        MapEndpoints(app);

        await app.StartAsync();
        _app = app;
        log($"Web control escuchando en {BaseUrl}");
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        try { await _app.StopAsync(TimeSpan.FromSeconds(3)); } catch { }
        _app = null;
    }

    private void MapEndpoints(WebApplication app)
    {
        // Sonda que usa el panel para descubrir en qué puerto está el control y
        // qué sabe hacer esta versión (irán apareciendo más capacidades).
        app.MapGet("/api/control/status", () => Results.Ok(new
        {
            ok = true,
            product = "CLR TrueCentral VMS — Complemento de enrolamiento",
            version = Version,
            machine = Environment.MachineName,
            capabilities = new[] { "fingerprint" },
            fingerprint = new
            {
                sdkVersion = fingerprint.SdkVersion(),
                busy = fingerprint.Busy,
                defaults = new { collectTimes = settings.CollectTimes, timeoutSeconds = settings.TimeoutSeconds },
            },
        }));

        // Compatibilidad con la primera versión del control (panel viejo).
        app.MapGet("/api/enroll/status", () => Results.Ok(new
        {
            ok = true,
            product = "CLR TrueCentral VMS — Complemento de enrolamiento",
            version = Version,
            machine = Environment.MachineName,
            sdkVersion = fingerprint.SdkVersion(),
            busy = fingerprint.Busy,
            defaults = new { collectTimes = settings.CollectTimes, timeoutSeconds = settings.TimeoutSeconds },
        }));

        app.MapGet("/api/enroll/readers", () => Results.Ok(FingerprintReaders.Scan()));

        app.MapPost("/api/enroll/start", (StartEnrollRequest? request) =>
        {
            string readerId = string.IsNullOrWhiteSpace(request?.ReaderId)
                ? FingerprintReaders.AutoId
                : request!.ReaderId!;
            string? error = fingerprint.Start(
                readerId,
                request?.CollectTimes ?? settings.CollectTimes,
                request?.TimeoutSeconds ?? settings.TimeoutSeconds);

            if (error is not null) return Results.Conflict(new { error });
            log($"Captura iniciada (lector: {readerId}).");
            return Results.Ok(fingerprint.Snapshot());
        });

        app.MapGet("/api/enroll/session", () => Results.Ok(fingerprint.Snapshot()));

        // Vista previa de la última captura; el panel la pide por número de
        // secuencia para no traerse la imagen en cada sondeo.
        app.MapGet("/api/enroll/image", () => fingerprint.CurrentImage() is { } bmp
            ? Results.File(bmp, "image/bmp")
            : Results.NotFound());

        app.MapPost("/api/enroll/cancel", () =>
        {
            fingerprint.Cancel();
            return Results.Ok(fingerprint.Snapshot());
        });

        app.MapPost("/api/enroll/test", (StartEnrollRequest? request) =>
        {
            var (ok, message) = fingerprint.TestReader(
                string.IsNullOrWhiteSpace(request?.ReaderId) ? FingerprintReaders.AutoId : request!.ReaderId!);
            return ok ? Results.Ok(new { ok, message }) : Results.BadRequest(new { error = message });
        });
    }

    /// <summary>Primer puerto de la lista que nadie esté escuchando en loopback.</summary>
    private static int FirstFreePort(int[] candidates)
    {
        foreach (int port in candidates)
        {
            try
            {
                using var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (SocketException)
            {
                // Ocupado: probar el siguiente.
            }
        }
        throw new InvalidOperationException(
            $"Todos los puertos del agente están ocupados ({string.Join(", ", candidates)}). " +
            $"Ajuste \"Ports\" en {WebControlSettings.FilePath}.");
    }
}
