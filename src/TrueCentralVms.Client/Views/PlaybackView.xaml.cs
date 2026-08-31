using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Módulo Reproducción: árbol de canales, grilla de cuadros sincronizados,
/// línea de tiempo del día y barra de control. El DataContext es el
/// MainViewModel de la ventana (heredado); el estado del módulo vive en su
/// propiedad Playback.
/// </summary>
public partial class PlaybackView : UserControl
{
    /// <summary>Salto de los dobles clics laterales sobre el video (segundos).</summary>
    private const int SideSkipSeconds = 30;

    private Window? _hostWindow;

    public PlaybackView()
    {
        InitializeComponent();
        Timeline.SeekRequested += async time => await Vm.Playback.SeekToAsync(time);
        Timeline.SelectionChanged += (from, to) => Vm.Playback.SetSelection(from, to);
        // Mientras se arrastra la aguja, el reloj de la barra muestra a dónde
        // va a saltar (y vuelve a la hora real al soltar).
        Timeline.Scrubbing += time => Vm.Playback.ScrubTime = time;

        // Esc sale de la pantalla completa: se escucha a nivel de ventana
        // porque el foco puede estar en el árbol o en la barra.
        Loaded += (_, _) =>
        {
            if (_hostWindow is not null || Window.GetWindow(this) is not { } window) return;
            _hostWindow = window;
            window.PreviewKeyDown += OnKeyDown;
        };
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    /// <summary>Doble clic: suma el canal (hasta 4); con Ctrl deja solo ese.</summary>
    private async void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaybackTree.SelectedItem is ChannelNode node)
            await Vm.Playback.SelectChannelAsync(node,
                replaceAll: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    /// <summary>Clic en la barra de un cuadro: pasa a ser el cuadro con foco.</summary>
    private void OnCellClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlaybackCellViewModel cell })
            Vm.Playback.SelectedCell = cell;
    }

    /// <summary>Clic sobre el área de video en WPF (cuadro sin imagen todavía).</summary>
    private void OnVideoAreaClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaybackCellViewModel cell } area) return;
        HandleVideoClick(cell, e.GetPosition(area).X, area.ActualWidth, e.ClickCount);
    }

    /// <summary>
    /// Dobles clics sobre el video, estilo reproductor: en el tercio
    /// izquierdo retrocede 30 s, en el derecho avanza 30 s y al centro entra
    /// o sale de pantalla completa. Un clic simple solo da el foco al cuadro.
    /// </summary>
    private void HandleVideoClick(PlaybackCellViewModel cell, double x, double width, int clickCount)
    {
        Vm.Playback.SelectedCell = cell;
        if (clickCount != 2 || width <= 0) return;

        double fraction = x / width;
        if (fraction < 1 / 3d)
            Vm.Playback.SkipCommand.Execute((-SideSkipSeconds).ToString());
        else if (fraction > 2 / 3d)
            Vm.Playback.SkipCommand.Execute(SideSkipSeconds.ToString());
        else
            ToggleFullscreen();
    }

    private void ToggleFullscreen() => Vm.IsGridFullscreen = !Vm.IsGridFullscreen;

    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void OnTimelineZoomIn(object sender, RoutedEventArgs e) => Timeline.ZoomIn();

    private void OnTimelineZoomOut(object sender, RoutedEventArgs e) => Timeline.ZoomOut();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible) return; // el módulo está oculto (otra página)
        if (e.Key == Key.Escape && Vm.IsGridFullscreen)
        {
            Vm.IsGridFullscreen = false;
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------
    // FlyleafLib pinta el video en ventanas WPF propias ("Surface" y
    // "Overlay") superpuestas a nuestro árbol visual: un clic ahí jamás llega
    // a los manejadores de la celda. Como sí son ventanas WPF, al cargarse
    // cada host nos suscribimos a sus eventos con handledEventsToo (Flyleaf
    // usa esos mismos eventos y puede marcarlos como manejados).
    // ------------------------------------------------------------------
    private readonly HashSet<FlyleafLib.Controls.WPF.FlyleafHost> _hookedHosts = [];

    /// <summary>Modo zoom digital (lupa + recuadro sobre el video).</summary>
    private readonly ZoomDragController _zoom = new();

    private void OnFlyleafHostLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FlyleafLib.Controls.WPF.FlyleafHost host || !_hookedHosts.Add(host))
            return;
        // Diferido a prioridad Loaded: garantiza que Flyleaf ya creó sus
        // ventanas en su propio manejador de Loaded, sea cual sea el orden.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            HookVideoWindow(host, host.Surface);
            HookVideoWindow(host, host.Overlay);
        }));
    }

    // ------------------------------------------------------------------
    // Arrastrar un canal del árbol y soltarlo sobre un cuadro. El umbral de
    // arrastre del sistema evita interferir con el clic y el doble clic, que
    // siguen sirviendo para sumar canales.
    // ------------------------------------------------------------------
    private ChannelNode? _dragCandidate;
    private Point _dragStart;

    private void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = (e.OriginalSource as FrameworkElement)?.DataContext as ChannelNode;
        _dragStart = e.GetPosition(this);
    }

    private void OnTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _dragCandidate = null; return; }
        if (_dragCandidate is not { } node) return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragCandidate = null;
        DragDrop.DoDragDrop(PlaybackTree, new DataObject(typeof(ChannelNode), node), DragDropEffects.Copy);
    }

    /// <summary>El botón de la barra enciende el modo zoom en todas las ventanas de video.</summary>
    private void OnDigitalZoomToggled(object sender, RoutedEventArgs e) =>
        _zoom.SetEnabled(Vm.Playback.IsDigitalZoomMode);

    private void HookVideoWindow(FlyleafLib.Controls.WPF.FlyleafHost host, Window? window)
    {
        if (window is null) return;
        // Con el modo zoom activo el arrastre marca el área: los saltos de
        // ±30 s por doble clic lateral quedan en pausa mientras tanto.
        _zoom.Attach(window, () => host.DataContext as PlaybackCellViewModel);
        window.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (_zoom.IsEnabled) return;
            if (host.DataContext is not PlaybackCellViewModel cell) return;
            HandleVideoClick(cell, e.GetPosition(window).X, window.ActualWidth, e.ClickCount);
        }), handledEventsToo: true);
        // Un clic en el video deja el foco del teclado en la ventana de
        // Flyleaf: Esc también debe salir de la pantalla completa desde ahí.
        window.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);
        // El video tapa el cuadro WPF: el canal arrastrado se suelta
        // directamente sobre la ventana de Flyleaf, que reemplaza ESE cuadro.
        window.AllowDrop = true;
        window.AddHandler(DragDrop.DropEvent, new DragEventHandler(async (_, e) =>
        {
            if (host.DataContext is PlaybackCellViewModel cell &&
                e.Data.GetData(typeof(ChannelNode)) is ChannelNode node)
                await Vm.Playback.SelectChannelAsync(node, target: cell);
        }), handledEventsToo: true);
    }
}
