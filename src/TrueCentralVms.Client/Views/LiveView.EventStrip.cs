using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Cinta de eventos: clic en una ficha abre su cámara en el cuadro
/// seleccionado, doble clic abre la ventana de alarma de un aviso, y la ficha
/// se arrastra a un cuadro como una cámara de la lista (mismo soltar).
/// </summary>
public partial class LiveView
{
    private EventStripItem? _stripPressed;
    private Point _stripPressedAt;
    private bool _stripDragged;

    private void OnEventStripItemDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EventStripItem item) return;
        if (e.ClickCount == 2)
        {
            _stripPressed = null;
            Vm.EventStrip.OpenAlertCommand.Execute(item);
            e.Handled = true;
            return;
        }
        _stripPressed = item;
        _stripPressedAt = e.GetPosition(this);
        _stripDragged = false;
    }

    private void OnEventStripItemMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _stripPressed is not { Camera: { } camera } item) return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _stripPressedAt.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _stripPressedAt.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _stripDragged = true;
        _stripPressed = null;
        WebCellView.DragActive = true; // las páginas web se esconden: el cuadro recibe el arrastre
        try { DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(ChannelNode), camera), DragDropEffects.Copy); }
        finally { _grow.Cancel(); WebCellView.DragActive = false; }
    }

    private void OnEventStripItemUp(object sender, MouseButtonEventArgs e)
    {
        if (_stripDragged || _stripPressed is not { } item) return;
        _stripPressed = null;
        Vm.EventStrip.OpenCommand.Execute(item);
    }

    /// <summary>La rueda del mouse recorre la cinta de lado (es horizontal).</summary>
    private void OnEventStripWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta);
        e.Handled = true;
    }
}
