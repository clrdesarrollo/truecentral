using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Línea de tiempo de 24 horas del módulo Reproducción: una pista por canal
/// abierto con sus tramos grabados (coloreados por tipo), la grilla horaria,
/// la aguja de reproducción y el tramo marcado para exportar. Un clic pide
/// reproducir desde esa hora (SeekRequested) y un arrastre marca un tramo
/// (SelectionChanged; el clic derecho lo borra). Es dibujo directo
/// (OnRender): cientos de segmentos sin costo de layout.
/// </summary>
public sealed class TimelineBar : FrameworkElement
{
    public static readonly DependencyProperty TracksProperty = DependencyProperty.Register(
        nameof(Tracks), typeof(IReadOnlyList<TimelineTrack>), typeof(TimelineBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DayProperty = DependencyProperty.Register(
        nameof(Day), typeof(DateTime), typeof(TimelineBar),
        new FrameworkPropertyMetadata(DateTime.Today, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayheadProperty = DependencyProperty.Register(
        nameof(Playhead), typeof(DateTime?), typeof(TimelineBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectionStartProperty = DependencyProperty.Register(
        nameof(SelectionStart), typeof(DateTime?), typeof(TimelineBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectionEndProperty = DependencyProperty.Register(
        nameof(SelectionEnd), typeof(DateTime?), typeof(TimelineBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<TimelineTrack>? Tracks
    {
        get => (IReadOnlyList<TimelineTrack>?)GetValue(TracksProperty);
        set => SetValue(TracksProperty, value);
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

    public DateTime? SelectionStart
    {
        get => (DateTime?)GetValue(SelectionStartProperty);
        set => SetValue(SelectionStartProperty, value);
    }

    public DateTime? SelectionEnd
    {
        get => (DateTime?)GetValue(SelectionEndProperty);
        set => SetValue(SelectionEndProperty, value);
    }

    /// <summary>El usuario pidió reproducir desde esta hora local.</summary>
    public event Action<DateTime>? SeekRequested;

    /// <summary>Tramo marcado con el arrastre (null, null = se borró la marca).</summary>
    public event Action<DateTime?, DateTime?>? SelectionChanged;

    private const double LabelStrip = 16;   // franja inferior para las horas
    private const double DragThreshold = 4; // px: menos que esto es un clic, no un arrastre

    private static readonly Brush Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0E, 0x13));
    private static readonly Brush TrackBrush = new SolidColorBrush(Color.FromRgb(0x11, 0x17, 0x1F));
    private static readonly Pen HourPen = new(new SolidColorBrush(Color.FromRgb(0x22, 0x2C, 0x38)), 1);
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x7A, 0x8C));
    private static readonly Brush TrackNameBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x9A, 0xAC));
    private static readonly Pen PlayheadPen = new(Brushes.White, 1.6);
    private static readonly Brush SelectionBrush = new SolidColorBrush(Color.FromArgb(0x46, 0x4C, 0xA0, 0xFF));
    private static readonly Pen SelectionPen = new(new SolidColorBrush(Color.FromRgb(0x4C, 0xA0, 0xFF)), 1);

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
        Background.Freeze(); TrackBrush.Freeze(); HourPen.Freeze(); LabelBrush.Freeze();
        TrackNameBrush.Freeze(); PlayheadPen.Freeze(); SelectionBrush.Freeze(); SelectionPen.Freeze();
    }

    public TimelineBar()
    {
        Cursor = Cursors.Hand;
        ToolTip = "Clic: reproducir desde esa hora · Arrastrar: marcar un tramo · Clic derecho: borrar la marca";
    }

    // Arrastre en curso (se dibuja mientras el botón sigue apretado).
    private double _dragStartX = -1;
    private DateTime? _dragFrom;
    private DateTime? _dragTo;

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        double barHeight = Math.Max(8, height - LabelStrip);

        dc.DrawRectangle(Background, null, new Rect(0, 0, width, height));

        var tracks = Tracks is { Count: > 0 } list ? list : null;
        int rows = Math.Max(tracks?.Count ?? 1, 1);
        double rowHeight = barHeight / rows;
        var dayStart = Day.Date;
        var typeface = new Typeface("Segoe UI");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int row = 0; row < rows; row++)
        {
            double top = row * rowHeight + 2;
            double bodyHeight = Math.Max(4, rowHeight - 4);
            dc.DrawRectangle(TrackBrush, null, new Rect(0, top, width, bodyHeight));

            if (tracks is null) continue;
            foreach (var segment in tracks[row].Segments)
            {
                double x1 = Math.Max(0, (segment.Start - dayStart).TotalHours / 24 * width);
                double x2 = Math.Min(width, (segment.End - dayStart).TotalHours / 24 * width);
                if (x2 <= 0 || x1 >= width) continue;
                if (x2 - x1 < 1) x2 = x1 + 1; // que los tramos cortos se vean
                var brush = KindBrushes.TryGetValue(segment.Kind, out var known) ? known : KindBrushes["Other"];
                dc.DrawRectangle(brush, null, new Rect(x1, top, x2 - x1, bodyHeight));
            }

            // Nombre del canal sobre su pista (solo si hay espacio vertical).
            if (rows > 1 && bodyHeight >= 14)
            {
                var name = new FormattedText(tracks[row].Name, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, typeface, 10, TrackNameBrush, dpi)
                {
                    MaxTextWidth = Math.Max(40, width / 4),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                dc.DrawText(name, new Point(4, top + (bodyHeight - name.Height) / 2));
            }
        }

        // Grilla horaria y rótulos cada 2 horas
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

        // Tramo marcado (el del arrastre en curso manda sobre el confirmado)
        var selectionFrom = _dragFrom ?? SelectionStart;
        var selectionTo = _dragTo ?? SelectionEnd;
        if (selectionFrom is { } from && selectionTo is { } to && to > from)
        {
            double x1 = Math.Max(0, (from - dayStart).TotalHours / 24 * width);
            double x2 = Math.Min(width, (to - dayStart).TotalHours / 24 * width);
            dc.DrawRectangle(SelectionBrush, null, new Rect(x1, 0, Math.Max(1, x2 - x1), barHeight));
            dc.DrawLine(SelectionPen, new Point(x1, 0), new Point(x1, barHeight));
            dc.DrawLine(SelectionPen, new Point(x2, 0), new Point(x2, barHeight));
        }

        // Aguja de reproducción
        if (Playhead is { } playhead && playhead.Date == dayStart)
        {
            double x = (playhead - dayStart).TotalHours / 24 * width;
            dc.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, barHeight));
        }
    }

    private DateTime TimeAt(double x) =>
        Day.Date.AddHours(Math.Clamp(x / Math.Max(ActualWidth, 1), 0, 1) * 24);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (ActualWidth <= 0) return;
        _dragStartX = e.GetPosition(this).X;
        _dragFrom = _dragTo = null;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragStartX < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        double x = e.GetPosition(this).X;
        if (Math.Abs(x - _dragStartX) < DragThreshold && _dragFrom is null) return;
        _dragFrom = TimeAt(Math.Min(_dragStartX, x));
        _dragTo = TimeAt(Math.Max(_dragStartX, x));
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragStartX < 0) return;
        ReleaseMouseCapture();
        double startX = _dragStartX;
        _dragStartX = -1;

        if (_dragFrom is { } from && _dragTo is { } to)
        {
            _dragFrom = _dragTo = null;
            SelectionChanged?.Invoke(from, to);
            InvalidateVisual();
            return;
        }
        SeekRequested?.Invoke(TimeAt(startX));
    }

    /// <summary>Clic derecho: borrar el tramo marcado.</summary>
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        _dragFrom = _dragTo = null;
        SelectionChanged?.Invoke(null, null);
        InvalidateVisual();
    }
}
