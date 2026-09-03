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
        Closing += OnShellClosing;
        Closed += (_, _) => _vm.Shutdown();
        StateChanged += OnWindowStateChanged;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsGridFullscreen))
                ApplyGridFullscreen(_vm.IsGridFullscreen);
        };
    }

    // ------------------------------------------------------------------
    // Confirmación de salida. Con descargas de grabaciones activas el aviso
    // cambia: salir corta la cola y los MP4 a medias se pierden.
    // ------------------------------------------------------------------
    private void OnShellClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        bool downloading = _vm.Downloads.HasActiveJobs;
        string message;
        if (downloading)
        {
            int active = _vm.Downloads.Jobs.Count(j => j.State == DownloadState.Downloading);
            int queued = _vm.Downloads.Jobs.Count(j => j.State == DownloadState.Pending);
            message = $"Hay {active} descarga(s) de grabaciones en curso" +
                      (queued > 0 ? $" y {queued} en cola" : "") +
                      ".\nSi sale ahora se cancelarán y los archivos a medias quedarán incompletos.\n\n" +
                      "¿Salir de todos modos?";
        }
        else
        {
            message = "¿Cerrar CLR TrueCentral VMS?";
        }

        var result = MessageBox.Show(this, message, "Salir de CLR TrueCentral VMS",
            MessageBoxButton.YesNo,
            downloading ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        // Mejor esfuerzo: cancelar la cola da la oportunidad de borrar los
        // parciales antes de que el proceso muera.
        if (downloading) _vm.Downloads.CancelAll();
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

    // ------------------------------------------------------------------
    // Maximizado que respeta la barra de tareas: una ventana sin marco
    // (WindowStyle=None) maximizada ocupa por defecto la PANTALLA completa,
    // tapando la barra. WM_GETMINMAXINFO limita el tamaño y la posición del
    // maximizado al área de trabajo del monitor donde esté la ventana.
    // (La pantalla completa de la grilla no pasa por aquí: usa estado Normal
    // con límites de monitor puestos a mano, y ahí tapar la barra es la idea.)
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
        // Coordenadas relativas al monitor, en píxeles de dispositivo.
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
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
