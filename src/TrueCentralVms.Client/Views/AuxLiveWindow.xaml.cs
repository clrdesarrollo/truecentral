using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Ventana de una pantalla auxiliar de Vista en Vivo (hasta 3, estilo
/// iVMS-4200): shell sin marco como la principal, árbol compartido y grilla
/// propia. Las interacciones de la grilla (selección, arrastres, ganchos a las
/// ventanas de video de Flyleaf) replican las de LiveView adaptadas a su
/// ViewModel; al abrirse busca un monitor sin ventanas de la aplicación.
/// </summary>
public partial class AuxLiveWindow : Window
{
    public AuxScreenViewModel Vm { get; }

    public AuxLiveWindow(AuxScreenViewModel vm)
    {
        Vm = vm;
        DataContext = vm;
        InitializeComponent();
        vm.OwnerWindow = this;
        // Las capturas y cápsulas hechas aquí avisan en ESTE monitor.
        vm.MediaSaved += (title, glyph, path) =>
        {
            if (IsLoaded) ToastWindow.ShowSaved(this, title, glyph, path);
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AuxScreenViewModel.IsGridFullscreen))
                ApplyGridFullscreen(Vm.IsGridFullscreen);
        };
        StateChanged += OnWindowStateChanged;
        PreviewKeyDown += OnWindowKeyDown;
        // Cerrar la pantalla detiene y libera sus streams (la principal sigue).
        Closed += (_, _) => Vm.Dispose();
    }

    // ------------------------------------------------------------------
    // Apertura: la pantalla parte maximizada en el primer monitor que no
    // tenga ventanas de la aplicación (para eso existe: más monitores). Si
    // todos están ocupados, cae en cascada sobre la ventana ancla.
    // ------------------------------------------------------------------
    public void ShowOnFreeMonitor(IReadOnlyList<Window> occupied)
    {
        var used = new HashSet<IntPtr>();
        foreach (var window in occupied)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero) used.Add(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));
        }
        var free = ListMonitors().FirstOrDefault(m => !used.Contains(m.Handle));
        if (free.Handle != IntPtr.Zero && occupied.Count > 0)
        {
            // La aplicación es system-DPI-aware: una sola escala para todos
            // los monitores desde el punto de vista de WPF.
            var dpi = VisualTreeHelper.GetDpi(occupied[0]);
            double left = free.Work.Left / dpi.DpiScaleX;
            double top = free.Work.Top / dpi.DpiScaleY;
            double width = (free.Work.Right - free.Work.Left) / dpi.DpiScaleX;
            double height = (free.Work.Bottom - free.Work.Top) / dpi.DpiScaleY;
            // Límites de restauración con margen, dentro del monitor elegido;
            // el maximizado (acotado por WM_GETMINMAXINFO) hace el resto.
            Left = left + 40;
            Top = top + 40;
            Width = Math.Max(MinWidth, width - 80);
            Height = Math.Max(MinHeight, height - 80);
            Show();
            WindowState = WindowState.Maximized;
            return;
        }
        if (occupied.Count > 0)
        {
            var anchor = occupied[0];
            Left = anchor.Left + 50 * Vm.SlotNumber;
            Top = anchor.Top + 50 * Vm.SlotNumber;
        }
        Show();
    }

    private struct MonitorRect
    {
        public IntPtr Handle;
        public RECT Work;
    }

    private static List<MonitorRect> ListMonitors()
    {
        var monitors = new List<MonitorRect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(handle, ref info))
                monitors.Add(new MonitorRect { Handle = handle, Work = info.rcWork });
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    // ------------------------------------------------------------------
    // Pantalla completa de la grilla EN ESTE MONITOR (mismo mecanismo que la
    // ventana principal: estado Normal con los límites del monitor a mano,
    // porque una ventana sin marco maximizada respeta la barra de tareas).
    // ------------------------------------------------------------------
    private Rect _fullscreenRestoreBounds;
    private WindowState _fullscreenRestoreState;

    private void ApplyGridFullscreen(bool on)
    {
        if (on)
        {
            _fullscreenRestoreState = WindowState;
            _fullscreenRestoreBounds = new Rect(Left, Top, Width, Height);

            var hwnd = new WindowInteropHelper(this).Handle;
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            var dpi = VisualTreeHelper.GetDpi(this);
            WindowState = WindowState.Normal;
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                Left = info.rcMonitor.Left / dpi.DpiScaleX;
                Top = info.rcMonitor.Top / dpi.DpiScaleY;
                Width = (info.rcMonitor.Right - info.rcMonitor.Left) / dpi.DpiScaleX;
                Height = (info.rcMonitor.Bottom - info.rcMonitor.Top) / dpi.DpiScaleY;
            }
            else
            {
                WindowState = WindowState.Maximized; // respaldo: al menos maximizada
            }
        }
        else
        {
            Left = _fullscreenRestoreBounds.Left;
            Top = _fullscreenRestoreBounds.Top;
            Width = _fullscreenRestoreBounds.Width;
            Height = _fullscreenRestoreBounds.Height;
            WindowState = _fullscreenRestoreState;
        }
    }

    /// <summary>Esc sale de la pantalla completa (también desde las ventanas
    /// de video de Flyleaf, que se enganchan aparte).</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !Vm.IsGridFullscreen) return;
        Vm.IsGridFullscreen = false;
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    // Maximizado que respeta la barra de tareas (ver MainWindow: una ventana
    // sin marco maximizada taparía la barra sin este límite).
    // ------------------------------------------------------------------
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WindowProc);
    }

    private const int WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
        // Ídem MainWindow: el tope por defecto es el monitor principal.
        mmi.ptMaxTrackSize.X = Math.Max(mmi.ptMaxTrackSize.X, info.rcMonitor.Right - info.rcMonitor.Left);
        mmi.ptMaxTrackSize.Y = Math.Max(mmi.ptMaxTrackSize.Y, info.rcMonitor.Bottom - info.rcMonitor.Top);
        Marshal.StructureToPtr(mmi, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    // ------------------------------------------------------------------
    // Controles de la barra de título propia (WindowChrome).
    // ------------------------------------------------------------------
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        RootShell.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaxGlyph.Text = maximized ? "\uE923" : "\uE922"; // ChromeRestore / ChromeMaximize
        MaxButton.ToolTip = maximized ? "Restaurar" : "Maximizar";
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------
    // Árbol: doble clic abre en ESTA grilla; arrastrar un canal lo lleva a
    // cualquier cuadro (de esta ventana o de otra: el drag & drop de WPF
    // cruza ventanas de la misma aplicación).
    // ------------------------------------------------------------------
    private async void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeviceTree.SelectedItem is ChannelNode node)
            await Vm.OpenChannelAsync(node);
        else if (DeviceTree.SelectedItem is DeviceNode device)
            await Vm.OpenDeviceAsync(device);
    }

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
        DragDrop.DoDragDrop(DeviceTree, new DataObject(typeof(ChannelNode), node), DragDropEffects.Copy);
    }

    /// <summary>Elegir una división cierra el popup (el comando ya la aplicó).</summary>
    private void OnLayoutPicked(object sender, RoutedEventArgs e) => LayoutPickerToggle.IsChecked = false;

    // ------------------------------------------------------------------
    // Cuadros: clic selecciona, doble clic maximiza dentro de la grilla,
    // arrastrar mueve/intercambia la cámara (también entre ventanas).
    // ------------------------------------------------------------------
    private async void OnCellDrop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VideoCellViewModel cell })
            await HandleCellDropAsync(cell, e);
    }

    private void OnCellDragOver(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VideoCellViewModel cell })
            HandleCellDragOver(cell, e);
    }

    private void OnCellDragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VideoCellViewModel cell })
            HandleCellDragLeave(cell);
    }

    private void OnCellClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: VideoCellViewModel cell } element) return;
        if (e.ClickCount == 2)
        {
            Vm.ToggleMaximize(cell);
            return;
        }
        ArmCellDrag(cell, element, e.GetPosition(element));
        SelectCell(cell);
    }

    private void OnCellMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is UIElement element) TryStartCellDrag(element, e);
    }

    private VideoCellViewModel? _cellDragCandidate;
    private UIElement? _cellDragOrigin;
    private Point _cellDragStart;

    private void ArmCellDrag(VideoCellViewModel cell, UIElement origin, Point start)
    {
        _cellDragCandidate = cell.IsEmpty ? null : cell;
        _cellDragOrigin = origin;
        _cellDragStart = start;
    }

    private void TryStartCellDrag(UIElement source, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _cellDragCandidate = null; return; }
        if (_cellDragCandidate is not { } cell) return;
        if (ReferenceEquals(source, _cellDragOrigin))
        {
            var position = e.GetPosition(source);
            if (Math.Abs(position.X - _cellDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(position.Y - _cellDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        }
        _cellDragCandidate = null;
        try { DragDrop.DoDragDrop(source, new DataObject(typeof(VideoCellViewModel), cell), DragDropEffects.Move); }
        finally { Vm.DropTargetCell = null; }
    }

    private static DragDropEffects CellDropEffect(DragEventArgs e, VideoCellViewModel cell) =>
        e.Data.GetData(typeof(VideoCellViewModel)) is VideoCellViewModel dragged
            ? ReferenceEquals(dragged, cell) ? DragDropEffects.None : DragDropEffects.Move
            : e.Data.GetDataPresent(typeof(ChannelNode)) ? DragDropEffects.Copy : DragDropEffects.None;

    private void HandleCellDragOver(VideoCellViewModel cell, DragEventArgs e)
    {
        e.Effects = CellDropEffect(e, cell);
        Vm.DropTargetCell = e.Effects == DragDropEffects.None ? null : cell;
        e.Handled = true;
    }

    private void HandleCellDragLeave(VideoCellViewModel cell)
    {
        if (Vm.DropTargetCell == cell) Vm.DropTargetCell = null;
    }

    private async Task HandleCellDropAsync(VideoCellViewModel cell, DragEventArgs e)
    {
        Vm.DropTargetCell = null;
        if (e.Data.GetData(typeof(VideoCellViewModel)) is VideoCellViewModel dragged)
        {
            e.Handled = true;
            Vm.SwapCells(dragged, cell);
            return;
        }
        if (e.Data.GetData(typeof(ChannelNode)) is ChannelNode node)
        {
            e.Handled = true;
            await Vm.OpenChannelInCellAsync(node, cell);
        }
    }

    // ------------------------------------------------------------------
    // Ganchos a las ventanas de video de Flyleaf (Surface/Overlay): los
    // clics sobre el video no llegan al árbol WPF de esta ventana — misma
    // técnica que LiveView (handledEventsToo y re-enganche al estrenar
    // player), sin PTZ (el panel vive en la ventana principal).
    // ------------------------------------------------------------------
    private readonly HashSet<FlyleafLib.Controls.WPF.FlyleafHost> _hookedHosts = [];
    private readonly HashSet<Window> _hookedWindows = [];

    /// <summary>Modo zoom digital (lupa + recuadro sobre el video).</summary>
    private readonly ZoomDragController _zoom = new();

    private void OnFlyleafHostLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FlyleafLib.Controls.WPF.FlyleafHost host || !_hookedHosts.Add(host))
            return;
        if (host.DataContext is VideoCellViewModel cell)
            cell.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(VideoCellViewModel.Player)) HookVideoWindows(host);
            };
        HookVideoWindows(host);
    }

    private void HookVideoWindows(FlyleafLib.Controls.WPF.FlyleafHost host) =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            HookVideoWindow(host, host.Surface);
            HookVideoWindow(host, host.Overlay);
        }));

    /// <summary>El botón de la barra enciende el modo zoom en todas las ventanas de video.</summary>
    private void OnDigitalZoomToggled(object sender, RoutedEventArgs e) =>
        _zoom.SetEnabled(Vm.IsDigitalZoomMode);

    private void HookVideoWindow(FlyleafLib.Controls.WPF.FlyleafHost host, Window? window)
    {
        if (window is null || !_hookedWindows.Add(window)) return;
        _zoom.Attach(window, () => host.DataContext as VideoCellViewModel);
        window.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (_zoom.IsEnabled) return;
            if (host.DataContext is not VideoCellViewModel cell) return;
            if (e.ClickCount == 2)
            {
                Vm.ToggleMaximize(cell);
                return;
            }
            ArmCellDrag(cell, window, e.GetPosition(window));
            SelectCell(cell);
        }), handledEventsToo: true);
        window.AddHandler(MouseMoveEvent, new MouseEventHandler((_, e) =>
        {
            if (_zoom.IsEnabled) return;
            TryStartCellDrag(window, e);
        }), handledEventsToo: true);
        window.AllowDrop = true;
        window.AddHandler(DragDrop.DragOverEvent, new DragEventHandler((_, e) =>
        {
            if (host.DataContext is VideoCellViewModel cell) HandleCellDragOver(cell, e);
        }), handledEventsToo: true);
        window.AddHandler(DragDrop.DragLeaveEvent, new DragEventHandler((_, e) =>
        {
            if (host.DataContext is VideoCellViewModel cell) HandleCellDragLeave(cell);
        }), handledEventsToo: true);
        window.AddHandler(DragDrop.DropEvent, new DragEventHandler(async (_, e) =>
        {
            if (host.DataContext is VideoCellViewModel cell) await HandleCellDropAsync(cell, e);
        }), handledEventsToo: true);
        // Esc en pantalla completa también debe funcionar con el foco en el video.
        window.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnWindowKeyDown), handledEventsToo: true);
        // Rueda del mouse sobre el video = zoom digital centrado en el cursor.
        window.AddHandler(MouseWheelEvent, new MouseWheelEventHandler((_, e) =>
        {
            if (_zoom.IsEnabled) return;
            if (host.DataContext is not VideoCellViewModel cell || cell.IsEmpty) return;
            var position = e.GetPosition(window);
            var center = new Point(
                window.ActualWidth > 0 ? position.X / window.ActualWidth : 0.5,
                window.ActualHeight > 0 ? position.Y / window.ActualHeight : 0.5);
            cell.DigitalZoomStep(e.Delta > 0, center);
            e.Handled = true;
        }), handledEventsToo: true);
    }

    private void SelectCell(VideoCellViewModel cell) => Vm.SelectedCell = Vm.SelectedCell == cell ? null : cell;
}
