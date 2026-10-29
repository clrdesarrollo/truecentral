using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace TrueCentralVms.Client.Services;

/// <summary>
/// Cada ventana del cliente vuelve a abrir donde se cerró la última vez:
/// monitor, posición, tamaño y maximizada. En los puestos con varias
/// pantallas los avisos (alarma, citofonía, verificación) no deben saltar de
/// monitor: el guardia los opera donde los dejó.
///
/// Va en su propio archivo (windows.json, junto a client.json) y no en
/// <see cref="ClientSettings"/>: hay varias copias de los ajustes cargadas a
/// la vez y la que guardara última pisaría las posiciones de las demás.
/// </summary>
public static class WindowPlacements
{
    private static readonly string FilePath = Path.Combine(
        Path.GetDirectoryName(ClientSettings.FileLocation)!, "windows.json");

    private static Dictionary<string, WindowBounds>? _saved;

    private static Dictionary<string, WindowBounds> Saved
    {
        get
        {
            if (_saved is not null) return _saved;
            try
            {
                if (File.Exists(FilePath))
                    _saved = JsonSerializer.Deserialize<Dictionary<string, WindowBounds>>(File.ReadAllText(FilePath));
            }
            catch { /* archivo corrupto: se parte de cero */ }
            return _saved ??= new Dictionary<string, WindowBounds>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Lugar guardado de esa ventana, si lo hay.</summary>
    public static WindowBounds? Get(string key) => Saved.TryGetValue(key, out var bounds) ? bounds : null;

    /// <summary>
    /// La ventana abre donde quedó la última vez (si ese lugar sigue a la vista
    /// en algún monitor) y lo anota al cerrarse u ocultarse. Se llama antes de
    /// mostrarla, típicamente en su constructor.
    /// </summary>
    public static void Remember(Window window, string key, WindowBounds? fallback = null)
    {
        if (ClientSettings.Load().RememberWindowPlacement && (Get(key) ?? fallback) is { } saved && Apply(window, saved))
            window.WindowState = saved.Maximized ? WindowState.Maximized : WindowState.Normal;
        // Las ventanas que se reutilizan (alarma, verificación) se ocultan en vez de cerrarse.
        window.IsVisibleChanged += (_, e) => { if (e.NewValue is false) Store(window, key); };
        window.Closing += (_, _) => Store(window, key);
    }

    /// <summary>Anota dónde está la ventana ahora (también la usan las pantallas auxiliares).</summary>
    public static void Store(Window window, string key)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
            : window.RestoreBounds;
        if (bounds.IsEmpty || !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top) ||
            bounds.Width <= 0 || bounds.Height <= 0) return;
        Saved[key] = new WindowBounds
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = window.WindowState == WindowState.Maximized,
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Saved, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* mejor esfuerzo */ }
    }

    /// <summary>
    /// Ubica la ventana (aún sin mostrar) en el lugar guardado. Si ya no cae en
    /// ningún monitor —se desconectó la segunda pantalla, cambió la
    /// disposición— no hace nada y la ventana abre donde abriría siempre.
    /// </summary>
    private static bool Apply(Window window, WindowBounds saved)
    {
        if (!double.IsFinite(saved.Left) || !double.IsFinite(saved.Top) ||
            !double.IsFinite(saved.Width) || !double.IsFinite(saved.Height) ||
            saved.Width <= 0 || saved.Height <= 0) return false;

        // Las de tamaño fijo o ajustado al contenido solo recuerdan la posición.
        bool resizable = window.SizeToContent == SizeToContent.Manual &&
                         window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;
        double width = resizable ? Math.Max(window.MinWidth, saved.Width) : window.Width;
        double height = resizable ? Math.Max(window.MinHeight, saved.Height) : window.Height;
        if (!double.IsFinite(width)) width = saved.Width;
        if (!double.IsFinite(height)) height = saved.Height;

        // La aplicación es system-DPI-aware: una sola escala para todos los monitores.
        var dpi = VisualTreeHelper.GetDpi(window);
        // Tienen que quedar a la vista el centro y la barra de título (para poder arrastrarla).
        double centerX = saved.Left + width / 2;
        if (!OnAnyMonitor(centerX, saved.Top + height / 2, dpi) || !OnAnyMonitor(centerX, saved.Top + 19, dpi))
            return false;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = saved.Left;
        window.Top = saved.Top;
        if (resizable)
        {
            window.Width = width;
            window.Height = height;
        }
        return true;
    }

    private static bool OnAnyMonitor(double x, double y, DpiScale dpi) =>
        MonitorFromPoint(new POINT { X = (int)(x * dpi.DpiScaleX), Y = (int)(y * dpi.DpiScaleY) },
            MONITOR_DEFAULTTONULL) != IntPtr.Zero;

    private const uint MONITOR_DEFAULTTONULL = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
}
