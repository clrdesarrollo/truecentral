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
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout), typeof(VideoLayout), typeof(VideoLayoutPanel),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

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

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = Normalize(availableSize);
        if (IsMaximized || Layout is not { } layout || layout.Cells.Count == 0)
        {
            foreach (UIElement child in InternalChildren)
                child.Measure(size);
            return size;
        }
        double slotWidth = size.Width / layout.Columns, slotHeight = size.Height / layout.Rows;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var cell = layout.Cells[Math.Min(i, layout.Cells.Count - 1)];
            InternalChildren[i].Measure(new Size(slotWidth * cell.ColSpan, slotHeight * cell.RowSpan));
        }
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (IsMaximized)
        {
            for (int i = 0; i < InternalChildren.Count; i++)
                InternalChildren[i].Arrange(i == MaximizedIndex
                    ? new Rect(0, 0, finalSize.Width, finalSize.Height)
                    : new Rect(0, 0, 0, 0));
            return finalSize;
        }
        if (Layout is not { } layout || layout.Cells.Count == 0)
            return finalSize;
        double slotWidth = finalSize.Width / layout.Columns, slotHeight = finalSize.Height / layout.Rows;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            // Hijos sobrantes durante un cambio de división: fuera de la vista.
            if (i >= layout.Cells.Count)
            {
                InternalChildren[i].Arrange(new Rect(0, 0, 0, 0));
                continue;
            }
            var cell = layout.Cells[i];
            InternalChildren[i].Arrange(new Rect(
                cell.Col * slotWidth, cell.Row * slotHeight,
                slotWidth * cell.ColSpan, slotHeight * cell.RowSpan));
        }
        return finalSize;
    }

    /// <summary>Measure puede llegar infinito (ScrollViewer/Popup): un tamaño
    /// finito cualquiera basta, el Arrange final manda.</summary>
    private static Size Normalize(Size size) => new(
        double.IsInfinity(size.Width) ? 640 : size.Width,
        double.IsInfinity(size.Height) ? 360 : size.Height);
}
