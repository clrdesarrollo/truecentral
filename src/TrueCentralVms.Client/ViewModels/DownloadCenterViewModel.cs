using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>Ciclo de vida de una exportación en el Centro de descargas.</summary>
public enum DownloadState
{
    /// <summary>En cola, esperando su turno.</summary>
    Pending,
    /// <summary>El servidor está tirando del equipo y el archivo crece.</summary>
    Downloading,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Una exportación del Centro de descargas (fila de la lista).</summary>
public partial class DownloadJobViewModel : ObservableObject
{
    public required int DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required int ChannelNumber { get; init; }
    public required string ChannelName { get; init; }
    public required int RtspChannel { get; init; }
    public required DateTime From { get; init; }
    public required DateTime To { get; init; }
    public required string FilePath { get; init; }
    /// <summary>Contenedor pedido al servidor: "mp4" o "mkv".</summary>
    public required string Format { get; init; }

    /// <summary>Cuándo se pidió: es el orden de la lista y de la cola.</summary>
    public DateTime EnqueuedAt { get; } = DateTime.Now;

    internal CancellationTokenSource? Cancellation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(IsDownloading))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    private DownloadState _state = DownloadState.Pending;

    /// <summary>Texto de avance ("En cola", "12,3 MB", mensaje de error).</summary>
    [ObservableProperty] private string _progressText = "En cola";

    public string Title => $"{DeviceName} · {ChannelName}";
    public string FileName => Path.GetFileName(FilePath);
    public string RangeText => $"{From:dd-MM-yyyy HH:mm:ss} – {To:HH:mm:ss}  ({(To - From).TotalMinutes:0.#} min)";
    public string EnqueuedText => $"{EnqueuedAt:dd-MM-yyyy HH:mm:ss}";

    public string StateText => State switch
    {
        DownloadState.Pending => "Por comenzar",
        DownloadState.Downloading => "Descargando",
        DownloadState.Completed => "Terminada",
        DownloadState.Failed => "Fallida",
        _ => "Cancelada",
    };

    public bool IsDownloading => State == DownloadState.Downloading;
    public bool IsCompleted => State == DownloadState.Completed;
    public bool CanCancel => State is DownloadState.Pending or DownloadState.Downloading;
    public bool CanRetry => State is DownloadState.Failed or DownloadState.Cancelled;
    public bool CanRemove => !CanCancel;
}

/// <summary>
/// Centro de descargas: cola de exportaciones de grabaciones a MP4. Las
/// solicitudes se atienden DE A UNA (los grabadores limitan las sesiones de
/// reproducción simultáneas) y la lista conserva las terminadas, fallidas y
/// canceladas de la sesión, ordenadas de la más nueva a la más antigua. Vive
/// en el shell: cerrar la ventana del centro no detiene nada.
/// </summary>
public partial class DownloadCenterViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private bool _pumping;

    public ObservableCollection<DownloadJobViewModel> Jobs { get; } = [];

    /// <summary>Se guardó un archivo (título, glifo MDL2, ruta): toast del shell.</summary>
    public event Action<string, string, string>? MediaSaved;

    /// <summary>Entró una exportación a la cola: el shell muestra la ventana.</summary>
    public event Action? JobEnqueued;

    /// <summary>Resumen del encabezado ("1 descargando · 2 en cola").</summary>
    [ObservableProperty] private string _summary = "Sin descargas en esta sesión.";

    /// <summary>Hay algo pendiente o descargando (para avisar al salir).</summary>
    public bool HasActiveJobs => Jobs.Any(j => j.CanCancel);

    public DownloadCenterViewModel(ApiClient api)
    {
        _api = api;
    }

    /// <summary>Encola la exportación de un tramo. La ruta se ajusta sola si ya
    /// existe un archivo (u otra descarga) con el mismo nombre.</summary>
    public void Enqueue(ChannelNode channel, DateTime from, DateTime to, string filePath, string format = "mp4")
    {
        var job = new DownloadJobViewModel
        {
            DeviceId = channel.Device.Id,
            DeviceName = channel.Device.Name,
            ChannelNumber = channel.Channel.ChannelNumber,
            ChannelName = channel.Channel.Name,
            RtspChannel = channel.Channel.RtspChannel,
            From = from,
            To = to,
            FilePath = UniquePath(filePath),
            Format = format,
        };
        Jobs.Insert(0, job); // la lista va de la más nueva a la más antigua
        UpdateSummary();
        JobEnqueued?.Invoke();
        Pump();
    }

    /// <summary>Evita pisar un MP4 existente o el destino de otra descarga en curso.</summary>
    private string UniquePath(string filePath)
    {
        string directory = Path.GetDirectoryName(filePath) ?? "";
        string baseName = Path.GetFileNameWithoutExtension(filePath);
        string extension = Path.GetExtension(filePath);
        string candidate = filePath;
        for (int i = 2; File.Exists(candidate) || Jobs.Any(j => j.CanCancel && j.FilePath == candidate); i++)
            candidate = Path.Combine(directory, $"{baseName} ({i}){extension}");
        return candidate;
    }

    /// <summary>Atiende la cola de a una descarga, de la más antigua a la más
    /// nueva. Corre en el hilo de UI (los await liberan la interfaz).</summary>
    private async void Pump()
    {
        if (_pumping) return;
        _pumping = true;
        try
        {
            // La más antigua pendiente es la última de la lista (orden inverso).
            while (Jobs.LastOrDefault(j => j.State == DownloadState.Pending) is { } job)
                await RunAsync(job);
        }
        finally
        {
            _pumping = false;
        }
    }

    private async Task RunAsync(DownloadJobViewModel job)
    {
        job.State = DownloadState.Downloading;
        job.ProgressText = "Conectando con el equipo…";
        job.Cancellation = new CancellationTokenSource();
        UpdateSummary();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(job.FilePath)!);
            var progress = new Progress<long>(bytes => job.ProgressText = $"{bytes / 1024d / 1024d:0.0} MB");
            await _api.DownloadPlaybackAsync(job.DeviceId, job.RtspChannel, job.From, job.To,
                job.FilePath, progress, job.Format, job.Cancellation.Token);

            job.ProgressText = $"{new FileInfo(job.FilePath).Length / 1024d / 1024d:0.0} MB";
            job.State = DownloadState.Completed;
            MediaSaved?.Invoke("Grabación exportada", "\uE896", job.FilePath);
            ReportAudit(job, "playback-export-saved");
        }
        catch (OperationCanceledException)
        {
            TryDelete(job.FilePath);
            job.ProgressText = "Cancelada por el operador";
            job.State = DownloadState.Cancelled;
            ReportAudit(job, "playback-export-cancelled");
        }
        catch (Exception ex)
        {
            TryDelete(job.FilePath);
            job.ProgressText = ex is ApiException ? ex.Message : $"No se pudo exportar: {ex.Message}";
            job.State = DownloadState.Failed;
            ReportAudit(job, "playback-export-failed", job.ProgressText);
        }
        finally
        {
            job.Cancellation?.Dispose();
            job.Cancellation = null;
            UpdateSummary();
        }
    }

    [RelayCommand]
    private void Cancel(DownloadJobViewModel job)
    {
        if (job.State == DownloadState.Pending)
        {
            // Todavía no toca el equipo: se marca sin pasar por RunAsync.
            job.ProgressText = "Cancelada antes de comenzar";
            job.State = DownloadState.Cancelled;
            ReportAudit(job, "playback-export-cancelled", "cancelada en cola");
            UpdateSummary();
            return;
        }
        job.Cancellation?.Cancel();
    }

    /// <summary>Cancela todo lo pendiente y en curso (cierre de la aplicación):
    /// mejor esfuerzo para que los MP4 a medias alcancen a borrarse.</summary>
    public void CancelAll()
    {
        foreach (var job in Jobs.Where(j => j.CanCancel).ToList())
            Cancel(job);
    }

    /// <summary>Vuelve a encolar una descarga fallida o cancelada.</summary>
    [RelayCommand]
    private void Retry(DownloadJobViewModel job)
    {
        if (!job.CanRetry) return;
        job.ProgressText = "En cola";
        job.State = DownloadState.Pending;
        UpdateSummary();
        Pump();
    }

    [RelayCommand]
    private void OpenFolder(DownloadJobViewModel job)
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{job.FilePath}\"");
        }
        catch
        {
            // Explorador no disponible: no hay nada útil que hacer.
        }
    }

    /// <summary>Saca la fila de la lista (no borra el archivo exportado).</summary>
    [RelayCommand]
    private void Remove(DownloadJobViewModel job)
    {
        if (!job.CanRemove) return;
        Jobs.Remove(job);
        UpdateSummary();
    }

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var job in Jobs.Where(j => j.CanRemove).ToList())
            Jobs.Remove(job);
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int downloading = Jobs.Count(j => j.State == DownloadState.Downloading);
        int pending = Jobs.Count(j => j.State == DownloadState.Pending);
        int done = Jobs.Count(j => j.State == DownloadState.Completed);
        Summary = Jobs.Count == 0
            ? "Sin descargas en esta sesión."
            : string.Join(" · ", new[]
            {
                downloading > 0 ? $"{downloading} descargando" : null,
                pending > 0 ? $"{pending} en cola" : null,
                done > 0 ? $"{done} terminada{(done == 1 ? "" : "s")}" : null,
            }.Where(s => s is not null));
        OnPropertyChanged(nameof(HasActiveJobs));
    }

    /// <summary>Bitácora de auditoría del servidor (fire-and-forget, nunca lanza).</summary>
    private void ReportAudit(DownloadJobViewModel job, string action, string? extra = null)
    {
        string detail = $"tramo {job.From:dd-MM-yyyy HH:mm:ss} a {job.To:HH:mm:ss}" +
                        (extra is null ? "" : $"; {extra}");
        _ = _api.ReportAuditEventAsync(new ClientAuditEventDto(
            action, job.DeviceId, job.DeviceName, job.ChannelNumber, job.ChannelName,
            job.FilePath, detail));
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch
        {
            // Archivo parcial tomado por otro programa: queda en la carpeta.
        }
    }
}
