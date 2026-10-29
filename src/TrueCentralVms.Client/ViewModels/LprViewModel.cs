using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Módulo Aplicaciones → Reconocimiento de patentes: muestra en vivo lo que
/// leen las cámaras ANPR y mantiene la lista de los últimos reconocimientos
/// con toda la información que entregó el equipo. Misma pantalla que la
/// página ANPR del panel web (wwwroot\anpr.js): filtros por equipo, patente y
/// rango de horas, lista a la izquierda y ficha a la derecha.
///
/// Las lecturas llegan empujadas por el hub (no hay sondeo): al abrir la
/// pantalla se carga el historial reciente y desde ahí cada evento nuevo se
/// inserta arriba. Con "Seguir en vivo" activo, la ficha de detalle salta
/// sola al último — es el modo de operación en una caseta de control.
/// </summary>
public sealed partial class LprViewModel : ObservableObject
{
    /// <summary>Cantidad de lecturas de la lista por omisión (la misma del panel web).</summary>
    private const int DefaultTake = 100;

    /// <summary>Formatos aceptados en "Desde" y "Hasta" (hora del equipo).</summary>
    private static readonly string[] DateTimeFormats =
        ["d-M-yyyy H:mm", "d-M-yyyy H:mm:ss", "d/M/yyyy H:mm", "d/M/yyyy H:mm:ss"];
    private static readonly string[] DateFormats = ["d-M-yyyy", "d/M/yyyy"];

    private readonly ApiClient _api;
    private readonly ClientSettings _settings;
    private CancellationTokenSource? _detailLoad;

    /// <summary>La selección la está moviendo el propio módulo (búsqueda o
    /// lectura nueva), no el operador: no debe apagar "Seguir en vivo".</summary>
    private bool _autoSelecting;

    /// <summary>Rango de la última búsqueda: una lectura en vivo fuera de él no se cuela en la lista.</summary>
    private DateTime? _from;
    private DateTime? _to;

    /// <summary>Aviso que dejó la última búsqueda (fecha mal escrita, sin
    /// resultados): la siguiente lo retira, pero no los avisos de las fuentes.</summary>
    private string? _searchNotice;

    /// <summary>Archivo guardado: el shell lo avisa con la misma notificación
    /// flotante que las capturas del vivo.</summary>
    public event Action<string, string, string>? MediaSaved;

    public LprViewModel(ApiClient api, VmsHubClient hub, ClientSettings settings)
    {
        _api = api;
        _settings = settings;

        Events.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasEvents));
            OnPropertyChanged(nameof(CountText));
        };
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

    /// <summary>Opciones del filtro "Equipo": "Todos" y cada fuente.</summary>
    public ObservableCollection<AnprDeviceOption> DeviceOptions { get; } = [AnprDeviceOption.All];

    /// <summary>Fuentes encendidas, con su semáforo, para la cabecera.</summary>
    public ObservableCollection<AnprSourceChip> SourceChips { get; } = [];

    /// <summary>Cantidades del selector "Mostrar".</summary>
    public IReadOnlyList<int> TakeOptions { get; } = [50, 100, 200, 500];

    [ObservableProperty] private PlateEventViewModel? _selected;

    /// <summary>La ficha salta sola al reconocimiento recién llegado.</summary>
    [ObservableProperty] private bool _followLive = true;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isSourcesPanelOpen;

    /// <summary>Filtro por equipo ("Todos" = sin filtro).</summary>
    [ObservableProperty] private AnprDeviceOption? _deviceFilter = AnprDeviceOption.All;

    /// <summary>Filtro por patente (búsqueda parcial, sin formato).</summary>
    [ObservableProperty] private string _plateFilter = "";

    /// <summary>Rango en hora del equipo, como se escribe: dd-mm-aaaa hh:mm (o solo la fecha).</summary>
    [ObservableProperty] private string _fromFilter = "";
    [ObservableProperty] private string _toFilter = "";

    /// <summary>Cuántas lecturas trae la búsqueda y retiene la lista.</summary>
    [ObservableProperty] private int _take = DefaultTake;

    /// <summary>Cantidad de fuentes con el canal de eventos realmente abierto.</summary>
    [ObservableProperty] private int _liveSourceCount;

    /// <summary>Encender o apagar fuentes exige el permiso de configurar fuentes de video.</summary>
    public bool IsAdmin => Services.PermissionScope.Current.Has(Core.Domain.Permissions.DevicesManage);

    public bool HasEvents => Events.Count > 0;

    /// <summary>Ninguna cámara está encendida como fuente (la cabecera lo avisa en vez de los semáforos).</summary>
    public bool HasNoActiveSources => SourceChips.Count == 0;

    /// <summary>Contador de la lista ("100 lecturas (últimas 100)").</summary>
    public string CountText
    {
        get
        {
            int n = Events.Count;
            return $"{n} lectura{(n == 1 ? "" : "s")}{(n >= Take ? $" (últimas {Take})" : "")}";
        }
    }

    partial void OnTakeChanged(int value) => OnPropertyChanged(nameof(CountText));

    partial void OnSelectedChanged(PlateEventViewModel? oldValue, PlateEventViewModel? newValue)
    {
        // La ficha anterior suelta sus imágenes grandes: solo la seleccionada las tiene.
        if (oldValue is not null && !ReferenceEquals(oldValue, newValue)) oldValue.ReleaseDetail();
    }

    partial void OnSelectedChanged(PlateEventViewModel? value)
    {
        // Elegir a mano una lectura antigua suspende el seguimiento en vivo:
        // si no, la próxima patente que llegue le quita la ficha al operador.
        if (!_autoSelecting && FollowLive && value is not null && Events.Count > 0 && !ReferenceEquals(value, Events[0]))
            FollowLive = false;

        _detailLoad?.Cancel();
        if (value is null) return;
        _detailLoad = new CancellationTokenSource();
        var token = _detailLoad.Token;
        _ = value.LoadDetailAsync(token);
    }

    private void SelectAutomatically(PlateEventViewModel? item)
    {
        _autoSelecting = true;
        try { Selected = item; }
        finally { _autoSelecting = false; }
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

            DeviceOptions.Clear();
            DeviceOptions.Add(AnprDeviceOption.All);
            foreach (var source in sources)
                DeviceOptions.Add(AnprDeviceOption.From(source));
            DeviceFilter = DeviceOptions.FirstOrDefault(o => o.DeviceId == keep) ?? AnprDeviceOption.All;

            UpdateSourceChips(sources);

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

    private void UpdateSourceChips(IReadOnlyCollection<AnprSourceDto> sources)
    {
        SourceChips.Clear();
        foreach (var source in sources.Where(s => s.Enabled))
            SourceChips.Add(AnprSourceChip.From(source));
        LiveSourceCount = sources.Count(s => s.Live);
        OnPropertyChanged(nameof(HasNoActiveSources));
    }

    private static string? FirstSourceError(IEnumerable<AnprSourceDto> sources) =>
        sources.FirstOrDefault(s => s.Enabled && s.LastError is { Length: > 0 })?.LastError;

    /// <summary>Recarga la lista aplicando los filtros vigentes.</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        if (_searchNotice is not null && StatusMessage == _searchNotice)
            StatusMessage = "";
        _searchNotice = null;

        if (!TryParseFilterDate(FromFilter, endOfDay: false, out var from))
        {
            ShowSearchNotice("“Desde” no es una fecha válida: escríbala como dd-mm-aaaa hh:mm (o solo dd-mm-aaaa).");
            return;
        }
        if (!TryParseFilterDate(ToFilter, endOfDay: true, out var to))
        {
            ShowSearchNotice("“Hasta” no es una fecha válida: escríbala como dd-mm-aaaa hh:mm (o solo dd-mm-aaaa).");
            return;
        }
        _from = from;
        _to = to;

        IsBusy = true;
        try
        {
            var events = await _api.GetPlateEventsAsync(DeviceFilter?.DeviceId, PlateFilter, from, to, Take);
            Events.Clear();
            foreach (var dto in events)
                Events.Add(Create(dto));
            SelectAutomatically(Events.FirstOrDefault());
            bool filtered = PlateFilter.Trim().Length > 0 || DeviceFilter?.DeviceId is not null || from is not null || to is not null;
            if (events.Count == 0 && filtered)
                ShowSearchNotice("No hay lecturas que coincidan con los filtros.");
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

    private void ShowSearchNotice(string message) => StatusMessage = _searchNotice = message;

    /// <summary>
    /// "Desde"/"Hasta" en hora del equipo. Vacío = sin límite; solo la fecha
    /// toma el día entero (desde las 00:00, hasta las 23:59:59).
    /// </summary>
    private static bool TryParseFilterDate(string text, bool endOfDay, out DateTime? value)
    {
        value = null;
        string trimmed = text.Trim();
        if (trimmed.Length == 0) return true;
        if (DateTime.TryParseExact(trimmed, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment))
        {
            value = moment;
            return true;
        }
        if (DateTime.TryParseExact(trimmed, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            value = endOfDay ? day.Date.AddDays(1).AddSeconds(-1) : day.Date;
            return true;
        }
        return false;
    }

    [RelayCommand]
    private async Task ClearFiltersAsync()
    {
        PlateFilter = "";
        FromFilter = "";
        ToFilter = "";
        DeviceFilter = AnprDeviceOption.All;
        Take = DefaultTake;
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
        if (DeviceFilter?.DeviceId is { } deviceId && dto.DeviceId != deviceId) return;
        if (_from is { } from && dto.CapturedAt < from) return;
        if (_to is { } to && dto.CapturedAt > to) return;
        if (PlateFilter.Trim().Length > 0)
        {
            string needle = new string(PlateFilter.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (needle.Length > 0 && !dto.PlateNumber.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return;
        }

        var item = Create(dto);
        Events.Insert(0, item);
        // La lista no crece sin fin: se recorta a la cantidad pedida.
        while (Events.Count > Take)
            Events.RemoveAt(Events.Count - 1);

        if (FollowLive || Selected is null)
            SelectAutomatically(item);
        StatusMessage = "";
        // Un evento prueba que hay al menos una fuente viva. Solo se consulta
        // cuando el contador dice lo contrario: con tránsito pesado, pedirlo
        // en cada lectura sería una llamada por vehículo.
        if (LiveSourceCount == 0)
            _ = RefreshLiveCountAsync();
    }

    /// <summary>Corrige los semáforos de las fuentes tras recibir un evento.</summary>
    private async Task RefreshLiveCountAsync()
    {
        try
        {
            UpdateSourceChips(await _api.GetAnprSourcesAsync());
        }
        catch (ApiException) { /* los semáforos se corrigen en la próxima recarga */ }
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

    /// <summary>
    /// Descarga el ZIP del reconocimiento (fotos + JSON con todos sus datos)
    /// a la carpeta de capturas, como el botón del panel web.
    /// </summary>
    [RelayCommand]
    private async Task DownloadZipAsync()
    {
        if (Selected is not { } item) return;
        try
        {
            var (content, fileName) = await _api.GetPlateEventZipAsync(item.Id);
            string folder = _settings.EffectiveSnapshotFolder;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, Path.GetFileName(fileName));
            await File.WriteAllBytesAsync(path, content);
            MediaSaved?.Invoke("Reconocimiento descargado", "", path);
        }
        catch (ApiException ex)
        {
            StatusMessage = $"No se pudo descargar el reconocimiento: {ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"No se pudo guardar el ZIP: {ex.Message}";
        }
    }
}

/// <summary>Opción del filtro "Equipo" (<see cref="DeviceId"/> null = todos).</summary>
public sealed record AnprDeviceOption(int? DeviceId, string Label)
{
    public static readonly AnprDeviceOption All = new(null, "Todos");

    /// <summary>Igual que el panel web: se aclara si la fuente está apagada o esperando licencia.</summary>
    public static AnprDeviceOption From(AnprSourceDto source) => new(source.DeviceId,
        source.DeviceName + (source.DisabledByLicense ? " (esperando cupo de licencia)"
            : source.Enabled ? "" : " (inactiva)"));
}

/// <summary>Fuente encendida en la cabecera: nombre y semáforo del canal de eventos.</summary>
public sealed record AnprSourceChip(string Name, bool Live, string Hint)
{
    public static AnprSourceChip From(AnprSourceDto source)
    {
        string hint = source.Live
            ? "Recibiendo lecturas"
            : source.LastError is { Length: > 0 } error ? error : "Sin conexión de eventos con el equipo";
        if (source.LastEventAt is { } last)
            hint += $" · última lectura {last.ToLocalTime():dd-MM-yyyy HH:mm:ss}";
        return new AnprSourceChip(source.DeviceName, source.Live, hint);
    }
}
