using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Reemplazo de <see cref="MessageBox"/> con la apariencia del cliente (barra
/// de título propia, colores y botones del tema activo) en lugar del cuadro
/// gris de Windows. Misma firma y mismos resultados: cambiar
/// <c>MessageBox.Show</c> por <c>AppMessageBox.Show</c> no altera la lógica.
/// </summary>
public static class AppMessageBox
{
    public static MessageBoxResult Show(Window? owner, string text, string caption = "",
        MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
            return dispatcher.Invoke(() => Show(owner, text, caption, button, icon, defaultResult));

        var dialog = new MessageDialog(text, caption, button, icon, defaultResult);
        // Solo se cuelga de una ventana a la vista: Owner con una ventana
        // cerrada u oculta lanza o deja el diálogo detrás de todo.
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.ShowInTaskbar = false;
        }
        dialog.ShowDialog();
        return dialog.Result;
    }

    public static MessageBoxResult Show(string text, string caption = "",
        MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None) =>
        Show(Application.Current?.MainWindow, text, caption, button, icon, defaultResult);

    private sealed class MessageDialog : Window
    {
        private readonly MessageBoxResult _cancelResult;

        public MessageBoxResult Result { get; private set; }

        public MessageDialog(string text, string caption, MessageBoxButton button,
            MessageBoxImage icon, MessageBoxResult defaultResult)
        {
            Title = string.IsNullOrWhiteSpace(caption) ? "TrueCentral VMS" : caption;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            MinWidth = 380;
            MaxWidth = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            UseLayoutRounding = true;
            SetResourceReference(BackgroundProperty, "BgBrush");
            SetResourceReference(ForegroundProperty, "TextBrush");
            var choices = Choices(button);
            _cancelResult = choices.Contains(MessageBoxResult.Cancel) ? MessageBoxResult.Cancel
                : choices.Contains(MessageBoxResult.No) ? MessageBoxResult.No
                : MessageBoxResult.OK;
            Result = _cancelResult;
            if (!choices.Contains(defaultResult)) defaultResult = choices[0];

            var root = new DockPanel();
            root.Children.Add(BuildButtons(choices, defaultResult));
            root.Children.Add(BuildBody(text, icon));
            Content = root;
            DarkChrome.Apply(this);

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            };
        }

        private static MessageBoxResult[] Choices(MessageBoxButton button) => button switch
        {
            MessageBoxButton.OKCancel => [MessageBoxResult.OK, MessageBoxResult.Cancel],
            MessageBoxButton.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
            MessageBoxButton.YesNoCancel => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel],
            _ => [MessageBoxResult.OK],
        };

        private static string Label(MessageBoxResult result) => result switch
        {
            MessageBoxResult.Yes => "Sí",
            MessageBoxResult.No => "No",
            MessageBoxResult.Cancel => "Cancelar",
            _ => "Aceptar",
        };

        private static UIElement BuildBody(string text, MessageBoxImage icon)
        {
            var panel = new DockPanel { Margin = new Thickness(22, 22, 24, 22) };

            var (glyph, brush) = icon switch
            {
                MessageBoxImage.Error => ("", "DangerBrush"),
                MessageBoxImage.Warning => ("", "WarnBrush"),
                MessageBoxImage.Question => ("", "AccentBrush"),
                MessageBoxImage.Information => ("", "InfoBrush"),
                _ => (null, null),
            };
            if (glyph != null)
            {
                var symbol = new TextBlock
                {
                    Text = glyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 26,
                    Margin = new Thickness(0, 0, 16, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                symbol.SetResourceReference(TextBlock.ForegroundProperty, brush!);
                DockPanel.SetDock(symbol, Dock.Left);
                panel.Children.Add(symbol);
            }

            var message = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                LineHeight = 19,
            };
            message.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            panel.Children.Add(message);
            return panel;
        }

        private UIElement BuildButtons(MessageBoxResult[] choices, MessageBoxResult defaultResult)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            foreach (var choice in choices)
            {
                var affirmative = choice is MessageBoxResult.OK or MessageBoxResult.Yes;
                var b = new Button
                {
                    Content = Label(choice),
                    MinWidth = 88,
                    Margin = new Thickness(8, 0, 0, 0),
                    IsDefault = choice == defaultResult,
                };
                b.SetResourceReference(StyleProperty, affirmative ? "PrimaryButton" : "GhostButton");
                b.Click += (_, _) => { Result = choice; Close(); };
                if (choice == defaultResult) Loaded += (_, _) => b.Focus();
                row.Children.Add(b);
            }

            var footer = new Border
            {
                Padding = new Thickness(16, 12, 16, 12),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = row,
            };
            footer.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            footer.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            DockPanel.SetDock(footer, Dock.Bottom);
            return footer;
        }
    }
}
