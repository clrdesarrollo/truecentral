using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

// ---------------------------------------------------------------------------
// Medidores de la tarjeta Servidor del Centro de mando, los mismos del panel
// de inicio del web: el semicírculo de porcentaje (CPU, memoria, disco), el de
// aguja del throughput y la curva de los últimos minutos. Se dibujan en las
// coordenadas del SVG del web, escaladas al ancho disponible, con los pinceles
// del tema (siguen la paleta elegida).
// ---------------------------------------------------------------------------

/// <summary>
/// Base de los medidores: alto proporcional al ancho y el arco que se anima
/// hacia el valor nuevo (como la transición del web).
/// </summary>
public abstract class GaugeBase : FrameworkElement
{
    // Fracción del arco que se ve (0..1); corre hacia Target en cada cambio.
    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        "Shown", typeof(double), typeof(GaugeBase),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    protected double Shown => (double)GetValue(ShownProperty);

    /// <summary>Tamaño del dibujo en sus propias unidades (el viewBox del SVG del web).</summary>
    protected abstract Size Design { get; }

    /// <summary>Fracción del arco que corresponde al valor pedido.</summary>
    protected abstract double Target { get; }

    protected abstract void Draw(DrawingContext dc);

    protected static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (GaugeBase)d;
        double to = double.IsNaN(gauge.Target) ? 0 : Math.Clamp(gauge.Target, 0, 1);
        gauge.BeginAnimation(ShownProperty, new DoubleAnimation(to, TimeSpan.FromSeconds(0.9))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        // El texto cambia aunque la fracción no.
        gauge.InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? Design.Width : available.Width;
        if (!double.IsInfinity(available.Height)) width = Math.Min(width, available.Height * Design.Width / Design.Height);
        return new Size(width, width * Design.Height / Design.Width);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double scale = Math.Min(ActualWidth / Design.Width, ActualHeight / Design.Height);
        if (scale <= 0) return;
        dc.PushTransform(new TranslateTransform((ActualWidth - Design.Width * scale) / 2, (ActualHeight - Design.Height * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        Draw(dc);
        dc.Pop();
        dc.Pop();
    }

    protected Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>Escribe centrado en x con la línea base en y (como text-anchor="middle" del SVG).</summary>
    protected void Label(DrawingContext dc, string text, double x, double y, double size, Brush brush, FontWeight weight)
    {
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, weight, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(x - formatted.Width / 2, y - formatted.Baseline));
    }

    /// <summary>Punto del círculo en el ángulo dado (grados; 0 = derecha, crece en sentido horario).</summary>
    protected static Point At(double cx, double cy, double r, double degrees)
    {
        double a = degrees * Math.PI / 180;
        return new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
    }

    protected static Geometry Arc(double cx, double cy, double r, double from, double sweep)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(At(cx, cy, r, from), false, false);
            ctx.ArcTo(At(cx, cy, r, from + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    protected static Pen RoundPen(Brush brush, double thickness) =>
        new(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
}

/// <summary>Semicírculo de porcentaje (0–100): verde, ámbar desde 75 y rojo desde 90, con el valor al centro.</summary>
public sealed class PercentGauge : GaugeBase
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(PercentGauge), new PropertyMetadata(double.NaN, OnTargetChanged));

    /// <summary>Porcentaje; NaN = sin dato («—»).</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override Size Design => new(100, 60);
    protected override double Target => Value / 100;

    protected override void Draw(DrawingContext dc)
    {
        dc.DrawGeometry(null, RoundPen(Res("Panel2Brush"), 9), Arc(50, 52, 40, 180, 180));
        double v = double.IsNaN(Value) ? 0 : Math.Clamp(Value, 0, 100);
        if (Shown > 0.002)
            dc.DrawGeometry(null, RoundPen(Res(v >= 90 ? "DangerBrush" : v >= 75 ? "WarnBrush" : "OkBrush"), 9),
                Arc(50, 52, 40, 180, 180 * Shown));
        Label(dc, double.IsNaN(Value) ? "—" : $"{v:0}%", 50, 50, 19, Res("TextBrush"), FontWeights.Bold);
    }
}

/// <summary>
/// Medidor de aguja del throughput: arco de 240° con degradado (ámbar o rojo
/// si el enlace va cargado), marcas en los cuartos de la escala, la aguja y el
/// total al centro.
/// </summary>
public sealed class ThroughputGauge : GaugeBase
{
    private const double Cx = 100, Cy = 100, R = 70, From = 150, Sweep = 240;

    private static readonly Brush Gradient = CreateGradient();
    private static readonly Geometry Needle = Geometry.Parse(
        FormattableString.Invariant($"M{Cx - 8},{Cy} L{Cx},{Cy - 3.5} L{Cx + R - 12},{Cy} L{Cx},{Cy + 3.5} Z"));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ThroughputGauge), new PropertyMetadata(0.0, OnTargetChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ThroughputGauge), new PropertyMetadata(10.0, OnTargetChanged));

    /// <summary>Carga del enlace: Ok (degradado), Warning (ámbar) o Alarm (rojo).</summary>
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(string), typeof(ThroughputGauge),
        new FrameworkPropertyMetadata("Ok", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Total (entrada + salida) en Mbit/s.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Fondo de la escala en Mbit/s.</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public string Level
    {
        get => (string)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    protected override Size Design => new(200, 160);
    protected override double Target => Maximum > 0 ? Value / Maximum : 0;

    protected override void Draw(DrawingContext dc)
    {
        var text = Res("TextBrush");
        var muted = Res("MutedBrush");
        dc.DrawGeometry(null, RoundPen(Res("Panel2Brush"), 12), Arc(Cx, Cy, R, From, Sweep));
        var value = Level switch { "Alarm" => Res("DangerBrush"), "Warning" => Res("WarnBrush"), _ => Gradient };
        if (Shown > 0.002) dc.DrawGeometry(null, RoundPen(value, 12), Arc(Cx, Cy, R, From, Sweep * Shown));

        var tick = new Pen(Res("DimTextBrush"), 1.2);
        foreach (double f in new[] { 0, 0.25, 0.5, 0.75, 1 })
        {
            double angle = From + Sweep * f;
            dc.DrawLine(tick, At(Cx, Cy, R + 9, angle), At(Cx, Cy, R - 9, angle));
            var at = At(Cx, Cy, R + 19, angle);
            Label(dc, NetRate.Split(Maximum * f).Number, at.X, at.Y + 3, 9, muted, FontWeights.Normal);
        }

        dc.PushTransform(new RotateTransform(From + Sweep * Shown, Cx, Cy));
        dc.DrawGeometry(text, null, Needle);
        dc.Pop();
        dc.DrawEllipse(Res("CellBgBrush"), new Pen(text, 3), new Point(Cx, Cy), 7, 7);

        var (number, unit) = NetRate.Split(Value);
        Label(dc, number, Cx, Cy + 34, 26, text, FontWeights.Bold);
        Label(dc, unit, Cx, Cy + 50, 11, muted, FontWeights.Normal);
    }

    // De celeste a violeta a lo ancho del arco completo (x 30 a 170), no del tramo dibujado.
    private static Brush CreateGradient()
    {
        var brush = new LinearGradientBrush(
            [
                new GradientStop(Color.FromRgb(0x6C, 0xB3, 0xFA), 0),
                new GradientStop(Color.FromRgb(0x1F, 0x6F, 0xEB), 0.6),
                new GradientStop(Color.FromRgb(0x7C, 0x5C, 0xFF), 1),
            ],
            new Point(Cx - R, 0), new Point(Cx + R, 0)) { MappingMode = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }
}

/// <summary>Curva del tráfico de los últimos minutos, estirada al tamaño del control, con el área en tono suave.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        double w = ActualWidth, h = ActualHeight;
        if (values is not { Count: > 1 } || w <= 0 || h <= 4) return;
        double max = Math.Max(1, values.Max());
        var points = values.Select((v, k) => new Point(k * w / (values.Count - 1), h - 2 - v / max * (h - 4))).ToList();

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            ctx.PolyLineTo(points.Skip(1).ToList(), true, true);
        }
        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            ctx.PolyLineTo([.. points, new Point(w, h)], false, true);
        }

        var accent = TryFindResource("AccentBrush") as SolidColorBrush ?? Brushes.SteelBlue;
        var fill = new LinearGradientBrush(Color.FromArgb(0x55, accent.Color.R, accent.Color.G, accent.Color.B),
            Color.FromArgb(0x05, accent.Color.R, accent.Color.G, accent.Color.B), 90);
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(accent, 1.5) { LineJoin = PenLineJoin.Round }, line);
    }
}
