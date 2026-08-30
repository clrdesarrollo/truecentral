using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Módulo Reproducción: árbol de canales, grilla de cuadros sincronizados,
/// línea de tiempo del día y controles. El DataContext es el MainViewModel de
/// la ventana (heredado); el estado del módulo vive en su propiedad Playback.
/// </summary>
public partial class PlaybackView : UserControl
{
    public PlaybackView()
    {
        InitializeComponent();
        Timeline.SeekRequested += async time => await Vm.Playback.SeekToAsync(time);
        Timeline.SelectionChanged += (from, to) => Vm.Playback.SetSelection(from, to);
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    /// <summary>Doble clic: reemplaza el canal; con Ctrl lo suma a la reproducción sincronizada.</summary>
    private async void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaybackTree.SelectedItem is ChannelNode node)
            await Vm.Playback.SelectChannelAsync(node, add: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    /// <summary>Clic en un cuadro: pasa a ser el cuadro con foco (audio y exportación).</summary>
    private void OnCellClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlaybackCellViewModel cell })
            Vm.Playback.SelectedCell = cell;
    }
}
