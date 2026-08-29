using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += async (_, _) => await _vm.LoadTreeAsync();
        Closed += (_, _) => _vm.Shutdown();
        StateChanged += OnWindowStateChanged;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsGridFullscreen))
                ApplyGridFullscreen(_vm.IsGridFullscreen);
        };
    }

    // ------------------------------------------------------------------
    // Pantalla completa de la grilla: el XAML ya ocultó todo el cromo
    // (bindings a IsGridFullscreen); aquí la ventana pasa a cubrir el
    // monitor COMPLETO (una ventana sin marco maximizada respeta la barra
    // de tareas, así que se fijan los límites del monitor a mano).
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

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // ------------------------------------------------------------------
    // Controles de la barra de título propia (la ventana no tiene el
    // marco estándar de Windows; ver WindowChrome en el XAML).
    // ------------------------------------------------------------------

    /// <summary>Al maximizar una ventana sin marco, Windows la extiende más
    /// allá de los bordes de la pantalla por el grosor del marco invisible:
    /// se compensa con un margen para que nada quede cortado.</summary>
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
}
