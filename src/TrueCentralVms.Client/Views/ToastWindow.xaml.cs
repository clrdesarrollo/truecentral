using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Notificación flotante de archivo guardado (captura o grabación): título +
/// ruta clickeable que abre el Explorador con el archivo seleccionado. Se
/// reutiliza una única instancia y se cierra sola a los 8 segundos.
/// </summary>
public partial class ToastWindow : Window
{
    private static ToastWindow? _current;
    private readonly DispatcherTimer _autoClose = new() { Interval = TimeSpan.FromSeconds(8) };
    private string _filePath = "";

    private ToastWindow()
    {
        InitializeComponent();
        _autoClose.Tick += (_, _) => Close();
        Closed += (_, _) => { if (_current == this) _current = null; };
    }

    /// <summary>Muestra (o actualiza) la notificación anclada al dueño.</summary>
    public static void ShowSaved(Window owner, string title, string glyph, string filePath)
    {
        var toast = _current ??= new ToastWindow();
        toast.Owner = owner;
        toast.TitleText.Text = title;
        toast.IconText.Text = glyph;
        toast._filePath = filePath;
        toast.PathRun.Text = filePath;
        if (!toast.IsVisible) toast.Show();
        toast.UpdateLayout();
        toast.PositionOverOwner();
        toast._autoClose.Stop();
        toast._autoClose.Start();
    }

    /// <summary>Esquina inferior derecha del área del dueño (coordenadas en
    /// DIPs vía el transform del monitor real: respeta DPI y maximizado).</summary>
    private void PositionOverOwner()
    {
        if (Owner is null || PresentationSource.FromVisual(Owner) is not { } source) return;
        var deviceBottomRight = Owner.PointToScreen(new Point(Owner.ActualWidth, Owner.ActualHeight));
        var bottomRight = source.CompositionTarget.TransformFromDevice.Transform(deviceBottomRight);
        Left = bottomRight.X - ActualWidth - 12;
        Top = bottomRight.Y - ActualHeight - 40; // sobre la barra de estado
    }

    private void OnOpenLocation(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(_filePath))
            {
                // Abre el Explorador con el archivo ya seleccionado.
                Process.Start("explorer.exe", $"/select,\"{_filePath}\"");
            }
            else if (Path.GetDirectoryName(_filePath) is { } folder && Directory.Exists(folder))
            {
                Process.Start("explorer.exe", $"\"{folder}\"");
            }
        }
        catch { /* sin Explorador disponible no hay nada que hacer */ }
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
