using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using TrueCentralVms.WebControl.Fingerprint;
using Brush = System.Windows.Media.Brush;
using MessageBox = System.Windows.MessageBox;

namespace TrueCentralVms.WebControl;

/// <summary>
/// Ventana de diagnóstico del web control. No es obligatoria: se abre desde el
/// icono de bandeja y al cerrarla el control sigue atendiendo al panel web.
/// </summary>
public partial class MainWindow : Window
{
    private readonly App _app;

    public MainWindow(App app)
    {
        InitializeComponent();
        _app = app;
        VersionText.Text = $"v{WebControlHost.Version}";

        foreach (string line in app.Activity) AppendActivity(line);
        app.Logged += AppendActivity;

        RefreshStatus();
        RefreshReaders();
    }

    /// <summary>Refleja el estado del control (lo actualiza también App al arrancar).</summary>
    public void RefreshStatus()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RefreshStatus); return; }
        StatusDot.Fill = (Brush)FindResource(_app.Running ? "OkBrush" : "DangerBrush");
        StatusText.Text = _app.StatusText;
        SdkText.Text = $"SDK del lector de huellas: {_app.Fingerprint.SdkVersion()}";
        FooterText.Text = "Al cerrar esta ventana el web control sigue corriendo en la bandeja del sistema. " +
                          $"Ajustes: {WebControlSettings.FilePath}";
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshReaders();

    private void RefreshReaders()
    {
        var readers = FingerprintReaders.Scan();
        ReaderList.ItemsSource = readers;
        int detected = readers.Count(r => r.Device is not null);
        _app.Log(detected == 0
            ? "No se detectó ningún lector conectado: se usará la detección automática del SDK."
            : $"{detected} lector(es) detectado(s): {string.Join(", ", readers.Where(r => r.Device is not null).Select(r => r.Id))}.");
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        _app.Log("Probando el lector…");
        var (ok, message) = await Task.Run(() => _app.Fingerprint.TestReader(FingerprintReaders.AutoId));
        _app.Log(message);
        if (!ok)
            MessageBox.Show(message, "Prueba del lector", MessageBoxButton.OK, MessageBoxImage.Warning);
        TestButton.IsEnabled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Cerrar la ventana NO cierra el control: sigue en la bandeja.
        base.OnClosing(e);
        _app.Logged -= AppendActivity;
        _app.ForgetWindow();
    }

    private void AppendActivity(string line)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AppendActivity(line)); return; }
        ActivityPanel.Children.Add(new TextBlock
        {
            Text = line,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2),
            Foreground = (Brush)FindResource("TextBrush"),
        });
        while (ActivityPanel.Children.Count > 200) ActivityPanel.Children.RemoveAt(0);
        ActivityScroll.ScrollToEnd();
    }
}
