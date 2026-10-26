using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.PatrolNotifier;

/// <summary>
/// Recordatorio de ronda en el PC de la guardia: «se acerca» (azul), «es hora de salir» (naranja) y «no
/// iniciada» (rojo). Se repite según la ruta mientras el bastón siga en la base y se cierra solo cuando
/// sale (el servidor deja de mandarlo). «Entendido» lo oculta hasta la próxima repetición.
/// </summary>
public sealed class ReminderWindow : Window
{
    private static readonly Brush Background1 = Hex("#1E2329");
    private static readonly Brush Border1 = Hex("#2F3740");
    private static readonly Brush Text = Hex("#E6E8EB");
    private static readonly Brush Muted = Hex("#8B949E");
    private static readonly Brush Info = Hex("#4DA3FF");
    private static readonly Brush Warning = Hex("#F5A524");
    private static readonly Brush Bad = Hex("#E5484D");

    private readonly TextBlock _title = new() { FontSize = 24, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _message = new() { FontSize = 16, Foreground = Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 18) };
    private readonly Border _stripe = new() { Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 0, 16) };
    private long? _runId;

    public ReminderWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = 500;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "TrueCentral · Ronda";

        var ok = new Button
        {
            Content = "Entendido", Padding = new Thickness(18, 8, 18, 8), HorizontalAlignment = HorizontalAlignment.Right,
            Background = Border1, Foreground = Text, BorderThickness = new Thickness(0), FontSize = 14, Cursor = System.Windows.Input.Cursors.Hand,
        };
        ok.Click += (_, _) => Hide();
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = "TRUECENTRAL · CONTROL DE RONDAS", FontSize = 11, Foreground = Muted, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(_stripe);
        panel.Children.Add(_title);
        panel.Children.Add(_message);
        panel.Children.Add(ok);
        Content = new Border
        {
            Background = Background1, BorderBrush = Border1, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.5 },
        };
    }

    public void Show(PatrolDockProgress p)
    {
        if (p.Stage == "reminder-clear")
        {
            if (p.RunId == _runId) Hide();
            return;
        }
        _runId = p.RunId;
        (string title, Brush color, SystemSound sound) = p.Stage switch
        {
            "reminder-upcoming" => ("Se acerca una ronda", Info, SystemSounds.Asterisk),
            "reminder-due" => ("Es hora de salir a la ronda", Warning, SystemSounds.Exclamation),
            _ => ("Ronda no iniciada", Bad, SystemSounds.Hand),
        };
        _title.Text = title;
        _title.Foreground = color;
        _stripe.Background = color;
        _message.Text = p.Message ?? "";
        if (!IsVisible) Show();
        sound.Play();
    }

    /// <summary>Imagen del recordatorio (modo --demo-reminder --snapshots).</summary>
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
