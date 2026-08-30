using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>Pista de la línea de tiempo: los tramos grabados de un canal.</summary>
public sealed record TimelineTrack(string Name, IReadOnlyList<RecordingSegmentDto> Segments);

/// <summary>
/// Módulo Reproducción: las grabaciones viven en el disco del DVR/NVR (o en
/// la tarjeta de la cámara). Se eligen hasta cuatro canales, la línea de
/// tiempo muestra una pista por canal y el clic sobre ella pide al servidor
/// una concesión de reproducción por canal — todos abren el MISMO instante,
/// así que la reproducción multicanal queda sincronizada por la hora del
/// equipo. Arrastrando sobre la línea se marca un tramo para exportarlo a MP4.
/// </summary>
public partial class PlaybackViewModel : ObservableObject, IDisposable
{
    /// <summary>Tope de canales simultáneos: cada uno es un pull de video
    /// desde el equipo, y los grabadores limitan las sesiones de playback.</summary>
    public const int MaxChannels = 4;

    private static readonly double[] Speeds = [0.25, 0.5, 1, 2, 4, 8];

    private readonly ApiClient _api;
    private readonly ClientSettings _settings;
    private readonly DispatcherTimer _clock;
    private CancellationTokenSource? _downloadCancel;

    public ObservableCollection<PlaybackCellViewModel> Cells { get; } = [];

    /// <summary>Cuadro con el foco: manda en el audio y en la exportación.</summary>
    [ObservableProperty] private PlaybackCellViewModel? _selectedCell;

    /// <summary>División de la grilla según cuántos canales haya abiertos.</summary>
    [ObservableProperty] private VideoLayout _layout = VideoLayout.Standard[0];

    /// <summary>Día visible en la línea de tiempo (hora local del equipo).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayheadText))]
    private DateTime _date = DateTime.Today;

    /// <summary>Una pista por canal abierto (las dibuja la línea de tiempo).</summary>
    [ObservableProperty] private List<TimelineTrack> _tracks = [];

    [ObservableProperty] private string _status = EmptyMessage;

    /// <summary>Hora local que se está reproduciendo (aguja de la línea de tiempo).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayheadText))]
    private DateTime? _playhead;

    /// <summary>Hora bajo el cursor mientras se arrastra la aguja (null si no se arrastra).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayheadText))]
    private DateTime? _scrubTime;

    /// <summary>Modo recorte: el arrastre sobre la línea marca un tramo en vez de navegar.</summary>
    [ObservableProperty] private bool _isSelectionMode;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isLoadingSegments;

    /// <summary>Velocidad de reproducción (0,5× a 4×).</summary>
    [ObservableProperty] private double _speed = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private DateTime? _selectionStart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private DateTime? _selectionEnd;

    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private string _downloadStatus = "";

    private const string EmptyMessage = "Elija un canal del árbol (doble clic) para ver sus grabaciones.";

    /// <summary>Se guardó un archivo local (título, glifo MDL2, ruta): el
    /// shell lo muestra como notificación con el enlace a la carpeta.</summary>
    public event Action<string, string, string>? MediaSaved;

    /// <summary>
    /// Reloj de la barra de reproducción, con fecha completa como en los
    /// grabadores. Mientras se arrastra la aguja manda la hora arrastrada:
    /// el operador ve a dónde va a saltar antes de soltar.
    /// </summary>
    public string PlayheadText => (ScrubTime ?? Playhead) is { } time
        ? time.ToString("yyyy-MM-dd HH:mm:ss")
        : $"{Date:yyyy-MM-dd} --:--:--";

    public bool HasSelection => SelectionStart is not null && SelectionEnd is not null;

    public string SelectionText => SelectionStart is { } from && SelectionEnd is { } to
        ? $"{from:HH:mm:ss} – {to:HH:mm:ss}  ({(to - from).TotalMinutes:0.#} min)"
        : "Arrastre sobre la línea de tiempo para marcar un tramo";

    public PlaybackViewModel(ApiClient api, ClientSettings settings)
    {
        _api = api;
        _settings = settings;

        // Aguja de la línea de tiempo: hora de inicio del tramo + reloj del player.
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clock.Tick += (_, _) =>
        {
            if (Cells.FirstOrDefault(c => c.Playhead is not null)?.Playhead is { } time)
                Playhead = time;
            IsPlaying = Cells.Any(c => c.IsPlaying);
        };
        _clock.Start();
    }

    // ------------------------------------------------------------------
    // Canales
    // ------------------------------------------------------------------

    /// <summary>
    /// Doble clic en un canal del árbol: se SUMA a la reproducción
    /// sincronizada (hasta <see cref="MaxChannels"/>, como la vista en vivo va
    /// llenando cuadros). Con la grilla llena reemplaza el cuadro con foco.
    /// Con <paramref name="replaceAll"/> (Ctrl + doble clic) deja solo ese
    /// canal.
    /// </summary>
    public async Task SelectChannelAsync(ChannelNode node, bool replaceAll = false)
    {
        if (Cells.FirstOrDefault(c => c.Channel.Device.Id == node.Device.Id &&
                                      c.Channel.Channel.ChannelNumber == node.Channel.ChannelNumber) is { } existing)
        {
            SelectedCell = existing;
            return;
        }

        if (replaceAll)
        {
            // Primero salen de la colección (la grilla suelta sus superficies)
            // y recién ahí se liberan los players, como en la vista en vivo.
            var previous = Cells.ToList();
            Cells.Clear();
            foreach (var cell in previous) Detach(cell);
            Playhead = null;
            IsPaused = false;
        }

        var created = new PlaybackCellViewModel(_api, _settings, node);
        created.AudioActivated += OnCellAudioActivated;
        created.CloseRequested += cell => RemoveCell(cell);

        // Grilla llena: el canal nuevo entra en el cuadro con foco (mismo
        // criterio que la vista en vivo al abrir sobre un cuadro ocupado).
        if (Cells.Count >= MaxChannels)
        {
            int index = SelectedCell is { } focused ? Cells.IndexOf(focused) : 0;
            if (index < 0) index = 0;
            var replaced = Cells[index];
            Cells[index] = created;
            Detach(replaced);
        }
        else
        {
            Cells.Add(created);
        }
        SelectedCell = created;
        Layout = LayoutFor(Cells.Count);

        IsLoadingSegments = true;
        Status = $"Consultando grabaciones de \"{node.Channel.Name}\" del {Date:dd-MM-yyyy}…";
        try
        {
            await created.LoadSegmentsAsync(Date);
        }
        finally
        {
            IsLoadingSegments = false;
        }
        RefreshTracks();
        DescribeSegments();

        // Con la reproducción andando, el canal recién sumado se engancha a la
        // misma hora en vez de quedarse en negro.
        if (Playhead is { } playhead && Cells.Count > 1 && Cells.Any(c => c.IsPlaying))
        {
            await created.OpenAtAsync(playhead, Date.Date.AddDays(1));
            created.ApplySpeed(Speed);
        }
    }

    /// <summary>Quita un canal de la reproducción (su ✕).</summary>
    public void RemoveCell(PlaybackCellViewModel cell)
    {
        if (!Cells.Remove(cell)) return;
        Detach(cell);
        if (ReferenceEquals(SelectedCell, cell)) SelectedCell = Cells.FirstOrDefault();
        Layout = LayoutFor(Math.Max(Cells.Count, 1));
        RefreshTracks();
        if (Cells.Count == 0)
        {
            Playhead = null;
            Status = EmptyMessage;
        }
    }

    private void Detach(PlaybackCellViewModel cell)
    {
        cell.AudioActivated -= OnCellAudioActivated;
        cell.Stop();
        cell.Dispose();
    }

    /// <summary>Audio exclusivo: el cuadro que lo enciende silencia a los demás.</summary>
    private void OnCellAudioActivated(PlaybackCellViewModel owner)
    {
        foreach (var cell in Cells)
            if (!ReferenceEquals(cell, owner)) cell.MuteQuietly();
    }

    private static VideoLayout LayoutFor(int count) => count switch
    {
        <= 1 => VideoLayout.Standard[0], // 1
        2 => VideoLayout.FitFor(2),      // 2×1
        _ => VideoLayout.Standard[1],    // 2×2
    };

    private void RefreshTracks() =>
        Tracks = Cells.Select(c => new TimelineTrack(c.Channel.Channel.Name, c.Segments)).ToList();

    private void DescribeSegments()
    {
        int total = Cells.Sum(c => c.Segments.Count);
        Status = total == 0
            ? $"Sin grabaciones el {Date:dd-MM-yyyy}. El equipo conserva según su propio disco y política."
            : "Clic en la línea de tiempo para reproducir; arrastre para marcar un tramo.";
    }

    // ------------------------------------------------------------------
    // Día visible
    // ------------------------------------------------------------------

    partial void OnDateChanged(DateTime value)
    {
        SelectionStart = SelectionEnd = null;
        _ = LoadSegmentsAsync();
    }

    [RelayCommand]
    private void PreviousDay() => Date = Date.AddDays(-1);

    [RelayCommand]
    private void NextDay()
    {
        if (Date < DateTime.Today) Date = Date.AddDays(1);
    }

    [RelayCommand]
    private void GoToday() => Date = DateTime.Today;

    /// <summary>Recarga los tramos de todos los canales abiertos.</summary>
    public async Task LoadSegmentsAsync()
    {
        if (Cells.Count == 0) return;
        IsLoadingSegments = true;
        Status = $"Consultando grabaciones del {Date:dd-MM-yyyy}…";
        try
        {
            await Task.WhenAll(Cells.Select(c => c.LoadSegmentsAsync(Date)));
        }
        finally
        {
            IsLoadingSegments = false;
        }
        RefreshTracks();
        DescribeSegments();
    }

    // ------------------------------------------------------------------
    // Reproducción
    // ------------------------------------------------------------------

    /// <summary>
    /// Clic en la línea de tiempo: TODOS los canales abren esa hora. Si el
    /// instante cae fuera de lo grabado, salta al inicio del siguiente tramo.
    /// </summary>
    public async Task SeekToAsync(DateTime localTime)
    {
        if (Cells.Count == 0)
        {
            Status = EmptyMessage;
            return;
        }

        if (!Cells.Any(c => c.Segments.Any(s => localTime >= s.Start && localTime < s.End)))
        {
            var next = Cells.SelectMany(c => c.Segments)
                .Where(s => s.Start > localTime)
                .OrderBy(s => s.Start)
                .FirstOrDefault();
            if (next is null)
            {
                Status = "No hay grabación desde esa hora en adelante.";
                return;
            }
            localTime = next.Start;
        }

        IsPaused = false;
        Playhead = localTime;
        Status = $"Abriendo grabación de las {localTime:HH:mm:ss}…";
        var endOfDay = Date.Date.AddDays(1);
        await Task.WhenAll(Cells.Select(c => c.OpenAtAsync(localTime, endOfDay)));
        foreach (var cell in Cells) cell.ApplySpeed(Speed);
        // ONVIF no admite posicionar por hora: conviene decirlo donde se ve.
        Status = Cells.Any(c => c.IsPlaying && !c.IsSeekExact)
            ? "El equipo ONVIF reproduce desde el inicio de su grabación: no permite posicionarse por hora."
            : "";
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (!IsPlaying) return;
        IsPaused = !IsPaused;
        foreach (var cell in Cells)
        {
            if (IsPaused) cell.Pause();
            else cell.Resume();
        }
    }

    /// <summary>Salto relativo en segundos (los botones de ±30 s y ±5 min).</summary>
    [RelayCommand]
    private async Task SkipAsync(string seconds)
    {
        if (!int.TryParse(seconds, out int delta)) return;
        var from = Playhead ?? Date.Date;
        var target = from.AddSeconds(delta);
        if (target < Date.Date) target = Date.Date;
        if (target >= Date.Date.AddDays(1)) target = Date.Date.AddDays(1).AddSeconds(-1);
        await SeekToAsync(target);
    }

    /// <summary>Velocidad más lenta (« de la barra).</summary>
    [RelayCommand]
    private void SlowDown() => StepSpeed(-1);

    /// <summary>Velocidad más rápida (» de la barra).</summary>
    [RelayCommand]
    private void SpeedUp() => StepSpeed(1);

    /// <summary>
    /// Cambia la velocidad de reproducción. El equipo entrega el video
    /// grabado a su propio ritmo: acelerar consume lo que ya llegó y puede
    /// quedarse esperando datos en enlaces lentos.
    /// </summary>
    private void StepSpeed(int step)
    {
        int index = Math.Max(0, Array.IndexOf(Speeds, Speed));
        int next = Math.Clamp(index + step, 0, Speeds.Length - 1);
        if (next == index) return;
        Speed = Speeds[next];
        foreach (var cell in Cells) cell.ApplySpeed(Speed);
        Status = Speed == 1 ? "" : $"Velocidad {Speed:0.##}× (depende de lo que alcance a entregar el equipo).";
    }

    [RelayCommand]
    private void Stop() => StopAll();

    private void StopAll()
    {
        foreach (var cell in Cells) cell.Stop();
        IsPlaying = false;
        IsPaused = false;
        Playhead = null;
        Status = Cells.Count == 0 ? EmptyMessage : "";
    }

    // ------------------------------------------------------------------
    // Selección y exportación de tramos
    // ------------------------------------------------------------------

    /// <summary>Arrastre sobre la línea de tiempo (null = limpiar la marca).</summary>
    public void SetSelection(DateTime? from, DateTime? to)
    {
        if (from is null || to is null || to <= from)
        {
            SelectionStart = SelectionEnd = null;
            return;
        }
        SelectionStart = from;
        SelectionEnd = to;
    }

    partial void OnIsSelectionModeChanged(bool value) =>
        Status = value
            ? "Modo recorte: arrastre sobre la línea de tiempo para marcar el tramo a exportar."
            : "";

    [RelayCommand]
    private void ClearSelection() => SetSelection(null, null);

    [RelayCommand]
    private async Task PlaySelectionAsync()
    {
        if (SelectionStart is { } from) await SeekToAsync(from);
    }

    /// <summary>
    /// Exporta el tramo marcado del cuadro con foco a un MP4 en la carpeta de
    /// grabaciones. El servidor tira del equipo con FFmpeg y va enviando el
    /// archivo: avanza al ritmo al que el grabador entrega el video.
    /// </summary>
    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (IsDownloading) return;
        if (SelectedCell is not { } cell)
        {
            Status = "Elija primero un canal.";
            return;
        }
        if (SelectionStart is not { } from || SelectionEnd is not { } to)
        {
            Status = "Arrastre sobre la línea de tiempo para marcar el tramo a exportar.";
            return;
        }
        if (to - from > TimeSpan.FromHours(2))
        {
            Status = "El tramo a exportar no puede superar 2 horas.";
            return;
        }

        string folder = _settings.EffectiveRecordingFolder;
        string file = Path.Combine(folder,
            $"{SafeName(cell.Channel.Device.Name)}_{SafeName(cell.Channel.Channel.Name)}_{from:yyyyMMdd_HHmmss}.mp4");

        _downloadCancel = new CancellationTokenSource();
        IsDownloading = true;
        DownloadStatus = "Preparando la exportación…";
        try
        {
            Directory.CreateDirectory(folder);
            var progress = new Progress<long>(bytes =>
                DownloadStatus = $"Exportando… {bytes / 1024d / 1024d:0.0} MB");
            await _api.DownloadPlaybackAsync(cell.Channel.Device.Id, cell.Channel.Channel.RtspChannel,
                from, to, file, progress, _downloadCancel.Token);
            Status = $"Tramo exportado ({(to - from).TotalMinutes:0.#} min).";
            MediaSaved?.Invoke("Grabación exportada", "\uE896", file);
        }
        catch (OperationCanceledException)
        {
            TryDelete(file);
            Status = "Exportación cancelada.";
        }
        catch (Exception ex)
        {
            TryDelete(file);
            Status = ex is ApiException ? ex.Message : $"No se pudo exportar el tramo: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
            DownloadStatus = "";
            _downloadCancel?.Dispose();
            _downloadCancel = null;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _downloadCancel?.Cancel();

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

    /// <summary>Nombre de equipo/canal apto para un archivo de Windows.</summary>
    private static string SafeName(string name)
    {
        var clean = name.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
            clean = clean.Replace(invalid, '_');
        clean = clean.Replace(' ', '_');
        return clean.Length > 0 ? clean : "canal";
    }

    public void Dispose()
    {
        _clock.Stop();
        _downloadCancel?.Cancel();
        var open = Cells.ToList();
        Cells.Clear();
        foreach (var cell in open) Detach(cell);
    }
}
