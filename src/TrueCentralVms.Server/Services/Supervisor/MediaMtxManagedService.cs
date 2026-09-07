namespace TrueCentralVms.Server.Services.Supervisor;

/// <summary>
/// MediaMTX visto por el watchdog. El proceso vivo no basta: se le pregunta a
/// su API de control (loopback) para detectar un media server colgado.
/// MediaMtxManager ya se relanza solo ante una caída del proceso; mientras
/// esa reposición está pendiente el watchdog lo reporta "Iniciando" en vez
/// de disparar un segundo arranque encima.
/// </summary>
public sealed class MediaMtxManagedService(MediaMtxManager manager) : IManagedService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public string Id => "mediamtx";
    public string Name => "Media server (MediaMTX)";
    public string Description => "Plano de media: un solo pull RTSP por cámara compartido entre todos los espectadores.";
    public string Kind => "process";
    public bool CanStop => true;
    public string? DisabledReason => null;

    public Task StartAsync(CancellationToken ct) => manager.StartAsync(ct);
    public Task StopAsync(CancellationToken ct) => manager.StopAsync(ct);

    public async Task<ServiceHealth> CheckAsync(CancellationToken ct)
    {
        if (manager.IsRunning)
        {
            string detail = $"PID {manager.ProcessId}, RTSP :{manager.RtspPort} (TCP), API 127.0.0.1:{manager.ApiPort}";
            try
            {
                using var response = await Http.GetAsync($"{manager.ApiBaseUrl}/v3/config/global/get", ct);
                if (response.IsSuccessStatusCode)
                    return ServiceHealth.Ok(detail);
                return ServiceHealth.Down($"La API de control de MediaMTX respondió {(int)response.StatusCode}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Recién lanzado todavía puede no escuchar: darle unos segundos.
                if (manager.StartedAtUtc is { } started && DateTime.UtcNow - started < TimeSpan.FromSeconds(15))
                    return ServiceHealth.Restarting("Proceso iniciado; esperando su API de control.");
                return ServiceHealth.Down("El proceso está vivo pero su API de control no responde.");
            }
        }
        if (manager.RestartPending)
            return ServiceHealth.Restarting("El proceso terminó; MediaMTX se relanza solo en unos segundos.");
        if (!manager.ExecutableFound)
            return ServiceHealth.Down(@"No se encontró tools\mediamtx\mediamtx.exe: copie MediaMTX junto al servidor.");
        return ServiceHealth.Down(manager.LastExitCode is { } code
            ? $"El proceso no está en ejecución (último código de salida {code})."
            : "El proceso no está en ejecución.");
    }
}
