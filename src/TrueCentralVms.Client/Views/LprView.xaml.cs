using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        if (_watched is not { HasPlateBox: true } item || item.SceneImage is null)
            return;

        double width = SceneImageElement.ActualWidth;
        double height = SceneImageElement.ActualHeight;
        if (width <= 0 || height <= 0) return;

        var (x, y, w, h) = Normalize(item.PlateBox);
        var box = new Rectangle
        {
            Width = Math.Max(2, w * width),
            Height = Math.Max(2, h * height),
            Stroke = (Brush)FindResource("AccentBrush"),
            StrokeThickness = 2,
            RadiusX = 2,
            RadiusY = 2,
            // Un velo tenue adentro: marca la placa sin taparla.
            Fill = new SolidColorBrush(Color.FromArgb(28, 59, 130, 246)),
        };
        Canvas.SetLeft(box, x * width);
        Canvas.SetTop(box, y * height);
        SceneOverlay.Children.Add(box);
    }

    /// <summary>
    /// El SDK declara el recuadro en 0..1, pero algunos firmwares lo entregan
    /// en milésimas (0..1000): si los valores se salen del rango se reescalan
    /// en vez de dibujar un recuadro gigante fuera de la imagen.
    /// </summary>
    private static (double X, double Y, double W, double H) Normalize((double X, double Y, double W, double H) box)
    {
        double scale = box.X > 1 || box.Y > 1 || box.W > 1 || box.H > 1 ? 1000.0 : 1.0;
        double x = Math.Clamp(box.X / scale, 0, 1);
        double y = Math.Clamp(box.Y / scale, 0, 1);
        return (x, y, Math.Clamp(box.W / scale, 0, 1 - x), Math.Clamp(box.H / scale, 0, 1 - y));
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
