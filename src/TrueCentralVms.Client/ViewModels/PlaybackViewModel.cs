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
/// Posición libre de la grilla de reproducción. No tiene estado: existe para
/// ocupar su lugar en <see cref="PlaybackViewModel.Slots"/> y así poder
/// dibujarla vacía y aceptar que le suelten un canal encima.
/// </summary>
public sealed class PlaybackSlot;

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
    /// <summary>Centro de descargas del shell: las exportaciones se encolan ahí.</summary>
    private readonly DownloadCenterViewModel _downloads;
    /// <summary>Canales del inventario (árbol compartido del shell), para que
    /// el diálogo de exportación ofrezca cualquier cámara, no solo las abiertas.</summary>
    private readonly Func<IEnumerable<ChannelNode>> _channelSource;

    /// <summary>
    /// Posiciones de la grilla EN ORDEN: cada una es un cuadro con canal
    /// (<see cref="PlaybackCellViewModel"/>) o una posición libre
    /// (<see cref="PlaybackSlot"/>). Es lo que dibuja la grilla, y lo que
    /// permite soltar una cámara exactamente donde el operador quiera.
    /// </summary>
    public ObservableCollection<object> Slots { get; } = [];

    /// <summary>Cuadros con canal, en el orden de la grilla. Lo mantiene
    /// <see cref="SyncCells"/>: la sincronización, las pistas de la línea de
    /// tiempo y el audio exclusivo trabajan sobre esta lista.</summary>
    public ObservableCollection<PlaybackCellViewModel> Cells { get; } = [];

    /// <summary>Cuadro con el foco: manda en el audio y en la exportación.</summary>
    [ObservableProperty] private PlaybackCellViewModel? _selectedCell;

    /// <summary>Posición bajo el puntero durante un arrastre (se ilumina para
    /// mostrar dónde va a caer la cámara). null = no hay arrastre encima.</summary>
    [ObservableProperty] private object? _dropTarget;

    /// <summary>División de la grilla; la fija <see cref="GridSize"/>.</summary>
    [ObservableProperty] private VideoLayout _layout = VideoLayout.Standard[0];

    /// <summary>
    /// Posiciones de la grilla: 1 (un canal a pantalla completa) o 4 (mosaico
    /// 2×2 con las posiciones libres a la vista, para comparar cámaras a la
    /// misma hora). Los botones de la barra la cambian; sumar un segundo canal
    /// la lleva sola a 4.
    /// </summary>
    [ObservableProperty] private int _gridSize = 1;

    partial void OnGridSizeChanged(int value)
    {
        Layout = value <= 1 ? VideoLayout.Standard[0] : VideoLayout.Standard[1];
        ResizeSlots();
    }

    /// <summary>
    /// Botones «1 canal» y «4 posiciones» de la barra. Bajar a un canal cierra
    /// los demás: cada cuadro abierto ocupa una sesión de reproducción del
    /// grabador, y dejarlos corriendo fuera de la vista las gastaría sin que
    /// nadie los mire.
    /// </summary>
    [RelayCommand]
    private void SetGridSize(int size)
    {
        size = size <= 1 ? 1 : MaxChannels;
        if (GridSize == size) return;

        if (size == 1)
        {
            var keep = SelectedCell ?? Cells.FirstOrDefault();
            foreach (var cell in Cells.Where(c => !ReferenceEquals(c, keep)).ToList())
                RemoveCell(cell);
            // Al achicar solo sobreviven las posiciones de adelante: el cuadro
            // que queda se muda a la primera (Move conserva su ventana de
            // video; recrear el elemento reiniciaría la reproducción).
            if (keep is not null && Slots.IndexOf(keep) is int from and > 0)
                Slots.Move(from, 0);
        }
        GridSize = size; // ajusta la división y las posiciones
    }

    /// <summary>Ajusta la cantidad de posiciones a <see cref="GridSize"/>,
    /// agregando o quitando de a una para no tocar los cuadros que ya están
    /// reproduciendo.</summary>
    private void ResizeSlots()
    {
        while (Slots.Count > GridSize)
        {
            int last = Slots.Count - 1;
            // Quien achica la grilla cierra antes los cuadros sobrantes; esto
            // es una red de seguridad para no dejar un player huérfano.
            if (Slots[last] is PlaybackCellViewModel orphan) Detach(orphan);
            Slots.RemoveAt(last);
        }
        while (Slots.Count < GridSize)
            Slots.Add(new PlaybackSlot());
        SyncCells();
    }

    /// <summary>Refleja en <see cref="Cells"/> los cuadros con canal, en el
    /// orden en que están puestos en la grilla.</summary>
    private void SyncCells()
    {
        var live = Slots.OfType<PlaybackCellViewModel>().ToList();
        if (Cells.SequenceEqual(live)) return;
        Cells.Clear();
        foreach (var cell in live) Cells.Add(cell);
    }

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

    public PlaybackViewModel(ApiClient api, ClientSettings settings, DownloadCenterViewModel downloads,
        Func<IEnumerable<ChannelNode>> channelSource)
    {
        _api = api;
        _settings = settings;
        _downloads = downloads;
        _channelSource = channelSource;

        // La grilla arranca con sus posiciones vacías a la vista.
        ResizeSlots();

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
    /// Pone el canal en la grilla de reproducción. Sin más datos entra en la
    /// primera posición libre (y sumar el segundo canal abre solo el mosaico de
    /// 4). <paramref name="slotIndex"/> lo manda a una posición EXACTA: es lo
    /// que usa el arrastrar y soltar, donde el operador eligió el lugar.
    /// <paramref name="replaceAll"/> (Ctrl + doble clic) deja solo ese canal.
    /// </summary>
    public async Task SelectChannelAsync(ChannelNode node, bool replaceAll = false, int? slotIndex = null)
    {
        if (Cells.FirstOrDefault(c => c.Channel.Device.Id == node.Device.Id &&
                                      c.Channel.Channel.ChannelNumber == node.Channel.ChannelNumber) is { } existing
            && !replaceAll)
        {
            // Ya está en la grilla: no se abre dos veces (sería una segunda
            // sesión de reproducción del mismo canal en el equipo).
            SelectedCell = existing;
            Status = $"\"{node.Channel.Name}\" ya está en la grilla.";
            return;
        }

        if (replaceAll)
        {
            foreach (var cell in Cells.ToList()) RemoveCell(cell);
            GridSize = 1;
            Playhead = null;
            IsPaused = false;
        }
        else if (slotIndex is null && GridSize == 1 && Cells.Count == 1)
        {
            // Sumar un segundo canal abre el mosaico de 4 posiciones: en la
            // grilla de uno solo el nuevo reemplazaría al que se está viendo.
            GridSize = MaxChannels;
        }

        // Posición destino: la elegida al soltar, la primera libre, o —con la
        // grilla llena— la del cuadro con foco.
        int index = slotIndex ?? Slots.IndexOf(Slots.FirstOrDefault(s => s is PlaybackSlot)!);
        if (index < 0) index = SelectedCell is { } focused ? Slots.IndexOf(focused) : 0;
        index = Math.Clamp(index, 0, Slots.Count - 1);

        var created = new PlaybackCellViewModel(_api, _settings, node);
        created.AudioActivated += OnCellAudioActivated;
        created.CloseRequested += cell => RemoveCell(cell);
        // Las capturas y cápsulas avisan igual que en la Vista en Vivo.
        created.MediaSaved += OnCellMediaSaved;

        // La posición elegida cambia de contenido: si tenía un canal, se cierra
        // (su sesión en el equipo se libera).
        if (Slots[index] is PlaybackCellViewModel replaced) Detach(replaced);
        Slots[index] = created;
        SyncCells();
        SelectedCell = created;

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
        if (Slots.IndexOf(cell) is not (int index and >= 0)) return;
        // La posición queda LIBRE en su lugar (no se recompone la grilla): es
        // la que eligió el operador con los botones de la barra.
        Slots[index] = new PlaybackSlot();
        SyncCells();
        Detach(cell);
        if (ReferenceEquals(SelectedCell, cell)) SelectedCell = Cells.FirstOrDefault();
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
    /// Abre el diálogo de exportación (cámara, rango exacto de fecha/hora,
    /// carpeta de destino, formato y división en archivos). El recorte de la
    /// línea de tiempo solo PRE-LLENA el rango: el diálogo es la fuente de
    /// verdad. Cada archivo aceptado entra como descarga al Centro de descargas.
    /// </summary>
    [RelayCommand]
    private void Download()
    {
        var channels = _channelSource().ToList();
        if (channels.Count == 0)
        {
            Status = "No hay canales disponibles para exportar.";
            return;
        }

        // Prellenado: el tramo marcado; sin marca, desde la aguja (o el
        // mediodía del día visible) con una hora de duración.
        DateTime from = SelectionStart ?? Playhead ?? Date.AddHours(12);
        DateTime to = SelectionEnd is { } end && end > from ? end : from.AddHours(1);

        var dialog = new Views.ExportDialog(channels, SelectedCell?.Channel, from, to, _settings, _downloads)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true) return;
        Status = dialog.EnqueuedCount == 1
            ? "Exportación agregada al Centro de descargas."
            : $"{dialog.EnqueuedCount} exportaciones agregadas al Centro de descargas.";
    }

    public void Dispose()
    {
        _clock.Stop();
        var open = Cells.ToList();
        // Primero salen de la grilla (suelta sus superficies de video) y recién
        // ahí se liberan los players, como en la vista en vivo.
        Slots.Clear();
        Cells.Clear();
        foreach (var cell in open) Detach(cell);
    }
}
