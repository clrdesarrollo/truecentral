using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
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
        // El menú del usuario cuelga alineado a la DERECHA de su botón: es lo
        // último del navbar y el menú es más ancho que el botón.
        UserMenuPopup.CustomPopupPlacementCallback = PlaceUserMenu;
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
        // Cerrar sesión ya preguntó lo suyo y la aplicación no se va: sigue el login.
        if (_loggingOut) return;

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
    // Menú del usuario (navbar) y cierre de sesión.
    // ------------------------------------------------------------------

    /// <summary>Cierre de sesión en curso: la ventana se cierra sin el
    /// "¿Cerrar CLR TrueCentral VMS?" porque la aplicación sigue viva en el
    /// login.</summary>
    private bool _loggingOut;

    /// <summary>Aire que el Border del menú deja para su sombra (su Margin en
    /// el XAML): entra en el tamaño del popup y hay que descontarlo para que el
    /// borde visible quede a ras del botón.</summary>
    private const double UserMenuShadowMargin = 8;

    /// <summary>Cuelga el menú del botón del usuario alineado por su borde
    /// derecho (Placement="Bottom" lo alinearía por la izquierda y se saldría
    /// de la ventana).</summary>
    private static CustomPopupPlacement[] PlaceUserMenu(Size popupSize, Size targetSize, Point offset) =>
        [new CustomPopupPlacement(
            new Point(targetSize.Width - popupSize.Width + UserMenuShadowMargin, targetSize.Height - 2),
            PopupPrimaryAxis.Horizontal)];

    /// <summary>
    /// Cerrar sesión: avisa al servidor (revoca el token y deja el registro en
    /// la bitácora), suelta streams, hub y ventanas auxiliares, y vuelve al
    /// login sin reiniciar la aplicación.
    /// </summary>
    private async void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        UserMenuToggle.IsChecked = false;

        if (_vm.Downloads.HasActiveJobs)
        {
            int active = _vm.Downloads.Jobs.Count(j => j.State == DownloadState.Downloading);
            int queued = _vm.Downloads.Jobs.Count(j => j.State == DownloadState.Pending);
            var answer = MessageBox.Show(this,
                $"Hay {active} descarga(s) de grabaciones en curso" +
                (queued > 0 ? $" y {queued} en cola" : "") +
                ".\nCerrar sesión las cancelará y los archivos a medias quedarán incompletos.\n\n" +
                "¿Cerrar sesión de todos modos?",
                "Cerrar sesión", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            _vm.Downloads.CancelAll();
        }

        _loggingOut = true;
        // El login se abre ANTES de cerrar esta ventana: con la última ventana
        // cerrada la aplicación terminaría (ShutdownMode por defecto).
        var login = new LoginWindow(skipAutoLogin: true);
        Application.Current.MainWindow = login;
        login.Show();
        Close(); // Closed → _vm.Shutdown(): cuadros, hub, timers y pantallas auxiliares
        await _vm.Api.LogoutAsync();
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
