using System.Windows;
using System.Windows.Controls;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Canal del inventario listo para mandar al muro, con el equipo al que
/// pertenece. Lo comparten el módulo del muro y sus diálogos.
/// </summary>
internal sealed record CameraChannel(DeviceDto Device, ChannelDto Channel)
{
    /// <summary>Nombre para mostrar en listas mixtas de varios equipos.</summary>
    public string Label => $"{Channel.Name} ({Device.Name})";
    /// <summary>Nombre para mostrar dentro de un equipo ya elegido.</summary>
    public string ChannelLabel => $"{Channel.ChannelNumber} — {Channel.Name}";
}

/// <summary>
/// Elige una cámara del inventario para una ventana del muro. Se usa donde no
/// hay arrastre disponible: al abrir una ventana flotante nueva, que además
/// puede recibir la proyección de la pantalla de este PC.
/// </summary>
public partial class WallAssignDialog : Window
{
    private readonly List<CameraChannel> _cameras;
    private readonly AssignmentDto? _current;

    /// <summary>Cámara elegida; null si el diálogo se cerró sin selección.</summary>
    internal CameraChannel? SelectedCamera => ChannelCombo.SelectedItem as CameraChannel;

    public int StreamType => StreamCombo.SelectedIndex == 1 ? 1 : 0;

    /// <summary>Muestra el botón "Proyectar aquí…" (transmitir este PC en vez de una cámara).</summary>
    public bool AllowProjection
    {
        get => ProjectButton.Visibility == Visibility.Visible;
        set => ProjectButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>true si el usuario eligió "Proyectar aquí…" (DialogResult también es true).</summary>
    public bool ProjectHere { get; private set; }

    internal WallAssignDialog(List<CameraChannel> cameras, string header, AssignmentDto? current)
    {
        _cameras = cameras;
        _current = current;
        InitializeComponent();

        HeaderText.Text = header;

        var devices = cameras
            .GroupBy(c => c.Device.Id)
            .Select(g => g.First().Device)
            .OrderBy(d => d.Name)
            .ToList();
        DeviceCombo.ItemsSource = devices;
        DeviceCombo.DisplayMemberPath = nameof(DeviceDto.Name);
        DeviceCombo.SelectedItem =
            (current is { IsExternal: false } a ? devices.FirstOrDefault(d => d.Id == a.DeviceId) : null)
            ?? devices.FirstOrDefault();

        if (current is { } cur)
            StreamCombo.SelectedIndex = cur.StreamType == 1 ? 1 : 0;
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is not DeviceDto device) return;

        // Solo canales con señal: el decoder no tendría qué mostrar del resto.
        var channels = _cameras
            .Where(c => c.Device.Id == device.Id && c.Channel.IsOnline)
            .OrderBy(c => c.Channel.ChannelNumber)
            .ToList();

        ChannelCombo.ItemsSource = channels;
        ChannelCombo.DisplayMemberPath = nameof(CameraChannel.ChannelLabel);
        ChannelCombo.SelectedItem =
            (_current is { IsExternal: false } a ? channels.FirstOrDefault(c => c.Channel.Id == a.ChannelId) : null)
            ?? channels.FirstOrDefault();

        HintText.Text = channels.Count == 0
            ? "Este equipo no tiene canales con señal."
            : $"{channels.Count} canal(es) con señal.";
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (SelectedCamera is null)
        {
            MessageBox.Show(this, "Seleccione un canal.", "Canal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnProjectClick(object sender, RoutedEventArgs e)
    {
        ProjectHere = true;
        DialogResult = true;
    }
}
