using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Engranaje de las tarjetas de lista: de qué equipos (los que ve la sesión por
/// su alcance) se muestran los eventos. «Últimas patentes» elige cámaras ANPR y
/// «Últimos accesos», equipos de acceso.
/// </summary>
public partial class CardSourcesWindow : Window
{
    /// <summary>Un equipo en la lista: el punto verde = en línea; el detalle va debajo del nombre.</summary>
    public sealed record Source(int Id, string Name, bool IsLive, string Detail);

    /// <summary>Los textos de la ventana según la tarjeta.</summary>
    /// <param name="NoneChecked">Aviso si se eligió «solo los marcados» sin marcar ninguno.</param>
    public sealed record Texts(string Glyph, string Header, string Intro, string All, string Some,
        string Empty, string NoneChecked);

    /// <summary>Un equipo en la lista, con su casilla.</summary>
    public sealed partial class SourceItem(Source source, bool isChecked) : ObservableObject
    {
        public int Id { get; } = source.Id;
        public string Name { get; } = source.Name;
        public bool IsLive { get; } = source.IsLive;
        public string Detail { get; } = source.Detail;
        [ObservableProperty] private bool _isChecked = isChecked;
    }

    private readonly List<SourceItem> _items;
    private readonly Texts _texts;

    private CardSourcesWindow(Texts texts, IReadOnlyList<Source> sources, IReadOnlyCollection<int> current)
    {
        InitializeComponent();
        _texts = texts;
        Title = $"{texts.Header} — CLR TrueCentral VMS";
        GlyphText.Text = texts.Glyph;
        HeaderText.Text = texts.Header;
        IntroText.Text = texts.Intro;
        AllRadio.Content = texts.All;
        SomeRadio.Content = texts.Some;
        EmptyText.Text = texts.Empty;
        _items = sources.Select(s => new SourceItem(s, current.Contains(s.Id))).ToList();
        SourceList.ItemsSource = _items;
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListBorder.Visibility = _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        // Elegidos que ya no están (otro alcance, equipo borrado) no cuentan: con ninguno, todos.
        if (_items.Any(i => i.IsChecked)) SomeRadio.IsChecked = true;
        else AllRadio.IsChecked = true;
    }

    /// <summary>Lo elegido: vacío = todos; null = se canceló.</summary>
    public IReadOnlyCollection<int>? Result { get; private set; }

    public static IReadOnlyCollection<int>? Ask(Window? owner, Texts texts, IReadOnlyList<Source> sources,
        IReadOnlyCollection<int> current)
    {
        var dialog = new CardSourcesWindow(texts, sources, current);
        if (owner is { IsLoaded: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    /// <summary>Con «todos» la lista queda a la vista pero no se marca.</summary>
    private void OnModeChanged(object sender, RoutedEventArgs e) =>
        SourceList.IsEnabled = SomeRadio.IsChecked == true;

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (AllRadio.IsChecked == true)
        {
            Result = [];
            DialogResult = true;
            return;
        }
        var chosen = _items.Where(i => i.IsChecked).Select(i => i.Id).ToHashSet();
        if (chosen.Count == 0)
        {
            ErrorText.Text = _texts.NoneChecked;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Result = chosen;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
