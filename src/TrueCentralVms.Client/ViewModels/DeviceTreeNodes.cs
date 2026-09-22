using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>Nodo de dispositivo en el árbol lateral.</summary>
public partial class DeviceNode(DeviceDto device) : ObservableObject
{
    [ObservableProperty]
    private DeviceDto _device = device;

    /// <summary>Visible según el buscador del árbol (vacío = todos).</summary>
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>Selección del TreeView (enlazada al contenedor).</summary>
    [ObservableProperty] private bool _isSelected;

    public ObservableCollection<ChannelNode> Channels { get; } = [];

    public string Header => Device.Name;
    public bool IsOnline => Device.Status == DeviceStatus.Online;

    partial void OnDeviceChanged(DeviceDto value)
    {
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(IsOnline));
        // Un equipo caído arrastra a todos sus canales: sin el DVR/NVR no hay
        // señal posible, por más que el último sondeo los haya visto activos.
        foreach (var channel in Channels)
            channel.RefreshOnlineState();
    }
}

/// <summary>Nodo de canal (hoja): lo que se abre con doble clic en la grilla.</summary>
public sealed partial class ChannelNode(DeviceNode deviceNode, ChannelDto channel) : ObservableObject
{
    private readonly DeviceNode _deviceNode = deviceNode;

    /// <summary>Datos vigentes del equipo (no una copia: siguen al nodo padre).</summary>
    public DeviceDto Device => _deviceNode.Device;

    public ChannelDto Channel { get; } = channel;
    public string Header => Channel.Name;

    /// <summary>Visible según el buscador del árbol (vacío = todos).</summary>
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>Selección del TreeView. Además de los clics del usuario, la
    /// fija el ViewModel: seleccionar un cuadro de la grilla selecciona aquí
    /// su canal.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>El canal está en un cuadro de la vista en vivo (grilla
    /// principal o pantalla auxiliar): el árbol lo marca con ▶. Lo mantiene
    /// el shell (MainViewModel.RefreshLiveChannels).</summary>
    [ObservableProperty] private bool _isLive;

    /// <summary>Dónde se está viendo ("En vivo en: cuadro 2, …"); vacío si no
    /// está en ningún cuadro. Es el tooltip del nodo mientras IsLive.</summary>
    [ObservableProperty] private string _liveLocation = "";

    /// <summary>El canal está en línea solo si además su equipo lo está.</summary>
    public bool IsOnline => _deviceNode.IsOnline && Channel.IsOnline;

    internal void RefreshOnlineState() => OnPropertyChanged(nameof(IsOnline));
}
