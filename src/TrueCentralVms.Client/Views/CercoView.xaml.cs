using System.Windows.Controls;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Pantalla del módulo Cerco eléctrico. Toda la lógica vive en
/// <see cref="ViewModels.CercoViewModel"/>: armar, desarmar y silenciar son
/// comandos con el panel como parámetro, enlazados desde la plantilla.
/// </summary>
public partial class CercoView : UserControl
{
    public CercoView() => InitializeComponent();
}
