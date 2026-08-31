using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Client.ViewModels;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Módulo Muro de video: grilla de monitores del muro, lista de cámaras del
/// inventario y operación en vivo contra el decodificador (asignar, cambiar la
/// división, pantalla completa, agrupar, ventanas flotantes, layouts guardados
/// y proyección de la pantalla del operador).
///
/// Portado del producto vwcontroller: conserva su interfaz construida en código
/// (la grilla del muro es dinámica) y su modelo de interacción, pero las
/// cámaras salen del inventario del VMS y la asignación de una ventana viaja
/// como un único ChannelId.
/// </summary>
public partial class WallView : UserControl
{
    /// <summary>Formato de arrastre de un canal del inventario: el id del canal.</summary>
    private const string DragFormat = "clr-truecentral-channel";

    private ApiClient _api = null!;
    private VmsHubClient _hub = null!;
    private bool _initialized;

    private List<WallDto> _walls = new();
    private List<CameraChannel> _cameras = new();
    private Dictionary<int, CameraChannel> _camerasById = new();
    private WallDto? _currentWall;
    private Point _dragStart;

    // Filas de canal por id para resaltar la cámara al seleccionar una ventana.
    private readonly Dictionary<int, Border> _channelRows = new();
    private readonly Dictionary<int, Expander> _channelExpanders = new();
    private Border? _highlightedRow;

    // Ventana del muro actualmente seleccionada (resaltada).
    private int? _selectedWindowId;

    // Selección múltiple (Ctrl+clic) para agrupar ventanas contiguas.
    private readonly HashSet<int> _multiSelected = new();

    // Arrastre de una ventana sobre otra para intercambiar sus cámaras.
    private const string WindowDragFormat = "clr-truecentral-window";
    private Point? _cellPressPoint;

    // Vista previa: muestra la imagen de cada cámara dentro de su ventana.
    private bool _previewMode;
    private System.Windows.Threading.DispatcherTimer? _previewTimer;
    // Límite de capturas simultáneas para no saturar los DVR (cada snapshot
    // abre una sesión al equipo).
    private readonly SemaphoreSlim _snapshotGate = new(4);
    private int _previewGeneration; // invalida cargas en vuelo al apagar/refrescar

    // Caché de una foto por canal: se muestra al instante y solo se recaptura
    // cuando pasa este tiempo (o el mouse se mantiene 15 s sobre el tooltip).
    private readonly Dictionary<int, (System.Windows.Media.Imaging.BitmapImage Img, DateTime At)> _snapCache = new();
    private static readonly TimeSpan SnapFreshness = TimeSpan.FromSeconds(15);

    // Pantallas con una operación en curso (overlay de carga que bloquea la
    // card), con el texto que muestra el overlay.
    private readonly Dictionary<int, string> _busyScreens = new();

    // Proyección de la pantalla de este PC como fuente del muro.
    private readonly ScreenProjectionService _projection = new();
    private readonly ClientSettings _settings = ClientSettings.Load();
    private bool _projectToggleGuard; // evita reentrar al revertir el toggle por código

    // Controles de reproducción del video proyectado (viven en su celda del muro).
    private Slider? _playbackSlider;
    private TextBlock? _playbackTime;
    private bool _playbackDragging;
    private readonly System.Windows.Threading.DispatcherTimer _playbackTimer;

    // Lista de cámaras colapsable (pestaña « » del borde).
    private bool _sourcesCollapsed;

    // Dibujo de ventanas flotantes (modo ▣): banda elástica sobre el muro.
    private System.Windows.Shapes.Rectangle? _floatDrawBand;
    private Point? _floatDrawStart;

    /// <summary>Ventana que aloja el módulo (dueña de los diálogos modales).</summary>
    private Window HostWindow => Window.GetWindow(this) ?? Application.Current.MainWindow;

    public WallView()
    {
        InitializeComponent();
        if (_settings.WallSourcesPanelCollapsed) SetSourcesPanelCollapsed(true);

        FloatLayer.MouseLeftButtonDown += OnFloatLayerMouseDown;
        FloatLayer.MouseMove += OnFloatLayerMouseMove;
        FloatLayer.MouseLeftButtonUp += OnFloatLayerMouseUp;

        _playbackTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _playbackTimer.Tick += (_, _) => UpdatePlaybackBar();

        // Si ffmpeg muere solo (p. ej. un formato cuyo bucle falla al terminar),
        // se relanza la reproducción desde el inicio y se re-engancha el decoder.
        _projection.ProjectionUnexpectedlyEnded += () => Dispatcher.Invoke(async () =>
        {
            if (!_projection.IsFileProjection) return;
            Status("La transmisión del video se cortó; reiniciando desde el inicio…");
            await SeekProjectionAsync(0);
        });

        // Video sin bucle que llegó al final: la transmisión termina sola y las
        // ventanas vuelven a su canal anterior (transmisión temporal).
        _projection.FilePlaybackFinished += () => Dispatcher.Invoke(async () =>
        {
            if (_projectionTargets.Count == 0) return;
            await FinishProjectionAsync("El video terminó; transmisión finalizada.");
        });

        // El muro solo se consulta cuando el operador abre el módulo: las
        // páginas del shell se conmutan por Visibility y todas nacen a la vez.
        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is not true) return;
            // Handler async void: una excepción aquí tumbaría la aplicación.
            try { await InitializeAsync(); }
            catch (Exception ex) { Status($"No se pudo abrir el muro: {ex.Message}"); }
        };

        Loaded += (_, _) =>
        {
            if (_hostWindow is not null || Window.GetWindow(this) is not { } window) return;
            _hostWindow = window;
            // Cerrar la aplicación con una proyección activa dejaría ffmpeg
            // corriendo y las ventanas del muro mostrando un stream muerto.
            window.Closed += async (_, _) => await ShutdownAsync();
        };
    }

    private Window? _hostWindow;
    private bool _shutdown;

    /// <summary>Detiene la proyección y suelta los recursos del módulo.</summary>
    private async Task ShutdownAsync()
    {
        if (_shutdown) return;
        _shutdown = true;
        _playbackTimer.Stop();
        _previewTimer?.Stop();
        if (_hub is not null)
        {
            _hub.WallStateChanged -= OnWallStatePushed;
            _hub.ConfigChanged -= OnConfigChanged;
        }
        try { await StopProjectionAsync(); }
        catch { /* el servidor puede estar cayéndose junto con el cliente */ }
        _projection.Dispose();
    }

    /// <summary>
    /// Primera apertura del módulo: toma el cliente de API y el hub del shell,
    /// carga los muros y se suscribe a los cambios en vivo. El muro no se
    /// consulta al arrancar la aplicación, solo cuando se abre la página.
    /// </summary>
    private async Task InitializeAsync()
    {
        if (_initialized || DataContext is not MainViewModel vm) return;
        _initialized = true;
        _api = vm.Api;
        _hub = vm.Hub;

        // El hub del VMS ya está conectado por el shell: aquí solo se escuchan
        // los eventos del muro (los emite el servidor ante cualquier cambio,
        // venga de este cliente, de otro puesto o del panel web).
        _hub.WallStateChanged += OnWallStatePushed;
        _hub.ConfigChanged += OnConfigChanged;

        await ReloadDataAsync();
    }

    private void OnWallStatePushed(WallDto dto) => Dispatcher.Invoke(() => OnWallStatePush(dto));

    private void OnConfigChanged(string entity)
    {
        // Solo interesan los cambios que afectan al muro: su configuración y el
        // inventario de cámaras que se le pueden asignar.
        if (entity is not ("walls" or "decoders" or "devices" or "channels")) return;
        Dispatcher.Invoke(async () => await ReloadDataAsync());
    }

    private async Task ReloadDataAsync()
    {
        try
        {
            int? selectedId = _currentWall?.Id;
            _walls = await _api.GetWallsAsync();
            await LoadCamerasAsync();

            RenderSourcesPanel();
            WallSelector.ItemsSource = _walls;

            var toSelect = _walls.FirstOrDefault(w => w.Id == selectedId) ?? _walls.FirstOrDefault();
            WallSelector.SelectedItem = toSelect;
            _currentWall = toSelect;
            RenderWall();
            Status($"Datos actualizados: {_walls.Count} muro(s), {_cameras.Count} canal(es).");
        }
        catch (Exception ex)
        {
            Status($"Error cargando datos: {ex.Message}");
        }
    }

    /// <summary>
    /// Canales del inventario que se pueden mandar al muro: los habilitados de
    /// cada dispositivo. Se piden una vez por recarga y quedan indexados por id
    /// (la asignación de una ventana viaja como un único ChannelId).
    /// </summary>
    private async Task LoadCamerasAsync()
    {
        var devices = await _api.GetDevicesAsync();
        var cameras = new List<CameraChannel>();
        foreach (var device in devices.OrderBy(d => d.Name))
        {
            try
            {
                foreach (var channel in (await _api.GetChannelsAsync(device.Id)).Where(c => c.Enabled))
                    cameras.Add(new CameraChannel(device, channel));
            }
            catch (Exception ex)
            {
                // Un equipo que no responde no debe dejar sin lista a los demás.
                Status($"No se pudieron leer los canales de {device.Name}: {ex.Message}");
            }
        }
        _cameras = cameras;
        _camerasById = cameras.ToDictionary(c => c.Channel.Id);
    }

    private void OnWallStatePush(WallDto dto)
    {
        int index = _walls.FindIndex(w => w.Id == dto.Id);
        if (index >= 0) _walls[index] = dto;
        // Primero se reconcilian los destinos de la proyección (una ventana pudo
        // moverse por intercambio) y recién después se pinta, para que la barra
        // de reproducción aparezca en la ventana correcta.
        bool finished = ReconcileProjectionTargets(dto);
        if (_currentWall?.Id == dto.Id)
        {
            _currentWall = dto;
            RenderWall();
            Status($"Estado del muro \"{dto.Name}\" actualizado.");
        }
        if (finished)
            _ = FinishProjectionAsync("La ventana de proyección fue reasignada; transmisión detenida.");
    }

    /// <summary>
    /// Ajusta los destinos de la proyección al nuevo estado del wall:
    /// - Si la proyección se MOVIÓ a otra ventana (intercambio arrastrando la
    ///   ventana sobre otra), el destino la sigue: la barra de reproducción y la
    ///   restauración al detener van a la ventana nueva.
    /// - Si una ventana destino dejó de mostrarla y no apareció en otra parte
    ///   (la reasignaron desde el panel web, arrastraron otro canal encima,
    ///   cerraron la flotante), se saca de la lista.
    /// Devuelve true si no queda ningún destino y hay que terminar la
    /// transmisión (si no, ffmpeg seguiría corriendo y el botón quedaría marcado).
    /// </summary>
    private bool ReconcileProjectionTargets(WallDto wall)
    {
        if (_projectionUrl is not { } projectionUrl || _projectionTargets.Count == 0) return false;

        var cells = wall.Screens.SelectMany(s => s.Windows.Select(w => (Screen: s, Win: w))).ToList();
        var byId = cells.ToDictionary(c => c.Win.Id);
        bool changed = false;

        for (int i = _projectionTargets.Count - 1; i >= 0; i--)
        {
            var t = _projectionTargets[i];
            if (t.WallId != wall.Id) continue;

            if (t.Kind == TargetKind.Floating)
            {
                // Flotante cerrada desde otro lado o con otra cámara: fuera.
                var floating = wall.Floating?.FirstOrDefault(f => f.Id == t.Id);
                if (floating is null || floating.Assignment?.ExternalUrl != projectionUrl)
                {
                    _projectionTargets.RemoveAt(i);
                    changed = true;
                }
                continue;
            }

            if (!byId.TryGetValue(t.Id, out var cell)) continue;             // ventana desconocida (layout): no tocar
            if (cell.Win.Assignment?.ExternalUrl == projectionUrl) continue; // sigue mostrando la proyección

            // ¿Se movió? Buscar una ventana que ahora muestre la proyección y no
            // sea ya un destino conocido (intercambio de ventanas).
            var moved = cells.FirstOrDefault(c =>
                c.Win.Assignment?.ExternalUrl == projectionUrl &&
                !_projectionTargets.Any(x => x.Kind == TargetKind.Window && x.Id == c.Win.Id));
            if (moved.Win is not null)
            {
                // Se conserva Previous: al detener, el canal que desplazó la
                // proyección vuelve en la ventana donde ésta terminó.
                _projectionTargets[i] = t with { Id = moved.Win.Id, Label = WindowLabel(moved.Screen, moved.Win) };
            }
            else
            {
                _projectionTargets.RemoveAt(i);
            }
            changed = true;
        }
        return changed && _projectionTargets.Count == 0;
    }

    private static string WindowLabel(ScreenDto screen, WindowDto win) => $"{screen.Label} · ventana {win.WindowIndex + 1}";

    private void OnWallSelected(object sender, SelectionChangedEventArgs e)
    {
        _currentWall = WallSelector.SelectedItem as WallDto;
        _selectedWindowId = null;
        RenderWall();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await ReloadDataAsync();

    /// <summary>Oculta/muestra la lista de fuentes con la pestaña « » del borde.</summary>
    private void OnToggleSourcesPanel(object sender, MouseButtonEventArgs e) =>
        SetSourcesPanelCollapsed(!_sourcesCollapsed);

    private void SetSourcesPanelCollapsed(bool collapsed)
    {
        _sourcesCollapsed = collapsed;
        SourcesColumn.Width = collapsed ? new GridLength(0) : new GridLength(320);
        SourcesPanelBorder.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SourcesToggleGlyph.Text = collapsed ? "«" : "»";
        SourcesToggle.ToolTip = collapsed ? "Mostrar la lista de fuentes" : "Ocultar la lista de fuentes";
        _settings.WallSourcesPanelCollapsed = collapsed;
        _settings.Save();
    }

    private void OnPreviewToggled(object sender, RoutedEventArgs e)
    {
        _previewMode = PreviewToggle.IsChecked == true;
        _previewGeneration++; // cancela cargas en vuelo

        if (_previewMode)
        {
            _previewTimer ??= new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30),
            };
            _previewTimer.Tick -= OnPreviewTick;
            _previewTimer.Tick += OnPreviewTick;
            _previewTimer.Start();
            Status("Vista previa activada.");
        }
        else
        {
            _previewTimer?.Stop();
            Status("Vista previa desactivada.");
        }
        RenderWall();
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        // Refresco periódico de las miniaturas mientras la vista previa está
        // activa (RenderWall incrementa la generación y recarga las imágenes).
        if (_previewMode) RenderWall();
    }

    // -----------------------------------------------------------------------
    // Panel de fuentes: cada fuente expande sus canales, arrastrables al wall.
    // -----------------------------------------------------------------------
    private void OnChannelFilterChanged(object sender, TextChangedEventArgs e)
    {
        ChannelFilterHint.Visibility = string.IsNullOrEmpty(ChannelFilterBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        RenderSourcesPanel();
    }

    /// <summary>
    /// Lista de cámaras: un grupo plegable por dispositivo con sus canales
    /// habilitados, arrastrables a cualquier ventana del muro.
    /// </summary>
    private void RenderSourcesPanel()
    {
        SourcesPanel.Children.Clear();
        _channelRows.Clear();
        _channelExpanders.Clear();
        _highlightedRow = null;

        string filter = ChannelFilterBox?.Text?.Trim() ?? "";
        bool filtering = filter.Length > 0;

        var byDevice = _cameras
            .GroupBy(c => c.Device.Id)
            .Select(g => (Device: g.First().Device, Channels: g.Select(c => c.Channel).OrderBy(c => c.ChannelNumber).ToList()))
            .OrderBy(g => g.Device.Name)
            .ToList();

        foreach (var (device, channels) in byDevice)
        {
            var expander = new Expander
            {
                Header = $"{device.Name}  ·  {KindLabel(device.DeviceType)}",
                Foreground = (Brush)FindResource("TextBrush"),
                Margin = new Thickness(0, 2, 0, 2),
                IsExpanded = byDevice.Count == 1,
            };

            var panel = new StackPanel { Margin = new Thickness(6, 6, 0, 8) };

            // Los canales sin señal (analógicos deshabilitados, IP sin cámara)
            // no se ofrecen: el decoder no tendría qué mostrar.
            var visible = channels.Where(c => c.IsOnline).ToList();

            // Filtro de búsqueda: por nombre de canal, número o nombre del equipo.
            bool deviceMatches = filtering && device.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
            if (filtering && !deviceMatches)
                visible = visible
                    .Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                c.ChannelNumber.ToString().Contains(filter))
                    .ToList();

            // Si hay filtro y este equipo no aporta coincidencias, se omite.
            if (filtering && visible.Count == 0)
                continue;

            // Con filtro activo se expanden los grupos con coincidencias.
            if (filtering) expander.IsExpanded = true;

            foreach (var channel in visible)
            {
                var row = new Border
                {
                    Background = (Brush)FindResource("Panel2Brush"),
                    BorderBrush = (Brush)FindResource("BorderBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 0, 6, 4),
                    Cursor = Cursors.Hand,
                    Tag = channel.Id,
                };

                var rowContent = new DockPanel();
                rowContent.Children.Add(new TextBlock
                {
                    Text = channel.ChannelNumber.ToString(),
                    Foreground = (Brush)FindResource("MutedBrush"),
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                rowContent.Children.Add(new TextBlock
                {
                    Text = channel.Name,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                row.Child = rowContent;

                AttachSnapshotTooltip(row, channel.Id, $"{device.Name} · canal {channel.ChannelNumber} — {channel.Name}");
                row.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(this);
                row.PreviewMouseMove += OnChipMouseMove;
                panel.Children.Add(row);
                _channelRows[channel.Id] = row;
                _channelExpanders[channel.Id] = expander;
            }

            if (visible.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Sin canales con señal.",
                    Foreground = (Brush)FindResource("MutedBrush"),
                    FontSize = 11,
                    FontStyle = FontStyles.Italic,
                });
            }

            expander.Content = panel;
            SourcesPanel.Children.Add(expander);
        }

        if (_cameras.Count == 0)
        {
            SourcesPanel.Children.Add(new TextBlock
            {
                Text = "No hay cámaras en el inventario.\nAgregue dispositivos desde el panel web.",
                Foreground = (Brush)FindResource("MutedBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else if (filtering && SourcesPanel.Children.Count == 0)
        {
            SourcesPanel.Children.Add(new TextBlock
            {
                Text = $"Sin canales que coincidan con \"{filter}\".",
                Foreground = (Brush)FindResource("MutedBrush"),
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    /// <summary>
    /// Tooltip con vista previa: al pasar el mouse se pide un fotograma JPEG
    /// del canal al servidor (que lo captura del DVR/cámara y lo cachea).
    /// </summary>
    private void AttachSnapshotTooltip(FrameworkElement element, int channelId, string title)
    {
        var image = new Image
        {
            MaxWidth = 320,
            MaxHeight = 200,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        var statusLine = new TextBlock
        {
            Text = "Cargando vista previa…",
            Foreground = (Brush)FindResource("MutedBrush"),
            FontSize = 11,
        };
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
        });
        content.Children.Add(statusLine);
        content.Children.Add(image);

        var tooltip = new ToolTip
        {
            Content = content,
            Background = (Brush)FindResource("PanelBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
        };
        element.ToolTip = tooltip;
        ToolTipService.SetInitialShowDelay(element, 350);
        // Sin auto-cierre: mientras el mouse siga encima, el tooltip permanece
        // y se refresca cada 15 s.
        ToolTipService.SetShowDuration(element, int.MaxValue);

        void ShowImage(System.Windows.Media.Imaging.BitmapImage bmp)
        {
            image.Source = bmp;
            image.Visibility = Visibility.Visible;
            statusLine.Visibility = Visibility.Collapsed;
        }

        async Task RefreshAsync()
        {
            var bmp = await FetchSnapshotAsync(channelId);
            if (bmp is not null) ShowImage(bmp);
            else if (image.Source is null) statusLine.Text = "Vista previa no disponible.";
        }

        System.Windows.Threading.DispatcherTimer? refreshTimer = null;

        tooltip.Opened += async (_, _) =>
        {
            // 1) Mostrar de inmediato la última foto en caché de este canal.
            if (_snapCache.TryGetValue(channelId, out var cached))
                ShowImage(cached.Img);
            else
            {
                statusLine.Text = "Cargando vista previa…";
                await RefreshAsync(); // primera vez para este canal
            }

            // 2) Mientras el mouse siga encima, recapturar cada 15 s.
            refreshTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = SnapFreshness,
            };
            refreshTimer.Tick += async (_, _) => await RefreshAsync();
            refreshTimer.Start();
        };

        tooltip.Closed += (_, _) =>
        {
            refreshTimer?.Stop();
            refreshTimer = null;
        };
    }

    private void OnChipMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var diff = _dragStart - e.GetPosition(this);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        if (sender is Border { Tag: int channelId } chip)
            DragDrop.DoDragDrop(chip, new DataObject(DragFormat, channelId.ToString()), DragDropEffects.Copy);
    }

    private static string KindLabel(DeviceType type) => type switch
    {
        DeviceType.Dvr => "DVR",
        DeviceType.Nvr => "NVR",
        DeviceType.Xvr => "XVR",
        _ => "Cámara",
    };

    // -----------------------------------------------------------------------
    // Grilla del wall: pantallas con sub-ventanas
    // -----------------------------------------------------------------------
    private void RenderWall()
    {
        // Cada re-render en modo vista previa invalida las cargas de miniaturas
        // anteriores (evita que se acumulen si el wall se refresca seguido).
        if (_previewMode) _previewGeneration++;

        // Los controles de reproducción se reconstruyen con su celda.
        _playbackSlider = null;
        _playbackTime = null;

        WallGrid.Children.Clear();
        WallGrid.RowDefinitions.Clear();
        WallGrid.ColumnDefinitions.Clear();

        if (_currentWall is null)
        {
            WallTitle.Text = "Sin muros de video configurados";
            FloatLayer.Children.Clear();
            return;
        }

        var wall = _currentWall;
        WallTitle.Text = $"{wall.Name} — {wall.Rows}×{wall.Columns} · Decodificador: {wall.DecoderName}";

        // Muro completo: una cámara ocupa todas las pantallas como una sola.
        // Se pinta una única celda gigante en lugar del mosaico (que sigue
        // decodificando por debajo, igual que en el equipo).
        if (wall.FullscreenWindowId is int wallFsId)
        {
            var fsWin = wall.Screens.SelectMany(s => s.Windows).FirstOrDefault(w => w.Id == wallFsId);
            if (fsWin is not null)
            {
                WallGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                WallGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                WallGrid.Children.Add(BuildWallFullscreenCell(fsWin));
                FloatLayer.Children.Clear(); // las flotantes quedan tapadas por la cámara
                return;
            }
        }

        for (int r = 0; r < wall.Rows; r++)
            WallGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int c = 0; c < wall.Columns; c++)
            WallGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int r = 0; r < wall.Rows; r++)
        {
            for (int c = 0; c < wall.Columns; c++)
            {
                var screen = wall.Screens.FirstOrDefault(s => s.Row == r && s.Col == c);
                var cell = BuildScreenCell(screen, r, c);
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                WallGrid.Children.Add(cell);
            }
        }

        RenderFloatLayer();
    }

    /// <summary>
    /// Celda única del modo "muro completo": la cámara ocupando todas las
    /// pantallas como una sola. Doble clic (o el ✕): volver al mosaico.
    /// </summary>
    private Border BuildWallFullscreenCell(WindowDto win)
    {
        var a = win.Assignment;
        string camName = a?.ChannelName ?? "Cámara";

        var border = new Border
        {
            Margin = new Thickness(5),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("BgBrush"),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(2),
            Cursor = Cursors.Hand,
            ToolTip = "Doble clic para volver al mosaico",
        };

        var host = new Grid();
        border.Child = host;

        if (_previewMode && a is not null)
        {
            var img = new Image { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.LowQuality);
            host.Children.Add(img);

            var loading = new TextBlock
            {
                Text = "cargando…",
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            host.Children.Add(loading);
            if (a.IsExternal) loading.Text = "proyección";
            else LoadCellSnapshotAsync(img, loading, a.ChannelId, _previewGeneration);

            host.Children.Add(new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x0F, 0x14, 0x1A)),
                Padding = new Thickness(6, 2, 6, 2),
                Child = new TextBlock
                {
                    Text = camName,
                    Foreground = Brushes.White,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            });
        }
        else
        {
            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            stack.Children.Add(new TextBlock
            {
                Text = camName,
                Foreground = (Brush)FindResource("AccentBrush"),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
            });
            if (a is not null)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = SubtitleOf(a),
                    Foreground = (Brush)FindResource("MutedBrush"),
                    FontSize = 11,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 0),
                });
            }
            stack.Children.Add(new TextBlock
            {
                Text = "Doble clic para volver al mosaico",
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 10,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0),
            });
            host.Children.Add(stack);
        }

        host.Children.Add(new Border
        {
            Background = (Brush)FindResource("Accent2Brush"),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(6, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = "MURO COMPLETO", FontSize = 9, Foreground = Brushes.White },
        });

        if (a is not null)
        if (!a.IsExternal)
            AttachSnapshotTooltip(border, a.ChannelId, $"Muro completo: {camName}");

        border.MouseLeftButtonDown += async (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                await ExitWallFullscreenUiAsync();
            }
        };

        return border;
    }

    private async Task EnterWallFullscreenUiAsync(WindowDto win)
    {
        if (_currentWall is null || win.Assignment is null) return;
        _multiSelected.Clear(); // el Ctrl del atajo no debe dejar una selección colgada
        string camName = win.Assignment.ChannelName;
        try
        {
            Status($"{camName} ocupando todo el muro…");
            await _api.WallEnterWallFullscreenAsync(_currentWall.Id, win.Id);
            Status($"{camName} en el muro completo (las demás decodificaciones siguen vivas debajo).");
        }
        catch (Exception ex)
        {
            MessageBox.Show(HostWindow, ex.Message, "No se pudo ocupar el muro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ExitWallFullscreenUiAsync()
    {
        if (_currentWall is null) return;
        try
        {
            Status("Restaurando el mosaico del muro…");
            await _api.WallExitWallFullscreenAsync(_currentWall.Id);
            Status("Mosaico restaurado (ningún video se cortó).");
        }
        catch (Exception ex)
        {
            MessageBox.Show(HostWindow, ex.Message, "No se pudo restaurar el mosaico", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static readonly int[] WindowModeOptions = { 1, 2, 4, 6, 8, 9, 12, 16, 25, 36 };

    private Border BuildScreenCell(ScreenDto? screen, int row, int col)
    {
        var border = new Border
        {
            Margin = new Thickness(5),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("Panel2Brush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
        };

        if (screen is null)
        {
            border.Opacity = 0.45;
            border.Child = new TextBlock
            {
                Text = $"F{row + 1}·C{col + 1}\nsin salida",
                Foreground = (Brush)FindResource("MutedBrush"),
                FontStyle = FontStyles.Italic,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
            return border;
        }

        // Capa de contenido + capa de overlay (bloqueo mientras se aplica un cambio).
        var overlayHost = new Grid();
        border.Child = overlayHost;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        overlayHost.Children.Add(root);

        var header = new DockPanel { Margin = new Thickness(2, 0, 0, 6) };

        // El operador puede cambiar el layout del monitor (cantidad de cámaras)
        // desde aquí; la estructura del wall (filas × columnas) solo se edita
        // en el panel web de administración.
        var modeCombo = new ComboBox
        {
            FontSize = 10.5,
            Padding = new Thickness(6, 2, 6, 2),
            MinWidth = 52,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Cantidad de ventanas de este monitor",
        };
        var modes = WindowModeOptions.ToList();
        if (!modes.Contains(screen.WindowMode)) modes.Add(screen.WindowMode);
        modes.Sort();
        foreach (int m in modes) modeCombo.Items.Add(m);
        modeCombo.SelectedItem = screen.WindowMode;
        modeCombo.SelectionChanged += (_, _) => OnWindowModeSelected(screen, modeCombo);
        DockPanel.SetDock(modeCombo, Dock.Right);
        header.Children.Add(modeCombo);

        // Agrupar (selección Ctrl+clic de este monitor) y desagrupar (ventana
        // agrupada seleccionada): layouts personalizados tipo HikCentral.
        var selectedHere = screen.Windows.Where(w => _multiSelected.Contains(w.Id)).ToList();
        if (selectedHere.Count >= 2 && selectedHere.All(w => w.SpanCols <= 1 && w.SpanRows <= 1))
        {
            var groupButton = new Button
            {
                Content = $"⊞ Agrupar {selectedHere.Count}",
                FontSize = 10.5,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Fusionar las ventanas seleccionadas (Ctrl+clic) en una ventana grande",
            };
            groupButton.Click += async (_, _) => await GroupSelectedAsync(screen);
            DockPanel.SetDock(groupButton, Dock.Right);
            header.Children.Add(groupButton);
        }

        var groupedSelected = screen.Windows.FirstOrDefault(w =>
            (w.SpanCols > 1 || w.SpanRows > 1) &&
            (_multiSelected.Contains(w.Id) || _selectedWindowId == w.Id));
        if (groupedSelected is not null)
        {
            var ungroupButton = new Button
            {
                Content = "⊟ Desagrupar",
                FontSize = 10.5,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Volver la ventana agrupada a sub-ventanas individuales",
            };
            ungroupButton.Click += async (_, _) => await UngroupAsync(screen, groupedSelected);
            DockPanel.SetDock(ungroupButton, Dock.Right);
            header.Children.Add(ungroupButton);
        }

        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(screen.Label) ? $"Salida {screen.DisplayChannel}" : screen.Label,
            Foreground = (Brush)FindResource("MutedBrush"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (screen.Fullscreen)
        {
            title.Children.Add(new Border
            {
                Background = (Brush)FindResource("Accent2Brush"),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "PANTALLA COMPLETA", FontSize = 9, Foreground = Brushes.White },
            });
        }
        header.Children.Add(title);
        root.Children.Add(header);

        var winGrid = new Grid();
        Grid.SetRow(winGrid, 1);
        root.Children.Add(winGrid);

        // En pantalla completa se muestra solo esa cámara ocupando el monitor
        // (refleja lo que se ve en el muro); las demás siguen decodificando.
        var fullscreenWin = screen.FullscreenWindowId is int fsId
            ? screen.Windows.FirstOrDefault(w => w.Id == fsId)
            : null;

        if (fullscreenWin is not null)
        {
            winGrid.Children.Add(BuildWindowCell(screen, fullscreenWin));
        }
        else
        {
            // La grilla base la define el modo del monitor; cada ventana vive
            // en su casilla y puede abarcar varias (ventanas agrupadas).
            int baseCount = Math.Max(1, screen.WindowMode);
            int gridCols = (int)Math.Ceiling(Math.Sqrt(baseCount));
            int gridRows = (int)Math.Ceiling(baseCount / (double)gridCols);
            for (int i = 0; i < gridRows; i++)
                winGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < gridCols; i++)
                winGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Mapa casilla→(ventana, card) para el modo agrupar por arrastre.
            var cellBySlot = new Dictionary<int, (WindowDto Win, Border Cell)>();
            foreach (var win in screen.Windows.OrderBy(w => w.WindowIndex))
            {
                var winCell = BuildWindowCell(screen, win);
                Grid.SetRow(winCell, Math.Min(win.WindowIndex / gridCols, gridRows - 1));
                Grid.SetColumn(winCell, win.WindowIndex % gridCols);
                Grid.SetRowSpan(winCell, Math.Max(1, win.SpanRows));
                Grid.SetColumnSpan(winCell, Math.Max(1, win.SpanCols));
                for (int dr = 0; dr < Math.Max(1, win.SpanRows); dr++)
                    for (int dc = 0; dc < Math.Max(1, win.SpanCols); dc++)
                        cellBySlot[win.WindowIndex + dr * gridCols + dc] = (win, winCell);
                winGrid.Children.Add(winCell);
            }

            // Modo agrupar: capa de selección por arrastre sobre el monitor
            // (garantiza que la selección sea un rectángulo de la grilla).
            if (GroupModeToggle.IsChecked == true && screen.Windows.Count > 1)
            {
                var selectOverlay = BuildGroupSelectOverlay(screen, gridCols, gridRows, cellBySlot);
                Grid.SetRow(selectOverlay, 1);
                root.Children.Add(selectOverlay);
            }
        }

        // Overlay de carga: oscurece la card y bloquea la interacción mientras el
        // cambio se hace efectivo en el decoder (tarda algunos segundos).
        if (_busyScreens.TryGetValue(screen.Id, out string? busyText))
            overlayHost.Children.Add(BuildLoadingOverlay(busyText));

        return border;
    }

    private Border BuildLoadingOverlay(string text)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(new ProgressBar
        {
            IsIndeterminate = true,
            Width = 140,
            Height = 6,
            Foreground = (Brush)FindResource("AccentBrush"),
            Background = (Brush)FindResource("Panel2Brush"),
        });
        panel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 260,
        });
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0F, 0x14, 0x1A)),
            CornerRadius = new CornerRadius(6),
            Child = panel,
        };
    }

    private void OnWindowModeSelected(ScreenDto screen, ComboBox modeCombo)
    {
        if (modeCombo.SelectedItem is not int mode || mode == screen.WindowMode) return;

        // Confirmación al reducir: avisa cuántas cámaras dejarán de verse.
        int assignedBeyond = screen.Windows.Count(w => w.WindowIndex >= mode && w.Assignment is not null);
        if (mode < screen.WindowMode && assignedBeyond > 0)
        {
            var confirm = MessageBox.Show(HostWindow,
                $"Reducir \"{screen.Label}\" a {mode} ventana(s) detendrá la visualización de {assignedBeyond} cámara(s) que están fuera del nuevo layout.\n\n¿Continuar?",
                "Reducir layout", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                modeCombo.SelectedItem = screen.WindowMode; // revertir
                return;
            }
        }

        _ = RunScreenOperationAsync(screen.Id,
            () => _api.WallChangeWindowModeAsync(_currentWall!.Id, screen.Id, mode),
            $"Cambiando {screen.Label} a {mode} ventana(s)…",
            $"{screen.Label} ahora tiene {mode} ventana(s).",
            "No se pudo cambiar el layout");
    }

    /// <summary>
    /// Ejecuta una operación que tarda en hacerse efectiva mostrando el overlay
    /// de carga sobre la pantalla afectada y bloqueando nuevos cambios hasta que
    /// termine.
    /// </summary>
    private async Task RunScreenOperationAsync(int screenId, Func<Task> operation,
        string startStatus, string okStatus, string errorTitle)
    {
        if (!_busyScreens.TryAdd(screenId, "Aplicando cambio…")) return; // ya hay una operación en curso
        RenderWall();
        Status(startStatus);
        try
        {
            await operation();
            Status(okStatus);
        }
        catch (Exception ex)
        {
            MessageBox.Show(HostWindow, ex.Message, errorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            Status("La operación falló.");
        }
        finally
        {
            _busyScreens.Remove(screenId);
            RenderWall();
        }
    }

    private Border BuildWindowCell(ScreenDto screen, WindowDto win)
    {
        bool selected = _selectedWindowId == win.Id || _multiSelected.Contains(win.Id);
        var assignedBorder = new SolidColorBrush(Color.FromRgb(0x2B, 0x4A, 0x75));
        // Color de reposo de la ventana (según asignación/selección) para
        // restaurarlo tras un arrastre.
        Brush restingBorder = selected ? (Brush)FindResource("AccentBrush")
            : win.Assignment is null ? (Brush)FindResource("BorderBrush") : assignedBorder;
        var restingThickness = new Thickness(selected ? 2 : 1);

        string? camName = win.Assignment?.ChannelName;

        var cell = new Border
        {
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(5),
            Background = (Brush)FindResource("BgBrush"),
            BorderBrush = restingBorder,
            BorderThickness = restingThickness,
            Padding = new Thickness(4),
            AllowDrop = true,
            Cursor = Cursors.Hand,
            // El modo agrupar usa Tag para restaurar el borde tras resaltar.
            Tag = restingBorder,
        };

        // Tooltip: con imagen de vista previa si hay cámara; texto si está vacía.
        if (win.Assignment is { IsExternal: false } tip)
            AttachSnapshotTooltip(cell, tip.ChannelId,
                $"Ventana {win.WindowIndex + 1}: {tip.ChannelName} · {tip.DeviceName} ch {tip.ChannelNumber}");
        else
            cell.ToolTip = $"Ventana {win.WindowIndex + 1} · vacía · arrastre un canal aquí";

        FrameworkElement content;
        if (_previewMode && win.Assignment is { } pa)
        {
            // Vista previa: la imagen de la cámara llena la ventana, con el
            // nombre en una franja inferior.
            var overlay = new Grid();
            var img = new Image { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.LowQuality);
            overlay.Children.Add(img);

            overlay.Children.Add(new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x0F, 0x14, 0x1A)),
                Padding = new Thickness(4, 1, 4, 1),
                Child = new TextBlock
                {
                    Text = camName ?? SubtitleOf(pa),
                    Foreground = Brushes.White,
                    FontSize = 10,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            });

            var loading = new TextBlock
            {
                Text = "cargando…",
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            overlay.Children.Add(loading);
            cell.Padding = new Thickness(0);
            content = overlay;
            if (pa.IsExternal) loading.Text = "proyección";
            else LoadCellSnapshotAsync(img, loading, pa.ChannelId, _previewGeneration);
        }
        else
        {
            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            content = stack;

            if (win.Assignment is { } a)
            {
                // Primero el nombre de la cámara (lo que importa al operador);
                // debajo la fuente y el canal.
                stack.Children.Add(new TextBlock
                {
                    Text = camName ?? a.ChannelName,
                    Foreground = (Brush)FindResource("AccentBrush"),
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Center,
                    MaxWidth = 150,
                });
                stack.Children.Add(new TextBlock
                {
                    Text = SubtitleOf(a),
                    Foreground = (Brush)FindResource("MutedBrush"),
                    FontSize = 10,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Center,
                    MaxWidth = 150,
                });
            }
            else
            {
                stack.Children.Add(new TextBlock
                {
                    Text = (win.WindowIndex + 1).ToString(),
                    Foreground = (Brush)FindResource("MutedBrush"),
                    FontSize = 11,
                    TextAlignment = TextAlignment.Center,
                });
            }
        }

        // Contenido + botón ✕ (cerrar video) superpuesto en la esquina.
        var host = new Grid();
        host.Children.Add(content);
        if (win.Assignment is not null)
            host.Children.Add(BuildWindowCloseButton(win));

        // Controles de reproducción cuando esta ventana muestra el video en
        // proyección de este cliente (pausa/reanudar, reiniciar y posición).
        // Con varias ventanas destino la barra va solo en la principal (los
        // controles afectan al stream, que es el mismo para todas).
        if (IsPrimaryProjectionTarget(TargetKind.Window, win.Id) &&
            win.Assignment?.ExternalUrl == _projectionUrl &&
            _projection.IsFileProjection)
        {
            host.Children.Add(BuildPlaybackBar());
        }
        cell.Child = host;

        // Clic simple: seleccionar (Ctrl+clic: selección múltiple para agrupar).
        // Doble clic: pantalla completa / volver. Cerrar video: botón ✕.
        // Arrastrar la ventana sobre otra: intercambiar cámaras.
        // Drop de un canal de la lista: asignación directa.
        cell.MouseLeftButtonDown += async (_, e) =>
        {
            _cellPressPoint = e.GetPosition(this);
            if (e.ClickCount == 2)
            {
                // Ctrl+doble clic: la cámara ocupa TODO el muro; doble clic a
                // secas: pantalla completa solo en su monitor.
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && win.Assignment is not null)
                    await EnterWallFullscreenUiAsync(win);
                else
                    await ToggleFullscreenAsync(screen, win);
            }
        };
        cell.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _cellPressPoint is not Point start) return;
            if (win.Assignment is null) return; // nada que arrastrar
            var position = e.GetPosition(this);
            if (Math.Abs(position.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(position.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _cellPressPoint = null;
            // Copy|Move: el destino decide el efecto; si solo se permite Move y
            // el DragOver responde Copy, WPF anula el drop.
            DragDrop.DoDragDrop(cell, new DataObject(WindowDragFormat, win.Id.ToString()),
                DragDropEffects.Copy | DragDropEffects.Move);
        };
        cell.MouseLeftButtonUp += (_, e) =>
        {
            _cellPressPoint = null;
            if (e.ChangedButton != MouseButton.Left) return;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                // Ctrl+clic: agregar/quitar de la selección múltiple (agrupar).
                if (!_multiSelected.Remove(win.Id)) _multiSelected.Add(win.Id);
                RenderWall();
                return;
            }
            _multiSelected.Clear();
            SelectWindow(win.Id);
            if (win.Assignment is { } clickAsg)
                HighlightChannel(clickAsg.ChannelId);
        };
        cell.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(WindowDragFormat) ? DragDropEffects.Move
                : e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        };
        cell.DragEnter += (_, _) => cell.BorderBrush = (Brush)FindResource("AccentBrush");
        cell.DragLeave += (_, _) => cell.BorderBrush = restingBorder;
        cell.Drop += async (_, e) =>
        {
            // Ventana sobre ventana: intercambiar cámaras.
            if (e.Data.GetData(WindowDragFormat) is string draggedRaw && int.TryParse(draggedRaw, out int draggedId))
            {
                if (draggedId != win.Id) await SwapWindowsAsync(draggedId, win.Id);
                return;
            }
            if (e.Data.GetData(DragFormat) is not string payload || !int.TryParse(payload, out int channelId))
                return;
            await AssignAsync(win, channelId, 0);
        };

        // Clic derecho: dividir esta ventana en 4/9/16 (estilo HikCentral),
        // desagrupar si está agrupada y cerrar su video.
        cell.ContextMenu = BuildWindowContextMenu(screen, win);

        return cell;
    }

    /// <summary>Menú contextual de una ventana del mosaico.</summary>
    private ContextMenu? BuildWindowContextMenu(ScreenDto screen, WindowDto win)
    {
        if (screen.Fullscreen) return null;

        var menu = new ContextMenu();
        if (win.Assignment is not null)
        {
            var wallFs = new MenuItem
            {
                Header = "🖵 Ocupar todo el muro",
                ToolTip = "La cámara cubre todas las pantallas como una sola (Ctrl+doble clic)",
            };
            wallFs.Click += async (_, _) => await EnterWallFullscreenUiAsync(win);
            menu.Items.Add(wallFs);
            menu.Items.Add(new Separator());
        }
        foreach (int parts in new[] { 4, 9, 16 })
        {
            var item = new MenuItem { Header = $"⊞ Dividir esta ventana en {parts}" };
            item.Click += async (_, _) => await SubdivideWindowAsync(screen, win, parts);
            menu.Items.Add(item);
        }
        if (win.SpanCols > 1 || win.SpanRows > 1)
        {
            menu.Items.Add(new Separator());
            var ungroup = new MenuItem { Header = "⊟ Desagrupar esta ventana" };
            ungroup.Click += async (_, _) => await UngroupAsync(screen, win);
            menu.Items.Add(ungroup);
        }
        if (win.Assignment is not null)
        {
            menu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "✕ Cerrar este video" };
            clear.Click += async (_, _) => await ClearWindowAsync(win);
            menu.Items.Add(clear);
        }
        return menu;
    }

    /// <summary>Subdivide una ventana del mosaico en N partes (clic derecho).</summary>
    private async Task SubdivideWindowAsync(ScreenDto screen, WindowDto win, int parts)
    {
        if (_currentWall is null) return;
        _multiSelected.Clear();
        await RunScreenOperationAsync(screen.Id,
            () => _api.WallSubdivideAsync(_currentWall.Id, win.Id, parts),
            $"Dividiendo la ventana {win.WindowIndex + 1} de {screen.Label} en {parts}…",
            $"Ventana dividida en {parts} en {screen.Label}.",
            "No se pudo dividir la ventana");
    }

    /// <summary>Botón ✕ para cerrar el video de una ventana (reemplaza al clic derecho).</summary>
    private FrameworkElement BuildWindowCloseButton(WindowDto win)
    {
        var label = new TextBlock
        {
            Text = "✕",
            FontSize = 10,
            Foreground = (Brush)FindResource("MutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0),
        };
        var close = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x1A, 0x22, 0x2E)),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            Cursor = Cursors.Hand,
            ToolTip = IsProjectionWindow(win)
                ? "Detener la transmisión en esta ventana (vuelve al canal anterior)"
                : "Cerrar este video",
            Child = label,
        };
        close.MouseEnter += (_, _) =>
        {
            label.Foreground = Brushes.White;
            close.Background = (Brush)FindResource("DangerBrush");
        };
        close.MouseLeave += (_, _) =>
        {
            label.Foreground = (Brush)FindResource("MutedBrush");
            close.Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x1A, 0x22, 0x2E));
        };
        // Se marca Handled para que el clic no seleccione la ventana ni dispare
        // la pantalla completa del doble clic.
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        close.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            await ClearWindowAsync(win);
        };
        return close;
    }

    // -----------------------------------------------------------------------
    // Controles de reproducción del video proyectado
    // -----------------------------------------------------------------------

    /// <summary>
    /// Barra de reproducción para la ventana que muestra el video proyectado:
    /// pausa/reanudar, reiniciar y barra de posición con salto.
    /// </summary>
    private FrameworkElement BuildPlaybackBar()
    {
        var bar = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x0F, 0x14, 0x1A)),
            Padding = new Thickness(6, 3, 6, 3),
            CornerRadius = new CornerRadius(0, 0, 4, 4),
        };
        var row = new DockPanel { LastChildFill = true };
        bar.Child = row;

        // active: botón tipo "switch" encendido (bucle): fondo y color de acento.
        FrameworkElement MiniButton(string glyph, string tooltip, Action onClick, bool active = false)
        {
            var idleBackground = active ? (Brush)FindResource("Panel2Brush") : Brushes.Transparent;
            var text = new TextBlock
            {
                Text = glyph,
                FontSize = 11,
                Foreground = active ? (Brush)FindResource("AccentBrush") : Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var button = new Border
            {
                Width = 20,
                Height = 18,
                CornerRadius = new CornerRadius(3),
                Background = idleBackground,
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                Child = text,
                Margin = new Thickness(0, 0, 4, 0),
            };
            button.MouseEnter += (_, _) => button.Background = (Brush)FindResource("Panel2Brush");
            button.MouseLeave += (_, _) => button.Background = idleBackground;
            button.MouseLeftButtonDown += (_, e) => e.Handled = true;
            button.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
            return button;
        }

        var playPause = MiniButton(
            _projection.IsFilePaused ? "▶" : "⏸",
            _projection.IsFilePaused ? "Reanudar" : "Pausar (congela la imagen en el muro)",
            () => _ = TogglePlaybackPauseAsync());
        DockPanel.SetDock(playPause, Dock.Left);
        row.Children.Add(playPause);

        var restart = MiniButton("⟲", "Reproducir desde el inicio", () => _ = SeekProjectionAsync(0));
        DockPanel.SetDock(restart, Dock.Left);
        row.Children.Add(restart);

        var loop = MiniButton("🔁",
            _projection.LoopFile
                ? "Bucle activado: el video se repite sin fin (clic para desactivar)"
                : "Bucle desactivado: al terminar el video la transmisión se detiene sola (clic para activar)",
            () => _ = TogglePlaybackLoopAsync(),
            active: _projection.LoopFile);
        DockPanel.SetDock(loop, Dock.Left);
        row.Children.Add(loop);

        _playbackTime = new TextBlock
        {
            Foreground = (Brush)FindResource("MutedBrush"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Text = FormatPlaybackTime(_projection.FilePosition) +
                   (_projection.FileDuration > 0 ? $" / {FormatPlaybackTime(_projection.FileDuration)}" : ""),
        };
        DockPanel.SetDock(_playbackTime, Dock.Right);
        row.Children.Add(_playbackTime);

        if (_projection.FileDuration > 0)
        {
            _playbackSlider = new Slider
            {
                Minimum = 0,
                Maximum = _projection.FileDuration,
                Value = Math.Min(_projection.FilePosition, _projection.FileDuration),
                IsMoveToPointEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0),
            };
            // Mientras se arrastra no se pisa el valor con el timer; al soltar
            // se salta a esa posición (reinicia el stream y re-engancha el decoder).
            _playbackSlider.PreviewMouseLeftButtonDown += (_, _) => _playbackDragging = true;
            _playbackSlider.PreviewMouseLeftButtonUp += (_, _) =>
            {
                _playbackDragging = false;
                if (_playbackSlider is { } s) _ = SeekProjectionAsync(s.Value);
            };
            row.Children.Add(_playbackSlider);
        }
        else
        {
            row.Children.Add(new TextBlock
            {
                Text = _projection.ActiveDescription ?? "video",
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        // La barra no debe seleccionar la ventana ni disparar pantalla completa.
        bar.MouseLeftButtonDown += (_, e) => e.Handled = true;
        bar.MouseLeftButtonUp += (_, e) => e.Handled = true;
        return bar;
    }

    private static string FormatPlaybackTime(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    /// <summary>Refresco periódico de la barra (posición y tiempos).</summary>
    private void UpdatePlaybackBar()
    {
        if (!_projection.IsFileProjection) return;
        if (_playbackTime is { } time)
        {
            time.Text = FormatPlaybackTime(_projection.FilePosition) +
                        (_projection.FileDuration > 0 ? $" / {FormatPlaybackTime(_projection.FileDuration)}" : "");
        }
        if (_playbackSlider is { } slider && !_playbackDragging)
            slider.Value = Math.Min(_projection.FilePosition, slider.Maximum);
    }

    private async Task TogglePlaybackPauseAsync()
    {
        if (!_projection.IsFileProjection) return;
        if (_projection.IsFilePaused)
        {
            await SeekProjectionAsync(_projection.FilePosition);
        }
        else
        {
            try
            {
                Status("Pausando…");
                await _projection.PauseFileAsync();
                // El publicador cambió de proceso (video → fotograma fijo): se
                // re-asigna la ventana para que el decoder se re-enganche ya, en
                // vez de esperar a su propio reintento (y ver el error RTSP).
                await ReattachDecoderAsync();
                Status("Video en pausa (la imagen queda congelada en el muro).");
            }
            catch (Exception ex)
            {
                Status($"No se pudo pausar: {ex.Message}");
            }
            RenderWall();
        }
    }

    /// <summary>Re-asigna las ventanas de proyección para que el decoder reabra el stream RTSP.</summary>
    private async Task ReattachDecoderAsync()
    {
        if (_projectionUrl is not { } url) return;
        foreach (var target in _projectionTargets.ToList())
            await AssignProjectionToTargetAsync(target, url);
    }

    private Task AssignProjectionToTargetAsync(ProjectionTarget target, string url) =>
        target.Kind == TargetKind.Floating
            ? _api.WallAssignFloatingExternalAsync(target.WallId, target.Id, url, ProjectionSourceName)
            : _api.WallAssignExternalAsync(target.WallId, target.Id, url, ProjectionSourceName);

    /// <summary>Asignación actual del destino según el estado cacheado del wall (null si no existe/está vacío).</summary>
    private static AssignmentDto? CurrentAssignmentOf(WallDto wall, ProjectionTarget target) =>
        target.Kind == TargetKind.Floating
            ? wall.Floating?.FirstOrDefault(f => f.Id == target.Id)?.Assignment
            : wall.Screens.SelectMany(s => s.Windows).FirstOrDefault(w => w.Id == target.Id)?.Assignment;

    /// <summary>
    /// Switch de bucle. El flag se aplica al (re)iniciar ffmpeg: si el video
    /// está reproduciéndose se relanza en la posición actual (breve corte,
    /// como un salto); en pausa basta con cambiar el flag (se aplica al reanudar).
    /// </summary>
    private async Task TogglePlaybackLoopAsync()
    {
        if (!_projection.IsFileProjection) return;
        _projection.LoopFile = !_projection.LoopFile;
        _settings.ProjectionLoop = _projection.LoopFile;
        _settings.Save();

        string state = _projection.LoopFile ? "Bucle activado." : "Bucle desactivado: al terminar el video la transmisión se detiene sola.";
        if (_projection.IsFilePaused)
        {
            Status(state);
            RenderWall();
            return;
        }
        await SeekProjectionAsync(_projection.FilePosition);
        Status(state);
    }

    /// <summary>
    /// Salta/reanuda la reproducción: reinicia el stream en esa posición y
    /// re-engancha el decoder (vuelve a verse en el muro en ~2 s).
    /// </summary>
    private async Task SeekProjectionAsync(double seconds)
    {
        if (!_projection.IsFileProjection) return;
        try
        {
            Status("Moviendo la reproducción…");
            await _projection.SeekFileAsync(seconds);
            await ReattachDecoderAsync();
            Status($"Reproduciendo desde {FormatPlaybackTime(_projection.FilePosition)}.");
        }
        catch (Exception ex)
        {
            Status($"No se pudo mover la reproducción: {ex.Message}");
        }
        RenderWall();
    }

    // -----------------------------------------------------------------------
    // Intercambio y agrupación de ventanas
    // -----------------------------------------------------------------------

    /// <summary>Intercambia las cámaras de dos ventanas (arrastrar y soltar).</summary>
    private async Task SwapWindowsAsync(int windowAId, int windowBId)
    {
        if (_currentWall is null) return;
        try
        {
            Status("Intercambiando cámaras…");
            await _api.WallSwapAsync(_currentWall.Id, windowAId, windowBId);
            Status("Cámaras intercambiadas.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(HostWindow, ex.Message, "No se pudo intercambiar", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status("El intercambio falló.");
        }
    }

    /// <summary>Agrupa las ventanas seleccionadas (Ctrl+clic) de un monitor en una ventana grande.</summary>
    private Task GroupSelectedAsync(ScreenDto screen) =>
        GroupWindowIdsAsync(screen, screen.Windows.Where(w => _multiSelected.Contains(w.Id)).Select(w => w.Id).ToList());

    /// <summary>Confirma (si se liberan cámaras) y agrupa las ventanas indicadas.</summary>
    private async Task GroupWindowIdsAsync(ScreenDto screen, List<int> ids)
    {
        if (_currentWall is null || ids.Count < 2) return;

        int cameras = screen.Windows.Count(w => ids.Contains(w.Id) && w.Assignment is not null);
        if (cameras > 1)
        {
            var confirm = MessageBox.Show(HostWindow,
                $"La ventana agrupada muestra UNA cámara (la de la esquina superior izquierda del grupo); " +
                $"las otras {cameras - 1} cámara(s) seleccionadas se liberarán.\n\n¿Continuar?",
                "Agrupar ventanas", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
        }

        _multiSelected.Clear();
        if (GroupModeToggle.IsChecked == true)
            GroupModeToggle.IsChecked = false; // salir del modo al aplicar
        await RunScreenOperationAsync(screen.Id,
            () => _api.WallGroupAsync(_currentWall.Id, screen.Id, ids),
            $"Agrupando {ids.Count} ventanas en {screen.Label}…",
            $"Ventanas agrupadas en {screen.Label}.",
            "No se pudo agrupar");
    }

    private void OnGroupModeToggled(object sender, RoutedEventArgs e)
    {
        Status(GroupModeToggle.IsChecked == true
            ? "Modo agrupar: arrastre con el mouse sobre un monitor para dibujar el rectángulo de ventanas a fusionar."
            : "Modo agrupar desactivado.");
        RenderWall();
    }

    /// <summary>
    /// Capa de selección del modo agrupar: se arrastra un rectángulo sobre la
    /// grilla del monitor; las cards cubiertas se resaltan y al soltar se
    /// agrupan. Al ser un arrastre rectangular, la selección siempre es válida.
    /// </summary>
    private FrameworkElement BuildGroupSelectOverlay(
        ScreenDto screen, int gridCols, int gridRows, Dictionary<int, (WindowDto Win, Border Cell)> cellBySlot)
    {
        var overlay = new Canvas { Background = Brushes.Transparent, Cursor = Cursors.Cross };
        var band = new System.Windows.Shapes.Rectangle
        {
            Stroke = (Brush)FindResource("AccentBrush"),
            StrokeThickness = 1.5,
            Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x3B, 0x82, 0xF6)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        overlay.Children.Add(band);

        int? anchorSlot = null;
        var highlighted = new List<Border>();

        (int Row, int Col) SlotOf(Point p)
        {
            double cellWidth = Math.Max(1, overlay.ActualWidth) / gridCols;
            double cellHeight = Math.Max(1, overlay.ActualHeight) / gridRows;
            return (Math.Clamp((int)(p.Y / cellHeight), 0, gridRows - 1),
                    Math.Clamp((int)(p.X / cellWidth), 0, gridCols - 1));
        }

        void ClearHighlight()
        {
            foreach (var cell in highlighted)
                if (cell.Tag is Brush resting)
                {
                    cell.BorderBrush = resting;
                    cell.BorderThickness = new Thickness(1);
                }
            highlighted.Clear();
        }

        (int R0, int C0, int R1, int C1) BoxTo(Point current)
        {
            var (anchorRow, anchorCol) = (anchorSlot!.Value / gridCols, anchorSlot!.Value % gridCols);
            var (row, col) = SlotOf(current);
            return (Math.Min(anchorRow, row), Math.Min(anchorCol, col),
                    Math.Max(anchorRow, row), Math.Max(anchorCol, col));
        }

        void Update(Point current)
        {
            if (anchorSlot is null) return;
            var (r0, c0, r1, c1) = BoxTo(current);

            double cellWidth = overlay.ActualWidth / gridCols;
            double cellHeight = overlay.ActualHeight / gridRows;
            Canvas.SetLeft(band, c0 * cellWidth);
            Canvas.SetTop(band, r0 * cellHeight);
            band.Width = (c1 - c0 + 1) * cellWidth;
            band.Height = (r1 - r0 + 1) * cellHeight;
            band.Visibility = Visibility.Visible;

            ClearHighlight();
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                    if (cellBySlot.TryGetValue(r * gridCols + c, out var hit) && !highlighted.Contains(hit.Cell))
                    {
                        hit.Cell.BorderBrush = (Brush)FindResource("AccentBrush");
                        hit.Cell.BorderThickness = new Thickness(2);
                        highlighted.Add(hit.Cell);
                    }
        }

        overlay.MouseLeftButtonDown += (_, e) =>
        {
            var (row, col) = SlotOf(e.GetPosition(overlay));
            anchorSlot = row * gridCols + col;
            overlay.CaptureMouse();
            Update(e.GetPosition(overlay));
        };
        overlay.MouseMove += (_, e) =>
        {
            if (anchorSlot is not null) Update(e.GetPosition(overlay));
        };
        overlay.MouseLeftButtonUp += async (_, e) =>
        {
            if (anchorSlot is null) return;
            overlay.ReleaseMouseCapture();
            var (r0, c0, r1, c1) = BoxTo(e.GetPosition(overlay));
            anchorSlot = null;
            band.Visibility = Visibility.Collapsed;
            ClearHighlight();

            var ids = new List<int>();
            bool touchesGrouped = false;
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                    if (cellBySlot.TryGetValue(r * gridCols + c, out var hit))
                    {
                        if (hit.Win.SpanCols > 1 || hit.Win.SpanRows > 1) touchesGrouped = true;
                        if (!ids.Contains(hit.Win.Id)) ids.Add(hit.Win.Id);
                    }

            if (touchesGrouped)
            {
                Status("La selección toca una ventana ya agrupada; desagrúpela primero.");
                return;
            }
            if (ids.Count < 2)
            {
                Status("Arrastre para abarcar al menos dos ventanas.");
                return;
            }
            await GroupWindowIdsAsync(screen, ids);
        };

        return overlay;
    }

    /// <summary>Deshace la agrupación de una ventana (vuelve a sub-ventanas 1×1).</summary>
    private async Task UngroupAsync(ScreenDto screen, WindowDto win)
    {
        if (_currentWall is null) return;
        _multiSelected.Clear();
        await RunScreenOperationAsync(screen.Id,
            () => _api.WallUngroupAsync(_currentWall.Id, win.Id),
            $"Desagrupando la ventana en {screen.Label}…",
            $"Ventana desagrupada en {screen.Label}.",
            "No se pudo desagrupar");
    }

    /// <summary>Marca una ventana del wall como seleccionada y refresca el resaltado.</summary>
    private void SelectWindow(int windowId)
    {
        _selectedWindowId = windowId;
        RenderWall();
    }

    private static System.Windows.Media.Imaging.BitmapImage? DecodeJpeg(byte[] jpeg)
    {
        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new System.IO.MemoryStream(jpeg);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    /// <summary>Captura un fotograma nuevo (respetando el límite de concurrencia) y actualiza la caché.</summary>
    private async Task<System.Windows.Media.Imaging.BitmapImage?> FetchSnapshotAsync(int channelId)
    {
        await _snapshotGate.WaitAsync();
        try
        {
            if (!_camerasById.TryGetValue(channelId, out var camera)) return null;
            byte[]? jpeg = await _api.GetSnapshotAsync(camera.Device.Id, camera.Channel.ChannelNumber);
            if (jpeg is null) return null;
            var img = DecodeJpeg(jpeg);
            if (img is not null) _snapCache[channelId] = (img, DateTime.Now);
            return img;
        }
        finally { _snapshotGate.Release(); }
    }

    /// <summary>
    /// Carga la miniatura de una cámara en la ventana del wall (vista previa):
    /// muestra al instante la foto en caché y solo recaptura si está vencida.
    /// </summary>
    private async void LoadCellSnapshotAsync(Image img, TextBlock loading, int channelId, int generation)
    {
        // 1) Mostrar de inmediato la foto en caché, si existe.
        bool fresh = false;
        if (_snapCache.TryGetValue(channelId, out var cached))
        {
            img.Source = cached.Img;
            loading.Visibility = Visibility.Collapsed;
            fresh = DateTime.Now - cached.At < SnapFreshness;
        }
        if (fresh) return; // la caché aún es reciente: no recapturar

        // 2) Capturar una nueva en segundo plano y actualizar.
        var updated = await FetchSnapshotAsync(channelId);
        if (generation != _previewGeneration) return; // se apagó/refrescó la vista previa
        if (updated is not null)
        {
            img.Source = updated;
            loading.Visibility = Visibility.Collapsed;
        }
        else if (img.Source is null)
        {
            loading.Text = "sin imagen";
        }
    }

    /// <summary>Resalta y hace visible la fila del canal en la lista de fuentes.</summary>
    private void HighlightChannel(int channelId)
    {
        // Si hay una búsqueda en curso que oculta este canal, se cancela para
        // que el canal seleccionado quede visible. Clear() dispara el
        // TextChanged que reconstruye la lista (y los mapas de filas).
        if (!_channelRows.ContainsKey(channelId) && !string.IsNullOrEmpty(ChannelFilterBox.Text))
            ChannelFilterBox.Clear();

        if (_highlightedRow is not null)
            _highlightedRow.BorderBrush = (Brush)FindResource("BorderBrush");

        if (_channelExpanders.TryGetValue(channelId, out var expander))
            expander.IsExpanded = true;

        if (_channelRows.TryGetValue(channelId, out var row))
        {
            row.BorderBrush = (Brush)FindResource("AccentBrush");
            _highlightedRow = row;
            // El grupo recién se expandió: se difiere el scroll hasta que el
            // layout se recalcule, si no BringIntoView desplaza a una posición vieja.
            Dispatcher.BeginInvoke(new Action(() => row.BringIntoView()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            _highlightedRow = null;
        }
    }

    // -----------------------------------------------------------------------
    // Proyección de pantalla / video a una ventana del wall
    // -----------------------------------------------------------------------

    private static string ProjectionSourceName => $"Pantalla - {Environment.MachineName}";

    // La fuente de proyección es interna (Enabled=false): no aparece en los
    // paneles de canales. Se recuerda a qué ventanas se transmitió y qué tenía
    // asignado cada una antes: la transmisión es temporal y al detenerla cada
    // ventana vuelve a su canal anterior (o queda libre si no tenía ninguno).
    // Puede haber varias ventanas destino (Ctrl+clic): todas reciben el mismo
    // stream RTSP; el decoder abre una sesión por ventana. Un destino puede ser
    // también una ventana flotante; si se creó para la proyección
    // (DeleteOnStop) se cierra al detener en vez de restaurar un canal.
    private enum TargetKind { Window, Floating }
    private sealed record ProjectionTarget(
        int WallId, TargetKind Kind, int Id, string Label, AssignmentDto? Previous, bool DeleteOnStop = false);
    /// <summary>
    /// URL RTSP que este PC está publicando mientras proyecta. Identifica la
    /// transmisión en el estado del muro: una ventana la muestra cuando su
    /// asignación es externa con esta misma URL.
    /// </summary>
    private string? _projectionUrl;
    private readonly List<ProjectionTarget> _projectionTargets = new();

    /// <summary>Destino "principal" de la proyección (donde se muestra la barra de reproducción).</summary>
    private ProjectionTarget? PrimaryProjectionTarget => _projectionTargets.Count > 0 ? _projectionTargets[0] : null;

    private bool IsPrimaryProjectionTarget(TargetKind kind, int id) =>
        PrimaryProjectionTarget is { } p && p.Kind == kind && p.Id == id;

    private async void OnProjectToggled(object sender, RoutedEventArgs e)
    {
        if (_projectToggleGuard) return;

        if (ProjectToggle.IsChecked == true)
        {
            bool started = await StartProjectionFlowAsync();
            if (started)
            {
                ProjectToggle.Content = "🖥 Proyectando";
            }
            else
            {
                _projectToggleGuard = true;
                ProjectToggle.IsChecked = false;
                _projectToggleGuard = false;
            }
        }
        else
        {
            string? restored = await StopProjectionAsync();
            ProjectToggle.Content = "🖥 Proyectar";
            Status(restored is null
                ? "Transmisión detenida."
                : $"Transmisión detenida; vuelve a {restored}.");
        }
    }

    /// <summary>
    /// Fin de la transmisión por sí sola (video sin bucle que terminó): se
    /// restauran las ventanas y se desmarca el botón de proyectar.
    /// </summary>
    private async Task FinishProjectionAsync(string reason)
    {
        // Nunca debe lanzar: se llama desde handlers async void (✕, push de
        // estado, fin de video); una excepción aquí tumbaría la aplicación.
        try
        {
            string? restored = await StopProjectionAsync();
            _projectToggleGuard = true;
            ProjectToggle.IsChecked = false;
            _projectToggleGuard = false;
            ProjectToggle.Content = "🖥 Proyectar";
            Status(restored is null ? reason : $"{reason} Vuelve a {restored}.");
        }
        catch (Exception ex)
        {
            Status($"{reason} (no se pudo restaurar alguna ventana: {ex.Message})");
        }
        RenderWall();
    }

    private async Task<bool> StartProjectionFlowAsync()
    {
        if (_currentWall is null) return false;

        // Destinos: la ventana seleccionada (clic) y/o las de la selección
        // múltiple (Ctrl+clic). Todas reciben el mismo stream.
        var targets = _currentWall.Screens
            .SelectMany(s => s.Windows.Select(w => (Screen: s, Win: w)))
            .Where(t => t.Win.Id == _selectedWindowId || _multiSelected.Contains(t.Win.Id))
            .OrderBy(t => t.Win.Id == _selectedWindowId ? 0 : 1) // la del clic simple es la principal
            .ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(HostWindow,
                "Primero haga clic en la ventana del muro donde quiere proyectar (queda resaltada; " +
                "Ctrl+clic para elegir varias) y vuelva a pulsar 🖥 Proyectar.",
                "Proyectar", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        static string LabelOf((ScreenDto Screen, WindowDto Win) t) => WindowLabel(t.Screen, t.Win);
        string targetLabel = targets.Count == 1
            ? LabelOf(targets[0])
            : $"{targets.Count} ventanas ({string.Join(", ", targets.Select(LabelOf))})";
        var screenIds = targets.Select(t => t.Screen.Id).Distinct().ToList();
        int wallId = _currentWall.Id;

        return await RunProjectionStartAsync(targetLabel, screenIds, targets.Count, async url =>
        {
            var created = new List<ProjectionTarget>();
            foreach (var t in targets)
            {
                // Lo que mostraba la ventana antes de proyectar, para restaurarlo
                // al detener. Si ya mostraba esta misma proyección (p. ej. quedó
                // así tras un cierre brusco) no hay nada que restaurar.
                var previous = t.Win.Assignment is { } prev && prev.ExternalUrl != url ? prev : null;
                var target = new ProjectionTarget(wallId, TargetKind.Window, t.Win.Id, LabelOf(t), previous);
                await AssignProjectionToTargetAsync(target, url);
                created.Add(target);
                _projectionTargets.Add(target); // ya asignada: si algo falla después, se restaura
            }
            return created;
        });
    }

    /// <summary>
    /// Proyección dentro de una ventana flotante nueva (dibujada por el
    /// usuario). La flotante se crea con la fuente de proyección y es temporal:
    /// al detener la transmisión se cierra.
    /// </summary>
    private async Task<bool> StartProjectionToNewFloatingAsync(double x, double y, double w, double h)
    {
        if (_currentWall is null) return false;
        int wallId = _currentWall.Id;
        // No hay pantalla concreta: la flotante va sobre todo el muro, así que el
        // overlay de carga se muestra en todas las pantallas del wall.
        var screenIds = _currentWall.Screens.Select(s => s.Id).ToList();

        return await RunProjectionStartAsync("la ventana flotante", screenIds, 1, async url =>
        {
            await _api.WallCreateFloatingExternalAsync(wallId, x, y, w, h, url, ProjectionSourceName);

            // El POST no devuelve el id: se relee el wall y se busca la flotante
            // recién creada con la fuente de proyección (que no es de otro destino).
            var wall = await _api.GetWallAsync(wallId);
            var known = _projectionTargets.Where(t => t.Kind == TargetKind.Floating).Select(t => t.Id).ToHashSet();
            var floating = wall.Floating?
                .Where(f => f.Assignment?.ExternalUrl == url && !known.Contains(f.Id))
                .OrderByDescending(f => f.Id)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("La ventana flotante se creó pero no se pudo localizar en el estado del muro.");

            var target = new ProjectionTarget(wallId, TargetKind.Floating, floating.Id, "ventana flotante", null, DeleteOnStop: true);
            _projectionTargets.Add(target);
            return new List<ProjectionTarget> { target };
        });
    }

    /// <summary>
    /// Núcleo común del inicio de una proyección: pide qué transmitir
    /// (ProjectionDialog), arranca MediaMTX/ffmpeg, registra la fuente,
    /// delega la asignación de destinos y espera a que el decoder se conecte.
    /// <paramref name="assignTargets"/> recibe el id de la fuente de proyección
    /// y debe asignarla a los destinos (agregándolos a _projectionTargets a
    /// medida que se asignan, para poder restaurar si algo falla a mitad).
    /// </summary>
    private async Task<bool> RunProjectionStartAsync(string targetLabel, IReadOnlyList<int> screenIds,
        int expectedReaders, Func<string, Task<List<ProjectionTarget>>> assignTargets)
    {
        var monitors = ScreenProjectionService.GetMonitors();
        if (monitors.Count == 0)
        {
            MessageBox.Show(HostWindow, "No se detectaron monitores.", "Proyectar",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // La última IP que funcionó va primero (en redes con router/NAT la IP
        // correcta es la del router visto desde el decoder, no autodetectable).
        var ips = ScreenProjectionService.GetLocalIPv4Candidates();
        if (!string.IsNullOrWhiteSpace(_settings.ProjectionIp))
        {
            ips.Remove(_settings.ProjectionIp!);
            ips.Insert(0, _settings.ProjectionIp!);
        }

        var dialog = new ProjectionDialog(monitors, ips, targetLabel, loopDefault: _settings.ProjectionLoop)
        {
            Owner = HostWindow,
        };
        if (dialog.ShowDialog() != true) return false;

        // Entre pulsar "Iniciar" y ver la imagen en el muro pasan varios
        // segundos (arranque de MediaMTX/ffmpeg, registro de la fuente,
        // asignación y conexión del decoder). Se muestra el overlay de carga
        // sobre las pantallas destino durante todo el proceso para que no
        // parezca que no pasó nada.
        int busyScreenId = screenIds.FirstOrDefault(id => _busyScreens.ContainsKey(id), -1);
        if (busyScreenId >= 0)
        {
            string label = _currentWall?.Screens.FirstOrDefault(s => s.Id == busyScreenId)?.Label ?? "La pantalla";
            MessageBox.Show(HostWindow, $"{label} tiene una operación en curso. Espere a que termine.",
                "Proyectar", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        void SetBusyAll(string text) { foreach (int id in screenIds) _busyScreens[id] = text; RenderWall(); }

        // Si había una transmisión previa (p. ej. se inicia otra desde una
        // flotante), se cierra antes restaurando sus destinos.
        if (_projectionTargets.Count > 0) await StopProjectionAsync();

        ProjectToggle.IsEnabled = false;
        SetBusyAll("Iniciando transmisión…");
        try
        {
            Status("Iniciando transmisión…");
            _projection.LoopFile = dialog.LoopFile;
            if (dialog.SelectedMonitor is { } monitor)
                await _projection.StartMonitorAsync(monitor);
            else
                await _projection.StartFileAsync(dialog.SelectedFile!);

            SetBusyAll("Asignando la fuente…");
            string url = ProjectionUrl(dialog.SelectedIp);
            _projectionUrl = url;
            _projectionTargets.Clear();
            await assignTargets(url);

            _settings.ProjectionIp = dialog.SelectedIp;
            _settings.ProjectionLoop = dialog.LoopFile;
            _settings.Save();

            // Ahora toca esperar a que el decoder venga a leer el stream: recién
            // ahí aparece la imagen en el muro. MediaMTX lo reporta en su log
            // (una sesión por ventana). Si abre menos sesiones de las esperadas
            // pero al menos una, se da por bueno.
            SetBusyAll("Esperando que el decoder se conecte…");
            Status("Transmisión iniciada; esperando que el decoder se conecte…");
            bool? connected = await _projection.WaitForReadersAsync(expectedReaders, TimeSpan.FromSeconds(25));
            if (connected == false && _projection.ReadersSinceStart > 0) connected = true;
            // Sin visibilidad del log (MediaMTX externo) se da un margen fijo
            // razonable para que el decoder enganche antes de quitar el overlay.
            if (connected is null) await Task.Delay(TimeSpan.FromSeconds(4));

            string transmitting = $"Transmitiendo {_projection.ActiveDescription} en {targetLabel}.";
            if (connected == false)
            {
                Status($"{transmitting} El decoder aún no se conecta: revise que la IP {dialog.SelectedIp} sea alcanzable desde el decoder y que el firewall permita mediamtx.exe.");
                MessageBox.Show(HostWindow,
                    $"La transmisión está activa pero el decoder todavía no se conectó a este PC.\n\n" +
                    $"Compruebe que la IP indicada ({dialog.SelectedIp}) sea alcanzable desde el decoder y que el firewall permita mediamtx.exe (puerto 8554/TCP).\n\n" +
                    "La transmisión sigue en marcha; si el decoder se conecta más tarde, la imagen aparecerá sola.",
                    "Proyectar", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                string loopHint = _projection.IsFileProjection && !_projection.LoopFile
                    ? " Al terminar el video la transmisión se detiene sola."
                    : "";
                Status($"{transmitting}{loopHint}");
            }
            _projectToggleGuard = true;
            ProjectToggle.IsChecked = true;
            _projectToggleGuard = false;
            ProjectToggle.Content = "🖥 Proyectando";
            return true;
        }
        catch (Exception ex)
        {
            // Si algún destino ya se había asignado, se restaura lo anterior.
            await StopProjectionAsync();
            MessageBox.Show(HostWindow, ex.Message, "No se pudo iniciar la transmisión",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Status("La transmisión no pudo iniciarse.");
            return false;
        }
        finally
        {
            foreach (int id in screenIds) _busyScreens.Remove(id);
            RenderWall();
            ProjectToggle.IsEnabled = true;
        }
    }

    /// <summary>Muestra/actualiza el overlay de carga de una pantalla con el texto indicado.</summary>
    private void SetBusy(int screenId, string text)
    {
        _busyScreens[screenId] = text;
        RenderWall();
    }

    /// <summary>
    /// Detiene la captura y, para cada ventana del wall que aún muestre esta
    /// proyección, la devuelve al canal que tenía antes de transmitir (o la
    /// libera si no tenía ninguno). Devuelve un texto con lo que se restauró
    /// (o null si no se restauró ningún canal).
    /// </summary>
    private async Task<string?> StopProjectionAsync()
    {
        _projection.Stop();

        // Copia + vaciado ANTES de cualquier await: mientras se restauran las
        // ventanas llegan pushes de estado del wall (mismo hilo de UI) que
        // recorren/modifican _projectionTargets; iterar la lista viva aquí
        // terminaba en "colección modificada" y tumbaba la aplicación.
        var targets = _projectionTargets.ToList();
        _projectionTargets.Clear();

        var restored = new List<string>();
        if (_projectionUrl is { } projectionUrl)
        {
            foreach (var target in targets)
            {
                var wall = _walls.FirstOrDefault(w => w.Id == target.WallId);
                if (wall is null || CurrentAssignmentOf(wall, target)?.ExternalUrl != projectionUrl)
                    continue; // la reasignaron/cerraron mientras tanto: no se toca

                try
                {
                    string? channel = await RestoreTargetAsync(target);
                    if (channel is not null)
                        restored.Add(targets.Count == 1 ? channel : $"{target.Label} → {channel}");
                }
                catch { /* mejor esfuerzo: la ventana pudo ser reasignada o el server no estar */ }
            }
        }
        return restored.Count == 0 ? null : string.Join("; ", restored);
    }

    /// <summary>
    /// Devuelve un destino a su estado previo a la proyección: canal anterior,
    /// libre si no tenía, o cerrada si es una flotante creada para proyectar.
    /// Devuelve la descripción del canal restaurado (null si se liberó/cerró).
    /// </summary>
    private async Task<string?> RestoreTargetAsync(ProjectionTarget target)
    {
        if (target.Kind == TargetKind.Floating)
        {
            if (target.DeleteOnStop || target.Previous is null)
            {
                await _api.WallDeleteFloatingAsync(target.WallId, target.Id);
                return null;
            }
            var fp = target.Previous;
            await _api.WallAssignFloatingAsync(target.WallId, target.Id, fp.ChannelId, fp.StreamType);
            return DescribeChannel(fp);
        }

        if (target.Previous is { } prev)
        {
            await _api.WallAssignAsync(target.WallId, target.Id, prev.ChannelId, prev.StreamType);
            return DescribeChannel(prev);
        }
        await _api.WallClearAsync(target.WallId, target.Id);
        return null;
    }

    private static string DescribeChannel(AssignmentDto a) =>
        a.IsExternal ? a.ChannelName : $"{a.ChannelName} ({a.DeviceName} · ch {a.ChannelNumber})";

    /// <summary>Segunda línea de una ventana: equipo y canal, o el rótulo de la proyección.</summary>
    private static string SubtitleOf(AssignmentDto a) =>
        a.IsExternal
            ? "transmisión de este puesto"
            : $"{a.DeviceName} · ch {a.ChannelNumber}{(a.StreamType == 1 ? " · sub" : "")}";

    /// <summary>
    /// URL RTSP que publica este PC mientras proyecta. No se registra como
    /// dispositivo del inventario: es temporal y solo existe mientras dura la
    /// transmisión, así que el muro la guarda como fuente externa por URL.
    /// </summary>
    private string ProjectionUrl(string hostIp) =>
        $"rtsp://{hostIp}:{ScreenProjectionService.RtspPort}/{_projection.PublishPath}";

    private async Task ToggleFullscreenAsync(ScreenDto screen, WindowDto win)
    {
        if (_currentWall is null) return;

        if (screen.Fullscreen)
        {
            await RunScreenOperationAsync(screen.Id,
                () => _api.WallExitFullscreenAsync(_currentWall.Id, screen.Id),
                $"Restaurando layout de {screen.Label}…",
                $"{screen.Label} restaurado.",
                "No se pudo restaurar el layout");
            return;
        }

        if (win.Assignment is null)
        {
            Status("Doble clic sobre una ventana con cámara para verla en pantalla completa.");
            return;
        }

        await RunScreenOperationAsync(screen.Id,
            () => _api.WallFullscreenAsync(_currentWall.Id, win.Id),
            $"{win.Assignment.ChannelName} a pantalla completa en {screen.Label}…",
            $"{win.Assignment.ChannelName} en pantalla completa.",
            "No se pudo poner en pantalla completa");
    }

    // -----------------------------------------------------------------------
    // Operaciones
    // -----------------------------------------------------------------------
    private async Task AssignAsync(WindowDto win, int channelId, int streamType)
    {
        if (_currentWall is null) return;
        _camerasById.TryGetValue(channelId, out var camera);
        try
        {
            Status($"Enviando {camera?.Label ?? $"canal {channelId}"} a la ventana {win.WindowIndex + 1}…");
            await _api.WallAssignAsync(_currentWall.Id, win.Id, channelId, streamType);
            Status($"{camera?.Label ?? $"Canal {channelId}"} en pantalla.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(HostWindow, ex.Message, "No se pudo asignar", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status("La asignación falló.");
        }
    }

    private async Task ClearWindowAsync(WindowDto win)
    {
        if (_currentWall is null || win.Assignment is null) return;

        // Si la ventana muestra la proyección de este cliente, el ✕ equivale a
        // terminar la transmisión en esa ventana: vuelve a su canal anterior y,
        // si era la última ventana destino, se detiene la transmisión completa
        // (ffmpeg, botón Proyectar).
        if (IsProjectionWindow(win))
        {
            await RemoveProjectionTargetAsync(TargetKind.Window, win.Id);
            return;
        }

        try
        {
            await _api.WallClearAsync(_currentWall.Id, win.Id);
            Status($"Ventana {win.WindowIndex + 1} liberada.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(HostWindow, ex.Message, "No se pudo liberar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool IsProjectionWindow(WindowDto win) =>
        _projectionUrl is { } projectionUrl &&
        win.Assignment?.ExternalUrl == projectionUrl &&
        _projectionTargets.Any(t => t.Kind == TargetKind.Window && t.Id == win.Id);

    private bool IsProjectionFloating(FloatingWindowDto floating) =>
        _projectionUrl is { } projectionUrl &&
        floating.Assignment?.ExternalUrl == projectionUrl &&
        _projectionTargets.Any(t => t.Kind == TargetKind.Floating && t.Id == floating.Id);

    /// <summary>
    /// Quita un destino de la proyección: lo devuelve a su canal anterior (o lo
    /// libera / cierra la flotante). Si no quedan más destinos, detiene la
    /// transmisión.
    /// </summary>
    private async Task RemoveProjectionTargetAsync(TargetKind kind, int id)
    {
        var target = _projectionTargets.FirstOrDefault(t => t.Kind == kind && t.Id == id);
        if (target is null) return;

        if (_projectionTargets.Count == 1)
        {
            await FinishProjectionAsync("Transmisión detenida.");
            return;
        }

        // Se quita de la lista ANTES del await (el push de estado que provoca la
        // restauración recorre la lista en el hilo de UI).
        _projectionTargets.Remove(target);
        try
        {
            string? channel = await RestoreTargetAsync(target);
            Status(channel is not null
                ? $"{target.Label} vuelve a {channel}; la transmisión sigue en los demás destinos."
                : $"{target.Label} liberada; la transmisión sigue en los demás destinos.");
        }
        catch (Exception ex)
        {
            _projectionTargets.Add(target); // sigue mostrando la proyección
            MessageBox.Show(HostWindow, ex.Message, "No se pudo liberar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RenderWall();
    }

    private async void OnClearAllClick(object sender, RoutedEventArgs e)
    {
        if (_currentWall is null) return;
        if (MessageBox.Show(HostWindow, $"¿Detener todas las ventanas de \"{_currentWall.Name}\"?",
                "Limpiar muro", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            await _api.WallClearAllAsync(_currentWall.Id);
            // Si la proyección iba a este wall, se detiene también (el usuario
            // pidió dejar todo vacío: no se restauran los canales anteriores).
            if (_projectionTargets.Any(t => t.WallId == _currentWall.Id))
            {
                _projectionTargets.Clear();
                await FinishProjectionAsync("Muro limpiado; transmisión detenida.");
                return;
            }
            Status("Muro limpiado.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(HostWindow, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnSyncClick(object sender, RoutedEventArgs e)
    {
        if (_currentWall is null) return;
        try
        {
            SyncButton.IsEnabled = false;
            Status("Sincronizando división de ventanas con el decoder…");
            await _api.WallSyncAsync(_currentWall.Id);
            Status("División de ventanas enviada al decoder.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(HostWindow, ex.Message, "Sincronización", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status("La sincronización falló.");
        }
        finally
        {
            SyncButton.IsEnabled = true;
        }
    }

    private void OnLayoutsClick(object sender, RoutedEventArgs e)
    {
        if (_currentWall is null) return;
        var win = new WallLayoutsWindow(_api, _currentWall) { Owner = HostWindow };
        win.ShowDialog();
    }

    /// <summary>
    /// Bloquea toda la ventana con un velo de progreso mientras corre una
    /// operación larga (p. ej. aplicar un layout), para que el operador no
    /// haga cambios sobre el muro a medio aplicar.
    /// </summary>
    public async Task RunBlockingAsync(string message, Func<Task> action)
    {
        BlockingText.Text = message;
        BlockingOverlay.Visibility = Visibility.Visible;
        try
        {
            await action();
        }
        finally
        {
            BlockingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    // -----------------------------------------------------------------------
    // Ventanas flotantes: dibujadas sobre el muro, encima de los demás videos
    // -----------------------------------------------------------------------

    private void OnFloatModeToggled(object sender, RoutedEventArgs e)
    {
        Status(FloatModeToggle.IsChecked == true
            ? "Modo flotante: dibuje un rectángulo sobre el muro (puede cruzar monitores); al soltar elija la cámara."
            : "Modo flotante desactivado.");
        RenderFloatLayer();
    }

    private void OnFloatLayerSizeChanged(object sender, SizeChangedEventArgs e) => RenderFloatLayer();

    /// <summary>Redibuja las tarjetas de las ventanas flotantes sobre la grilla del wall.</summary>
    private void RenderFloatLayer()
    {
        FloatLayer.Children.Clear();
        bool drawMode = FloatModeToggle.IsChecked == true;
        // Sin fondo, la capa deja pasar el mouse a los monitores; en modo
        // dibujo se vuelve transparente-activa para capturar el arrastre.
        FloatLayer.Background = drawMode ? Brushes.Transparent : null;
        FloatLayer.Cursor = drawMode ? Cursors.Cross : Cursors.Arrow;

        if (_currentWall is null) return;
        double width = FloatLayer.ActualWidth, height = FloatLayer.ActualHeight;
        if (width < 16 || height < 16) return; // el primer layout dispara SizeChanged

        foreach (var floating in _currentWall.Floating ?? Array.Empty<FloatingWindowDto>().ToList())
            FloatLayer.Children.Add(BuildFloatingCard(floating, width, height));
    }

    /// <summary>Tarjeta de una ventana flotante: arrastrable, redimensionable y con ✕.</summary>
    private Border BuildFloatingCard(FloatingWindowDto floating, double layerWidth, double layerHeight)
    {
        var wall = _currentWall!;
        double unitX = layerWidth / Math.Max(1, wall.Columns);
        double unitY = layerHeight / Math.Max(1, wall.Rows);

        var card = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0xE2, 0x14, 0x1C, 0x26)),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(2),
            Cursor = Cursors.SizeAll,
            Width = Math.Max(46, floating.W * unitX),
            Height = Math.Max(34, floating.H * unitY),
            AllowDrop = true,
        };
        Canvas.SetLeft(card, floating.X * unitX);
        Canvas.SetTop(card, floating.Y * unitY);

        var host = new Grid();
        card.Child = host;

        var label = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        string title = floating.Assignment is { } a ? a.ChannelName : "sin cámara";
        label.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = (Brush)FindResource("AccentBrush"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 170,
        });
        if (floating.Assignment is { } asg)
        {
            label.Children.Add(new TextBlock
            {
                Text = SubtitleOf(asg),
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                MaxWidth = 170,
            });
        }
        host.Children.Add(label);

        host.Children.Add(new Border
        {
            Background = (Brush)FindResource("Accent2Brush"),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(4, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = floating.Fullscreen ? "FLOTANTE · PANTALLA COMPLETA" : "FLOTANTE",
                FontSize = 8.5,
                Foreground = Brushes.White,
            },
        });

        bool isProjection = IsProjectionFloating(floating);
        if (floating.Assignment is { IsExternal: false } tip && !isProjection)
            AttachSnapshotTooltip(card, tip.ChannelId,
                $"Flotante: {tip.ChannelName} · {tip.DeviceName} ch {tip.ChannelNumber}");

        // Controles de reproducción si esta flotante es el destino principal de
        // un video proyectado desde este cliente.
        if (isProjection && IsPrimaryProjectionTarget(TargetKind.Floating, floating.Id) && _projection.IsFileProjection)
        {
            var bar = BuildPlaybackBar();
            bar.Margin = new Thickness(0, 0, 16, 0); // deja libre el agarre de redimensión
            host.Children.Add(bar);
        }

        // ✕ cerrar (quita la ventana del muro).
        var closeLabel = new TextBlock
        {
            Text = "✕",
            FontSize = 10,
            Foreground = (Brush)FindResource("MutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0),
        };
        var close = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x1A, 0x22, 0x2E)),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            // Corrido de la esquina: el vértice queda libre como agarre de
            // redimensión (la flotante se estira desde cualquier esquina).
            Margin = new Thickness(0, 3, 19, 0),
            Cursor = Cursors.Hand,
            ToolTip = isProjection
                ? "Detener la transmisión en esta ventana flotante"
                : "Cerrar la ventana flotante",
            Child = closeLabel,
        };
        close.MouseEnter += (_, _) => { closeLabel.Foreground = Brushes.White; close.Background = (Brush)FindResource("DangerBrush"); };
        close.MouseLeave += (_, _) => { closeLabel.Foreground = (Brush)FindResource("MutedBrush"); close.Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x1A, 0x22, 0x2E)); };
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        close.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            // Si es un destino de la proyección, el ✕ termina la transmisión ahí
            // (y cierra/restaura la flotante según corresponda).
            if (IsProjectionFloating(floating))
                await RemoveProjectionTargetAsync(TargetKind.Floating, floating.Id);
            else
                await DeleteFloatingAsync(floating);
        };
        host.Children.Add(close);

        // Glifo de redimensión (decorativo): el agarre real es cualquiera de
        // las cuatro esquinas de la tarjeta, detectado por zona más abajo.
        var grip = new Border
        {
            Width = 14,
            Height = 14,
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 10 2 L 2 10 M 10 6 L 6 10"),
                Stroke = (Brush)FindResource("MutedBrush"),
                StrokeThickness = 1.5,
                Margin = new Thickness(0, 0, 2, 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
            },
        };
        host.Children.Add(grip);

        // Drop de un canal de la lista: cambiar la cámara de la flotante.
        card.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        card.Drop += async (_, e) =>
        {
            if (e.Data.GetData(DragFormat) is not string payload || !int.TryParse(payload, out int channelId))
                return;
            await AssignFloatingAsync(floating, channelId);
        };

        // Arrastre para mover y CUALQUIER esquina para redimensionar: la
        // tarjeta se mueve en vivo y al soltar se envía el rect nuevo
        // (roaming en el muro). La esquina opuesta queda anclada.
        const double gripZone = 14;
        Point? pressAt = null;
        (bool L, bool T, bool R, bool B)? resizeCorner = null;
        double startLeft = 0, startTop = 0, startWidth = 0, startHeight = 0;
        bool moved = false;

        (bool L, bool T, bool R, bool B)? CornerAt(Point p)
        {
            bool l = p.X <= gripZone, r = !l && p.X >= card.Width - gripZone;
            bool t = p.Y <= gripZone, b = !t && p.Y >= card.Height - gripZone;
            return (l || r) && (t || b) ? (l, t, r, b) : null;
        }

        void BeginGesture(MouseButtonEventArgs e, (bool L, bool T, bool R, bool B)? corner)
        {
            pressAt = e.GetPosition(FloatLayer);
            resizeCorner = corner;
            moved = false;
            startLeft = Canvas.GetLeft(card);
            startTop = Canvas.GetTop(card);
            startWidth = card.Width;
            startHeight = card.Height;
            card.CaptureMouse();
            e.Handled = true;
        }

        card.MouseLeftButtonDown += async (_, e) =>
        {
            // Doble clic: pantalla completa sobre todas las pantallas que la
            // flotante toca; otro doble clic la devuelve a su rect original.
            if (e.ClickCount == 2)
            {
                pressAt = null;
                card.ReleaseMouseCapture();
                e.Handled = true;
                await ToggleFloatingFullscreenAsync(floating);
                return;
            }
            BeginGesture(e, CornerAt(e.GetPosition(card)));
        };
        card.MouseMove += (_, e) =>
        {
            if (pressAt is not Point start || e.LeftButton != MouseButtonState.Pressed)
            {
                // Sin gesto activo: el cursor anticipa qué hará el clic.
                var hover = CornerAt(e.GetPosition(card));
                card.Cursor = hover is { } hz
                    ? (hz.L == hz.T ? Cursors.SizeNWSE : Cursors.SizeNESW)
                    : Cursors.SizeAll;
                return;
            }
            var pos = e.GetPosition(FloatLayer);
            double dx = pos.X - start.X, dy = pos.Y - start.Y;
            if (Math.Abs(dx) > 2 || Math.Abs(dy) > 2) moved = true;
            if (resizeCorner is { } c)
            {
                double newLeft = startLeft, newTop = startTop;
                double newWidth = startWidth, newHeight = startHeight;
                if (c.R) newWidth = Math.Clamp(startWidth + dx, 46, layerWidth - startLeft);
                if (c.B) newHeight = Math.Clamp(startHeight + dy, 34, layerHeight - startTop);
                if (c.L)
                {
                    newWidth = Math.Clamp(startWidth - dx, 46, startLeft + startWidth);
                    newLeft = startLeft + startWidth - newWidth;
                }
                if (c.T)
                {
                    newHeight = Math.Clamp(startHeight - dy, 34, startTop + startHeight);
                    newTop = startTop + startHeight - newHeight;
                }
                card.Width = newWidth;
                card.Height = newHeight;
                Canvas.SetLeft(card, newLeft);
                Canvas.SetTop(card, newTop);
            }
            else
            {
                Canvas.SetLeft(card, Math.Clamp(startLeft + dx, 0, layerWidth - card.Width));
                Canvas.SetTop(card, Math.Clamp(startTop + dy, 0, layerHeight - card.Height));
            }
        };
        card.MouseLeftButtonUp += async (_, e) =>
        {
            if (pressAt is null) return;
            card.ReleaseMouseCapture();
            pressAt = null;
            if (!moved) return;
            double x = Canvas.GetLeft(card) / unitX;
            double y = Canvas.GetTop(card) / unitY;
            double w = card.Width / unitX;
            double h = card.Height / unitY;
            await MoveFloatingAsync(floating, x, y, w, h);
        };

        return card;
    }

    private async Task ToggleFloatingFullscreenAsync(FloatingWindowDto floating)
    {
        if (_currentWall is null) return;
        try
        {
            Status(floating.Fullscreen
                ? "Restaurando la ventana flotante a su tamaño…"
                : "Ventana flotante a pantalla completa…");
            await _api.WallToggleFloatingFullscreenAsync(_currentWall.Id, floating.Id);
            Status(floating.Fullscreen
                ? "Ventana flotante restaurada (su video no se cortó)."
                : "Ventana flotante en pantalla completa (su video no se cortó).");
        }
        catch (Exception ex)
        {
            MessageBox.Show(HostWindow, ex.Message, "No se pudo cambiar la flotante", MessageBoxButton.OK, MessageBoxImage.Warning);
            RenderFloatLayer(); // volver a la posición real
        }
    }

    private async Task MoveFloatingAsync(FloatingWindowDto floating, double x, double y, double w, double h)
    {
        if (_currentWall is null) return;
        try
        {
            Status("Moviendo la ventana flotante…");
            await _api.WallMoveFloatingAsync(_currentWall.Id, floating.Id, x, y, w, h);
            Status("Ventana flotante reubicada (su video no se cortó).");
        }
        catch (Exception ex)
        {
            MessageBox.Show(HostWindow, ex.Message, "No se pudo mover", MessageBoxButton.OK, MessageBoxImage.Warning);
            RenderFloatLayer(); // volver a la posición real
        }
    }

    private async Task AssignFloatingAsync(FloatingWindowDto floating, int channelId, int streamType = 0)
    {
        if (_currentWall is null) return;
        _camerasById.TryGetValue(channelId, out var camera);
        try
        {
            Status($"Enviando {camera?.Label ?? $"canal {channelId}"} a la ventana flotante…");
            await _api.WallAssignFloatingAsync(_currentWall.Id, floating.Id, channelId, streamType);
            Status($"{camera?.Label ?? $"Canal {channelId}"} en la ventana flotante.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(HostWindow, ex.Message, "No se pudo asignar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task DeleteFloatingAsync(FloatingWindowDto floating)
    {
        if (_currentWall is null) return;
        try
        {
            await _api.WallDeleteFloatingAsync(_currentWall.Id, floating.Id);
            Status("Ventana flotante cerrada.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(HostWindow, ex.Message, "No se pudo cerrar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // --- Modo dibujo: banda elástica sobre el muro para abrir una flotante ---

    private void OnFloatLayerMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FloatModeToggle.IsChecked != true || _currentWall is null) return;
        if (e.OriginalSource != FloatLayer) return; // los gestos de las tarjetas van aparte

        _floatDrawStart = e.GetPosition(FloatLayer);
        _floatDrawBand = new System.Windows.Shapes.Rectangle
        {
            Stroke = (Brush)FindResource("AccentBrush"),
            StrokeThickness = 1.5,
            Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x3B, 0x82, 0xF6)),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(_floatDrawBand, _floatDrawStart.Value.X);
        Canvas.SetTop(_floatDrawBand, _floatDrawStart.Value.Y);
        FloatLayer.Children.Add(_floatDrawBand);
        FloatLayer.CaptureMouse();
        e.Handled = true;
    }

    private void OnFloatLayerMouseMove(object sender, MouseEventArgs e)
    {
        if (_floatDrawStart is not Point start || _floatDrawBand is null) return;
        var pos = e.GetPosition(FloatLayer);
        Canvas.SetLeft(_floatDrawBand, Math.Min(start.X, pos.X));
        Canvas.SetTop(_floatDrawBand, Math.Min(start.Y, pos.Y));
        _floatDrawBand.Width = Math.Abs(pos.X - start.X);
        _floatDrawBand.Height = Math.Abs(pos.Y - start.Y);
    }

    private async void OnFloatLayerMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_floatDrawStart is not Point start || _floatDrawBand is null) return;
        FloatLayer.ReleaseMouseCapture();
        var end = e.GetPosition(FloatLayer);
        FloatLayer.Children.Remove(_floatDrawBand);
        _floatDrawBand = null;
        _floatDrawStart = null;

        double x0 = Math.Min(start.X, end.X), y0 = Math.Min(start.Y, end.Y);
        double w = Math.Abs(end.X - start.X), h = Math.Abs(end.Y - start.Y);
        if (w < 40 || h < 30)
        {
            Status("Arrastre un rectángulo más grande para la ventana flotante.");
            return;
        }
        await CreateFloatingFromPixelsAsync(x0, y0, w, h);
    }

    private async Task CreateFloatingFromPixelsAsync(double px, double py, double pw, double ph)
    {
        if (_currentWall is null) return;
        double unitX = FloatLayer.ActualWidth / Math.Max(1, _currentWall.Columns);
        double unitY = FloatLayer.ActualHeight / Math.Max(1, _currentWall.Rows);

        var dialog = new WallAssignDialog(_cameras, "Ventana flotante — elija la cámara", null)
        {
            Owner = HostWindow,
            AllowProjection = true,
        };
        bool accepted = dialog.ShowDialog() == true;
        FloatModeToggle.IsChecked = false; // salir del modo dibujo en cualquier caso

        // "Proyectar aquí…": la flotante se crea con la transmisión de este PC
        // (pantalla o video) en lugar de una cámara; se cierra al detenerla.
        if (accepted && dialog.ProjectHere)
        {
            await StartProjectionToNewFloatingAsync(px / unitX, py / unitY, pw / unitX, ph / unitY);
            return;
        }
        if (!accepted || dialog.SelectedCamera is not { } camera) return;

        try
        {
            Status("Abriendo la ventana flotante en el muro…");
            await _api.WallCreateFloatingAsync(_currentWall.Id,
                px / unitX, py / unitY, pw / unitX, ph / unitY,
                camera.Channel.Id, dialog.StreamType);
            Status("Ventana flotante abierta encima de los demás videos.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(HostWindow, ex.Message, "No se pudo abrir la ventana flotante",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Status("La ventana flotante no pudo abrirse.");
        }
    }

    private void Status(string message) => StatusText.Text = $"{DateTime.Now:HH:mm:ss} — {message}";
}
