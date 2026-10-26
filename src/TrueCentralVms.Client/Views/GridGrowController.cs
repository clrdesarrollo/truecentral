using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Ampliar la grilla arrastrando: una cámara del árbol llevada al borde de la
/// grilla (arriba, abajo, izquierda o derecha) muestra la franja nueva que se
/// agregaría ahí, la grilla se aleja un poco para dejarla ver y, al soltar,
/// la franja se agrega con la cámara en el cuadro marcado. Lo comparten la
/// Vista en Vivo y las pantallas auxiliares.
///
/// El borde se detecta con la posición en pantalla: el puntero puede estar
/// sobre la celda WPF o sobre la ventana de video de Flyleaf, que son árboles
/// visuales distintos. Una vez abierta, la franja es WPF (no tiene video
/// encima) y recibe el arrastre ella misma.
/// </summary>
internal sealed class GridGrowController(
    FrameworkElement host,
    Canvas overlay,
    Func<GridEdge, string?> blockedReason,
    Action<string> showStatus)
{
    private static readonly Duration AnimationTime = TimeSpan.FromMilliseconds(170);
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    private VideoLayoutPanel? _panel;
    private GridEdge _edge = GridEdge.None;
    private int _slot = -1;
    /// <summary>Borde cuyo aviso de tope ya se mostró (no repetirlo en cada DragOver).</summary>
    private GridEdge _blockedShown = GridEdge.None;

    /// <summary>El panel de la grilla nace de la plantilla del ItemsControl.</summary>
    public void Attach(VideoLayoutPanel panel)
    {
        if (ReferenceEquals(_panel, panel)) return;
        if (_panel is not null) _panel.StripArranged -= OnStripArranged;
        _panel = panel;
        panel.StripArranged += OnStripArranged;
    }

    private static Point ScreenPoint(DragEventArgs e, Visual relativeTo) =>
        relativeTo.PointToScreen(e.GetPosition((IInputElement)relativeTo));

    /// <summary>Solo una cámara del árbol amplía la grilla (no un cuadro movido).</summary>
    private static bool IsChannelDrag(DragEventArgs e) =>
        e.Data.GetDataPresent(typeof(ChannelNode)) && !e.Data.GetDataPresent(typeof(VideoCellViewModel));

    /// <summary>
    /// Arrastre sobre la grilla (una celda, el video o la franja): true si el
    /// puntero está en un borde y la franja quedó a la vista; en ese caso el
    /// arrastre es de la franja y no de la celda de abajo.
    /// </summary>
    public bool Track(DragEventArgs e, Visual source)
    {
        if (_panel is not { IsVisible: true } panel || !IsChannelDrag(e)) return false;
        var point = panel.PointFromScreen(ScreenPoint(e, source));
        var edge = Detect(panel, point);
        if (edge != GridEdge.None && blockedReason(edge) is { } reason)
        {
            if (_blockedShown != edge) showStatus(reason);
            _blockedShown = edge;
            edge = GridEdge.None;
        }
        else if (edge == GridEdge.None)
            _blockedShown = GridEdge.None;

        if (edge == GridEdge.None)
        {
            Cancel();
            return false;
        }
        if (edge != _edge) Show(panel, edge);
        int slot = SlotAt(panel, point);
        if (slot != _slot)
        {
            _slot = slot;
            Redraw();
        }
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        return true;
    }

    /// <summary>
    /// Borde bajo el puntero. Con la franja abierta, todo su ancho (y el margen
    /// del alejamiento) la mantiene: es más ancha que la banda que la abre, así
    /// que la franja no parpadea al aparecer debajo del puntero.
    /// </summary>
    private GridEdge Detect(VideoLayoutPanel panel, Point p)
    {
        double width = panel.ActualWidth, height = panel.ActualHeight;
        if (width <= 0 || height <= 0 || panel.MaximizedIndex >= 0) return GridEdge.None;
        if (p.X < 0 || p.Y < 0 || p.X > width || p.Y > height) return GridEdge.None;
        var strip = panel.StripRect;
        if (_edge != GridEdge.None && !strip.IsEmpty && _edge switch
            {
                GridEdge.Left => p.X <= strip.Right,
                GridEdge.Right => p.X >= strip.Left,
                GridEdge.Top => p.Y <= strip.Bottom,
                _ => p.Y >= strip.Top,
            })
            return _edge;

        double bandX = Math.Clamp(width * 0.06, 14, 48), bandY = Math.Clamp(height * 0.06, 14, 48);
        var best = GridEdge.None;
        double bestDepth = 1;
        void Consider(GridEdge edge, double distance, double band)
        {
            if (distance < band && distance / band < bestDepth)
            {
                best = edge;
                bestDepth = distance / band;
            }
        }
        Consider(GridEdge.Left, p.X, bandX);
        Consider(GridEdge.Right, width - p.X, bandX);
        Consider(GridEdge.Top, p.Y, bandY);
        Consider(GridEdge.Bottom, height - p.Y, bandY);
        return best;
    }

    /// <summary>Cuadro de la franja bajo el puntero (0 = el primero).</summary>
    private int SlotAt(VideoLayoutPanel panel, Point p)
    {
        if (panel.Layout is not { } layout) return 0;
        var strip = panel.StripRect;
        if (strip.IsEmpty) return Math.Max(_slot, 0);
        bool sideways = _edge is GridEdge.Left or GridEdge.Right;
        int count = sideways ? layout.Rows : layout.Columns;
        double along = sideways ? (p.Y - strip.Top) / strip.Height : (p.X - strip.Left) / strip.Width;
        return Math.Clamp((int)Math.Floor(along * count), 0, count - 1);
    }

    private void Show(VideoLayoutPanel panel, GridEdge edge)
    {
        _edge = edge;
        _slot = -1;
        panel.BeginAnimation(VideoLayoutPanel.GrowProgressProperty, null);
        panel.GrowProgress = 0;
        panel.GrowEdge = edge;
        Animate(panel, VideoLayoutPanel.GrowProgressProperty, 1);
        // Alejar: un margen chico y proporcional a la grilla.
        Animate(panel, VideoLayoutPanel.InsetProperty,
            Math.Clamp(Math.Min(panel.ActualWidth, panel.ActualHeight) * 0.025, 8, 22));
    }

    /// <summary>El arrastre salió del borde (o terminó sin soltar ahí): la franja se recoge.</summary>
    public void Cancel()
    {
        if (_panel is not { } panel || _edge == GridEdge.None) return;
        _edge = GridEdge.None;
        _slot = -1;
        Animate(panel, VideoLayoutPanel.GrowProgressProperty, 0, () =>
        {
            if (_edge == GridEdge.None) panel.GrowEdge = GridEdge.None;
        });
        Animate(panel, VideoLayoutPanel.InsetProperty, 0);
    }

    /// <summary>Salida del arrastre de un elemento de la grilla: si el puntero
    /// quedó fuera de toda la grilla, la franja se recoge.</summary>
    public void Leave(DragEventArgs e, Visual source)
    {
        if (_edge == GridEdge.None) return;
        var point = host.PointFromScreen(ScreenPoint(e, source));
        if (point.X < 0 || point.Y < 0 || point.X > host.ActualWidth || point.Y > host.ActualHeight)
            Cancel();
    }

    /// <summary>
    /// Soltar con la franja abierta: se amplía la grilla con la cámara en el
    /// cuadro marcado. false = no había franja (el soltar es de la celda).
    /// </summary>
    public async Task<bool> TryDropAsync(DragEventArgs e, Func<GridEdge, int, ChannelNode, Task> grow)
    {
        if (_edge == GridEdge.None || _panel is not { } panel ||
            e.Data.GetData(typeof(ChannelNode)) is not ChannelNode node) return false;
        e.Handled = true;
        // La franja entera: los cuadros nuevos se crean ahí con su tamaño real.
        panel.BeginAnimation(VideoLayoutPanel.GrowProgressProperty, null);
        panel.GrowProgress = 1;
        var (edge, slot) = (_edge, Math.Max(_slot, 0));
        _edge = GridEdge.None;
        _slot = -1;
        try { await grow(edge, slot, node); }
        finally
        {
            // La división nueva ya trae la franja: se quita la vista previa
            // sin animar (la geometría es la misma) y la grilla vuelve a acercarse.
            panel.BeginAnimation(VideoLayoutPanel.GrowProgressProperty, null);
            panel.GrowProgress = 0;
            panel.GrowEdge = GridEdge.None;
            Animate(panel, VideoLayoutPanel.InsetProperty, 0);
        }
        return true;
    }

    private static void Animate(VideoLayoutPanel panel, DependencyProperty property, double to, Action? completed = null)
    {
        var animation = new DoubleAnimation(to, AnimationTime) { EasingFunction = Ease };
        // Antes de empezar: el reloj de la animación copia los manejadores al crearse.
        if (completed is not null) animation.Completed += (_, _) => completed();
        panel.BeginAnimation(property, animation);
    }

    // ------------------------------------------------------------------
    // Dibujo de la franja: contorno punteado del acento, sus cuadros y el
    // cuadro donde caería la cámara resaltado con el rótulo del cambio.
    // ------------------------------------------------------------------

    private void OnStripArranged(object? sender, EventArgs e) => Redraw();

    private void Redraw()
    {
        overlay.Children.Clear();
        if (_panel is not { Layout: { } layout } panel || panel.StripRect.IsEmpty || panel.GrowEdge == GridEdge.None)
            return;
        var origin = panel.TranslatePoint(new Point(0, 0), overlay);
        var strip = panel.StripRect;
        strip.Offset(origin.X, origin.Y);
        if (strip.Width < 2 || strip.Height < 2) return;

        var accent = (host.TryFindResource("AccentBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x38, 0xBD, 0xF8);
        Brush Tint(byte alpha) => new SolidColorBrush(Color.FromArgb(alpha, accent.R, accent.G, accent.B));

        Place(new Rectangle
        {
            Fill = Tint(0x1A), Stroke = Tint(0xCC), StrokeThickness = 1.2,
            StrokeDashArray = [5, 3], RadiusX = 3, RadiusY = 3,
        }, strip);

        bool sideways = panel.GrowEdge is GridEdge.Left or GridEdge.Right;
        int count = sideways ? layout.Rows : layout.Columns;
        for (int i = 0; i < count; i++)
        {
            var slot = sideways
                ? new Rect(strip.X, strip.Y + i * strip.Height / count, strip.Width, strip.Height / count)
                : new Rect(strip.X + i * strip.Width / count, strip.Y, strip.Width / count, strip.Height);
            slot.Inflate(-3, -3);
            if (slot.Width < 2 || slot.Height < 2) continue;
            bool target = i == _slot;
            Place(new Rectangle
            {
                Fill = target ? Tint(0x45) : Tint(0x0D),
                Stroke = target ? Tint(0xFF) : Tint(0x55),
                StrokeThickness = target ? 1.5 : 1,
                StrokeDashArray = target ? null : [3, 3],
                RadiusX = 2, RadiusY = 2,
            }, slot);
            if (target && slot.Width >= 60 && slot.Height >= 44)
                Place(Caption(layout, panel.GrowEdge, Tint(0xFF)), slot);
        }
    }

    /// <summary>"+" y el cambio de la grilla: "Columna nueva · 3×2 → 4×2".</summary>
    private static FrameworkElement Caption(VideoLayout layout, GridEdge edge, Brush accent)
    {
        bool sideways = edge is GridEdge.Left or GridEdge.Right;
        int columns = layout.Columns + (sideways ? 1 : 0), rows = layout.Rows + (sideways ? 0 : 1);
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = "+", FontSize = 26, FontWeight = FontWeights.Light, Foreground = accent,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -6, 0, 0),
        });
        panel.Children.Add(new TextBlock
        {
            Text = sideways ? "Columna nueva" : "Fila nueva", FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{layout.Columns}×{layout.Rows} → {columns}×{rows}", FontSize = 11, Foreground = accent,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0),
        });
        return panel;
    }

    private void Place(FrameworkElement element, Rect rect)
    {
        element.Width = rect.Width;
        element.Height = rect.Height;
        Canvas.SetLeft(element, rect.X);
        Canvas.SetTop(element, rect.Y);
        if (element is Panel content)
        {
            // El rótulo se centra en el cuadro sin agrandarlo.
            element.ClearValue(FrameworkElement.WidthProperty);
            element.ClearValue(FrameworkElement.HeightProperty);
            var holder = new Grid { Width = rect.Width, Height = rect.Height, IsHitTestVisible = false };
            holder.Children.Add(content);
            Canvas.SetLeft(holder, rect.X);
            Canvas.SetTop(holder, rect.Y);
            overlay.Children.Add(holder);
            return;
        }
        overlay.Children.Add(element);
    }
}
