using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using TrueCentralVms.Client.Services;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Dónde abrir las ventanas de más de un monitor (otro Centro de mando, Flota
/// en vivo) y el tamaño mínimo del cliente. Una ventana nueva vuelve donde
/// quedó la última vez (con la opción de Configuración encendida); si no hay
/// lugar anotado, parte maximizada en el primer monitor que no tenga ventanas
/// de la aplicación y, si todos están ocupados, en cascada sobre la principal.
/// </summary>
public static class MonitorPlacement
{
    /// <summary>Tamaño mínimo del cliente (1024×768), acotado al área útil del monitor donde está la ventana.</summary>
    public const double MinimumWidth = 1024, MinimumHeight = 768;

    /// <summary>
    /// Fija el tamaño mínimo de la ventana: 1024×768, salvo que el monitor donde
    /// está tenga menos área útil (un 1024×768 con la barra de tareas deja 728
    /// de alto: maximizada no puede pasarse de ahí). Se recalcula al cambiar de monitor.
    /// </summary>
    public static void EnforceMinimumSize(Window window)
    {
        void Apply()
        {
            double width = MinimumWidth, height = MinimumHeight;
            var hwnd = new WindowInteropHelper(window).Handle;
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (hwnd != IntPtr.Zero && GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info))
            {
                var dpi = VisualTreeHelper.GetDpi(window);
                width = Math.Min(width, (info.rcWork.Right - info.rcWork.Left) / dpi.DpiScaleX);
                height = Math.Min(height, (info.rcWork.Bottom - info.rcWork.Top) / dpi.DpiScaleY);
            }
            if (window.MinWidth != width) window.MinWidth = width;
            if (window.MinHeight != height) window.MinHeight = height;
        }
        window.MinWidth = MinimumWidth;
        window.MinHeight = MinimumHeight;
        window.SourceInitialized += (_, _) => Apply();
        window.LocationChanged += (_, _) => Apply();
        window.DpiChanged += (_, _) => Apply();
    }

    /// <summary>Abre la ventana donde quedó (lugar anotado con <paramref name="placementKey"/>) o en un monitor libre.</summary>
    public static void ShowOnFreeMonitor(Window window, IReadOnlyList<Window> occupied, string placementKey, int cascade)
    {
        if (ClientSettings.Load().RememberWindowPlacement && WindowPlacements.Get(placementKey) is { } saved)
        {
            ShowAt(window, new Rect(saved.Left, saved.Top, saved.Width, saved.Height), saved.Maximized, occupied, cascade);
            return;
        }
        ShowOnFirstFreeMonitor(window, occupied, cascade);
    }

    private static void ShowOnFirstFreeMonitor(Window window, IReadOnlyList<Window> occupied, int cascade)
    {
        var used = new HashSet<IntPtr>();
        foreach (var other in occupied)
        {
            var hwnd = new WindowInteropHelper(other).Handle;
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
            window.Left = left + 40;
            window.Top = top + 40;
            window.Width = Math.Max(Math.Min(window.MinWidth, width), width - 80);
            window.Height = Math.Max(Math.Min(window.MinHeight, height), height - 80);
            window.Show();
            window.WindowState = WindowState.Maximized;
            return;
        }
        if (occupied.Count > 0)
        {
            var anchor = occupied[0];
            window.Left = anchor.Left + 50 * cascade;
            window.Top = anchor.Top + 50 * cascade;
        }
        window.Show();
    }

    /// <summary>
    /// Reabre la ventana donde estaba (última sesión). Si ese lugar ya no cae
    /// en ningún monitor —se desconectó uno, cambió la disposición—, parte
    /// como una ventana nueva: en el primer monitor libre.
    /// </summary>
    public static void ShowAt(Window window, Rect bounds, bool maximized, IReadOnlyList<Window> occupied, int cascade)
    {
        bool usable = bounds.Width >= 200 && bounds.Height >= 200 &&
                      !double.IsNaN(bounds.Left) && !double.IsInfinity(bounds.Left);
        if (usable && occupied.Count > 0)
        {
            var dpi = VisualTreeHelper.GetDpi(occupied[0]);
            var center = new Point((bounds.Left + bounds.Width / 2) * dpi.DpiScaleX,
                (bounds.Top + bounds.Height / 2) * dpi.DpiScaleY);
            usable = ListMonitors().Any(m => center.X >= m.Bounds.Left && center.X < m.Bounds.Right &&
                                              center.Y >= m.Bounds.Top && center.Y < m.Bounds.Bottom);
        }
        if (!usable)
        {
            ShowOnFirstFreeMonitor(window, occupied, cascade);
            return;
        }
        window.Left = bounds.Left;
        window.Top = bounds.Top;
        window.Width = bounds.Width;
        window.Height = bounds.Height;
        window.Show();
        if (maximized) window.WindowState = WindowState.Maximized;
    }

    private struct MonitorRect
    {
        public IntPtr Handle;
        public RECT Work;
        public RECT Bounds;
    }

    private static List<MonitorRect> ListMonitors()
    {
        var monitors = new List<MonitorRect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(handle, ref info))
                monitors.Add(new MonitorRect { Handle = handle, Work = info.rcWork, Bounds = info.rcMonitor });
            return true;
        }, IntPtr.Zero);
        return monitors;
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
}
