using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Otro Centro de mando (abierto con «+» junto a las viñetas): la misma vista
/// que el de la ventana principal (<see cref="Views.LiveView"/>) con su propia
/// grilla. Lo que es de la grilla vive aquí (división, cuadros, PTZ del cuadro
/// seleccionado, su desplegable de tableros); lo demás es el mismo del shell
/// (la lista, el buscador, los parlantes, los permisos, la lista de tableros).
/// </summary>
public partial class AuxScreenViewModel : ICommandCenter
{
    /// <summary>La ventana donde está (avisos de apertura masiva y notificaciones).</summary>
    public Window? LiveWindow
    {
        get => OwnerWindow;
        set => OwnerWindow = value;
    }

    /// <summary>Parlantes IP: el mismo panel en todos los Centros de mando.</summary>
    public SpeakersViewModel Speakers => _shell.Speakers;

    public Task RunLocationCommandAsync(LocationNode node, string command) => _shell.RunLocationCommandAsync(node, command);

    // ---------- Divisiones para monitor horizontal o vertical ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LayoutGroups))]
    private bool _isVerticalLayouts;

    /// <summary>Hasta que se elija, el selector la toma de la forma de la ventana al abrirse.</summary>
    public bool LayoutOrientationUnset { get; private set; } = true;

    partial void OnIsVerticalLayoutsChanged(bool value) => LayoutOrientationUnset = false;

    // ---------- Subdividir un card (ver MainViewModel.SubdivideCellAsync) ----------

    public async Task SubdivideCellAsync(VideoCellViewModel cell, int across, int down)
    {
        if (_layoutBusy) return;
        if (MaximizedIndex >= 0)
        {
            StatusMessage = "Restaure el cuadro maximizado para subdividir.";
            return;
        }
        int index = Cells.IndexOf(cell);
        if (index < 0 || index >= CurrentLayout.CellCount) return;
        if (CurrentLayout.Subdivide(index, across, down, out var reason) is not { } split)
        {
            StatusMessage = reason ?? "";
            return;
        }
        _layoutBusy = true;
        try { await ApplyLayoutAsync(split.Layout, split.Placement); }
        finally { _layoutBusy = false; }
        SelectedCell = Cells[split.StripCells.Count > 1 ? split.StripCells[1] : split.StripCells[0]];
        StatusMessage = $"Card subdividido en {across * down}: la grilla queda en {split.Layout.CellCount} cuadros.";
    }

    // ---------- PTZ del cuadro seleccionado de esta grilla ----------

    [ObservableProperty] private bool _isPtzAvailable;
    [ObservableProperty] private bool _isPtzExpanded;
    [ObservableProperty] private bool _isPrecisionMode;
    [ObservableProperty] private string _ptzTargetName = "sin cámara PTZ";
    [ObservableProperty] private int _ptzSpeed = 4;
    [ObservableProperty] private int _ptzPresetIndex = 1;
    private ChannelNode? _ptzChannel;

    /// <summary>El joystick mueve la cámara del Centro de mando de la ventana principal.</summary>
    public bool IsJoystickActive => false;

    public bool CanPtz => _shell.CanPtz;

    partial void OnSelectedCellChanged(VideoCellViewModel? oldValue, VideoCellViewModel? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnSelectedCellPropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnSelectedCellPropertyChanged;
        UpdatePtzPanel();
    }

    private void OnSelectedCellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoCellViewModel.AssignedChannel)) UpdatePtzPanel();
    }

    /// <summary>Mismas reglas que la grilla principal: permiso «Mover cámaras PTZ» y alcance.</summary>
    private void UpdatePtzPanel()
    {
        var assigned = SelectedCell?.AssignedChannel is { } node
            ? _shell.Devices.SelectMany(d => d.Channels).FirstOrDefault(c => c.Channel.Id == node.Channel.Id) ?? node
            : null;
        _ptzChannel = assigned is { Channel.SupportsPtz: true } ? assigned : null;
        bool operable = _ptzChannel is not null && OperableScope.Current.CanOperateChannel(_ptzChannel.Channel.Id);
        IsPtzAvailable = operable && CanPtz;
        PtzTargetName = _ptzChannel is null ? "sin cámara PTZ"
            : !CanPtz ? $"{_ptzChannel.Channel.Name} (sus roles no lo permiten)"
            : operable ? _ptzChannel.Channel.Name
            : $"{_ptzChannel.Channel.Name} (fuera de su alcance)";
    }

    public async Task PtzAsync(PtzCommand command, bool stop, int? speed = null)
    {
        if (_ptzChannel is not { } node || !IsPtzAvailable) return;
        try { await _shell.Api.PtzAsync(node.Device.Id, node.Channel.ChannelNumber, command, speed ?? PtzSpeed, stop); }
        catch (ApiException ex) { StatusMessage = $"PTZ: {ex.Message}"; }
    }

    public async Task PtzPresetAsync(PtzPresetAction action)
    {
        if (_ptzChannel is not { } node || !IsPtzAvailable) return;
        int index = Math.Clamp(PtzPresetIndex, 1, 300);
        PtzPresetIndex = index;
        try
        {
            await _shell.Api.PtzPresetAsync(node.Device.Id, node.Channel.ChannelNumber, action, index);
            StatusMessage = action switch
            {
                PtzPresetAction.Goto => $"PTZ: moviéndose al preset {index}.",
                PtzPresetAction.Set => $"PTZ: posición actual guardada como preset {index}.",
                PtzPresetAction.Clear => $"PTZ: preset {index} eliminado.",
                _ => StatusMessage,
            };
        }
        catch (ApiException ex) { StatusMessage = $"PTZ: {ex.Message}"; }
    }

    // ---------- Tableros: la lista es la del shell; cargar y guardar, sobre esta grilla ----------

    public ObservableCollection<SavedViewItem> SavedViews => _shell.SavedViews;
    public bool CanSaveViews => _shell.CanSaveViews;
    public bool CanShareViews => _shell.CanShareViews;

    [ObservableProperty] private bool _isViewsPopupOpen;
    [ObservableProperty] private string _newViewName = "";
    [ObservableProperty] private bool _newViewShared;
    [ObservableProperty] private string _savedViewsStatus = "";
    [ObservableProperty] private string _activeViewName = "";

    partial void OnIsViewsPopupOpenChanged(bool value)
    {
        if (value) _ = _shell.LoadSavedViewsForAsync(this);
    }

    [RelayCommand]
    private Task SaveCurrentViewAsync() => _shell.SaveCurrentViewForAsync(this);

    [RelayCommand]
    private Task UpdateSavedViewAsync(SavedViewItem item) => _shell.UpdateSavedViewForAsync(this, item);

    [RelayCommand]
    private Task DeleteSavedViewAsync(SavedViewItem item) => _shell.DeleteSavedViewForAsync(this, item);

    [RelayCommand]
    private Task ApplySavedViewAsync(SavedViewItem item) => _shell.ApplySavedViewForAsync(this, item);
}
