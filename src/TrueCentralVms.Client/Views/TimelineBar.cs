using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Línea de tiempo del módulo Reproducción: una pista por canal abierto con
/// sus tramos grabados (coloreados por tipo), regla horaria, aguja de
/// reproducción y tramo marcado para exportar.
///
/// La ventana visible es un ZOOM sobre el día (24 h a 1 min): los botones
/// − / + y la rueda del mouse cambian la densidad, y mientras se reproduce la
/// vista sigue a la aguja. Arrastrar mueve la aguja (al soltar se reproduce
/// desde ahí) y, si el cursor se sale por un costado, la vista acompaña. En
/// modo recorte el arrastre marca un tramo en vez de navegar.
///
/// Es dibujo directo (OnRender): cientos de segmentos sin costo de layout.
/// </summary>
public sealed class TimelineBar : FrameworkElement
{
    /// <summary>Duraciones de la ventana visible, de menor a mayor zoom (minutos).</summary>
    private static readonly int[] ZoomMinutes = [1440, 720, 360, 180, 60, 30, 10, 5, 2, 1];

    public static readonly DependencyProperty TracksProperty = DependencyProperty.Register(
        nameof(Tracks), typeof(IReadOnlyList<TimelineTrack>), typeof(TimelineBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DayProperty = DependencyProperty.Register(
        nameof(Day), typeof(DateTime), typeof(TimelineBar),
        new FrameworkPropertyMetadata(DateTime.Today, FrameworkPropertyMetadataOptions.AffectsRender, OnDayChanged));

    public static readonly DependencyProperty PlayheadProperty = DependencyProperty.Register(
        nameof(Playhead), typeof(DateTime?), typeof(TimelineBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPlayheadChanged));

    public static readonly DependencyProperty SelectionStartProperty = DependencyProperty.Register(
        nameof(SelectionStart), typeof(DateTime?), typeof(TimelineBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectionEndProperty = DependencyProperty.Register(
        nameof(SelectionEnd), typeof(DateTime?), typeof(TimelineBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Modo recorte: el arrastre marca un tramo en vez de navegar.</summary>
    public static readonly DependencyProperty SelectionModeProperty = DependencyProperty.Register(
        nameof(SelectionMode), typeof(bool), typeof(TimelineBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnSelectionModeChanged));

    /// <summary>Densidad actual para mostrar junto a los botones − / + ("24 h", "10 min"…).</summary>
    public static readonly DependencyProperty SpanLabelProperty = DependencyProperty.Register(
        nameof(SpanLabel), typeof(string), typeof(TimelineBar), new PropertyMetadata("24 h"));

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

    public bool SelectionMode
    {
        get => (bool)GetValue(SelectionModeProperty);
        set => SetValue(SelectionModeProperty, value);
    }

    public string SpanLabel
    {
        get => (string)GetValue(SpanLabelProperty);
        private set => SetValue(SpanLabelProperty, value);
    }

    /// <summary>El usuario pidió reproducir desde esta hora local.</summary>
    public event Action<DateTime>? SeekRequested;

    /// <summary>Tramo marcado con el arrastre (null, null = se borró la marca).</summary>
    public event Action<DateTime?, DateTime?>? SelectionChanged;

    /// <summary>Hora bajo el cursor mientras se arrastra la aguja (null al soltar).</summary>
    public event Action<DateTime?>? Scrubbing;

    private const double RulerStrip = 16;   // franja inferior con las horas
    private const double DragThreshold = 3; // px: menos que esto es un clic

    private static readonly Brush Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0E, 0x13));
    private static readonly Brush TrackBrush = new SolidColorBrush(Color.FromRgb(0x11, 0x17, 0x1F));
    private static readonly Pen TickPen = new(new SolidColorBrush(Color.FromRgb(0x1C, 0x24, 0x2E)), 1);
    private static readonly Pen LabelTickPen = new(new SolidColorBrush(Color.FromRgb(0x2C, 0x38, 0x46)), 1);
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x7A, 0x8C));
    private static readonly Brush TrackNameBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x9A, 0xAC));
    private static readonly Pen PlayheadPen = new(new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4D)), 1.6);
    private static readonly Brush PlayheadBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4D));
    private static readonly Brush SelectionBrush = new SolidColorBrush(Color.FromArgb(0x46, 0x4C, 0xA0, 0xFF));
    private static readonly Pen SelectionPen = new(new SolidColorBrush(Color.FromRgb(0x4C, 0xA0, 0xFF)), 1);
    private static readonly Brush TimeChipBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x22, 0x2C));

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
        Background.Freeze(); TrackBrush.Freeze(); TickPen.Freeze(); LabelTickPen.Freeze();
        LabelBrush.Freeze(); TrackNameBrush.Freeze(); PlayheadPen.Freeze(); PlayheadBrush.Freeze();
        SelectionBrush.Freeze(); SelectionPen.Freeze(); TimeChipBrush.Freeze();
    }

    public TimelineBar()
    {
        Cursor = Cursors.Hand;
        UpdateToolTip();
    }

    // ---------- Ventana visible ----------

    private int _zoom;                 // índice en ZoomMinutes (0 = día completo)
    private DateTime? _center;         // centro de la ventana; null = seguir a la aguja
    private static readonly Typeface Font = new("Segoe UI");

    private TimeSpan Span => TimeSpan.FromMinutes(ZoomMinutes[_zoom]);

    private DateTime ViewStart
    {
        get
        {
            var day = Day.Date;
            var span = Span;
            if (span >= TimeSpan.FromHours(24)) return day;
            var center = _center ?? Playhead ?? day.AddHours(12);
            var start = center - TimeSpan.FromTicks(span.Ticks / 2);
            var last = day.AddDays(1) - span;
            return start < day ? day : start > last ? last : start;
        }
    }

    /// <summary>Acerca la línea de tiempo (más detalle) alrededor de la aguja.</summary>
    public void ZoomIn() => SetZoom(_zoom + 1);

    /// <summary>Aleja la línea de tiempo (menos detalle).</summary>
    public void ZoomOut() => SetZoom(_zoom - 1);

    private void SetZoom(int level, DateTime? anchor = null)
    {
        level = Math.Clamp(level, 0, ZoomMinutes.Length - 1);
        if (level == _zoom) return;
        _center = anchor ?? _center ?? Playhead ?? ViewStart + TimeSpan.FromTicks(Span.Ticks / 2);
        _zoom = level;
        SpanLabel = ZoomMinutes[_zoom] >= 60 ? $"{ZoomMinutes[_zoom] / 60} h" : $"{ZoomMinutes[_zoom]} min";
        InvalidateVisual();
    }

    private static void OnDayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Otro día: la ventana vuelve a arrancar centrada en la aguja (o al medio).
        if (d is TimelineBar bar) bar._center = null;
    }

    private static void OnPlayheadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Mientras se reproduce, la vista acompaña a la aguja (la tira corre
        // bajo ella); durante un arrastre manda el usuario.
        if (d is TimelineBar { _dragStartX: < 0 } bar && e.NewValue is DateTime playhead)
            bar._center = playhead;
    }

    private static void OnSelectionModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineBar bar) bar.UpdateToolTip();
    }

    private void UpdateToolTip()
    {
        Cursor = SelectionMode ? Cursors.Cross : Cursors.Hand;
        ToolTip = SelectionMode
            ? "Modo recorte: arrastre para marcar el tramo a exportar · clic derecho borra la marca"
            : "Clic o arrastre: reproducir desde esa hora · rueda: acercar/alejar · clic derecho: borrar la marca";
    }

    // ---------- Dibujo ----------

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        double barHeight = Math.Max(8, height - RulerStrip);

        var viewStart = ViewStart;
        var span = Span;
        double PixelsAt(DateTime time) => (time - viewStart).TotalSeconds / span.TotalSeconds * width;

        dc.DrawRectangle(Background, null, new Rect(0, 0, width, height));

        var tracks = Tracks is { Count: > 0 } list ? list : null;
        int rows = Math.Max(tracks?.Count ?? 1, 1);
        double rowHeight = barHeight / rows;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int row = 0; row < rows; row++)
        {
            double top = row * rowHeight + 2;
            double bodyHeight = Math.Max(4, rowHeight - 4);
            dc.DrawRectangle(TrackBrush, null, new Rect(0, top, width, bodyHeight));

            if (tracks is null) continue;
            foreach (var segment in tracks[row].Segments)
            {
                double x1 = PixelsAt(segment.Start), x2 = PixelsAt(segment.End);
                if (x2 <= 0 || x1 >= width) continue; // fuera de la ventana visible
                x1 = Math.Max(0, x1);
                x2 = Math.Min(width, x2);
                if (x2 - x1 < 1) x2 = x1 + 1; // que los tramos cortos se vean
                var brush = KindBrushes.TryGetValue(segment.Kind, out var known) ? known : KindBrushes["Other"];
                dc.DrawRectangle(brush, null, new Rect(x1, top, x2 - x1, bodyHeight));
            }

            // Nombre del canal sobre su pista (solo si hay espacio vertical).
            if (rows > 1 && bodyHeight >= 14)
            {
                var name = new FormattedText(tracks[row].Name, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, Font, 10, TrackNameBrush, dpi)
                {
                    MaxTextWidth = Math.Max(40, width / 4),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                dc.DrawText(name, new Point(4, top + (bodyHeight - name.Height) / 2));
            }
        }

        DrawRuler(dc, width, barHeight, viewStart, span, dpi);

        // Tramo marcado (el del arrastre en curso manda sobre el confirmado)
        var selectionFrom = _dragFrom ?? SelectionStart;
        var selectionTo = _dragTo ?? SelectionEnd;
        if (selectionFrom is { } from && selectionTo is { } to && to > from)
        {
            double x1 = Math.Max(0, PixelsAt(from)), x2 = Math.Min(width, PixelsAt(to));
            if (x2 > 0 && x1 < width)
            {
                dc.DrawRectangle(SelectionBrush, null, new Rect(x1, 0, Math.Max(1, x2 - x1), barHeight));
                dc.DrawLine(SelectionPen, new Point(x1, 0), new Point(x1, barHeight));
                dc.DrawLine(SelectionPen, new Point(x2, 0), new Point(x2, barHeight));
            }
        }

        // Aguja: la del arrastre en curso, o la de la reproducción
        var needle = _scrubTime ?? Playhead;
        if (needle is { } time && time >= viewStart && time <= viewStart + span)
        {
            double x = PixelsAt(time);
            dc.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, barHeight));
            dc.DrawEllipse(PlayheadBrush, null, new Point(x, 3), 3, 3);
            if (_scrubTime is not null) DrawTimeChip(dc, x, barHeight, time, dpi, width);
        }
    }

    /// <summary>Regla horaria: la densidad de marcas y rótulos sigue al zoom.</summary>
    private static (TimeSpan Label, TimeSpan Tick) StepsFor(TimeSpan span) => span.TotalMinutes switch
    {
        >= 1440 => (TimeSpan.FromHours(2), TimeSpan.FromHours(1)),
        >= 720 => (TimeSpan.FromHours(1), TimeSpan.FromMinutes(30)),
        >= 360 => (TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(10)),
        >= 180 => (TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5)),
        >= 60 => (TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2)),
        >= 30 => (TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)),
        >= 10 => (TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30)),
        >= 5 => (TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(15)),
        >= 2 => (TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)),
        _ => (TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5)),
    };

    private void DrawRuler(DrawingContext dc, double width, double barHeight, DateTime viewStart, TimeSpan span, double dpi)
    {
        var (labelStep, tickStep) = StepsFor(span);
        string format = labelStep.TotalMinutes >= 1 ? "HH:mm" : "HH:mm:ss";
        var viewEnd = viewStart + span;

        // Primera marca alineada al múltiplo del paso desde la medianoche.
        var day = Day.Date;
        long tickTicks = tickStep.Ticks;
        var first = day.AddTicks((viewStart - day).Ticks / tickTicks * tickTicks);
        for (var time = first; time <= viewEnd; time = time.AddTicks(tickTicks))
        {
            if (time < viewStart) continue;
            double x = (time - viewStart).TotalSeconds / span.TotalSeconds * width;
            bool labeled = (time - day).Ticks % labelStep.Ticks == 0;
            dc.DrawLine(labeled ? LabelTickPen : TickPen,
                new Point(x, labeled ? 0 : barHeight - 6), new Point(x, barHeight));
            if (!labeled) continue;
            var text = new FormattedText(time.ToString(format, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 9.5, LabelBrush, dpi);
            double textX = Math.Min(x + 2, width - text.Width);
            dc.DrawText(text, new Point(Math.Max(0, textX), barHeight + 1));
        }
    }

    /// <summary>Etiqueta con la hora exacta bajo la aguja mientras se arrastra.</summary>
    private static void DrawTimeChip(DrawingContext dc, double x, double barHeight, DateTime time, double dpi, double width)
    {
        var text = new FormattedText(time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 10.5, Brushes.White, dpi);
        double chipWidth = text.Width + 12, chipHeight = text.Height + 4;
        double left = Math.Clamp(x - chipWidth / 2, 0, Math.Max(0, width - chipWidth));
        double top = Math.Max(0, (barHeight - chipHeight) / 2);
        dc.DrawRoundedRectangle(TimeChipBrush, SelectionPen, new Rect(left, top, chipWidth, chipHeight), 3, 3);
        dc.DrawText(text, new Point(left + 6, top + 2));
    }

    // ---------- Interacción ----------

    private double _dragStartX = -1;
    private bool _dragging;
    private DateTime? _dragFrom, _dragTo; // marca en curso (modo recorte)
    private DateTime? _scrubTime;         // aguja arrastrada (modo navegación)

    private DateTime TimeAt(double x)
    {
        var span = Span;
        var time = ViewStart + TimeSpan.FromSeconds(Math.Clamp(x / Math.Max(ActualWidth, 1), 0, 1) * span.TotalSeconds);
        var day = Day.Date;
        return time < day ? day : time >= day.AddDays(1) ? day.AddDays(1).AddSeconds(-1) : time;
    }

    /// <summary>Arrastre fuera del control: la ventana acompaña al cursor.</summary>
    private void PanIfOutside(double x)
    {
        double overflow = x < 0 ? x : x > ActualWidth ? x - ActualWidth : 0;
        if (overflow == 0 || ActualWidth <= 0) return;
        var span = Span;
        _center = (_center ?? ViewStart + TimeSpan.FromTicks(span.Ticks / 2))
            + TimeSpan.FromSeconds(overflow / ActualWidth * span.TotalSeconds);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (ActualWidth <= 0) return;
        _dragStartX = e.GetPosition(this).X;
        _dragging = false;
        _dragFrom = _dragTo = null;
        _scrubTime = null;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragStartX < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        double x = e.GetPosition(this).X;
        if (!_dragging && Math.Abs(x - _dragStartX) < DragThreshold) return;
        _dragging = true;

        if (SelectionMode)
        {
            _dragFrom = TimeAt(Math.Min(_dragStartX, x));
            _dragTo = TimeAt(Math.Max(_dragStartX, x));
        }
        else
        {
            PanIfOutside(x);
            _scrubTime = TimeAt(x);
            Scrubbing?.Invoke(_scrubTime);
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragStartX < 0) return;
        ReleaseMouseCapture();
        double startX = _dragStartX;
        _dragStartX = -1;

        if (_dragging && SelectionMode && _dragFrom is { } from && _dragTo is { } to)
        {
            _dragFrom = _dragTo = null;
            SelectionChanged?.Invoke(from, to);
            InvalidateVisual();
            return;
        }

        var target = _scrubTime ?? TimeAt(startX);
        _scrubTime = null;
        _dragging = false;
        Scrubbing?.Invoke(null);
        InvalidateVisual();
        SeekRequested?.Invoke(target);
    }

    /// <summary>Clic derecho: borrar el tramo marcado.</summary>
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        _dragFrom = _dragTo = null;
        SelectionChanged?.Invoke(null, null);
        InvalidateVisual();
    }

    /// <summary>Rueda: acercar/alejar la línea de tiempo alrededor del cursor.</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        SetZoom(_zoom + (e.Delta > 0 ? 1 : -1), anchor: TimeAt(e.GetPosition(this).X));
        e.Handled = true;
    }
}
