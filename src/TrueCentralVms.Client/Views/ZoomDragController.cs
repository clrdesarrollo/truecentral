using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Modo zoom digital: con el modo encendido el puntero pasa a lupa y arrastrar
/// sobre el video dibuja el recuadro del área a acercar; al soltar, esa área
/// pasa a ocupar todo el cuadro. Clic derecho vuelve a 1×.
///
/// Vive aquí y no en cada vista porque la Vista en Vivo y la Reproducción
/// pintan el video igual: en ventanas propias de Flyleaf superpuestas al árbol
/// visual, así que el mouse hay que tomarlo a nivel de esas ventanas.
/// </summary>
internal sealed class ZoomDragController
{
    private readonly ZoomRubberBand _band = new();
    private readonly List<Window> _windows = [];

    private Window? _dragWindow;
    private IZoomTarget? _dragTarget;
    private Point _origin;

    public bool IsEnabled { get; private set; }

    /// <summary>Enciende o apaga el modo (cambia el puntero de todas las ventanas de video).</summary>
    public void SetEnabled(bool enabled)
    {
        IsEnabled = enabled;
        if (!enabled) CancelDrag();
        // El puntero se decide en QueryCursor (ver Attach), que solo se
        // consulta al moverse: sin este empujón, encender o apagar el modo no
        // se notaría hasta mover el mouse.
        Mouse.UpdateCursor();
    }

    /// <summary>
    /// Engancha una ventana de video. <paramref name="resolve"/> devuelve el
    /// cuadro que está mostrando esa ventana en este momento (cambia cuando el
    /// operador reasigna canales, por eso se consulta en cada gesto).
    /// </summary>
    public void Attach(Window window, Func<IZoomTarget?> resolve)
    {
        if (_windows.Contains(window)) return;
        _windows.Add(window);

        // El puntero se responde a la CONSULTA de WPF en vez de asignarse a la
        // ventana. Asignándolo, Flyleaf lo pisaba con el suyo al reentrar al
        // video (maneja el cursor por actividad) y la lupa se perdía para
        // siempre en cuanto el mouse salía del cuadro una vez.
        window.AddHandler(UIElement.QueryCursorEvent, new QueryCursorEventHandler((_, e) =>
        {
            if (!IsEnabled) return;
            e.Cursor = MagnifierCursor.Instance;
            e.Handled = true;
        }), handledEventsToo: true);

        window.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (!IsEnabled || resolve() is not { CanDigitalZoom: true } target) return;
            _dragWindow = window;
            _dragTarget = target;
            _origin = e.GetPosition(window);
            window.CaptureMouse();
            e.Handled = true;
        }), handledEventsToo: true);

        window.AddHandler(UIElement.MouseMoveEvent, new MouseEventHandler((_, e) =>
        {
            if (!ReferenceEquals(_dragWindow, window)) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                // El botón se soltó fuera del alcance de la ventana.
                CancelDrag();
                return;
            }
            _band.Update(window, _origin, e.GetPosition(window));
        }), handledEventsToo: true);

        window.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (!ReferenceEquals(_dragWindow, window)) return;
            var target = _dragTarget;
            var area = new Rect(_origin, e.GetPosition(window));
            CancelDrag();
            // Un clic sin arrastre no cambia nada: el propio zoom descarta las
            // marcas diminutas, así no hay saltos accidentales a 20×.
            target?.DigitalZoomToArea(ToPhysicalPixels(window, area));
            e.Handled = true;
        }), handledEventsToo: true);

        // Rueda con el modo activo: acerca/aleja sobre el puntero, sin marcar área.
        window.AddHandler(UIElement.MouseWheelEvent, new MouseWheelEventHandler((_, e) =>
        {
            if (!IsEnabled || resolve() is not { CanDigitalZoom: true } target) return;
            var point = e.GetPosition(window);
            var dpi = VisualTreeHelper.GetDpi(window);
            target.DigitalZoomStepAt(new Point(point.X * dpi.DpiScaleX, point.Y * dpi.DpiScaleY), e.Delta > 0);
            e.Handled = true;
        }), handledEventsToo: true);

        window.AddHandler(UIElement.MouseRightButtonUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (!IsEnabled || resolve() is not { } target) return;
            target.ResetDigitalZoom();
            e.Handled = true;
        }), handledEventsToo: true);
    }

    /// <summary>
    /// El mouse de WPF va en unidades independientes del dispositivo y el
    /// viewport de Flyleaf en píxeles reales: sin esta conversión el zoom
    /// quedaría corrido en pantallas con escalado (125 %, 150 %…).
    /// </summary>
    private static Rect ToPhysicalPixels(Window window, Rect area)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        return new Rect(area.X * dpi.DpiScaleX, area.Y * dpi.DpiScaleY,
            area.Width * dpi.DpiScaleX, area.Height * dpi.DpiScaleY);
    }

    private void CancelDrag()
    {
        _band.Hide();
        _dragWindow?.ReleaseMouseCapture();
        _dragWindow = null;
        _dragTarget = null;
    }
}
