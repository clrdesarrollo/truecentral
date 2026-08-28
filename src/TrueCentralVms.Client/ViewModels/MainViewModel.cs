using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Ventana principal: árbol de dispositivos/canales y grilla de video en vivo
/// (1/4/9/16). El hub SignalR refresca el árbol ante cambios de configuración
/// y los estados en línea de los equipos.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly VmsHubClient _hub;

    public ObservableCollection<DeviceNode> Devices { get; } = [];
    public ObservableCollection<VideoCellViewModel> Cells { get; } = [];

    [ObservableProperty] private int _gridColumns = 2;
    [ObservableProperty] private string _connectionStatus = "Conectado";
    [ObservableProperty] private bool _isConnected = true;
    [ObservableProperty] private VideoCellViewModel? _selectedCell;
    [ObservableProperty] private string _statusMessage = HintMessage;

    // ---------- PTZ (visible solo si el canal del cuadro seleccionado lo soporta) ----------
    [ObservableProperty] private bool _isPtzVisible;
    [ObservableProperty] private string _ptzTargetName = "";
    [ObservableProperty] private int _ptzSpeed = 4;
    private ChannelNode? _ptzChannel;

    partial void OnSelectedCellChanged(VideoCellViewModel? oldValue, VideoCellViewModel? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnSelectedCellPropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnSelectedCellPropertyChanged;
        UpdatePtzPanel();
    }

    private void OnSelectedCellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoCellViewModel.AssignedChannel))
            UpdatePtzPanel();
    }

    private void UpdatePtzPanel()
    {
        _ptzChannel = SelectedCell?.AssignedChannel is { Channel.SupportsPtz: true } node ? node : null;
        IsPtzVisible = _ptzChannel is not null;
        PtzTargetName = _ptzChannel?.Channel.Name ?? "";
    }

    /// <summary>Envía la orden PTZ al canal del cuadro seleccionado (fuego y olvido; errores a la barra de estado).</summary>
    public async Task PtzAsync(PtzCommand command, bool stop)
    {
        if (_ptzChannel is not { } node) return;
        try
        {
            await _api.PtzAsync(node.Device.Id, node.Channel.ChannelNumber, command, PtzSpeed, stop);
        }
        catch (ApiException ex)
        {
            StatusMessage = $"PTZ: {ex.Message}";
        }
    }

    /// <summary>Número de preset activo en el panel PTZ (1..300).</summary>
    [ObservableProperty] private int _ptzPresetIndex = 1;

    /// <summary>Ir / guardar / borrar el preset del panel, con confirmación en la barra de estado.</summary>
    public async Task PtzPresetAsync(PtzPresetAction action)
    {
        if (_ptzChannel is not { } node) return;
        int index = Math.Clamp(PtzPresetIndex, 1, 300);
        PtzPresetIndex = index;
        try
        {
            await _api.PtzPresetAsync(node.Device.Id, node.Channel.ChannelNumber, action, index);
            StatusMessage = action switch
            {
                PtzPresetAction.Goto => $"PTZ: moviéndose al preset {index}.",
                PtzPresetAction.Set => $"PTZ: posición actual guardada como preset {index}.",
                PtzPresetAction.Clear => $"PTZ: preset {index} eliminado.",
                _ => StatusMessage,
            };
        }
        catch (ApiException ex)
        {
            StatusMessage = $"PTZ: {ex.Message}";
        }
    }

    private const string HintMessage =
        "Clic en la barra de un cuadro para seleccionarlo (borde azul); doble clic en un canal del árbol lo abre ahí.";

    public string UserLabel => $"{_api.Username} ({(_api.Role == "Admin" ? "Administrador" : "Operador")}) — {_api.BaseUrl}";

    public MainViewModel(ApiClient api, VmsHubClient hub)
    {
        _api = api;
        _hub = hub;

        _hub.ConfigChanged += entity =>
        {
            if (entity is "devices" or "channels")
                Application.Current.Dispatcher.InvokeAsync(() => _ = LoadTreeAsync());
        };
        _hub.DeviceStatusChanged += dto => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var node = Devices.FirstOrDefault(d => d.Device.Id == dto.Id);
            if (node is not null) node.Device = dto;
        });
        _hub.ConnectionStateChanged += ok => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            IsConnected = ok;
            ConnectionStatus = ok ? "Conectado" : "Reconectando…";
        });

        ApplyLayout(4);
    }

    public async Task LoadTreeAsync()
    {
        try
        {
            var devices = await _api.GetDevicesAsync();
            Devices.Clear();
            foreach (var device in devices)
            {
                var node = new DeviceNode(device);
                var channels = await _api.GetChannelsAsync(device.Id);
                foreach (var channel in channels.Where(c => c.Enabled))
                    node.Channels.Add(new ChannelNode(node, channel));
                Devices.Add(node);
            }
            StatusMessage = HintMessage;
        }
        catch (ApiException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>Comando de los botones 1/4/9/16 (el parámetro XAML llega como texto).</summary>
    [RelayCommand]
    private void SetLayout(string count) => ApplyLayout(int.Parse(count));

    /// <summary>Cambia la grilla conservando lo que quepa (las celdas sobrantes se liberan).</summary>
    public void ApplyLayout(int count)
    {
        GridColumns = count switch { 1 => 1, 4 => 2, 9 => 3, _ => 4 };
        while (Cells.Count > count)
        {
            var cell = Cells[^1];
            Cells.RemoveAt(Cells.Count - 1);
            cell.Dispose();
        }
        while (Cells.Count < count)
            Cells.Add(new VideoCellViewModel(_api));
        for (int i = 0; i < Cells.Count; i++)
            Cells[i].Index = i + 1;
        if (SelectedCell is not null && !Cells.Contains(SelectedCell))
            SelectedCell = null;
    }

    /// <summary>
    /// Abre un canal: en el cuadro seleccionado, o en el primero libre, o en
    /// el primero de la grilla si está todo ocupado. Después la selección
    /// avanza al cuadro siguiente (como SmartPSS: doble clics consecutivos van
    /// llenando la grilla). Grillas grandes usan el perfil sub (estándar VMS
    /// para cuidar el ancho de banda del equipo).
    /// </summary>
    public async Task OpenChannelAsync(ChannelNode node)
    {
        var cell = SelectedCell
            ?? Cells.FirstOrDefault(c => c.IsEmpty)
            ?? Cells.FirstOrDefault();
        if (cell is null) return;

        // La selección avanza ANTES del await: el usuario puede seguir
        // abriendo canales mientras este cuadro conecta.
        int next = Cells.IndexOf(cell) + 1;
        SelectedCell = next < Cells.Count ? Cells[next] : null;

        var profile = Cells.Count <= 4 ? StreamProfile.Main : StreamProfile.Sub;
        await cell.OpenAsync(node, profile);
    }

    [RelayCommand]
    public void ClearAll()
    {
        foreach (var cell in Cells)
            cell.Clear();
    }

    public void Shutdown()
    {
        foreach (var cell in Cells)
            cell.Dispose();
        _ = _hub.DisposeAsync();
    }
}
