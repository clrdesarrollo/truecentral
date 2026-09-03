using System.Windows.Controls;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Pantalla del módulo Paneles de alarma. Toda la lógica vive en
/// <see cref="ViewModels.AlarmsViewModel"/>: las órdenes por área y zona son
/// comandos con parámetro enlazados desde las plantillas.
/// </summary>
public partial class AlarmsView : UserControl
{
    public AlarmsView() => InitializeComponent();
}
