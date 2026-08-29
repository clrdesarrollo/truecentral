using System.Windows;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Ventana de "cargando" para la apertura masiva: bloquea la interfaz
/// (deshabilita a su dueño) y muestra progreso indeterminado hasta Cerrar.
/// La barra anima de verdad porque el trabajo pesado cede ciclos al
/// dispatcher en vez de congelar el hilo de UI.
/// </summary>
public partial class LoadingWindow : Window
{
    private Window? _owner;

    private LoadingWindow() => InitializeComponent();

    /// <summary>Abre el aviso centrado sobre el dueño y lo deshabilita.</summary>
    public static LoadingWindow Open(Window owner, string message)
    {
        var window = new LoadingWindow { Owner = owner, _owner = owner };
        window.MessageText.Text = message;
        owner.IsEnabled = false;
        window.Show();
        return window;
    }

    /// <summary>Cierra el aviso y rehabilita al dueño.</summary>
    public void Finish()
    {
        if (_owner is not null) _owner.IsEnabled = true;
        _owner = null;
        Close();
    }
}
