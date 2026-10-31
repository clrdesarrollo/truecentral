using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using TrueCentralVms.Client.ViewModels;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Engranaje del Cuadro vigía (<see cref="WatchOptions"/>): qué tipos de evento,
/// de qué ubicaciones, la gravedad mínima, cuánto queda cada uno en pantalla, la
/// cámara de reposo y el stream.
/// </summary>
public partial class WatchSettingsWindow : Window
{
    /// <summary>Una fila con casilla (un tipo de evento o una ubicación).</summary>
    public sealed partial class CheckItem(string key, int id, string name, bool isChecked) : ObservableObject
    {
        public string Key { get; } = key;
        public int Id { get; } = id;
        public string Name { get; } = name;
        [ObservableProperty] private bool _isChecked = isChecked;
    }

    /// <summary>Una opción de las listas: se muestra por su texto (también en lo elegido).</summary>
    public sealed record Choice(string Label, object Value)
    {
        public override string ToString() => Label;
    }

    private readonly List<CheckItem> _categories;
    private readonly List<CheckItem> _locations;

    private WatchSettingsWindow(WatchOptions current, IReadOnlyList<(int Id, string Path)> locations,
        IReadOnlyList<ChannelNode> channels)
    {
        InitializeComponent();
        _categories = LiveEventCategories.All
            .Select(c => new CheckItem(c, 0, LiveEventCategories.Label(c), current.Categories.Contains(c))).ToList();
        CategoryList.ItemsSource = _categories;

        _locations = locations.OrderBy(l => l.Path, StringComparer.CurrentCultureIgnoreCase)
            .Select(l => new CheckItem("", l.Id, l.Path, current.LocationIds.Contains(l.Id))).ToList();
        LocationList.ItemsSource = _locations;
        NoLocationsText.Visibility = _locations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SomePlacesRadio.IsEnabled = _locations.Count > 0;
        if (_locations.Any(l => l.IsChecked)) SomePlacesRadio.IsChecked = true;
        else AllPlacesRadio.IsChecked = true;

        SeverityBox.ItemsSource = new[]
        {
            new Choice("Informativa (todo)", AlarmSeverity.Info),
            new Choice("Advertencia y crítica", AlarmSeverity.Warning),
            new Choice("Solo crítica", AlarmSeverity.Critical),
        };
        SeverityBox.SelectedValue = current.MinSeverity;

        int[] holds = [5, 10, 15, 20, 30, 45, 60, 90, 120];
        HoldBox.ItemsSource = holds.Select(h => new Choice($"{h} s", h)).ToList();
        HoldBox.SelectedValue = holds.Contains(current.HoldSeconds) ? current.HoldSeconds : 20;

        var idle = new List<Choice> { new("Nada (queda esperando)", 0) };
        idle.AddRange(channels.OrderBy(c => c.Device.Name).ThenBy(c => c.Channel.ChannelNumber)
            .Select(c => new Choice($"{c.Device.Name} · {c.Channel.Name}", c.Channel.Id)));
        IdleBox.ItemsSource = idle;
        IdleBox.SelectedValue = current.IdleChannelId is { } id && idle.Any(i => (int)i.Value == id) ? id : 0;

        int[] pins = [1, 2, 5, 10, 15, 30, 0];
        PinBox.ItemsSource = pins.Select(p => new Choice(p == 0 ? "Nunca" : $"A los {p} min", p)).ToList();
        PinBox.SelectedValue = pins.Contains(current.PinMinutes) ? current.PinMinutes : 5;

        MainStreamCheck.IsChecked = current.MainStream;
    }

    /// <summary>Lo elegido; null = se canceló.</summary>
    public WatchOptions? Result { get; private set; }

    public static WatchOptions? Ask(Window? owner, WatchOptions current, IReadOnlyList<(int Id, string Path)> locations,
        IReadOnlyList<ChannelNode> channels)
    {
        var dialog = new WatchSettingsWindow(current, locations, channels);
        if (owner is { IsLoaded: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private void OnPlacesChanged(object sender, RoutedEventArgs e) =>
        LocationList.IsEnabled = SomePlacesRadio.IsChecked == true;

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        var categories = _categories.Where(c => c.IsChecked).Select(c => c.Key).ToHashSet();
        if (categories.Count == 0)
        {
            ErrorText.Text = "Marque al menos un tipo de evento.";
            return;
        }
        var locations = SomePlacesRadio.IsChecked == true
            ? _locations.Where(l => l.IsChecked).Select(l => l.Id).ToHashSet()
            : [];
        if (SomePlacesRadio.IsChecked == true && locations.Count == 0)
        {
            ErrorText.Text = "Marque al menos una ubicación, o elija «Todo lo que veo».";
            return;
        }
        Result = new WatchOptions(categories, locations,
            SeverityBox.SelectedValue is AlarmSeverity severity ? severity : AlarmSeverity.Warning,
            HoldBox.SelectedValue is int hold ? hold : 20,
            IdleBox.SelectedValue is int idle && idle > 0 ? idle : null,
            MainStreamCheck.IsChecked == true,
            PinBox.SelectedValue is int pin ? pin : 5);
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
