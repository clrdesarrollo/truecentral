using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Engranaje de «Últimas patentes»: de qué cámaras ANPR (equipos fuente de
/// patentes, los que ve la sesión por su alcance) se muestran las lecturas.
/// </summary>
public partial class PlateSourcesWindow : Window
{
    /// <summary>Una cámara ANPR en la lista.</summary>
    public sealed partial class SourceItem(AnprSourceDto source, bool isChecked) : ObservableObject
    {
        public int Id { get; } = source.DeviceId;
        public string Name { get; } = source.DeviceName;
        public bool IsLive { get; } = source.Live;
        public string Detail { get; } = !source.Enabled ? "Apagada como fuente de patentes"
            : source.LastEventAt is { } at ? $"Última lectura {at.ToLocalTime():dd-MM HH:mm}" : "Sin lecturas todavía";
        [ObservableProperty] private bool _isChecked = isChecked;
    }

    private readonly List<SourceItem> _items;

    private PlateSourcesWindow(IReadOnlyList<AnprSourceDto> sources, IReadOnlyCollection<int> current)
    {
        InitializeComponent();
        _items = sources.Select(s => new SourceItem(s, current.Contains(s.DeviceId))).ToList();
        SourceList.ItemsSource = _items;
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListBorder.Visibility = _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        // Elegidas que ya no están (otro alcance, equipo borrado) no cuentan: con ninguna, todas.
        if (_items.Any(i => i.IsChecked)) SomeRadio.IsChecked = true;
        else AllRadio.IsChecked = true;
    }

    /// <summary>Lo elegido: vacío = todas; null = se canceló.</summary>
    public IReadOnlyCollection<int>? Result { get; private set; }

    public static IReadOnlyCollection<int>? Ask(Window? owner, IReadOnlyList<AnprSourceDto> sources, IReadOnlyCollection<int> current)
    {
        var dialog = new PlateSourcesWindow(sources, current);
        if (owner is { IsLoaded: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    /// <summary>Con «todas» la lista queda a la vista pero no se marca.</summary>
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
            ErrorText.Text = "Marque al menos una cámara, o elija «De todas las cámaras ANPR».";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Result = chosen;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
