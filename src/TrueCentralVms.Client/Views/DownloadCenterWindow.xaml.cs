using System.Windows;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Ventana del Centro de descargas. NO es modal y no es dueña de nada: la cola
/// vive en <see cref="DownloadCenterViewModel"/> (en el shell), así que cerrar
/// esta ventana no detiene ninguna exportación.
/// </summary>
public partial class DownloadCenterWindow : Window
{
    public DownloadCenterWindow(DownloadCenterViewModel downloads)
    {
        InitializeComponent();
        DataContext = downloads;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
