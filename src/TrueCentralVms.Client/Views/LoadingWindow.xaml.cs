using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Ventana de "cargando" para la apertura masiva: bloquea la interfaz del
/// dueño y muestra progreso indeterminado hasta Finish. El bloqueo es por
/// hit-testing + teclado y NO con IsEnabled=false: deshabilitar la ventana
/// haría que los controles se pinten con los colores de sistema
/// deshabilitados (fondos blancos) y el tema oscuro se "lava".
///
/// Como bloquea la entrada, el aviso NUNCA debe poder quedarse puesto: si el
/// trabajo que lo abrió se cuelga, la aplicación entera queda inutilizable
/// para el operador. Por eso tiene dos válvulas de escape propias — Esc y un
/// tope de tiempo — que devuelven el control aunque nadie llame a Finish.
/// </summary>
public partial class LoadingWindow : Window
{
    /// <summary>Tope de vida del aviso: pasado esto se cierra solo.</summary>
    private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(60);

    private Window? _owner;
    private DispatcherTimer? _watchdog;
    private bool _finished;
    private readonly KeyEventHandler _keyBlocker;

    private LoadingWindow()
    {
        InitializeComponent();
        // Se traga el teclado del dueño mientras dura… salvo Esc, que es la
        // salida de emergencia del operador.
        _keyBlocker = (_, e) =>
        {
            if (e.Key == Key.Escape) Finish();
            e.Handled = true;
        };
    }

    /// <summary>Abre el aviso centrado sobre el dueño y bloquea su entrada.</summary>
    public static LoadingWindow Open(Window owner, string message)
    {
        var window = new LoadingWindow { Owner = owner, _owner = owner };
        window.MessageText.Text = message;
        owner.IsHitTestVisible = false;                  // sin clics…
        owner.PreviewKeyDown += window._keyBlocker;      // …ni teclado
        window._watchdog = new DispatcherTimer { Interval = MaxDuration };
        window._watchdog.Tick += (_, _) => window.Finish();
        window._watchdog.Start();
        window.Show();
        return window;
    }

    /// <summary>Refresca el mensaje mientras el trabajo avanza.</summary>
    public void Update(string message)
    {
        if (!_finished) MessageText.Text = message;
    }

    /// <summary>
    /// Cierra el aviso y devuelve la entrada al dueño. Idempotente: lo llaman
    /// tanto el trabajo que lo abrió como sus válvulas de escape.
    /// </summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        _watchdog?.Stop();
        _watchdog = null;
        if (_owner is not null)
        {
            _owner.IsHitTestVisible = true;
            _owner.PreviewKeyDown -= _keyBlocker;
        }
        _owner = null;
        // El cursor de espera lo pone quien abre el aviso: si el control vuelve
        // por una válvula de escape, tiene que volver también el puntero normal.
        Mouse.OverrideCursor = null;
        Close();
    }
}
