using System.Windows;
using System.Windows.Controls;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Panel de la grilla de video: ubica cada hijo en el rectángulo que la
/// división activa define (soporta las divisiones asimétricas estilo
/// iVMS-4200: un cuadro grande + varios chicos). El hijo i usa Layout.Cells[i].
/// También dibuja las miniaturas del selector de divisiones.
/// </summary>
public sealed class VideoLayoutPanel : Panel
{
    // Los cuadros que sobran se acomodan fuera de la vista (ver StripSlot).
    public VideoLayoutPanel() => ClipToBounds = true;

    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout), typeof(VideoLayout), typeof(VideoLayoutPanel),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange,
            OnLayoutChanged));

    /// <summary>La división nueva ya trae la franja que se estaba mostrando: la
    /// vista previa se apaga sin animar (la geometría es la misma) y la grilla
    /// vuelve a acercarse, sin esperar a que la cámara soltada termine de abrir.</summary>
    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (VideoLayoutPanel)d;
        if (panel.GrowEdge == GridEdge.None) return;
        panel.BeginAnimation(GrowProgressProperty, null);
        panel.GrowProgress = 0;
        panel.GrowEdge = GridEdge.None;
        panel.BeginAnimation(InsetProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(170)));
    }

    public VideoLayout? Layout
    {
        get => (VideoLayout?)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    /// <summary>Índice del cuadro maximizado con doble clic: ese hijo ocupa
    /// todo el panel y el resto queda oculto (sin destruirse: sus streams
    /// siguen corriendo). −1 = grilla normal.</summary>
    public static readonly DependencyProperty MaximizedIndexProperty = DependencyProperty.Register(
        nameof(MaximizedIndex), typeof(int), typeof(VideoLayoutPanel),
        new FrameworkPropertyMetadata(-1,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public int MaximizedIndex
    {
        get => (int)GetValue(MaximizedIndexProperty);
        set => SetValue(MaximizedIndexProperty, value);
    }

    private bool IsMaximized => MaximizedIndex >= 0 && MaximizedIndex < InternalChildren.Count;

    // ------------------------------------------------------------------
    // Ampliar la grilla: mientras se arrastra una cámara sobre un borde, la
    // grilla se acomoda como si ya tuviera la franja nueva (GrowProgress de 0
    // a 1, animado) y se aleja un poco de los bordes (Inset, en píxeles). Es
    // acomodo y no una transformación: el video de Flyleaf vive en ventanas
    // propias que siguen el acomodo, pero no un RenderTransform.
    // ------------------------------------------------------------------

    public static readonly DependencyProperty GrowEdgeProperty = DependencyProperty.Register(
        nameof(GrowEdge), typeof(GridEdge), typeof(VideoLayoutPanel),
        new FrameworkPropertyMetadata(GridEdge.None,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>Borde donde se está mostrando la franja nueva (None = ninguno).</summary>
    public GridEdge GrowEdge
    {
        get => (GridEdge)GetValue(GrowEdgeProperty);
        set => SetValue(GrowEdgeProperty, value);
    }

    public static readonly DependencyProperty GrowProgressProperty = DependencyProperty.Register(
        nameof(GrowProgress), typeof(double), typeof(VideoLayoutPanel),
        new FrameworkPropertyMetadata(0.0,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>Cuánto de la franja nueva se muestra: 0 nada, 1 una fila o columna entera.</summary>
    public double GrowProgress
    {
        get => (double)GetValue(GrowProgressProperty);
        set => SetValue(GrowProgressProperty, value);
    }

    public static readonly DependencyProperty InsetProperty = DependencyProperty.Register(
        nameof(Inset), typeof(double), typeof(VideoLayoutPanel),
        new FrameworkPropertyMetadata(0.0,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>Margen con que la grilla se aleja de los bordes (efecto de alejar).</summary>
    public double Inset
    {
        get => (double)GetValue(InsetProperty);
        set => SetValue(InsetProperty, value);
    }

    /// <summary>Dónde quedó la franja nueva en el último acomodo (vacío si no hay).</summary>
    public Rect StripRect { get; private set; } = Rect.Empty;

    /// <summary>Cada acomodo con franja (o al quitarla): la capa que la dibuja se pone al día.</summary>
    public event EventHandler? StripArranged;

    /// <summary>Rectángulo de cada cuadro y de la franja para el tamaño dado.</summary>
    private (Rect Strip, Func<LayoutCell, Rect> Place) Geometry(VideoLayout layout, Size size)
    {
        double inset = Math.Min(Inset, Math.Min(size.Width, size.Height) / 4);
        var area = new Rect(inset, inset, Math.Max(0, size.Width - 2 * inset), Math.Max(0, size.Height - 2 * inset));
        var edge = GrowEdge;
        double p = edge == GridEdge.None ? 0 : Math.Clamp(GrowProgress, 0, 1);
        bool sideways = edge is GridEdge.Left or GridEdge.Right;
        double slotWidth = area.Width / (layout.Columns + (sideways ? p : 0));
        double slotHeight = area.Height / (layout.Rows + (sideways ? 0 : p));
        double shiftX = edge == GridEdge.Left ? p * slotWidth : 0;
        double shiftY = edge == GridEdge.Top ? p * slotHeight : 0;
        var strip = edge switch
        {
            GridEdge.Left => new Rect(area.X, area.Y, p * slotWidth, area.Height),
            GridEdge.Right => new Rect(area.X + layout.Columns * slotWidth, area.Y, p * slotWidth, area.Height),
            GridEdge.Top => new Rect(area.X, area.Y, area.Width, p * slotHeight),
            GridEdge.Bottom => new Rect(area.X, area.Y + layout.Rows * slotHeight, area.Width, p * slotHeight),
            _ => Rect.Empty,
        };
        return (strip, cell => new Rect(
            area.X + shiftX + cell.Col * slotWidth, area.Y + shiftY + cell.Row * slotHeight,
            slotWidth * cell.ColSpan, slotHeight * cell.RowSpan));
    }

    /// <summary>
    /// Lugar de un cuadro que sobra de la división (recién creado, todavía no
    /// es parte de ella): el cuadro k de la franja, o fuera de la vista con el
    /// tamaño de una celda. Nunca 0×0: si recibe video antes de que cambie la
    /// división (ampliar, separar), Flyleaf necesita una superficie de tamaño
    /// real o se cae.
    /// </summary>
    private Rect StripSlot(VideoLayout layout, Rect strip, int k, Size size)
    {
        // Uno con video que sale de la división: 0×0 (su ventana de Flyleaf es
        // de primer nivel y no la recorta el ClipToBounds de WPF).
        if (InternalChildren[layout.Cells.Count + k] is FrameworkElement { DataContext: VideoCellViewModel { IsEmpty: false } })
            return new Rect(0, 0, 0, 0);
        if (strip.IsEmpty || strip.Width < 1 || strip.Height < 1)
            return new Rect(size.Width + 8, 0, Math.Max(size.Width / layout.Columns, 16), Math.Max(size.Height / layout.Rows, 16));
        bool sideways = GrowEdge is GridEdge.Left or GridEdge.Right;
        int count = sideways ? layout.Rows : layout.Columns;
        k = Math.Min(k, count - 1);
        return sideways
            ? new Rect(strip.X, strip.Y + k * strip.Height / count, strip.Width, strip.Height / count)
            : new Rect(strip.X + k * strip.Width / count, strip.Y, strip.Width / count, strip.Height);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = Normalize(availableSize);
        if (IsMaximized || Layout is not { } layout || layout.Cells.Count == 0)
        {
            foreach (UIElement child in InternalChildren)
                child.Measure(size);
            return size;
        }
        var (strip, place) = Geometry(layout, size);
        for (int i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Measure(i < layout.Cells.Count
                ? place(layout.Cells[i]).Size
                : StripSlot(layout, strip, i - layout.Cells.Count, size).Size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var strip = Rect.Empty;
        if (IsMaximized)
        {
            for (int i = 0; i < InternalChildren.Count; i++)
                InternalChildren[i].Arrange(i == MaximizedIndex
                    ? new Rect(0, 0, finalSize.Width, finalSize.Height)
                    : new Rect(0, 0, 0, 0));
        }
        else if (Layout is { } layout && layout.Cells.Count > 0)
        {
            (strip, var place) = Geometry(layout, finalSize);
            for (int i = 0; i < InternalChildren.Count; i++)
            {
                // Hijos sobrantes durante un cambio de división: fuera de la vista;
                // al ampliar la grilla, los cuadros nuevos ya en la franja (el
                // video que reciban necesita una superficie de tamaño real).
                InternalChildren[i].Arrange(i < layout.Cells.Count
                    ? place(layout.Cells[i])
                    : StripSlot(layout, strip, i - layout.Cells.Count, finalSize));
            }
        }
        if (strip != StripRect)
        {
            StripRect = strip;
            StripArranged?.Invoke(this, EventArgs.Empty);
        }
        return finalSize;
    }

    /// <summary>Measure puede llegar infinito (ScrollViewer/Popup): un tamaño
    /// finito cualquiera basta, el Arrange final manda.</summary>
    private static Size Normalize(Size size) => new(
        double.IsInfinity(size.Width) ? 640 : size.Width,
        double.IsInfinity(size.Height) ? 360 : size.Height);
}
