using System.Windows.Media;

namespace TrueCentralVms.Client.Services;

/// <summary>
/// Paleta de la apariencia Consola: el color de cada pincel del tema (mismas
/// claves que App.xaml / Themes/Consola.xaml). Esta es la fuente de verdad:
/// los valores de Consola.xaml son solo los de la paleta por omisión para que
/// el diseñador de XAML muestre algo coherente.
/// </summary>
public sealed class ConsolePalette
{
    public ConsolePalette(string key, string name, string description, IReadOnlyDictionary<string, string> colors)
    {
        Key = key;
        Name = name;
        Description = description;
        Colors = colors.ToDictionary(c => c.Key, c => (Color)ColorConverter.ConvertFromString(c.Value));
    }

    public string Key { get; }
    public string Name { get; }
    public string Description { get; }
    public IReadOnlyDictionary<string, Color> Colors { get; }

    // Muestras para elegir la paleta en Configuración.
    public Brush Bg => Swatch("BgBrush");
    public Brush Panel => Swatch("PanelBrush");
    public Brush Border => Swatch("BorderBrush");
    public Brush Text => Swatch("TextBrush");
    public Brush Accent => Swatch("AccentBrush");
    public Brush Ok => Swatch("OkBrush");
    public Brush Info => Swatch("InfoBrush");
    public Brush Warn => Swatch("WarnBrush");
    public Brush Danger => Swatch("DangerBrush");

    private Brush Swatch(string key)
    {
        var brush = new SolidColorBrush(Colors[key]);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Paletas de la Consola. Cada una tiene un carácter propio: un acento con
/// personalidad y señales (correcto / promedio / atención / falla) que no se
/// confunden con el acento. La por defecto es Grafito.
/// </summary>
public static class ConsolePalettes
{
    public const string Default = "grafito";

    /// <summary>Tinta azul noche con cian eléctrico: la de sala de monitoreo, emparentada con el azul del logo.</summary>
    public static readonly ConsolePalette Control = new("control", "Sala de control",
        "Azul noche con cian eléctrico; señales en menta, lavanda, ámbar y coral.",
        new Dictionary<string, string>
        {
            ["BgBrush"] = "#08111F",
            ["PanelBrush"] = "#0C1728",
            ["Panel2Brush"] = "#122036",
            ["BorderBrush"] = "#1F3150",
            ["ChromeBrush"] = "#0C1728",
            ["TextBrush"] = "#E3EBF7",
            ["MutedBrush"] = "#7F90B0",
            ["AccentBrush"] = "#2BC4FF",
            ["AccentHoverBrush"] = "#1497D6",
            ["AccentFillBrush"] = "#0C7DBA",
            ["AccentFillTextBrush"] = "#FFFFFF",
            ["AccentTintBrush"] = "#1F2BC4FF",
            ["OkBrush"] = "#2FE3A6",
            ["InfoBrush"] = "#A391FF",
            ["WarnBrush"] = "#FFB547",
            ["DangerBrush"] = "#FF5A76",
            ["HoverBrush"] = "#15233A",
            ["HoverStrongBrush"] = "#1C2D48",
            ["MenuHoverBrush"] = "#192841",
            ["CellBgBrush"] = "#040A14",
            ["CellBarBrush"] = "#0A1526",
            ["CellHoverBrush"] = "#0F1C30",
            ["VideoAreaBrush"] = "#030812",
            ["FaintTextBrush"] = "#2B3D5C",
            ["DimTextBrush"] = "#50648A",
            ["ScrollThumbBrush"] = "#22355A",
            ["ScrollThumbHoverBrush"] = "#304A78",
        });

    /// <summary>Grafito cálido con ámbar de terminal: alto contraste, cómodo en turnos de noche.</summary>
    public static readonly ConsolePalette Amber = new("ambar", "Ámbar",
        "Grafito cálido con ámbar de terminal; señales en lima, celeste, naranja y rojo.",
        new Dictionary<string, string>
        {
            ["BgBrush"] = "#110F0B",
            ["PanelBrush"] = "#19160F",
            ["Panel2Brush"] = "#211D15",
            ["BorderBrush"] = "#3A3123",
            ["ChromeBrush"] = "#19160F",
            ["TextBrush"] = "#F3EBDC",
            ["MutedBrush"] = "#A5977F",
            ["AccentBrush"] = "#FFB224",
            ["AccentHoverBrush"] = "#FFC452",
            ["AccentFillBrush"] = "#FFB224",
            // Sobre ámbar el texto blanco no se lee: los botones llenos van en tinta oscura.
            ["AccentFillTextBrush"] = "#1A1205",
            ["AccentTintBrush"] = "#24FFB224",
            ["OkBrush"] = "#9BD94A",
            ["InfoBrush"] = "#62C6FF",
            ["WarnBrush"] = "#FF8A3D",
            ["DangerBrush"] = "#FF5148",
            ["HoverBrush"] = "#262118",
            ["HoverStrongBrush"] = "#302A1F",
            ["MenuHoverBrush"] = "#2B261C",
            ["CellBgBrush"] = "#090806",
            ["CellBarBrush"] = "#15120D",
            ["CellHoverBrush"] = "#1C1811",
            ["VideoAreaBrush"] = "#070604",
            ["FaintTextBrush"] = "#463C2E",
            ["DimTextBrush"] = "#6E604B",
            ["ScrollThumbBrush"] = "#3B3326",
            ["ScrollThumbHoverBrush"] = "#554935",
        });

    /// <summary>Verde petróleo profundo con aguamarina: sobrio, distinto de los VMS azules.</summary>
    public static readonly ConsolePalette Petroleum = new("petroleo", "Petróleo",
        "Verde petróleo con aguamarina; señales en verde, azul, oro y coral.",
        new Dictionary<string, string>
        {
            ["BgBrush"] = "#061719",
            ["PanelBrush"] = "#0A2024",
            ["Panel2Brush"] = "#0F2A2F",
            ["BorderBrush"] = "#1A4046",
            ["ChromeBrush"] = "#0A2024",
            ["TextBrush"] = "#DDF0EE",
            ["MutedBrush"] = "#7FA7A4",
            ["AccentBrush"] = "#2EE6CF",
            ["AccentHoverBrush"] = "#13A895",
            ["AccentFillBrush"] = "#0C8C7D",
            ["AccentFillTextBrush"] = "#FFFFFF",
            ["AccentTintBrush"] = "#1F2EE6CF",
            ["OkBrush"] = "#9AE66E",
            ["InfoBrush"] = "#5FA8FF",
            ["WarnBrush"] = "#F3C14B",
            ["DangerBrush"] = "#FF6170",
            ["HoverBrush"] = "#123238",
            ["HoverStrongBrush"] = "#183D44",
            ["MenuHoverBrush"] = "#15383F",
            ["CellBgBrush"] = "#031011",
            ["CellBarBrush"] = "#08191C",
            ["CellHoverBrush"] = "#0C2226",
            ["VideoAreaBrush"] = "#020C0D",
            ["FaintTextBrush"] = "#224846",
            ["DimTextBrush"] = "#4C7774",
            ["ScrollThumbBrush"] = "#1D4549",
            ["ScrollThumbHoverBrush"] = "#2A5E63",
        });

    /// <summary>
    /// Grafito neutro (sin tinte) con azul azur: listas casi negras, barra
    /// superior en gris medio y el azul vivo para lo activo y las grabaciones.
    /// Colores medidos sobre la referencia que entregó el usuario (2026-10-18).
    /// </summary>
    public static readonly ConsolePalette Graphite = new("grafito", "Grafito",
        "Grafito neutro con azul azur; barra superior en gris medio y celeste en lo activo.",
        new Dictionary<string, string>
        {
            ["BgBrush"] = "#141414",
            ["PanelBrush"] = "#1C1C1C",
            ["Panel2Brush"] = "#262626",
            ["BorderBrush"] = "#333435",
            ["ChromeBrush"] = "#313131",
            ["TextBrush"] = "#EDEDED",
            ["MutedBrush"] = "#9A9A9A",
            ["AccentBrush"] = "#3A96EC",
            ["AccentHoverBrush"] = "#2A8AE6",
            ["AccentFillBrush"] = "#1A7AD6",
            ["AccentFillTextBrush"] = "#FFFFFF",
            ["AccentTintBrush"] = "#263A96EC",
            ["OkBrush"] = "#46C46E",
            ["InfoBrush"] = "#96E4FF",
            ["WarnBrush"] = "#F2B33D",
            ["DangerBrush"] = "#F0544F",
            ["HoverBrush"] = "#2A2A2A",
            ["HoverStrongBrush"] = "#343434",
            ["MenuHoverBrush"] = "#303030",
            ["CellBgBrush"] = "#060606",
            ["CellBarBrush"] = "#0A0A0A",
            ["CellHoverBrush"] = "#121212",
            ["VideoAreaBrush"] = "#050505",
            ["FaintTextBrush"] = "#3C3C3C",
            ["DimTextBrush"] = "#6A6A6A",
            ["ScrollThumbBrush"] = "#3A3A3A",
            ["ScrollThumbHoverBrush"] = "#505050",
        });

    /// <summary>En el orden de Configuración: la por defecto primero.</summary>
    public static IReadOnlyList<ConsolePalette> All { get; } = [Graphite, Control, Petroleum, Amber];

    public static ConsolePalette Find(string? key) =>
        All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Graphite;
}
