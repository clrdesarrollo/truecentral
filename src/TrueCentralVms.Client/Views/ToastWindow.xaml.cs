using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
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
    /// <summary>Qué hacer cuando el operador se da por enterado (null = aviso sin acuse).</summary>
    private Func<Task>? _onAcknowledge;
    /// <summary>Alerta que se está mostrando, para poder bajarla si otro la confirma.</summary>
    private long _alertId;

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
        toast.IconText.Foreground = (System.Windows.Media.Brush)toast.FindResource("AccentBrush");
        toast._filePath = filePath;
        toast.PathRun.Text = filePath;
        toast.PathText.Visibility = Visibility.Visible;
        toast.MessageText.Visibility = Visibility.Collapsed;
        toast.SetImage(null);
        toast.SetAcknowledge(0, null);
        toast.Present();
    }

    /// <summary>
    /// Aviso de alarma (sin archivo): título + mensaje, icono en rojo, y
    /// opcionalmente la foto que capturó una automatización. Se queda más
    /// tiempo que un aviso de archivo guardado porque el operador puede estar
    /// mirando otra pantalla; con foto, más todavía.
    /// </summary>
    public static void ShowAlert(Window owner, string title, string message, string glyph = "\uE814",
        byte[]? image = null)
    {
        var toast = _current ??= new ToastWindow();
        toast.Owner = owner;
        toast.TitleText.Text = title;
        toast.IconText.Text = glyph;
        toast.IconText.Foreground = (System.Windows.Media.Brush)toast.FindResource("DangerBrush");
        toast._filePath = "";
        toast.MessageText.Text = message;
        toast.PathText.Visibility = Visibility.Collapsed;
        toast.MessageText.Visibility = Visibility.Visible;
        toast.SetImage(image);
        toast.SetAcknowledge(0, null);
        toast.Present(TimeSpan.FromSeconds(image is null ? 15 : 30));
    }

    /// <summary>
    /// Alerta que EXIGE acuse de recibo: se queda en pantalla hasta que el
    /// operador la confirme (o hasta que otro la confirme desde otro puesto,
    /// en cuyo caso el servidor avisa y se cierra sola).
    /// </summary>
    public static void ShowAlertWithAck(Window owner, long alertId, string title, string message,
        string glyph, byte[]? image, Func<Task> onAcknowledge)
    {
        var toast = _current ??= new ToastWindow();
        toast.Owner = owner;
        toast.TitleText.Text = title;
        toast.IconText.Text = glyph;
        toast.IconText.Foreground = (System.Windows.Media.Brush)toast.FindResource("DangerBrush");
        toast._filePath = "";
        toast.MessageText.Text = message;
        toast.PathText.Visibility = Visibility.Collapsed;
        toast.MessageText.Visibility = Visibility.Visible;
        toast.SetImage(image);
        toast.SetAcknowledge(alertId, onAcknowledge);
        toast.Present();
    }

    /// <summary>Cierra el aviso de esa alerta si es el que está en pantalla (la confirmó otro puesto).</summary>
    public static void DismissAlert(long alertId)
    {
        if (_current is { } toast && toast._alertId == alertId) toast.Close();
    }

    private void SetAcknowledge(long alertId, Func<Task>? onAcknowledge)
    {
        _alertId = alertId;
        _onAcknowledge = onAcknowledge;
        AckPanel.Visibility = onAcknowledge is null ? Visibility.Collapsed : Visibility.Visible;
        AckButton.IsEnabled = true;
        AckButton.Content = "Enterado";
    }

    private async void OnAcknowledge(object sender, RoutedEventArgs e)
    {
        if (_onAcknowledge is not { } acknowledge) { Close(); return; }
        AckButton.IsEnabled = false;
        AckButton.Content = "Registrando…";
        try
        {
            await acknowledge();
            Close();
        }
        catch (Exception)
        {
            // Sin registro no hay acuse: se deja el aviso en pantalla para
            // reintentar (perderlo sería justamente el agujero que se quiere evitar).
            AckButton.IsEnabled = true;
            AckButton.Content = "Reintentar";
            AckHint.Text = "No se pudo registrar la confirmación; reintente.";
        }
    }

    /// <summary>Pinta (o quita) la foto del aviso. Una imagen ilegible no
    /// puede tumbar la notificación: en ese caso el aviso va sin foto.</summary>
    private void SetImage(byte[]? image)
    {
        if (image is null or { Length: 0 })
        {
            AlertImage.Source = null;
            AlertImage.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(image);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;   // se puede cerrar el stream
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            AlertImage.Source = bitmap;
            AlertImage.Visibility = Visibility.Visible;
        }
        catch
        {
            AlertImage.Source = null;
            AlertImage.Visibility = Visibility.Collapsed;
        }
    }

    private void Present(TimeSpan? autoClose = null)
    {
        var toast = this;
        if (!toast.IsVisible) toast.Show();
        toast.UpdateLayout();
        toast.PositionOverOwner();
        toast._autoClose.Stop();
        // Un aviso con acuse de recibo NO se cierra solo: si desapareciera
        // después de unos segundos, nadie podría demostrar que lo vio.
        if (toast._onAcknowledge is not null) return;
        toast._autoClose.Interval = autoClose ?? TimeSpan.FromSeconds(8);
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
