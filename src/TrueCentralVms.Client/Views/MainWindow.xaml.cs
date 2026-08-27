using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += async (_, _) => await _vm.LoadTreeAsync();
        Closed += (_, _) => _vm.Shutdown();
    }

    /// <summary>Doble clic en un canal del árbol: abrirlo en la grilla.</summary>
    private async void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeviceTree.SelectedItem is ChannelNode node)
            await _vm.OpenChannelAsync(node);
    }

    /// <summary>Clic en la barra del cuadro (o en el hueco vacío): seleccionarlo.</summary>
    private void OnCellClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VideoCellViewModel cell })
            SelectCell(cell);
    }

    /// <summary>
    /// Clic sobre el video en reproducción: el Border transparente vive en la
    /// ventana "Overlay" de FlyleafLib (árbol visual separado del nuestro), así
    /// que su DataContext no es el VideoCellViewModel sino el propio
    /// FlyleafHost — de ahí se recupera vía HostDataContext (ver XAML).
    /// </summary>
    private void OnVideoOverlayClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FlyleafLib.Controls.WPF.FlyleafHost host } &&
            host.HostDataContext is VideoCellViewModel cell)
            SelectCell(cell);
    }

    private void SelectCell(VideoCellViewModel cell) => _vm.SelectedCell = _vm.SelectedCell == cell ? null : cell;

    // ------------------------------------------------------------------
    // PTZ: movimiento continuo — presionar inicia, soltar (o salir del
    // botón con el mouse presionado) detiene.
    // ------------------------------------------------------------------
    private string? _activePtzCommand;

    private void OnPtzPress(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string command } || command.Length == 0) return;
        _activePtzCommand = command;
        _ = _vm.PtzAsync(Enum.Parse<TrueCentralVms.Core.Drivers.PtzCommand>(command), stop: false);
    }

    private void OnPtzRelease(object sender, MouseButtonEventArgs e) => StopPtz(sender);

    private void OnPtzLeave(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            StopPtz(sender);
    }

    private void StopPtz(object sender)
    {
        if (sender is not FrameworkElement { Tag: string command } || command.Length == 0) return;
        if (_activePtzCommand != command) return;
        _activePtzCommand = null;
        _ = _vm.PtzAsync(Enum.Parse<TrueCentralVms.Core.Drivers.PtzCommand>(command), stop: true);
    }
}
