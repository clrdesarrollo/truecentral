using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Línea de tiempo de 24 horas del módulo Reproducción: dibuja los tramos
/// grabados (coloreados por tipo), la grilla horaria y la aguja de
/// reproducción. Un clic pide reproducir desde esa hora (evento SeekRequested).
/// Es dibujo directo (OnRender): cientos de segmentos sin costo de layout.
/// </summary>
public sealed class TimelineBar : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<RecordingSegmentDto>), typeof(TimelineBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DayProperty = DependencyProperty.Register(
        nameof(Day), typeof(DateTime), typeof(TimelineBar),
        new FrameworkPropertyMetadata(DateTime.Today, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayheadProperty = DependencyProperty.Register(
        nameof(Playhead), typeof(DateTime?), typeof(TimelineBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<RecordingSegmentDto>? Segments
    {
        get => (IReadOnlyList<RecordingSegmentDto>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public DateTime Day
    {
        get => (DateTime)GetValue(DayProperty);
        set => SetValue(DayProperty, value);
    }

    public DateTime? Playhead
    {
        get => (DateTime?)GetValue(PlayheadProperty);
        set => SetValue(PlayheadProperty, value);
    }

    /// <summary>El usuario pidió reproducir desde esta hora local.</summary>
    public event Action<DateTime>? SeekRequested;

    private const double LabelStrip = 16; // franja inferior para las horas

    private static readonly Brush Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0E, 0x13));
    private static readonly Pen HourPen = new(new SolidColorBrush(Color.FromRgb(0x22, 0x2C, 0x38)), 1);
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x7A, 0x8C));
    private static readonly Pen PlayheadPen = new(Brushes.White, 1.6);

    private static readonly Dictionary<string, Brush> KindBrushes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Continuous"] = new SolidColorBrush(Color.FromRgb(0x2E, 0x6B, 0xC4)),
        ["Motion"] = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
        ["Alarm"] = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
        ["Manual"] = new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)),
        ["Other"] = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
    };

    static TimelineBar()
    {
        foreach (var brush in KindBrushes.Values) brush.Freeze();
        Background.Freeze(); HourPen.Freeze(); LabelBrush.Freeze(); PlayheadPen.Freeze();
    }

    public TimelineBar()
    {
        Cursor = Cursors.Hand;
        ToolTip = "Clic para reproducir desde esa hora";
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        double barHeight = Math.Max(8, height - LabelStrip);

        dc.DrawRectangle(Background, null, new Rect(0, 0, width, height));

        // Tramos grabados
        if (Segments is { } segments)
        {
            var dayStart = Day.Date;
            foreach (var segment in segments)
            {
                double x1 = Math.Max(0, (segment.Start - dayStart).TotalHours / 24 * width);
                double x2 = Math.Min(width, (segment.End - dayStart).TotalHours / 24 * width);
                if (x2 - x1 < 1) x2 = x1 + 1; // que los tramos cortos se vean
                var brush = KindBrushes.TryGetValue(segment.Kind, out var b) ? b : KindBrushes["Other"];
                dc.DrawRectangle(brush, null, new Rect(x1, 3, x2 - x1, barHeight - 6));
            }
        }

        // Grilla horaria y rótulos cada 2 horas
        var typeface = new Typeface("Segoe UI");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int hour = 0; hour <= 24; hour++)
        {
            double x = hour / 24.0 * width;
            dc.DrawLine(HourPen, new Point(x, 0), new Point(x, barHeight));
            if (hour % 2 == 0 && hour < 24)
            {
                var text = new FormattedText($"{hour:00}", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 9.5, LabelBrush, dpi);
                dc.DrawText(text, new Point(x + 2, barHeight + 1));
            }
        }

        // Aguja de reproducción
        if (Playhead is { } playhead && playhead.Date == Day.Date)
        {
            double x = (playhead - Day.Date).TotalHours / 24 * width;
            dc.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, barHeight));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (ActualWidth <= 0) return;
        double fraction = Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1);
        SeekRequested?.Invoke(Day.Date.AddHours(fraction * 24));
    }
}
