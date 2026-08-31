using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Módulo Aplicaciones → Reconocimiento de patentes: muestra en vivo lo que
/// leen las cámaras ANPR y mantiene la lista de los últimos reconocimientos
/// con toda la información que entregó el equipo.
///
/// Las lecturas llegan empujadas por el hub (no hay sondeo): al abrir la
/// pantalla se carga el historial reciente y desde ahí cada evento nuevo se
/// inserta arriba. Con "Seguir en vivo" activo, la ficha de detalle salta
/// sola al último — es el modo de operación en una caseta de control.
/// </summary>
public sealed partial class LprViewModel : ObservableObject
{
    /// <summary>Tope de la lista en memoria: más allá no aporta y consume RAM en miniaturas.</summary>
    private const int MaxItems = 300;

    private readonly ApiClient _api;
    private readonly ClientSettings _settings;
    private CancellationTokenSource? _detailLoad;

    /// <summary>Archivo guardado: el shell lo avisa con la misma notificación
    /// flotante que las capturas del vivo.</summary>
    public event Action<string, string, string>? MediaSaved;

    public LprViewModel(ApiClient api, VmsHubClient hub, ClientSettings settings)
    {
        _api = api;
        _settings = settings;

        hub.PlateRecognized += dto => Application.Current.Dispatcher.InvokeAsync(() => OnPlateRecognized(dto));
        hub.ConfigChanged += entity =>
        {
            if (entity is "anpr-sources" or "devices")
                Application.Current.Dispatcher.InvokeAsync(() => _ = LoadSourcesAsync());
        };
    }

    // ------------------------------------------------------------------
    // Estado
    // ------------------------------------------------------------------

    public ObservableCollection<PlateEventViewModel> Events { get; } = [];

    /// <summary>Equipos capaces de entregar patentes (encendidos o no).</summary>
    public ObservableCollection<AnprSourceDto> Sources { get; } = [];

    [ObservableProperty] private PlateEventViewModel? _selected;

    /// <summary>La ficha salta sola al reconocimiento recién llegado.</summary>
    [ObservableProperty] private bool _followLive = true;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isSourcesPanelOpen;

    /// <summary>Filtro por equipo (null = todos).</summary>
    [ObservableProperty] private AnprSourceDto? _deviceFilter;

    /// <summary>Filtro por patente (búsqueda parcial, sin formato).</summary>
    [ObservableProperty] private string _plateFilter = "";

    /// <summary>Cantidad de fuentes con el canal de eventos realmente abierto.</summary>
    [ObservableProperty] private int _liveSourceCount;

    /// <summary>Encender o apagar fuentes exige rol administrador.</summary>
    public bool IsAdmin => _api.Role == "Admin";

    public bool HasEvents => Events.Count > 0;

    /// <summary>Resumen para la cabecera ("3 de 4 fuentes activas").</summary>
    public string SourcesSummary => Sources.Count == 0
        ? "Sin equipos compatibles"
        : $"{LiveSourceCount} de {Sources.Count(s => s.Enabled)} fuente(s) activa(s)";

    partial void OnLiveSourceCountChanged(int value) => OnPropertyChanged(nameof(SourcesSummary));

    partial void OnSelectedChanged(PlateEventViewModel? value)
    {
        _detailLoad?.Cancel();
        if (value is null) return;
        _detailLoad = new CancellationTokenSource();
        var token = _detailLoad.Token;
        _ = value.LoadDetailAsync(token);
    }

    // ------------------------------------------------------------------
    // Carga
    // ------------------------------------------------------------------

    /// <summary>Primera carga al abrir el módulo (fuentes + historial reciente).</summary>
    public async Task InitializeAsync()
    {
        await LoadSourcesAsync();
        await SearchAsync();
    }

    public async Task LoadSourcesAsync()
    {
        try
        {
            var sources = await _api.GetAnprSourcesAsync();
            // Se conserva el equipo filtrado aunque la lista se recargue.
            int? keep = DeviceFilter?.DeviceId;
            Sources.Clear();
            foreach (var source in sources)
                Sources.Add(source);
            DeviceFilter = keep is { } id ? Sources.FirstOrDefault(s => s.DeviceId == id) : null;
            LiveSourceCount = sources.Count(s => s.Live);
            OnPropertyChanged(nameof(SourcesSummary));

            if (sources.Count == 0)
                StatusMessage = "No hay equipos compatibles con reconocimiento de patentes en el inventario.";
            else if (!sources.Any(s => s.Enabled))
                StatusMessage = IsAdmin
                    ? "Ninguna cámara está encendida como fuente: ábrala en “Fuentes” y actívela."
                    : "Ninguna cámara está encendida como fuente. Pida a un administrador que la active.";
            else if (LiveSourceCount == 0)
                StatusMessage = FirstSourceError(sources) ?? "Conectando con las cámaras…";
            else
                StatusMessage = "";
        }
        catch (ApiException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private static string? FirstSourceError(IEnumerable<AnprSourceDto> sources) =>
        sources.FirstOrDefault(s => s.Enabled && s.LastError is { Length: > 0 })?.LastError;

    /// <summary>Recarga la lista aplicando los filtros vigentes.</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        IsBusy = true;
        try
        {
            var events = await _api.GetPlateEventsAsync(DeviceFilter?.DeviceId, PlateFilter, take: MaxItems);
            Events.Clear();
            foreach (var dto in events)
                Events.Add(Create(dto));
            Selected = Events.FirstOrDefault();
            OnPropertyChanged(nameof(HasEvents));
            if (events.Count == 0 && (PlateFilter.Length > 0 || DeviceFilter is not null))
                StatusMessage = "Ningún reconocimiento coincide con la búsqueda.";
        }
        catch (ApiException ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearFiltersAsync()
    {
        PlateFilter = "";
        DeviceFilter = null;
        await SearchAsync();
    }

    private PlateEventViewModel Create(PlateEventDto dto)
    {
        var item = new PlateEventViewModel(_api, dto);
        _ = item.LoadThumbnailAsync();
        return item;
    }

    // ------------------------------------------------------------------
    // Llegada en vivo
    // ------------------------------------------------------------------

    private void OnPlateRecognized(PlateEventDto dto)
    {
        // Con un filtro puesto, la lista sigue siendo la de la búsqueda: lo
        // que no calza no se cuela (pero sí actualiza el contador de fuentes).
        if (DeviceFilter is { } device && dto.DeviceId != device.DeviceId) return;
        if (PlateFilter.Trim().Length > 0)
        {
            string needle = new string(PlateFilter.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (needle.Length > 0 && !dto.PlateNumber.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return;
        }

        var item = Create(dto);
        Events.Insert(0, item);
        while (Events.Count > MaxItems)
            Events.RemoveAt(Events.Count - 1);
        OnPropertyChanged(nameof(HasEvents));

        if (FollowLive)
            Selected = item;
        StatusMessage = "";
        // Un evento prueba que hay al menos una fuente viva. Solo se consulta
        // cuando el contador dice lo contrario: con tránsito pesado, pedirlo
        // en cada lectura sería una llamada por vehículo.
        if (LiveSourceCount == 0)
            _ = RefreshLiveCountAsync();
    }

    /// <summary>Corrige el contador de fuentes activas tras recibir un evento.</summary>
    private async Task RefreshLiveCountAsync()
    {
        try
        {
            var sources = await _api.GetAnprSourcesAsync();
            LiveSourceCount = sources.Count(s => s.Live);
        }
        catch (ApiException) { /* el contador se corrige en la próxima recarga */ }
    }

    // ------------------------------------------------------------------
    // Fuentes (administrador)
    // ------------------------------------------------------------------

    [RelayCommand]
    private void ToggleSourcesPanel() => IsSourcesPanelOpen = !IsSourcesPanelOpen;

    /// <summary>Enciende o apaga un equipo como fuente de patentes.</summary>
    public async Task SetSourceAsync(AnprSourceDto source, bool enabled)
    {
        try
        {
            await _api.SetAnprSourceAsync(source.DeviceId, enabled);
            StatusMessage = enabled
                ? $"“{source.DeviceName}” encendida: conectando con el equipo…"
                : $"“{source.DeviceName}” apagada como fuente.";
            await LoadSourcesAsync();
        }
        catch (ApiException ex)
        {
            StatusMessage = ex.Message;
            await LoadSourcesAsync(); // devolver el interruptor a su estado real
        }
    }

    // ------------------------------------------------------------------
    // Acciones sobre el reconocimiento seleccionado
    // ------------------------------------------------------------------

    [RelayCommand]
    private void CopyPlate()
    {
        if (Selected is not { } item) return;
        try
        {
            Clipboard.SetText(item.PlateNumber);
            StatusMessage = $"Patente {item.PlateNumber} copiada al portapapeles.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Otra aplicación tiene tomado el portapapeles: no es un error del VMS.
            StatusMessage = "No se pudo copiar: el portapapeles está ocupado por otra aplicación.";
        }
    }

    /// <summary>Guarda las fotos del reconocimiento en la carpeta de capturas.</summary>
    [RelayCommand]
    private void SaveImages()
    {
        if (Selected is not { } item) return;
        if (item.SceneImage is null && item.PlateImage is null)
        {
            StatusMessage = "Este reconocimiento no tiene imágenes que guardar.";
            return;
        }
        try
        {
            string folder = _settings.EffectiveSnapshotFolder;
            Directory.CreateDirectory(folder);
            string stem = $"{item.PlateNumber}_{item.Event.CapturedAt:yyyyMMdd_HHmmss}";
            string? last = null;
            if (item.SceneImage is BitmapSource scene)
                last = WriteJpeg(scene, Path.Combine(folder, $"{stem}_escena.jpg"));
            if (item.PlateImage is BitmapSource plate)
                last = WriteJpeg(plate, Path.Combine(folder, $"{stem}_placa.jpg")) ?? last;
            if (last is not null)
                MediaSaved?.Invoke("Reconocimiento guardado", "", last);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"No se pudieron guardar las imágenes: {ex.Message}";
        }
    }

    private static string? WriteJpeg(BitmapSource source, string path)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }
}
