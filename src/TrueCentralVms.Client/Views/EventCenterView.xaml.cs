using System.Windows.Controls;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Centro de eventos. Todo el comportamiento vive en
/// <see cref="ViewModels.EventCenterViewModel"/>: esta vista solo presenta, y
/// por eso sirve igual dentro del shell que en la ventana independiente.
/// </summary>
public partial class EventCenterView : UserControl
{
    public EventCenterView() => InitializeComponent();
}
