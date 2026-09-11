using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Client.ViewModels;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Ventana de alarma: el hecho a la izquierda (qué lo disparó, cuándo, con qué
/// severidad), las fotos que capturó la automatización a la derecha, lo que
/// hizo el sistema en la otra pestaña, y abajo el acuse de recibo.
///
/// Es una sola ventana para todas las alertas pendientes: se navega entre
/// ellas con el paginador, igual que en una central de monitoreo. Cerrarla NO
/// confirma nada — la alerta sigue pendiente en el servidor hasta que alguien
/// se dé por enterado.
/// </summary>
public partial class AlertWindow : Window
{
    private static AlertWindow? _current;

    private readonly ApiClient _api;
    private readonly ClientSettings _settings;
    /// <summary>Resuelve el canal del árbol de dispositivos para poder abrir su video.</summary>
    private readonly Func<int, ChannelNode?> _findChannel;
    /// <summary>Cuadros de video de la ventana (uno por cámara vinculada; mismo motor que la Vista en vivo).</summary>
    private readonly List<VideoCellViewModel> _cells = [];
    /// <summary>Alarma sonora en curso, para poder callarla al confirmar o cerrar.</summary>
    private readonly AlertSoundPlayer _sound;
    /// <summary>Cámaras de la alerta que se está mostrando.</summary>
    private List<ChannelNode> _cameras = [];
    private readonly List<WorkflowAlertDto> _alerts = [];
    private readonly Dictionary<string, BitmapImage> _images = new(StringComparer.OrdinalIgnoreCase);
    private int _index;
    private List<string> _photos = [];
    private int _photo;

    private AlertWindow(ApiClient api, ClientSettings settings, AlertSoundPlayer sound,
        Func<int, ChannelNode?> findChannel)
    {
        _api = api;
        _settings = settings;
        _sound = sound;
        _findChannel = findChannel;
        InitializeComponent();
        StateChanged += (_, _) => MaxGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        Closed += (_, _) =>
        {
            if (_current == this) _current = null;
            // Cerrar la ventana calla la alarma y suelta las concesiones de
            // video: nada puede quedar sonando ni consumiendo sesiones RTSP.
            _sound.Stop();
            foreach (var cell in _cells) cell.Dispose();
            _cells.Clear();
        };
    }

    /// <summary>Columnas de la grilla de video (la enlaza el UniformGrid del XAML).</summary>
    public static readonly DependencyProperty VideoColumnsProperty =
        DependencyProperty.Register(nameof(VideoColumns), typeof(int), typeof(AlertWindow), new PropertyMetadata(1));

    public int VideoColumns
    {
        get => (int)GetValue(VideoColumnsProperty);
        set => SetValue(VideoColumnsProperty, value);
    }

    private WorkflowAlertDto? Current => _index >= 0 && _index < _alerts.Count ? _alerts[_index] : null;

    /// <summary>
    /// Muestra las alertas (crea la ventana o reutiliza la abierta) y deja
    /// arriba la indicada, o la más reciente sin confirmar.
    /// </summary>
    public static void Show(Window? owner, ApiClient api, ClientSettings settings, AlertSoundPlayer sound,
        Func<int, ChannelNode?> findChannel, IEnumerable<WorkflowAlertDto> alerts, long? focus = null)
    {
        var window = _current ??= new AlertWindow(api, settings, sound, findChannel);
        if (owner is not null && !ReferenceEquals(window.Owner, owner) && owner.IsLoaded)
        {
            try { window.Owner = owner; } catch (InvalidOperationException) { /* ya visible con otro dueño */ }
        }

        foreach (var alert in alerts)
        {
            int existing = window._alerts.FindIndex(a => a.Id == alert.Id);
            if (existing >= 0) window._alerts[existing] = alert;
            else window._alerts.Add(alert);
        }
        window._alerts.Sort((a, b) => a.RaisedAt.CompareTo(b.RaisedAt));

        long? target = focus ?? window.Current?.Id;
        int position = target is { } id ? window._alerts.FindIndex(a => a.Id == id) : -1;
        window._index = position >= 0 ? position : window._alerts.Count - 1;

        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        _ = window.RenderAsync();
    }

    /// <summary>Otro puesto (u otro operador) confirmó la alerta: se refleja aquí.</summary>
    public static void Update(WorkflowAlertDto alert)
    {
        if (_current is not { } window) return;
        int position = window._alerts.FindIndex(a => a.Id == alert.Id);
        if (position < 0) return;
        window._alerts[position] = alert;
        if (position == window._index) _ = window.RenderAsync();
        else window.RenderPager();
    }

    /// <summary>Cierra la ventana si ya no queda ninguna alerta sin confirmar.</summary>
    public static void CloseIfDone()
    {
        if (_current is { } window && window._alerts.All(a => !a.Pending)) window.Close();
    }

    // ------------------------------------------------------------------
    // Pintado
    // ------------------------------------------------------------------

    private async Task RenderAsync()
    {
        if (Current is not { } alert)
        {
            Close();
            return;
        }

        bool critical = alert.Severity != AlarmSeverity.Info;
        SeverityGlyph.Text = critical ? "\uE814" : "\uE783";
        SeverityGlyph.Foreground = (Brush)FindResource(critical ? "DangerBrush" : "AccentBrush");

        TitleText.Text = alert.Title;
        SubtitleText.Text = $"{alert.WorkflowName} · {alert.RaisedAt.ToLocalTime():dd-MM-yyyy HH:mm:ss}";
        WorkflowText.Text = alert.WorkflowName;
        TriggerText.Text = alert.TriggerSummary.Length > 0 ? alert.TriggerSummary : "—";
        SeverityText.Text = alert.Severity switch
        {
            AlarmSeverity.Critical => "Crítica",
            AlarmSeverity.Warning => "Advertencia",
            _ => "Informativa",
        };
        RaisedText.Text = alert.RaisedAt.ToLocalTime().ToString("dd-MM-yyyy HH:mm:ss");
        RunText.Text = alert.RunId is { } run ? $"N° {run}" : "—";
        SoundText.Text = alert.Sound is { Length: > 0 } sound
            ? (sound == WorkflowNotificationDto.SystemSoundName ? "Pitido del sistema" : sound) +
              (alert.SoundRepeat > 1 ? $" (×{alert.SoundRepeat})" : "")
            : "Sin sonido";
        MessageText.Text = alert.Message;

        RenderAck(alert);
        RenderPager();
        RenderSteps(alert);
        RenderCameras(alert);
        MuteButton.IsEnabled = true;
        MuteButton.Content = "Silenciar";
        await RenderPhotosAsync(alert);
    }

    // ------------------------------------------------------------------
    // Video en vivo de la cámara vinculada
    // ------------------------------------------------------------------

    /// <summary>
    /// Carga las cámaras de la alerta en el selector. Son las que capturaron la
    /// foto (o las que indique la acción de aviso): al operador le sirve ver el
    /// vivo del sector, no solo la imagen del instante.
    /// </summary>
    private void RenderCameras(WorkflowAlertDto alert)
    {
        _cameras = alert.ChannelIds.Select(_findChannel).Where(n => n is not null).Select(n => n!).ToList();

        // Grilla: 1 cámara ocupa todo, 2 van lado a lado, 3-4 en 2×2, y así.
        VideoColumns = _cameras.Count switch
        {
            <= 1 => 1,
            2 => 2,
            <= 4 => 2,
            <= 9 => 3,
            _ => 4,
        };

        // Un cuadro por cámara; los sobrantes se sueltan.
        while (_cells.Count > _cameras.Count)
        {
            _cells[^1].Dispose();
            _cells.RemoveAt(_cells.Count - 1);
        }
        while (_cells.Count < _cameras.Count)
            _cells.Add(new VideoCellViewModel(_api, _settings));

        VideoGrid.ItemsSource = null;
        VideoGrid.ItemsSource = _cells;

        NoVideoText.Visibility = _cameras.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoVideoText.Text = alert.ChannelIds.Count == 0
            ? "Esta alerta no tiene cámara vinculada."
            : "Las cámaras vinculadas ya no están disponibles en este cliente.";
        _ = OpenVideoAsync();
    }

    /// <summary>Abre el vivo de todas las cámaras si la pestaña está a la vista; si no, las suelta.</summary>
    private async Task OpenVideoAsync()
    {
        bool visible = Tabs.SelectedIndex == 0 && IsVisible;
        if (!visible)
        {
            foreach (var cell in _cells) cell.Clear();
            return;
        }
        for (int i = 0; i < _cells.Count && i < _cameras.Count; i++)
        {
            try
            {
                // Perfil secundario: es una ventana de aviso, no la Vista en
                // vivo; pesa menos en el equipo y basta para ver qué pasa.
                await _cells[i].OpenAsync(_cameras[i], StreamProfile.Sub);
            }
            catch (Exception)
            {
                // El cuadro muestra su propio estado de error; la ventana sigue.
            }
        }
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        _ = OpenVideoAsync();
    }

    private void OnMute(object sender, RoutedEventArgs e)
    {
        _sound.Stop();
        MuteButton.IsEnabled = false;
        MuteButton.Content = "Silenciada";
    }

    private void RenderAck(WorkflowAlertDto alert)
    {
        if (alert.AcknowledgedAt is { } at)
        {
            AckStateText.Text = "Confirmada";
            AckStateText.Foreground = (Brush)FindResource("OkBrush");
            AckDetailText.Text = $"Por {alert.AcknowledgedBy} el {at.ToLocalTime():dd-MM-yyyy HH:mm:ss} " +
                                 $"({alert.ResponseSeconds} s después de emitida), desde " +
                                 (alert.AcknowledgedFrom == "client" ? "el cliente de escritorio." : "el panel web.");
            AckButton.Visibility = Visibility.Collapsed;
            AckHint.Text = "Esta alerta ya tiene acuse de recibo; el registro no se modifica.";
        }
        else if (!alert.RequiresAck)
        {
            AckStateText.Text = "Informativa";
            AckStateText.Foreground = (Brush)FindResource("MutedBrush");
            AckDetailText.Text = "Este aviso no exige confirmación.";
            AckButton.Visibility = Visibility.Collapsed;
            AckHint.Text = "";
        }
        else
        {
            AckStateText.Text = "PENDIENTE";
            AckStateText.Foreground = (Brush)FindResource("DangerBrush");
            AckDetailText.Text = "Nadie se ha dado por enterado todavía.";
            AckButton.Visibility = Visibility.Visible;
            AckButton.IsEnabled = true;
            AckButton.Content = "Enterado";
            AckHint.Text = "Queda registrado quién confirma, a qué hora y desde dónde.";
        }
    }

    private void RenderPager()
    {
        int pending = _alerts.Count(a => a.Pending);
        PagerText.Text = $"{_index + 1}/{_alerts.Count}" + (pending > 0 ? $"  ·  {pending} sin confirmar" : "");
        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < _alerts.Count - 1;
    }

    /// <summary>Pestaña "qué hizo el sistema": los pasos de la ejecución que generó la alerta.</summary>
    private async void RenderSteps(WorkflowAlertDto alert)
    {
        StepsPanel.Children.Clear();
        if (alert.RunId is not { } runId)
        {
            StepsPanel.Children.Add(Muted("Esta alerta no está asociada a una ejecución."));
            return;
        }
        StepsPanel.Children.Add(Muted("Cargando…"));
        var run = await _api.GetWorkflowRunAsync(runId);
        StepsPanel.Children.Clear();
        if (run is null)
        {
            StepsPanel.Children.Add(Muted("No se pudo leer el detalle de la ejecución."));
            return;
        }
        foreach (var step in run.Steps)
        {
            var box = new Border
            {
                Background = (Brush)FindResource("Panel2Brush"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8),
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = $"{step.Order}. {step.Label}   {(step.Success ? "✓" : "✕")}   {step.ElapsedMs / 1000.0:0.0} s",
                FontWeight = FontWeights.SemiBold,
                FontSize = 12.5,
                Foreground = (Brush)FindResource(step.Success ? "OkBrush" : "DangerBrush"),
            });
            stack.Children.Add(new TextBlock
            {
                Text = step.Detail ?? "",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = (Brush)FindResource("TextBrush"),
            });
            box.Child = stack;
            StepsPanel.Children.Add(box);
        }
    }

    private TextBlock Muted(string text) => new()
    {
        Text = text,
        Foreground = (Brush)FindResource("MutedBrush"),
        FontSize = 12.5,
        TextWrapping = TextWrapping.Wrap,
    };

    private async Task RenderPhotosAsync(WorkflowAlertDto alert)
    {
        _photos = alert.ImagePaths.Count > 0
            ? [.. alert.ImagePaths]
            : alert.ImagePath is { Length: > 0 } one ? [one] : [];
        _photo = _photos.Count - 1;   // la última es la más cercana al hecho
        Thumbnails.Items.Clear();

        if (_photos.Count == 0)
        {
            MainImage.Source = null;
            NoImageText.Visibility = Visibility.Visible;
            ImageCounter.Text = "";
            return;
        }
        NoImageText.Visibility = Visibility.Collapsed;

        for (int i = 0; i < _photos.Count; i++)
        {
            int position = i;
            var thumb = new Button
            {
                Width = 132,
                Height = 76,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(0),
                BorderThickness = new Thickness(2),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                Background = Brushes.Black,
                Cursor = System.Windows.Input.Cursors.Hand,
                Content = new Image { Stretch = Stretch.UniformToFill },
                Tag = position,
            };
            thumb.Click += async (_, _) => { _photo = position; await ShowPhotoAsync(); };
            Thumbnails.Items.Add(thumb);
        }
        await ShowPhotoAsync();
    }

    private async Task ShowPhotoAsync()
    {
        if (_photos.Count == 0) return;
        _photo = Math.Clamp(_photo, 0, _photos.Count - 1);
        ImageCounter.Text = $"{_photo + 1}/{_photos.Count}";

        MainImage.Source = await LoadAsync(_photos[_photo]);
        for (int i = 0; i < Thumbnails.Items.Count; i++)
        {
            if (Thumbnails.Items[i] is not Button button) continue;
            button.BorderBrush = (Brush)FindResource(i == _photo ? "AccentBrush" : "BorderBrush");
            if (button.Content is Image { Source: null } image)
                image.Source = await LoadAsync(_photos[i]);
        }
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => _ = OpenViewerAsync();

    private void OnMainImageClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) _ = OpenViewerAsync();
    }

    /// <summary>
    /// Abre todas las fotos de la alerta en el visor a pantalla completa
    /// (zoom digital con la rueda, arrastre para mover), partiendo por la que
    /// se está mirando. Las fotos ya están descargadas y cacheadas.
    /// </summary>
    private async Task OpenViewerAsync()
    {
        if (_photos.Count == 0) return;
        var images = new List<BitmapSource?>(_photos.Count);
        var captions = new List<string>(_photos.Count);
        var alert = _alerts.Count > 0 ? _alerts[Math.Clamp(_index, 0, _alerts.Count - 1)] : null;
        for (int i = 0; i < _photos.Count; i++)
        {
            images.Add(await LoadAsync(_photos[i]));
            string name = Path.GetFileNameWithoutExtension(_photos[i]);
            captions.Add(alert is null ? name : $"{alert.Title} · {alert.RaisedAt.ToLocalTime():dd-MM-yyyy HH:mm:ss} · {name}");
        }
        ImageViewerWindow.Open(images, captions, _photo, this);
    }

    /// <summary>Descarga (y cachea) una foto de la alerta.</summary>
    private async Task<BitmapImage?> LoadAsync(string path)
    {
        if (_images.TryGetValue(path, out var cached)) return cached;
        byte[]? bytes = await _api.GetWorkflowImageAsync(path);
        if (bytes is null or { Length: 0 }) return null;
        try
        {
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            _images[path] = bitmap;
            return bitmap;
        }
        catch (Exception)
        {
            return null;   // imagen ilegible: la ventana sigue sirviendo igual
        }
    }

    // ------------------------------------------------------------------
    // Acciones
    // ------------------------------------------------------------------

    private async void OnAcknowledge(object sender, RoutedEventArgs e)
    {
        if (Current is not { } alert) return;
        AckButton.IsEnabled = false;
        AckButton.Content = "Registrando…";
        _sound.Stop();
        try
        {
            var updated = await _api.AcknowledgeAlertAsync(alert.Id);
            if (updated is not null) _alerts[_index] = updated;

            // Saltar a la siguiente pendiente; si no queda ninguna, cerrar.
            int next = _alerts.FindIndex(a => a.Pending);
            if (next < 0)
            {
                Close();
                return;
            }
            _index = next;
            await RenderAsync();
        }
        catch (Exception ex)
        {
            // Sin registro no hay acuse: la alerta sigue pendiente y a la vista.
            AckButton.IsEnabled = true;
            AckButton.Content = "Reintentar";
            AckHint.Text = $"No se pudo registrar la confirmación: {ex.Message}";
        }
    }

    private async void OnPrevious(object sender, RoutedEventArgs e)
    {
        if (_index <= 0) return;
        _index--;
        await RenderAsync();
    }

    private async void OnNext(object sender, RoutedEventArgs e)
    {
        if (_index >= _alerts.Count - 1) return;
        _index++;
        await RenderAsync();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
