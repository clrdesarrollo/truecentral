using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using TrueCentralVms.AlarmScreen.Services;
using TrueCentralVms.AlarmScreen.ViewModels;

namespace TrueCentralVms.AlarmScreen.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // en la caseta la app va a pantalla completa y sin bordes; en el escritorio de
        // desarrollo, en ventana normal
        if (ConfigService.Load().Kiosk)
        {
            SystemDecorations = SystemDecorations.None;
            WindowState = WindowState.FullScreen;
            Cursor = new Cursor(StandardCursorType.None);
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Doble clic sobre la foto ampliada: vuelve a la pantalla normal.</summary>
    private void OnFullScreenPhotoDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ClosePhotoFullScreen();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Escape cierra primero la foto ampliada; recién después sale del modo kiosco, que
            // es lo que sirve para atender el equipo sin desconectarlo
            if (DataContext is MainViewModel { IsPhotoFullScreen: true } vm)
            {
                vm.ClosePhotoFullScreen();
                e.Handled = true;
            }
            else if (WindowState == WindowState.FullScreen)
            {
                WindowState = WindowState.Normal;
                SystemDecorations = SystemDecorations.Full;
                Cursor = Cursor.Default;
            }
        }
        base.OnKeyDown(e);
    }
}
