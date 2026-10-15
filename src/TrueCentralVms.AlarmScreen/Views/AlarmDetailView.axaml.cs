using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using TrueCentralVms.AlarmScreen.ViewModels;

namespace TrueCentralVms.AlarmScreen.Views;

public partial class AlarmDetailView : UserControl
{
    public AlarmDetailView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Doble clic sobre la foto: la abre a pantalla completa para mirar una patente o una cara.</summary>
    private void OnPhotoDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.TogglePhotoFullScreenCommand.Execute(null);
    }
}
