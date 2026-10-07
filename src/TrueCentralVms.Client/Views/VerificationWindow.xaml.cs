using System.Windows;
using System.Windows.Media;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Client.ViewModels;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Verificación de un aviso: cuando una zona, un área, un cerco o una puerta
/// avisa algo y su ficha en Recursos tiene consignas o cámaras asociadas, esta
/// ventana muestra qué hacer y el vivo de esas cámaras (la principal primero).
///
/// Es una sola ventana para todos los avisos: se navega con el paginador, y un
/// recurso que repite su aviso dentro del mismo minuto actualiza su entrada en
/// vez de sumar otra (una puerta mantenida abierta avisa varias veces).
/// Cerrarla no cambia nada en el equipo ni en el servidor.
/// </summary>
public partial class VerificationWindow : Window
{
    /// <summary>Un aviso con el resumen del recurso que lo dio.</summary>
    public sealed record Item(ResourceBriefingDto Briefing, string Title, string Detail, bool Critical, DateTime At)
    {
        /// <summary>Cuándo se mostró en este puesto (la hora del evento la pone el equipo y puede venir corrida).</summary>
        public DateTime ShownAt { get; init; } = DateTime.UtcNow;
    }

    private const int MaxItems = 30;
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(1);
    /// <summary>Una alerta de automatización reemplaza las verificaciones de su recurso de este lapso.</summary>
    private static readonly TimeSpan SupersedeWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Clave del recurso que originó el aviso ("Zone:3"), la misma que traen las
    /// alertas de automatizaciones (puede no ser el de las consignas: una zona
    /// sin ficha usa las de su área).
    /// </summary>
    public static string Key(ResourceBriefingDto briefing) => briefing.OriginKey ?? $"{briefing.Kind}:{briefing.Id}";

    private static VerificationWindow? _current;

    private readonly ApiClient _api;
    private readonly ClientSettings _settings;
    private readonly Func<int, ChannelNode?> _findChannel;
    private readonly Func<string, IReadOnlyList<ChannelNode>, Task> _openInLive;
    private readonly List<VideoCellViewModel> _cells = [];
    private readonly List<Item> _items = [];
    private List<ChannelNode> _cameras = [];
    private int _index;

    private VerificationWindow(ApiClient api, ClientSettings settings, Func<int, ChannelNode?> findChannel,
        Func<string, IReadOnlyList<ChannelNode>, Task> openInLive)
    {
        _api = api;
        _settings = settings;
        _findChannel = findChannel;
        _openInLive = openInLive;
        InitializeComponent();
        StateChanged += (_, _) => MaxGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        Closed += (_, _) =>
        {
            if (_current == this) _current = null;
            // Nada puede quedar consumiendo sesiones de video al cerrar.
            foreach (var cell in _cells) cell.Dispose();
            _cells.Clear();
        };
    }

    /// <summary>Columnas de la grilla de video (la enlaza el UniformGrid del XAML).</summary>
    public static readonly DependencyProperty VideoColumnsProperty =
        DependencyProperty.Register(nameof(VideoColumns), typeof(int), typeof(VerificationWindow), new PropertyMetadata(1));

    public int VideoColumns
    {
        get => (int)GetValue(VideoColumnsProperty);
        set => SetValue(VideoColumnsProperty, value);
    }

    /// <summary>Suma el aviso (o actualiza el del mismo recurso del último minuto) y lo deja a la vista.</summary>
    public static void Show(Window? owner, ApiClient api, ClientSettings settings, Func<int, ChannelNode?> findChannel,
        Func<string, IReadOnlyList<ChannelNode>, Task> openInLive, Item item)
    {
        var window = _current ??= new VerificationWindow(api, settings, findChannel, openInLive);
        if (owner is not null && !ReferenceEquals(window.Owner, owner) && owner.IsLoaded)
        {
            try { window.Owner = owner; } catch (InvalidOperationException) { /* ya visible con otro dueño */ }
        }

        // Mismo ORIGEN (no mismo resumen): dos zonas de un área sin ficha comparten consignas, no evento.
        int repeated = window._items.FindLastIndex(i =>
            Key(i.Briefing) == Key(item.Briefing) && (item.At - i.At).Duration() < RepeatWindow);
        if (repeated >= 0)
        {
            window._items[repeated] = item;
            window._index = repeated;
        }
        else
        {
            window._items.Add(item);
            while (window._items.Count > MaxItems) window._items.RemoveAt(0);
            window._index = window._items.Count - 1;
        }

        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.Render(reopenVideo: repeated < 0 || !window._cells.Any(c => !c.IsEmpty));
    }

    /// <summary>
    /// Llegó la alerta de una automatización por ese mismo recurso: su ventana
    /// ya trae las consignas y las cámaras, así que las verificaciones
    /// recientes del recurso salen de aquí (si no queda ninguna, se cierra).
    /// </summary>
    public static void Supersede(string resourceKey)
    {
        if (_current is not { } window) return;
        var now = DateTime.UtcNow;
        var shown = window.Current;
        if (window._items.RemoveAll(i => Key(i.Briefing) == resourceKey && now - i.ShownAt < SupersedeWindow) == 0)
            return;
        if (window._items.Count == 0)
        {
            window.Close();
            return;
        }
        int position = shown is null ? -1 : window._items.IndexOf(shown);
        window._index = position >= 0 ? position : window._items.Count - 1;
        // Si sigue a la vista el mismo aviso, su video no se toca.
        window.Render(reopenVideo: position < 0);
    }

    private Item? Current => _index >= 0 && _index < _items.Count ? _items[_index] : null;

    private static string KindLabel(ResourceKind kind) => kind switch
    {
        ResourceKind.Camera => "cámara",
        ResourceKind.Door => "puerta",
        ResourceKind.Partition => "área de alarma",
        ResourceKind.Zone => "zona de alarma",
        ResourceKind.Fence => "cerco eléctrico",
        ResourceKind.Speaker => "parlante",
        ResourceKind.Intercom => "citófono",
        _ => "recurso",
    };

    private void Render(bool reopenVideo = true)
    {
        if (Current is not { } item)
        {
            Close();
            return;
        }
        var b = item.Briefing;
        SeverityGlyph.Text = item.Critical ? "\uE814" : "\uE783";
        SeverityGlyph.Foreground = (Brush)FindResource(item.Critical ? "DangerBrush" : "AccentBrush");
        TitleText.Text = item.Title;
        SubtitleText.Text = $"{b.Name} · {b.LocationPath ?? "sin ubicación"} · {item.At.ToLocalTime():dd-MM-yyyy HH:mm:ss}";

        bool hasInstructions = !string.IsNullOrWhiteSpace(b.Instructions);
        InstructionsText.Text = hasInstructions
            ? b.Instructions
            : "Este recurso no tiene consignas. Se escriben en su ficha (panel web → Recursos).";
        InstructionsText.Foreground = (Brush)FindResource(hasInstructions ? "TextBrush" : "MutedBrush");
        ResourceText.Text = $"{b.Name} ({KindLabel(b.Kind)})";
        LocationText.Text = b.LocationPath ?? "Sin ubicación asignada";
        DescriptionPanel.Visibility = string.IsNullOrWhiteSpace(b.Description) ? Visibility.Collapsed : Visibility.Visible;
        DescriptionText.Text = b.Description ?? "";
        EventText.Text = item.Detail;

        RenderPager();
        if (reopenVideo) RenderCameras(b);
    }

    private void RenderPager()
    {
        PagerText.Text = _items.Count > 1 ? $"Aviso {_index + 1} de {_items.Count}" : "";
        PrevButton.Visibility = NextButton.Visibility = _items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < _items.Count - 1;
    }

    /// <summary>Un cuadro por cámara asociada que este cliente conoce (las deshabilitadas no están en su árbol).</summary>
    private void RenderCameras(ResourceBriefingDto briefing)
    {
        _cameras = briefing.Cameras.Select(c => _findChannel(c.ChannelId)).Where(n => n is not null).Select(n => n!).ToList();
        VideoColumns = _cameras.Count switch
        {
            <= 1 => 1,
            2 => 2,
            <= 4 => 2,
            <= 9 => 3,
            _ => 4,
        };
        while (_cells.Count > _cameras.Count)
        {
            _cells[^1].Dispose();
            _cells.RemoveAt(_cells.Count - 1);
        }
        while (_cells.Count < _cameras.Count)
            _cells.Add(new VideoCellViewModel(_api, _settings) { Index = _cells.Count + 1 });
        VideoGrid.ItemsSource = null;
        VideoGrid.ItemsSource = _cells;

        NoVideoText.Visibility = _cameras.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoVideoText.Text = briefing.Cameras.Count == 0
            ? "Este recurso no tiene cámaras asociadas. Se eligen en su ficha (panel web → Recursos)."
            : "Las cámaras asociadas no están disponibles en este cliente.";
        OpenLiveButton.IsEnabled = _cameras.Count > 0;
        _ = OpenVideoAsync();
    }

    private async Task OpenVideoAsync()
    {
        for (int i = 0; i < _cells.Count && i < _cameras.Count; i++)
        {
            try
            {
                // Secundario: es una ventana de verificación; pesa menos en el
                // equipo y basta para ver qué pasa (la Vista en vivo da el principal).
                await _cells[i].OpenAsync(_cameras[i], StreamProfile.Sub);
            }
            catch (Exception)
            {
                // El cuadro muestra su propio error; la ventana sigue.
            }
        }
    }

    private async void OnOpenLive(object sender, RoutedEventArgs e)
    {
        if (Current is not { } item || _cameras.Count == 0) return;
        try { await _openInLive(item.Briefing.Name, _cameras); }
        catch (Exception) { /* la Vista en vivo informa lo que no pudo abrir */ }
    }

    private void OnPrevious(object sender, RoutedEventArgs e)
    {
        if (_index <= 0) return;
        _index--;
        Render();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_index >= _items.Count - 1) return;
        _index++;
        Render();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
