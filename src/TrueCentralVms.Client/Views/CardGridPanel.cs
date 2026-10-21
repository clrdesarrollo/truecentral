using System.Windows;
using System.Windows.Controls;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Grilla de tarjetas del Inicio: caben tantas columnas como permita
/// <see cref="MinItemWidth"/> y todas se estiran por igual hasta ocupar el
/// ancho disponible. Los hijos ocultos (sin permiso) no ocupan casilla.
/// </summary>
public sealed class CardGridPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(CardGridPanel),
        new FrameworkPropertyMetadata(300.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(CardGridPanel),
        new FrameworkPropertyMetadata(112.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(CardGridPanel),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private int VisibleCount()
    {
        var n = 0;
        foreach (UIElement child in InternalChildren)
            if (child.Visibility != Visibility.Collapsed) n++;
        return n;
    }

    private int Columns(double width) =>
        double.IsInfinity(width) ? Math.Max(1, VisibleCount())
            : Math.Max(1, (int)((width + Spacing) / (MinItemWidth + Spacing)));

    private double ItemWidth(double width, int columns) =>
        double.IsInfinity(width) ? MinItemWidth
            : Math.Max(0, (width - Spacing * (columns - 1)) / columns);

    protected override Size MeasureOverride(Size available)
    {
        var columns = Columns(available.Width);
        var itemWidth = ItemWidth(available.Width, columns);
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(itemWidth, ItemHeight));

        var count = VisibleCount();
        if (count == 0) return new Size(0, 0);
        var rows = (count + columns - 1) / columns;
        var width = double.IsInfinity(available.Width)
            ? count * itemWidth + (count - 1) * Spacing
            : available.Width;
        return new Size(width, rows * ItemHeight + (rows - 1) * Spacing);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var columns = Columns(final.Width);
        var itemWidth = ItemWidth(final.Width, columns);
        var i = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var col = i % columns;
            var row = i / columns;
            child.Arrange(new Rect(col * (itemWidth + Spacing), row * (ItemHeight + Spacing),
                itemWidth, ItemHeight));
            i++;
        }
        return final;
    }
}
