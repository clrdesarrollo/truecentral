using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// «Unir cuadros»: con el modo encendido, apretar en un cuadro y arrastrar
/// hasta otro marca el rectángulo que se uniría (borde de acento, o rojo si
/// corta un cuadro por la mitad) y al soltar se une. Un clic sin arrastrar en
/// un cuadro unido lo separa. Lo comparten la Vista en Vivo y las pantallas
/// auxiliares.
///
/// El puntero se sigue por su posición en pantalla mientras el botón está
/// apretado (sondeo cada 30 ms): el gesto cruza la barra WPF de los cuadros y
/// las ventanas de video de Flyleaf, que no comparten los eventos del mouse.
/// </summary>
internal sealed class CellMergeController(
    Func<VideoLayoutPanel?> panel,
    Action<VideoCellViewModel, VideoCellViewModel?> preview,
    Func<VideoCellViewModel, VideoCellViewModel, Task> merge,
    Func<VideoCellViewModel, Task> split)
{
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    private const int VK_LBUTTON = 0x01;

    private DispatcherTimer? _timer;
    private VideoCellViewModel? _from, _to;
    private Window? _band;
    private System.Windows.Controls.Border? _bandRect;
    private System.Windows.Controls.TextBlock? _bandText;

    /// <summary>Botón apretado sobre un cuadro con el modo encendido.</summary>
    public void Start(VideoCellViewModel cell)
    {
        _timer?.Stop();
        _from = _to = cell;
        preview(cell, cell);
        ShowBand(cell, cell);
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
    }

    private async Task TickAsync()
    {
        if (_from is not { } from) return;
        if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0)
        {
            if (CellUnderCursor() is { } under && under != _to)
            {
                _to = under;
                preview(from, under);
                ShowBand(from, under);
            }
            return;
        }
        // Botón suelto: unir, o separar si fue un clic sin moverse del cuadro.
        _timer?.Stop();
        _timer = null;
        HideBand();
        var to = _to ?? from;
        _from = _to = null;
        if (to == from) await split(from);
        else await merge(from, to);
    }

    /// <summary>El arrastre se cortó (salir del modo, cerrar la ventana): sin cambios.</summary>
    public void Cancel()
    {
        _timer?.Stop();
        _timer = null;
        HideBand();
        if (_from is { } from) preview(from, null);
        _from = _to = null;
    }

    // ------------------------------------------------------------------
    // Recuadro de la selección: el rectángulo que quedaría unido, ajustado a
    // los cuadros (acento si se puede unir, rojo si corta un cuadro). Va en
    // una ventana transparente propia, encima de todo: el video de Flyleaf
    // vive en ventanas que taparían cualquier cosa dibujada en la grilla.
    // ------------------------------------------------------------------

    private void ShowBand(VideoCellViewModel from, VideoCellViewModel to)
    {
        if (panel() is not { IsVisible: true, Layout: { } layout } grid) return;
        int a = IndexOf(grid, from), b = IndexOf(grid, to);
        if (a < 0 || b < 0 || a >= layout.CellCount || b >= layout.CellCount) return;
        var (rect, inside, cut) = layout.MergeSelection(a, b);
        bool ok = cut.Count == 0;

        var source = PresentationSource.FromVisual(grid);
        if (source?.CompositionTarget is null) return;
        var origin = source.CompositionTarget.TransformFromDevice.Transform(grid.PointToScreen(new Point(0, 0)));
        double slotWidth = grid.ActualWidth / layout.Columns, slotHeight = grid.ActualHeight / layout.Rows;

        if (_band is null)
        {
            _bandText = new System.Windows.Controls.TextBlock
            {
                FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
            };
            _bandRect = new System.Windows.Controls.Border
            {
                BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(3),
                Child = new System.Windows.Controls.Border
                {
                    CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Child = _bandText,
                },
            };
            var canvas = new System.Windows.Controls.Canvas();
            canvas.Children.Add(_bandRect);
            _band = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ShowInTaskbar = false, ShowActivated = false, Topmost = true, IsHitTestVisible = false,
                ResizeMode = ResizeMode.NoResize, Content = canvas, Focusable = false,
                Owner = Window.GetWindow(grid),
            };
            // Los clics pasan a través: el gesto sigue siendo de la grilla.
            _band.SourceInitialized += (_, _) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(_band).Handle;
                SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            };
        }
        _band.Left = origin.X;
        _band.Top = origin.Y;
        _band.Width = grid.ActualWidth;
        _band.Height = grid.ActualHeight;

        var accent = (grid.TryFindResource("AccentBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x38, 0xBD, 0xF8);
        var color = ok ? accent : Color.FromRgb(0xEF, 0x44, 0x44);
        _bandRect!.BorderBrush = new SolidColorBrush(color);
        _bandRect.Background = new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B));
        _bandRect.Width = rect.ColSpan * slotWidth;
        _bandRect.Height = rect.RowSpan * slotHeight;
        System.Windows.Controls.Canvas.SetLeft(_bandRect, rect.Col * slotWidth);
        System.Windows.Controls.Canvas.SetTop(_bandRect, rect.Row * slotHeight);
        _bandText!.Text = !ok ? "Corta un cuadro: no se puede unir"
            : inside.Count < 2 ? "Arrastre hasta otro cuadro"
            : $"Unir {inside.Count} cuadros";
        if (!_band.IsVisible) _band.Show();
    }

    private void HideBand() => _band?.Hide();

    private static int IndexOf(VideoLayoutPanel grid, VideoCellViewModel cell)
    {
        for (int i = 0; i < grid.Children.Count; i++)
            if (grid.Children[i] is FrameworkElement { DataContext: var dc } && ReferenceEquals(dc, cell)) return i;
        return -1;
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>Cuadro de la grilla bajo el puntero, por la posición en pantalla.</summary>
    private VideoCellViewModel? CellUnderCursor()
    {
        if (panel() is not { IsVisible: true } grid || !GetCursorPos(out var p)) return null;
        var point = grid.PointFromScreen(new Point(p.X, p.Y));
        int count = Math.Min(grid.Children.Count, grid.Layout?.CellCount ?? 0);
        for (int i = 0; i < count; i++)
        {
            if (grid.Children[i] is not FrameworkElement { DataContext: VideoCellViewModel cell } child) continue;
            var bounds = child.TransformToAncestor(grid).TransformBounds(new Rect(child.RenderSize));
            if (bounds.Contains(point)) return cell;
        }
        return null;
    }
}
