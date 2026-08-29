using System.Windows;
using System.Windows.Input;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Ventana de "cargando" para la apertura masiva: bloquea la interfaz del
/// dueño y muestra progreso indeterminado hasta Finish. El bloqueo es por
/// hit-testing + teclado y NO con IsEnabled=false: deshabilitar la ventana
/// haría que los controles se pinten con los colores de sistema
/// deshabilitados (fondos blancos) y el tema oscuro se "lava".
/// </summary>
public partial class LoadingWindow : Window
{
    private Window? _owner;
    private readonly KeyEventHandler _keyBlocker = (_, e) => e.Handled = true;

    private LoadingWindow() => InitializeComponent();

    /// <summary>Abre el aviso centrado sobre el dueño y bloquea su entrada.</summary>
    public static LoadingWindow Open(Window owner, string message)
    {
        var window = new LoadingWindow { Owner = owner, _owner = owner };
        window.MessageText.Text = message;
        owner.IsHitTestVisible = false;                  // sin clics…
        owner.PreviewKeyDown += window._keyBlocker;      // …ni teclado
        window.Show();
        return window;
    }

    /// <summary>Cierra el aviso y devuelve la entrada al dueño.</summary>
    public void Finish()
    {
        if (_owner is not null)
        {
            _owner.IsHitTestVisible = true;
            _owner.PreviewKeyDown -= _keyBlocker;
        }
        _owner = null;
        Close();
    }
}
