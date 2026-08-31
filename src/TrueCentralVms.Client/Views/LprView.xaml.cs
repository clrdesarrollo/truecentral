using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using TrueCentralVms.Client.ViewModels;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Pantalla del módulo Reconocimiento de patentes. El código de esta vista solo
/// resuelve lo que la marcación declarativa no puede: el recuadro de la placa
/// sobre la escena (depende del tamaño REAL con que se pintó la imagen, que
/// solo se conoce después del layout) y los interruptores de las fuentes.
/// </summary>
public partial class LprView : UserControl
{
    private LprViewModel? _model;
    private PlateEventViewModel? _watched;

    public LprView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_model is not null)
            _model.PropertyChanged -= OnModelPropertyChanged;
        Detach();

        _model = DataContext as LprViewModel;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelPropertyChanged;
            Watch(_model.Selected);
        }
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LprViewModel.Selected))
            Watch(_model?.Selected);
    }

    /// <summary>Sigue al reconocimiento en pantalla: su escena llega asíncrona.</summary>
    private void Watch(PlateEventViewModel? item)
    {
        Detach();
        _watched = item;
        if (_watched is not null)
            _watched.PropertyChanged += OnSelectedItemPropertyChanged;
        QueueOverlayUpdate();
    }

    private void Detach()
    {
        if (_watched is null) return;
        _watched.PropertyChanged -= OnSelectedItemPropertyChanged;
        _watched = null;
    }

    private void OnSelectedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlateEventViewModel.SceneImage))
            QueueOverlayUpdate();
    }

    // ------------------------------------------------------------------
    // Recuadro de la placa sobre la escena
    // ------------------------------------------------------------------

    private void OnSceneSizeChanged(object sender, SizeChangedEventArgs e) => UpdateOverlay();

    /// <summary>Redibuja el recuadro DESPUÉS del layout: antes, la imagen todavía
    /// no tiene el tamaño con que se va a pintar.</summary>
    private void QueueOverlayUpdate() =>
        Dispatcher.InvokeAsync(UpdateOverlay, System.Windows.Threading.DispatcherPriority.Loaded);

    private void UpdateOverlay()
    {
        SceneOverlay.Children.Clear();
        if (_watched is not { HasPlateBox: true } item || item.SceneImage is not BitmapSource scene)
            return;

        double width = SceneImageElement.ActualWidth;
        double height = SceneImageElement.ActualHeight;
        if (width <= 0 || height <= 0) return;
        if (ToFraction(item.PlateBox, scene.PixelWidth, scene.PixelHeight) is not { } box) return;

        double left = box.X * width;
        double top = box.Y * height;
        double boxWidth = Math.Max(3, box.W * width);
        double boxHeight = Math.Max(3, box.H * height);

        // Dos trazos: uno oscuro por fuera y el verde por dentro. La placa casi
        // siempre queda sobre chapa clara, donde un verde solo se pierde.
        Add(new Rectangle
        {
            Width = boxWidth + 3,
            Height = boxHeight + 3,
            Stroke = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
            StrokeThickness = 3,
            RadiusX = 3,
            RadiusY = 3,
        }, left - 1.5, top - 1.5);

        Add(new Rectangle
        {
            Width = boxWidth,
            Height = boxHeight,
            Stroke = (Brush)FindResource("OkBrush"),
            StrokeThickness = 2,
            RadiusX = 2,
            RadiusY = 2,
            // Un velo tenue adentro: marca la placa sin taparla.
            Fill = new SolidColorBrush(Color.FromArgb(30, 34, 197, 94)),
        }, left, top);

        void Add(Rectangle shape, double x, double y)
        {
            Canvas.SetLeft(shape, x);
            Canvas.SetTop(shape, y);
            SceneOverlay.Children.Add(shape);
        }
    }

    /// <summary>
    /// Traduce a fracción 0..1 de la foto el recuadro que informó el equipo.
    ///
    /// El SDK declara NET_VCA_RECT en 0..1 relativo a la imagen, pero las
    /// cámaras ITS de este parque entregan PÍXELES DIVIDIDOS POR 1000: sobre la
    /// escena de 1920x1104 la placa de KPYS66 llegó como (0,898 · 0,623 ·
    /// 0,049 · 0,027) y calza exactamente en el píxel (898, 623) midiendo
    /// 49x27. De ahí que existan lecturas con X mayor que 1 (1,217 = píxel
    /// 1217), imposible en una fracción, que es lo que mandaba el recuadro
    /// lejos de la placa.
    ///
    /// La convención se decide por evento:
    ///  1. algún borde pasa de 1 → no puede ser fracción: son píxeles/1000;
    ///  2. los cuatro valores son milésimas exactas y el recuadro en píxeles
    ///     cabe en la foto → píxeles/1000 (una fracción de verdad sale de
    ///     dividir píxeles por el tamaño y no cae en milésimas justas);
    ///  3. cualquier otro caso → fracción, como dice el SDK.
    ///
    /// Al recuadro ya traducido se le suma aire por lado: el equipo encierra
    /// la banda de los caracteres y el marco blanco de la placa queda afuera,
    /// sobre todo por abajo (de ahí que el margen vertical sea mayor). Es
    /// proporcional al propio recuadro porque la placa se ve más chica cuanto
    /// más lejos pasa el vehículo.
    /// </summary>
    private static (double X, double Y, double W, double H)? ToFraction(
        (double X, double Y, double W, double H) box, double pixelWidth, double pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || box.W <= 0 || box.H <= 0)
            return null;

        bool pixels = box.X + box.W > 1.001 || box.Y + box.H > 1.001 ||
                      (IsThousandths(box) &&
                       box.X + box.W <= pixelWidth / 1000.0 &&
                       box.Y + box.H <= pixelHeight / 1000.0);

        double scaleX = pixels ? 1000.0 / pixelWidth : 1.0;
        double scaleY = pixels ? 1000.0 / pixelHeight : 1.0;

        double w = box.W * scaleX;
        double h = box.H * scaleY;
        double x = box.X * scaleX - w * BoxMarginX;
        double y = box.Y * scaleY - h * BoxMarginY;
        w *= 1 + BoxMarginX * 2;
        h *= 1 + BoxMarginY * 2;

        // Recortar contra la foto sin correr el recuadro: lo que se sale por
        // arriba o por la izquierda se le descuenta al ancho, no se desplaza.
        double left = Math.Clamp(x, 0, 1);
        double top = Math.Clamp(y, 0, 1);
        w = Math.Clamp(w - (left - x), 0, 1 - left);
        h = Math.Clamp(h - (top - y), 0, 1 - top);
        return w > 0 && h > 0 ? (left, top, w, h) : null;
    }

    /// <summary>Aire que se le suma al recuadro del equipo, por lado, en veces su tamaño.</summary>
    private const double BoxMarginX = 0.30;
    private const double BoxMarginY = 0.45;

    /// <summary>¿Los cuatro valores son milésimas exactas? (viajan como float de 32 bits).</summary>
    private static bool IsThousandths((double X, double Y, double W, double H) box) =>
        IsThousandth(box.X) && IsThousandth(box.Y) && IsThousandth(box.W) && IsThousandth(box.H);

    private static bool IsThousandth(double value)
    {
        double scaled = value * 1000.0;
        return Math.Abs(scaled - Math.Round(scaled)) < 0.01;
    }

    // ------------------------------------------------------------------
    // Interacción
    // ------------------------------------------------------------------

    private void OnPlateFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _model is null) return;
        e.Handled = true;
        _model.SearchCommand.Execute(null);
    }

    /// <summary>
    /// Encender/apagar una fuente. Se usa Click y no Checked/Unchecked a
    /// propósito: esos también disparan cuando la lista se recarga y el enlace
    /// vuelve a fijar el estado, lo que mandaría escrituras fantasma al servidor.
    /// </summary>
    private void OnSourceToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: AnprSourceDto source } toggle || _model is null) return;
        bool enabled = toggle.IsChecked == true;
        _ = _model.SetSourceAsync(source, enabled);
    }
}
