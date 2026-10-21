using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Shell;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Barra de título propia para los diálogos (sin el cromo claro de Windows):
/// envuelve el contenido actual de la ventana con una barra del tema activo
/// que muestra <see cref="Window.Title"/> y un botón de cerrar. Cerrar con la
/// X equivale a la X de Windows (DialogResult queda sin aceptar).
/// Llamar después de InitializeComponent o de asignar Content.
/// </summary>
public static class DarkChrome
{
    public const double BarHeight = 38;

    public static void Apply(Window window)
    {
        window.WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = BarHeight,
            ResizeBorderThickness = new Thickness(window.ResizeMode == ResizeMode.NoResize ? 0 : 6),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        var body = window.Content as UIElement;
        window.Content = null;

        var root = new DockPanel();
        root.Children.Add(BuildTitleBar(window));
        if (body != null) root.Children.Add(body);

        var frame = new Border { BorderThickness = new Thickness(1), Child = root };
        frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        window.Content = frame;
    }

    private static UIElement BuildTitleBar(Window window)
    {
        var title = new TextBlock
        {
            Margin = new Thickness(14, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(Window.Title)) { Source = window });
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var close = new Button { Content = "", ToolTip = "Cerrar", IsTabStop = false };
        close.SetResourceReference(FrameworkElement.StyleProperty, "TitleBarCloseButton");
        close.Click += (_, _) => window.Close();
        DockPanel.SetDock(close, Dock.Right);

        var bar = new DockPanel();
        bar.Children.Add(close);
        bar.Children.Add(title);

        var border = new Border { Height = BarHeight, BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(border, Dock.Top);
        return border;
    }
}
