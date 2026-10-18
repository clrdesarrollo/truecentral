using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrueCentralVms.Client.Services;

/// <summary>
/// Apariencia del cliente. La clásica es la de siempre (App.xaml); la
/// <b>Consola</b> es un diseño técnico de sala de monitoreo: fondo oscuro con
/// tinte según la paleta elegida (<see cref="ConsolePalettes"/>), tipografía
/// condensada (Bahnschrift), cifras en monoespaciada,
/// esquinas rectas, telemetría bajo cada cuadro de video y la botonera de la
/// Vista en Vivo abajo.
/// <para>
/// Se aplica UNA vez, al arrancar y antes de crear cualquier ventana: las
/// vistas resuelven sus estilos con StaticResource al construirse, así que un
/// cambio de apariencia rige desde la próxima vez que se abre el cliente.
/// </para>
/// </summary>
public static class ClientTheme
{
    public const string Classic = "clasica";
    public const string Console = "consola";

    /// <summary>La apariencia en uso es la Consola (fija durante toda la ejecución).</summary>
    public static bool IsConsole { get; private set; }

    // Atajos para XAML ({x:Static}): las piezas propias de cada diseño se
    // muestran u ocultan sin converters ni enlaces.
    public static Visibility ConsoleOnly => IsConsole ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility ClassicOnly => IsConsole ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Barra de herramientas de la Vista en Vivo: arriba en la clásica; en la
    /// Consola, abajo y centrada como una botonera flotante. Va en su propia
    /// fila del DockPanel y no encima de la grilla: el video de Flyleaf se
    /// pinta en ventanas propias que taparían cualquier cosa superpuesta.
    /// </summary>
    public static Dock LiveToolbarDock => IsConsole ? Dock.Bottom : Dock.Top;
    public static HorizontalAlignment LiveToolbarAlignment =>
        IsConsole ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
    /// <summary>Columnas de los extremos de la barra: en la clásica se reparten
    /// el ancho (el grupo "Vistas" queda al centro); en la Consola la barra se
    /// ciñe a su contenido.</summary>
    public static GridLength LiveToolbarSideColumn =>
        IsConsole ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
    /// <summary>Separación del grupo central ("Vistas") en la botonera ceñida de la Consola.</summary>
    public static Thickness LiveToolbarGroupGap => IsConsole ? new Thickness(10, 0, 10, 0) : new Thickness(0);

    private const string ThemeUri = "/TrueCentralVms.Client;component/Themes/Consola.xaml";

    public static void Apply(Application app, string? appearance, string? palette)
    {
        // La Consola es la apariencia por defecto: solo "clasica" explícito la apaga.
        IsConsole = !string.Equals(appearance, Classic, StringComparison.OrdinalIgnoreCase);
        if (!IsConsole) return;

        var theme = (ResourceDictionary)Application.LoadComponent(new Uri(ThemeUri, UriKind.Relative));
        // Primero la paleta elegida sobre los pinceles del propio tema: sus
        // estilos los usan, y todavía no los congeló ningún estilo sellado.
        foreach (var (key, color) in ConsolePalettes.Find(palette).Colors)
        {
            if (!theme.Contains(key)) continue;
            if (theme[key] is SolidColorBrush own && !own.IsFrozen) own.Color = color;
            else theme[key] = new SolidColorBrush(color);
        }
        foreach (object key in theme.Keys)
        {
            object value = theme[key];
            // Los pinceles se REPINTAN en vez de reemplazarse: así también
            // cambian los estilos de App.xaml que ya los hubieran tomado (el
            // diccionario de la aplicación crea sus valores al primer uso, pero
            // no conviene depender de eso). Uno congelado se reemplaza.
            if (value is SolidColorBrush brush && app.Resources.Contains(key) &&
                app.Resources[key] is SolidColorBrush current && !current.IsFrozen)
            {
                current.Color = brush.Color;
                continue;
            }
            app.Resources[key] = value;
        }

        // Tipografía de toda la aplicación: la heredan los controles desde la
        // ventana (los glifos de Segoe MDL2 fijan la suya y no se ven afectados).
        if (theme["UiFont"] is FontFamily ui)
            Control.FontFamilyProperty.OverrideMetadata(typeof(Window),
                new FrameworkPropertyMetadata(ui, FrameworkPropertyMetadataOptions.Inherits));
    }
}
