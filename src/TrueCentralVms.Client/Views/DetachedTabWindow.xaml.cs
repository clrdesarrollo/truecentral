using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Ventana de una viñeta extraída de la principal (ver MainWindow.Tabs): la
/// vista del módulo se MUDA aquí —no es una copia— con su DataContext de
/// siempre, así el Centro de mando sigue con sus cuadros, su lista y su PTZ, y
/// el video no se corta (el host de Flyleaf se reengancha a esta ventana).
/// Cerrarla cierra el módulo, como la ✕ de su viñeta; para devolverla sin
/// cerrarla, se arrastra su viñeta sobre las viñetas de la principal o se usa
/// el botón de devolver.
/// </summary>
public partial class DetachedTabWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _released;
    private bool _fullscreenOn;

    /// <summary>Clave de la viñeta ("Live", "Alarms"…).</summary>
    public string Section { get; }

    /// <summary>La vista del módulo, que vive aquí mientras la viñeta está afuera.</summary>
    public FrameworkElement View { get; }

    /// <summary>Devolver la viñeta a la principal (botón o soltar sobre sus viñetas); true = mostrarla allá.</summary>
    public event Action<DetachedTabWindow>? ReturnRequested;

    /// <summary>Se cierra la ventana sin haber devuelto la vista: el módulo se cierra (su ✕).</summary>
    public event Action<DetachedTabWindow>? CloseRequested;

    /// <summary>Dice si un punto de la pantalla (en unidades de WPF) cae sobre las viñetas de la principal.</summary>
    public Func<Point, bool>? IsOverMainTabs { get; init; }

    public DetachedTabWindow(MainViewModel vm, string section, string title, string glyph, string closeHint,
        FrameworkElement view)
    {
        _vm = vm;
        DataContext = vm;
        Section = section;
        View = view;
        InitializeComponent();
        Title = $"{title} — CLR TrueCentral VMS";
        TabTitle.Text = title;
        TabGlyph.Text = glyph;
        CloseButton.ToolTip = closeHint;
        TabCloseButton.ToolTip = closeHint;
        Host.Children.Add(view);
        // Abre donde quedó la última vez que esta viñeta estuvo afuera.
        WindowPlacements.Remember(this, $"tab-{section}");
        StateChanged += OnWindowStateChanged;
        vm.PropertyChanged += OnVmPropertyChanged;
        Closing += (_, _) =>
        {
            if (!_released) CloseRequested?.Invoke(this);
        };
        Closed += (_, _) => vm.PropertyChanged -= OnVmPropertyChanged;
    }

    /// <summary>Saca la vista de esta ventana (la principal la vuelve a poner en su lugar).</summary>
    public FrameworkElement ReleaseView()
    {
        if (_fullscreenOn) ApplyGridFullscreen(false);
        _fullscreenOn = false;
        _released = true;
        Host.Children.Remove(View);
        return View;
    }

    /// <summary>
    /// Recién extraída con el botón todavía apretado: la ventana sigue al
    /// puntero hasta que se suelte (como una pestaña de navegador). Si se
    /// suelta sobre las viñetas de la principal, vuelve a ella.
    /// </summary>
    public void FollowPointer()
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); }
        catch (InvalidOperationException) { return; } // el botón se soltó antes
        CheckDropOnMainTabs();
    }

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        // La ✕ de la viñeta cierra, no arrastra.
        if (e.OriginalSource is DependencyObject source && IsInside(source, TabCloseButton)) return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            OnMaxRestoreClick(sender, e);
            return;
        }
        FollowPointer();
    }

    private static bool IsInside(DependencyObject node, DependencyObject container)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current == container) return true;
        return false;
    }

    /// <summary>Soltada sobre las viñetas de la principal: vuelve a ella.</summary>
    private void CheckDropOnMainTabs()
    {
        if (IsOverMainTabs is null || !GetCursorPos(out var cursor)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        if (IsOverMainTabs(new Point(cursor.X / dpi.DpiScaleX, cursor.Y / dpi.DpiScaleY)))
            ReturnRequested?.Invoke(this);
    }

    private void OnReturnClick(object sender, RoutedEventArgs e) => ReturnRequested?.Invoke(this);

    // ------------------------------------------------------------------
    // Pantalla completa de la grilla del módulo que está aquí: esta ventana
    // cubre su monitor (como la principal cuando el módulo está en ella).
    // ------------------------------------------------------------------
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsGridFullscreen) || _released) return;
        bool want = _vm.IsGridFullscreen && _vm.FullscreenSection == Section;
        if (want == _fullscreenOn) return;
        _fullscreenOn = want;
        ApplyGridFullscreen(want);
    }

    private Rect _fullscreenRestoreBounds;
    private WindowState _fullscreenRestoreState;

    private void ApplyGridFullscreen(bool on)
    {
        TitleBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on)
        {
            _fullscreenRestoreState = WindowState;
            _fullscreenRestoreBounds = new Rect(Left, Top, Width, Height);
            var monitor = MonitorFromWindow(new WindowInteropHelper(this).Handle, MONITOR_DEFAULTTONEAREST);
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

    // ------------------------------------------------------------------
    // Maximizado que respeta la barra de tareas (ver MainWindow).
    // ------------------------------------------------------------------
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WindowProc);
    }

    private const int WM_GETMINMAXINFO = 0x0024;

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
        mmi.ptMaxTrackSize.X = Math.Max(mmi.ptMaxTrackSize.X, info.rcMonitor.Right - info.rcMonitor.Left);
        mmi.ptMaxTrackSize.Y = Math.Max(mmi.ptMaxTrackSize.Y, info.rcMonitor.Bottom - info.rcMonitor.Top);
        Marshal.StructureToPtr(mmi, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        RootShell.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaxGlyph.Text = maximized ? "" : ""; // ChromeRestore / ChromeMaximize
        MaxButton.ToolTip = maximized ? "Restaurar" : "Maximizar";
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

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

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}
