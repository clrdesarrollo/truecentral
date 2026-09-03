using System.Windows;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Ventana independiente del Centro de eventos ("sacar la pestaña"). Solo
/// aporta el marco: el contenido es la misma vista y el mismo ViewModel que
/// dentro del shell, así que la lista no se duplica ni se desincroniza.
/// Cerrarla devuelve el módulo al panel principal.
/// </summary>
public partial class EventCenterWindow : Window
{
    public EventCenterWindow(EventCenterViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await viewModel.LoadAsync();
        StateChanged += (_, _) =>
            MaxGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
