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

    /// <summary>Muestra el diálogo; null si se canceló. Save = dejarla en «Mis páginas».</summary>
    public static (string Url, string Title, bool Save)? Ask(Window? owner)
    {
        var dialog = new WebUrlWindow();
        if (owner is { IsLoaded: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? (dialog.Url, dialog.PageTitle, dialog.SaveBox.IsChecked == true) : null;
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        string address = UrlBox.Text.Trim(), title = TitleBox.Text.Trim();
        // La dirección quedó en el nombre (y algo que no es dirección, en la
        // dirección): se dan vuelta en vez de abrir "https://teles/".
        if (Parse(address) is null && Parse(title) is not null)
        {
            (address, title) = (title, address);
            UrlBox.Text = address;
            TitleBox.Text = title;
        }
        if (Parse(address) is not { } uri)
        {
            ErrorText.Text = address.Length == 0
                ? "Escriba la dirección de la página, por ejemplo https://www.ejemplo.cl."
                : $"«{address}» no es una dirección completa: falta el sitio con su dominio (por ejemplo " +
                  "https://www.ejemplo.cl/pagina).";
            ErrorText.Visibility = Visibility.Visible;
            UrlBox.Focus();
            return;
        }
        Url = uri.ToString();
        PageTitle = title.Length > 0 ? title : uri.Host;
        DialogResult = true;
    }

    /// <summary>
    /// La dirección escrita, como la entiende la barra de un navegador: sin
    /// esquema se asume https. Sin esquema, el sitio tiene que tener dominio
    /// ("www.ejemplo.cl"), ser una IP o localhost; escribiendo http:// o
    /// https:// vale también un nombre de la red interna. Null si no sirve.
    /// </summary>
    private static Uri? Parse(string text)
    {
        if (text.Length == 0 || text.Any(char.IsWhiteSpace)) return null;
        bool explicitScheme = text.Contains("://");
        if (!explicitScheme) text = "https://" + text;
        if (text.Length > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || string.IsNullOrEmpty(uri.Host))
            return null;
        return explicitScheme || !IsBareName(uri.Host) ? uri : null;
    }

    /// <summary>Un nombre suelto ("teles"): sin punto, ni IP ni localhost.</summary>
    private static bool IsBareName(string host) =>
        !host.Contains('.') && !host.Contains(':') && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
