using System.Windows;
using System.Windows.Threading;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Asignación de los botones del joystick a acciones (las mismas del panel
/// web): «Asignar» espera el próximo botón que se apriete. Un botón hace una
/// sola acción y cada acción tiene un solo botón. Se guarda al asignar.
/// </summary>
public partial class JoystickButtonsWindow : Window
{
    private sealed record Row(string Key, string Label, string ButtonText, string AssignText,
        Visibility UnsetVisibility, FontWeight ButtonWeight);

    private readonly ClientSettings _settings;
    private readonly JoystickConfig _config;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
    private bool[] _pressed = [];
    private string? _learning;

    public JoystickButtonsWindow(ClientSettings settings, JoystickConfig config, string deviceName)
    {
        InitializeComponent();
        _settings = settings;
        _config = config;
        DeviceText.Text = deviceName;
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Render();
    }

    private void Poll()
    {
        var state = Joystick.Read();
        bool[] buttons = state?.Buttons ?? [];
        var pressed = buttons.Select((p, i) => p ? (i + 1).ToString() : null).Where(b => b is not null);
        PressedText.Text = state is null
            ? "No se detecta el joystick."
            : $"Botones apretados: {(string.Join(", ", pressed) is { Length: > 0 } list ? list : "—")}";
        int rising = -1;
        for (int i = 0; i < buttons.Length; i++)
            if (buttons[i] && (i >= _pressed.Length || !_pressed[i])) { rising = i; break; }
        _pressed = buttons;
        if (_learning is { } key && rising >= 0) Assign(rising, key);
    }

    private void Assign(int button, string key)
    {
        foreach (int i in _config.Buttons.Where(p => p.Key == button || p.Value == key).Select(p => p.Key).ToList())
            _config.Buttons.Remove(i);
        _config.Buttons[button] = key;
        _learning = null;
        _settings.Save();
        Render();
    }

    private void Render()
    {
        ActionList.ItemsSource = JoystickActions.All.Select(a =>
        {
            int? button = _config.Buttons.Where(p => p.Value == a.Key).Select(p => (int?)p.Key).FirstOrDefault();
            bool learning = _learning == a.Key;
            return new Row(a.Key, a.Label,
                learning ? "Apriete el botón…" : button is { } b ? $"Botón {b + 1}" : "—",
                learning ? "Cancelar" : "Asignar",
                button is not null && !learning ? Visibility.Visible : Visibility.Collapsed,
                learning ? FontWeights.SemiBold : FontWeights.Normal);
        }).ToList();
    }

    private void OnAssignClick(object sender, RoutedEventArgs e)
    {
        string key = (string)((FrameworkElement)sender).Tag;
        _learning = _learning == key ? null : key;
        Render();
    }

    private void OnUnsetClick(object sender, RoutedEventArgs e)
    {
        string key = (string)((FrameworkElement)sender).Tag;
        foreach (int i in _config.Buttons.Where(p => p.Value == key).Select(p => p.Key).ToList())
            _config.Buttons.Remove(i);
        _learning = null;
        _settings.Save();
        Render();
    }

    private void OnClearAllClick(object sender, RoutedEventArgs e)
    {
        _config.Buttons.Clear();
        _learning = null;
        _settings.Save();
        Render();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
