using System.Windows;

namespace TrueCentralVms.Client.Views;

/// <summary>Pide la dirección (y un rótulo) de una página web para un cuadro.</summary>
public partial class WebUrlWindow : Window
{
    public WebUrlWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => UrlBox.Focus();
    }

    /// <summary>Dirección aceptada (absoluta, http o https).</summary>
    public string Url { get; private set; } = "";

    /// <summary>Rótulo del cuadro: el escrito o, si no, el nombre del sitio.</summary>
    public string PageTitle { get; private set; } = "";

    /// <summary>Muestra el diálogo; null si se canceló.</summary>
    public static (string Url, string Title)? Ask(Window? owner)
    {
        var dialog = new WebUrlWindow();
        if (owner is { IsLoaded: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? (dialog.Url, dialog.PageTitle) : null;
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        string text = UrlBox.Text.Trim();
        // Sin esquema se asume https (lo que se escribe en la barra de un navegador).
        if (text.Length > 0 && !text.Contains("://")) text = "https://" + text;
        if (text.Length > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || string.IsNullOrEmpty(uri.Host))
        {
            ErrorText.Text = "Escriba una dirección web completa, por ejemplo https://www.ejemplo.cl.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Url = uri.ToString();
        PageTitle = TitleBox.Text.Trim() is { Length: > 0 } title ? title : uri.Host;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
