using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.PatrolNotifier;

/// <summary>
/// Aviso de la descarga: sin bordes, siempre encima y centrado, para que el
/// guardia no saque el bastón a mitad. Se cierra solo si todo salió bien; si
/// algo falló queda abierto hasta que lo cierren (clic).
/// </summary>
public sealed class ProgressWindow : Window
{
    private static readonly Brush Background1 = Hex("#1E2329");
    private static readonly Brush Border1 = Hex("#2F3740");
    private static readonly Brush Text = Hex("#E6E8EB");
    private static readonly Brush Muted = Hex("#8B949E");
    private static readonly Brush Warning = Hex("#F5A524");
    private static readonly Brush Ok = Hex("#2EBD85");
    private static readonly Brush Bad = Hex("#E5484D");

    private static readonly (string Stage, string Label)[] Steps =
    [
        ("detected", "Bastón detectado"),
        ("clock", "Puesto en hora"),
        ("reading", "Leyendo la memoria"),
        ("uploading", "Enviando al servidor"),
        ("erasing", "Vaciando el bastón"),
    ];

    private readonly TextBlock _title = new() { FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _subtitle = new() { FontSize = 13, Foreground = Muted, Margin = new Thickness(0, 4, 0, 14) };
    private readonly ProgressBar _bar = new() { Height = 10, Minimum = 0, Maximum = 100, Foreground = Warning, Background = Border1, BorderThickness = new Thickness(0) };
    private readonly StackPanel _steps = new() { Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBlock _result = new() { FontSize = 13, Foreground = Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly DispatcherTimer _autoClose = new() { Interval = TimeSpan.FromSeconds(6) };
    private int _reached = -1;

    public ProgressWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "TrueCentral · Control de rondas";

        var header = new TextBlock { Text = "TRUECENTRAL · CONTROL DE RONDAS", FontSize = 11, Foreground = Muted, Margin = new Thickness(0, 0, 0, 10) };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(header);
        panel.Children.Add(_title);
        panel.Children.Add(_subtitle);
        panel.Children.Add(_bar);
        panel.Children.Add(_steps);
        panel.Children.Add(_result);
        Content = new Border
        {
            Background = Background1, BorderBrush = Border1, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.5 },
        };
        MouseLeftButtonUp += (_, _) => { if (!_bar.IsIndeterminate && _reached >= Steps.Length) Hide(); };
        _autoClose.Tick += (_, _) => { _autoClose.Stop(); Hide(); };
    }

    public void Show(PatrolDockProgress p)
    {
        _autoClose.Stop();
        string who = p.Serial is { } s ? $"Bastón {s:D6} · {p.Port}" : p.Port;
        _subtitle.Text = who;
        int index = Array.FindIndex(Steps, x => x.Stage == p.Stage);

        switch (p.Stage)
        {
            case "detected":
                _reached = 0;
                _result.Text = "";
                Busy("No desconecte el bastón");
                break;
            case "done":
                _reached = Steps.Length;
                Finish("Listo: descarga completa", Ok, p.Message, SystemSounds.Asterisk, autoClose: true);
                return;
            case "queued":
                _reached = Steps.Length;
                Finish("Lecturas guardadas en este PC", Warning, p.Message, SystemSounds.Exclamation, autoClose: true);
                return;
            case "removed":
                _reached = Steps.Length;
                Finish("Se desconectó el bastón", Bad, p.Message, SystemSounds.Hand, autoClose: false);
                return;
            case "error":
                _reached = Steps.Length;
                Finish("No se pudo completar la descarga", Bad, p.Message, SystemSounds.Hand, autoClose: false);
                return;
            default:
                if (index > _reached) _reached = index;
                Busy("No desconecte el bastón");
                break;
        }

        if (p.Stage == "reading" && p.Blocks > 0)
        {
            _bar.IsIndeterminate = false;
            _bar.Value = 100.0 * p.Block / p.Blocks;
        }
        else _bar.IsIndeterminate = true;

        DrawSteps(p);
        if (!IsVisible) Show();
    }

    private void Busy(string title)
    {
        _title.Text = title;
        _title.Foreground = Warning;
        _bar.Foreground = Warning;
    }

    private void Finish(string title, Brush color, string? message, SystemSound sound, bool autoClose)
    {
        _title.Text = title;
        _title.Foreground = color;
        _bar.IsIndeterminate = false;
        _bar.Value = 100;
        _bar.Foreground = color;
        _result.Text = (message ?? "") + (autoClose ? "" : "\n\nToque este aviso para cerrarlo.");
        DrawSteps(null);
        if (!IsVisible) Show();
        sound.Play();
        if (autoClose) _autoClose.Start();
    }

    private void DrawSteps(PatrolDockProgress? current)
    {
        _steps.Children.Clear();
        for (int i = 0; i < Steps.Length; i++)
        {
            bool done = i < _reached || (_reached >= Steps.Length);
            bool active = i == _reached && _reached < Steps.Length;
            string label = Steps[i].Label;
            if (active && current is { Stage: "reading", Blocks: > 0 })
                label += $" · bloque {current.Block} de {current.Blocks}";
            _steps.Children.Add(new TextBlock
            {
                Text = (done ? "✓  " : active ? "●  " : "○  ") + label,
                FontSize = 14,
                Margin = new Thickness(0, 3, 0, 3),
                Foreground = done ? Text : active ? Warning : Muted,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            });
        }
    }

    /// <summary>Imagen del aviso tal como se ve (modo --demo --snapshots, para revisar el diseño).</summary>
    public void SaveSnapshot(string path)
    {
        if (Content is not FrameworkElement root) return;
        root.Measure(new Size(Width, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(path);
        encoder.Save(file);
    }

    private static SolidColorBrush Hex(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
