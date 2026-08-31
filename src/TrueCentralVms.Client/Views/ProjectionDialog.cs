using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrueCentralVms.Client.Services;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Diálogo de proyección: elige qué transmitir (un monitor de este PC o un
/// archivo de video en bucle) y la IP de este PC que el decoder usará para
/// traer el stream RTSP. El destino es la ventana del wall ya seleccionada.
/// </summary>
public sealed class ProjectionDialog : Window
{
    private readonly RadioButton _monitorRadio;
    private readonly RadioButton _fileRadio;
    private readonly ComboBox _monitorCombo;
    private readonly TextBox _fileBox;
    private readonly Button _browseButton;
    private readonly ComboBox _ipCombo;
    private readonly CheckBox _loopCheck;

    public MonitorInfo? SelectedMonitor { get; private set; }
    public string? SelectedFile { get; private set; }
    public string SelectedIp { get; private set; } = "";

    /// <summary>Reproducir el archivo en bucle (si no, al terminar se detiene la transmisión).</summary>
    public bool LoopFile { get; private set; } = true;

    public ProjectionDialog(IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<string> ips, string targetLabel,
        bool loopDefault = true)
    {
        Title = "Proyectar al muro de video";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush)FindResource("BgBrush");
        Foreground = (Brush)FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        ShowInTaskbar = false;
        // Los estilos del módulo del muro (botones, combos, cajas de texto):
        // sin esto los controles saldrían con el tema claro de Windows.
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/TrueCentralVms.Client;component/Views/WallStyles.xaml", UriKind.Relative),
        });

        var root = new StackPanel { Margin = new Thickness(18) };
        Content = root;

        root.Children.Add(new TextBlock
        {
            Text = $"Destino: {targetLabel}",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
        });

        _monitorRadio = new RadioButton
        {
            Content = "Una pantalla de este PC",
            Foreground = (Brush)FindResource("TextBrush"),
            IsChecked = true,
        };
        root.Children.Add(_monitorRadio);

        _monitorCombo = new ComboBox { ItemsSource = monitors, Margin = new Thickness(20, 6, 0, 10) };
        int primary = monitors.ToList().FindIndex(m => m.Primary);
        _monitorCombo.SelectedIndex = primary >= 0 ? primary : 0;
        root.Children.Add(_monitorCombo);

        _fileRadio = new RadioButton
        {
            Content = "Un archivo de video",
            Foreground = (Brush)FindResource("TextBrush"),
        };
        root.Children.Add(_fileRadio);

        var fileRow = new DockPanel { Margin = new Thickness(20, 6, 0, 0) };
        _browseButton = new Button { Content = "Examinar…", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(_browseButton, Dock.Right);
        _fileBox = new TextBox { Padding = new Thickness(6, 4, 6, 4), IsReadOnly = true };
        fileRow.Children.Add(_browseButton);
        fileRow.Children.Add(_fileBox);
        root.Children.Add(fileRow);

        _loopCheck = new CheckBox
        {
            Content = "Reproducir en bucle",
            IsChecked = loopDefault,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(20, 8, 0, 0),
            ToolTip = "Desactivado: al terminar el video la transmisión se detiene sola y las ventanas vuelven a su canal anterior.",
        };
        root.Children.Add(_loopCheck);

        _browseButton.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Video a transmitir",
                Filter = "Videos|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.ts;*.m4v|Todos los archivos|*.*",
            };
            if (dialog.ShowDialog(this) == true)
            {
                _fileBox.Text = dialog.FileName;
                _fileRadio.IsChecked = true;
            }
        };

        void SyncEnabled()
        {
            _monitorCombo.IsEnabled = _monitorRadio.IsChecked == true;
            _fileBox.IsEnabled = _browseButton.IsEnabled = _loopCheck.IsEnabled = _fileRadio.IsChecked == true;
        }
        _monitorRadio.Checked += (_, _) => SyncEnabled();
        _fileRadio.Checked += (_, _) => SyncEnabled();
        SyncEnabled();

        root.Children.Add(new TextBlock
        {
            Text = "IP de este PC (la que el decoder puede alcanzar)",
            Foreground = (Brush)FindResource("MutedBrush"),
            Margin = new Thickness(0, 12, 0, 4),
        });
        _ipCombo = new ComboBox { IsEditable = true, ItemsSource = ips };
        if (ips.Count > 0) _ipCombo.SelectedIndex = 0;
        root.Children.Add(_ipCombo);

        root.Children.Add(new TextBlock
        {
            Text = "El decoder inicia la conexión hacia este PC (RTSP H.264, puerto 8554/TCP). " +
                   "El PC debe estar en una red alcanzable desde el decoder y el firewall debe " +
                   "permitir mediamtx.exe.",
            Foreground = (Brush)FindResource("MutedBrush"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var start = new Button { Content = "Iniciar", Padding = new Thickness(14, 6, 14, 6), IsDefault = true };
        var cancel = new Button { Content = "Cancelar", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        start.Click += (_, _) =>
        {
            SelectedIp = (_ipCombo.Text ?? "").Trim();
            SelectedMonitor = _monitorRadio.IsChecked == true ? _monitorCombo.SelectedItem as MonitorInfo : null;
            SelectedFile = _fileRadio.IsChecked == true ? _fileBox.Text.Trim() : null;
            LoopFile = _loopCheck.IsChecked == true;

            if (SelectedIp.Length == 0 ||
                (SelectedMonitor is null && string.IsNullOrEmpty(SelectedFile)))
            {
                MessageBox.Show(this, "Seleccione qué transmitir y la IP del PC.", "Proyectar",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
        };
        buttons.Children.Add(start);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
    }
}
