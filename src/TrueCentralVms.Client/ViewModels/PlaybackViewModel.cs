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

    /// <summary>
    /// Modo zoom digital: el puntero pasa a lupa y arrastrar sobre el video
    /// marca el área a acercar (sobre la imagen ya recibida del equipo).
    /// </summary>
    [ObservableProperty] private bool _isDigitalZoomMode;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanControl))]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanControl))]
    private bool _isPaused;

    /// <summary>
    /// Los botones de pausa y detención siguen activos con la reproducción
    /// PAUSADA. Antes colgaban de IsPlaying, y como una pausa larga termina
    /// cortando el tramo (el equipo entrega a 1× y el búfer se llena), los
    /// botones se apagaban solos y ya no se podía reanudar.
    /// </summary>
    public bool CanControl => IsPlaying || IsPaused;

    /// <summary>Instante en que se pausó: si el tramo se cortó, se reanuda exactamente ahí.</summary>
    private DateTime? _pausedAt;
    [ObservableProperty] private bool _isLoadingSegments;

    /// <summary>Velocidad de reproducción (0,25× a 8×; 1 = normal).</summary>
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

    /// <summary>
    /// Calendario para saltar a cualquier fecha (y ver qué días tiene grabados
    /// el equipo) sin recorrer día por día con las flechas.
    /// </summary>
    public RecordingCalendar Calendar { get; } = new();

    /// <summary>
    /// Marcas ya consultadas, por canal y mes. Preguntarle al equipo qué días
    /// grabó es una búsqueda completa sobre el mes y tarda: sin esto, cerrar y
    /// volver a abrir el calendario en la misma cámara la repetía entera.
    /// </summary>
    private readonly Dictionary<(int Device, int Channel, int Year, int Month), (List<int> Days, DateTime LoadedAt)>
        _calendarMarks = [];

    /// <summary>
    /// Cuánto vale la respuesta del mes EN CURSO: sigue grabando, así que la
    /// lista de días crece. Los meses ya cerrados no cambian y quedan
    /// cacheados toda la sesión.
    /// </summary>
    private static readonly TimeSpan CurrentMonthCacheLife = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Pide al equipo qué días del mes visible tienen grabación. Se consulta
    /// por el canal con el foco: es una búsqueda por canal, y preguntar por
    /// los cuatro abiertos multiplicaría la espera sin agregar información
    /// (en la práctica graban con la misma política).
    /// </summary>
    private async Task LoadCalendarMarksAsync()
    {
        if ((SelectedCell ?? Cells.FirstOrDefault()) is not { } cell)
        {
            Calendar.ApplyMarks([]);
            return;
        }

        var month = Calendar.Month;
        var key = (cell.Channel.Device.Id, cell.Channel.Channel.ChannelNumber, month.Year, month.Month);
        if (_calendarMarks.TryGetValue(key, out var cached) && !IsStale(key, cached.LoadedAt))
        {
            Calendar.ApplyMarks(cached.Days);
            return;
        }

        Calendar.IsLoading = true;
        try
        {
            var days = await _api.GetRecordedDaysAsync(key.Item1, key.Item2, month.Year, month.Month);
            _calendarMarks[key] = (days, DateTime.UtcNow);
            // El operador pudo cambiar de mes mientras el equipo respondía.
            if (Calendar.Month == month)
                Calendar.ApplyMarks(days);
        }
        catch (ApiException)
        {
            Calendar.ApplyMarks([]); // sin marcas el calendario igual sirve para elegir la fecha
        }
        finally
        {
            Calendar.IsLoading = false;
        }
    }

    private static bool IsStale((int Device, int Channel, int Year, int Month) key, DateTime loadedAt)
    {
        var today = DateTime.Today;
        bool currentMonth = key.Year == today.Year && key.Month == today.Month;
        return currentMonth && DateTime.UtcNow - loadedAt > CurrentMonthCacheLife;
    }

    public PlaybackViewModel(ApiClient api, ClientSettings settings)
    {
        _api = api;
        _settings = settings;

        Calendar.Selected = Date;
        Calendar.DayPicked += day => Date = day;
        Calendar.MonthChanged += () => _ = LoadCalendarMarksAsync();

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
    /// <summary>
    /// Suma el canal a la reproducción. <paramref name="target"/> (arrastrar y
    /// soltar sobre un cuadro) reemplaza ESE cuadro en vez del que tiene el
    /// foco: el operador soltó ahí a propósito.
    /// </summary>
    public async Task SelectChannelAsync(ChannelNode node, bool replaceAll = false,
        PlaybackCellViewModel? target = null)
    {
        if (target is not null && Cells.Contains(target))
            SelectedCell = target;
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
        // Las capturas y cápsulas avisan igual que en la Vista en Vivo.
        created.MediaSaved += OnCellMediaSaved;

        // Grilla llena: el canal nuevo entra en el cuadro con foco (mismo
        // criterio que la vista en vivo al abrir sobre un cuadro ocupado).
        if (Cells.Count >= MaxChannels || target is not null)
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

        // Sumar un canal arranca la reproducción solo: quedarse en negro
        // esperando un clic en la línea de tiempo era un paso de más.
        if (created.Segments.Count == 0)
            return; // este canal no grabó nada ese día: no hay qué reproducir

        var instant = Playhead ?? _pausedAt;
        // IsPlaying lo refresca el reloj cada medio segundo, así que acá se
        // miran los cuadros directamente: el recién creado todavía no arrancó.
        bool othersRunning = IsPaused || Cells.Any(c => !ReferenceEquals(c, created) && c.IsPlaying);

        if (instant is { } time && othersRunning)
        {
            // Ya hay reproducción en curso: el canal nuevo se engancha a la
            // misma hora en vez de reiniciar a los demás.
            await created.OpenAtAsync(time, Date.Date.AddDays(1), Speed);
            created.ApplySpeed(Speed);
            return;
        }

        // Nada andando: parte desde donde quedó la aguja si el operador ya se
        // había posicionado, y si no, desde el comienzo del día elegido (si a
        // esa hora no hay grabación, SeekToAsync salta al primer tramo).
        await SeekToAsync(instant ?? Date.Date);
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
        cell.MediaSaved -= OnCellMediaSaved;
        cell.Stop();
        cell.Dispose();
    }

    /// <summary>Un cuadro guardó una captura o cápsula: el shell lo notifica.</summary>
    private void OnCellMediaSaved(string title, string glyph, string path) =>
        MediaSaved?.Invoke(title, glyph, path);

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
        Calendar.Selected = value;
        SelectionStart = SelectionEnd = null;
        _ = ChangeDayAsync(value);
    }

    /// <summary>
    /// Cambiar de día continúa en la MISMA hora: si venía reproduciendo las
    /// 14:32 del martes y elige el viernes, arranca en las 14:32 del viernes
    /// (es como se revisa un mismo hecho en días distintos). Sin nada en
    /// reproducción parte a las 00:00 del día elegido. Si a esa hora no hay
    /// grabación, salta al primer tramo disponible.
    /// </summary>
    private async Task ChangeDayAsync(DateTime day)
    {
        // La hora se toma ANTES de recargar: la aguja se mueve con los tramos.
        var timeOfDay = (Playhead ?? _pausedAt)?.TimeOfDay;
        bool wasRunning = IsPlaying || IsPaused;

        await LoadSegmentsAsync();
        if (Cells.Count == 0) return;

        if (!Cells.Any(c => c.Segments.Count > 0))
        {
            // El día elegido no tiene nada: dejar corriendo el día anterior
            // sería engañoso (la línea de tiempo ya muestra el nuevo).
            StopAll();
            DescribeSegments();
            return;
        }

        await SeekToAsync(day.Date + (wasRunning && timeOfDay is { } time ? time : TimeSpan.Zero));
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
        await Task.WhenAll(Cells.Select(c => c.OpenAtAsync(localTime, endOfDay, Speed)));
        foreach (var cell in Cells) cell.ApplySpeed(Speed);
        // ONVIF no admite posicionar por hora: conviene decirlo donde se ve.
        Status = Cells.Any(c => c.IsPlaying && !c.IsSeekExact)
            ? "El equipo ONVIF reproduce desde el inicio de su grabación: no permite posicionarse por hora."
            : "";
    }

    /// <summary>
    /// Pausa y reanudación. Pausar es local (instantáneo), pero el equipo
    /// sigue entregando a 1× y una pausa larga acaba cortando el tramo: al
    /// reanudar, si el player ya no está vivo se vuelve a abrir la grabación
    /// exactamente en el instante donde quedó la aguja.
    /// </summary>
    [RelayCommand]
    private async Task TogglePauseAsync()
    {
        if (Cells.Count == 0) return;

        if (!IsPaused)
        {
            if (!IsPlaying) return;
            _pausedAt = Playhead;
            IsPaused = true;
            foreach (var cell in Cells) cell.Pause();
            Status = "En pausa.";
            return;
        }

        IsPaused = false;
        if (Cells.All(c => c.IsPlaying))
        {
            foreach (var cell in Cells) cell.Resume();
            Status = "";
            return;
        }
        await SeekToAsync(_pausedAt ?? Playhead ?? Date.Date);
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
    private Task SlowDownAsync() => StepSpeedAsync(-1);

    /// <summary>Velocidad más rápida (» de la barra).</summary>
    [RelayCommand]
    private Task SpeedUpAsync() => StepSpeedAsync(1);

    /// <summary>
    /// Cambia la velocidad de reproducción. El equipo entrega el video
    /// grabado a su propio ritmo: acelerar consume lo que ya llegó y puede
    /// quedarse esperando datos en enlaces lentos.
    /// </summary>
    private async Task StepSpeedAsync(int step)
    {
        int index = Math.Max(0, Array.IndexOf(Speeds, Speed));
        int next = Math.Clamp(index + step, 0, Speeds.Length - 1);
        if (next == index) return;
        Speed = Speeds[next];

        // La velocidad la decide el EQUIPO al abrir el tramo, así que cambiarla
        // exige volver a pedirlo desde donde va la aguja. Sin reproducción en
        // curso basta con dejar el player listo para la próxima apertura.
        if (Cells.Count > 0 && (IsPlaying || IsPaused))
            await SeekToAsync(Playhead ?? _pausedAt ?? Date.Date);
        else
            foreach (var cell in Cells) cell.ApplySpeed(Speed);
        Status = Speed == 1 ? "" : $"Velocidad {Speed:0.##}×.";
    }

    [RelayCommand]
    private void Stop() => StopAll();

    private void StopAll()
    {
        foreach (var cell in Cells) cell.Stop();
        _pausedAt = null;
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

    partial void OnIsDigitalZoomModeChanged(bool value) =>
        Status = value
            ? "Zoom digital: arrastre sobre el video para marcar el área; clic derecho vuelve a 1×."
            : "";

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
