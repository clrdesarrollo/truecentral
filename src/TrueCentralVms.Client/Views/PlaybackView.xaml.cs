using System.Windows.Controls;
using System.Windows.Input;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Módulo Reproducción: árbol de canales, línea de tiempo del día y player.
/// El DataContext es el MainViewModel de la ventana (heredado); el estado del
/// módulo vive en su propiedad Playback.
/// </summary>
public partial class PlaybackView : UserControl
{
    public PlaybackView()
    {
        InitializeComponent();
        Timeline.SeekRequested += async time => await Vm.Playback.SeekToAsync(time);
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private async void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaybackTree.SelectedItem is ChannelNode node)
            await Vm.Playback.SelectChannelAsync(node);
    }
}
