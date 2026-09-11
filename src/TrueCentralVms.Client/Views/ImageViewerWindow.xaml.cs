using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Visor de fotos a pantalla completa con zoom digital. Recibe las imágenes
/// ya cargadas (las de una alerta, por ejemplo) y permite acercar con la
/// rueda sobre el punto del cursor, mover arrastrando y saltar entre fotos
/// con las flechas. No descarga nada: quien lo abre le pasa los bitmaps.
/// </summary>
public partial class ImageViewerWindow : Window
{
    private const double MinScale = 0.1;
    private const double MaxScale = 16;

    private readonly IReadOnlyList<BitmapSource?> _images;
    private readonly IReadOnlyList<string> _captions;
    private int _index;

    private Point _dragStart;
    private double _dragOffsetX, _dragOffsetY;
    private bool _dragging;
    private bool _fitted = true;
    private DateTime _lastClick = DateTime.MinValue;
    private Point _lastClickPoint;

    public ImageViewerWindow(IReadOnlyList<BitmapSource?> images, IReadOnlyList<string> captions, int start, Window? owner)
    {
        _images = images;
        _captions = captions;
        _index = Math.Clamp(start, 0, Math.Max(0, images.Count - 1));
        InitializeComponent();
        if (owner is not null && owner.IsLoaded)
        {
            try { Owner = owner; } catch (InvalidOperationException) { /* sin dueño */ }
        }
        Loaded += (_, _) => { Show(_index); Focus(); };
        SizeChanged += (_, _) => { if (_fitted) Fit(); };
    }

    /// <summary>Abre el visor con las fotos dadas, partiendo por la de <paramref name="start"/>.</summary>
    public static void Open(IReadOnlyList<BitmapSource?> images, IReadOnlyList<string> captions, int start, Window? owner)
    {
        if (images.Count == 0) return;
        new ImageViewerWindow(images, captions, start, owner).Show();
    }

    // ------------------------------------------------------------------
    // Fotos
    // ------------------------------------------------------------------

    private void Show(int index)
    {
        if (_images.Count == 0) return;
        _index = Math.Clamp(index, 0, _images.Count - 1);
        Picture.Source = _images[_index];
        TitleText.Text = _index < _captions.Count ? _captions[_index] : "Foto";
        CounterText.Text = _images.Count > 1 ? $"{_index + 1} / {_images.Count}" : "";
        PrevButton.Visibility = NextButton.Visibility = _images.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _fitted = true;
        Fit();
    }

    private void OnPrev(object sender, RoutedEventArgs e) => Show(_index - 1);
    private void OnNext(object sender, RoutedEventArgs e) => Show(_index + 1);
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------
    // Zoom y desplazamiento
    // ------------------------------------------------------------------

    private double ImageWidth => Picture.Source?.Width ?? 0;
    private double ImageHeight => Picture.Source?.Height ?? 0;

    /// <summary>Encaja la foto completa en la pantalla, centrada.</summary>
    private void Fit()
    {
        if (ImageWidth <= 0 || Viewport.ActualWidth <= 0) return;
        double scale = Math.Min(Viewport.ActualWidth / ImageWidth, Viewport.ActualHeight / ImageHeight);
        scale = Math.Min(scale, 1);   // una foto chica no se estira: se ve nítida
        Apply(scale, (Viewport.ActualWidth - ImageWidth * scale) / 2, (Viewport.ActualHeight - ImageHeight * scale) / 2);
        _fitted = true;
    }

    private void ActualSize()
    {
        if (ImageWidth <= 0) return;
        ZoomAt(1, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));
    }

    /// <summary>Cambia la escala manteniendo quieto el punto de la pantalla <paramref name="anchor"/>.</summary>
    private void ZoomAt(double newScale, Point anchor)
    {
        newScale = Math.Clamp(newScale, MinScale, MaxScale);
        double old = Scale.ScaleX;
        if (old <= 0) old = 1;
        double x = anchor.X - (anchor.X - Offset.X) * (newScale / old);
        double y = anchor.Y - (anchor.Y - Offset.Y) * (newScale / old);
        Apply(newScale, x, y);
        _fitted = false;
    }

    private void Apply(double scale, double x, double y)
    {
        Scale.ScaleX = Scale.ScaleY = scale;
        Offset.X = x;
        Offset.Y = y;
        Clamp();
        ZoomText.Text = $"{scale * 100:0}%";
    }

    /// <summary>La foto no se pierde fuera de la pantalla: si es más chica que el visor queda centrada, si es más grande siempre lo cubre.</summary>
    private void Clamp()
    {
        double w = ImageWidth * Scale.ScaleX, h = ImageHeight * Scale.ScaleY;
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        Offset.X = w <= vw ? (vw - w) / 2 : Math.Clamp(Offset.X, vw - w, 0);
        Offset.Y = h <= vh ? (vh - h) / 2 : Math.Clamp(Offset.Y, vh - h, 0);
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.25 : 1 / 1.25;
        ZoomAt(Scale.ScaleX * factor, e.GetPosition(Viewport));
        e.Handled = true;
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) =>
        ZoomAt(Scale.ScaleX * 1.25, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));

    private void OnZoomOut(object sender, RoutedEventArgs e) =>
        ZoomAt(Scale.ScaleX / 1.25, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));

    private void OnFit(object sender, RoutedEventArgs e) => Fit();
    private void OnActualSize(object sender, RoutedEventArgs e) => ActualSize();

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(Viewport);
        // Doble clic: alterna entre ajustar y tamaño real sobre el punto pulsado.
        if ((DateTime.UtcNow - _lastClick).TotalMilliseconds < 400 && (point - _lastClickPoint).Length < 8)
        {
            _lastClick = DateTime.MinValue;
            if (_fitted) ZoomAt(Math.Max(1, Scale.ScaleX * 2), point);
            else Fit();
            return;
        }
        _lastClick = DateTime.UtcNow;
        _lastClickPoint = point;

        _dragging = true;
        _dragStart = point;
        _dragOffsetX = Offset.X;
        _dragOffsetY = Offset.Y;
        Viewport.CaptureMouse();
        Viewport.Cursor = Cursors.SizeAll;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var point = e.GetPosition(Viewport);
        Offset.X = _dragOffsetX + (point.X - _dragStart.X);
        Offset.Y = _dragOffsetY + (point.Y - _dragStart.Y);
        Clamp();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Viewport.ReleaseMouseCapture();
        Viewport.Cursor = Cursors.Hand;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Left: Show(_index - 1); break;
            case Key.Right: Show(_index + 1); break;
            case Key.Add or Key.OemPlus: OnZoomIn(sender, e); break;
            case Key.Subtract or Key.OemMinus: OnZoomOut(sender, e); break;
            case Key.D0 or Key.NumPad0: Fit(); break;
            case Key.D1 or Key.NumPad1: ActualSize(); break;
            default: return;
        }
        e.Handled = true;
    }
}
