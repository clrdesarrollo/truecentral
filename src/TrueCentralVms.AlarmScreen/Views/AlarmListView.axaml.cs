using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace TrueCentralVms.AlarmScreen.Views;

public partial class AlarmListView : UserControl
{
    /// <summary>
    /// Cuánto hay que mover el dedo para que deje de ser un toque y pase a ser un arrastre. Por
    /// debajo de esto, un dedo nunca queda del todo quieto y elegir una alarma sería una lotería.
    /// </summary>
    private const double UmbralArrastre = 12;

    /// <summary>
    /// Con qué velocidad hay que soltar (px/s) para que la lista siga sola. Por debajo fue un
    /// arrastre acompañado, y la lista tiene que quedarse donde el guardia la dejó.
    /// </summary>
    private const double VelocidadParaSeguir = 250;

    /// <summary>Velocidad por debajo de la cual la inercia ya no se nota y se corta (px/s).</summary>
    private const double VelocidadDeReposo = 30;

    /// <summary>Tope de velocidad inicial (px/s): un manotazo no debe mandar la lista al fondo.</summary>
    private const double VelocidadTope = 6000;

    /// <summary>
    /// Fracción de la velocidad que sobrevive tras un segundo de inercia. Calcado del freno de
    /// los teléfonos (0.998 por milisegundo): es la sensación que el usuario ya conoce.
    /// </summary>
    private const double FrenoPorSegundo = 0.135;

    /// <summary>
    /// Cuántos segundos de recorrido reciente cuentan para medir la velocidad al soltar. Solo el
    /// final del gesto: si el dedo frenó antes de levantar, la lista no tiene que salir volando.
    /// </summary>
    private const double VentanaDeMuestras = 0.12;

    private ScrollViewer? _scroll;
    private ListBoxItem? _presionado;
    private Point _origen;
    private double _desplazamientoInicial;
    private bool _arrastrando;
    private bool _capturado;
    private bool _atajado;

    private readonly Stopwatch _reloj = Stopwatch.StartNew();
    private readonly List<(double Instante, double Y)> _muestras = [];
    private readonly DispatcherTimer _inercia;
    private double _velocidad;
    private double _ultimoTic;

    public AlarmListView()
    {
        InitializeComponent();

        _inercia = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _inercia.Tick += AlAvanzarInercia;

        // En fase de túnel y no de burbujeo: hay que llegar antes que el ListBox, que selecciona
        // en cuanto se presiona. Si se le deja actuar, arrastrar la lista para desplazarla iría
        // cambiando de alarma por el camino, que en una pantalla táctil es exactamente el gesto
        // que uno hace para leer el historial.
        AddHandler(PointerPressedEvent, AlPresionar, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, AlMover, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, AlSoltar, RoutingStrategies.Tunnel);

        // Evento directo, sin túnel: registrado en túnel no se dispara nunca. Si otro control
        // roba la captura a mitad del gesto (el reconocedor táctil del tema, por ejemplo), se
        // suelta todo y que el gesto lo termine quien lo tomó — la lista se mueve igual.
        PointerCaptureLost += (_, _) => Reiniciar();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void AlPresionar(object? sender, PointerPressedEventArgs e)
    {
        _scroll ??= this.FindDescendantOfType<ScrollViewer>();
        if (_scroll is null) return;

        // La barra lateral se maneja sola; capturarle el puntero la dejaría muerta.
        if ((e.Source as Visual)?.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // Tocar una lista que viene deslizándose la frena, como en el teléfono. Ese toque ya
        // cumplió su papel: al soltar no debe además elegir la alarma que quedó bajo el dedo.
        _atajado = _inercia.IsEnabled;
        DetenerInercia();

        // Se captura también en los huecos entre tarjetas: para el dedo toda la lista es la
        // misma superficie, y que el gesto dependa de en qué milímetro cayó es una tómbola.
        _presionado = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        _origen = e.GetPosition(this);
        _desplazamientoInicial = _scroll.Offset.Y;
        _arrastrando = false;
        _muestras.Clear();
        Muestrear(_origen.Y);

        // se retiene el evento: el ListBox no llega a seleccionar, y la selección la hacemos
        // nosotros al soltar solo si resultó ser un toque
        e.Pointer.Capture(this);
        _capturado = true;
        e.Handled = true;
    }

    private void AlMover(object? sender, PointerEventArgs e)
    {
        if (!_capturado || _scroll is null) return;

        var y = e.GetPosition(this).Y;
        Muestrear(y);

        var recorrido = _origen.Y - y;
        if (!_arrastrando && Math.Abs(recorrido) < UmbralArrastre) return;

        _arrastrando = true;
        _scroll.Offset = new Vector(_scroll.Offset.X, Acotar(_desplazamientoInicial + recorrido));
        e.Handled = true;
    }

    private void AlSoltar(object? sender, PointerReleasedEventArgs e)
    {
        if (!_capturado) return;

        if (_arrastrando)
        {
            Muestrear(e.GetPosition(this).Y);
            LanzarInercia();
        }
        // un toque que no llegó a ser arrastre ni frenó la lista sí elige la alarma
        else if (!_atajado && _presionado?.DataContext is { } elemento &&
                 this.FindDescendantOfType<ListBox>() is { } lista)
        {
            lista.SelectedItem = elemento;
        }

        e.Pointer.Capture(null);
        e.Handled = true;
        Reiniciar();
    }

    private void Reiniciar()
    {
        _capturado = false;
        _arrastrando = false;
        _presionado = null;
        _atajado = false;
    }

    private void Muestrear(double y)
    {
        var ahora = _reloj.Elapsed.TotalSeconds;
        _muestras.Add((ahora, y));
        _muestras.RemoveAll(m => ahora - m.Instante > VentanaDeMuestras);
    }

    private void LanzarInercia()
    {
        if (_muestras.Count < 2) return;

        var (instanteInicial, yInicial) = _muestras[0];
        var (instanteFinal, yFinal) = _muestras[^1];
        var lapso = instanteFinal - instanteInicial;
        if (lapso < 0.01) return;

        // el desplazamiento corre al revés del dedo: arrastrar hacia arriba avanza la lista
        var velocidad = (yInicial - yFinal) / lapso;
        if (Math.Abs(velocidad) < VelocidadParaSeguir) return;

        _velocidad = Math.Clamp(velocidad, -VelocidadTope, VelocidadTope);
        _ultimoTic = _reloj.Elapsed.TotalSeconds;
        _inercia.Start();
    }

    private void AlAvanzarInercia(object? sender, EventArgs e)
    {
        if (_scroll is null)
        {
            DetenerInercia();
            return;
        }

        var ahora = _reloj.Elapsed.TotalSeconds;
        var lapso = ahora - _ultimoTic;
        _ultimoTic = ahora;

        var maximo = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        var destino = Math.Clamp(_scroll.Offset.Y + _velocidad * lapso, 0, maximo);
        _scroll.Offset = new Vector(_scroll.Offset.X, destino);
        _velocidad *= Math.Pow(FrenoPorSegundo, lapso);

        var chocoConElBorde = (destino <= 0 && _velocidad < 0) || (destino >= maximo && _velocidad > 0);
        if (chocoConElBorde || Math.Abs(_velocidad) < VelocidadDeReposo) DetenerInercia();
    }

    private void DetenerInercia()
    {
        _inercia.Stop();
        _velocidad = 0;
    }

    private double Acotar(double destino)
    {
        var maximo = Math.Max(0, _scroll!.Extent.Height - _scroll.Viewport.Height);
        return Math.Clamp(destino, 0, maximo);
    }

    /// <summary>
    /// Trae a la vista la alarma seleccionada. Las nuevas entran arriba y se seleccionan solas,
    /// así que esto deja la lista donde el guardia necesita mirar sin que tenga que arrastrarla.
    /// Se aplaza un ciclo porque el contenedor del elemento recién insertado todavía no existe.
    /// </summary>
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list || list.SelectedItem is not { } selected) return;

        // Una alarma nueva manda: si la lista venía deslizándose por inercia, se frena aquí
        // para que el ScrollIntoView no tenga que pelear contra el impulso.
        DetenerInercia();

        Dispatcher.UIThread.Post(() =>
        {
            try { list.ScrollIntoView(selected); }
            catch { /* la lista puede haberse recargado entremedio */ }
        }, DispatcherPriority.Background);
    }
}
